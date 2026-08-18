using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    sealed class MessageReplyHistoryState
    {
        public List<MessageReplyHistoryItem> Items { get; set; } = new();
    }

    sealed class MessageReplyHistoryItem
    {
        public string Key { get; set; } = "";
        public string User { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime AcceptedAtUtc { get; set; }
        public DateTime? RepliedAtUtc { get; set; }
    }

    sealed record RequestSnapshot(string Key, string Text, string Href);

    static string MessageHistoryKey(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
        return Convert.ToHexString(bytes[..12]);
    }

    static MessageReplyHistoryState LoadMessageReplyHistory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new MessageReplyHistoryState();
            var state = JsonSerializer.Deserialize<MessageReplyHistoryState>(File.ReadAllText(path));
            return state ?? new MessageReplyHistoryState();
        }
        catch { return new MessageReplyHistoryState(); }
    }

    static void SaveMessageReplyHistory(string path, MessageReplyHistoryState state)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            state.Items = state.Items
                .Where(x => x.AcceptedAtUtc >= cutoff)
                .OrderByDescending(x => x.RepliedAtUtc ?? x.AcceptedAtUtc)
                .Take(2000)
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public async Task<TikTokMessageReplyRunResult> ProcessTikTokMessageRequestsAsync(
        TikTokMessageReplyOptions options,
        Action<TikTokMessageReplyProgress>? report = null,
        CancellationToken ct = default,
        Action<string>? trace = null)
    {
        if (!Connected) throw new InvalidOperationException("Chrome chưa kết nối CDP.");
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!options.AcceptRequests)
            throw new InvalidOperationException("Hãy bật 'Chấp nhận yêu cầu'. Chế độ trả lời của bản này chỉ xử lý các cuộc trò chuyện vừa được chấp nhận.");

        var rawMessages = (options.Messages ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        // Mỗi nội dung giống hệt nhau chỉ được gửi một lần trong cùng một người.
        // Các đoạn KHÁC nhau do người dùng tách bằng dòng --- vẫn được giữ nguyên thứ tự.
        var messages = rawMessages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (options.ReplyAfterAccept && messages.Length == 0)
            throw new InvalidOperationException("Danh sách nội dung trả lời đang trống.");

        var minDelay = Math.Clamp(options.DelayMinMs, 0, 60000);
        var maxDelay = Math.Clamp(options.DelayMaxMs, minDelay, 60000);
        var retries = Math.Clamp(options.RetryCount, 1, 5);
        var history = LoadMessageReplyHistory(options.HistoryPath);

        var found = 0;
        var processed = 0;
        var accepted = 0;
        var replied = 0;
        var skipped = 0;
        var failed = 0;
        var currentUser = "";
        var cancelled = false;
        var noRequestsLeft = false;

        void Trace(string message)
        {
            try { trace?.Invoke(message); } catch { }
            _log.Info("[MESSAGE_REPLY_TRACE] " + message);
        }

        void Report(string stage, string message, bool running = true, bool completed = false)
        {
            Trace($"[{stage}] {message} | found={found} processed={processed} accept={accepted} sent={replied} skip={skipped} fail={failed} user={currentUser}");
            report?.Invoke(new TikTokMessageReplyProgress(
                running, stage, found, processed, accepted, replied, skipped, failed,
                currentUser, message, completed, cancelled));
        }

        Trace($"[MESSAGE_PLAN] raw={rawMessages.Length} unique={messages.Length} | " +
            string.Join(" || ", messages.Select((m, i) => $"#{i + 1}:{(m.Length > 60 ? m[..60] + "…" : m).Replace("\r", " ").Replace("\n", " ")}")));

        async Task DelayRandomAsync()
        {
            if (maxDelay <= 0) return;
            var delay = minDelay == maxDelay ? minDelay : Random.Shared.Next(minDelay, maxDelay + 1);
            if (delay > 0) await Task.Delay(delay, ct);
        }

        static bool ResultBool(JsonElement result)
            => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;

        static string ResultString(JsonElement result)
            => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "")
                : "";

        async Task<bool> WaitBoolAsync(string js, int timeoutMs = 10000, int delayMs = 250)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ResultBool(await EvalAsync(js, ct: ct))) return true;
                }
                catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
                await Task.Delay(delayMs, ct);
            }
            return false;
        }

        async Task<bool> RetryBoolAsync(Func<Task<bool>> action, string label)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Trace($"[RETRY] step={label} attempt={attempt}/{retries} bắt đầu");
                try
                {
                    if (await action())
                    {
                        Trace($"[RETRY] step={label} attempt={attempt}/{retries} => OK");
                        return true;
                    }
                    Trace($"[RETRY] step={label} attempt={attempt}/{retries} => FALSE");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Trace($"[RETRY] step={label} attempt={attempt}/{retries} => EXCEPTION: {ex.Message}");
                }

                if (attempt < retries) await Task.Delay(500, ct);
            }

            if (last is not null)
                _log.Warn($"[MESSAGE_REPLY_RETRY_FAILED] step={label} error={last.Message}");
            Trace($"[RETRY_FAILED] step={label}");
            return false;
        }

        async Task EnsureMessagesPageAsync(bool forceReload = false)
        {
            // Luồng bình thường KHÔNG reload /messages. TikTok Messages là SPA; nếu đã ở /messages
            // thì giữ nguyên DOM hiện tại để chuyển section/chat bằng click. Chỉ navigate khi chưa ở trang
            // Tin nhắn hoặc khi caller yêu cầu recovery cưỡng bức.
            var alreadyOnMessages = !forceReload && ResultBool(await EvalAsync("""
(() => {
  try { return location.pathname === '/messages' || location.pathname.startsWith('/messages/'); }
  catch { return false; }
})()
""", ct: ct));

            if (alreadyOnMessages)
            {
                Trace("[OPEN_MESSAGES_SKIP] Đã ở /messages → giữ nguyên DOM, không tải lại trang.");
                return;
            }

            Report(forceReload ? "RECOVER_MESSAGES" : "OPEN_MESSAGES",
                forceReload ? "Recovery: tải lại trang Tin nhắn..." : "Đang mở trang Tin nhắn...");
            await NavigateAndWaitAsync("https://www.tiktok.com/messages", 800, 18000, ct);
            Trace(forceReload
                ? "[RECOVER_MESSAGES] NavigateAndWait hoàn tất."
                : "[OPEN_MESSAGES] NavigateAndWait hoàn tất.");
            // Chỉ delay khi thực sự navigate; các bước DOM nội bộ dùng wait selector riêng.
            await DelayRandomAsync();
        }

        async Task RecoverMessagesHomeAsync(string reason)
        {
            Trace($"[RECOVERY] {reason} → fallback Navigate /messages.");
            await EnsureMessagesPageAsync(forceReload: true);
        }

        async Task<bool> IsMessageComposerReadyAsync(int timeoutMs = 700)
        {
            return await WaitBoolAsync("""
(() => {
  const el=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!el) return false;
  const r=el.getBoundingClientRect();
  const cs=getComputedStyle(el);
  return r.width>4 && r.height>4 && cs.display!=='none' && cs.visibility!=='hidden';
})()
""", timeoutMs, 120);
        }

        async Task<bool> TryReturnFromRequestsToMessagesByDomAsync()
        {
            // Nếu TikTok sau Accept đã tự rời section Request thì không cần làm gì.
            var inRequests = ResultBool(await EvalAsync("""
(() => {
  const fold=s=>(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  return [...document.querySelectorAll('h1,h2,h3,[role="heading"],div,p,span')].filter(visible).some(el=>{
    const r=el.getBoundingClientRect();
    const t=fold(el.innerText||el.textContent||'');
    return r.left>50 && r.left<innerWidth*0.42 && r.top<190 && (t==='yeu cau tin nhan' || t==='message requests');
  });
})()
""", ct: ct));

            if (!inRequests)
            {
                Trace("[BACK_MESSAGES_DOM] Không còn ở section Yêu cầu tin nhắn → không cần Back.");
                return true;
            }

            var raw = ResultString(await EvalAsync("""
(() => {
  const fold=s=>(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const texts=[...document.querySelectorAll('h1,h2,h3,[role="heading"],div,p,span')].filter(visible).map(el=>({el,r:el.getBoundingClientRect(),t:fold(el.innerText||el.textContent||'')}));
  const head=texts.filter(x=>x.r.left>50&&x.r.left<innerWidth*0.42&&x.r.top<190&&(x.t==='yeu cau tin nhan'||x.t==='message requests')).sort((a,b)=>a.r.top-b.r.top)[0];
  if (!head) return JSON.stringify({ok:true,reason:'request_heading_gone'});

  const hy=head.r.top+head.r.height/2;
  const candidates=[...document.querySelectorAll('button,[role="button"],a')].filter(visible).map(el=>({el,r:el.getBoundingClientRect()}))
    .filter(x=>x.r.left>45 && x.r.left<head.r.left+10 && x.r.top<210 && x.r.width>=22 && x.r.width<=72 && x.r.height>=22 && x.r.height<=72)
    .sort((a,b)=>Math.abs((a.r.top+a.r.height/2)-hy)-Math.abs((b.r.top+b.r.height/2)-hy));
  const hit=candidates[0]?.el;
  if (!hit) return JSON.stringify({ok:false,reason:'back_button_not_found'});
  try { hit.click(); } catch { return JSON.stringify({ok:false,reason:'back_click_exception'}); }
  return JSON.stringify({ok:true,reason:'back_click',tag:(hit.tagName||'').toLowerCase(),aria:hit.getAttribute('aria-label')||''});
})()
""", ct: ct));

            var clicked = false;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                clicked = root.TryGetProperty("ok", out var ok) && ok.GetBoolean();
                var reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "") : "";
                Trace($"[BACK_MESSAGES_DOM] ok={clicked} reason={reason}");
            }
            catch (Exception ex)
            {
                Trace($"[BACK_MESSAGES_DOM_PARSE_ERROR] raw={raw} | {ex.Message}");
                return false;
            }

            if (!clicked) return false;

            // Đợi heading Request biến mất. Không yêu cầu entry Request phải còn vì có thể vừa Accept request cuối.
            var leftRequests = await WaitBoolAsync("""
(() => {
  const fold=s=>(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const still=[...document.querySelectorAll('h1,h2,h3,[role="heading"],div,p,span')].filter(visible).some(el=>{
    const r=el.getBoundingClientRect(); const t=fold(el.innerText||el.textContent||'');
    return r.left>50&&r.left<innerWidth*0.42&&r.top<190&&(t==='yeu cau tin nhan'||t==='message requests');
  });
  return !still;
})()
""", 3000, 120);
            Trace($"[BACK_MESSAGES_DOM] sectionClosed={leftRequests}");
            return leftRequests;
        }

        async Task<int> ReadRequestCountFromMessagesHomeAsync()
        {
            var raw = ResultString(await EvalAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g,' ').trim();
  const fold = s => norm(s).normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').toLowerCase();
  const els=[...document.querySelectorAll('div,span,p,a,button')];
  for (const el of els) {
    const t=norm(el.innerText || el.textContent || '');
    const f=fold(t);
    if (!f.includes('ban nhan duoc') && !f.includes('you received')) continue;
    if (!f.includes('yeu cau') && !f.includes('request')) continue;
    const m=t.match(/(\d{1,4})/);
    if (m) return m[1];
  }
  // Fallback: badge gần dòng "Yêu cầu tin nhắn".
  const req=[...document.querySelectorAll('div,a,button,[role="button"]')].find(el => {
    const f=fold(el.innerText || el.textContent || '');
    return f.includes('yeu cau tin nhan') || f.includes('message requests');
  });
  if (req) {
    const text=norm(req.innerText || req.textContent || '');
    const m=text.match(/(\d{1,4})/);
    if (m) return m[1];
    const parent=req.parentElement;
    if (parent) {
      const pt=norm(parent.innerText || parent.textContent || '');
      const pm=pt.match(/(\d{1,4})/);
      if (pm) return pm[1];
    }
  }
  return '';
})()
""", ct: ct));
            return int.TryParse(raw, out var n) && n >= 0 ? n : -1;
        }

        async Task<bool> HasRequestsSectionOnMessagesHomeAsync()
        {
            // Khi đã xử lý hết request, TikTok ẩn hẳn dòng "Yêu cầu tin nhắn" khỏi /messages.
            // Đây là trạng thái hoàn tất bình thường, không phải lỗi click/DOM.
            // Poll ngắn để tránh kết luận nhầm khi danh sách Tin nhắn vừa load xong nhưng entry request chưa render kịp.
            var deadline = DateTime.UtcNow.AddMilliseconds(1800);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var exists = ResultBool(await EvalAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'&&Number(cs.opacity||1)>0; };
  return [...document.querySelectorAll('a,button,[role="button"],div')].filter(visible).some(el => {
    const t=fold(el.innerText || el.textContent || '');
    if (!t.includes('yeu cau tin nhan') && !t.includes('message requests')) return false;
    const r=el.getBoundingClientRect();
    return r.left < innerWidth*0.48;
  });
})()
""", ct: ct));
                if (exists) return true;
                await Task.Delay(200, ct);
            }
            return false;
        }

        async Task<bool> OpenRequestsSectionAsync()
        {
            Report("OPEN_REQUESTS", "Đang mở Yêu cầu tin nhắn...");
            var clicked = await RetryBoolAsync(async () => ResultBool(await EvalAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const heads=[...document.querySelectorAll('h1,h2,h3,[role="heading"]')].filter(visible).map(x=>fold(x.innerText||x.textContent||''));
  if (heads.some(t => t==='yeu cau tin nhan' || t==='message requests')) return true;

  const candidates=[...document.querySelectorAll('a,button,[role="button"],div')].filter(visible).map(el=>{
    const text=fold(el.innerText || el.textContent || '');
    if (!text.includes('yeu cau tin nhan') && !text.includes('message requests')) return null;
    const r=el.getBoundingClientRect();
    let score=0;
    if (r.left < innerWidth*0.43) score += 50;
    if (r.width > 170 && r.height > 35 && r.height < 180) score += 30;
    if (text.includes('ban nhan duoc') || text.includes('you received')) score += 40;
    score -= Math.min(50, text.length/10);
    return {el,score};
  }).filter(Boolean).sort((a,b)=>b.score-a.score);
  const hit=candidates[0]?.el;
  if (!hit) return false;
  (hit.closest('a,button,[role="button"]') || hit).click();
  return true;
})()
""", ct: ct)), "open_requests_section");
            if (!clicked) return false;

            var ready = await WaitBoolAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const heads=[...document.querySelectorAll('h1,h2,h3,[role="heading"]')].filter(visible).map(x=>fold(x.innerText||x.textContent||''));
  if (heads.some(t => t==='yeu cau tin nhan' || t==='message requests')) return true;
  // Một số build TikTok không dùng heading semantic. Nếu panel trái có các row request
  // và vùng phải chưa có composer, coi như đã vào request list.
  return [...document.querySelectorAll('div,li,a,[role="button"]')].filter(visible).some(el=>{
    const r=el.getBoundingClientRect();
    const t=fold(el.innerText||el.textContent||'');
    return r.left < innerWidth*0.42 && r.top>100 && r.height>=48 && r.height<=125 && t && !t.includes('ban nhan duoc');
  });
})()
""", 10000, 250);
            if (!ready) await Task.Delay(700, ct);
            return true;
        }

        async Task<RequestSnapshot?> ReadFirstRequestRowAsync()
        {
            // TikTok Messages hiện cung cấp selector ổn định cho nickname của request:
            // <p data-e2e="dm-new-conversation-nickname"><span>...</span></p>
            // Dùng selector này trực tiếp để tránh nhầm badge unread ("1", "2"...) thành tên.
            var raw = ResultString(await EvalAsync("""
(() => {
  const norm=s=>(s||'').replace(/\s+/g,' ').trim();
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'&&Number(cs.opacity||1)>0; };

  const nicknames=[...document.querySelectorAll('p[data-e2e="dm-new-conversation-nickname"]')]
    .filter(visible)
    .map(el=>({el,r:el.getBoundingClientRect(),name:norm(el.innerText||el.textContent||'')}))
    .filter(x=>x.name && x.r.left < innerWidth*0.48 && x.r.bottom > 0)
    .sort((a,b)=>a.r.top-b.r.top || a.r.left-b.r.left);

  const hit=nicknames[0];
  if (!hit) return '';

  // href chỉ là dữ liệu phụ; tên lấy trực tiếp từ data-e2e mới là khóa chính.
  const link=hit.el.closest('a[href]') || hit.el.parentElement?.closest?.('a[href]') || null;
  const href=link?.href || link?.getAttribute?.('href') || '';
  const row=hit.el.closest('a,button,[role="button"],li') || hit.el.parentElement || hit.el;
  const debugLines=(row.innerText||row.textContent||'').split(/\r?\n/).map(norm).filter(Boolean).slice(0,12);
  return JSON.stringify({key:(href||hit.name),name:hit.name,href,debugLines,selector:'p[data-e2e="dm-new-conversation-nickname"]'});
})()
""", ct: ct));

            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace("[READ_TEMP_USER_NOT_FOUND] Không tìm thấy p[data-e2e=dm-new-conversation-nickname].");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var key = root.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                var href = root.TryGetProperty("href", out var h) ? (h.GetString() ?? "") : "";
                var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (root.TryGetProperty("debugLines", out var dl) && dl.ValueKind == JsonValueKind.Array)
                {
                    var lines = dl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                    Trace($"[REQUEST_ROW_DEBUG] lines=[{string.Join(" | ", lines)}]");
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    Trace("[READ_TEMP_USER_INVALID] Selector nickname tồn tại nhưng text rỗng.");
                    return null;
                }

                Trace($"[REQUEST_NAME_SELECTOR] data-e2e=dm-new-conversation-nickname => '{name}' (lấy nickname có vị trí cao nhất, không bỏ dòng đầu)");
                return new RequestSnapshot(string.IsNullOrWhiteSpace(key) ? name : key, name, href);
            }
            catch (Exception ex)
            {
                Trace($"[READ_TEMP_USER_PARSE_ERROR] {ex.Message}");
                return null;
            }
        }

        async Task<bool> ClickStoredRequestUserAsync(RequestSnapshot request)
        {
            var wantedName = request.Text;

            // TikTok đang expose đúng nickname bằng data-e2e=dm-new-conversation-nickname.
            // Không đoán ancestor để click ngay từ đầu nữa: ưu tiên click CHÍNH nickname (span/p),
            // sau đó mới fallback pointer/mouse event và ancestor gần nhất nếu React chưa nhận sự kiện.
            async Task<bool> AcceptButtonVisibleAsync(int timeoutMs = 1400)
            {
                return await WaitBoolAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  return [...document.querySelectorAll('button,[role="button"]')].filter(visible).some(el => {
    const t=fold(`${el.innerText||el.textContent||''} ${el.getAttribute('aria-label')||''}`);
    return t==='chap nhan' || t==='accept';
  });
})()
""", timeoutMs, 180);
            }

            for (var mode = 0; mode < 7; mode++)
            {
                ct.ThrowIfCancellationRequested();
                var js = $$"""
(() => {
  const wantedName={{JsString(wantedName)}};
  const mode={{mode}};
  const norm=s=>(s||'').replace(/\s+/g,' ').trim();
  const fold=s=>norm(s).normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').toLowerCase();
  const wn=fold(wantedName);
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };

  const nicknames=[...document.querySelectorAll('p[data-e2e="dm-new-conversation-nickname"]')]
    .filter(visible)
    .map(el=>({el,r:el.getBoundingClientRect(),name:norm(el.innerText||el.textContent||'')}))
    .filter(x=>x.name && x.r.left < innerWidth*0.48 && x.r.bottom > 0 && fold(x.name)===wn)
    .sort((a,b)=>a.r.top-b.r.top || a.r.left-b.r.left);

  const hit=nicknames[0];
  if (!hit) return JSON.stringify({ok:false,reason:'nickname_not_found'});

  hit.el.scrollIntoView({block:'center',inline:'nearest'});

  // mode 0: click span chứa text; mode 1: click chính p nickname.
  // mode 2: dispatch pointer/mouse vào element thực tế tại tâm nickname.
  // mode 3+: thử từng ancestor GẦN NHẤT, không lấy ancestor ngoài cùng như bản cũ.
  let target=null;
  let reason='';
  if (mode===0) {
    target=hit.el.querySelector('span') || hit.el;
    reason='nickname_span_click';
  } else if (mode===1) {
    target=hit.el;
    reason='nickname_p_click';
  } else if (mode===2) {
    const r=hit.el.getBoundingClientRect();
    target=document.elementFromPoint(r.left + Math.min(Math.max(r.width/2, 4), Math.max(r.width-4,4)), r.top+r.height/2) || hit.el;
    reason='nickname_center_pointer';
  } else {
    let node=hit.el.parentElement;
    let idx=3;
    while (node && idx<mode) { node=node.parentElement; idx++; }
    target=node || hit.el;
    reason='ancestor_'+mode;
  }

  if (!target) return JSON.stringify({ok:false,reason:'target_null'});
  const tr=target.getBoundingClientRect();
  if (!visible(target)) return JSON.stringify({ok:false,reason:'target_not_visible'});

  try { target.focus?.({preventScroll:true}); } catch {}

  if (mode===2) {
    const cx=tr.left+tr.width/2, cy=tr.top+tr.height/2;
    const base={bubbles:true,cancelable:true,composed:true,clientX:cx,clientY:cy,button:0,buttons:1};
    try { target.dispatchEvent(new PointerEvent('pointerdown',{...base,pointerId:1,pointerType:'mouse',isPrimary:true})); } catch {}
    try { target.dispatchEvent(new MouseEvent('mousedown',base)); } catch {}
    try { target.dispatchEvent(new PointerEvent('pointerup',{...base,pointerId:1,pointerType:'mouse',isPrimary:true,buttons:0})); } catch {}
    try { target.dispatchEvent(new MouseEvent('mouseup',{...base,buttons:0})); } catch {}
    try { target.dispatchEvent(new MouseEvent('click',{...base,buttons:0})); } catch {}
  } else {
    try { target.click(); } catch {}
  }

  return JSON.stringify({
    ok:true,
    reason,
    tag:(target.tagName||'').toLowerCase(),
    dataE2e:target.getAttribute?.('data-e2e')||'',
    role:target.getAttribute?.('role')||'',
    cls:String(target.className||'').slice(0,120)
  });
})()
""";

                var raw = ResultString(await EvalAsync(js, ct: ct));
                Trace($"[OPEN_REQUEST_USER_CLICK] mode={mode} target={raw}");

                if (!string.IsNullOrWhiteSpace(raw) && raw.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase))
                {
                    if (await AcceptButtonVisibleAsync())
                    {
                        Trace($"[OPEN_REQUEST_USER_CLICK_OK] nickname='{wantedName}' mode={mode} → đã hiện nút Chấp nhận.");
                        return true;
                    }
                }
            }

            Trace($"[OPEN_REQUEST_USER_CLICK_FAILED] Đã click trực tiếp nickname và fallback ancestor nhưng vẫn chưa hiện nút Chấp nhận cho '{wantedName}'.");
            return false;
        }

        async Task<(string key, string displayName, string handle)> ReadOpenedRequestIdentityAsync(RequestSnapshot request)
        {
            var raw = ResultString(await EvalAsync("""
(() => {
  const norm=s=>(s||'').replace(/\s+/g,' ').trim();
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const area=[...document.querySelectorAll('h1,h2,h3,[role="heading"],a,span,div')].filter(visible).filter(el=>{
    const r=el.getBoundingClientRect(); return r.left>innerWidth*0.24 && r.top>=65 && r.top<=220;
  });
  let handle=''; let name='';
  for (const el of area) {
    const t=norm(el.innerText||el.textContent||'');
    if (!t || t.length>140) continue;
    const m=t.match(/@[A-Za-z0-9._]{2,64}/);
    if (m) { handle=m[0]; break; }
  }
  for (const el of area) {
    const t=norm(el.innerText||el.textContent||'');
    if (!t || t.length>90 || t.startsWith('@')) continue;
    if (/^(xoa|delete|chap nhan|accept)$/i.test(t)) continue;
    if (t.includes('\n')) continue;
    name=t; break;
  }
  return JSON.stringify({handle,name});
})()
""", ct: ct));

            string handle = "", name = "";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var root = doc.RootElement;
                handle = root.TryGetProperty("handle", out var h) ? (h.GetString() ?? "") : "";
                name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            }
            catch { }

            if (string.IsNullOrWhiteSpace(name)) name = request.Text;
            var rawKey = !string.IsNullOrWhiteSpace(handle)
                ? handle
                : (!string.IsNullOrWhiteSpace(request.Href) ? request.Href : name);
            return (MessageHistoryKey(rawKey), name, handle);
        }

        async Task<bool> ClickAcceptAsync()
        {
            return ResultBool(await EvalAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const buttons=[...document.querySelectorAll('button,[role="button"]')].filter(visible);
  const hit=buttons.find(el => {
    const t=fold(`${el.innerText||el.textContent||''} ${el.getAttribute('aria-label')||''}`);
    return t==='chap nhan' || t==='accept';
  });
  if (!hit) return false;
  hit.click();
  return true;
})()
""", ct: ct));
        }

        async Task<bool> WaitAcceptCompletedAsync()
        {
            return await WaitBoolAsync("""
(() => {
  const fold = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible = el => { if (!el) return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const body=fold(document.body?.innerText||'');
  if (body.includes('yeu cau tro chuyen da duoc chap nhan') || body.includes('message request has been accepted')) return true;
  return ![...document.querySelectorAll('button,[role="button"]')].filter(visible).some(el=>{
    const t=fold(`${el.innerText||el.textContent||''} ${el.getAttribute('aria-label')||''}`);
    return t==='chap nhan' || t==='accept';
  });
})()
""", 10000, 250);
        }

        async Task<bool> OpenAcceptedConversationAsync(string displayName, string handle)
        {
            // TikTok dùng cùng selector nickname cho cả danh sách Request và danh sách Tin nhắn chính:
            // <p data-e2e="dm-new-conversation-nickname"><span>Gia Vỹ</span></p>
            // Không dò row bằng div/innerText nữa. Tìm đúng nickname đã lưu tạm, cuộn panel nếu cần,
            // rồi click trực tiếp span/p giống flow Request đã xác nhận hoạt động.
            var js = $$"""
(async () => {
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  const wantedName={{JsString(displayName)}};
  const wantedHandle={{JsString(handle)}};
  const norm=s=>(s||'').replace(/\s+/g,' ').trim();
  const fold=s=>norm(s).normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').toLowerCase();
  const wn=fold(wantedName);
  const wh=fold((wantedHandle||'').replace(/^@/,''));
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>4&&r.height>4&&cs.display!=='none'&&cs.visibility!=='hidden'&&Number(cs.opacity||1)>0; };

  const allNicknames=()=>[...document.querySelectorAll('p[data-e2e="dm-new-conversation-nickname"]')];

  const findHit=()=>{
    const candidates=allNicknames().filter(visible).map(p=>{
      const r=p.getBoundingClientRect();
      const name=norm(p.innerText||p.textContent||'');
      const f=fold(name);
      let score=0;
      if (wn && f===wn) score+=1000;
      else if (wn && (f.includes(wn)||wn.includes(f))) score+=300;

      // Nếu sau này lấy được @handle thì dùng thêm như tín hiệu phụ từ row/ancestor.
      if (wh) {
        let node=p;
        let full='';
        for (let i=0;i<5 && node;i++,node=node.parentElement) full += ' ' + norm(node.innerText||node.textContent||'');
        if (fold(full).includes(wh)) score+=600;
      }

      // Danh sách hội thoại nằm ở panel trái.
      if (r.left < innerWidth*0.50) score+=100;
      if (r.top > 70) score+=30;
      return {p,r,name,score};
    }).filter(x=>x.score>0).sort((a,b)=>b.score-a.score || a.r.top-b.r.top);
    return candidates[0]||null;
  };

  const clickHit=(hit)=>{
    if (!hit) return false;
    const p=hit.p;
    const span=p.querySelector('span') || p;
    try { span.scrollIntoView({block:'center',inline:'nearest'}); } catch {}
    try { span.focus?.(); } catch {}
    try { span.click(); } catch {}
    return true;
  };

  // 1) Thử ngay trên phần DOM đang hiển thị.
  let hit=findHit();
  if (hit) {
    clickHit(hit);
    return JSON.stringify({ok:true,reason:'direct_nickname_click',name:hit.name,tag:(hit.p.querySelector('span')||hit.p).tagName.toLowerCase()});
  }

  // 2) Xác định đúng panel cuộn từ các nickname đang có trong danh sách Tin nhắn.
  const visibleNicks=allNicknames().filter(visible);
  let scroller=null;
  for (const p of visibleNicks) {
    let node=p.parentElement;
    for (let depth=0; node && depth<12; depth++,node=node.parentElement) {
      const r=node.getBoundingClientRect();
      if (r.left < innerWidth*0.50 && r.width>180 && r.height>250 && node.scrollHeight>node.clientHeight+20) {
        scroller=node;
        break;
      }
    }
    if (scroller) break;
  }

  // Fallback nếu list đang virtualize và chưa render nickname mục tiêu.
  if (!scroller) {
    const candidates=[...document.querySelectorAll('div,section,main,aside')].filter(visible).map(el=>({el,r:el.getBoundingClientRect(),delta:el.scrollHeight-el.clientHeight}))
      .filter(x=>x.r.left<innerWidth*0.50 && x.r.width>180 && x.r.width<innerWidth*0.52 && x.r.height>250 && x.delta>20)
      .sort((a,b)=>b.delta-a.delta);
    scroller=candidates[0]?.el||null;
  }

  if (!scroller) return JSON.stringify({ok:false,reason:'no_scroller',wanted:wantedName,visibleNames:visibleNicks.slice(0,12).map(p=>norm(p.innerText||p.textContent||''))});

  scroller.scrollTop=0;
  await sleep(120);
  for (let i=0;i<80;i++) {
    hit=findHit();
    if (hit) {
      clickHit(hit);
      return JSON.stringify({ok:true,reason:'scrolled_nickname_click',name:hit.name,step:i,scrollTop:scroller.scrollTop});
    }

    const max=Math.max(0,scroller.scrollHeight-scroller.clientHeight);
    if (scroller.scrollTop>=max-2) break;
    const before=scroller.scrollTop;
    scroller.scrollTop=Math.min(max,before+Math.max(160,scroller.clientHeight*0.65));
    await sleep(120);
    if (Math.abs(scroller.scrollTop-before)<1 && before<max-2) {
      try { scroller.dispatchEvent(new WheelEvent('wheel',{deltaY:500,bubbles:true})); } catch {}
      await sleep(120);
    }
  }

  const names=allNicknames().filter(visible).slice(0,20).map(p=>norm(p.innerText||p.textContent||''));
  return JSON.stringify({ok:false,reason:'nickname_not_found_after_scroll',wanted:wantedName,visibleNames:names});
})()
""";

            var raw = ResultString(await EvalAsync(js, awaitPromise: true, ct: ct));
            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace($"[OPEN_ACCEPTED_CHAT_SELECTOR_MISS] nickname='{displayName}' => JS không trả dữ liệu.");
                return false;
            }

            var clicked = false;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                clicked = root.TryGetProperty("ok", out var ok) && ok.GetBoolean();
                var reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "") : "";
                var matched = root.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                Trace($"[OPEN_ACCEPTED_CHAT_SELECTOR] wanted='{displayName}' matched='{matched}' ok={clicked} reason={reason}");
                if (!clicked && root.TryGetProperty("visibleNames", out var vn) && vn.ValueKind == JsonValueKind.Array)
                {
                    var names = vn.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                    Trace($"[OPEN_ACCEPTED_CHAT_VISIBLE_NAMES] [{string.Join(" | ", names)}]");
                }
            }
            catch (Exception ex)
            {
                Trace($"[OPEN_ACCEPTED_CHAT_SELECTOR_PARSE_ERROR] raw={raw} | {ex.Message}");
                return false;
            }

            if (!clicked) return false;

            // TikTok Messages dùng DraftJS editor cố định:
            // div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]
            // Chỉ coi conversation đã mở khi đúng editor này xuất hiện.
            var inputReady = await WaitBoolAsync("""
(() => {
  const el=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!el) return false;
  const r=el.getBoundingClientRect();
  const cs=getComputedStyle(el);
  return r.width>4 && r.height>4 && cs.display!=='none' && cs.visibility!=='hidden';
})()
""", 10000, 250);
            Trace($"[OPEN_ACCEPTED_CHAT_COMPOSER] nickname='{displayName}' selector=dm-new-input-editor ready={inputReady}");
            return inputReady;
        }

        async Task<bool> FocusMessageInputAsync()
        {
            var raw = ResultString(await EvalAsync("""
(() => {
  const editor=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!editor) return JSON.stringify({ok:false,reason:'editor_not_found'});
  const r=editor.getBoundingClientRect();
  const cs=getComputedStyle(editor);
  if (!(r.width>4 && r.height>4 && cs.display!=='none' && cs.visibility!=='hidden'))
    return JSON.stringify({ok:false,reason:'editor_not_visible'});
  try { editor.scrollIntoView({block:'center',inline:'nearest'}); } catch {}
  try { editor.focus(); } catch {}
  try { editor.click(); } catch {}
  const active=document.activeElement===editor || editor.contains(document.activeElement);
  return JSON.stringify({ok:active,reason:active?'focused':'focus_failed',aria:editor.getAttribute('aria-label')||'',e2e:editor.parentElement?.getAttribute('data-e2e')||''});
})()
""", ct: ct));
            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace("[COMPOSER_SELECTOR] JS không trả dữ liệu.");
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                var reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "") : "";
                var aria = root.TryGetProperty("aria", out var ar) ? (ar.GetString() ?? "") : "";
                Trace($"[COMPOSER_SELECTOR] selector=dm-new-input-editor ok={ok} reason={reason} aria='{aria}'");
                return ok;
            }
            catch (Exception ex)
            {
                Trace($"[COMPOSER_SELECTOR_PARSE_ERROR] raw={raw} | {ex.Message}");
                return false;
            }
        }

        async Task ClearFocusedInputAsync()
        {
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17, modifiers = 2 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Backspace", code = "Backspace", windowsVirtualKeyCode = 8, nativeVirtualKeyCode = 8 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Backspace", code = "Backspace", windowsVirtualKeyCode = 8, nativeVirtualKeyCode = 8 }, ct);
        }

        async Task<bool> ClickSendButtonAsync()
        {
            // Nút Gửi của TikTok hiện không có data-e2e ổn định; icon gửi có path:
            // fill="#FE2C55" và d bắt đầu bằng "M30.488 4.667...".
            // Dispatch click ngay từ path/SVG để event bubble tới handler React của nút cha.
            var raw = ResultString(await EvalAsync("""
(() => {
  const visible=el=>{ if(!el)return false; const r=el.getBoundingClientRect(); const cs=getComputedStyle(el); return r.width>1&&r.height>1&&cs.display!=='none'&&cs.visibility!=='hidden'; };
  const editor=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!editor) return JSON.stringify({ok:false,reason:'editor_not_found'});
  const er=editor.getBoundingClientRect();

  const paths=[...document.querySelectorAll('path[fill="#FE2C55"]')].filter(p=>{
    const d=(p.getAttribute('d')||'').trim();
    if (!d.startsWith('M30.488 4.667')) return false;
    const svg=p.closest('svg');
    if (!svg || !visible(svg)) return false;
    const r=svg.getBoundingClientRect();
    // Icon gửi phải ở cùng vùng composer và về phía phải ô nhập.
    return r.top>=er.top-50 && r.bottom<=er.bottom+70 && r.left>=er.left+er.width*0.55;
  });
  const path=paths.sort((a,b)=>b.getBoundingClientRect().left-a.getBoundingClientRect().left)[0];
  if (!path) return JSON.stringify({ok:false,reason:'send_path_not_found',count:paths.length});

  const svg=path.closest('svg');
  let clickable=svg;
  let node=svg?.parentElement;
  for (let depth=0; node && depth<7; depth++,node=node.parentElement) {
    const role=(node.getAttribute('role')||'').toLowerCase();
    const tag=(node.tagName||'').toLowerCase();
    const tab=node.getAttribute('tabindex');
    const aria=(node.getAttribute('aria-label')||'').toLowerCase();
    const r=node.getBoundingClientRect();
    if ((tag==='button'||role==='button'||tab==='0'||aria.includes('gửi')||aria.includes('send'))
        && r.width>8 && r.height>8 && r.width<180 && r.height<120) { clickable=node; break; }
  }

  const target=clickable||svg||path;
  try { target.scrollIntoView?.({block:'center',inline:'nearest'}); } catch {}
  try { target.focus?.(); } catch {}
  try {
    // QUAN TRỌNG: chỉ phát ĐÚNG MỘT click. Bản cũ dispatch cả pointer/mouse/click,
    // TikTok/React có thể bắt nhiều handler và gửi trùng cùng một nội dung.
    if (typeof target.click === 'function') {
      target.click();
      return JSON.stringify({ok:true,reason:'single_native_click',target:(target.tagName||'').toLowerCase(),role:target.getAttribute?.('role')||'',aria:target.getAttribute?.('aria-label')||''});
    }
    target.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,view:window,button:0}));
    return JSON.stringify({ok:true,reason:'single_mouse_click',target:(target.tagName||'').toLowerCase(),role:target.getAttribute?.('role')||'',aria:target.getAttribute?.('aria-label')||''});
  } catch (e) {
    return JSON.stringify({ok:false,reason:'click_exception',error:String(e)});
  }
})()
""", ct: ct));

            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace("[SEND_BUTTON_SELECTOR] JS không trả dữ liệu.");
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                var reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "") : "";
                var target = root.TryGetProperty("target", out var tg) ? (tg.GetString() ?? "") : "";
                var aria = root.TryGetProperty("aria", out var ar) ? (ar.GetString() ?? "") : "";
                Trace($"[SEND_BUTTON_SELECTOR] fill=#FE2C55 d=M30.488... ok={ok} reason={reason} target={target} aria='{aria}'");
                return ok;
            }
            catch (Exception ex)
            {
                Trace($"[SEND_BUTTON_SELECTOR_PARSE_ERROR] raw={raw} | {ex.Message}");
                return false;
            }
        }

        async Task<bool> ComposerHasTextAsync()
        {
            return ResultBool(await EvalAsync("""
(() => {
  const el=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!el) return false;
  const value=(el.innerText||el.textContent||'').replace(/\u200B/g,'').trim();
  return value.length>0;
})()
""", ct: ct));
        }

        async Task<bool> SendOneMessageByButtonAsync(string message)
        {
            var preview = message.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 80) preview = preview[..80] + "…";
            for (var attempt = 1; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Trace($"[SEND] attempt={attempt}/{retries} tìm/focus ô nhập | text={preview}");
                if (!await FocusMessageInputAsync())
                {
                    Trace($"[SEND] attempt={attempt}/{retries} => KHÔNG THẤY Ô NHẬP");
                    await Task.Delay(400, ct);
                    continue;
                }

                Trace($"[SEND] attempt={attempt}/{retries} focus ô nhập OK; đang xóa nội dung cũ");
                await ClearFocusedInputAsync();
                await Cdp.CallAsync("Input.insertText", new { text = message }, ct);
                await Task.Delay(250, ct);

                if (!await ComposerHasTextAsync())
                {
                    Trace($"[SEND] attempt={attempt}/{retries} => insertText xong nhưng ô nhập KHÔNG CÓ TEXT");
                    if (attempt < retries) await Task.Delay(400, ct);
                    continue;
                }

                Trace($"[SEND] attempt={attempt}/{retries} ô nhập đã có text; đang tìm nút Gửi");
                if (await ClickSendButtonAsync())
                {
                    Trace($"[SEND] attempt={attempt}/{retries} => CLICK NÚT GỬI 1 LẦN OK; khóa chống gửi trùng cho message này");

                    // Sau khi đã phát click Gửi thì TUYỆT ĐỐI không click lại cùng message,
                    // kể cả UI TikTok cập nhật chậm. Chờ editor trống chỉ để xác nhận/log.
                    var cleared = await WaitBoolAsync("""
(() => {
  const el=document.querySelector('div[data-e2e="dm-new-input-editor"] div[contenteditable="true"][role="textbox"]');
  if (!el) return true;
  const value=(el.innerText||el.textContent||'').replace(/\u200B/g,'').trim();
  return value.length===0;
})()
""", 5000, 150);
                    Trace($"[SEND_CONFIRM] composerCleared={cleared}; không retry nút Gửi dù xác nhận chậm");
                    return true;
                }

                Trace($"[SEND] attempt={attempt}/{retries} => KHÔNG CLICK ĐƯỢC NÚT GỬI");
                if (attempt < retries) await Task.Delay(500, ct);
            }
            Trace("[SEND_FAILED] Hết retry nhưng chưa gửi được tin nhắn.");
            return false;
        }

        async Task<bool> ProcessOneRoundAsync()
        {
            // 1) Chỉ đảm bảo đang ở /messages; nếu đã ở đây thì giữ nguyên DOM, KHÔNG reload.
            await EnsureMessagesPageAsync();

            // 2) Vào mục Yêu cầu tin nhắn trực tiếp từ UI hiện tại. Nếu TikTok đã ẩn hẳn mục này thì coi là đã hết request
            // và kết thúc module bình thường, không retry/không tăng bộ đếm lỗi.
            if (!await HasRequestsSectionOnMessagesHomeAsync())
            {
                noRequestsLeft = true;
                currentUser = "";
                Report("NO_REQUESTS", "Đã hết yêu cầu tin nhắn; tự kết thúc xử lý.");
                Trace("[NO_REQUESTS] /messages không còn hiển thị mục Yêu cầu tin nhắn → dừng bình thường.");
                return false;
            }

            if (!await OpenRequestsSectionAsync())
                throw new InvalidOperationException("Có mục Yêu cầu tin nhắn nhưng không bấm mở được.");
            await DelayRandomAsync();

            // 3) Quét username ở dòng trên cùng và LƯU TẠM cho đúng một vòng.
            var first = await ReadFirstRequestRowAsync();
            if (first is null)
                return false; // Hết request.

            currentUser = first.Text;
            Report("READ_TEMP_USER", $"Đã lưu tạm người dùng: {currentUser}");
            Trace($"[TEMP_USER] name={currentUser} | key={first.Key} | href={first.Href}");
            _log.Info($"[MESSAGE_REPLY_TEMP_USER] user={currentUser}");

            // 4) Click chính username vừa lưu để hiện Xóa / Chấp nhận.
            Report("OPEN_REQUEST_USER", $"Đang mở yêu cầu của {currentUser}...");
            if (!await RetryBoolAsync(() => ClickStoredRequestUserAsync(first), "open_saved_request_user"))
                throw new InvalidOperationException($"Không bấm được người dùng '{currentUser}' trong Yêu cầu tin nhắn.");
            await DelayRandomAsync();

            // Bổ sung handle nếu TikTok hiển thị trên đầu panel chi tiết.
            var identity = await ReadOpenedRequestIdentityAsync(first);
            var historyKey = identity.key;
            // Giữ đúng tên đã quét/lưu tạm ở row Yêu cầu tin nhắn làm khóa mở lại chat.
            // Identity chi tiết chỉ bổ sung @handle để tăng độ chính xác, không thay tên tạm bằng text khác trên panel phải.
            var tempDisplayName = currentUser;
            var tempHandle = identity.handle;
            Trace($"[IDENTITY] display={tempDisplayName} | handle={tempHandle} | historyKey={historyKey}");
            _log.Info($"[MESSAGE_REPLY_IDENTITY] display={tempDisplayName} handle={tempHandle}");

            // 5) Chấp nhận.
            Report("ACCEPT", $"Đang chấp nhận {currentUser}...");
            if (!await RetryBoolAsync(ClickAcceptAsync, "accept_request"))
                throw new InvalidOperationException("Không tìm thấy hoặc không bấm được nút Chấp nhận.");
            accepted++;
            Trace("[ACCEPT] Click nút Chấp nhận OK; đang chờ TikTok cập nhật UI.");
            var acceptSettled = await WaitAcceptCompletedAsync();
            Trace($"[ACCEPT] WaitAcceptCompleted => {acceptSettled}");

            if (!string.IsNullOrWhiteSpace(historyKey))
            {
                var item = history.Items.FirstOrDefault(x => string.Equals(x.Key, historyKey, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    item = new MessageReplyHistoryItem
                    {
                        Key = historyKey,
                        User = currentUser,
                        Status = "ACCEPTED",
                        AcceptedAtUtc = DateTime.UtcNow
                    };
                    history.Items.Add(item);
                }
                else
                {
                    item.User = currentUser;
                    item.Status = string.Equals(item.Status, "REPLIED", StringComparison.OrdinalIgnoreCase) ? item.Status : "ACCEPTED";
                    if (item.AcceptedAtUtc == default) item.AcceptedAtUtc = DateTime.UtcNow;
                }
                SaveMessageReplyHistory(options.HistoryPath, history);
            }

            await DelayRandomAsync();
            if (!options.ReplyAfterAccept)
            {
                Report("ACCEPTED", $"Đã chấp nhận {currentUser}; không gửi trả lời vì tùy chọn Trả lời đang tắt.");
                return true;
            }

            var alreadyReplied = !string.IsNullOrWhiteSpace(historyKey)
                && history.Items.Any(x => string.Equals(x.Key, historyKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Status, "REPLIED", StringComparison.OrdinalIgnoreCase));
            if (options.SkipAlreadyReplied && alreadyReplied)
            {
                skipped++;
                Report("SKIP_REPLIED", $"Bỏ qua {currentUser}: lịch sử cho biết đã trả lời.");
                return true;
            }

            // 6) Sau Accept, ưu tiên dùng ngay conversation hiện tại nếu TikTok đã mở composer.
            // Nếu chưa có composer thì quay về danh sách bằng nút Back DOM, KHÔNG reload.
            // Chỉ khi DOM recovery thất bại mới Navigate /messages một lần.
            var composerReady = await IsMessageComposerReadyAsync(650);
            if (composerReady)
            {
                Trace("[POST_ACCEPT_FAST_PATH] Composer đã sẵn sàng ngay sau Accept → gửi trực tiếp, không quay trang.");
            }
            else
            {
                Report("BACK_MESSAGES", $"Đã Accept {currentUser}; quay về danh sách Tin nhắn bằng DOM...");
                var backOk = await TryReturnFromRequestsToMessagesByDomAsync();
                if (!backOk)
                {
                    await RecoverMessagesHomeAsync("Không quay về Inbox bằng DOM được sau Accept");
                }

                // 7) Tìm đúng username đã lưu tạm, click để hiện ô nhập + nút gửi.
                Report("OPEN_ACCEPTED_CHAT", $"Đang mở lại cuộc trò chuyện của {currentUser}...");
                Trace($"[OPEN_ACCEPTED_CHAT] tìm lại name={tempDisplayName} handle={tempHandle}");
                var opened = await RetryBoolAsync(() => OpenAcceptedConversationAsync(tempDisplayName, tempHandle), "open_accepted_chat");
                if (!opened)
                {
                    // Recovery cuối: chỉ reload khi selector/DOM thật sự thất bại.
                    await RecoverMessagesHomeAsync($"Không tìm được chat '{currentUser}' bằng DOM");
                    opened = await RetryBoolAsync(() => OpenAcceptedConversationAsync(tempDisplayName, tempHandle), "open_accepted_chat_after_recovery");
                }
                if (!opened)
                    throw new InvalidOperationException($"Không tìm/click được '{currentUser}' trong danh sách Tin nhắn sau khi Accept.");
                Trace("[OPEN_ACCEPTED_CHAT] Click conversation OK; đang chờ ô nhập.");
            }

            // 8) Gán nội dung vào ô nhập và BẤM NÚT GỬI.
            Report("REPLY", $"Đang gửi {messages.Length} tin cho {currentUser}...");
            var sentThisRound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();

                // Khóa ở mức NỘI DUNG, không chỉ ở nút Gửi: nếu cùng một message vô tình
                // xuất hiện hai lần trong payload thì không được dán/gửi lần thứ hai.
                var messageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
                if (!sentThisRound.Add(messageKey))
                {
                    Trace($"[SEND_DUPLICATE_BLOCKED] Bỏ qua nội dung trùng trong cùng vòng cho user={currentUser}.");
                    continue;
                }

                if (!await SendOneMessageByButtonAsync(message))
                    throw new InvalidOperationException("Đã mở đúng hội thoại nhưng không gán được nội dung hoặc không bấm được nút Gửi.");
                await DelayRandomAsync();
            }

            // 9) Click Gửi xong => kết thúc 1 vòng.
            replied++;
            if (!string.IsNullOrWhiteSpace(historyKey))
            {
                var item = history.Items.FirstOrDefault(x => string.Equals(x.Key, historyKey, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    item = new MessageReplyHistoryItem
                    {
                        Key = historyKey,
                        User = currentUser,
                        AcceptedAtUtc = DateTime.UtcNow
                    };
                    history.Items.Add(item);
                }
                item.Status = "REPLIED";
                item.RepliedAtUtc = DateTime.UtcNow;
                SaveMessageReplyHistory(options.HistoryPath, history);
            }

            Report("ROUND_DONE", $"Hoàn tất 1 vòng cho {currentUser}; chuẩn bị người tiếp theo.");
            return true;
        }

        // Auto-message mode phải coi LIVE là luồng ưu tiên. Lưu URL trước khi rời LIVE
        // và luôn cố quay lại trong finally, kể cả khi xử lý tin nhắn ném exception/cancel.
        var returnUrl = "";
        if (options.ReturnToPreviousPage)
        {
            try
            {
                returnUrl = ResultString(await EvalAsync("(() => String(location.href || ''))()", ct: ct)).Trim();
                if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                    returnUrl = "";
                Trace($"[RETURN_PAGE_CAPTURE] url={returnUrl}");
            }
            catch (Exception ex)
            {
                Trace($"[RETURN_PAGE_CAPTURE_FAILED] {ex.Message}");
                returnUrl = "";
            }
        }

        async Task ReturnToPreviousPageBestEffortAsync()
        {
            if (!options.ReturnToPreviousPage || string.IsNullOrWhiteSpace(returnUrl)) return;
            try
            {
                // Không dùng ct của phiên tin nhắn ở bước phục hồi vì ct có thể đã bị Cancel.
                // Dùng timeout riêng để việc quay lại LIVE vẫn được thử trước khi resume automation.
                using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var current = "";
                try
                {
                    current = ResultString(await EvalAsync("(() => String(location.href || ''))()", ct: restoreCts.Token)).Trim();
                }
                catch { }

                if (!string.Equals(current, returnUrl, StringComparison.OrdinalIgnoreCase))
                {
                    Trace($"[RETURN_PAGE_START] from={current} to={returnUrl}");
                    await NavigateAndWaitAsync(returnUrl, 700, 18000, restoreCts.Token);
                }
                else
                {
                    Trace("[RETURN_PAGE_SKIP] Đã ở đúng URL trước khi xử lý Tin nhắn.");
                }

                var settle = Math.Clamp(options.ReturnPageSettleMs, 0, 10000);
                if (settle > 0) await Task.Delay(settle, restoreCts.Token);
                Trace($"[RETURN_PAGE_OK] url={returnUrl}");
            }
            catch (Exception ex)
            {
                // Đây là best-effort. Manager vẫn resume LIVE để engine tự dùng cơ chế recovery hiện có.
                Trace($"[RETURN_PAGE_FAILED] {ex.GetType().Name}: {ex.Message}");
                _log.Warn($"[MESSAGE_REPLY_RETURN_PAGE_FAILED] url={returnUrl} error={ex.Message}");
            }
        }

        try
        {
            Report("START", "Bắt đầu xử lý tin nhắn TikTok...");

            // Đọc số request ở thời điểm bấm Chạy. Chỉ navigate nếu Chrome chưa ở /messages.
            await EnsureMessagesPageAsync();
            var initialCount = await ReadRequestCountFromMessagesHomeAsync();
            if (initialCount >= 0)
            {
                found = initialCount;
                _log.Info($"[MESSAGE_REPLY_INITIAL_COUNT] requests={initialCount}");
            }
            else
            {
                _log.Warn("[MESSAGE_REPLY_INITIAL_COUNT] Không đọc được badge số lượng; sẽ chạy đến khi không còn request.");
            }

            if (initialCount == 0)
            {
                Report("DONE", "Không có yêu cầu tin nhắn nào cần xử lý.", running: false, completed: true);
                return new TikTokMessageReplyRunResult(0, 0, 0, 0, 0, 0, false, "Không có yêu cầu tin nhắn.");
            }

            // "Chỉ xử lý yêu cầu có lúc bấm Chạy": khóa số vòng theo badge ban đầu.
            // Nếu không đọc được badge, dùng safety 250 và tự dừng khi ReadFirstRequestRowAsync trả null.
            var maxRounds = options.OnlyInitialRequests && initialCount > 0 ? initialCount : 250;
            var rounds = 0;
            while (rounds < maxRounds)
            {
                ct.ThrowIfCancellationRequested();
                var countThisRound = false;
                try
                {
                    var hadRequest = await ProcessOneRoundAsync();
                    if (!hadRequest) break;
                    countThisRound = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    countThisRound = true;
                    failed++;
                    _log.Warn($"[MESSAGE_REPLY_ITEM_FAILED] user={currentUser} error={ex.Message}");
                    Trace($"[ITEM_FAILED] user={currentUser} | {ex.GetType().Name}: {ex.Message}");
                    Report("ITEM_FAILED", $"Lỗi {currentUser}: {ex.Message}");
                    if (options.AbortOnAnyError)
                    {
                        Trace("[FAIL_OPEN_TO_LIVE] Auto mode gặp lỗi request → bỏ ngay phần Tin nhắn để trả quyền cho LIVE.");
                        throw new InvalidOperationException($"Lỗi xử lý Tin nhắn ở {currentUser}; bỏ phiên để quay về LIVE: {ex.Message}", ex);
                    }
                    // Manual mode giữ recovery cũ: chỉ reload khi một vòng thực sự lỗi.
                    try { await RecoverMessagesHomeAsync($"Vòng lỗi: {ex.Message}"); }
                    catch (Exception recoverEx) { Trace($"[RECOVERY_FAILED] {recoverEx.Message}"); }
                }
                finally
                {
                    // Hết request không phải là một vòng xử lý và không được cộng processed/rounds.
                    if (countThisRound)
                    {
                        processed++;
                        rounds++;
                    }
                }

                if (!options.OnlyInitialRequests && rounds >= 250) break;
            }

            // Nếu badge ban đầu đọc không được, found phản ánh số vòng đã gặp request.
            if (found <= 0) found = Math.Max(accepted + failed, processed);

            var doneMessage = noRequestsLeft
                ? $"Đã hết yêu cầu tin nhắn. Hoàn tất: accept {accepted}, trả lời {replied}, bỏ qua {skipped}, lỗi {failed}."
                : $"Hoàn tất: accept {accepted}, trả lời {replied}, bỏ qua {skipped}, lỗi {failed}.";
            Report("DONE", doneMessage, running: false, completed: true);
            return new TikTokMessageReplyRunResult(found, processed, accepted, replied, skipped, failed, false,
                noRequestsLeft
                    ? $"Đã hết yêu cầu tin nhắn. Chấp nhận: {accepted}; Đã trả lời: {replied}; Bỏ qua: {skipped}; Lỗi: {failed}."
                    : $"Hoàn tất. Yêu cầu: {found}; Chấp nhận: {accepted}; Đã trả lời: {replied}; Bỏ qua: {skipped}; Lỗi: {failed}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            Report("STOPPED", "Đã dừng xử lý tin nhắn.", running: false, completed: true);
            return new TikTokMessageReplyRunResult(found, processed, accepted, replied, skipped, failed, true, "Đã dừng theo yêu cầu.");
        }
        finally
        {
            await ReturnToPreviousPageBestEffortAsync();
        }
    }
}
