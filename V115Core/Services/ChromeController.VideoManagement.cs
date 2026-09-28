using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolTikTokV11.Services;

public sealed record TikTokVideoDeleteResult(
    bool Ok,
    int InitialCount,
    int DeletedCount,
    int RemainingCount,
    bool VerifiedEmpty,
    string Message,
    string Error);

public sealed record TikTokVideoDeleteProgress(
    bool Running,
    string Stage,
    int InitialCount,
    int DeletedCount,
    int RemainingCount,
    string Message,
    bool Completed,
    bool Ok,
    bool VerifiedEmpty,
    string Error);

public sealed partial class ChromeController
{
    // Nhớ kiểu menu đã mở thành công gần nhất của PRF này để các bài kế tiếp không phải chờ dò lâu.
    // Quy tắc ưu tiên luôn cố định: RIGHT > LEFT > SINGLE. Hint chỉ giúp tăng tốc, không được đảo ưu tiên.
    string _videoDeleteMenuUiHint = "UNKNOWN";

    sealed class TikTokVideoSkipException : Exception
    {
        public string Reason { get; }
        public TikTokVideoSkipException(string reason, string message) : base(message)
            => Reason = reason;
    }

    sealed record TikTokProfilePostScan(IReadOnlyList<string> Hrefs, bool EmptyMarkerVisible, string ProfileHref)
    {
        public int Count => Hrefs.Count;
    }

    static string NormalizeTikTokVideoHandle(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.StartsWith("https://www.tiktok.com/@", StringComparison.OrdinalIgnoreCase))
        {
            var marker = raw.IndexOf("/@", StringComparison.OrdinalIgnoreCase);
            raw = marker >= 0 ? raw[(marker + 2)..] : raw;
            var cut = raw.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) raw = raw[..cut];
        }

        raw = raw.Trim().TrimStart('@');
        if (raw.Length == 0 || raw.Length > 64) return "";
        if (raw.Contains('@') || raw.Any(char.IsWhiteSpace)) return "";
        if (!raw.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_')) return "";
        return raw;
    }

    static string StripTikTokUrlSuffix(string value)
    {
        value ??= "";
        var cut = value.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? value[..cut] : value;
    }

    static bool ReadEvalBool(JsonElement result)
        => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;

    static string ReadEvalString(JsonElement result)
        => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    async Task<bool> WaitVideoBoolAsync(
        string js,
        int timeoutMs,
        int delayMs,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(300, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (ReadEvalBool(await EvalAsync(js, ct: ct))) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

            await Task.Delay(Math.Max(80, delayMs), ct);
        }
        return false;
    }

    async Task<string> ReadCurrentLocationHrefAsync(CancellationToken ct)
    {
        try
        {
            return ReadEvalString(await EvalAsync("location.href", ct: ct));
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex))
        {
            await Task.Delay(250, ct);
            return ReadEvalString(await EvalAsync("location.href", ct: ct));
        }
    }

    async Task<string> FindOwnTikTokProfileHrefFromDomAsync(CancellationToken ct)
    {
        var js = """
(() => {
  const clean = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const candidates = [
    document.querySelector('a[data-e2e="nav-profile"]'),
    document.querySelector('[data-e2e="nav-profile"] a'),
    ...document.querySelectorAll('a[href*="/@"]')
  ].filter(Boolean).filter(visible);
  for (const a of candidates) {
    const href = a.href || a.getAttribute?.('href') || '';
    const text = clean(`${a.innerText || a.textContent || ''} ${a.getAttribute?.('aria-label') || ''} ${a.getAttribute?.('data-e2e') || ''}`);
    if (!href || !href.includes('/@')) continue;
    if (a.matches?.('[data-e2e="nav-profile"]')
        || a.closest?.('[data-e2e="nav-profile"]')
        || text === 'profile'
        || text === 'hồ sơ'
        || text.includes('nav-profile')) return href;
  }
  return '';
})()
""";
        return ReadEvalString(await EvalAsync(js, ct: ct));
    }

    async Task<bool> IsOwnTikTokProfilePageAsync(int timeoutMs, CancellationToken ct)
    {
        const string js = """
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  if (!location.pathname.startsWith('/@')) return false;
  const direct = document.querySelector('[data-e2e="edit-profile-entrance"],button[data-e2e*="edit-profile"],[role="button"][data-e2e*="edit-profile"]');
  if (visible(direct)) return true;
  return [...document.querySelectorAll('button,[role="button"],a')].filter(visible).some(el => {
    const t = norm(`${el.innerText || el.textContent || ''} ${el.getAttribute('aria-label') || ''}`);
    return t === 'edit profile' || t === 'chỉnh sửa hồ sơ' || t === 'sửa hồ sơ';
  });
})()
""";
        return await WaitVideoBoolAsync(js, timeoutMs, 250, ct);
    }

    async Task<bool> IsTikTokLoginPromptVisibleAsync(CancellationToken ct)
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

  const direct = document.querySelector('[data-e2e="top-login-button"],[data-e2e="login-button"],button[data-e2e*="login"]');
  if (visible(direct)) return true;

  return [...document.querySelectorAll('button,[role="button"],a')].filter(visible).some(el => {
    const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
    return t === 'dang nhap' || t === 'log in' || t === 'login';
  });
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
        catch { return false; }
    }

    async Task<string> NavigateToOwnTikTokProfileForVideoAsync(string? username, CancellationToken ct)
    {
        var handle = NormalizeTikTokVideoHandle(username);
        string profileHref = "";

        if (!string.IsNullOrWhiteSpace(handle))
        {
            profileHref = "https://www.tiktok.com/@" + Uri.EscapeDataString(handle);
            try
            {
                _log.Info($"[VIDEO_DELETE_PROFILE_DIRECT] username={handle} href={profileHref}");
                await NavigateAndWaitAsync(profileHref, 1000, 30000, ct);
                if (await IsOwnTikTokProfilePageAsync(5000, ct))
                {
                    var actual = await ReadCurrentLocationHrefAsync(ct);
                    return string.IsNullOrWhiteSpace(actual) ? profileHref : StripTikTokUrlSuffix(actual);
                }
                if (await IsTikTokLoginPromptVisibleAsync(ct))
                {
                    _log.Warn($"[VIDEO_DELETE_SKIP_LOGIN] stage=direct-profile href={profileHref}");
                    throw new TikTokVideoSkipException("LOGIN_REQUIRED", "TikTok đã mất đăng nhập; bỏ qua phần VIDEO để logic đăng nhập hiện tại của tool xử lý.");
                }
                _log.Warn($"[VIDEO_DELETE_PROFILE_DIRECT_NOT_OWN] href={profileHref}; fallback=sidebar-profile");
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_DELETE_PROFILE_DIRECT_FAILED] href={profileHref} error={ex.Message}");
            }
        }

        var domHref = await FindOwnTikTokProfileHrefFromDomAsync(ct);
        if (string.IsNullOrWhiteSpace(domHref))
        {
            if (await IsTikTokLoginPromptVisibleAsync(ct))
            {
                _log.Warn("[VIDEO_DELETE_SKIP_LOGIN] stage=sidebar-profile href=missing");
                throw new TikTokVideoSkipException("LOGIN_REQUIRED", "TikTok đã mất đăng nhập; bỏ qua phần VIDEO để logic đăng nhập hiện tại của tool xử lý.");
            }
            throw new InvalidOperationException("Không xác định được trang Hồ sơ TikTok của chính tài khoản. Tool dừng để tránh xóa nhầm tài khoản.");
        }

        _log.Info($"[VIDEO_DELETE_PROFILE_DOM_FALLBACK] href={domHref}");
        await NavigateAndWaitAsync(domHref, 1000, 30000, ct);
        if (!await IsOwnTikTokProfilePageAsync(6000, ct))
        {
            if (await IsTikTokLoginPromptVisibleAsync(ct))
            {
                _log.Warn($"[VIDEO_DELETE_SKIP_LOGIN] stage=sidebar-profile-verify href={domHref}");
                throw new TikTokVideoSkipException("LOGIN_REQUIRED", "TikTok đã mất đăng nhập; bỏ qua phần VIDEO để logic đăng nhập hiện tại của tool xử lý.");
            }
            throw new InvalidOperationException("Đã vào trang Hồ sơ nhưng không thấy nút Sửa hồ sơ/Edit profile. Tool dừng để tránh xóa nhầm tài khoản.");
        }

        var finalHref = await ReadCurrentLocationHrefAsync(ct);
        if (string.IsNullOrWhiteSpace(finalHref)) finalHref = domHref;
        var uri = new Uri(finalHref);
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
    }

    async Task TrySelectTikTokNewestSortAsync(CancellationToken ct)
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
  const nodes = [...document.querySelectorAll('button,[role="button"],div,span')].filter(visible);
  const hit = nodes.find(el => {
    const t = fold(el.innerText || el.textContent || '');
    return t === 'moi nhat' || t === 'latest' || t === 'newest';
  });
  if (!hit) return false;
  const click = hit.closest('button,[role="button"]') || hit;
  click.click();
  return true;
})()
""";
        try
        {
            if (ReadEvalBool(await EvalAsync(js, ct: ct)))
            {
                _log.Info("[VIDEO_DELETE_SORT] selected=newest");
                await Task.Delay(500, ct);
            }
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
        catch (Exception ex) { _log.Warn("[VIDEO_DELETE_SORT_WARN] " + ex.Message); }
    }

    async Task<TikTokProfilePostScan> ScanTikTokProfilePostsAsync(
        string profileHref,
        int maxScrollRounds,
        CancellationToken ct)
    {
        var hrefs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stableRounds = 0;
        var lastCount = -1;
        var emptyMarker = false;

        for (var round = 0; round < Math.Max(1, maxScrollRounds); round++)
        {
            ct.ThrowIfCancellationRequested();
            string json;
            try
            {
                json = ReadEvalString(await EvalAsync("""
(() => {
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const prefix = (location.pathname.match(/^\/@[^/]+/) || [''])[0];
  const links = [...document.querySelectorAll('a[href*="/video/"],a[href*="/photo/"]')]
    .map(a => a.href || a.getAttribute('href') || '')
    .filter(Boolean)
    .filter(raw => {
      try {
        const u = new URL(raw, location.origin);
        if (!u.hostname.endsWith('tiktok.com')) return false;
        if (!prefix || !u.pathname.toLowerCase().startsWith(prefix.toLowerCase() + '/')) return false;
        return u.pathname.includes('/video/') || u.pathname.includes('/photo/');
      } catch { return false; }
    })
    .map(raw => {
      try { const u = new URL(raw, location.origin); return u.origin + u.pathname; }
      catch { return raw.split(/[?#]/)[0]; }
    });
  const unique = [...new Set(links)];
  const text = fold(document.body?.innerText || '');
  const empty = text.includes('tai video dau tien cua ban len')
    || text.includes('video cua ban se xuat hien tai day')
    || text.includes('upload your first video')
    || text.includes('your videos will appear here');
  return JSON.stringify({ hrefs: unique, empty, path: location.pathname, scrollHeight: document.documentElement.scrollHeight || document.body?.scrollHeight || 0 });
})()
""", ct: ct));
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                await Task.Delay(350, ct);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                if (root.TryGetProperty("hrefs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var href = item.GetString() ?? "";
                        if (href.Length > 0 && seen.Add(href)) hrefs.Add(href);
                    }
                }
                emptyMarker |= root.TryGetProperty("empty", out var empty) && empty.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_DELETE_SCAN_PARSE_WARN] round={round + 1} error={ex.Message}");
            }

            if (hrefs.Count == lastCount) stableRounds++; else stableRounds = 0;
            lastCount = hrefs.Count;

            if (emptyMarker && hrefs.Count == 0) break;
            if (stableRounds >= 3) break;

            try
            {
                await EvalAsync("window.scrollTo(0, Math.max(document.body?.scrollHeight || 0, document.documentElement?.scrollHeight || 0)); true", ct: ct);
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
            await Task.Delay(500, ct);
        }

        _log.Info($"[VIDEO_DELETE_SCAN] count={hrefs.Count} emptyMarker={emptyMarker} profile={profileHref}");
        return new TikTokProfilePostScan(hrefs, emptyMarker, profileHref);
    }

    async Task<bool> OpenTikTokPostAsync(string postHref, CancellationToken ct)
    {
        var clickJs = $$"""
(() => {
  const target = {{JsString(postHref)}};
  const clean = raw => {
    try { const u = new URL(raw, location.origin); return u.origin + u.pathname; }
    catch { return String(raw || '').split(/[?#]/)[0]; }
  };
  const wanted = clean(target);
  const links = [...document.querySelectorAll('a[href*="/video/"],a[href*="/photo/"]')];
  const hit = links.find(a => clean(a.href || a.getAttribute('href') || '') === wanted);
  if (!hit) return false;
  hit.scrollIntoView({ block: 'center', inline: 'center' });
  hit.click();
  return true;
})()
""";

        var clicked = false;
        try { clicked = ReadEvalBool(await EvalAsync(clickJs, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

        if (clicked)
        {
            var opened = await WaitVideoBoolAsync("""
(() => location.pathname.includes('/video/') || location.pathname.includes('/photo/'))()
""", 15000, 250, ct);
            if (opened) return true;
        }

        _log.Warn($"[VIDEO_DELETE_OPEN_POST_FALLBACK_NAVIGATE] href={postHref}");
        await NavigateAndWaitAsync(postHref, 900, 30000, ct);
        return await WaitVideoBoolAsync("""
(() => location.pathname.includes('/video/') || location.pathname.includes('/photo/'))()
""", 10000, 250, ct);
    }

    async Task<bool> IsTikTokOwnerDeleteMenuVisibleAsync(CancellationToken ct)
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

  const all = [...document.querySelectorAll('button,[role="button"],[role="menuitem"],li,div,span,p')].filter(visible);
  let hasDelete = false;
  let hasPrivacy = false;
  for (const el of all) {
    const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''} ${el.getAttribute?.('title') || ''}`);
    if (t === 'xoa' || t === 'delete') hasDelete = true;
    if (t === 'cai dat quyen rieng tu' || t === 'privacy settings' || t === 'privacy setting') hasPrivacy = true;
    if (hasDelete && hasPrivacy) return true;
  }

  // Một số bản TikTok chỉ render mục Xóa rõ ràng mà không có label privacy.
  // Chấp nhận nếu Xóa nằm trong popup/menu nổi ở nửa phải màn hình.
  const deleteLeaves = all.filter(el => {
    const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
    return t === 'xoa' || t === 'delete';
  });
  return deleteLeaves.some(el => {
    let n = el;
    for (let i = 0; i < 7 && n; i++, n = n.parentElement) {
      if (!visible(n)) continue;
      const r = n.getBoundingClientRect();
      const txt = fold(n.innerText || n.textContent || '');
      const menuLike = n.matches?.('[role="menu"],[role="listbox"],[class*="Menu"],[class*="menu"],[class*="Popup"],[class*="popup"],[class*="Popover"],[class*="popover"]');
      if ((menuLike || txt.includes('xoa') || txt.includes('delete')) && r.left > innerWidth * 0.55 && r.top < innerHeight * 0.45)
        return true;
    }
    return false;
  });
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task<string> ReadTikTokVisibleMenuSummaryAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden' && Number(cs.opacity || 1) > 0.05;
  };
  const clean = s => (s || '').replace(/\s+/g, ' ').trim();
  const out = [];
  const nodes = [...document.querySelectorAll('[role="menu"],[role="menuitem"],[class*="Menu"],[class*="menu"],[class*="Popup"],[class*="popup"],[class*="Popover"],[class*="popover"],button,[role="button"]')]
    .filter(visible);
  for (const el of nodes) {
    const t = clean(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
    if (!t || t.length > 240) continue;
    const r = el.getBoundingClientRect();
    if (r.top > innerHeight * 0.60) continue;
    if (t.includes('Xóa') || /delete/i.test(t) || t.includes('quyền riêng tư') || /privacy/i.test(t) || t.includes('Báo cáo') || /report/i.test(t))
      out.push(t);
    if (out.length >= 12) break;
  }
  return JSON.stringify([...new Set(out)]);
})()
""";
        try { return ReadEvalString(await EvalAsync(js, ct: ct)); }
        catch { return ""; }
    }

    sealed record CdpDomMenuTarget(
        int NodeId,
        int BackendNodeId,
        double X,
        double Y,
        double RawX,
        double RawY,
        double Width,
        double Height,
        string Source);
    sealed record CdpViewportMetrics(double Width, double Height, double PageX, double PageY);
    sealed record CdpResolvedRect(
        double Left, double Top, double Width, double Height,
        double ViewportWidth, double ViewportHeight, double Dpr,
        double VisualOffsetX, double VisualOffsetY, double VisualScale);
    sealed record CdpFlatDomNode(int NodeId, int BackendNodeId, int ParentId, string NodeName, string NodeValue, IReadOnlyDictionary<string, string> Attributes);
    sealed record CdpFlatMenuProbe(CdpDomMenuTarget? Right, CdpDomMenuTarget? Left, int NodeCount, int ShadowRootCount, int FrameCount, int RightCandidateCount, int LeftCandidateCount);

    static IReadOnlyDictionary<string, string> ReadCdpFlatAttributes(JsonElement node)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!node.TryGetProperty("attributes", out var raw) || raw.ValueKind != JsonValueKind.Array)
            return attrs;

        var parts = raw.EnumerateArray().ToArray();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var name = parts[i].ValueKind == JsonValueKind.String ? parts[i].GetString() ?? "" : "";
            var value = parts[i + 1].ValueKind == JsonValueKind.String ? parts[i + 1].GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(name)) attrs[name] = value;
        }
        return attrs;
    }

    static string ReadCdpFlatAttribute(CdpFlatDomNode node, string name)
        => node.Attributes.TryGetValue(name, out var value) ? value ?? "" : "";

    async Task<CdpViewportMetrics> ReadCdpViewportMetricsAsync(CancellationToken ct)
    {
        try
        {
            var metrics = await Cdp.CallAsync("Page.getLayoutMetrics", ct: ct);
            if (metrics.TryGetProperty("cssVisualViewport", out var viewport))
            {
                var width = viewport.TryGetProperty("clientWidth", out var w) && w.TryGetDouble(out var wd) ? wd : 0;
                var height = viewport.TryGetProperty("clientHeight", out var h) && h.TryGetDouble(out var hd) ? hd : 0;
                var pageX = viewport.TryGetProperty("pageX", out var px) && px.TryGetDouble(out var pxd) ? pxd : 0;
                var pageY = viewport.TryGetProperty("pageY", out var py) && py.TryGetDouble(out var pyd) ? pyd : 0;
                if (width > 10 && height > 10) return new CdpViewportMetrics(width, height, pageX, pageY);
            }
            if (metrics.TryGetProperty("visualViewport", out viewport))
            {
                var width = viewport.TryGetProperty("clientWidth", out var w) && w.TryGetDouble(out var wd) ? wd : 0;
                var height = viewport.TryGetProperty("clientHeight", out var h) && h.TryGetDouble(out var hd) ? hd : 0;
                var pageX = viewport.TryGetProperty("pageX", out var px) && px.TryGetDouble(out var pxd) ? pxd : 0;
                var pageY = viewport.TryGetProperty("pageY", out var py) && py.TryGetDouble(out var pyd) ? pyd : 0;
                if (width > 10 && height > 10) return new CdpViewportMetrics(width, height, pageX, pageY);
            }
        }
        catch { }

        try
        {
            var raw = ReadEvalString(await EvalAsync("JSON.stringify({w:innerWidth||0,h:innerHeight||0,x:scrollX||0,y:scrollY||0})", ct: ct));
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var width = root.TryGetProperty("w", out var w) && w.TryGetDouble(out var wd) ? wd : 0;
            var height = root.TryGetProperty("h", out var h) && h.TryGetDouble(out var hd) ? hd : 0;
            var pageX = root.TryGetProperty("x", out var px) && px.TryGetDouble(out var pxd) ? pxd : 0;
            var pageY = root.TryGetProperty("y", out var py) && py.TryGetDouble(out var pyd) ? pyd : 0;
            return new CdpViewportMetrics(Math.Max(1, width), Math.Max(1, height), pageX, pageY);
        }
        catch
        {
            return new CdpViewportMetrics(1920, 1080, 0, 0);
        }
    }

    static bool TryReadCdpQuad(JsonElement model, string name, out double[] quad)
    {
        quad = Array.Empty<double>();
        if (!model.TryGetProperty(name, out var q) || q.ValueKind != JsonValueKind.Array) return false;
        var values = new List<double>(8);
        foreach (var v in q.EnumerateArray())
        {
            if (v.TryGetDouble(out var d)) values.Add(d);
        }
        if (values.Count < 8) return false;
        quad = values.Take(8).ToArray();
        return true;
    }

    async Task<CdpResolvedRect?> ReadResolvedNodeRectAsync(
        int nodeId,
        int backendNodeId,
        CancellationToken ct)
    {
        JsonElement resolved;
        try
        {
            if (nodeId > 0)
                resolved = await Cdp.CallAsync("DOM.resolveNode", new { nodeId }, ct);
            else if (backendNodeId > 0)
                resolved = await Cdp.CallAsync("DOM.resolveNode", new { backendNodeId }, ct);
            else
                return null;
        }
        catch
        {
            if (backendNodeId <= 0) return null;
            try { resolved = await Cdp.CallAsync("DOM.resolveNode", new { backendNodeId }, ct); }
            catch { return null; }
        }

        if (!resolved.TryGetProperty("object", out var remote)
            || !remote.TryGetProperty("objectId", out var objectIdEl)
            || objectIdEl.ValueKind != JsonValueKind.String)
            return null;

        var objectId = objectIdEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(objectId)) return null;

        try
        {
            var call = await Cdp.CallAsync("Runtime.callFunctionOn", new
            {
                objectId,
                functionDeclaration = """
function() {
  if (!this || typeof this.getBoundingClientRect !== 'function') return null;
  const r = this.getBoundingClientRect();
  const cs = getComputedStyle(this);
  const vv = window.visualViewport;
  return {
    left: r.left,
    top: r.top,
    width: r.width,
    height: r.height,
    right: r.right,
    bottom: r.bottom,
    viewportWidth: window.innerWidth || 0,
    viewportHeight: window.innerHeight || 0,
    dpr: window.devicePixelRatio || 1,
    visualOffsetX: vv ? (vv.offsetLeft || 0) : 0,
    visualOffsetY: vv ? (vv.offsetTop || 0) : 0,
    visualScale: vv ? (vv.scale || 1) : 1,
    display: cs.display || '',
    visibility: cs.visibility || '',
    opacity: Number(cs.opacity || 1)
  };
}
""",
                returnByValue = true,
                awaitPromise = false,
                userGesture = true
            }, ct);

            if (call.TryGetProperty("exceptionDetails", out _)) return null;
            if (!call.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object)
                return null;

            static double Num(JsonElement obj, string name, double fallback = 0)
                => obj.TryGetProperty(name, out var e) && e.TryGetDouble(out var d) ? d : fallback;
            static string Str(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

            var left = Num(value, "left");
            var top = Num(value, "top");
            var width = Num(value, "width");
            var height = Num(value, "height");
            var viewportWidth = Num(value, "viewportWidth");
            var viewportHeight = Num(value, "viewportHeight");
            var dpr = Num(value, "dpr", 1);
            var vvX = Num(value, "visualOffsetX");
            var vvY = Num(value, "visualOffsetY");
            var vvScale = Num(value, "visualScale", 1);
            var display = Str(value, "display");
            var visibility = Str(value, "visibility");
            var opacity = Num(value, "opacity", 1);

            if (width <= 2 || height <= 2) return null;
            if (display.Equals("none", StringComparison.OrdinalIgnoreCase)
                || visibility.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                || opacity <= 0.05)
                return null;

            if (viewportWidth <= 10 || viewportHeight <= 10)
            {
                var fallback = await ReadCdpViewportMetricsAsync(ct);
                viewportWidth = fallback.Width;
                viewportHeight = fallback.Height;
            }

            var right = left + width;
            var bottom = top + height;
            if (right <= 0 || bottom <= 0 || left >= viewportWidth || top >= viewportHeight)
                return null;

            return new CdpResolvedRect(
                left, top, width, height,
                viewportWidth, viewportHeight, dpr,
                vvX, vvY, vvScale);
        }
        finally
        {
            try { await Cdp.CallAsync("Runtime.releaseObject", new { objectId }, ct); } catch { }
        }
    }

    async Task<CdpDomMenuTarget?> TryBuildCdpFlatTargetAsync(
        CdpFlatDomNode node,
        string source,
        bool requireRightHalf,
        CdpViewportMetrics viewport,
        CancellationToken ct)
    {
        // Quan trọng: Input.dispatchMouseEvent nhận tọa độ CSS theo viewport.
        // Vì vậy lấy trực tiếp getBoundingClientRect() trên đúng remote DOM node,
        // không dùng DOM.getBoxModel rồi tự trừ pageX/pageY nữa.
        var rect = await ReadResolvedNodeRectAsync(node.NodeId, node.BackendNodeId, ct);
        if (rect is null) return null;

        var x = rect.Left + rect.Width / 2.0;
        var y = rect.Top + rect.Height / 2.0;
        if (requireRightHalf && x <= rect.ViewportWidth * 0.50) return null;

        return new CdpDomMenuTarget(
            node.NodeId,
            node.BackendNodeId,
            x,
            y,
            x,
            y,
            rect.Width,
            rect.Height,
            source);
    }

    async Task<CdpDomMenuTarget?> RefreshCdpMenuTargetRectAsync(
        CdpDomMenuTarget target,
        bool requireRightHalf,
        string role,
        CancellationToken ct)
    {
        var rect = await ReadResolvedNodeRectAsync(target.NodeId, target.BackendNodeId, ct);
        if (rect is null)
        {
            _log.Warn($"[VIDEO_RECT_MISSING] role={role} source={target.Source} nodeId={target.NodeId} backendNodeId={target.BackendNodeId}");
            return null;
        }

        var x = rect.Left + rect.Width / 2.0;
        var y = rect.Top + rect.Height / 2.0;
        if (requireRightHalf && x <= rect.ViewportWidth * 0.50)
        {
            _log.Warn($"[VIDEO_RECT_WRONG_HALF] role={role} x={x:F1} viewportW={rect.ViewportWidth:F1}");
            return null;
        }

        _log.Info(
            $"[VIDEO_RECT] role={role} source={target.Source} " +
            $"left={rect.Left:F1} top={rect.Top:F1} width={rect.Width:F1} height={rect.Height:F1} " +
            $"center=({x:F1},{y:F1}) viewport={rect.ViewportWidth:F0}x{rect.ViewportHeight:F0} " +
            $"dpr={rect.Dpr:F2} vvOffset=({rect.VisualOffsetX:F1},{rect.VisualOffsetY:F1}) vvScale={rect.VisualScale:F2}");

        return target with
        {
            X = x,
            Y = y,
            RawX = x,
            RawY = y,
            Width = rect.Width,
            Height = rect.Height
        };
    }

    async Task<List<CdpDomMenuTarget>> BuildCdpFlatTargetsAsync(
        IEnumerable<CdpFlatDomNode> nodes,
        string source,
        bool requireRightHalf,
        CdpViewportMetrics viewport,
        CancellationToken ct)
    {
        var targets = new List<CdpDomMenuTarget>();
        foreach (var node in nodes.Take(32))
        {
            ct.ThrowIfCancellationRequested();
            var target = await TryBuildCdpFlatTargetAsync(node, source, requireRightHalf, viewport, ct);
            if (target is not null) targets.Add(target);
        }
        return targets;
    }

    static IEnumerable<int> EnumerateAncestorIds(int nodeId, IReadOnlyDictionary<int, CdpFlatDomNode> byId, int maxDepth = 12)
    {
        var current = nodeId;
        for (var i = 0; i < maxDepth && current > 0; i++)
        {
            yield return current;
            if (!byId.TryGetValue(current, out var node) || node.ParentId <= 0) yield break;
            current = node.ParentId;
        }
    }

    static bool IsNodeRelated(int hitNodeId, int targetNodeId, IReadOnlyDictionary<int, CdpFlatDomNode> byId)
    {
        if (hitNodeId <= 0 || targetNodeId <= 0) return false;
        if (hitNodeId == targetNodeId) return true;
        if (EnumerateAncestorIds(hitNodeId, byId).Contains(targetNodeId)) return true;   // hit là con của target
        if (EnumerateAncestorIds(targetNodeId, byId).Contains(hitNodeId)) return true; // hit là cha gần của target
        return false;
    }

    async Task<(double X, double Y, int HitNodeId, int HitBackendNodeId, string Mode)?> FindVerifiedCdpHitPointAsync(
        CdpDomMenuTarget target,
        string role,
        CancellationToken ct)
    {
        try { await Cdp.CallAsync("DOM.enable", ct: ct); } catch { }

        // ScrollIntoView chỉ tác động nội bộ tab, không đụng chuột Windows.
        try
        {
            if (target.NodeId > 0)
                await Cdp.CallAsync("DOM.scrollIntoViewIfNeeded", new { nodeId = target.NodeId }, ct);
            else if (target.BackendNodeId > 0)
                await Cdp.CallAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId = target.BackendNodeId }, ct);
        }
        catch { }

        var viewport = await ReadCdpViewportMetricsAsync(ct);
        var flat = await Cdp.CallAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, ct);
        var byId = new Dictionary<int, CdpFlatDomNode>();
        if (flat.TryGetProperty("nodes", out var rawNodes) && rawNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var raw in rawNodes.EnumerateArray())
            {
                var nodeId = raw.TryGetProperty("nodeId", out var ni) && ni.TryGetInt32(out var nid) ? nid : 0;
                if (nodeId <= 0) continue;
                var backendNodeId = raw.TryGetProperty("backendNodeId", out var bi) && bi.TryGetInt32(out var bid) ? bid : 0;
                var parentId = raw.TryGetProperty("parentId", out var pi) && pi.TryGetInt32(out var pid) ? pid : 0;
                var nodeName = raw.TryGetProperty("nodeName", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() ?? "" : "";
                var nodeValue = raw.TryGetProperty("nodeValue", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
                byId[nodeId] = new CdpFlatDomNode(nodeId, backendNodeId, parentId, nodeName, nodeValue, ReadCdpFlatAttributes(raw));
            }
        }

        var dx = Math.Min(Math.Max(2, target.Width * 0.18), 8);
        var dy = Math.Min(Math.Max(2, target.Height * 0.18), 8);
        var seeds = new List<(double X, double Y, string Mode)>
        {
            (target.X, target.Y, "adjusted-center"),
            (target.RawX, target.RawY, "raw-center"),
            (target.X - dx, target.Y, "adjusted-left"),
            (target.X + dx, target.Y, "adjusted-right"),
            (target.X, target.Y - dy, "adjusted-up"),
            (target.X, target.Y + dy, "adjusted-down"),
            (target.RawX - dx, target.RawY, "raw-left"),
            (target.RawX + dx, target.RawY, "raw-right"),
            (target.RawX, target.RawY - dy, "raw-up"),
            (target.RawX, target.RawY + dy, "raw-down")
        };

        var dedup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var x = (int)Math.Round(seed.X);
            var y = (int)Math.Round(seed.Y);
            var key = $"{x}|{y}";
            if (!dedup.Add(key)) continue;
            if (x < 0 || y < 0 || x >= viewport.Width || y >= viewport.Height) continue;

            try
            {
                var hit = await Cdp.CallAsync("DOM.getNodeForLocation", new
                {
                    x,
                    y,
                    includeUserAgentShadowDOM = true,
                    ignorePointerEventsNone = false
                }, ct);
                var hitNodeId = hit.TryGetProperty("nodeId", out var n) && n.TryGetInt32(out var nid) ? nid : 0;
                var hitBackend = hit.TryGetProperty("backendNodeId", out var b) && b.TryGetInt32(out var bid) ? bid : 0;
                var related = IsNodeRelated(hitNodeId, target.NodeId, byId)
                    || (target.BackendNodeId > 0 && hitBackend == target.BackendNodeId);

                _log.Info($"[VIDEO_HITTEST] role={role} mode={seed.Mode} x={x:F0} y={y:F0} targetNode={target.NodeId} targetBackend={target.BackendNodeId} hitNode={hitNodeId} hitBackend={hitBackend} related={related}");
                if (related)
                    return (x, y, hitNodeId, hitBackend, seed.Mode);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_HITTEST_ERROR] role={role} mode={seed.Mode} x={x:F0} y={y:F0} error={ex.Message}");
            }
        }

        _log.Warn($"[VIDEO_HITTEST_FAILED] role={role} source={target.Source} adjusted=({target.X:F0},{target.Y:F0}) raw=({target.RawX:F0},{target.RawY:F0}) size={target.Width:F0}x{target.Height:F0}");
        return null;
    }

    async Task<CdpDomMenuTarget?> TryPromoteRightTargetToInteractiveAncestorAsync(
        CdpDomMenuTarget svgTarget,
        IReadOnlyDictionary<int, CdpFlatDomNode> byId,
        CdpViewportMetrics viewport,
        CancellationToken ct)
    {
        if (!byId.TryGetValue(svgTarget.NodeId, out var current)) return svgTarget;

        var parentId = current.ParentId;
        for (var depth = 0; depth < 5 && parentId > 0; depth++)
        {
            if (!byId.TryGetValue(parentId, out var parent)) break;
            var name = parent.NodeName.ToUpperInvariant();
            if (name is "DIV" or "BUTTON" or "SPAN")
            {
                var candidate = await TryBuildCdpFlatTargetAsync(parent, svgTarget.Source + "_PARENT", true, viewport, ct);
                if (candidate is not null
                    && candidate.Width >= 16 && candidate.Height >= 16
                    && candidate.Width <= 120 && candidate.Height <= 120
                    && Math.Abs(candidate.X - svgTarget.X) <= 60
                    && Math.Abs(candidate.Y - svgTarget.Y) <= 60)
                {
                    return candidate;
                }
            }
            parentId = parent.ParentId;
        }
        return svgTarget;
    }

    async Task<CdpFlatMenuProbe> ProbeCdpFlattenedMenuTargetsAsync(CancellationToken ct)
    {
        try { await Cdp.CallAsync("DOM.enable", ct: ct); } catch { }

        var flat = await Cdp.CallAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, ct);
        if (!flat.TryGetProperty("nodes", out var rawNodes) || rawNodes.ValueKind != JsonValueKind.Array)
            return new CdpFlatMenuProbe(null, null, 0, 0, 0, 0, 0);

        var nodes = new List<CdpFlatDomNode>();
        var shadowCount = 0;
        var frameCount = 0;

        foreach (var raw in rawNodes.EnumerateArray())
        {
            var nodeId = raw.TryGetProperty("nodeId", out var ni) && ni.TryGetInt32(out var nid) ? nid : 0;
            var backendNodeId = raw.TryGetProperty("backendNodeId", out var bi) && bi.TryGetInt32(out var bid) ? bid : 0;
            var parentId = raw.TryGetProperty("parentId", out var pi) && pi.TryGetInt32(out var pid) ? pid : 0;
            var nodeName = raw.TryGetProperty("nodeName", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() ?? "" : "";
            var nodeValue = raw.TryGetProperty("nodeValue", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
            if (raw.TryGetProperty("shadowRootType", out _)) shadowCount++;
            if (nodeName.Equals("IFRAME", StringComparison.OrdinalIgnoreCase)
                || nodeName.Equals("FRAME", StringComparison.OrdinalIgnoreCase)) frameCount++;
            nodes.Add(new CdpFlatDomNode(nodeId, backendNodeId, parentId, nodeName, nodeValue, ReadCdpFlatAttributes(raw)));
        }

        var viewport = await ReadCdpViewportMetricsAsync(ct);

        var leftNodes = nodes.Where(n =>
            ReadCdpFlatAttribute(n, "data-e2e").Equals("browse-ellipsis", StringComparison.OrdinalIgnoreCase)).ToList();

        var rightClassNodes = nodes.Where(n =>
            n.NodeName.Equals("SVG", StringComparison.OrdinalIgnoreCase)
            && ReadCdpFlatAttribute(n, "class").Contains("StyledEllipsisHorizontal", StringComparison.OrdinalIgnoreCase)).ToList();

        // Fallback nếu TikTok đổi tên class: nhận đúng SVG ba chấm 48x48 có path bắt đầu M4 24,
        // rồi chọn instance visible phải nhất. data-e2e=browse-ellipsis vẫn được tách riêng cho nút trái.
        var pathParents = nodes.Where(n =>
                n.NodeName.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                && ReadCdpFlatAttribute(n, "d").Contains("M4 24", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.ParentId)
            .Where(id => id > 0)
            .ToHashSet();
        var rightShapeNodes = nodes.Where(n =>
            n.NodeName.Equals("SVG", StringComparison.OrdinalIgnoreCase)
            && ReadCdpFlatAttribute(n, "viewBox").Replace(",", " ", StringComparison.Ordinal).Trim().Equals("0 0 48 48", StringComparison.OrdinalIgnoreCase)
            && pathParents.Contains(n.NodeId)).ToList();

        var byId = nodes.Where(n => n.NodeId > 0).ToDictionary(n => n.NodeId, n => n);

        var rightTargets = await BuildCdpFlatTargetsAsync(rightClassNodes, "RIGHT_FLAT_CLASS", true, viewport, ct);
        if (rightTargets.Count == 0)
            rightTargets = await BuildCdpFlatTargetsAsync(rightShapeNodes, "RIGHT_FLAT_SHAPE", true, viewport, ct);
        var leftTargets = await BuildCdpFlatTargetsAsync(leftNodes, "LEFT_FLAT_DATA_E2E", false, viewport, ct);

        var promotedRightTargets = new List<CdpDomMenuTarget>();
        foreach (var target in rightTargets)
            promotedRightTargets.Add(await TryPromoteRightTargetToInteractiveAncestorAsync(target, byId, viewport, ct) ?? target);

        var right = promotedRightTargets
            .OrderByDescending(x => x.X)
            .ThenBy(x => x.Y)
            .FirstOrDefault();
        var left = leftTargets
            .OrderByDescending(x => x.X)
            .ThenBy(x => x.Y)
            .FirstOrDefault();

        return new CdpFlatMenuProbe(
            right,
            left,
            nodes.Count,
            shadowCount,
            frameCount,
            rightClassNodes.Count + rightShapeNodes.Count,
            leftNodes.Count);
    }

    async Task<IReadOnlyList<CdpDomMenuTarget>> ProbeCdpSingleEllipsisCandidatesAsync(CancellationToken ct)
    {
        // Fallback cho layout TikTok chỉ có MỘT dấu ... trên vùng bài đang mở.
        // Không click mù: chỉ thu các candidate hình ellipsis ở nửa trên màn hình;
        // sau mỗi click bắt buộc menu owner phải thực sự có "Xóa/Delete" mới chấp nhận.
        try { await Cdp.CallAsync("DOM.enable", ct: ct); } catch { }

        var flat = await Cdp.CallAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, ct);
        if (!flat.TryGetProperty("nodes", out var rawNodes) || rawNodes.ValueKind != JsonValueKind.Array)
            return Array.Empty<CdpDomMenuTarget>();

        var nodes = new List<CdpFlatDomNode>();
        foreach (var raw in rawNodes.EnumerateArray())
        {
            var nodeId = raw.TryGetProperty("nodeId", out var ni) && ni.TryGetInt32(out var nid) ? nid : 0;
            var backendNodeId = raw.TryGetProperty("backendNodeId", out var bi) && bi.TryGetInt32(out var bid) ? bid : 0;
            var parentId = raw.TryGetProperty("parentId", out var pi) && pi.TryGetInt32(out var pid) ? pid : 0;
            var nodeName = raw.TryGetProperty("nodeName", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() ?? "" : "";
            var nodeValue = raw.TryGetProperty("nodeValue", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
            nodes.Add(new CdpFlatDomNode(nodeId, backendNodeId, parentId, nodeName, nodeValue, ReadCdpFlatAttributes(raw)));
        }

        var byId = nodes.Where(n => n.NodeId > 0).ToDictionary(n => n.NodeId, n => n);
        var viewport = await ReadCdpViewportMetricsAsync(ct);

        bool AttrLooksLikeEllipsis(CdpFlatDomNode n)
        {
            var cls = ReadCdpFlatAttribute(n, "class");
            var e2e = ReadCdpFlatAttribute(n, "data-e2e");
            var aria = ReadCdpFlatAttribute(n, "aria-label");
            var title = ReadCdpFlatAttribute(n, "title");
            var id = ReadCdpFlatAttribute(n, "id");
            var all = $"{cls} {e2e} {aria} {title} {id}".ToLowerInvariant();
            return all.Contains("ellipsis") || all.Contains("more-option") || all.Contains("more_action")
                || all.Contains("more-action") || all.Contains("more menu") || all.Contains("more-menu");
        }

        // Các SVG chứa path hình 3 chấm. Không bắt buộc viewBox/class vì layout single-ellipsis
        // có thể dùng component khác với hai layout đã biết.
        var pathParentIds = nodes.Where(n =>
                n.NodeName.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                && (ReadCdpFlatAttribute(n, "d").Contains("M4 24", StringComparison.OrdinalIgnoreCase)
                    || ReadCdpFlatAttribute(n, "d").Contains("M4 24C4", StringComparison.OrdinalIgnoreCase)
                    || ReadCdpFlatAttribute(n, "d").Contains("M4 24a", StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.ParentId)
            .Where(id => id > 0)
            .ToHashSet();

        var rawCandidates = nodes.Where(n =>
                (n.NodeName.Equals("SVG", StringComparison.OrdinalIgnoreCase) && pathParentIds.Contains(n.NodeId))
                || AttrLooksLikeEllipsis(n))
            .Where(n => !ReadCdpFlatAttribute(n, "data-e2e").Equals("browse-ellipsis", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(n => n.NodeId)
            .ToList();

        var built = await BuildCdpFlatTargetsAsync(rawCandidates, "SINGLE_ELLIPSIS", false, viewport, ct);
        var filtered = built
            .Where(t => t.Width >= 12 && t.Height >= 12 && t.Width <= 110 && t.Height <= 110)
            .Where(t => t.X >= viewport.Width * 0.42 && t.Y <= viewport.Height * 0.42)
            .OrderBy(t => t.Y)
            .ThenByDescending(t => t.X)
            .Take(6)
            .ToList();

        _log.Info($"[VIDEO_SINGLE_ELLIPSIS_SCAN] raw={rawCandidates.Count} usable={filtered.Count} viewport={viewport.Width:F0}x{viewport.Height:F0}");
        return filtered;
    }

    async Task<bool> TryOpenTikTokSingleEllipsisDeleteMenuAsync(CancellationToken ct)
    {
        IReadOnlyList<CdpDomMenuTarget> candidates;
        try { candidates = await ProbeCdpSingleEllipsisCandidatesAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_SINGLE_ELLIPSIS_SCAN_ERROR] {ex.Message}");
            return false;
        }

        var attempt = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                _log.Info($"[VIDEO_SINGLE_ELLIPSIS_TRY] attempt={attempt}/{candidates.Count} source={candidate.Source} nodeId={candidate.NodeId} backendNodeId={candidate.BackendNodeId} x={candidate.X:F0} y={candidate.Y:F0} size={candidate.Width:F0}x{candidate.Height:F0}");
                if (!await SendCdpVirtualClickAsync(candidate, "SINGLE_ELLIPSIS", ct))
                    continue;

                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await IsTikTokOwnerDeleteMenuVisibleAsync(ct))
                    {
                        _log.Info($"[VIDEO_SINGLE_ELLIPSIS_VISIBLE] attempt={attempt} menu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
                        return true;
                    }
                    await Task.Delay(200, ct);
                }

                _log.Warn($"[VIDEO_SINGLE_ELLIPSIS_WRONG] attempt={attempt} visibleMenu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_SINGLE_ELLIPSIS_ERROR] attempt={attempt} error={ex.Message}");
            }
        }

        return false;
    }

    async Task LogVideoCdpTargetStateAsync(CancellationToken ct)
    {
        string runtimeUrl = "";
        CdpPage? metadata = Page;
        try { runtimeUrl = await ReadCurrentLocationHrefAsync(ct); } catch { }
        try { metadata = await RefreshAttachedPageMetadataAsync() ?? metadata; } catch { }

        var cdpUrl = metadata?.Url ?? "";
        var targetId = metadata?.Id ?? Page?.Id ?? "";
        var runtimeDetail = runtimeUrl.Contains("/video/", StringComparison.OrdinalIgnoreCase)
            || runtimeUrl.Contains("/photo/", StringComparison.OrdinalIgnoreCase);
        var cdpDetail = cdpUrl.Contains("/video/", StringComparison.OrdinalIgnoreCase)
            || cdpUrl.Contains("/photo/", StringComparison.OrdinalIgnoreCase);
        _log.Info($"[VIDEO_TARGET] targetId={targetId} connected={Connected} runtimeDetail={runtimeDetail} cdpDetail={cdpDetail} runtimeUrl={StripTikTokUrlSuffix(runtimeUrl)} cdpUrl={StripTikTokUrlSuffix(cdpUrl)}");
    }

    async Task<bool> SendCdpVirtualHoverAsync(CdpDomMenuTarget target, CancellationToken ct)
    {
        // Không dùng chuột Windows. Mỗi lần hover đều đo lại boundingClientRect ngay trước khi gửi event,
        // nên thay đổi kích thước Chrome/viewport giữa các lần chạy không làm giữ tọa độ cũ.
        var fresh = await RefreshCdpMenuTargetRectAsync(target, true, "RIGHT", ct);
        if (fresh is null) return false;

        var viewport = await ReadCdpViewportMetricsAsync(ct);
        var x = fresh.X;
        var y = fresh.Y;
        if (x < 0 || y < 0 || x >= viewport.Width || y >= viewport.Height)
        {
            _log.Warn($"[VIDEO_HOVER_OUT_OF_VIEWPORT] x={x:F1} y={y:F1} viewport={viewport.Width:F0}x{viewport.Height:F0}");
            return false;
        }

        // Đi từ ngoài mép trái của vùng hover vào tâm để React nhận mouseenter/mouseover thật từ CDP.
        var left = x - fresh.Width / 2.0;
        var fromX = Math.Max(1, left - Math.Min(10, Math.Max(4, fresh.Width * 0.25)));
        var fromY = y;

        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x = fromX,
            y = fromY,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(100, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x,
            y,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(180, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x = Math.Min(x + 1, Math.Max(1, viewport.Width - 1)),
            y,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);

        _log.Info($"[VIDEO_HOVER_RECT_SENT] x={x:F1} y={y:F1} source={fresh.Source}");
        return true;
    }

    async Task<bool> SendCdpVirtualClickAsync(CdpDomMenuTarget target, string role, CancellationToken ct)
    {
        var fresh = await RefreshCdpMenuTargetRectAsync(target, false, role, ct);
        if (fresh is null) return false;

        var viewport = await ReadCdpViewportMetricsAsync(ct);
        var x = fresh.X;
        var y = fresh.Y;
        if (x < 0 || y < 0 || x >= viewport.Width || y >= viewport.Height)
        {
            _log.Warn($"[VIDEO_CLICK_OUT_OF_VIEWPORT] role={role} x={x:F1} y={y:F1} viewport={viewport.Width:F0}x{viewport.Height:F0}");
            return false;
        }

        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x,
            y,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(110, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            buttons = 1,
            clickCount = 1,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(100, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            buttons = 0,
            clickCount = 1,
            pointerType = "mouse"
        }, ct);

        _log.Info($"[VIDEO_CLICK_RECT_SENT] role={role} x={x:F1} y={y:F1} source={fresh.Source}");
        return true;
    }

    async Task<bool> TryDismissTikTokVideoBlockingPopupAsync(string stage, CancellationToken ct)
    {
        // Popup Guard chỉ xử lý những popup TikTok đã biết chắc là che thao tác VIDEO.
        // Hiện tại: popup gợi ý tạo Passkey. Tuyệt đối không bấm "Tạo passkey";
        // chỉ bấm các lựa chọn trì hoãn như "Để sau / Not now / Later".
        const string probeAndClickJs = """
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

  const all = [...document.querySelectorAll('[role="dialog"],[aria-modal="true"],div')].filter(visible);
  const roots = all
    .map(el => {
      const text = fold(el.innerText || el.textContent || '');
      if (!text.includes('passkey')) return null;
      if (!(text.includes('tao passkey') || text.includes('create passkey') || text.includes('set up passkey'))) return null;
      const r = el.getBoundingClientRect();
      // Ưu tiên container modal nhỏ nhất chứa đầy đủ nội dung popup, tránh chọn body/root lớn.
      return {el, r, area:r.width*r.height, text};
    })
    .filter(Boolean)
    .sort((a,b) => a.area - b.area);

  if (!roots.length) return JSON.stringify({found:false});
  const root = roots[0].el;
  const rootRect = roots[0].r;

  const dismissWords = new Set([
    'de sau', 'not now', 'later', 'maybe later', 'skip', 'bo qua', 'cancel'
  ]);
  const candidates = [...root.querySelectorAll('button,[role="button"],[tabindex]')]
    .filter(visible)
    .map(el => {
      const text = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
      const r = el.getBoundingClientRect();
      return {el,text,r};
    })
    .filter(x => dismissWords.has(x.text))
    .sort((a,b) => (b.r.width*b.r.height) - (a.r.width*a.r.height));

  if (!candidates.length) {
    return JSON.stringify({
      found:true, kind:'PASSKEY', dismissFound:false,
      root:{left:rootRect.left,top:rootRect.top,width:rootRect.width,height:rootRect.height}
    });
  }

  const hit = candidates[0];
  const r = hit.r;
  let method = '';
  let clickError = '';
  try {
    hit.el.focus?.({preventScroll:true});
    if (typeof hit.el.click === 'function') {
      hit.el.click();
      method = 'HTMLElement.click';
    }
  } catch (e) { clickError = String(e?.message || e); }

  return JSON.stringify({
    found:true, kind:'PASSKEY', dismissFound:true, clicked:!!method, method, clickError,
    text:hit.text,
    rect:{left:r.left,top:r.top,width:r.width,height:r.height}
  });
})()
""";

        const string visibleJs = """
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
  return [...document.querySelectorAll('[role="dialog"],[aria-modal="true"],div')]
    .filter(visible)
    .some(el => {
      const t = fold(el.innerText || el.textContent || '');
      return t.includes('passkey') && (t.includes('tao passkey') || t.includes('create passkey') || t.includes('set up passkey'));
    });
})()
""";

        try
        {
            string raw = "";
            try { raw = ReadEvalString(await EvalAsync(probeAndClickJs, ct: ct)); }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
            if (string.IsNullOrWhiteSpace(raw)) return false;

            bool found = false;
            bool dismissFound = false;
            bool clicked = false;
            double left = 0, top = 0, width = 0, height = 0;
            string text = "", method = "";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                found = root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
                if (!found) return false;
                dismissFound = root.TryGetProperty("dismissFound", out var df) && df.ValueKind == JsonValueKind.True;
                clicked = root.TryGetProperty("clicked", out var cl) && cl.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) text = tx.GetString() ?? "";
                if (root.TryGetProperty("method", out var mt) && mt.ValueKind == JsonValueKind.String) method = mt.GetString() ?? "";
                if (root.TryGetProperty("rect", out var rr) && rr.ValueKind == JsonValueKind.Object)
                {
                    if (rr.TryGetProperty("left", out var v) && v.TryGetDouble(out var d)) left = d;
                    if (rr.TryGetProperty("top", out v) && v.TryGetDouble(out d)) top = d;
                    if (rr.TryGetProperty("width", out v) && v.TryGetDouble(out d)) width = d;
                    if (rr.TryGetProperty("height", out v) && v.TryGetDouble(out d)) height = d;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_POPUP_GUARD_PARSE_WARN] stage={stage} error={ex.Message} raw={raw}");
                return false;
            }

            _log.Info($"[VIDEO_POPUP_GUARD_FOUND] stage={stage} kind=PASSKEY dismissFound={dismissFound} clicked={clicked} method={method} text={text}");
            if (!dismissFound)
            {
                _log.Warn($"[VIDEO_POPUP_GUARD_NO_DISMISS] stage={stage} kind=PASSKEY");
                return true;
            }

            // DOM click thường đủ. Nếu popup vẫn còn, fallback bằng CDP trusted click ngay tại
            // nút "Để sau". Đây vẫn là chuột ảo trong tab Chrome, không đụng con trỏ Windows.
            var goneDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < goneDeadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!ReadEvalBool(await EvalAsync(visibleJs, ct: ct)))
                    {
                        _log.Info($"[VIDEO_POPUP_GUARD_DISMISSED] stage={stage} kind=PASSKEY method={method}");
                        return true;
                    }
                }
                catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
                await Task.Delay(180, ct);
            }

            if (width > 2 && height > 2)
            {
                var (vw, vh) = await GetViewportSizeAsync(ct);
                var x = Math.Clamp(left + width * 0.50, 1, Math.Max(1, vw - 2));
                var y = Math.Clamp(top + height * 0.50, 1, Math.Max(1, vh - 2));
                _log.Info($"[VIDEO_POPUP_GUARD_CDP_CLICK] stage={stage} kind=PASSKEY x={x:F1} y={y:F1}");
                await Cdp.CallAsync("Input.dispatchMouseEvent", new
                {
                    type = "mousePressed",
                    x,
                    y,
                    button = "left",
                    buttons = 1,
                    clickCount = 1,
                    pointerType = "mouse"
                }, ct);
                await Task.Delay(100, ct);
                await Cdp.CallAsync("Input.dispatchMouseEvent", new
                {
                    type = "mouseReleased",
                    x,
                    y,
                    button = "left",
                    buttons = 0,
                    clickCount = 1,
                    pointerType = "mouse"
                }, ct);
            }

            var verifyDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < verifyDeadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!ReadEvalBool(await EvalAsync(visibleJs, ct: ct)))
                    {
                        _log.Info($"[VIDEO_POPUP_GUARD_DISMISSED] stage={stage} kind=PASSKEY method=CDP_CLICK");
                        return true;
                    }
                }
                catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
                await Task.Delay(250, ct);
            }

            _log.Warn($"[VIDEO_POPUP_GUARD_STILL_VISIBLE] stage={stage} kind=PASSKEY");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Popup guard là best-effort: lỗi guard không được phép làm treo hoặc chặn luồng xóa.
            _log.Warn($"[VIDEO_POPUP_GUARD_ERROR] stage={stage} error={ex.Message}");
            return false;
        }
    }

    async Task<bool> ClickTikTokPostMoreMenuAsync(CancellationToken ct)
    {
        // Popup TikTok (đặc biệt Passkey) có thể xuất hiện sau khi chuyển bài và che toàn bộ vùng ...
        // Guard chỉ đóng popup đã biết; nếu guard lỗi thì luồng VIDEO vẫn tiếp tục best-effort.
        await TryDismissTikTokVideoBlockingPopupAsync("before-more-menu", ct);
        await LogVideoCdpTargetStateAsync(ct);

        _log.Info($"[VIDEO_MENU_FAST_SCAN_START] hint={_videoDeleteMenuUiHint} priority=RIGHT>LEFT>SINGLE");

        async Task<bool> TryRightAsync(CdpDomMenuTarget right)
        {
            try
            {
                _log.Info($"[VIDEO_FLAT_DOM_RIGHT] found=1 source={right.Source} nodeId={right.NodeId} backendNodeId={right.BackendNodeId} x={right.X:F0} y={right.Y:F0} rawX={right.RawX:F0} rawY={right.RawY:F0}");
                if (!await SendCdpVirtualHoverAsync(right, ct))
                {
                    _log.Warn("[VIDEO_MENU_RIGHT_RECT_FAILED] Không lấy được tọa độ viewport hiện tại của dấu ba chấm bên phải.");
                    return false;
                }

                _log.Info("[VIDEO_MENU_RIGHT_CDP_HOVER_SENT]");
                var hoverDeadline = DateTime.UtcNow.AddSeconds(18);
                var rehoverAt = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < hoverDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await TryDismissTikTokVideoBlockingPopupAsync("right-hover-wait", ct);
                    if (await IsTikTokOwnerDeleteMenuVisibleAsync(ct))
                    {
                        _videoDeleteMenuUiHint = "RIGHT";
                        _log.Info("[VIDEO_MENU_RIGHT_VISIBLE] method=RECT_CDP_HOVER hint=RIGHT");
                        return true;
                    }

                    if (DateTime.UtcNow >= rehoverAt)
                    {
                        if (!await SendCdpVirtualHoverAsync(right, ct))
                        {
                            _log.Warn("[VIDEO_MENU_RIGHT_REHOVER_RECT_FAILED]");
                            break;
                        }
                        rehoverAt = DateTime.UtcNow.AddSeconds(4);
                    }
                    await Task.Delay(300, ct);
                }

                _log.Warn($"[VIDEO_MENU_RIGHT_NO_MENU] method=RECT_CDP_HOVER visibleMenu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_MENU_RIGHT_ERROR] method=RECT_CDP_HOVER error={ex.Message}");
            }
            return false;
        }

        async Task<bool> TryLeftAsync(CdpDomMenuTarget left)
        {
            try
            {
                _log.Info($"[VIDEO_FLAT_DOM_LEFT] found=1 source={left.Source} nodeId={left.NodeId} backendNodeId={left.BackendNodeId} x={left.X:F0} y={left.Y:F0} rawX={left.RawX:F0} rawY={left.RawY:F0}");
                if (!await SendCdpVirtualClickAsync(left, "LEFT", ct))
                {
                    _log.Warn("[VIDEO_MENU_LEFT_RECT_FAILED] Không lấy được tọa độ viewport hiện tại của browse-ellipsis.");
                    return false;
                }

                _log.Info("[VIDEO_MENU_LEFT_CDP_CLICK_SENT]");
                var clickDeadline = DateTime.UtcNow.AddSeconds(18);
                while (DateTime.UtcNow < clickDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await IsTikTokOwnerDeleteMenuVisibleAsync(ct))
                    {
                        _videoDeleteMenuUiHint = "LEFT";
                        _log.Info("[VIDEO_MENU_LEFT_VISIBLE] method=RECT_CDP_CLICK hint=LEFT");
                        return true;
                    }
                    await Task.Delay(300, ct);
                }

                _log.Warn($"[VIDEO_MENU_LEFT_NO_MENU] method=RECT_CDP_CLICK visibleMenu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_MENU_LEFT_ERROR] method=RECT_CDP_CLICK error={ex.Message}");
            }
            return false;
        }

        async Task<bool> TrySingleAsync(string reason)
        {
            try
            {
                _log.Info($"[VIDEO_MENU_SINGLE_TRY_FAST] reason={reason} hint={_videoDeleteMenuUiHint}");
                if (await TryOpenTikTokSingleEllipsisDeleteMenuAsync(ct))
                {
                    _videoDeleteMenuUiHint = "SINGLE";
                    _log.Info("[VIDEO_MENU_SINGLE_VISIBLE] method=SINGLE_ELLIPSIS_CDP_CLICK hint=SINGLE");
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_MENU_SINGLE_ERROR] reason={reason} error={ex.Message}");
            }
            return false;
        }

        // Dò nhanh cả ba layout. Mỗi snapshot luôn xét RIGHT trước, rồi LEFT, sau cùng mới SINGLE.
        // Vì vậy nếu giao diện có đồng thời hai dấu như a33 thì RIGHT luôn thắng ngay trong cùng snapshot.
        var started = DateTime.UtcNow;
        var fullDeadline = started.AddSeconds(30); // giữ dư cho VPS chậm, nhưng không bắt SINGLE phải chờ hết 30s.
        var nextLog = DateTime.MinValue;
        DateTime? firstLeftSeenAt = null;
        var singleLastTriedAt = DateTime.MinValue;
        CdpFlatMenuProbe probe = new(null, null, 0, 0, 0, 0, 0);

        while (DateTime.UtcNow < fullDeadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryDismissTikTokVideoBlockingPopupAsync("menu-fast-scan", ct);

            try
            {
                probe = await ProbeCdpFlattenedMenuTargetsAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_FLAT_DOM_ERROR] error={ex.Message}");
                await Task.Delay(500, ct);
                continue;
            }

            if (DateTime.UtcNow >= nextLog)
            {
                _log.Info($"[VIDEO_MENU_FAST_SCAN] hint={_videoDeleteMenuUiHint} nodes={probe.NodeCount} right={(probe.Right is null ? 0 : 1)} left={(probe.Left is null ? 0 : 1)} rightCandidates={probe.RightCandidateCount} leftCandidates={probe.LeftCandidateCount} elapsed={(DateTime.UtcNow-started).TotalSeconds:F1}s");
                nextLog = DateTime.UtcNow.AddSeconds(3);
            }

            // Ưu tiên tuyệt đối RIGHT. Nếu cùng snapshot có RIGHT + LEFT thì không bao giờ đụng LEFT/SINGLE.
            if (probe.Right is not null)
            {
                if (await TryRightAsync(probe.Right)) return true;

                // RIGHT có node nhưng không mở được menu: reprobe rồi mới thử LEFT/SINGLE,
                // tránh dùng tọa độ/node cũ nếu TikTok vừa re-render.
                try { probe = await ProbeCdpFlattenedMenuTargetsAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"[VIDEO_FLAT_DOM_REPROBE_ERROR] {ex.Message}");
                }

                if (probe.Right is not null)
                {
                    // Nếu RIGHT vẫn còn mà vừa thất bại, không spam hover liên tục; cho LEFT rồi SINGLE fallback.
                    _log.Warn("[VIDEO_MENU_RIGHT_FALLBACK] RIGHT không mở menu; thử LEFT rồi SINGLE.");
                }
                if (probe.Left is not null && await TryLeftAsync(probe.Left)) return true;
                if (await TrySingleAsync("right-failed")) return true;
                break;
            }

            // Không có RIGHT. LEFT vẫn đứng trên SINGLE. Cho RIGHT một grace rất ngắn 1.2s nếu LEFT vừa xuất hiện,
            // để trường hợp UI hai dấu render lệch vài frame vẫn ưu tiên RIGHT mà không phải chờ 8-30 giây.
            if (probe.Left is not null)
            {
                firstLeftSeenAt ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - firstLeftSeenAt.Value).TotalMilliseconds < 1200)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                // Reprobe đúng trước khi click LEFT; nếu RIGHT vừa xuất hiện thì RIGHT thắng.
                try { probe = await ProbeCdpFlattenedMenuTargetsAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"[VIDEO_FLAT_DOM_REPROBE_ERROR] {ex.Message}");
                }
                if (probe.Right is not null)
                {
                    _log.Info("[VIDEO_MENU_PRIORITY_RIGHT] RIGHT xuất hiện trong grace LEFT; chuyển sang RIGHT.");
                    if (await TryRightAsync(probe.Right)) return true;
                }
                if (probe.Left is not null && await TryLeftAsync(probe.Left)) return true;
                if (await TrySingleAsync("left-failed")) return true;
                break;
            }

            firstLeftSeenAt = null;

            // Không có RIGHT/LEFT trong snapshot => thử SINGLE NGAY, không chờ timeout 30s.
            // Giãn tối thiểu 1.2s giữa các lần thử để tránh click lặp khi trang đang mount chậm.
            if ((DateTime.UtcNow - singleLastTriedAt).TotalMilliseconds >= 1200)
            {
                singleLastTriedAt = DateTime.UtcNow;
                if (await TrySingleAsync(_videoDeleteMenuUiHint == "SINGLE" ? "remembered-single" : "no-right-left"))
                    return true;
            }

            await Task.Delay(350, ct);
        }

        var finalSummary = await ReadTikTokVisibleMenuSummaryAsync(ct);
        _log.Warn($"[VIDEO_DELETE_MORE_FAILED] method=FAST_PRIORITY_SCAN hint={_videoDeleteMenuUiHint} right={(probe.Right is null ? 0 : 1)} left={(probe.Left is null ? 0 : 1)} nodes={probe.NodeCount} shadowRoots={probe.ShadowRootCount} frames={probe.FrameCount} visibleMenu={finalSummary}");
        return false;
    }

    async Task<bool> IsTikTokDeleteConfirmDialogVisibleAsync(CancellationToken ct)
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
  const looksConfirm = text => {
    const t = fold(text);
    return (t.includes('chac chan') && t.includes('xoa') && (t.includes('video') || t.includes('bai dang') || t.includes('bai viet') || t.includes('anh')))
      || (t.includes('sure') && t.includes('delete') && (t.includes('video') || t.includes('post') || t.includes('photo')))
      || t.includes('delete this video')
      || t.includes('delete this post')
      || t.includes('delete this photo');
  };
  return [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"],[class*="Modal"],[class*="modal"],[class*="Dialog"],[class*="dialog"],div')]
    .filter(visible)
    .some(el => looksConfirm(el.innerText || el.textContent || ''));
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    sealed record VideoActionRect(double Left, double Top, double Width, double Height, string Tag, string Role, string Cursor, double Score);

    static VideoActionRect? ParseVideoActionRect(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            static double Num(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var e) && e.TryGetDouble(out var d) ? d : 0;
            static string Str(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

            var rect = new VideoActionRect(
                Num(root, "left"),
                Num(root, "top"),
                Num(root, "width"),
                Num(root, "height"),
                Str(root, "tag"),
                Str(root, "role"),
                Str(root, "cursor"),
                Num(root, "score"));
            return rect.Width > 1 && rect.Height > 1 ? rect : null;
        }
        catch
        {
            return null;
        }
    }

    async Task<bool> IsVideoDeletePointStillValidAsync(double x, double y, CancellationToken ct)
    {
        var js = FormattableString.Invariant($$"""
(() => {
  const x = {{x}}, y = {{y}};
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  let el = document.elementFromPoint(x, y);
  if (!el) return false;
  for (let i = 0; i < 7 && el; i++, el = el.parentElement) {
    const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
    if (t === 'xoa' || t === 'delete' || t.includes(' xoa') || t.includes('delete')) return true;
  }
  return false;
})()
""");
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch { return false; }
    }

    async Task<bool> ClickVideoActionRectWithVirtualMouseAsync(
        VideoActionRect rect,
        string role,
        CancellationToken ct,
        double xRatio = 0.50)
    {
        var (vw, vh) = await GetViewportSizeAsync(ct);
        if (rect.Width < 2 || rect.Height < 2
            || rect.Left + rect.Width <= 0 || rect.Top + rect.Height <= 0
            || rect.Left >= vw || rect.Top >= vh)
        {
            _log.Warn($"[VIDEO_ACTION_RECT_OUTSIDE] role={role} left={rect.Left:F1} top={rect.Top:F1} width={rect.Width:F1} height={rect.Height:F1} viewport={vw}x{vh}");
            return false;
        }

        var ratio = Math.Clamp(xRatio, 0.15, 0.85);
        var x = Math.Clamp(rect.Left + rect.Width * ratio, 1, Math.Max(1, vw - 2));
        var y = Math.Clamp(rect.Top + rect.Height / 2.0, 1, Math.Max(1, vh - 2));

        // elementFromPoint chỉ dùng để chẩn đoán. Không chặn click nếu React vừa remount node,
        // vì tọa độ vừa được lấy trực tiếp trong cùng lần Eval trước đó.
        var pointValid = await IsVideoDeletePointStillValidAsync(x, y, ct);
        _log.Info($"[VIDEO_ACTION_POINT_CHECK] role={role} valid={pointValid} x={x:F1} y={y:F1} rect=({rect.Left:F1},{rect.Top:F1},{rect.Width:F1},{rect.Height:F1})");

        var enterX = Math.Clamp(rect.Left + Math.Min(Math.Max(4, rect.Width * 0.12), 14), 1, Math.Max(1, vw - 2));
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x = enterX,
            y,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(80, ct);

        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x,
            y,
            button = "none",
            buttons = 0,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(120, ct);

        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            buttons = 1,
            clickCount = 1,
            pointerType = "mouse"
        }, ct);
        await Task.Delay(90, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            buttons = 0,
            clickCount = 1,
            pointerType = "mouse"
        }, ct);

        _log.Info($"[VIDEO_ACTION_DIRECT_RECT_CLICK_SENT] role={role} x={x:F1} y={y:F1} ratio={ratio:F2} pointValid={pointValid}");
        return true;
    }

    async Task<bool> ClickTikTokDeleteMenuItemAsync(CancellationToken ct)
    {
        // Menu bên phải phụ thuộc trạng thái HOVER của dấu ba chấm. Vì vậy tuyệt đối không
        // mouseMoved từ dấu ba chấm sang row "Xóa" trước khi kích hoạt, nếu không menu có thể
        // biến mất trước khi click thật sự được nhận.
        //
        // Cách mới:
        //   1) Giữ nguyên hover hiện tại trên dấu ba chấm.
        //   2) Tìm chính xác row Xóa đang visible và kích hoạt TRỰC TIẾP trong cùng Eval.
        //   3) Chỉ coi là thành công nếu dialog xác nhận thực sự xuất hiện.
        //   4) Nếu chưa có dialog: mở lại menu rồi thử lại. Không dùng điều kiện
        //      "menu biến mất = đã click thành công" nữa.
        const string activateJs = """
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

  const menuLooksRight = el => {
    if (!visible(el)) return false;
    const t = fold(el.innerText || el.textContent || '');
    if (!t.includes('xoa') && !t.includes('delete')) return false;
    const r = el.getBoundingClientRect();
    // menu Xóa của chủ bài nằm ở nửa phải, vùng phía trên của màn hình detail.
    return r.left > innerWidth * 0.50 && r.top < innerHeight * 0.65;
  };

  let scopes = [...document.querySelectorAll(
    '[role="menu"],[role="listbox"],[class*="Menu"],[class*="menu"],[class*="Popup"],[class*="popup"],[class*="Popover"],[class*="popover"]'
  )].filter(menuLooksRight);

  // TikTok đôi khi portal menu vào div thường, nên fallback theo text menu đã nhìn thấy.
  if (!scopes.length) {
    scopes = [...document.querySelectorAll('div')]
      .filter(menuLooksRight)
      .filter(el => {
        const t = fold(el.innerText || el.textContent || '');
        const r = el.getBoundingClientRect();
        return (t.includes('quyen rieng tu') || t.includes('privacy') || t === 'xoa' || t === 'delete' || t.includes(' xoa'))
          && r.width >= 90 && r.width <= 520 && r.height >= 30 && r.height <= 420;
      });
  }

  const searchRoots = scopes.length ? scopes : [document.body];
  const hits = [];

  for (const root of searchRoots) {
    const nodes = [...root.querySelectorAll('button,[role="button"],[role="menuitem"],li,div,span,p')]
      .filter(visible)
      .filter(el => {
        const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''} ${el.getAttribute?.('title') || ''}`);
        return t === 'xoa' || t === 'delete';
      });

    for (const leaf of nodes) {
      let best = leaf;
      let n = leaf;
      for (let depth = 0; depth < 7 && n; depth++, n = n.parentElement) {
        if (!visible(n)) continue;
        const t = fold(n.innerText || n.textContent || '');
        if (t !== 'xoa' && t !== 'delete') continue;
        const r = n.getBoundingClientRect();
        const cs = getComputedStyle(n);
        if (r.left < innerWidth * 0.50 || r.top > innerHeight * 0.70) continue;

        let score = 0;
        if (n.matches?.('button,[role="button"],[role="menuitem"],li')) score += 500;
        if (cs.cursor === 'pointer') score += 420;
        if (r.height >= 26 && r.height <= 100) score += 180;
        if (r.width >= 80 && r.width <= 520) score += 160;
        if (n.hasAttribute?.('tabindex')) score += 80;
        if (n.onclick) score += 120;
        hits.push({ node:n, score, depth, rect:r, tag:n.tagName, role:n.getAttribute?.('role') || '', cursor:cs.cursor || '' });
      }
    }
  }

  hits.sort((a,b) => b.score - a.score || b.rect.width - a.rect.width || a.depth - b.depth);
  if (!hits.length) return JSON.stringify({ok:false, reason:'DELETE_ROW_NOT_FOUND'});

  const hit = hits[0];
  const el = hit.node;
  const r = hit.rect;
  const info = {
    ok:true,
    tag:hit.tag,
    role:hit.role,
    cursor:hit.cursor,
    score:hit.score,
    left:r.left, top:r.top, width:r.width, height:r.height,
    text:(el.innerText || el.textContent || '').trim().slice(0,80)
  };

  try { el.focus?.({preventScroll:true}); } catch {}
  try {
    // Native HTMLElement.click() gọi thẳng click handler của React mà không cần di chuyển
    // con trỏ ảo ra khỏi dấu ba chấm đang giữ hover.
    if (typeof el.click === 'function') {
      el.click();
      return JSON.stringify({...info, method:'HTMLElement.click'});
    }
  } catch (e) {
    info.clickError = String(e?.message || e);
  }

  // Fallback DOM event sequence, vẫn không đụng chuột Windows/CDP pointer position.
  try {
    const init = {bubbles:true, cancelable:true, composed:true, view:window, button:0, buttons:1};
    try { el.dispatchEvent(new PointerEvent('pointerdown', {...init, pointerType:'mouse', isPrimary:true})); } catch {}
    el.dispatchEvent(new MouseEvent('mousedown', init));
    try { el.dispatchEvent(new PointerEvent('pointerup', {...init, pointerType:'mouse', isPrimary:true, buttons:0})); } catch {}
    el.dispatchEvent(new MouseEvent('mouseup', {...init, buttons:0}));
    el.dispatchEvent(new MouseEvent('click', {...init, buttons:0}));
    return JSON.stringify({...info, method:'DOM_EVENT_SEQUENCE'});
  } catch (e) {
    return JSON.stringify({...info, ok:false, reason:'ACTIVATE_EXCEPTION', error:String(e?.message || e)});
  }
})()
""";

        const string rectOnlyJs = """
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
  const hits = [...document.querySelectorAll('button,[role="button"],[role="menuitem"],li,div,span,p')]
    .filter(visible)
    .filter(el => {
      const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
      if (t !== 'xoa' && t !== 'delete') return false;
      const r = el.getBoundingClientRect();
      return r.left > innerWidth * 0.50 && r.top < innerHeight * 0.70;
    })
    .map(el => {
      let n = el;
      for (let i=0; i<6 && n; i++, n=n.parentElement) {
        if (!visible(n)) continue;
        const t = fold(n.innerText || n.textContent || '');
        if (t !== 'xoa' && t !== 'delete') continue;
        const r = n.getBoundingClientRect();
        const cs = getComputedStyle(n);
        if (r.left < innerWidth * 0.50) continue;
        let score = 0;
        if (n.matches?.('button,[role="button"],[role="menuitem"],li')) score += 500;
        if (cs.cursor === 'pointer') score += 400;
        if (r.width >= 80 && r.width <= 520) score += 140;
        if (r.height >= 26 && r.height <= 100) score += 120;
        return {score,left:r.left,top:r.top,width:r.width,height:r.height,tag:n.tagName,role:n.getAttribute?.('role')||'',cursor:cs.cursor||''};
      }
      return null;
    })
    .filter(Boolean)
    .sort((a,b)=>b.score-a.score || b.width-a.width);
  if (!hits.length) return '';
  return JSON.stringify(hits[0]);
})()
""";

        async Task<bool> WaitConfirmAsync(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsTikTokDeleteConfirmDialogVisibleAsync(ct)) return true;
                await Task.Delay(220, ct);
            }
            return false;
        }

        async Task EnsureDeleteMenuOpenAsync()
        {
            if (await IsTikTokOwnerDeleteMenuVisibleAsync(ct)) return;
            _log.Info("[VIDEO_DELETE_MENU_REOPEN] menu not visible -> reopen more menu");
            await ClickTikTokPostMoreMenuAsync(ct);
        }

        // Tối đa 3 vòng. Mỗi vòng chỉ thành công khi thấy dialog xác nhận thật.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await EnsureDeleteMenuOpenAsync();
                if (!await IsTikTokOwnerDeleteMenuVisibleAsync(ct))
                {
                    _log.Warn($"[VIDEO_DELETE_MENU_DIRECT] attempt={attempt} menuVisible=false");
                    continue;
                }

                string activation = "";
                try { activation = ReadEvalString(await EvalAsync(activateJs, ct: ct)); }
                catch (Exception ex) when (IsTransientDocumentContextError(ex))
                {
                    _log.Warn($"[VIDEO_DELETE_MENU_DOM_ACTIVATE_TRANSIENT] attempt={attempt} error={ex.Message}");
                }

                _log.Info($"[VIDEO_DELETE_MENU_DOM_ACTIVATE] attempt={attempt} result={activation}");

                if (await WaitConfirmAsync(4500))
                {
                    _log.Info($"[VIDEO_DELETE_MENU_ITEM_ACCEPTED] attempt={attempt} method=DOM_DIRECT confirmVisible=true");
                    return true;
                }

                // Nếu DOM click chưa kích hoạt được (isTrusted=false), thử một lần CDP press/release
                // trực tiếp tại row Xóa NHƯNG KHÔNG gửi mouseMoved. Như vậy hover dấu ba chấm
                // vẫn được giữ, tránh hiện tượng menu nhấp nháy rồi biến mất trước khi click.
                if (!await IsTikTokOwnerDeleteMenuVisibleAsync(ct))
                {
                    await EnsureDeleteMenuOpenAsync();
                }

                string rectInfo = "";
                try { rectInfo = ReadEvalString(await EvalAsync(rectOnlyJs, ct: ct)); }
                catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

                var rect = ParseVideoActionRect(rectInfo);
                if (rect is not null)
                {
                    var (vw, vh) = await GetViewportSizeAsync(ct);
                    var x = Math.Clamp(rect.Left + rect.Width * 0.50, 1, Math.Max(1, vw - 2));
                    var y = Math.Clamp(rect.Top + rect.Height * 0.50, 1, Math.Max(1, vh - 2));
                    _log.Info($"[VIDEO_DELETE_MENU_NOMOVE_CLICK] attempt={attempt} x={x:F1} y={y:F1} rect={rectInfo}");

                    await Cdp.CallAsync("Input.dispatchMouseEvent", new
                    {
                        type = "mousePressed",
                        x,
                        y,
                        button = "left",
                        buttons = 1,
                        clickCount = 1,
                        pointerType = "mouse"
                    }, ct);
                    await Task.Delay(100, ct);
                    await Cdp.CallAsync("Input.dispatchMouseEvent", new
                    {
                        type = "mouseReleased",
                        x,
                        y,
                        button = "left",
                        buttons = 0,
                        clickCount = 1,
                        pointerType = "mouse"
                    }, ct);

                    if (await WaitConfirmAsync(4500))
                    {
                        _log.Info($"[VIDEO_DELETE_MENU_ITEM_ACCEPTED] attempt={attempt} method=CDP_NO_MOVE confirmVisible=true");
                        return true;
                    }
                }

                // QUAN TRỌNG: menu biến mất KHÔNG còn được coi là thành công.
                var menuStillVisible = await IsTikTokOwnerDeleteMenuVisibleAsync(ct);
                _log.Warn($"[VIDEO_DELETE_MENU_ITEM_NO_CONFIRM] attempt={attempt} menuStillVisible={menuStillVisible} visibleMenu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_DELETE_MENU_ITEM_ATTEMPT_ERROR] attempt={attempt} error={ex.Message}");
            }

            await Task.Delay(500, ct);
        }

        _log.Warn($"[VIDEO_DELETE_MENU_ITEM_NOT_CLICKED] Không xuất hiện dialog xác nhận sau 3 lần thử. visibleMenu={await ReadTikTokVisibleMenuSummaryAsync(ct)}");
        return false;
    }

    async Task<bool> ClickTikTokDeleteConfirmAsync(CancellationToken ct)
    {
        // Hộp xác nhận: lấy rect của nút/row "Xóa" trực tiếp trong cùng một Eval
        // và click ngay bằng CDP. Không gắn attribute tạm để tránh React remount làm mất node.
        const string rectJs = """
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
  const looksConfirm = text => {
    const t = fold(text);
    return (t.includes('chac chan') && t.includes('xoa') && (t.includes('video') || t.includes('bai dang') || t.includes('bai viet') || t.includes('anh')))
      || (t.includes('sure') && t.includes('delete') && (t.includes('video') || t.includes('post') || t.includes('photo')))
      || t.includes('delete this video')
      || t.includes('delete this post')
      || t.includes('delete this photo');
  };

  let scopes = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"],[class*="Modal"],[class*="modal"],[class*="Dialog"],[class*="dialog"]')]
    .filter(visible)
    .filter(el => looksConfirm(el.innerText || el.textContent || ''));

  if (!scopes.length) {
    scopes = [...document.querySelectorAll('div')]
      .filter(visible)
      .filter(el => looksConfirm(el.innerText || el.textContent || ''))
      .filter(el => {
        const r = el.getBoundingClientRect();
        return r.width >= 220 && r.height >= 100 && r.width <= innerWidth * 0.90 && r.height <= innerHeight * 0.90;
      });
  }

  if (!scopes.length) return '';
  scopes.sort((a,b) => {
    const ar=a.getBoundingClientRect(), br=b.getBoundingClientRect();
    return (ar.width*ar.height) - (br.width*br.height);
  });
  const scope = scopes[0];
  const scopeRect = scope.getBoundingClientRect();

  const leaves = [...scope.querySelectorAll('button,[role="button"],div,span,p')]
    .filter(visible)
    .filter(el => {
      const t = fold(`${el.innerText || el.textContent || ''} ${el.getAttribute?.('aria-label') || ''}`);
      return t === 'xoa' || t === 'delete';
    });

  const hits = [];
  for (const leaf of leaves) {
    let n = leaf;
    for (let depth=0; depth<6 && n && scope.contains(n); depth++, n=n.parentElement) {
      if (!visible(n)) continue;
      const t = fold(n.innerText || n.textContent || '');
      if (t !== 'xoa' && t !== 'delete') continue;

      const r=n.getBoundingClientRect();
      const cs=getComputedStyle(n);
      let score=0;
      if (n.matches?.('button,[role="button"]')) score += 280;
      if (cs.cursor === 'pointer') score += 260;
      if (r.height >= 30 && r.height <= 100) score += 100;
      if (r.width >= 80 && r.width <= 600) score += 120;
      const lr=leaf.getBoundingClientRect();
      if (r.width >= lr.width + 20) score += 90;
      if (r.width > scopeRect.width * 1.05) score -= 500;

      hits.push({
        score, depth,
        rect:{left:r.left,top:r.top,width:r.width,height:r.height},
        cursor:cs.cursor, tag:n.tagName, role:n.getAttribute?.('role')||''
      });
    }
  }

  hits.sort((a,b)=>b.score-a.score || b.rect.width-a.rect.width || a.depth-b.depth);
  if (!hits.length) return '';
  const hit=hits[0];
  return JSON.stringify({
    left:hit.rect.left, top:hit.rect.top,
    width:hit.rect.width, height:hit.rect.height,
    score:hit.score, cursor:hit.cursor, tag:hit.tag, role:hit.role
  });
})()
""";

        var deadline = DateTime.UtcNow.AddSeconds(20);
        var ratios = new[] { 0.50, 0.35, 0.65 };

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var ratio in ratios)
            {
                ct.ThrowIfCancellationRequested();

                string info = "";
                try { info = ReadEvalString(await EvalAsync(rectJs, ct: ct)); }
                catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

                var rect = ParseVideoActionRect(info);
                if (rect is null)
                {
                    await Task.Delay(220, ct);
                    continue;
                }

                _log.Info($"[VIDEO_DELETE_CONFIRM_RECT] ratio={ratio:F2} {info}");

                try
                {
                    if (!await ClickVideoActionRectWithVirtualMouseAsync(rect, "DELETE_CONFIRM", ct, ratio))
                        continue;

                    await Task.Delay(550, ct);
                    var dialogStillVisible = await IsTikTokDeleteConfirmDialogVisibleAsync(ct);
                    var toastVisible = await IsTikTokDeleteToastVisibleAsync(ct);
                    _log.Info($"[VIDEO_DELETE_CONFIRM_VERIFY] ratio={ratio:F2} dialogStillVisible={dialogStillVisible} toastVisible={toastVisible}");
                    if (!dialogStillVisible || toastVisible)
                    {
                        _log.Info($"[VIDEO_DELETE_CONFIRM_ACCEPTED] ratio={ratio:F2} toastVisible={toastVisible}");
                        return true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO_DELETE_CONFIRM_DIRECT_RECT_ERROR] ratio={ratio:F2} error={ex.Message}");
                }

                await Task.Delay(250, ct);
            }

            await Task.Delay(250, ct);
        }

        _log.Warn("[VIDEO_DELETE_CONFIRM_NOT_FOUND_OR_NOT_CLICKED] Không tìm/click được nút Xóa trong dialog xác nhận sau 20s.");
        return false;
    }

    async Task<bool> IsTikTokDeleteToastVisibleAsync(CancellationToken ct)
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
  const selectors = '[role="alert"],[role="status"],[aria-live], [data-e2e*="toast"], [class*="Toast"], [class*="toast"]';
  const preferred = [...document.querySelectorAll(selectors)].filter(visible);
  const isDeleted = el => {
    const t = fold(el.innerText || el.textContent || '');
    return t === 'da xoa' || t === 'deleted' || t.includes('video da xoa') || t.includes('video deleted') || t.includes('post deleted');
  };
  if (preferred.some(isDeleted)) return true;
  return [...document.querySelectorAll('div,span,p')].filter(visible).some(el => {
    const t = fold(el.innerText || el.textContent || '');
    if (!(t === 'da xoa' || t === 'deleted')) return false;
    const r = el.getBoundingClientRect();
    return r.top < innerHeight * 0.35;
  });
})()
""";
        try { return ReadEvalBool(await EvalAsync(js, ct: ct)); }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { return false; }
    }

    async Task<bool> WaitForTikTokDeleteToastAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsTikTokDeleteToastVisibleAsync(ct)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    async Task<bool> WaitForTikTokDeleteToastGoneAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsTikTokDeleteToastVisibleAsync(ct)) return true;
            await Task.Delay(200, ct);
        }
        return !await IsTikTokDeleteToastVisibleAsync(ct);
    }

    async Task<bool> WaitForTikTokNextPostAsync(string oldUrl, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var href = await ReadCurrentLocationHrefAsync(ct);
                if (!string.IsNullOrWhiteSpace(href)
                    && !string.Equals(StripTikTokUrlSuffix(href), StripTikTokUrlSuffix(oldUrl), StringComparison.OrdinalIgnoreCase)
                    && (href.Contains("/video/", StringComparison.OrdinalIgnoreCase)
                        || href.Contains("/photo/", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
            await Task.Delay(250, ct);
        }
        return false;
    }

    async Task DeleteCurrentTikTokPostAsync(int ordinal, int expectedTotal, CancellationToken ct)
    {
        var currentUrl = await ReadCurrentLocationHrefAsync(ct);
        if (string.IsNullOrWhiteSpace(currentUrl)
            || (!currentUrl.Contains("/video/", StringComparison.OrdinalIgnoreCase)
                && !currentUrl.Contains("/photo/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Không còn đứng ở giao diện bài đăng TikTok trước khi xóa.");
        }

        await TryDismissTikTokVideoBlockingPopupAsync($"delete-item-{ordinal}", ct);

        if (!await WaitForTikTokDeleteToastGoneAsync(ct))
            throw new InvalidOperationException("Thông báo 'Đã xóa' của lượt trước vẫn còn trên màn hình quá lâu. Tool dừng để không nhận nhầm toast cũ cho bài kế tiếp.");
        if (await IsCaptchaVisibleAsync(ct))
            throw new InvalidOperationException("Phát hiện CAPTCHA trên TikTok. Tool dừng phần xóa video để bạn xử lý thủ công.");
        _log.Info($"[VIDEO_DELETE_ITEM_START] item={ordinal}/{expectedTotal} url={currentUrl}");

        if (!await ClickTikTokPostMoreMenuAsync(ct))
            throw new InvalidOperationException("Không tìm thấy nút dấu ba chấm (...) của bài đang mở.");

        if (!await ClickTikTokDeleteMenuItemAsync(ct))
            throw new InvalidOperationException("Đã mở menu nhưng không tìm thấy mục Xóa/Delete.");

        if (!await ClickTikTokDeleteConfirmAsync(ct))
            throw new InvalidOperationException("Không thấy hộp 'Bạn có chắc chắn muốn xóa video này?' hoặc không tìm thấy nút Xóa trong hộp xác nhận.");

        if (!await WaitForTikTokDeleteToastAsync(ct))
            throw new InvalidOperationException("Đã bấm xác nhận Xóa nhưng không thấy thông báo nổi 'Đã xóa' trong 30 giây. Tool không tính bài này là đã xóa.");

        _log.Info($"[VIDEO_DELETE_ITEM_OK] item={ordinal}/{expectedTotal} toast=confirmed url={currentUrl}");
    }

    async Task<(bool Empty, int Remaining, bool EmptyMarker)> VerifyTikTokProfileEmptyAsync(
        string profileHref,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await NavigateAndWaitAsync(profileHref, 1000, 30000, ct);
            var scan = await ScanTikTokProfilePostsAsync(profileHref, 8, ct);
            if (scan.Count == 0 && scan.EmptyMarkerVisible)
            {
                _log.Info($"[VIDEO_DELETE_EMPTY_VERIFIED] attempt={attempt}/2 marker=true remaining=0");
                return (true, 0, true);
            }

            if (scan.Count > 0)
                return (false, scan.Count, scan.EmptyMarkerVisible);

            if (attempt < 2)
            {
                _log.Warn("[VIDEO_DELETE_EMPTY_MARKER_MISSING] remaining=0 marker=false; reload để xác nhận lại");
                await ReloadAndWaitAsync(1100, 25000, ct);
                await Task.Delay(500, ct);
            }
            else
            {
                // Hai lần liên tiếp không còn link bài thuộc đúng profile: chấp nhận trạng thái trống
                // dù TikTok chưa render câu 'Tải video đầu tiên của bạn lên'.
                _log.Info("[VIDEO_DELETE_EMPTY_VERIFIED] attempt=2/2 marker=false remaining=0 method=double-empty-scan");
                return (true, 0, false);
            }
        }

        return (false, -1, false);
    }

    public async Task<TikTokVideoDeleteResult> DeleteTikTokProfilePostsAsync(
        string? username,
        string? deleteMode,
        Action<TikTokVideoDeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Connected)
            return new TikTokVideoDeleteResult(false, 0, 0, -1, false, "", "Chrome chưa kết nối CDP.");

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

        Report("STARTING", "Đang chuẩn bị xóa video/bài cũ...");

        try
        {
            _log.Info($"[VIDEO_DELETE_START] mode={mode} username={NormalizeTikTokVideoHandle(username)}");
            Report("SESSION_CHECK", "Đang kiểm tra đăng nhập TikTok...");

            if (!await EnsureTikTokIdentitySessionReadyAsync(ct))
                throw new TikTokVideoSkipException("LOGIN_REQUIRED", "TikTok chưa đăng nhập; bỏ qua phần VIDEO để logic đăng nhập hiện tại của tool xử lý.");

            Report("NAVIGATING_PROFILE", "Đang vào đúng trang cá nhân...");
            var profileHref = await NavigateToOwnTikTokProfileForVideoAsync(username, ct);
            Report("SCANNING", "Đang quét video/bài cũ trên trang cá nhân...");
            if (mode == "newest")
                await TrySelectTikTokNewestSortAsync(ct);
            var initialScan = await ScanTikTokProfilePostsAsync(profileHref, 24, ct);
            initialCount = initialScan.Count;
            remainingCount = initialCount;
            Report("SCANNED", $"Đã quét thấy {initialCount} video/bài cũ.");

            if (initialCount == 0)
            {
                var empty = await VerifyTikTokProfileEmptyAsync(profileHref, ct);
                verifiedEmpty = empty.Empty;
                remainingCount = Math.Max(0, empty.Remaining);
                if (verifiedEmpty)
                {
                    var message = initialScan.EmptyMarkerVisible || empty.EmptyMarker
                        ? "Trang cá nhân đã không còn video/bài cũ (đã thấy trạng thái 'Tải video đầu tiên của bạn lên')."
                        : "Trang cá nhân đã không còn video/bài cũ.";
                    Report("COMPLETED", message, running: false, completed: true, ok: true);
                    return new TikTokVideoDeleteResult(true, 0, 0, 0, true, message, "");
                }
                throw new InvalidOperationException("Không quét thấy bài cũ nhưng cũng chưa xác nhận được trang cá nhân trống.");
            }

            _log.Info($"[VIDEO_DELETE_INITIAL_SCAN] count={initialCount} mode={mode}");

            if (mode == "newest")
            {
                await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                if (!await OpenTikTokPostAsync(initialScan.Hrefs[0], ct))
                    throw new InvalidOperationException("Không mở được video/bài đầu tiên trên trang cá nhân.");

                Report("DELETING", "Đang xóa video/bài mới nhất (1/1)...");
                await DeleteCurrentTikTokPostAsync(1, 1, ct);
                deletedCount = 1;
                remainingCount = Math.Max(0, initialCount - 1);
                Report("DELETED", "Đã nhận xác nhận 'Đã xóa' cho video/bài mới nhất.");
                await WaitForTikTokDeleteToastGoneAsync(ct);

                await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                var verify = await ScanTikTokProfilePostsAsync(profileHref, 10, ct);
                remainingCount = verify.Count;
                var deletedHrefStillExists = verify.Hrefs.Any(x => string.Equals(x, initialScan.Hrefs[0], StringComparison.OrdinalIgnoreCase));
                if (deletedHrefStillExists)
                    throw new InvalidOperationException("TikTok đã hiện 'Đã xóa' nhưng bài vừa xóa vẫn còn xuất hiện khi quét lại trang cá nhân.");

                _log.Info($"[VIDEO_DELETE_NEWEST_DONE] deleted=1 initial={initialCount} remaining={remainingCount}");
                var newestMessage = $"Đã xóa video/bài mới nhất. Còn {remainingCount} bài.";
                verifiedEmpty = remainingCount == 0;
                Report("COMPLETED", newestMessage, running: false, completed: true, ok: true);
                return new TikTokVideoDeleteResult(true, initialCount, 1, remainingCount, verifiedEmpty, newestMessage, "");
            }

            // XÓA TẤT CẢ: quét -> mở bài đầu tiên -> xóa tuần tự. Sau mỗi pass lại về
            // profile quét lại để bắt các bài lazy-load chưa thấy ở lần đầu. Tối đa 3 pass.
            var passScan = initialScan;
            for (var pass = 1; pass <= 3; pass++)
            {
                ct.ThrowIfCancellationRequested();
                if (passScan.Count == 0) break;

                _log.Info($"[VIDEO_DELETE_PASS_START] pass={pass}/3 remainingAtStart={passScan.Count} deletedTotal={deletedCount}");
                await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                if (!await OpenTikTokPostAsync(passScan.Hrefs[0], ct))
                    throw new InvalidOperationException("Không mở được video/bài đầu tiên trên trang cá nhân.");

                for (var i = 0; i < passScan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var oldUrl = await ReadCurrentLocationHrefAsync(ct);
                    Report("DELETING", $"Đang xóa video/bài {deletedCount + 1}/{Math.Max(initialCount, deletedCount + passScan.Count)}...");
                    await DeleteCurrentTikTokPostAsync(deletedCount + 1, initialCount, ct);
                    deletedCount++;
                    remainingCount = Math.Max(0, remainingCount - 1);
                    Report("DELETED", $"Đã xóa {deletedCount} video/bài; đang tiếp tục...");

                    if (deletedCount >= 500)
                        throw new InvalidOperationException("Đã chạm giới hạn an toàn 500 bài trong một lượt. Tool dừng để tránh vòng lặp ngoài ý muốn.");

                    var moreExpectedInThisPass = i + 1 < passScan.Count;
                    if (!moreExpectedInThisPass) break;

                    var moved = await WaitForTikTokNextPostAsync(oldUrl, ct);
                    await WaitForTikTokDeleteToastGoneAsync(ct);
                    if (moved)
                    {
                        _log.Info($"[VIDEO_DELETE_NEXT_POST] pass={pass} item={i + 2}/{passScan.Count} source=auto-transition");
                        await TryDismissTikTokVideoBlockingPopupAsync("after-next-post", ct);
                        continue;
                    }

                    // Nếu TikTok không tự chuyển sau toast, không đoán/click tiếp trên DOM cũ.
                    // Về profile, quét lại và mở bài đầu tiên còn lại rồi tiếp tục.
                    _log.Warn($"[VIDEO_DELETE_NEXT_POST_TIMEOUT] pass={pass} item={i + 1}/{passScan.Count}; recovery=profile-rescan");
                    await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                    var recovery = await ScanTikTokProfilePostsAsync(profileHref, 8, ct);
                    if (recovery.Count == 0) break;
                    if (!await OpenTikTokPostAsync(recovery.Hrefs[0], ct))
                        throw new InvalidOperationException("TikTok không tự chuyển bài kế tiếp và tool cũng không mở lại được bài còn lại từ trang cá nhân.");
                }

                Report("VERIFYING", "Đang quay lại trang cá nhân để kiểm tra còn video/bài nào không...");
                var emptyState = await VerifyTikTokProfileEmptyAsync(profileHref, ct);
                remainingCount = emptyState.Remaining;
                if (emptyState.Empty)
                {
                    verifiedEmpty = true;
                    _log.Info($"[VIDEO_DELETE_ALL_VERIFIED] initial={initialCount} deleted={deletedCount} remaining=0 marker={emptyState.EmptyMarker}");
                    break;
                }

                if (pass >= 3)
                    break;

                _log.Warn($"[VIDEO_DELETE_RESCAN_REMAINING] pass={pass}/3 remaining={remainingCount}; tiếp tục xóa phần còn lại");
                await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                passScan = await ScanTikTokProfilePostsAsync(profileHref, 24, ct);
                remainingCount = passScan.Count;
            }

            if (!verifiedEmpty)
            {
                await NavigateAndWaitAsync(profileHref, 900, 30000, ct);
                var finalScan = await ScanTikTokProfilePostsAsync(profileHref, 12, ct);
                remainingCount = finalScan.Count;
                if (finalScan.Count == 0)
                {
                    var finalEmpty = await VerifyTikTokProfileEmptyAsync(profileHref, ct);
                    verifiedEmpty = finalEmpty.Empty;
                    remainingCount = Math.Max(0, finalEmpty.Remaining);
                }
            }

            if (!verifiedEmpty)
                throw new InvalidOperationException($"Đã xóa {deletedCount} bài nhưng kiểm tra cuối vẫn còn {Math.Max(0, remainingCount)} bài trên trang cá nhân.");

            var completedMessage = $"Đã xóa toàn bộ {deletedCount} video/bài cũ và xác nhận trang cá nhân đã trống.";
            Report("COMPLETED", completedMessage, running: false, completed: true, ok: true);
            return new TikTokVideoDeleteResult(
                true,
                initialCount,
                deletedCount,
                0,
                true,
                completedMessage,
                "");
        }
        catch (TikTokVideoSkipException ex)
        {
            _log.Warn($"[VIDEO_DELETE_SKIPPED] reason={ex.Reason} initial={initialCount} deleted={deletedCount} remaining={remainingCount} message={ex.Message}");
            Report("SKIPPED_LOGIN", ex.Message, running: false, completed: true, ok: false, error: ex.Message);
            return new TikTokVideoDeleteResult(false, initialCount, deletedCount, remainingCount, verifiedEmpty, "", ex.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_DELETE_FAILED] initial={initialCount} deleted={deletedCount} remaining={remainingCount} error={ex.Message}");
            Report("ERROR", ex.Message, running: false, completed: true, ok: false, error: ex.Message);
            return new TikTokVideoDeleteResult(false, initialCount, deletedCount, remainingCount, verifiedEmpty, "", ex.Message);
        }
    }
    public async Task CleanupTikTokVideoOperationAsync(CancellationToken ct = default)
    {
        // Không tác động giao diện TikTok khi cleanup.
        // Đặc biệt KHÔNG gửi Escape, KHÔNG đóng menu/dialog và KHÔNG điều hướng,
        // vì Escape có thể đóng luôn giao diện /video/ hoặc /photo/ và đưa về profile.
        // Cleanup ở đây chỉ nhường lại luồng cho Worker/Manager; các cờ điều phối được
        // lớp gọi quản lý trong finally như trước.
        ct.ThrowIfCancellationRequested();
        if (!Connected) return;
        _log.Info("[VIDEO_CLEANUP] action=none_ui_preserved");
        await Task.CompletedTask;
    }

}
