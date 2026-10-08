using System.Text.Json;
using ToolTikTokV11.Models;
using ToolTikTokV11.Services;
using ToolTikTokV11.Utils;

namespace CommentVisibilityMonitor;

/// <summary>
/// Chrome Observer is completely independent from the PRF/Worker Chrome instances.
/// It only reuses the shared ChromeController implementation so launch/CDP/profile
/// handling stays consistent with the main tool.
///
/// Isolation guarantees:
/// - dedicated ChromeController instance
/// - dedicated CDP port
/// - dedicated user-data-dir
/// - no reference to any Worker/Manager ChromeController
/// - no command path back to Worker/Manager
/// </summary>
internal sealed class ObserverChromeSession : IAsyncDisposable
{
    readonly int _port;
    readonly string _userDataDir;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Logger _chromeLog;
    readonly ChromeController _chrome;
    readonly string _coreLogRoot;
    string _currentLiveUrl = "";

    public ObserverChromeSession(int port, string userDataDir)
    {
        _port = port;
        _userDataDir = Path.GetFullPath(userDataDir);
        Directory.CreateDirectory(_userDataDir);

        var dataRoot = Path.GetDirectoryName(_userDataDir) ?? AppContext.BaseDirectory;
        _coreLogRoot = Path.Combine(dataRoot, "ObserverChromeCore");
        _chromeLog = new Logger(_coreLogRoot)
        {
            VerboseDiagnosticsEnabled = false
        };
        _chrome = new ChromeController(_chromeLog);

        // Observer is a read-only helper. Keep the shared controller in Normal mode
        // so this auxiliary browser does not inherit VM-specific media policies.
        _chrome.ConfigureVmOptimization(new VmOptimizationSettings { Mode = VmOptimizationMode.Normal });
    }

    public bool Connected => _chrome.Connected;
    public string CurrentLiveUrl => _currentLiveUrl;
    public int Port => _port;
    public string CoreLogDirectory => Path.Combine(_coreLogRoot, "logs");

    public async Task<TikTokStartupResult> LoginAsync(
        string username,
        string password,
        string totpSecret,
        CancellationToken ct = default)
    {
        await EnsureStartedAsync();

        await _gate.WaitAsync(ct);
        try
        {
            if (!_chrome.Connected)
                throw new InvalidOperationException("Chrome Observer chưa kết nối CDP.");

            // Giống nút đăng nhập thủ công hiện tại: xóa cookie TikTok cũ để
            // tài khoản nhập trong 3 ô thật sự là tài khoản được đăng nhập, rồi gọi
            // đúng credential/login flow có sẵn của ChromeController.
            // Không gọi Auto Run / Auto Replace / VIDEO / Tên-Ảnh của Worker.
            var deletedTikTokCookies = await _chrome.ClearTikTokCookiesAsync(ct);
            _chromeLog.Info(
                $"[COMMENT_OBSERVER_LOGIN_COOKIES_CLEARED] count={deletedTikTokCookies} action=EXISTING_LOGIN_FLOW");

            var result = await _chrome.PrepareTikTokStartupAsync(
                username ?? "",
                password ?? "",
                totpSecret ?? "",
                autoLogin: true,
                openLiveWhenReady: false,
                stopOnCaptcha: true,
                ct: ct);

            await RefreshCurrentUrlCoreAsync();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizeLiveUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            var u = new Uri(raw.Trim());
            if (!u.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)) return "";
            var path = u.AbsolutePath.TrimEnd('/');
            if (!path.Contains("/live", StringComparison.OrdinalIgnoreCase)) return "";
            return $"{u.Scheme}://{u.Host}{path}";
        }
        catch { return ""; }
    }

    public bool IsOnLive(string? raw)
    {
        var wanted = NormalizeLiveUrl(raw);
        return wanted.Length > 0
            && string.Equals(wanted, NormalizeLiveUrl(_currentLiveUrl), StringComparison.OrdinalIgnoreCase);
    }

    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_chrome.Connected)
            {
                try
                {
                    await RefreshCurrentUrlCoreAsync();
                    return;
                }
                catch (Exception ex)
                {
                    // WebSocket state can look connected for a short time after
                    // Chrome has died. Drop the stale session and recover below.
                    _chromeLog.Warn($"[COMMENT_OBSERVER_CONNECTED_PROBE_FAILED] {ex.GetType().Name}: {ex.Message}");
                    try { await _chrome.DisconnectAsync(TimeSpan.FromSeconds(1)); } catch { }
                }
            }

            // First try to attach to this observer's already-running Chrome on
            // the dedicated port. This keeps the Observer login/profile intact
            // across monitor restarts when the previous Chrome is still healthy.
            if (await TryAttachExistingCoreAsync())
            {
                try
                {
                    await CompleteReadyCoreAsync("attach-existing");
                    return;
                }
                catch (Exception ex)
                {
                    // The port answered, but the old Observer session is not
                    // actually usable. Treat it as stale and rebuild cleanly.
                    _chromeLog.Warn($"[COMMENT_OBSERVER_ATTACH_READY_FAILED] {ex.GetType().Name}: {ex.Message}");
                    try { await _chrome.DisconnectAsync(TimeSpan.FromSeconds(1)); } catch { }
                }
            }

            Exception? lastError = null;
            const int maxAttempts = 2;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // If CDP is not attachable but this exact dedicated profile is
                // still owned by chrome.exe, it is an orphan/stale Observer from
                // a failed launch. Clean ONLY this profile before launching again.
                var owners = _chrome.DescribeProfileOwners(_userDataDir);
                if (!string.IsNullOrWhiteSpace(owners))
                {
                    _chromeLog.Warn(
                        $"[COMMENT_OBSERVER_STALE_PROFILE] attempt={attempt}/{maxAttempts} " +
                        $"profile={_userDataDir} owners={owners}");

                    var remaining = await _chrome.ForceCleanupOwnedProfileProcessesAsync(_userDataDir, _port);
                    if (remaining.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Không thể dọn Chrome Observer cũ đang giữ profile. " +
                            $"ProfilePath={_userDataDir}. PID còn lại={string.Join(',', remaining)}");
                    }
                }

                try
                {
                    _chromeLog.Info($"[COMMENT_OBSERVER_START_ATTEMPT] attempt={attempt}/{maxAttempts} port={_port} profile={_userDataDir}");

                    // Reuse the exact launch/profile/CDP lifecycle already used
                    // by the main tool, but with an isolated controller/port/profile.
                    await _chrome.LaunchAsync(_port, _userDataDir);
                    await _chrome.ConnectAsync(_port);

                    if (!_chrome.Connected)
                        throw new InvalidOperationException("Chrome Observer đã mở nhưng ChromeController chưa kết nối CDP.");

                    await CompleteReadyCoreAsync($"launch-attempt-{attempt}");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _chromeLog.Warn(
                        $"[COMMENT_OBSERVER_START_FAILED] attempt={attempt}/{maxAttempts} " +
                        $"error={ex.GetType().Name}: {ex.Message}");

                    // Critical recovery: LaunchAsync can leave Chrome processes
                    // alive when CDP never becomes ready. If they are not cleaned,
                    // the next click always fails with "profile đang được sử dụng".
                    var remaining = await _chrome.ForceCleanupOwnedProfileProcessesAsync(_userDataDir, _port);
                    if (remaining.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Chrome Observer mở lỗi và không thể dọn sạch tiến trình cũ. " +
                            $"ProfilePath={_userDataDir}. PID còn lại={string.Join(',', remaining)}",
                            ex);
                    }

                    if (attempt < maxAttempts)
                    {
                        _chromeLog.Warn("[COMMENT_OBSERVER_START_RETRY] stale Observer đã được dọn; chờ 800ms rồi thử lại đúng 1 lần.");
                        await Task.Delay(800);
                    }
                }
            }

            throw new InvalidOperationException(
                "Không mở/kết nối được Chrome Observer sau 2 lần thử. Các tiến trình Observer lỗi đã được dọn sạch để lần sau có thể thử lại.",
                lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<bool> TryAttachExistingCoreAsync()
    {
        try
        {
            var version = await _chrome.GetVersionAsync(_port);
            if (string.IsNullOrWhiteSpace(version.WebSocketDebuggerUrl)) return false;

            await _chrome.ConnectAsync(_port);
            if (!_chrome.Connected) return false;

            _chromeLog.Info($"[COMMENT_OBSERVER_ATTACH_EXISTING_OK] port={_port} profile={_userDataDir}");
            return true;
        }
        catch (Exception ex)
        {
            _chromeLog.Warn($"[COMMENT_OBSERVER_ATTACH_EXISTING_FAIL] port={_port} error={ex.GetType().Name}: {ex.Message}");
            try { await _chrome.DisconnectAsync(TimeSpan.FromSeconds(1)); } catch { }
            return false;
        }
    }

    async Task CompleteReadyCoreAsync(string source)
    {
        if (!_chrome.Connected)
            throw new InvalidOperationException("Chrome Observer chưa kết nối CDP.");

        await RefreshCurrentUrlCoreAsync();

        // Do NOT force /live when the user only opens the Observer. The monitor
        // navigates to the concrete target LIVE after checking has started.
        _chromeLog.Info($"[COMMENT_OBSERVER_READY] source={source} port={_port} currentUrl={_currentLiveUrl}");
        await InstallDomObserverCoreAsync();
    }

    public async Task<bool> EnsureLiveAsync(string liveUrl)
    {
        var wanted = NormalizeLiveUrl(liveUrl);
        if (wanted.Length == 0) return false;

        await EnsureStartedAsync();

        await _gate.WaitAsync();
        try
        {
            if (!_chrome.Connected) return false;

            await RefreshCurrentUrlCoreAsync();
            if (!string.Equals(NormalizeLiveUrl(_currentLiveUrl), wanted, StringComparison.OrdinalIgnoreCase))
            {
                await _chrome.NavigateAndWaitAsync(wanted, minWaitMs: 900, timeoutMs: 12000);
                await RefreshCurrentUrlCoreAsync();
            }

            if (!string.Equals(NormalizeLiveUrl(_currentLiveUrl), wanted, StringComparison.OrdinalIgnoreCase))
                return false;

            await InstallDomObserverCoreAsync();
            return true;
        }
        catch (Exception ex) when (_chrome.IsCdpSessionLost(ex))
        {
            // One reconnect attempt is isolated to Observer only. It never touches PRF Chrome.
            try
            {
                await _chrome.ReconnectAsync();
                await RefreshCurrentUrlCoreAsync();
                if (!string.Equals(NormalizeLiveUrl(_currentLiveUrl), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    await _chrome.NavigateAndWaitAsync(wanted, minWaitMs: 900, timeoutMs: 12000);
                    await RefreshCurrentUrlCoreAsync();
                }
                await InstallDomObserverCoreAsync();
                return string.Equals(NormalizeLiveUrl(_currentLiveUrl), wanted, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UserSearchProbe> SearchUserExactAsync(
        string username,
        CancellationToken ct = default)
    {
        var target = (username ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (target.Length == 0)
            return new UserSearchProbe(UserSearchState.Unknown, "username trống", 0);

        await EnsureStartedAsync();
        await _gate.WaitAsync(ct);
        try
        {
            if (!_chrome.Connected)
                return new UserSearchProbe(UserSearchState.Unknown, "Observer chưa kết nối", 0);

            var url = "https://www.tiktok.com/search/user?q="
                + Uri.EscapeDataString(target)
                + "&t="
                + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await _chrome.NavigateAndWaitAsync(url, minWaitMs: 1100, timeoutMs: 12000);
            await RefreshCurrentUrlCoreAsync();

            const string jsTemplate = """
(() => {
  const target = __TARGET__;
  const norm = v => String(v || '').trim().replace(/^@+/, '').toLocaleLowerCase('en-US');
  const visible = e => {
    if (!e || e.nodeType !== 1) return false;
    const r = e.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    const s = getComputedStyle(e);
    return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity || 1) !== 0;
  };
  const hrefUser = href => {
    try {
      const u = new URL(href || '', location.href);
      const m = u.pathname.match(/^\/@([^/?#]+)/i);
      return m ? norm(decodeURIComponent(m[1])) : '';
    } catch { return ''; }
  };

  const isSearch = /\/search\/user(?:\?|$)/i.test(location.pathname + location.search);
  const main = document.querySelector('#main-content-search_page')
    || document.querySelector('[id*="main-content-search" i]')
    || document.querySelector('main')
    || document.querySelector('[role="main"]');
  const bodyText = String(document.body?.innerText || '');
  const challenge = !!document.querySelector('iframe[src*="captcha" i], [class*="captcha" i], [id*="captcha" i]')
    || /verify to continue|security verification|xác minh để tiếp tục|captcha/i.test(bodyText);

  if (!main) {
    return { isSearch, challenge, ready:false, found:false, count:0, noResult:false, detail:'main_not_ready' };
  }

  const anchors = [...main.querySelectorAll('a[href*="/@"]')].filter(visible);
  const users = [...new Set(anchors.map(a => hrefUser(a.href || a.getAttribute('href'))).filter(Boolean))];
  const resultItems = main.querySelectorAll('[data-e2e*="search-user" i], [data-e2e*="user-item" i]');
  const text = String(main.innerText || main.textContent || '');
  const lines = text.split(/\n+/).map(norm).filter(Boolean);
  const found = users.includes(target) || lines.includes(target) || lines.includes('@' + target);
  const noResult = /không tìm thấy|không có kết quả|no results|couldn['’]t find|try another search/i.test(text);
  const ready = document.readyState !== 'loading' && (found || users.length > 0 || resultItems.length > 0 || noResult);

  return {
    isSearch,
    challenge,
    ready,
    found,
    count: users.length,
    noResult,
    detail: found ? 'exact_found' : (noResult ? 'no_result' : (users.length > 0 ? 'result_list_ready' : 'waiting'))
  };
})()
""";

            var script = jsTemplate.Replace("__TARGET__", JsonSerializer.Serialize(target));
            DateTime readySinceUtc = DateTime.MinValue;
            var lastDetail = "waiting";
            var lastCount = 0;

            for (var i = 0; i < 18; i++)
            {
                ct.ThrowIfCancellationRequested();
                var value = await EvalValueCoreAsync(script);
                if (value.ValueKind == JsonValueKind.Object)
                {
                    static bool B(JsonElement v, string name)
                        => v.TryGetProperty(name, out var p)
                           && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                           && p.GetBoolean();
                    static int I(JsonElement v, string name)
                        => v.TryGetProperty(name, out var p) && p.TryGetInt32(out var x) ? x : 0;
                    static string S(JsonElement v, string name)
                        => v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString() ?? ""
                            : "";

                    var isSearch = B(value, "isSearch");
                    var challenge = B(value, "challenge");
                    var ready = B(value, "ready");
                    var found = B(value, "found");
                    var noResult = B(value, "noResult");
                    lastCount = I(value, "count");
                    lastDetail = S(value, "detail");

                    if (challenge)
                        return new UserSearchProbe(UserSearchState.Unknown, "TikTok challenge/CAPTCHA", lastCount);
                    if (!isSearch)
                        return new UserSearchProbe(UserSearchState.Unknown, "không ở trang Search Người dùng", lastCount);
                    if (found)
                        return new UserSearchProbe(UserSearchState.Found, "exact username", lastCount);

                    if (ready)
                    {
                        if (readySinceUtc == DateTime.MinValue)
                            readySinceUtc = DateTime.UtcNow;

                        // Không kết luận quá sớm khi TikTok mới render một phần danh sách.
                        // QUAN TRỌNG CHECK BAN:
                        // Màn TikTok "Không tìm thấy kết quả dành cho ..." có thể là lỗi Search tạm thời,
                        // KHÔNG được coi là bằng chứng BAN. Trả Unknown để BanCheckForm không note Excel;
                        // lần chạy sau dòng này vẫn còn trống và sẽ được check lại.
                        var stableFor = DateTime.UtcNow - readySinceUtc;
                        if (noResult && stableFor >= TimeSpan.FromSeconds(2.0))
                        {
                            return new UserSearchProbe(
                                UserSearchState.Unknown,
                                "TIKTOK_SEARCH_INVALID_NO_RESULT_PAGE",
                                lastCount);
                        }

                        // Chỉ khi TikTok thực sự trả một danh sách Search đã ổn định nhưng không có
                        // exact username thì mới kết luận NotFound => CHECK BAN được phép ghi ban.
                        if (!noResult && stableFor >= TimeSpan.FromSeconds(3.5))
                        {
                            return new UserSearchProbe(
                                UserSearchState.NotFound,
                                "danh sách đã load nhưng không có exact username",
                                lastCount);
                        }
                    }
                    else
                    {
                        readySinceUtc = DateTime.MinValue;
                    }
                }

                await Task.Delay(500, ct);
            }

            return new UserSearchProbe(UserSearchState.Unknown, "Search chưa ổn định: " + lastDetail, lastCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (_chrome.IsCdpSessionLost(ex))
        {
            try { await _chrome.ReconnectAsync(); } catch { }
            return new UserSearchProbe(UserSearchState.Unknown, "CDP_SESSION_LOST", 0);
        }
        catch (Exception ex)
        {
            return new UserSearchProbe(UserSearchState.Unknown, ex.GetType().Name + ": " + ex.Message, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ArmAsync(string key, string username, string content)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_chrome.Connected) return false;
            await InstallDomObserverCoreAsync();
            var expr = $"window.__ttcc.arm({JsonSerializer.Serialize(key)},{JsonSerializer.Serialize(username ?? "")},{JsonSerializer.Serialize(content ?? "")})";
            return await EvalBoolCoreAsync(expr);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ObserverMatch> CheckAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_chrome.Connected) return new ObserverMatch(false, false, "", "", 0);
            var value = await EvalValueCoreAsync($"window.__ttcc?.check({JsonSerializer.Serialize(key)}) ?? null");
            if (value.ValueKind != JsonValueKind.Object)
                return new ObserverMatch(false, false, "", "", 0);

            static string S(JsonElement v, string n)
                => v.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            static long L(JsonElement v, string n)
                => v.TryGetProperty(n, out var p) && p.TryGetInt64(out var x) ? x : 0;
            static bool B(JsonElement v, string n)
                => v.TryGetProperty(n, out var p)
                    && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
                    && p.GetBoolean();

            return new ObserverMatch(B(value, "exists"), B(value, "matched"), S(value, "mode"), S(value, "sample"), L(value, "matchedAt"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (_chrome.Connected)
                await EvalValueCoreAsync($"window.__ttcc?.clear({JsonSerializer.Serialize(key)})");
        }
        catch { }
        finally
        {
            _gate.Release();
        }
    }

    async Task RefreshCurrentUrlCoreAsync()
    {
        if (!_chrome.Connected)
        {
            _currentLiveUrl = "";
            return;
        }

        var href = await EvalStringCoreAsync("location.href");
        if (!string.IsNullOrWhiteSpace(href))
            _currentLiveUrl = href;
    }

    async Task InstallDomObserverCoreAsync()
    {
        if (!_chrome.Connected) return;
        const string js = """
(() => {
  if (window.__ttcc && window.__ttcc.version === 4) return true;
  try { window.__ttcc?.shutdown?.(); } catch {}

  const norm = v => String(v || '').replace(/\s+/g,' ').trim().toLocaleLowerCase('vi-VN');
  const userNorm = v => norm(v).replace(/^@+/,'');
  const targets = new Map();
  const claimedRows = new WeakMap();
  let observer = null;
  let pollTimer = null;

  const handle = href => {
    try {
      const u = new URL(href || '', location.href);
      const m = u.pathname.match(/^\/@([^/?#]+)/i);
      return m ? decodeURIComponent(m[1]).toLocaleLowerCase('en-US') : '';
    } catch { return ''; }
  };

  const visible = e => {
    if (!e || e.nodeType !== 1) return false;
    const s = getComputedStyle(e);
    const r = e.getBoundingClientRect();
    return s.display !== 'none'
      && s.visibility !== 'hidden'
      && Number(s.opacity || 1) !== 0
      && r.width > 0
      && r.height > 0;
  };

  const allChatRows = () => [...document.querySelectorAll('[data-e2e="chat-message"]')];

  const contentNodeOf = row => {
    if (!row?.querySelector) return null;
    return row.querySelector('[class~="w-full"][class~="break-words"][class~="align-middle"]');
  };

  const ownerNodeOf = row => row?.querySelector?.('[data-e2e="message-owner-name"]') || null;

  const rowIndexOf = row => {
    const wrap = row?.closest?.('[data-index]');
    const raw = wrap?.getAttribute?.('data-index') || '';
    const n = Number.parseInt(raw, 10);
    return Number.isFinite(n) ? n : -1;
  };

  const rowContent = row => norm(contentNodeOf(row)?.textContent || '');

  const rowSample = row => {
    const owner = ownerNodeOf(row);
    const ownerText = String(owner?.getAttribute?.('title') || owner?.textContent || '').replace(/\s+/g,' ').trim();
    const contentText = String(contentNodeOf(row)?.innerText || contentNodeOf(row)?.textContent || '').replace(/\s+/g,' ').trim();
    return (`owner=${ownerText} | content=${contentText}`).slice(0, 500);
  };

  const explicitHandles = row => {
    if (!row?.querySelectorAll) return [];
    const out = [];
    for (const a of row.querySelectorAll('a[href*="/@"]')) {
      const h = handle(a.href || a.getAttribute('href'));
      if (h) out.push(h);
    }
    return [...new Set(out)];
  };

  const ownerContainsExpected = (row, expected) => {
    if (!expected) return false;
    const owner = ownerNodeOf(row);
    if (!owner) return false;
    const values = [
      owner.getAttribute?.('title') || '',
      owner.getAttribute?.('aria-label') || '',
      owner.getAttribute?.('data-unique-id') || '',
      owner.getAttribute?.('data-username') || '',
      owner.textContent || ''
    ].map(userNorm).filter(Boolean);
    return values.some(v => v === expected || v.includes('@' + expected) || v.includes(expected));
  };

  // TikTok LIVE hiện tại chỉ render nickname ở data-e2e=message-owner-name,
  // không luôn expose @uniqueId trong DOM. Thử đọc React props/fiber một cách giới hạn
  // để xác minh handle nếu TikTok vẫn giữ uniqueId trong props nội bộ.
  const reactContainsExpected = (row, expected) => {
    if (!row || !expected) return false;
    const roots = [];
    const collect = el => {
      if (!el) return;
      for (const k of Object.keys(el)) {
        if (k.startsWith('__reactProps$') || k.startsWith('__reactFiber$')) {
          try { roots.push(el[k]); } catch {}
        }
      }
    };
    collect(row);
    collect(ownerNodeOf(row));
    if (roots.length === 0) return false;

    const seen = new WeakSet();
    let budget = 450;
    const interestingKey = k => /unique.?id|username|user.?name|handle|author|owner|user/i.test(String(k || ''));

    const walk = (v, depth, keyHint='') => {
      if (budget-- <= 0 || depth > 6 || v == null) return false;
      if (typeof v === 'string') {
        const x = userNorm(v);
        if (!x) return false;
        if (x === expected || x === '@' + expected) return true;
        if (interestingKey(keyHint) && (x.includes('@' + expected) || x === expected)) return true;
        return false;
      }
      if (typeof v !== 'object') return false;
      if (seen.has(v)) return false;
      seen.add(v);

      let entries;
      try { entries = Object.entries(v); } catch { return false; }
      for (const [k,val] of entries) {
        if (k === 'return' || k === 'child' || k === 'sibling' || k === 'stateNode') continue;
        if (walk(val, depth + 1, k)) return true;
      }
      return false;
    };

    for (const root of roots) {
      if (walk(root, 0, '')) return true;
    }
    return false;
  };

  const hasUnmatchedTarget = () => {
    for (const [,t] of targets) if (!t.matched) return true;
    return false;
  };

  const stopIfIdle = () => {
    if (targets.size !== 0 && hasUnmatchedTarget()) return;
    if (observer) {
      try { observer.disconnect(); } catch {}
      observer = null;
    }
    if (pollTimer) {
      try { clearInterval(pollTimer); } catch {}
      pollTimer = null;
    }
  };

  const cleanupExpired = () => {
    const now = Date.now();
    for (const [k,t] of targets) {
      if (now - t.armedAt > 60000) targets.delete(k);
    }
    stopIfIdle();
  };

  const inspectRow = row => {
    if (!row || targets.size === 0 || !visible(row)) return;
    const contentNode = contentNodeOf(row);
    if (!contentNode || !visible(contentNode)) return;

    const content = rowContent(row);
    if (!content) return;

    const claimedBy = claimedRows.get(row) || '';
    const handles = explicitHandles(row);
    const sample = rowSample(row);
    const idx = rowIndexOf(row);

    // Một DOM row chỉ được dùng để xác nhận một pending comment.
    // Chọn target cũ nhất trước nếu có nhiều pending cùng nội dung.
    const candidates = [...targets.entries()]
      .filter(([,t]) => !t.matched && t.content && content === t.content)
      .sort((a,b) => a[1].armedAt - b[1].armedAt);

    for (const [key,t] of candidates) {
      if (claimedBy && claimedBy !== key) continue;
      if (t.baselineMatches?.has?.(row)) continue;

      t.textFound = true;
      t.lastSample = sample;
      t.lastRowIndex = idx;

      let userVerified = false;
      let explicitMismatch = false;

      if (!t.username) {
        userVerified = true;
      } else if (handles.length > 0) {
        userVerified = handles.includes(t.username);
        explicitMismatch = !userVerified;
      } else if (ownerContainsExpected(row, t.username)) {
        userVerified = true;
      } else if (reactContainsExpected(row, t.username)) {
        userVerified = true;
      }

      if (explicitMismatch) {
        t.mode = 'TEXT_FOUND_USER_MISMATCH';
        continue;
      }

      if (userVerified) {
        t.matched = true;
        t.mode = t.username ? 'USER+TEXT' : 'TEXT_ONLY';
      } else {
        // HTML LIVE hiện tại không luôn chứa @handle; chỉ có nickname trong
        // [data-e2e=message-owner-name]. Nếu đúng nội dung xuất hiện ở một row mới
        // sau ARM và không có bằng chứng đây là handle khác, coi là Visible theo
        // temporal row match thay vì âm thầm tính Missing.
        t.matched = true;
        t.mode = 'NEW_ROW+TEXT';
      }

      t.sample = sample;
      t.matchedAt = Date.now();
      claimedRows.set(row, key);
      break;
    }

    stopIfIdle();
  };

  const inspect = node => {
    if (targets.size === 0) return;
    const e = node?.nodeType === 1 ? node : node?.parentElement;
    if (!e) return;

    const direct = e.closest?.('[data-e2e="chat-message"]');
    if (direct) inspectRow(direct);

    if (e.querySelectorAll) {
      const rows = e.matches?.('[data-e2e="chat-message"]')
        ? [e]
        : [...e.querySelectorAll('[data-e2e="chat-message"]')];
      for (const row of rows) inspectRow(row);
    }
  };

  const scanVisibleCandidates = () => {
    if (!hasUnmatchedTarget()) return;
    const rows = allChatRows();
    const start = Math.max(0, rows.length - 180);
    for (let i = start; i < rows.length; i++) inspectRow(rows[i]);
  };

  const ensureWatching = () => {
    if (!hasUnmatchedTarget()) return;

    if (!observer) {
      observer = new MutationObserver(ms => {
        if (targets.size === 0) {
          stopIfIdle();
          return;
        }
        for (const m of ms) {
          if (m.type === 'childList') {
            for (const n of m.addedNodes) inspect(n);
            inspect(m.target);
          } else if (m.type === 'characterData') {
            inspect(m.target);
          }
        }
        cleanupExpired();
      });
      observer.observe(document.body || document.documentElement, {
        subtree:true,
        childList:true,
        characterData:true
      });
    }

    if (!pollTimer) {
      pollTimer = setInterval(() => {
        try { scanVisibleCandidates(); } catch {}
        cleanupExpired();
      }, 300);
    }

    try { scanVisibleCandidates(); } catch {}
  };

  const makeBaselineMatches = content => {
    const set = new WeakSet();
    if (!content) return set;
    for (const row of allChatRows()) {
      if (rowContent(row) === content) set.add(row);
    }
    return set;
  };

  window.__ttcc = {
    version:4,
    arm:(key,username,content) => {
      const normalizedContent = norm(content);
      targets.set(String(key), {
        username:userNorm(username),
        content:normalizedContent,
        armedAt:Date.now(),
        baselineMatches:makeBaselineMatches(normalizedContent),
        matched:false,
        mode:'TEXT_NOT_FOUND',
        sample:'',
        lastSample:'',
        lastRowIndex:-1,
        textFound:false,
        matchedAt:0
      });
      ensureWatching();
      return true;
    },
    check:key => {
      const t=targets.get(String(key));
      if (!t) return {exists:false,matched:false,mode:'',sample:'',matchedAt:0};
      const diagnosticMode = t.matched
        ? (t.mode || '')
        : (t.textFound ? (t.mode || 'TEXT_FOUND_USER_UNVERIFIED') : 'TEXT_NOT_FOUND');
      return {
        exists:true,
        matched:!!t.matched,
        mode:diagnosticMode,
        sample:t.sample || t.lastSample || '',
        matchedAt:t.matchedAt || 0
      };
    },
    clear:key => {
      targets.delete(String(key));
      stopIfIdle();
      return true;
    },
    pendingCount:() => targets.size,
    shutdown:() => {
      try { observer?.disconnect?.(); } catch {}
      observer = null;
      try { if (pollTimer) clearInterval(pollTimer); } catch {}
      pollTimer = null;
      targets.clear();
      return true;
    }
  };
  return true;
})()
""";
        await EvalValueCoreAsync(js);
    }

    async Task<JsonElement> EvalValueCoreAsync(string expression)
    {
        if (!_chrome.Connected) return default;
        var result = await _chrome.EvalAsync(expression, awaitPromise: true);
        if (result.TryGetProperty("value", out var value))
            return value.Clone();
        return default;
    }

    async Task<string> EvalStringCoreAsync(string expression)
    {
        var v = await EvalValueCoreAsync(expression);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    async Task<bool> EvalBoolCoreAsync(string expression)
    {
        var v = await EvalValueCoreAsync(expression);
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _chrome.DisposeAsync(); } catch { }
        try { _chromeLog.Dispose(); } catch { }
        _gate.Dispose();
    }
}

internal sealed record ObserverMatch(bool Exists, bool Matched, string Mode, string Sample, long MatchedAt);

internal enum UserSearchState
{
    Found,
    NotFound,
    Unknown
}

internal sealed record UserSearchProbe(UserSearchState State, string Detail, int ResultCount);
