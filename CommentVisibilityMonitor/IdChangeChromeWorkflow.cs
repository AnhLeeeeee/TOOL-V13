using System.Text.Json;
using System.Text.RegularExpressions;
using ToolTikTokV11.Services;

namespace CommentVisibilityMonitor;

internal sealed record IdChangeAttemptResult(string State, string Detail);

// Runs only in the independent Check -> Doi ID Observer Chrome.
// Does not call UpdateTikTokProfileIdentityAsync (Name/Avatar/Video/Auto Run).
internal sealed class IdChangeChromeWorkflow
{
    readonly ChromeController _chrome;
    readonly Action<string> _log;
    public IdChangeChromeWorkflow(ChromeController chrome, Action<string> log) { _chrome=chrome; _log=log; }

    static string Normalize(string v) => (v ?? "").Trim().TrimStart('@').ToLowerInvariant();
    static string Value(JsonElement result)
        => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    async Task<string> EvalString(string js, CancellationToken ct)
        => Value(await _chrome.EvalAsync(js, ct: ct));

    // Only trust TikTok's OWN profile control or own editable profile page.
    // A missing selector must return ACCOUNT_UNKNOWN, NOT ACCOUNT_MISMATCH.
    public async Task<string> ReadOwnUsernameAsync(CancellationToken ct)
    {
        const string ownProbe = """
(() => {
  const visible = e => { if (!e) return false; const r=e.getBoundingClientRect(); const s=getComputedStyle(e); return r.width>2 && r.height>2 && s.visibility!=='hidden' && s.display!=='none'; };
  const norm = s => String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const parse = href => { try { const u=new URL(href,location.href); if(!/(^|\.)tiktok\.com$/i.test(u.hostname))return ''; const m=u.pathname.match(/^\/@([a-z0-9_.]+)\/?$/i);return m?m[1].toLowerCase():''; }catch(_){return '';} };
  const nav=document.querySelector('[data-e2e="nav-profile"]');
  const ownAnchor=nav?.closest?.('a[href]') || nav?.querySelector?.('a[href]');
  const ownHref=ownAnchor?.href || nav?.getAttribute('href') || '';
  const fromOwnNav=parse(ownHref);
  if (fromOwnNav) return 'OWN_NAV|'+fromOwnNav;
  const ownPage=visible(document.querySelector('[data-e2e="edit-profile-entrance"]')) ||
    [...document.querySelectorAll('button,[role="button"]')].some(b=>visible(b)&&/^(edit profile|sua ho so|chinh sua ho so)$/.test(norm(b.innerText||b.textContent||b.getAttribute('aria-label'))));
  const modal=[...document.querySelectorAll('[role="dialog"],[aria-modal="true"]')].filter(visible).at(-1);
  const isOwnEditModal=Boolean(modal && /sua ho so|edit profile/.test(norm(modal.querySelector('h1,h2,h3,[role="heading"]')?.textContent || '')));
  const fromOwnUrl=parse(location.href);
  if ((ownPage || isOwnEditModal) && fromOwnUrl) return 'OWN_PAGE|'+fromOwnUrl;
  if (nav && visible(nav)) { nav.click(); return 'NAV_CLICKED'; }
  const ownSideBar=[...document.querySelectorAll('nav a,aside a,[role="navigation"] a')]
    .find(a=>visible(a)&&/^(ho so|profile)$/.test(norm(a.innerText||a.textContent||a.getAttribute('aria-label'))));
  if(ownSideBar){ownSideBar.click();return 'NAV_CLICKED';}
  return 'UNKNOWN';
})()
""";
        // First inspect the existing page.  If the user is already on their own
        // profile or in its Edit dialog, preserve that strong signal.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await EvalString(ownProbe, ct);
            if (result.StartsWith("OWN_NAV|", StringComparison.Ordinal) ||
                result.StartsWith("OWN_PAGE|", StringComparison.Ordinal))
            {
                var actual = Normalize(result[(result.IndexOf('|') + 1)..]);
                if (Regex.IsMatch(actual, "^[a-z0-9._]{2,30}$"))
                {
                    _log($"[ID_CHANGE_ACCOUNT_PROBE_OK] source={result[..result.IndexOf('|')]} actual={actual}");
                    return actual;
                }
            }
            _log($"[ID_CHANGE_ACCOUNT_PROBE_RETRY] attempt={attempt + 1}/3 state={result}");
            // TikTok routing and avatar/profile sidebar can be asynchronous.
            await Task.Delay(3000, ct);
        }
        string lastUrl = "";
        try { lastUrl = await EvalString("String(location.href || '')", ct); }
        catch { }
        _log("[ID_CHANGE_ACCOUNT_PROBE_UNKNOWN] own profile not verified after 3 probes url=" + lastUrl);
        return "";
    }

    public async Task<IdChangeAttemptResult> BeginAsync(string expectedOld, string candidate, CancellationToken ct)
    {
        expectedOld=Normalize(expectedOld); candidate=Normalize(candidate);
        if (candidate.StartsWith("user", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(candidate, "^[a-z][a-z0-9._]{4,23}$") || candidate.EndsWith('.') || candidate.EndsWith('_'))
            return new("INVALID_GENERATED", "ID sinh ra không hợp lệ hoặc bắt đầu bằng user.");
        var actual = await ReadOwnUsernameAsync(ct);
        if (string.IsNullOrWhiteSpace(actual))
            return new("ACCOUNT_UNKNOWN", "Không đọc được TikTok ID của tài khoản đang đăng nhập; KHÔNG đổi ID.");
        if (!string.Equals(actual,expectedOld,StringComparison.OrdinalIgnoreCase))
            return new("ACCOUNT_MISMATCH", $"Tài khoản đang đăng nhập @{actual} không khớp @{expectedOld}; KHÔNG đổi ID.");
        await _chrome.NavigateAndWaitAsync("https://www.tiktok.com/@"+Uri.EscapeDataString(actual),900,18000,ct);
        bool clicked=false;
        for(int attempt=0;attempt<3 && !clicked;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result=await EvalString("""
(() => {
  const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect();const s=getComputedStyle(e);return r.width>2&&r.height>2&&s.visibility!=='hidden'&&s.display!=='none'};
  const norm=s=>String(s||'').replace(/\s+/g,' ').trim().toLowerCase();
  const direct=document.querySelector('[data-e2e="edit-profile-entrance"]');
  const button = (direct && visible(direct) ? direct : [...document.querySelectorAll('button,[role="button"]')].find(b=>visible(b)&&/^(edit profile|sửa hồ sơ|chỉnh sửa hồ sơ)$/i.test(norm(b.innerText||b.textContent||b.getAttribute('aria-label')))));
  if(!button)return 'NO_EDIT_BUTTON';
  button.click();return 'CLICKED';
})()
""",ct);
            clicked=result=="CLICKED";
            if(!clicked && attempt<2) { await _chrome.ReloadAndWaitAsync(700,12000,ct); await Task.Delay(500,ct); }
        }
        if (!clicked) return new("MANUAL_REQUIRED","Không tìm thấy Sửa hồ sơ trên chính tài khoản vừa đăng nhập.");
        await Task.Delay(650,ct);
        // Nested TikTok ID editor or directly editable field: only act on explicitly labeled ID row.
        for (int i=0;i<4;i++)
        {
            var mode = await EvalString(EditorJs("locate",candidate), ct);
            if(mode=="FIELD_READY") break;
            if(mode=="FIELD_READONLY" || mode=="ROW_CLICKABLE")
            {
                var action = await EvalString(EditorJs("open",candidate),ct);
                if(action!="OPENED") return new("MANUAL_REQUIRED","Không mở được ô TikTok ID; không chạm ô Tên.");
            }
            else if(mode=="COOLDOWN") return new("COOLDOWN","TikTok đang giới hạn đổi ID 30 ngày.");
            else if(i==3) return new("MANUAL_REQUIRED","Không tìm thấy ô TikTok ID được gắn nhãn rõ ràng.");
            await Task.Delay(450,ct);
        }
        if(await EvalString(EditorJs("focus",candidate),ct)!="FOCUSED")
            return new("MANUAL_REQUIRED","Không focus được chính xác ô TikTok ID.");
        try { await _chrome.TypeTikTokIdByKeyboardAsync(candidate, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log("[ID_CHANGE_KEYBOARD_INPUT_ERROR] " + ex.Message);
            return new("MANUAL_REQUIRED", "Nhập qua bàn phím CDP thất bại; không gán trực tiếp bằng JavaScript.");
        }
        if(await EvalString(EditorJs("verify_typed",candidate),ct)!="FILLED")
            return new("MANUAL_REQUIRED","TikTok ID không khớp sau khi nhập qua bàn phím CDP.");
        _log("[ID_CHANGE_KEYBOARD_INPUT_OK] len=" + candidate.Length);
        // Wait 5 seconds after EVERY TikTok ID input (including retries) so TikTok's
        // asynchronous availability check can finish before reading its validation state.
        // Cancellation still works immediately when the user presses Stop.
        await Task.Delay(5000,ct);
        var validity="";
        for(int i=0;i<7;i++)
        {
            ct.ThrowIfCancellationRequested();
            validity=await EvalString(EditorJs("validate",candidate),ct);
            if(validity is "AVAILABLE" or "UNAVAILABLE" or "COOLDOWN" or "RATE_LIMIT") break;
            await Task.Delay(550,ct);
        }
        if(validity=="AVAILABLE")
        {
            // TikTok checks availability asynchronously. Require a second stable probe.
            await Task.Delay(950,ct);
            validity=await EvalString(EditorJs("validate",candidate),ct);
        }
        if(validity=="UNAVAILABLE") return new("UNAVAILABLE","TikTok báo ID không khả dụng; thử ID khác.");
        if(validity=="COOLDOWN") return new("COOLDOWN","TikTok báo chưa đủ thời gian đổi ID.");
        if(validity=="RATE_LIMIT") return new("RATE_LIMIT","TikTok giới hạn thao tác; cần dừng.");
        if(validity!="AVAILABLE") return new("MANUAL_REQUIRED","Không xác minh được ID hợp lệ và nút Lưu. Không bấm Lưu.");
        return new("READY_TO_SAVE","TikTok xác nhận ô ID hợp lệ và nút Lưu khả dụng.");
    }

    public async Task<IdChangeAttemptResult> SaveAndVerifyAsync(string candidate, CancellationToken ct)
    {
        candidate=Normalize(candidate);
        var saved=await EvalString(EditorJs("save",candidate),ct);
        if(saved!="SAVED") return new("NEED_VERIFY","Đã chuẩn bị ghi ID nhưng chưa chắc lệnh Lưu được gửi.");
        _log("[ID_CHANGE_SAVE_CLICKED] candidate="+candidate);
        for (var attempt=0;attempt<5;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1200,ct);
            try
            {
                var actual=await ReadOwnUsernameAsync(ct);
                if(string.Equals(actual,candidate,StringComparison.OrdinalIgnoreCase))
                    return new("DONE","Đã xác minh @"+candidate+" từ đường dẫn Hồ sơ của tài khoản đăng nhập.");
            }
            catch (Exception e) { _log("[ID_CHANGE_VERIFY_RETRY] "+e.GetType().Name); }
        }
        return new("NEED_VERIFY","Đã bấm Lưu nhưng chưa xác minh được username mới; kiểm tra ID dự kiến, không thử tiếp.");
    }

    // Very restrictive field selection: matches explicit TikTok ID / username label, NEVER nickname.
    // This JS is only used inside the dedicated ID change browser.
    static string EditorJs(string action,string candidate) => """
(() => {
  const action=__ACTION__, wanted=__WANTED__;
  const norm=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
  const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect();const s=getComputedStyle(e);return r.width>2&&r.height>2&&s.display!=='none'&&s.visibility!=='hidden'};
  const dialogs=[...document.querySelectorAll('[role="dialog"],[aria-modal="true"]')].filter(visible);
  const scope=dialogs.at(-1)||document;
  const title=norm(scope.querySelector('h1,h2,h3,[role="heading"]')?.textContent||'');
  const inputs=[...scope.querySelectorAll('input')].filter(x=>visible(x) && /^(text|search|)$/.test(x.getAttribute('type')||''));
  const isId=s=>/tiktok.?id|username|user.?name|ten nguoi dung/.test(s);
  const isNickname=s=>/nickname|biet danh|display.?name/.test(s);
  const isIdEditor=isId(title) && !isNickname(title);
  const scored=inputs.map(el=>{
    const attrs=norm([el.name,el.id,el.placeholder,el.getAttribute('aria-label'),el.getAttribute('data-e2e')].join(' '));
    let n=el.parentElement, near='';
    for(let i=0;i<3&&n;i++,n=n.parentElement) {
      const t=norm(n.innerText||n.textContent);
      // Avoid matching a whole Edit Profile dialog containing BOTH Name and ID.
      if(t.length>0 && t.length<=100 && isId(t) && !isNickname(t)){near=t;break;}
    }
    const associated=el.id ? norm(scope.querySelector('label[for="'+CSS.escape(el.id)+'"]')?.textContent||'') : '';
    let score=0;
    if(isId(attrs))score+=150;
    if(isId(associated)&&!isNickname(associated))score+=130;
    if(isId(near))score+=70;
    if(isNickname(attrs)||isNickname(associated))score-=400;
    if(isIdEditor && inputs.length===1)score+=110;
    return {el,score};
  }).sort((a,b)=>b.score-a.score);
  const best=scored[0];
  const input=best?.score>0 && (scored.length===1 || best.score>scored[1].score) ? best.el : null;
  const body=norm(scope.innerText||scope.textContent||'');
  const cooldown=/30 ngay|30 days|change your username again|doi ten nguoi dung sau/.test(body);
  if(action==='locate'){
    if(cooldown)return 'COOLDOWN';
    if(input)return input.readOnly||input.disabled?'FIELD_READONLY':'FIELD_READY';
    const leaves=[...scope.querySelectorAll('label,span,div,p')].filter(x=>visible(x)&&x.children.length<=2&&/^(tiktok id|username|ten nguoi dung)$/.test(norm(x.textContent)));
    return leaves.length?'ROW_CLICKABLE':'NO_ID_FIELD';
  }
  if(action==='open'){
    let hit=input;
    if(!hit){
      const labels=[...scope.querySelectorAll('label,span,div,p')].filter(x=>visible(x)&&x.children.length<=2&&/^(tiktok id|username|ten nguoi dung)$/.test(norm(x.textContent)));
      for(const label of labels){
        let n=label.parentElement;
        for(let k=0;k<3&&n;k++,n=n.parentElement){
          const button=[...n.querySelectorAll('button,[role="button"],a')].filter(visible).find(b=>/edit|sua|chinh|pen|pencil/i.test(String(b.getAttribute('aria-label')||'')+' '+String(b.innerText||'')+' '+String(b.getAttribute('data-e2e')||''))) || [...n.querySelectorAll('button,[role="button"]')].filter(visible).at(-1);
          if(button){hit=button;break;}
        }
        if(hit)break;
      }
    }
    if(!hit)return 'NO_OPEN_CONTROL';
    hit.click();return 'OPENED';
  }
  if(!input || input.disabled || input.readOnly)return 'NO_EDITABLE_ID_INPUT';
  if(action==='focus') {
    input.focus();
    input.dataset.idChangeTarget='1';
    return document.activeElement===input?'FOCUSED':'NOT_FOCUSED';
  }
  if(action==='verify_typed') {
    return document.activeElement===input && String(input.value||'').toLowerCase()===wanted?'FILLED':'NOT_FILLED';
  }
  const local=norm(input.parentElement?.parentElement?.innerText||'');
  if(/not available|unavailable|already taken|already exists|khong kha dung|da duoc su dung|khong the su dung|invalid username|khong hop le/.test(local))return 'UNAVAILABLE';
  if(cooldown)return 'COOLDOWN';
  if(/too many attempts|try again later|qua nhieu lan|thu lai sau/.test(local+' '+body))return 'RATE_LIMIT';
  const buttons=[...scope.querySelectorAll('button,[role="button"]')].filter(visible);
  const save=buttons.find(b=>/^(luu|save|save changes|luu thay doi|xac nhan|confirm)$/.test(norm(b.textContent||b.getAttribute('aria-label'))) && !b.disabled && b.getAttribute('aria-disabled')!=='true');
  // Must stay on the username editor (not generic profile popup with another Save).
  if(!save || String(input.value||'').toLowerCase()!==wanted)return 'NOT_READY';
  if(action==='validate')return 'AVAILABLE';
  if(action==='save'){
    save.click();return 'SAVED';
  }
  return 'NO_ACTION';
})()
""".Replace("__ACTION__",JsonSerializer.Serialize(action)).Replace("__WANTED__",JsonSerializer.Serialize(candidate));
}
