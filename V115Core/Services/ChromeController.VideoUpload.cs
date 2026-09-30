using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed record TikTokVideoUploadResult(
    bool Ok,
    bool Posted,
    bool PrivacyUpdated,
    bool ProfileVerified,
    string VideoPath,
    string PostedHref,
    string Message,
    string Error)
{
    public bool DeleteFallbackAttempted { get; init; }
    public bool DeleteFallbackSucceeded { get; init; }
    public int DeleteFallbackDeletedCount { get; init; }
    public int DeleteFallbackRemainingCount { get; init; } = -1;
    public string DeleteFallbackError { get; init; } = "";
}

public sealed record TikTokVideoUploadProgress(
    bool Running,
    string Stage,
    string VideoPath,
    string PostedHref,
    string Message,
    bool Completed,
    bool Ok,
    bool Posted,
    bool PrivacyUpdated,
    bool ProfileVerified,
    string Error)
{
    public bool DeleteFallbackAttempted { get; init; }
    public bool DeleteFallbackSucceeded { get; init; }
    public int DeleteFallbackDeletedCount { get; init; }
    public int DeleteFallbackRemainingCount { get; init; } = -1;
    public string DeleteFallbackError { get; init; } = "";
}

public sealed partial class ChromeController
{
    static string GetTikTokPostId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";
        var clean = StripTikTokUrlSuffix(href.Trim()).TrimEnd('/');
        var slash = clean.LastIndexOf('/');
        return slash >= 0 && slash + 1 < clean.Length ? clean[(slash + 1)..] : "";
    }

    async Task<bool> WaitForTikTokStudioUploadShellAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  if (!location.pathname.includes('/tiktokstudio/upload')) return false;
  return !!document.querySelector('input[type="file"]');
})()
""";
        return await WaitVideoBoolAsync(js, 30000, 300, ct);
    }

    async Task SetTikTokUploadFileAsync(string videoPath, CancellationToken ct)
    {
        var fileEval = await Cdp.CallAsync("Runtime.evaluate", new
        {
            expression = "(()=>document.querySelector('input[type=\"file\"][accept*=\"video\" i]') || document.querySelector('input[type=\"file\"]'))()",
            awaitPromise = false,
            returnByValue = false,
            userGesture = true
        }, ct);

        if (!fileEval.TryGetProperty("result", out var fileResult)
            || !fileResult.TryGetProperty("objectId", out var objectIdElement)
            || string.IsNullOrWhiteSpace(objectIdElement.GetString()))
        {
            throw new InvalidOperationException("Không lấy được DOM object của input file video TikTok Studio.");
        }

        await Cdp.CallAsync("DOM.setFileInputFiles", new
        {
            files = new[] { videoPath },
            objectId = objectIdElement.GetString()
        }, ct);

        _log.Info($"[VIDEO_UPLOAD_FILE_SET] file={Path.GetFileName(videoPath)}");
    }

    async Task<string> HandleTikTokStudioUploadPopupAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const dialogs = [...document.querySelectorAll('[role="dialog"],[aria-modal="true"],div')]
    .filter(visible)
    .map(el => ({el, text:fold(el.innerText || el.textContent || ''), r:el.getBoundingClientRect()}))
    .filter(x => x.r.width > 220 && x.r.height > 120)
    .sort((a,b) => (a.r.width*a.r.height) - (b.r.width*b.r.height));

  const clickByWords = (root, words) => {
    const buttons = [...root.querySelectorAll('button,[role="button"],[tabindex]')].filter(visible);
    for (const b of buttons) {
      const t = fold(`${b.innerText || b.textContent || ''} ${b.getAttribute?.('aria-label') || ''}`);
      if (!words.includes(t)) continue;
      try { b.click(); return t; } catch (_) {}
    }
    return '';
  };

  for (const d of dialogs) {
    const t = d.text;
    if ((t.includes('bat kiem tra noi dung tu dong') || t.includes('enable automatic content check') || t.includes('automatic content check'))
        && (t.includes('kiem tra ban quyen') || t.includes('copyright') || t.includes('kiem tra noi dung') || t.includes('content check'))) {
      const hit = clickByWords(d.el, ['huy','cancel','not now','de sau','later']);
      if (hit) return JSON.stringify({action:'AUTO_CHECK_CANCEL', text:hit});
    }
  }

  for (const d of dialogs) {
    const hit = clickByWords(d.el, ['da hieu','got it','understood','i understand','ok']);
    if (hit) return JSON.stringify({action:'GOT_IT', text:hit});
  }

  return JSON.stringify({action:''});
})()
""";

        try
        {
            var raw = ReadEvalString(await EvalAsync(js, ct: ct));
            if (string.IsNullOrWhiteSpace(raw)) return "";
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                return a.GetString() ?? "";
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
        catch (Exception ex) { _log.Warn("[VIDEO_UPLOAD_POPUP_GUARD] " + ex.Message); }
        return "";
    }

    async Task EnsureTikTokStudioChecksOffAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const labels = [
    ['music','kiem tra ban quyen nhac','check music copyright','music copyright check'],
    ['quick','kiem tra noi dung nhanh','quick content check','content check']
  ];
  const result = [];
  for (const [key, ...phrases] of labels) {
    const textNodes = [...document.querySelectorAll('div,span,label,p')].filter(visible);
    let label = textNodes.find(el => phrases.some(p => fold(el.innerText || el.textContent || '') === p))
      || textNodes.find(el => phrases.some(p => fold(el.innerText || el.textContent || '').includes(p)));
    if (!label) { result.push({key,found:false}); continue; }

    let row = label;
    let sw = null;
    for (let i=0; i<7 && row; i++, row=row.parentElement) {
      sw = row.querySelector?.('input[type="checkbox"],[role="switch"],button[role="switch"],button[aria-checked]');
      if (sw && visible(sw)) break;
      sw = null;
    }
    if (!sw) { result.push({key,found:true,switchFound:false}); continue; }

    const state = (() => {
      if (sw.matches?.('input[type="checkbox"]')) return !!sw.checked;
      const aria = (sw.getAttribute?.('aria-checked') || '').toLowerCase();
      if (aria === 'true') return true;
      if (aria === 'false') return false;
      const ds = (sw.getAttribute?.('data-state') || '').toLowerCase();
      if (ds === 'checked' || ds === 'on') return true;
      if (ds === 'unchecked' || ds === 'off') return false;
      return null;
    })();

    let clicked = false;
    if (state === true) {
      try { sw.click(); clicked = true; } catch (_) {}
    }
    result.push({key,found:true,switchFound:true,on:state,clicked});
  }
  return JSON.stringify(result);
})()
""";

        try
        {
            var raw = ReadEvalString(await EvalAsync(js, ct: ct));
            if (!string.IsNullOrWhiteSpace(raw))
                _log.Info("[VIDEO_UPLOAD_CHECKS_OFF] " + raw);
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
        catch (Exception ex) { _log.Warn("[VIDEO_UPLOAD_CHECKS_OFF_WARN] " + ex.Message); }

        await Task.Delay(350, ct);
    }

    async Task<bool> SetTikTokStudioCaptionAsync(string caption, CancellationToken ct)
    {
        caption ??= "";

        // Caption là best-effort: lỗi locator/input/verify chỉ trả false để caller ghi WARN
        // rồi tiếp tục Đăng. TikTok Studio dùng DraftJS nên ưu tiên tìm node qua CDP
        // flattened DOM thay vì document.querySelector của execution context hiện tại.
        static Dictionary<string, string> ReadFlatAttributes(JsonElement raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!raw.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
                return map;
            var values = attrs.EnumerateArray().ToArray();
            for (var i = 0; i + 1 < values.Length; i += 2)
            {
                var key = values[i].ValueKind == JsonValueKind.String ? values[i].GetString() ?? "" : "";
                var value = values[i + 1].ValueKind == JsonValueKind.String ? values[i + 1].GetString() ?? "" : "";
                if (key.Length > 0) map[key] = value;
            }
            return map;
        }

        async Task<(int NodeId, int BackendNodeId)?> FindCaptionNodeAsync()
        {
            try { await Cdp.CallAsync("DOM.enable", ct: ct); } catch { }
            var flat = await Cdp.CallAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, ct);
            if (!flat.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            var parentById = new Dictionary<int, int>();
            var attrsById = new Dictionary<int, Dictionary<string, string>>();
            var backendById = new Dictionary<int, int>();
            foreach (var raw in nodes.EnumerateArray())
            {
                var nodeId = raw.TryGetProperty("nodeId", out var ni) && ni.TryGetInt32(out var nid) ? nid : 0;
                if (nodeId <= 0) continue;
                var parentId = raw.TryGetProperty("parentId", out var pi) && pi.TryGetInt32(out var pid) ? pid : 0;
                var backendId = raw.TryGetProperty("backendNodeId", out var bi) && bi.TryGetInt32(out var bid) ? bid : 0;
                parentById[nodeId] = parentId;
                backendById[nodeId] = backendId;
                attrsById[nodeId] = ReadFlatAttributes(raw);
            }

            (int NodeId, int BackendNodeId, int Score)? best = null;
            foreach (var kv in attrsById)
            {
                var nodeId = kv.Key;
                var a = kv.Value;
                a.TryGetValue("class", out var cls);
                a.TryGetValue("contenteditable", out var editable);
                a.TryGetValue("role", out var role);
                cls ??= "";
                editable ??= "";
                role ??= "";

                var score = 0;
                if (cls.Contains("public-DraftEditor-content", StringComparison.OrdinalIgnoreCase)) score += 500;
                if (string.Equals(editable, "true", StringComparison.OrdinalIgnoreCase)) score += 250;
                if (string.Equals(role, "combobox", StringComparison.OrdinalIgnoreCase)) score += 180;
                if (cls.Contains("notranslate", StringComparison.OrdinalIgnoreCase)) score += 20;
                if (score < 500) continue;

                var parent = parentById.TryGetValue(nodeId, out var p0) ? p0 : 0;
                for (var depth = 0; depth < 10 && parent > 0; depth++)
                {
                    if (attrsById.TryGetValue(parent, out var pa)
                        && pa.TryGetValue("class", out var parentClass)
                        && parentClass.Contains("caption-editor", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1000;
                        break;
                    }
                    parent = parentById.TryGetValue(parent, out var next) ? next : 0;
                }

                var backend = backendById.TryGetValue(nodeId, out var b) ? b : 0;
                if (best is null || score > best.Value.Score)
                    best = (nodeId, backend, score);
            }

            if (best is null) return null;
            return (best.Value.NodeId, best.Value.BackendNodeId);
        }

        async Task<string?> ResolveCaptionObjectIdAsync((int NodeId, int BackendNodeId) node)
        {
            JsonElement resolved;
            try
            {
                resolved = node.NodeId > 0
                    ? await Cdp.CallAsync("DOM.resolveNode", new { nodeId = node.NodeId }, ct)
                    : await Cdp.CallAsync("DOM.resolveNode", new { backendNodeId = node.BackendNodeId }, ct);
            }
            catch
            {
                if (node.BackendNodeId <= 0) return null;
                try { resolved = await Cdp.CallAsync("DOM.resolveNode", new { backendNodeId = node.BackendNodeId }, ct); }
                catch { return null; }
            }

            if (!resolved.TryGetProperty("object", out var obj)
                || !obj.TryGetProperty("objectId", out var oid)
                || oid.ValueKind != JsonValueKind.String)
                return null;
            return oid.GetString();
        }

        async Task<(bool Ok, string Current)> FocusSelectCaptionAsync()
        {
            var node = await FindCaptionNodeAsync();
            if (node is null) return (false, "");

            try
            {
                if (node.Value.NodeId > 0)
                    await Cdp.CallAsync("DOM.scrollIntoViewIfNeeded", new { nodeId = node.Value.NodeId }, ct);
                else if (node.Value.BackendNodeId > 0)
                    await Cdp.CallAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId = node.Value.BackendNodeId }, ct);
            }
            catch { }

            try
            {
                if (node.Value.NodeId > 0)
                    await Cdp.CallAsync("DOM.focus", new { nodeId = node.Value.NodeId }, ct);
                else if (node.Value.BackendNodeId > 0)
                    await Cdp.CallAsync("DOM.focus", new { backendNodeId = node.Value.BackendNodeId }, ct);
            }
            catch { }

            var objectId = await ResolveCaptionObjectIdAsync(node.Value);
            if (string.IsNullOrWhiteSpace(objectId)) return (false, "");

            var call = await Cdp.CallAsync("Runtime.callFunctionOn", new
            {
                objectId,
                functionDeclaration = """
function() {
  if (!this || typeof this.getBoundingClientRect !== 'function') return {ok:false,current:''};
  const r = this.getBoundingClientRect();
  const cs = getComputedStyle(this);
  if (r.width < 10 || r.height < 10 || cs.display === 'none' || cs.visibility === 'hidden')
    return {ok:false,current:''};
  try { this.scrollIntoView({block:'center', inline:'nearest'}); } catch (_) {}
  try { this.focus({preventScroll:true}); } catch (_) { try { this.focus(); } catch (_) {} }
  const spans = [...this.querySelectorAll('[data-text="true"]')];
  const current = (spans.length ? spans.map(x => x.textContent || '').join('') : (this.innerText || this.textContent || '')).replace(/\u200B/g,'');
  try {
    const sel = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(this);
    sel.removeAllRanges();
    sel.addRange(range);
  } catch (_) {}
  return {ok:true,current};
}
""",
                returnByValue = true,
                awaitPromise = false,
                userGesture = true
            }, ct);

            if (!call.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object)
                return (false, "");
            var ok = value.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var current = value.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.String
                ? cur.GetString() ?? ""
                : "";
            return (ok, current);
        }

        async Task<string?> ReadCaptionAsync()
        {
            var node = await FindCaptionNodeAsync();
            if (node is null) return null;
            var objectId = await ResolveCaptionObjectIdAsync(node.Value);
            if (string.IsNullOrWhiteSpace(objectId)) return null;

            var call = await Cdp.CallAsync("Runtime.callFunctionOn", new
            {
                objectId,
                functionDeclaration = """
function() {
  if (!this) return null;
  const spans = [...this.querySelectorAll('[data-text="true"]')];
  return (spans.length ? spans.map(x => x.textContent || '').join('') : (this.innerText || this.textContent || '')).replace(/\u200B/g,'');
}
""",
                returnByValue = true,
                awaitPromise = false
            }, ct);
            if (!call.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return value.GetString() ?? "";
        }

        static string NormalizeCaptionForCompare(string? value) => (value ?? "")
            .Replace("\u200B", "")
            .Replace("\r\n", "\n")
            .Trim();

        // VPS có thể render editor chậm sau khi video vừa upload. Chờ tối đa ~12 giây,
        // nhưng thấy node DraftJS qua flattened DOM là xử lý ngay.
        var editorReady = false;
        for (var wait = 1; wait <= 30; wait++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var prepared = await FocusSelectCaptionAsync();
                if (prepared.Ok)
                {
                    editorReady = true;
                    _log.Info($"[VIDEO_UPLOAD_CAPTION_FLATDOM_FOUND] wait={wait} currentLen={prepared.Current.Length} wantedLen={caption.Length}");
                    break;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
            catch (Exception ex)
            {
                if (wait == 1 || wait % 10 == 0)
                    _log.Warn($"[VIDEO_UPLOAD_CAPTION_FLATDOM_PROBE_WARN] wait={wait} error={ex.Message}");
            }

            if (wait == 1 || wait % 10 == 0)
                _log.Info($"[VIDEO_UPLOAD_CAPTION_WAIT] step={wait}/30 method=FLAT_DOM");
            await Task.Delay(400, ct);
        }

        if (!editorReady)
        {
            _log.Warn("[VIDEO_UPLOAD_CAPTION_TARGET_NOT_FOUND] DraftJS caption editor not found in flattened DOM after wait; fail-open.");
            return false;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var prepared = await FocusSelectCaptionAsync();
                if (!prepared.Ok)
                {
                    _log.Warn($"[VIDEO_UPLOAD_CAPTION_EDITOR_LOST] attempt={attempt}");
                    await Task.Delay(450, ct);
                    continue;
                }

                _log.Info($"[VIDEO_UPLOAD_CAPTION_BEFORE] attempt={attempt} currentLen={prepared.Current.Length} wantedLen={caption.Length} method=FLAT_DOM");

                // Selection đã được đặt trực tiếp trên DraftJS node. Backspace xóa caption
                // TikTok tự sinh, Input.insertText tạo input thật cho DraftJS/React.
                await Cdp.CallAsync("Input.dispatchKeyEvent", new
                {
                    type = "keyDown",
                    key = "Backspace",
                    code = "Backspace",
                    windowsVirtualKeyCode = 8,
                    nativeVirtualKeyCode = 8
                }, ct);
                await Cdp.CallAsync("Input.dispatchKeyEvent", new
                {
                    type = "keyUp",
                    key = "Backspace",
                    code = "Backspace",
                    windowsVirtualKeyCode = 8,
                    nativeVirtualKeyCode = 8
                }, ct);

                if (!string.IsNullOrEmpty(caption))
                    await Cdp.CallAsync("Input.insertText", new { text = caption }, ct);

                await Task.Delay(320, ct);
                var actual = await ReadCaptionAsync();
                if (actual is not null
                    && NormalizeCaptionForCompare(actual) == NormalizeCaptionForCompare(caption))
                {
                    _log.Info($"[VIDEO_UPLOAD_CAPTION_VERIFIED] attempt={attempt} length={actual.Length} method=FLAT_DOM");
                    return true;
                }

                _log.Warn($"[VIDEO_UPLOAD_CAPTION_VERIFY_MISMATCH] attempt={attempt} actualLen={(actual is null ? -1 : actual.Length)} wantedLen={caption.Length} method=FLAT_DOM");

                // Fallback trong lần kế: Ctrl+A ở chính editor đã focus rồi nhập lại.
                if (attempt < 3)
                {
                    await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17, modifiers = 2 }, ct);
                    await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
                    await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
                    await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17 }, ct);
                    await Task.Delay(120, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                _log.Warn($"[VIDEO_UPLOAD_CAPTION_TRANSIENT] attempt={attempt} {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_UPLOAD_CAPTION_ATTEMPT_WARN] attempt={attempt} {ex.Message}");
            }

            await Task.Delay(450, ct);
        }

        return false;
    }

    async Task<bool> IsTikTokStudioPostButtonEnabledAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 30 && r.height > 20 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  const b = buttons.find(el => {
    const t = fold(el.innerText || el.textContent || el.getAttribute?.('aria-label') || '');
    return t === 'dang' || t === 'post';
  });
  if (!b) return false;
  const disabled = !!b.disabled || b.getAttribute?.('aria-disabled') === 'true' || b.matches?.('[disabled]');
  return !disabled;
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task WaitTikTokStudioUploadReadyAsync(
        string videoPath,
        Action<TikTokVideoUploadProgress>? report,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deadline = started.AddMinutes(9);
        var lastReport = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryDismissTikTokVideoBlockingPopupAsync("upload-processing", ct);
            var popupAction = await HandleTikTokStudioUploadPopupAsync(ct);
            if (!string.IsNullOrWhiteSpace(popupAction))
            {
                _log.Info($"[VIDEO_UPLOAD_POPUP] action={popupAction}");
                await Task.Delay(350, ct);
            }
            await EnsureTikTokStudioChecksOffAsync(ct);

            if (await IsTikTokStudioPostButtonEnabledAsync(ct))
            {
                _log.Info($"[VIDEO_UPLOAD_READY] file={Path.GetFileName(videoPath)} elapsed={(DateTime.UtcNow-started).TotalSeconds:F1}s");
                return;
            }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                var elapsed = (int)Math.Round((DateTime.UtcNow - started).TotalSeconds);
                try
                {
                    report?.Invoke(new TikTokVideoUploadProgress(
                        true, "UPLOADING", videoPath, "",
                        $"Đang tải/xử lý video... {elapsed}s", false, false, false, false, false, ""));
                }
                catch { }
            }
            await Task.Delay(850, ct);
        }

        throw new TimeoutException("Video chưa sẵn sàng để Đăng sau 9 phút. Tool bỏ qua bước đăng để không treo profile.");
    }

    async Task<bool> ClickTikTokStudioPostAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 30 && r.height > 20 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  const b = buttons.find(el => {
    const t = fold(el.innerText || el.textContent || el.getAttribute?.('aria-label') || '');
    return t === 'dang' || t === 'post';
  });
  if (!b) return JSON.stringify({found:false});
  const disabled = !!b.disabled || b.getAttribute?.('aria-disabled') === 'true' || b.matches?.('[disabled]');
  if (disabled) return JSON.stringify({found:true,disabled:true});
  try { b.scrollIntoView({block:'center', inline:'nearest'}); } catch (_) {}
  const r = b.getBoundingClientRect();
  try { b.click(); } catch (_) {}
  return JSON.stringify({found:true,disabled:false,left:r.left,top:r.top,width:r.width,height:r.height});
})()
""";
        var raw = ReadEvalString(await EvalAsync(js, ct: ct));
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var found = root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
            var disabled = root.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
            if (!found || disabled) return false;
        }
        catch { return false; }

        _log.Info("[VIDEO_UPLOAD_POST_CLICKED] method=DOM_CLICK");
        return true;
    }

    async Task<bool> WaitTikTokPostSubmissionAcceptedAsync(CancellationToken ct)
    {
        // Policy VIDEO hiện tại: chỉ chính toast xuất hiện SAU khi bấm Đăng mới là
        // bằng chứng UPLOAD DONE. Không dùng redirect /tiktokstudio/content, privacy
        // hay việc bài xuất hiện trên profile làm điều kiện thành công nữa.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            const string js = """
(() => {
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };

  // Ưu tiên các vùng toast/status trước; body chỉ là fallback vì TikTok có thể
  // thay đổi wrapper của toast giữa các phiên bản giao diện.
  const nodes = [...document.querySelectorAll('[role="alert"],[role="status"],[aria-live],div,span')]
    .filter(visible);
  const matched = nodes.some(el => {
    const t = fold(el.innerText || el.textContent || '');
    return t === 'da dang video'
      || t.includes('da dang video')
      || t === 'video posted'
      || t.includes('video has been posted')
      || t.includes('posted successfully');
  });
  if (matched) return true;

  const body = fold(document.body?.innerText || '');
  return body.includes('da dang video')
    || body.includes('video has been posted')
    || body.includes('posted successfully');
})()
""";
            try
            {
                if (ReadEvalBool(await EvalAsync(js, ct: ct)))
                {
                    _log.Info("[VIDEO_UPLOAD_POST_TOAST_CONFIRMED] text=DA_DANG_VIDEO");
                    return true;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

            await Task.Delay(150, ct);
        }

        _log.Warn("[VIDEO_UPLOAD_POST_TOAST_TIMEOUT] waited=30s text=DA_DANG_VIDEO");
        return false;
    }

    async Task<string> FindNewTikTokProfilePostAsync(
        string profileHref,
        HashSet<string> beforePosts,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            await NavigateAndWaitAsync(profileHref, 700, 30000, ct);
            var scan = await ScanTikTokProfilePostsAsync(profileHref, 10, ct);
            var newly = scan.Hrefs.FirstOrDefault(h => !beforePosts.Contains(StripTikTokUrlSuffix(h)));
            if (!string.IsNullOrWhiteSpace(newly))
            {
                _log.Info($"[VIDEO_UPLOAD_NEW_POST_FOUND] attempt={attempt} href={newly}");
                return StripTikTokUrlSuffix(newly);
            }
            _log.Info($"[VIDEO_UPLOAD_NEW_POST_WAIT] attempt={attempt} before={beforePosts.Count} now={scan.Count}");
            await Task.Delay(2500, ct);
        }
        return "";
    }

    async Task<(bool Ok, bool AlreadyPublic)> SetTikTokStudioPostPrivacyPublicAsync(
        string postedHref,
        CancellationToken ct)
    {
        var postId = GetTikTokPostId(postedHref);
        var deadline = DateTime.UtcNow.AddSeconds(75);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryDismissTikTokVideoBlockingPopupAsync("privacy-content", ct);
            var js = $$"""
(() => {
  const wantedId = {{JsString(postId)}};
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const privacyWords = ['moi nguoi','everyone','public','ban be','friends','chi minh toi','only me','private'];
  const nodes = [...document.querySelectorAll('button,[role="button"],[aria-haspopup],div,span')]
    .filter(visible)
    .map(el => ({el, t:fold(el.innerText || el.textContent || ''), r:el.getBoundingClientRect()}))
    .filter(x => privacyWords.includes(x.t));
  if (!nodes.length) return JSON.stringify({found:false});

  const scored = nodes.map((x, idx) => {
    let row = x.el;
    let best = x.el;
    for (let i=0; i<10 && row; i++, row=row.parentElement) {
      const rr = row.getBoundingClientRect?.();
      if (!rr || rr.width < 250 || rr.height < 40 || rr.height > 400) continue;
      const txt = fold(row.innerText || row.textContent || '');
      if (txt.includes('luot xem') || txt.includes('noi dung') || row.querySelector?.('button,[role="button"]')) best = row;
    }
    const rt = fold(best.innerText || best.textContent || '');
    const html = best.innerHTML || '';
    let score = -x.r.top;
    if (wantedId && (html.includes(wantedId) || rt.includes(wantedId))) score += 100000;
    if (rt.includes('noi dung dang duoc xet duyet') || rt.includes('under review')) score += 5000;
    if (idx === 0) score += 100;
    return {...x, row:best, score};
  }).sort((a,b) => b.score - a.score);

  const hit = scored[0];
  if (!hit) return JSON.stringify({found:false});
  const current = hit.t;
  if (current === 'moi nguoi' || current === 'everyone' || current === 'public')
    return JSON.stringify({found:true,alreadyPublic:true,current});
  try { hit.el.click(); } catch (_) { return JSON.stringify({found:true,clickFailed:true,current}); }
  return JSON.stringify({found:true,opened:true,current});
})()
""";
            string raw;
            try { raw = ReadEvalString(await EvalAsync(js, ct: ct)); }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { await Task.Delay(350, ct); continue; }
            if (string.IsNullOrWhiteSpace(raw)) { await Task.Delay(350, ct); continue; }

            bool found = false, alreadyPublic = false, opened = false;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                found = root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
                alreadyPublic = root.TryGetProperty("alreadyPublic", out var ap) && ap.ValueKind == JsonValueKind.True;
                opened = root.TryGetProperty("opened", out var op) && op.ValueKind == JsonValueKind.True;
            }
            catch { }
            if (!found) { await Task.Delay(550, ct); continue; }
            if (alreadyPublic)
            {
                _log.Info("[VIDEO_UPLOAD_PRIVACY] already=public");
                return (true, true);
            }
            if (!opened) { await Task.Delay(550, ct); continue; }

            await Task.Delay(350, ct);
            const string choosePublicJs = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const all = [...document.querySelectorAll('[role="option"],[role="menuitem"],button,[role="button"],li,div')].filter(visible);
  const candidates = all.filter(el => {
    const t = fold(el.innerText || el.textContent || '');
    return t === 'moi nguoi' || t === 'everyone' || t === 'public';
  }).sort((a,b) => {
    const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return (ra.width*ra.height) - (rb.width*rb.height);
  });
  for (const el of candidates) {
    try { el.click(); return true; } catch (_) {}
  }
  return false;
})()
""";
            bool clicked;
            try { clicked = ReadEvalBool(await EvalAsync(choosePublicJs, ct: ct)); }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { clicked = false; }
            if (!clicked) { await Task.Delay(600, ct); continue; }

            // Không chờ cố định 15 giây cho toast. TikTok nhiều lần cập nhật privacy
            // ngay nhưng toast không được DOM probe nhìn thấy. Poll nhanh cả 2 tín hiệu:
            // toast HOẶC dropdown của đúng bài đã đổi thành Mọi người.
            var confirmDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < confirmDeadline)
            {
                ct.ThrowIfCancellationRequested();
                var confirmJs = $$"""
(() => {
  const wantedId = {{JsString(postId)}};
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const body = fold(document.body?.innerText || '');
  const toast = body.includes('da cap nhat cai dat quyen rieng tu')
    || body.includes('privacy settings have been updated')
    || body.includes('privacy settings updated');

  const privacyWords = ['moi nguoi','everyone','public','ban be','friends','chi minh toi','only me','private'];
  const nodes = [...document.querySelectorAll('button,[role="button"],[aria-haspopup],div,span')]
    .filter(visible)
    .map(el => ({el, t:fold(el.innerText || el.textContent || ''), r:el.getBoundingClientRect()}))
    .filter(x => privacyWords.includes(x.t));
  if (!nodes.length) return JSON.stringify({toast, public:false, current:''});
  const scored = nodes.map((x, idx) => {
    let row=x.el, best=x.el;
    for (let i=0;i<10&&row;i++,row=row.parentElement) {
      const rr=row.getBoundingClientRect?.();
      if (!rr || rr.width<250 || rr.height<40 || rr.height>400) continue;
      const txt=fold(row.innerText || row.textContent || '');
      if (txt.includes('luot xem') || txt.includes('noi dung') || row.querySelector?.('button,[role="button"]')) best=row;
    }
    const rt=fold(best.innerText || best.textContent || '');
    const html=best.innerHTML || '';
    let score=-x.r.top;
    if (wantedId && (html.includes(wantedId) || rt.includes(wantedId))) score += 100000;
    if (rt.includes('noi dung dang duoc xet duyet') || rt.includes('under review')) score += 5000;
    if (idx===0) score += 100;
    return {...x,score};
  }).sort((a,b)=>b.score-a.score);
  const current=scored[0]?.t || '';
  const isPublic=current==='moi nguoi' || current==='everyone' || current==='public';
  return JSON.stringify({toast, public:isPublic, current});
})()
""";
                string confirmRaw;
                try { confirmRaw = ReadEvalString(await EvalAsync(confirmJs, ct: ct)); }
                catch (Exception ex) when (IsTransientDocumentContextError(ex))
                {
                    await Task.Delay(250, ct);
                    continue;
                }
                try
                {
                    using var confirmDoc = JsonDocument.Parse(confirmRaw);
                    var root = confirmDoc.RootElement;
                    var toast = root.TryGetProperty("toast", out var tv) && tv.ValueKind == JsonValueKind.True;
                    var isPublic = root.TryGetProperty("public", out var pv) && pv.ValueKind == JsonValueKind.True;
                    var current = root.TryGetProperty("current", out var cv) && cv.ValueKind == JsonValueKind.String ? (cv.GetString() ?? "") : "";
                    if (toast || isPublic)
                    {
                        _log.Info($"[VIDEO_UPLOAD_PRIVACY_OK] toast={toast} statePublic={isPublic} current={current}");
                        return (true, false);
                    }
                }
                catch { }
                await Task.Delay(250, ct);
            }
            _log.Warn("[VIDEO_UPLOAD_PRIVACY_NOT_CONFIRMED_5S] retrying");
        }
        return (false, false);
    }

    sealed record TikTokStudioContentRowsSnapshot(int Count, string SecondKey);
    sealed record TikTokStudioDeleteFallbackResult(bool Ok, int DeletedCount, int RemainingCount, string Error);

    async Task<TikTokStudioContentRowsSnapshot> ScanTikTokStudioContentRowsAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight + 1200 && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const privacyWords = new Set(['moi nguoi','everyone','public','ban be','friends','chi minh toi','only me','private']);
  const isPrivacyText = t => privacyWords.has(t)
    || t.startsWith('chi minh') || t.startsWith('only me');
  const anchors = [...document.querySelectorAll('button,[role="button"],[aria-haspopup],div,span')]
    .filter(visible)
    .filter(el => isPrivacyText(fold(el.innerText || el.textContent || '')));
  const rows = [];
  for (const anchor of anchors) {
    let node = anchor;
    let best = null;
    for (let i = 0; i < 11 && node; i++, node = node.parentElement) {
      const r = node.getBoundingClientRect?.();
      if (!r) continue;
      if (r.width < Math.min(650, innerWidth * 0.45) || r.height < 70 || r.height > 190) continue;
      const buttons = [...node.querySelectorAll('button,[role="button"]')].filter(visible);
      if (buttons.length < 2) continue;
      best = node;
      break;
    }
    if (!best) continue;
    const r = best.getBoundingClientRect();
    if (rows.some(x => Math.abs(x.top - r.top) < 5)) continue;
    rows.push({el:best, top:r.top});
  }
  rows.sort((a,b) => a.top - b.top);
  const hash = txt => {
    let h = 2166136261 >>> 0;
    for (let i=0;i<txt.length;i++) { h ^= txt.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
    return h.toString(16);
  };
  const secondKey = rows.length > 1 ? hash(rows[1].el.innerHTML || rows[1].el.innerText || '') : '';
  return JSON.stringify({count:rows.length, secondKey});
})()
""";
        try
        {
            var raw = ReadEvalString(await EvalAsync(js, ct: ct));
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var count = root.TryGetProperty("count", out var cv) && cv.TryGetInt32(out var c) ? c : 0;
            var secondKey = root.TryGetProperty("secondKey", out var sk) && sk.ValueKind == JsonValueKind.String
                ? sk.GetString() ?? ""
                : "";
            return new TikTokStudioContentRowsSnapshot(count, secondKey);
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex))
        {
            return new TikTokStudioContentRowsSnapshot(0, "");
        }
        catch
        {
            return new TikTokStudioContentRowsSnapshot(0, "");
        }
    }

    async Task<bool> ClickTikTokStudioSecondRowMenuAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight + 1200 && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const privacyWords = new Set(['moi nguoi','everyone','public','ban be','friends','chi minh toi','only me','private']);
  const isPrivacyText = t => privacyWords.has(t)
    || t.startsWith('chi minh') || t.startsWith('only me');
  const anchors = [...document.querySelectorAll('button,[role="button"],[aria-haspopup],div,span')]
    .filter(visible)
    .filter(el => isPrivacyText(fold(el.innerText || el.textContent || '')));
  const rows = [];
  for (const anchor of anchors) {
    let node = anchor;
    let best = null;
    for (let i = 0; i < 11 && node; i++, node = node.parentElement) {
      const r = node.getBoundingClientRect?.();
      if (!r) continue;
      if (r.width < Math.min(650, innerWidth * 0.45) || r.height < 70 || r.height > 190) continue;
      const buttons = [...node.querySelectorAll('button,[role="button"]')].filter(visible);
      if (buttons.length < 2) continue;
      best = node;
      break;
    }
    if (!best) continue;
    const r = best.getBoundingClientRect();
    if (rows.some(x => Math.abs(x.top - r.top) < 5)) continue;
    rows.push({el:best, top:r.top});
  }
  rows.sort((a,b) => a.top - b.top);
  if (rows.length < 2) return false;
  const row = rows[1].el;
  const rr = row.getBoundingClientRect();
  const buttons = [...row.querySelectorAll('button,[role="button"]')]
    .filter(visible)
    .map(el => ({el, r:el.getBoundingClientRect()}))
    .filter(x => x.r.left > rr.left + rr.width * 0.62 && x.r.width <= 90 && x.r.height <= 90)
    .sort((a,b) => b.r.right - a.r.right);
  const hit = buttons[0];
  if (!hit) return false;
  try { hit.el.click(); return true; } catch (_) { return false; }
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task<bool> ClickTikTokStudioFirstRowMenuAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight + 1200 && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const privacyWords = new Set(['moi nguoi','everyone','public','ban be','friends','chi minh toi','only me','private']);
  const isPrivacyText = t => privacyWords.has(t)
    || t.startsWith('chi minh') || t.startsWith('only me');
  const anchors = [...document.querySelectorAll('button,[role="button"],[aria-haspopup],div,span')]
    .filter(visible)
    .filter(el => isPrivacyText(fold(el.innerText || el.textContent || '')));
  const rows = [];
  for (const anchor of anchors) {
    let node = anchor;
    let best = null;
    for (let i = 0; i < 11 && node; i++, node = node.parentElement) {
      const r = node.getBoundingClientRect?.();
      if (!r) continue;
      if (r.width < Math.min(650, innerWidth * 0.45) || r.height < 70 || r.height > 190) continue;
      const buttons = [...node.querySelectorAll('button,[role="button"]')].filter(visible);
      if (buttons.length < 2) continue;
      best = node;
      break;
    }
    if (!best) continue;
    const r = best.getBoundingClientRect();
    if (rows.some(x => Math.abs(x.top - r.top) < 5)) continue;
    rows.push({el:best, top:r.top});
  }
  rows.sort((a,b) => a.top - b.top);
  if (rows.length < 1) return false;
  const row = rows[0].el;
  const rr = row.getBoundingClientRect();
  const buttons = [...row.querySelectorAll('button,[role="button"]')]
    .filter(visible)
    .map(el => ({el, r:el.getBoundingClientRect()}))
    .filter(x => x.r.left > rr.left + rr.width * 0.62 && x.r.width <= 90 && x.r.height <= 90)
    .sort((a,b) => b.r.right - a.r.right);
  const hit = buttons[0];
  if (!hit) return false;
  try { hit.el.click(); return true; } catch (_) { return false; }
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task<bool> IsTikTokStudioContentConfirmedEmptyAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  if (!location.pathname.includes('/tiktokstudio/content')) return false;
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const text = fold(document.body?.innerText || '');
  if (/\bbai dang\s*0\b/.test(text) || /\bposts?\s*0\b/.test(text)) return true;
  return text.includes('chua co bai dang')
    || text.includes('khong co bai dang')
    || text.includes('no posts yet')
    || text.includes('you have no posts');
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
        catch { return false; }
    }

    async Task<bool> ClickTikTokStudioDeleteMenuItemAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const all = [...document.querySelectorAll('[role="menuitem"],li,button,[role="button"],div,span')]
    .filter(visible)
    .map(el => ({el, t:fold(el.innerText || el.textContent || ''), r:el.getBoundingClientRect()}))
    .filter(x => x.t === 'xoa' || x.t === 'delete')
    .filter(x => x.r.width < 450 && x.r.height < 110);
  if (!all.length) return false;
  all.sort((a,b) => b.r.left - a.r.left || b.r.top - a.r.top);
  try { all[0].el.click(); return true; } catch (_) { return false; }
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task<bool> ConfirmTikTokStudioDeleteDialogAsync(CancellationToken ct)
    {
        // TikTok Studio mở thêm một modal "Xóa bài đăng?" sau khi bấm mục Xóa trong menu 3 chấm.
        // Không dùng ancestor-size heuristic cũ vì modal mới có thể bọc nhiều lớp và nút xác nhận
        // không nằm trong ancestor thỏa kích thước đó. Tìm đúng dialog/title đang visible rồi bấm
        // nút Xóa/Delete nằm trong dialog; fallback cuối cùng chọn nút Xóa lớn ở vùng modal giữa màn hình.
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth
      && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const isDelete = el => {
    const t = fold(el?.innerText || el?.textContent || '');
    return t === 'xoa' || t === 'delete';
  };
  const isCancelText = t => t.includes('huy') || t.includes('cancel');
  const isDeleteDialogText = t =>
    t.includes('xoa bai dang') || t.includes('delete post') || t.includes('delete this post');
  const click = el => {
    if (!el) return false;
    try {
      el.scrollIntoView?.({block:'center', inline:'center'});
      el.click();
      return true;
    } catch (_) {
      try {
        const r = el.getBoundingClientRect();
        const x = r.left + r.width / 2, y = r.top + r.height / 2;
        for (const type of ['pointerdown','mousedown','pointerup','mouseup','click']) {
          el.dispatchEvent(new MouseEvent(type, {bubbles:true, cancelable:true, clientX:x, clientY:y, view:window}));
        }
        return true;
      } catch (_) { return false; }
    }
  };

  // 1) Ưu tiên semantic dialog/aria-modal.
  const dialogs = [...document.querySelectorAll('[role="dialog"],[aria-modal="true"]')]
    .filter(visible)
    .filter(el => {
      const t = fold(el.innerText || el.textContent || '');
      return isDeleteDialogText(t) || (isCancelText(t) && (t.includes('xoa') || t.includes('delete')));
    });
  for (const dialog of dialogs) {
    const candidates = [...dialog.querySelectorAll('button,[role="button"],[tabindex]')]
      .filter(visible).filter(isDelete)
      .map(el => ({el, r:el.getBoundingClientRect()}))
      .sort((a,b) => b.r.width - a.r.width || a.r.top - b.r.top);
    if (candidates.length && click(candidates[0].el)) return true;
  }

  // 2) TikTok có thể không gắn role=dialog. Tìm title "Xóa bài đăng?" rồi đi lên container modal.
  const title = [...document.querySelectorAll('h1,h2,h3,h4,[role="heading"],div,span')]
    .filter(visible)
    .find(el => isDeleteDialogText(fold(el.innerText || el.textContent || '')));
  if (title) {
    let node = title;
    for (let i = 0; i < 10 && node; i++, node = node.parentElement) {
      const r = node.getBoundingClientRect?.();
      if (!r || r.width < 260 || r.width > Math.min(900, innerWidth * 0.9) || r.height < 180 || r.height > innerHeight * 0.95) continue;
      const t = fold(node.innerText || node.textContent || '');
      if (!isCancelText(t) || !(t.includes('xoa') || t.includes('delete'))) continue;
      const candidates = [...node.querySelectorAll('button,[role="button"],[tabindex]')]
        .filter(visible).filter(isDelete)
        .map(el => ({el, r:el.getBoundingClientRect()}))
        .sort((a,b) => b.r.width - a.r.width || a.r.top - b.r.top);
      if (candidates.length && click(candidates[0].el)) return true;
    }
  }

  // 3) Fallback an toàn cho layout trong ảnh: nút xác nhận là nút Xóa lớn ở modal giữa viewport.
  // Menu-item Xóa ở cột Hành động nhỏ hơn nhiều và nằm lệch phải, nên không chọn nó.
  const centerX = innerWidth / 2;
  const candidates = [...document.querySelectorAll('button,[role="button"],[tabindex]')]
    .filter(visible).filter(isDelete)
    .map(el => ({el, r:el.getBoundingClientRect()}))
    .filter(x => x.r.width >= 180 && x.r.height >= 36
      && Math.abs((x.r.left + x.r.width / 2) - centerX) <= innerWidth * 0.28
      && x.r.top >= innerHeight * 0.18 && x.r.bottom <= innerHeight * 0.88)
    .sort((a,b) => b.r.width - a.r.width || a.r.top - b.r.top);
  return candidates.length ? click(candidates[0].el) : false;
})()
""";

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (ReadEvalBool(await EvalAsync(js, ct: ct)))
                    return true;
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                // Modal/page có thể đang re-render; thử lại trong cửa sổ ngắn thay vì fail ngay.
            }
            await Task.Delay(200, ct);
        }
        return false;
    }

    async Task<TikTokStudioDeleteFallbackResult> DeleteTikTokStudioOldPostsKeepingFirstAsync(CancellationToken ct)
    {
        var readyDeadline = DateTime.UtcNow.AddSeconds(30);
        TikTokStudioContentRowsSnapshot scan = new(0, "");
        while (DateTime.UtcNow < readyDeadline)
        {
            ct.ThrowIfCancellationRequested();
            scan = await ScanTikTokStudioContentRowsAsync(ct);
            if (scan.Count > 0) break;
            await Task.Delay(500, ct);
        }

        if (scan.Count <= 0)
            return new TikTokStudioDeleteFallbackResult(false, 0, -1, "TikTok Studio chưa tải được danh sách Bài đăng để chạy fallback xóa.");

        _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_READY] rows={scan.Count} keep=row1");
        if (scan.Count == 1)
            return new TikTokStudioDeleteFallbackResult(true, 0, 1, "");

        var deleted = 0;
        for (var round = 1; round <= 100; round++)
        {
            ct.ThrowIfCancellationRequested();
            scan = await ScanTikTokStudioContentRowsAsync(ct);
            if (scan.Count == 1)
            {
                _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_DONE] deleted={deleted} remaining=1");
                return new TikTokStudioDeleteFallbackResult(true, deleted, 1, "");
            }
            if (scan.Count <= 0)
            {
                // Danh sách có thể biến mất thoáng qua khi Studio reflow sau khi xóa.
                // Không được coi count=0 là thành công vì mục tiêu cuối phải còn đúng row 1.
                await Task.Delay(500, ct);
                continue;
            }

            var beforeCount = scan.Count;
            _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_ROW2_BEGIN] round={round} rows={beforeCount}");

            if (!await ClickTikTokStudioSecondRowMenuAsync(ct))
                return new TikTokStudioDeleteFallbackResult(false, deleted, beforeCount, "Không mở được nút 3 chấm của video cũ ở hàng thứ 2.");

            await Task.Delay(300, ct);
            if (!await ClickTikTokStudioDeleteMenuItemAsync(ct))
                return new TikTokStudioDeleteFallbackResult(false, deleted, beforeCount, "Không tìm/bấm được mục Xóa trong menu 3 chấm của video cũ.");

            await Task.Delay(350, ct);
            var confirmed = await ConfirmTikTokStudioDeleteDialogAsync(ct);
            if (!confirmed)
            {
                _log.Warn($"[VIDEO_DELETE_FALLBACK_STUDIO_CONFIRM_FAIL] round={round} rows={beforeCount}");
                return new TikTokStudioDeleteFallbackResult(false, deleted, beforeCount, "Đã mở hộp xác nhận nhưng không bấm được nút Xóa trong modal 'Xóa bài đăng?'.");
            }
            _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_CONFIRM_OK] round={round} rowsBefore={beforeCount}");

            // Sau khi xác nhận, TikTok Studio tự refresh/reflow danh sách. Một lần xóa chỉ được
            // tính thành công khi số row giảm đúng N -> N-1. Không dùng SecondKey đổi làm bằng chứng,
            // vì re-render có thể đổi DOM/hash dù bài cũ chưa thực sự bị xóa.
            var changed = false;
            var expectedCount = Math.Max(1, beforeCount - 1);
            var settleDeadline = DateTime.UtcNow.AddSeconds(15);
            TikTokStudioContentRowsSnapshot after = scan;
            while (DateTime.UtcNow < settleDeadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(450, ct);
                after = await ScanTikTokStudioContentRowsAsync(ct);
                if (after.Count == expectedCount)
                {
                    changed = true;
                    break;
                }
                // count=0 thường chỉ là khoảng trống lúc trang đang reload; tiếp tục chờ render lại.
            }

            if (!changed)
                return new TikTokStudioDeleteFallbackResult(false, deleted, after.Count,
                    $"Đã xác nhận Xóa nhưng số video chưa giảm đúng {beforeCount}->{expectedCount} sau 15 giây.");

            deleted++;
            _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_ROW2_OK] round={round} deleted={deleted} rowsBefore={beforeCount} rowsNow={after.Count}");
        }

        scan = await ScanTikTokStudioContentRowsAsync(ct);
        if (scan.Count == 1)
        {
            _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_DONE] deleted={deleted} remaining=1 boundary=max_rounds");
            return new TikTokStudioDeleteFallbackResult(true, deleted, 1, "");
        }
        return new TikTokStudioDeleteFallbackResult(false, deleted, scan.Count, "Fallback xóa đã chạm giới hạn an toàn 100 video.");
    }

    public async Task<TikTokVideoDeleteResult> DeleteTikTokStudioPostsAsync(
        string? deleteMode,
        Action<TikTokVideoDeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var mode = (deleteMode ?? "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "newest" or "none")) mode = "all";
        if (mode == "none")
            return new TikTokVideoDeleteResult(true, 0, 0, 0, false, "Không xóa video theo cấu hình.", "");

        var initialCount = 0;
        var deletedCount = 0;
        var remainingCount = -1;
        var verifiedEmpty = false;

        void Report(string stage, string message, bool running = true, bool completed = false, bool ok = false, string error = "")
        {
            try
            {
                progress?.Invoke(new TikTokVideoDeleteProgress(
                    running,
                    stage,
                    initialCount,
                    deletedCount,
                    remainingCount,
                    message,
                    completed,
                    ok,
                    verifiedEmpty,
                    error));
            }
            catch { }
        }

        try
        {
            if (!Connected)
                throw new InvalidOperationException("Chrome chưa kết nối.");

            Report("SESSION_CHECK", "Đang kiểm tra đăng nhập TikTok...");
            if (!await EnsureTikTokIdentitySessionReadyAsync(ct))
                return new TikTokVideoDeleteResult(
                    false, 0, 0, -1, false, "",
                    "LOGIN_REQUIRED: TikTok chưa đăng nhập; trả về logic đăng nhập hiện tại.");

            Report("OPEN_STUDIO", "Đang mở TikTok Studio để xóa video/bài cũ...");
            const string contentUrl = "https://www.tiktok.com/tiktokstudio/content";
            await NavigateAndWaitAsync(contentUrl, 900, 30000, ct);
            if (await IsTikTokLoginPromptVisibleAsync(ct))
                return new TikTokVideoDeleteResult(
                    false, 0, 0, -1, false, "",
                    "LOGIN_REQUIRED: TikTok mất đăng nhập khi mở TikTok Studio.");

            // Chờ Studio render. count=0 chỉ được coi là sạch khi có empty-state rõ.
            var readyDeadline = DateTime.UtcNow.AddSeconds(30);
            TikTokStudioContentRowsSnapshot scan = new(0, "");
            while (DateTime.UtcNow < readyDeadline)
            {
                ct.ThrowIfCancellationRequested();
                scan = await ScanTikTokStudioContentRowsAsync(ct);
                if (scan.Count > 0) break;
                if (await IsTikTokStudioContentConfirmedEmptyAsync(ct))
                {
                    initialCount = 0;
                    remainingCount = 0;
                    verifiedEmpty = true;
                    Report("COMPLETED", "TikTok Studio xác nhận không còn video/bài cũ.", false, true, true);
                    _log.Info("[VIDEO_DELETE_STUDIO_EMPTY] initial=0 remaining=0");
                    return new TikTokVideoDeleteResult(true, 0, 0, 0, true, "Không còn video/bài cũ.", "");
                }
                await Task.Delay(450, ct);
            }

            if (scan.Count <= 0)
                throw new TimeoutException("TikTok Studio chưa tải được danh sách Bài đăng và cũng chưa xác nhận trạng thái trống.");

            initialCount = scan.Count;
            remainingCount = initialCount;
            _log.Info($"[VIDEO_DELETE_STUDIO_READY] mode={mode} rows={initialCount} engine=FALLBACK_STUDIO_DIRECT");
            Report("SCANNED", $"TikTok Studio có {initialCount} video/bài cũ.");

            // Với XÓA TẤT CẢ: tái sử dụng đúng nhánh fallback đã ổn định trước đây
            // để giữ hàng 1 và xóa hàng 2 trở xuống. Sau khi chỉ còn hàng 1 mới xóa
            // nốt hàng 1. Như vậy toàn bộ Delete chạy trên Studio, không chạm profile.
            if (mode == "all" && initialCount > 1)
            {
                Report("DELETE_STUDIO_OLD", "Đang dùng nhánh Studio ổn định để xóa từ hàng 2 trở xuống...");
                var keepFirst = await DeleteTikTokStudioOldPostsKeepingFirstAsync(ct);
                deletedCount += keepFirst.DeletedCount;
                remainingCount = keepFirst.RemainingCount;
                if (!keepFirst.Ok || remainingCount != 1)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(keepFirst.Error)
                            ? $"Studio chưa thu gọn được danh sách về đúng 1 video (remaining={remainingCount})."
                            : keepFirst.Error);

                Report("DELETE_STUDIO_LAST", $"Đã xóa {deletedCount} bài cũ; đang xóa video/bài cuối cùng...");
            }
            else
            {
                Report("DELETE_STUDIO_ROW1", mode == "newest"
                    ? "Đang xóa video/bài mới nhất bằng TikTok Studio..."
                    : "Đang xóa video/bài cuối cùng bằng TikTok Studio...");
            }

            // newest: xóa hàng 1 đúng một lần.
            // all: sau nhánh keep-first phía trên, đây là hàng cuối cùng còn lại.
            scan = await ScanTikTokStudioContentRowsAsync(ct);
            if (scan.Count <= 0)
            {
                if (mode == "all" && await IsTikTokStudioContentConfirmedEmptyAsync(ct))
                {
                    remainingCount = 0;
                    verifiedEmpty = true;
                    var doneAlready = $"Đã xóa sạch {deletedCount} video/bài cũ bằng TikTok Studio.";
                    Report("COMPLETED", doneAlready, false, true, true);
                    return new TikTokVideoDeleteResult(true, initialCount, deletedCount, 0, true, doneAlready, "");
                }
                throw new InvalidOperationException("TikTok Studio mất danh sách bài trước bước xóa hàng 1.");
            }

            var beforeCount = scan.Count;
            if (!await ClickTikTokStudioFirstRowMenuAsync(ct))
                throw new InvalidOperationException("Không mở được nút 3 chấm của video/bài hàng 1 trên TikTok Studio.");

            await Task.Delay(250, ct);
            if (!await ClickTikTokStudioDeleteMenuItemAsync(ct))
                throw new InvalidOperationException("Không tìm/bấm được mục Xóa trong menu TikTok Studio.");

            await Task.Delay(300, ct);
            if (!await ConfirmTikTokStudioDeleteDialogAsync(ct))
                throw new InvalidOperationException("Không bấm được nút Xóa trong hộp xác nhận TikTok Studio.");

            var expectedCount = Math.Max(0, beforeCount - 1);
            var settleDeadline = DateTime.UtcNow.AddSeconds(15);
            var changed = false;
            var zeroStable = 0;
            while (DateTime.UtcNow < settleDeadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(350, ct);
                var after = await ScanTikTokStudioContentRowsAsync(ct);
                if (expectedCount == 0)
                {
                    if (after.Count == 0)
                    {
                        zeroStable++;
                        if (zeroStable >= 2 || await IsTikTokStudioContentConfirmedEmptyAsync(ct))
                        {
                            changed = true;
                            break;
                        }
                    }
                    else
                    {
                        zeroStable = 0;
                    }
                }
                else if (after.Count == expectedCount)
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
                throw new InvalidOperationException(
                    $"Đã xác nhận Xóa nhưng số video chưa giảm đúng {beforeCount}->{expectedCount} sau 15 giây.");

            deletedCount++;
            remainingCount = expectedCount;
            verifiedEmpty = remainingCount == 0;
            _log.Info($"[VIDEO_DELETE_STUDIO_ROW1_OK] mode={mode} deleted={deletedCount} remaining={remainingCount}");

            var message = mode == "all"
                ? $"Đã xóa sạch {deletedCount} video/bài cũ bằng TikTok Studio."
                : "Đã xóa video/bài mới nhất bằng TikTok Studio.";
            Report("COMPLETED", message, false, true, true);
            return new TikTokVideoDeleteResult(
                true,
                initialCount,
                deletedCount,
                remainingCount,
                verifiedEmpty,
                message,
                "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_DELETE_STUDIO_FAILED] mode={mode} initial={initialCount} deleted={deletedCount} remaining={remainingCount} error={ex.Message}");
            Report("ERROR", ex.Message, false, true, false, ex.Message);
            return new TikTokVideoDeleteResult(false, initialCount, deletedCount, remainingCount, verifiedEmpty, "", ex.Message);
        }
    }

    public async Task<TikTokVideoUploadResult> CleanupTikTokStudioOldPostsKeepingFirstAsync(
        Action<TikTokVideoUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var attempted = false;
        var succeeded = false;
        var deleted = 0;
        var remaining = -1;
        var fallbackError = "";

        void Report(string stage, string message, bool running = true, bool completed = false, bool ok = false, string error = "")
        {
            try
            {
                progress?.Invoke(new TikTokVideoUploadProgress(
                    running, stage, "", "", message, completed, ok,
                    false, false, false, error)
                {
                    DeleteFallbackAttempted = attempted,
                    DeleteFallbackSucceeded = succeeded,
                    DeleteFallbackDeletedCount = deleted,
                    DeleteFallbackRemainingCount = remaining,
                    DeleteFallbackError = fallbackError
                });
            }
            catch { }
        }

        try
        {
            if (!Connected)
                throw new InvalidOperationException("Chrome chưa kết nối.");

            attempted = true;
            Report("DELETE_FALLBACK_OPEN_STUDIO", "Đang mở TikTok Studio để giữ hàng 1 và xóa video cũ từ hàng 2...");

            const string contentUrl = "https://www.tiktok.com/tiktokstudio/content";
            await NavigateAndWaitAsync(contentUrl, 900, 30000, ct);
            if (await IsTikTokLoginPromptVisibleAsync(ct))
                throw new InvalidOperationException("TikTok mất đăng nhập trước bước fallback Studio.");

            Report("DELETE_FALLBACK", "Đang giữ video hàng 1 và xóa các video cũ từ hàng 2...");
            var fallback = await DeleteTikTokStudioOldPostsKeepingFirstAsync(ct);
            succeeded = fallback.Ok;
            deleted = fallback.DeletedCount;
            remaining = fallback.RemainingCount;
            fallbackError = fallback.Error;

            if (fallback.Ok)
            {
                _log.Info($"[VIDEO_DELETE_FALLBACK_STUDIO_RESULT] ok=true mode=cleanup_only deleted={deleted} remaining={remaining}");
                Report("DELETE_FALLBACK_DONE", $"Fallback Studio hoàn tất: đã xóa {deleted} video cũ, còn {remaining} video.", false, true, true);
                return new TikTokVideoUploadResult(
                    true, false, false, false, "", "",
                    "Fallback Studio đã giữ hàng 1 và xóa video cũ.", "")
                {
                    DeleteFallbackAttempted = true,
                    DeleteFallbackSucceeded = true,
                    DeleteFallbackDeletedCount = deleted,
                    DeleteFallbackRemainingCount = remaining,
                    DeleteFallbackError = ""
                };
            }

            _log.Warn($"[VIDEO_DELETE_FALLBACK_STUDIO_RESULT] ok=false mode=cleanup_only deleted={deleted} remaining={remaining} error={fallbackError}");
            Report("DELETE_FALLBACK_WARN", "Fallback Studio chưa xóa hết video cũ.", false, true, false, fallbackError);
            return new TikTokVideoUploadResult(
                false, false, false, false, "", "",
                "Fallback Studio chưa hoàn tất.", fallbackError)
            {
                DeleteFallbackAttempted = true,
                DeleteFallbackSucceeded = false,
                DeleteFallbackDeletedCount = deleted,
                DeleteFallbackRemainingCount = remaining,
                DeleteFallbackError = fallbackError
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fallbackError = ex.Message;
            _log.Warn($"[VIDEO_DELETE_FALLBACK_STUDIO_EXCEPTION] mode=cleanup_only error={ex.Message}");
            Report("DELETE_FALLBACK_ERROR", "Fallback Studio gặp lỗi.", false, true, false, ex.Message);
            return new TikTokVideoUploadResult(
                false, false, false, false, "", "",
                "Fallback Studio gặp lỗi.", ex.Message)
            {
                DeleteFallbackAttempted = attempted,
                DeleteFallbackSucceeded = false,
                DeleteFallbackDeletedCount = deleted,
                DeleteFallbackRemainingCount = remaining,
                DeleteFallbackError = fallbackError
            };
        }
    }

    async Task CleanupTikTokStudioUploadPageAsync(string profileHref, long beforeAcceptedCount)
    {
        if (!Connected) return;

        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cleanupCt = cleanupCts.Token;
        try
        {
            var current = ReadEvalString(await EvalAsync("location.href", ct: cleanupCt));
            if (!current.Contains("/tiktokstudio/upload", StringComparison.OrdinalIgnoreCase))
                return;

            var target = IsSafeTikTokRecoveryUrl(profileHref) ? profileHref : TikTokUrl;
            _log.Info($"[VIDEO_UPLOAD_CLEANUP_LEAVE_STUDIO] current={current} target={target}");

            // AutoAcceptBeforeUnload đã được arm trước đó. Nếu trang có form chưa lưu,
            // Page.javascriptDialogOpening(type=beforeunload) sẽ được CdpClient tự accept.
            await NavigateAndWaitAsync(target, 500, 15000, cleanupCt);

            var acceptedAfter = Cdp.BeforeUnloadAutoAcceptedCount;
            if (acceptedAfter > beforeAcceptedCount)
            {
                _log.Info(
                    $"[VIDEO_BEFOREUNLOAD_ACCEPTED] count={acceptedAfter - beforeAcceptedCount} " +
                    $"source=video_upload_cleanup");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_UPLOAD_CLEANUP_LEAVE_WARN] error={ex.Message}");
        }
    }

    public async Task<TikTokVideoUploadResult> UploadTikTokVideoAsync(
        string? username,
        string videoPath,
        string? caption,
        bool studioDeleteFallback = false,
        Action<TikTokVideoUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        videoPath = (videoPath ?? "").Trim();
        caption ??= "";
        var posted = false;
        var privacyUpdated = false;
        var profileVerified = false;
        var postedHref = "";
        var recoveryHref = TikTokUrl;
        var deleteFallbackAttempted = false;
        var deleteFallbackSucceeded = false;
        var deleteFallbackDeletedCount = 0;
        var deleteFallbackRemainingCount = -1;
        var deleteFallbackError = "";
        var beforeUnloadGuardArmed = false;
        long beforeUnloadAcceptedAtArm = 0;

        void Report(string stage, string message, bool running = true, bool completed = false, bool ok = false, string error = "")
        {
            try
            {
                progress?.Invoke(new TikTokVideoUploadProgress(
                    running, stage, videoPath, postedHref, message, completed, ok,
                    posted, privacyUpdated, profileVerified, error)
                {
                    DeleteFallbackAttempted = deleteFallbackAttempted,
                    DeleteFallbackSucceeded = deleteFallbackSucceeded,
                    DeleteFallbackDeletedCount = deleteFallbackDeletedCount,
                    DeleteFallbackRemainingCount = deleteFallbackRemainingCount,
                    DeleteFallbackError = deleteFallbackError
                });
            }
            catch { }
        }

        try
        {
            if (!Connected) throw new InvalidOperationException("Chrome chưa kết nối.");
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                throw new FileNotFoundException("Không tìm thấy file video cần đăng.", videoPath);

            // Delete hiện được xử lý RIÊNG trước Upload bằng TikTok Studio. Tham số
            // fallback cũ chỉ được giữ để tương thích IPC/source cũ; Upload không chạy
            // thêm bất kỳ bước xóa nào sau khi bài đã đăng.
            if (studioDeleteFallback)
            {
                _log.Warn("[VIDEO_UPLOAD_DELETE_FALLBACK_IGNORED] reason=delete_is_separate_preupload_stage");
            }

            Report("OPEN_UPLOAD", "Đang mở thẳng TikTok Studio Upload...");
            const string uploadUrl = "https://www.tiktok.com/tiktokstudio/upload?from=webapp&tab=video";
            await NavigateAndWaitAsync(uploadUrl, 900, 30000, ct);
            if (await IsTikTokLoginPromptVisibleAsync(ct))
                throw new InvalidOperationException("TikTok đã mất đăng nhập; bỏ qua phần ĐĂNG để logic đăng nhập hiện tại của tool xử lý.");
            if (!await WaitForTikTokStudioUploadShellAsync(ct))
                throw new TimeoutException("Không tải được giao diện TikTok Studio Upload.");

            // Từ lúc đã vào Studio Upload, rời trang có thể phát sinh dialog native
            // beforeunload. Guard này chỉ phục vụ cleanup nếu thao tác lỗi/dừng.
            Cdp.AutoAcceptBeforeUnload = true;
            beforeUnloadGuardArmed = true;
            beforeUnloadAcceptedAtArm = Cdp.BeforeUnloadAutoAcceptedCount;
            _log.Info("[VIDEO_BEFOREUNLOAD_GUARD_ON] scope=studio_upload");

            Report("SET_FILE", $"Đang gán trực tiếp file {Path.GetFileName(videoPath)}...");
            await SetTikTokUploadFileAsync(videoPath, ct);
            await Task.Delay(700, ct);

            Report("UPLOADING", "Đang chờ TikTok tải/xử lý video...");
            await WaitTikTokStudioUploadReadyAsync(videoPath, progress, ct);

            for (var i = 0; i < 3; i++)
            {
                var action = await HandleTikTokStudioUploadPopupAsync(ct);
                if (string.IsNullOrWhiteSpace(action)) break;
                _log.Info($"[VIDEO_UPLOAD_POPUP_AFTER_READY] action={action}");
                await Task.Delay(300, ct);
            }
            await EnsureTikTokStudioChecksOffAsync(ct);

            // Caption vẫn là best-effort như policy cũ: lỗi/không verify caption
            // không được chặn nút Đăng.
            Report("CAPTION", string.IsNullOrWhiteSpace(caption) ? "Đang xóa caption mặc định..." : "Đang thay caption...");
            if (await SetTikTokStudioCaptionAsync(caption, ct))
                _log.Info($"[VIDEO_UPLOAD_CAPTION_OK] length={caption.Length}");
            else
                _log.Warn("[VIDEO_UPLOAD_CAPTION_WARN] Không xác nhận được caption mới; vẫn tiếp tục đăng theo fail-open.");
            await Task.Delay(220, ct);

            if (!await IsTikTokStudioPostButtonEnabledAsync(ct))
                throw new InvalidOperationException("Nút Đăng chưa sẵn sàng sau khi video đã xử lý.");

            Report("POSTING", "Đang bấm Đăng...");
            if (!await ClickTikTokStudioPostAsync(ct))
                throw new InvalidOperationException("Không tìm/bấm được nút Đăng trên TikTok Studio.");

            // Mốc DUY NHẤT để xác nhận UPLOAD DONE: toast "Đã đăng video" xuất hiện
            // sau lần click Đăng vừa rồi. Không đổi privacy, không poll Mọi người,
            // không quay profile verify nữa.
            Report("WAIT_POST_TOAST", "Đang chờ TikTok xác nhận 'Đã đăng video'...");
            var accepted = await WaitTikTokPostSubmissionAcceptedAsync(ct);
            if (!accepted)
                throw new TimeoutException("Đã bấm Đăng nhưng không thấy thông báo 'Đã đăng video' trong 30 giây.");

            posted = true;
            var message = "Đăng video thành công — TikTok đã hiện thông báo 'Đã đăng video'.";
            _log.Info("[VIDEO_UPLOAD_DONE_BY_TOAST] posted=true privacy=not_checked profile=not_checked");
            Report("COMPLETED", message, running: false, completed: true, ok: true);

            return new TikTokVideoUploadResult(
                true,
                true,
                false,
                false,
                videoPath,
                "",
                message,
                "")
            {
                DeleteFallbackAttempted = false,
                DeleteFallbackSucceeded = false,
                DeleteFallbackDeletedCount = 0,
                DeleteFallbackRemainingCount = -1,
                DeleteFallbackError = ""
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_UPLOAD_FAILED] posted={posted} toastConfirmed={posted} error={ex.Message}");
            Report("ERROR", ex.Message, running: false, completed: true, ok: false, error: ex.Message);
            return new TikTokVideoUploadResult(false, posted, false, false, videoPath, "", "", ex.Message)
            {
                DeleteFallbackAttempted = false,
                DeleteFallbackSucceeded = false,
                DeleteFallbackDeletedCount = 0,
                DeleteFallbackRemainingCount = -1,
                DeleteFallbackError = ""
            };
        }
        finally
        {
            if (beforeUnloadGuardArmed)
            {
                try
                {
                    await CleanupTikTokStudioUploadPageAsync(recoveryHref, beforeUnloadAcceptedAtArm);
                }
                catch { }

                try
                {
                    Cdp.AutoAcceptBeforeUnload = false;
                    _log.Info("[VIDEO_BEFOREUNLOAD_GUARD_OFF] scope=studio_upload");
                }
                catch { }
            }
        }
    }

}
