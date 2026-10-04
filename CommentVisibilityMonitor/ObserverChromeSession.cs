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
