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
                await RefreshCurrentUrlCoreAsync();
                return;
            }

            // First try to attach to this observer's already-running Chrome on the
            // dedicated port. This preserves the observer login/profile across monitor restarts.
            var attached = false;
            try
            {
                var version = await _chrome.GetVersionAsync(_port);
                if (!string.IsNullOrWhiteSpace(version.WebSocketDebuggerUrl))
                {
                    await _chrome.ConnectAsync(_port);
                    attached = _chrome.Connected;
                }
            }
            catch
            {
                attached = false;
            }

            if (!attached)
            {
                // Reuse the exact launch/profile/CDP lifecycle already used by the main tool,
                // but with an entirely separate controller, port and profile directory.
                await _chrome.LaunchAsync(_port, _userDataDir);
                await _chrome.ConnectAsync(_port);
            }

            if (!_chrome.Connected)
                throw new InvalidOperationException("Chrome Observer đã mở nhưng ChromeController chưa kết nối CDP.");

            await RefreshCurrentUrlCoreAsync();

            // Do NOT force /live when the user only clicks "Mở Chrome Observer".
            // ChromeController launches the isolated observer at the normal TikTok entry page.
            // The monitor navigates to a concrete target LIVE only after checking is started
            // and telemetry tells us which PRF/LIVE is currently being verified.
            _chromeLog.Info($"[COMMENT_OBSERVER_READY] port={_port} currentUrl={_currentLiveUrl}");
            await InstallDomObserverCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
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
                        // Chờ danh sách ổn định rồi mới trả NOT_FOUND; bên ngoài còn xác nhận lần 2.
                        var stableFor = DateTime.UtcNow - readySinceUtc;
                        if ((noResult && stableFor >= TimeSpan.FromSeconds(2.0))
                            || stableFor >= TimeSpan.FromSeconds(3.5))
                        {
                            return new UserSearchProbe(
                                UserSearchState.NotFound,
                                noResult ? "trang báo không có kết quả" : "danh sách đã load nhưng không có exact username",
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
  if (window.__ttcc && window.__ttcc.version === 2) return true;

  const norm = v => String(v || '').replace(/\s+/g,' ').trim().toLocaleLowerCase('vi-VN');
  const userNorm = v => norm(v).replace(/^@+/,'');
  const targets = new Map();
  let observer = null;

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

  const hasUnmatchedTarget = () => {
    for (const [,t] of targets) if (!t.matched) return true;
    return false;
  };

  const stopIfIdle = () => {
    if (!observer || (targets.size !== 0 && hasUnmatchedTarget())) return;
    try { observer.disconnect(); } catch {}
    observer = null;
  };

  const cleanupExpired = () => {
    const now = Date.now();
    for (const [k,t] of targets) {
      if (now - t.armedAt > 60000) targets.delete(k);
    }
    stopIfIdle();
  };

  const inspect = node => {
    if (targets.size === 0) return;

    let e = node?.nodeType === 1 ? node : node?.parentElement;
    for (let depth = 0; e && depth < 7; depth++, e = e.parentElement) {
      // Fast pre-filter: textContent avoids style/layout work for nearly all
      // unrelated high-volume LIVE DOM mutations.
      const quick = norm(e.textContent || '');
      if (!quick || quick.length > 1800) continue;

      const candidates = [];
      for (const [key,t] of targets) {
        if (!t.matched && t.content && quick.includes(t.content)) candidates.push([key,t]);
      }
      if (candidates.length === 0) continue;

      // Only pay the expensive visibility/layout cost after the content can match.
      if (!visible(e)) continue;

      const raw = String(e.innerText || e.textContent || '').replace(/\s+/g,' ').trim();
      if (!raw || raw.length > 1400) continue;
      const text = norm(raw);

      const matchedCandidates = candidates.filter(([,t]) => text.includes(t.content));
      if (matchedCandidates.length === 0) continue;

      let uniq = null;
      const getUsers = () => {
        if (uniq) return uniq;
        const anchors = [...e.querySelectorAll('a[href*="/@"]')]
          .map(a => handle(a.href || a.getAttribute('href')))
          .filter(Boolean);
        if (e.matches?.('a[href*="/@"]')) anchors.push(handle(e.href || e.getAttribute('href')));
        uniq = [...new Set(anchors)];
        return uniq;
      };

      for (const [key,t] of matchedCandidates) {
        let userOk = true;
        let mode = 'TEXT_ONLY';
        if (t.username) {
          const users = getUsers();
          userOk = users.includes(t.username) || text.includes('@' + t.username);
          mode = userOk ? 'USER+TEXT' : '';
        }
        if (!userOk) continue;

        t.matched = true;
        t.mode = mode;
        t.sample = raw.slice(0,500);
        t.matchedAt = Date.now();
      }
      stopIfIdle();
    }
  };

  const ensureWatching = () => {
    if (observer || !hasUnmatchedTarget()) return;
    observer = new MutationObserver(ms => {
      if (targets.size === 0) {
        stopIfIdle();
        return;
      }
      for (const m of ms) {
        for (const n of m.addedNodes) inspect(n);
      }
      cleanupExpired();
    });
    observer.observe(document.body || document.documentElement, {subtree:true, childList:true});
  };

  window.__ttcc = {
    version:2,
    arm:(key,username,content) => {
      targets.set(String(key), {
        username:userNorm(username),
        content:norm(content),
        armedAt:Date.now(),
        matched:false,
        mode:'',
        sample:'',
        matchedAt:0
      });
      ensureWatching();
      return true;
    },
    check:key => {
      const t=targets.get(String(key));
      return t
        ? {exists:true,matched:!!t.matched,mode:t.mode||'',sample:t.sample||'',matchedAt:t.matchedAt||0}
        : {exists:false,matched:false,mode:'',sample:'',matchedAt:0};
    },
    clear:key => {
      targets.delete(String(key));
      stopIfIdle();
      return true;
    },
    pendingCount:() => targets.size
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
