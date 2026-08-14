using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToolTikTokV11.Utils;
using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed record CdpPage(string Id, string Title, string Url, string WebSocketDebuggerUrl);
public sealed record DomBox(double X, double Y, double Width, double Height);
public sealed record CdpVersionInfo(string Browser, string WebSocketDebuggerUrl);
public sealed record ManagedChromeCloseResult(bool WasRunning, bool Closed, IReadOnlyList<int> RemainingPids, bool CdpReady, string Method);
public enum ChromeWindowState { NotFound, Visible, Minimized }

public sealed class ChromeController : IAsyncDisposable
{
    sealed record ProfileOwner(int ProcessId, string CommandLine);

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    const int AfInet = 2;
    const int TcpTableOwnerPidListener = 3;
    const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool order, int ipVersion, int tableClass, uint reserved);

    readonly Logger _log;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    CdpClient? _cdp;
    int _port;
    const string TikTokUrl = "https://www.tiktok.com/";
    string _managedProfileDir = "";
    int _managedWindowPort;
    readonly HashSet<int> _managedPids = [];
    readonly SemaphoreSlim _manualCloseGate = new(1, 1);
    IntPtr _managedWindowHandle;
    VmOptimizationSettings _vmOptimization = new();

    public CdpPage? Page { get; private set; }
    public bool Connected => _cdp?.Connected == true;

    /// <summary>
    /// A DOM/XPath miss is not a disconnected browser.  Keep this test deliberately
    /// narrow so normal TikTok re-renders do not make the worker report CDP loss.
    /// </summary>
    public bool IsCdpSessionLost(Exception ex)
    {
        if (!Connected) return true;

        var text = ex.ToString();
        return text.Contains("[CDP_SESSION_LOST]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WebSocket is not connected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WebSocketException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("remote party closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such target", StringComparison.OrdinalIgnoreCase)
            || text.Contains("target closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("target was closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("session not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("session closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("inspected target navigated or closed", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTransientDocumentContextError(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot find context", StringComparison.OrdinalIgnoreCase)
            || text.Contains("context with specified id", StringComparison.OrdinalIgnoreCase);
    }

    public ChromeController(Logger log) => _log = log;

    public void ConfigureVmOptimization(VmOptimizationSettings settings)
    {
        _vmOptimization = new VmOptimizationSettings { Mode = settings?.Mode ?? VmOptimizationMode.Normal };
        _log.Info($"[VM_MODE] chrome={_vmOptimization.Mode}");
    }

    static string TrimForLog(string text, int max = 140)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : text[..max] + "...";
    }

    void LogCdpStart(string action, string detail) => _log.Info($"CDP START {action}: {detail}");
    void LogCdpDone(string action, string detail) => _log.Info($"CDP DONE {action}: {detail}");

    public string FindChrome()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "chrome.exe";
    }

    public async Task LaunchAsync(int port, string profileDir, Action? beforeLaunch = null)
    {
        profileDir = Path.GetFullPath(profileDir);
        if (!Directory.Exists(profileDir))
            throw new InvalidOperationException("Không tìm thấy Chrome profile cố định: " + profileDir);

        await CloseBrowserOnPortAsync(port);
        await WaitForProfileReleaseAsync(profileDir, TimeSpan.FromSeconds(4));
        var owners = await FindChromeProcessesUsingProfileAsync(profileDir, TimeSpan.FromSeconds(4));
        if (owners.Count > 0)
        {
            var detail = string.Join(" | ", owners.Select(x => $"PID={x.ProcessId}"));
            throw new InvalidOperationException($"Chrome profile đang được sử dụng bởi tiến trình khác. ProfilePath={profileDir}. {detail}");
        }

        // The caller may safely update Preferences here: the exact profile has
        // been released and Chrome has not been started again yet.
        beforeLaunch?.Invoke();

        var chrome = FindChrome();
        var backgroundFlags = _vmOptimization.AllowChromeBackgroundThrottling
            ? ""
            : "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding ";
        var args =
            $"--remote-debugging-port={port} --remote-allow-origins=* --user-data-dir=\"{profileDir}\" " +
            "--no-first-run --no-default-browser-check " + backgroundFlags +
            "--window-size=1600,1000 " + TikTokUrl;
        var psi = new ProcessStartInfo(chrome, args)
        {
            UseShellExecute = true,
            CreateNoWindow = false
        };

        _log.Info($"Launching Chrome user-data-dir={profileDir}");
        var launched = Process.Start(psi);
        CacheManagedLaunch(profileDir, port, launched?.Id);
        _log.Info($"Đã mở Chrome V13 ở chế độ HIỂN THỊ; cổng CDP={port}; profile={profileDir}");
        await WaitForCdpReadyAsync(port, TimeSpan.FromSeconds(10));
        await EnsureTikTokTargetAsync(port, preferredId: null, timeout: TimeSpan.FromSeconds(8));
    }

    void CacheManagedLaunch(string profileDir, int port, int? pid)
    {
        _managedProfileDir = Path.GetFullPath(profileDir);
        _managedWindowPort = port;
        _managedWindowHandle = IntPtr.Zero;
        _managedPids.Clear();
        if (pid is > 0) _managedPids.Add(pid.Value);
    }

    async Task WaitForProfileReleaseAsync(string profileDir, TimeSpan timeout)
    {
        // Query command lines once, then wait on the exact PIDs we found.  The
        // old loop spawned powershell.exe + CIM every 250 ms.  LaunchAsync still
        // performs a final owner query, so a newly acquired profile is detected.
        var end = DateTime.UtcNow + timeout;
        var owners = await FindChromeProcessesUsingProfileAsync(profileDir, timeout);
        if (owners.Count == 0) return;

        while (DateTime.UtcNow < end)
        {
            if (owners.All(owner => !IsProcessRunning(owner.ProcessId))) return;
            await Task.Delay(250);
        }
    }

    async Task<List<ProfileOwner>> FindChromeProcessesUsingProfileAsync(string profileDir, TimeSpan timeout)
    {
        try
        {
            using var p = CreateProfileOwnerQuery(profileDir);
            p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try { p.Kill(true); } catch { }
                _log.Warn("Kiểm tra tiến trình Chrome đang giữ profile bị timeout; bỏ qua kiểm tra owner.");
                return [];
            }

            var output = (await outputTask).Trim();
            var err = (await errorTask).Trim();
            if (p.ExitCode != 0)
            {
                _log.Warn("Kiểm tra owner của Chrome profile thất bại: " + err);
                return [];
            }
            return ParseProfileOwners(output);
        }
        catch (Exception ex)
        {
            _log.Warn("Không kiểm tra được owner của Chrome profile: " + ex.Message);
            return [];
        }
    }

    List<ProfileOwner> FindChromeProcessesUsingProfile(string profileDir)
    {
        try
        {
            using var p = CreateProfileOwnerQuery(profileDir);
            p.Start();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(true); } catch { }
                _log.Warn("Kiểm tra tiến trình Chrome đang giữ profile bị timeout; bỏ qua kiểm tra owner.");
                return [];
            }

            var output = p.StandardOutput.ReadToEnd().Trim();
            var err = p.StandardError.ReadToEnd().Trim();
            if (p.ExitCode != 0)
            {
                _log.Warn("Kiểm tra owner của Chrome profile thất bại: " + err);
                return [];
            }
            return ParseProfileOwners(output);
        }
        catch (Exception ex)
        {
            _log.Warn("Không kiểm tra được owner của Chrome profile: " + ex.Message);
            return [];
        }
    }

    static Process CreateProfileOwnerQuery(string profileDir)
    {
        var p = new Process();
        p.StartInfo = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
            "\"$t=$env:TARGET_PROFILE; " +
            "$items=Get-CimInstance Win32_Process -Filter \\\"Name = 'chrome.exe'\\\" | " +
            "Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($t, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } | " +
            "Select-Object ProcessId, CommandLine; " +
            "if($items){ $items | ConvertTo-Json -Compress }\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        p.StartInfo.Environment["TARGET_PROFILE"] = profileDir;
        return p;
    }

    static List<ProfileOwner> ParseProfileOwners(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var doc = JsonDocument.Parse(output);
        var list = new List<ProfileOwner>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            ReadOwner(doc.RootElement, list);
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var item in doc.RootElement.EnumerateArray()) ReadOwner(item, list);
        return list;
    }

    static void ReadOwner(JsonElement item, List<ProfileOwner> list)
    {
        if (!item.TryGetProperty("ProcessId", out var pidEl)) return;
        int pid = pidEl.ValueKind == JsonValueKind.Number ? pidEl.GetInt32() : 0;
        string commandLine = item.TryGetProperty("CommandLine", out var cmdEl) ? cmdEl.GetString() ?? "" : "";
        if (pid > 0) list.Add(new ProfileOwner(pid, commandLine));
    }

    List<int> FindChromeProcessIds(string profileDir, int port)
    {
        var normalized = Path.GetFullPath(profileDir);
        return FindChromeProcessesUsingProfile(normalized)
            .Where(x => x.CommandLine.IndexOf($"--remote-debugging-port={port}", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => x.ProcessId)
            .Distinct()
            .ToList();
    }

    bool IsManagedContext(string profileDir, int port)
        => _managedWindowPort == port
        && !string.IsNullOrWhiteSpace(_managedProfileDir)
        && Path.GetFullPath(profileDir).Equals(_managedProfileDir, StringComparison.OrdinalIgnoreCase);

    bool IsLiveWindowHandle(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public string? DescribeProfileOwners(string profileDir)
    {
        var owners = FindChromeProcessesUsingProfile(Path.GetFullPath(profileDir));
        if (owners.Count == 0) return null;
        return string.Join(" | ", owners.Select(x => $"PID={x.ProcessId}"));
    }

    public void AttachManagedWindow(string profileDir, int port)
    {
        if (string.IsNullOrWhiteSpace(profileDir)) return;
        var sw = Stopwatch.StartNew();
        _managedProfileDir = Path.GetFullPath(profileDir);
        _managedWindowPort = port;
        if (_managedPids.Count == 0)
            foreach (var pid in FindChromeProcessIds(_managedProfileDir, port))
                _managedPids.Add(pid);
        if (!IsLiveWindowHandle(_managedWindowHandle))
            _managedWindowHandle = DiscoverManagedWindowHandle();
        sw.Stop();
        _log.Info($"[PERF] Chrome window discovery: {sw.ElapsedMilliseconds} ms");
    }

    public async Task CloseBrowserOnPortAsync(int port)
    {
        try
        {
            await DisconnectAsync();
            var pages = await GetPagesAsync(port);
            var page = pages.FirstOrDefault();
            if (page is null || string.IsNullOrWhiteSpace(page.WebSocketDebuggerUrl)) return;
            await using var temp = new CdpClient();
            await temp.ConnectAsync(page.WebSocketDebuggerUrl);
            try { await temp.CallAsync("Browser.close"); } catch { }
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(150);
                try { if ((await GetPagesAsync(port)).Count == 0) break; } catch { break; }
            }
        }
        catch { }
    }

    /// <summary>
    /// Manual close path.  It never launches Chrome or probes arbitrary chrome.exe
    /// instances.  The CDP listener and exact --user-data-dir are the ownership
    /// checks before any process-level fallback is allowed.
    /// </summary>
    public async Task<ManagedChromeCloseResult> CloseManagedBrowserAsync(string profileDir, int port, bool manualRequest = true)
    {
        if (!manualRequest) throw new InvalidOperationException("Manual close phải được gọi với manualRequest=true.");

        profileDir = NormalizeProfilePath(profileDir);
        await _manualCloseGate.WaitAsync();
        try
        {
            return await CloseManualRequestAsync(profileDir, port);
        }
        finally
        {
            _manualCloseGate.Release();
        }
    }

    async Task<ManagedChromeCloseResult> CloseManualRequestAsync(string profileDir, int port)
    {
        _log.Info($"[CHROME_CLOSE_REQUEST] profilePath={profileDir} cdpPort={port}");

        var cdpReadyAtStart = await IsCdpReadyAsync(port);
        var activeOwnedSession = cdpReadyAtStart && Connected && IsManagedContext(profileDir, port);
        var listenerPid = TryGetListeningProcessId(port);
        ProfileOwner? verifiedOwner = null;

        // Keep the active managed CDP session as the preferred Browser.close
        // route, but also capture the exact listener ownership for final PID
        // verification and for any CloseMainWindow/Kill fallback.
        if (listenerPid is > 0)
            verifiedOwner = await InspectExistingChromeProcessAsync(listenerPid.Value, profileDir);

        _log.Info($"[CHROME_CLOSE_STATE] cdpReady={cdpReadyAtStart} activeOwnedSession={activeOwnedSession} listenerPid={(listenerPid?.ToString() ?? "-")}");
        if (verifiedOwner is not null)
            _log.Info($"[CHROME_CLOSE_OWNER_VERIFY] pid={verifiedOwner.ProcessId} matched=True profilePath={profileDir}");

        if (!cdpReadyAtStart && listenerPid is null)
        {
            _log.Info("[CHROME_CLOSE_GRACEFUL] method=Browser.close attempted=False reason=cdp-not-ready");
            return await BuildManualCloseResultAsync(false, port, [], "not-running");
        }

        var canUseCdp = cdpReadyAtStart && (activeOwnedSession || verifiedOwner is not null);
        var browserCloseSent = false;
        string? browserCloseError = null;
        if (canUseCdp)
        {
            try
            {
                if (activeOwnedSession)
                {
                    await Cdp.CallAsync("Browser.close");
                }
                else
                {
                    var page = (await GetPagesAsync(port)).FirstOrDefault();
                    if (page is null || string.IsNullOrWhiteSpace(page.WebSocketDebuggerUrl))
                        throw new InvalidOperationException("CDP không trả về page target để gửi Browser.close.");
                    await using var cdp = new CdpClient();
                    await cdp.ConnectAsync(page.WebSocketDebuggerUrl);
                    await cdp.CallAsync("Browser.close");
                }
                browserCloseSent = true;
            }
            catch (Exception ex)
            {
                browserCloseError = ex.Message;
            }
            finally
            {
                await DisconnectAsync(TimeSpan.FromSeconds(1));
            }
        }
        _log.Info($"[CHROME_CLOSE_GRACEFUL] method=Browser.close attempted={canUseCdp} result={(browserCloseSent ? "Success" : "Failed")}{(string.IsNullOrWhiteSpace(browserCloseError) ? "" : " error=" + TrimForLog(browserCloseError))}");

        var verifiedPids = verifiedOwner is null ? [] : new[] { verifiedOwner.ProcessId };
        if (canUseCdp && await WaitForCdpStoppedAsync(port, TimeSpan.FromSeconds(3)))
        {
            await WaitForVerifiedPidsStoppedAsync(verifiedPids, TimeSpan.FromSeconds(2));
            return await BuildManualCloseResultAsync(true, port, verifiedPids, "Browser.close");
        }

        // A remaining CDP listener is never enough to close a process.  Refuse
        // the fallback unless the listener PID was verified against profilePath.
        if (verifiedOwner is null)
        {
            _log.Warn($"[CHROME_CLOSE_OWNER_UNKNOWN] port={port} profilePath={profileDir} cdpReady={await IsCdpReadyAsync(port)}");
            return await BuildManualCloseResultAsync(cdpReadyAtStart || listenerPid is not null, port, [], "owner-unknown");
        }

        try
        {
            using var process = Process.GetProcessById(verifiedOwner.ProcessId);
            var sent = process.CloseMainWindow();
            _log.Info($"[CHROME_CLOSE_GRACEFUL] method=CloseMainWindow pid={verifiedOwner.ProcessId} result={sent}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[CHROME_CLOSE_GRACEFUL] method=CloseMainWindow pid={verifiedOwner.ProcessId} result=failed message={TrimForLog(ex.Message)}");
        }

        if (await WaitForCdpStoppedAsync(port, TimeSpan.FromSeconds(2)))
        {
            await WaitForVerifiedPidsStoppedAsync(verifiedPids, TimeSpan.FromSeconds(2));
            return await BuildManualCloseResultAsync(true, port, verifiedPids, "CloseMainWindow");
        }

        // Last resort: only this exact, verified profile listener's process tree.
        try
        {
            using var process = Process.GetProcessById(verifiedOwner.ProcessId);
            process.Kill(entireProcessTree: true);
            _log.Info($"[CHROME_CLOSE_FORCE] pid={verifiedOwner.ProcessId} result=sent");
        }
        catch (Exception ex)
        {
            _log.Warn($"[CHROME_CLOSE_FORCE] pid={verifiedOwner.ProcessId} result=failed message={TrimForLog(ex.Message)}");
        }

        await WaitForCdpStoppedAsync(port, TimeSpan.FromSeconds(3));
        await WaitForVerifiedPidsStoppedAsync(verifiedPids, TimeSpan.FromSeconds(3));
        return await BuildManualCloseResultAsync(true, port, verifiedPids, "ForceKillProfileTree");
    }

    async Task<ManagedChromeCloseResult> BuildManualCloseResultAsync(bool wasRunning, int port, IReadOnlyList<int> verifiedPids, string method)
    {
        var cdpReady = await IsCdpReadyAsync(port);
        var remaining = verifiedPids.Where(IsProcessRunning).Distinct().ToList();
        var closed = !cdpReady && remaining.Count == 0;
        _log.Info($"[CHROME_CLOSE_VERIFY] cdpReady={cdpReady} remainingPids={string.Join(',', remaining)} result={(closed ? "Closed" : "NotClosed")} method={method}");
        if (closed && _managedWindowPort == port)
        {
            _managedPids.Clear();
            _managedWindowHandle = IntPtr.Zero;
        }
        return new ManagedChromeCloseResult(wasRunning, closed, remaining, cdpReady, method);
    }

    static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    async Task<bool> WaitForCdpStoppedAsync(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            if (!await IsCdpReadyAsync(port)) return true;
            await Task.Delay(150);
        }
        while (DateTime.UtcNow < until);
        return !await IsCdpReadyAsync(port);
    }

    static async Task WaitForVerifiedPidsStoppedAsync(IReadOnlyList<int> verifiedPids, TimeSpan timeout)
    {
        if (verifiedPids.Count == 0) return;
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (verifiedPids.All(pid => !IsProcessRunning(pid))) return;
            await Task.Delay(150);
        }
    }

    static int? TryGetListeningProcessId(int port)
    {
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != ErrorInsufficientBuffer || size <= sizeof(int))
            return null;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return null;
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(table, sizeof(int) + i * rowSize));
                var localPort = (ushort)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                if (localPort == port && row.ProcessId > 0) return unchecked((int)row.ProcessId);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    async Task<ProfileOwner?> InspectExistingChromeProcessAsync(int pid, string profileDir)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return null;
            using var query = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$p=Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}'; if($p){{ [Console]::Out.Write($p.CommandLine) }}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            query.Start();
            var outputTask = query.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            try
            {
                await query.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try { query.Kill(entireProcessTree: true); } catch { }
                _log.Warn($"[CHROME_CLOSE_OWNER_VERIFY] pid={pid} matched=False reason=lookup-timeout");
                return null;
            }

            var commandLine = (await outputTask).Trim();
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            var owner = new ProfileOwner(pid, commandLine);
            var matched = CommandLineUsesProfile(owner.CommandLine, profileDir);
            if (!matched) _log.Info($"[CHROME_CLOSE_OWNER_VERIFY] pid={pid} matched=False");
            return matched ? owner : null;
        }
        catch (Exception ex)
        {
            _log.Warn($"[CHROME_CLOSE_OWNER_VERIFY] pid={pid} matched=False reason={TrimForLog(ex.Message)}");
            return null;
        }
    }

    async Task<bool> IsCdpReadyAsync(int port)
    {
        try { return !string.IsNullOrWhiteSpace((await GetVersionAsync(port)).WebSocketDebuggerUrl); }
        catch { return false; }
    }

    static bool CommandLineUsesProfile(string commandLine, string profileDir)
    {
        var target = NormalizeProfilePath(profileDir);
        var match = Regex.Match(commandLine ?? "", "--user-data-dir(?:=|\\s+)(?:\\\"(?<value>[^\\\"]+)\\\"|'(?<single>[^']+)'|(?<bare>[^\\s]+))", RegexOptions.IgnoreCase);
        var raw = match.Groups["value"].Success ? match.Groups["value"].Value
            : match.Groups["single"].Success ? match.Groups["single"].Value
            : match.Groups["bare"].Value;
        try { return !string.IsNullOrWhiteSpace(raw) && NormalizeProfilePath(raw).Equals(target, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string NormalizeProfilePath(string profileDir)
        => Path.GetFullPath((profileDir ?? string.Empty).Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task<List<CdpPage>> GetPagesAsync(int port)
    {
        var json = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
        using var doc = JsonDocument.Parse(json);
        var pages = new List<CdpPage>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (p.TryGetProperty("type", out var t) && t.GetString() != "page") continue;
            pages.Add(new CdpPage(
                p.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                p.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                p.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                p.TryGetProperty("webSocketDebuggerUrl", out var wsEl) ? wsEl.GetString() ?? "" : ""));
        }
        return pages;
    }

    public async Task<CdpVersionInfo> GetVersionAsync(int port)
    {
        var json = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new CdpVersionInfo(
            root.TryGetProperty("Browser", out var browserEl) ? browserEl.GetString() ?? "" : "",
            root.TryGetProperty("webSocketDebuggerUrl", out var wsEl) ? wsEl.GetString() ?? "" : "");
    }

    async Task WaitForCdpReadyAsync(int port, TimeSpan timeout)
    {
        _log.Info($"CDP_WAIT port={port}");
        var end = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < end)
        {
            try
            {
                var version = await GetVersionAsync(port);
                if (!string.IsNullOrWhiteSpace(version.WebSocketDebuggerUrl))
                {
                    _log.Info("CDP_READY");
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Chrome CDP chưa sẵn sàng trên cổng {port}. {last?.GetType().Name}: {last?.Message}");
    }

    static bool IsUsablePageTarget(CdpPage page)
        => !string.IsNullOrWhiteSpace(page.Id)
        && !string.IsNullOrWhiteSpace(page.WebSocketDebuggerUrl);

    static CdpPage? SelectTarget(List<CdpPage> pages, string? preferredId)
    {
        return pages.FirstOrDefault(p => !string.IsNullOrWhiteSpace(preferredId) && p.Id == preferredId && IsUsablePageTarget(p))
            ?? pages.FirstOrDefault(p => p.Url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) && IsUsablePageTarget(p))
            ?? pages.FirstOrDefault(IsUsablePageTarget);
    }

    async Task OpenNewTabAsync(int port, string url)
    {
        var endpoint = $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(url)}";
        try
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, endpoint);
            using var response = await _http.SendAsync(put);
            response.EnsureSuccessStatusCode();
            return;
        }
        catch
        {
            using var response = await _http.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
        }
    }

    async Task<CdpPage> EnsureTikTokTargetAsync(int port, string? preferredId, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        bool openedTikTok = false;
        Exception? last = null;
        while (DateTime.UtcNow < end)
        {
            try
            {
                var pages = await GetPagesAsync(port);
                _log.Info($"TARGETS_FOUND count={pages.Count}");
                var target = SelectTarget(pages, preferredId);
                if (target is not null)
                {
                    _log.Info($"TIKTOK_TARGET id={target.Id} url={target.Url}");
                    return target;
                }

                if (!openedTikTok)
                {
                    await OpenNewTabAsync(port, TikTokUrl);
                    openedTikTok = true;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(300);
        }

        throw new InvalidOperationException($"[CDP_TARGET_NOT_FOUND] Không tìm thấy tab TikTok trong Chrome. {last?.GetType().Name}: {last?.Message}");
    }

    public async Task ConnectAsync(int port, string? preferredId = null)
    {
        await DisconnectAsync();
        _port = port;
        try
        {
            await WaitForCdpReadyAsync(port, TimeSpan.FromSeconds(15));
            var page = await EnsureTikTokTargetAsync(port, preferredId, TimeSpan.FromSeconds(15));
            if (!IsUsablePageTarget(page))
                throw new InvalidOperationException("[CDP_TARGET_NOT_FOUND] Không tìm thấy tab TikTok trong Chrome.");

            _cdp = new CdpClient();
            await _cdp.ConnectAsync(page.WebSocketDebuggerUrl);
            await _cdp.CallAsync("Runtime.enable");
            await _cdp.CallAsync("Page.enable");
            Page = page;
            AttachManagedWindow(_managedProfileDir, port);
            await ApplyVmRuntimePolicyAsync();
            _log.Info("CDP_CONNECTED");
            _log.Info($"Đã kết nối CDP tới tab: {page.Title} | {page.Url} | chế độ=HIỂN THỊ");
        }
        catch (Exception ex)
        {
            _log.Error($"CDP CONNECT ERROR {ex.GetType().Name} @ {ex.TargetSite?.Name}: {ex.Message}");
            await DisconnectAsync();
            throw;
        }
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (_port <= 0) throw new InvalidOperationException("Chưa có cổng CDP để reconnect.");
        var preferredId = Page?.Id;
        await DisconnectAsync();
        await WaitForCdpReadyAsync(_port, TimeSpan.FromSeconds(15));
        var page = await EnsureTikTokTargetAsync(_port, preferredId, TimeSpan.FromSeconds(15));
        if (!IsUsablePageTarget(page))
            throw new InvalidOperationException("[CDP_TARGET_NOT_FOUND] Không tìm thấy tab TikTok trong Chrome khi reconnect.");

        _cdp = new CdpClient();
        await _cdp.ConnectAsync(page.WebSocketDebuggerUrl, ct);
        await _cdp.CallAsync("Runtime.enable", ct: ct);
        await _cdp.CallAsync("Page.enable", ct: ct);
        Page = page;
        AttachManagedWindow(_managedProfileDir, _port);
        await ApplyVmRuntimePolicyAsync(ct);
        _log.Info("CDP_RECONNECTED");
        _log.Info($"Đã reconnect CDP tới tab: {page.Title} | {page.Url}");
    }

    public async Task DisconnectAsync(TimeSpan? timeout = null)
    {
        var cdp = _cdp;
        _cdp = null;
        Page = null;
        if (cdp is not null)
        {
            var disposeTask = cdp.DisposeAsync().AsTask();
            if (timeout is null) await disposeTask;
            else await Task.WhenAny(disposeTask, Task.Delay(timeout.Value));
        }
    }

    CdpClient Cdp => _cdp ?? throw new InvalidOperationException("Chưa kết nối Chrome V13.");

    static string JsString(string s) => JsonSerializer.Serialize(s);

    public async Task<JsonElement> EvalAsync(string expression, bool awaitPromise = true, CancellationToken ct = default)
    {
        var r = await Cdp.CallAsync("Runtime.evaluate", new
        {
            expression,
            awaitPromise,
            returnByValue = true,
            userGesture = true
        }, ct);
        if (r.TryGetProperty("exceptionDetails", out var ex)) throw new InvalidOperationException("JavaScript lỗi: " + ex);
        return r.GetProperty("result");
    }

    public async Task ApplyVmRuntimePolicyAsync(CancellationToken ct = default)
    {
        if (!Connected) return;

        try
        {
            await Cdp.CallAsync("Network.enable", ct: ct);
            var blocked = _vmOptimization.BlockCommonMedia
                ? new[]
                {
                    "*://*/*.m3u8*", "*://*/*.m4s*", "*://*/*.mp4*", "*://*/*.webm*", "*://*/*.flv*",
                    "*://*/*.ts?*", "*://*/*.ts#*"
                }
                : Array.Empty<string>();
            await Cdp.CallAsync("Network.setBlockedURLs", new { urls = blocked }, ct);
        }
        catch (Exception ex)
        {
            _log.Warn("[VM_MEDIA_POLICY] Không áp dụng được Network policy: " + ex.Message);
        }

        var enabled = _vmOptimization.Enabled ? "true" : "false";
        var disableAnimations = _vmOptimization.DisableCssAnimations ? "true" : "false";
        var script = $$"""
(() => {
  const enabled = {{enabled}};
  const disableAnimations = {{disableAnimations}};
  const stateKey = '__ttVmSaverV132State';
  const old = window[stateKey];

  if (!enabled) {
    try { old?.observer?.disconnect?.(); } catch (_) {}
    try { if (old?.timer) clearInterval(old.timer); } catch (_) {}
    try { if (old?.playHandler) document.removeEventListener('play', old.playHandler, true); } catch (_) {}
    try { document.getElementById('__tt_vm_v132_style')?.remove(); } catch (_) {}
    try { delete window[stateKey]; } catch (_) {}
    return true;
  }

  const apply = () => {
    try {
      for (const v of document.querySelectorAll('video')) {
        try {
          v.pause();
          v.muted = true;
          v.volume = 0;
          v.preload = 'metadata';
          v.disablePictureInPicture = true;
        } catch (_) {}
      }
      if (disableAnimations && document.documentElement) {
        let style = document.getElementById('__tt_vm_v132_style');
        if (!style) {
          style = document.createElement('style');
          style.id = '__tt_vm_v132_style';
          style.textContent = '*{animation-duration:0.001ms!important;animation-delay:0ms!important;transition-duration:0.001ms!important;transition-delay:0ms!important;scroll-behavior:auto!important;}';
          (document.head || document.documentElement).appendChild(style);
        }
      } else {
        document.getElementById('__tt_vm_v132_style')?.remove();
      }
    } catch (_) {}
  };

  apply();
  if (!old) {
    let observer = null;
    try {
      observer = new MutationObserver(apply);
      observer.observe(document.documentElement || document, { childList: true, subtree: true });
    } catch (_) {}
    const playHandler = e => { try { if (e?.target?.tagName === 'VIDEO') e.target.pause(); } catch (_) {} };
    try { document.addEventListener('play', playHandler, true); } catch (_) {}
    const timer = setInterval(apply, 1500);
    window[stateKey] = { observer, timer, playHandler };
  }
  return true;
})()
""";
        try
        {
            await EvalAsync(script, ct: ct);
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex))
        {
            // Trang đang chuyển document; ReloadAndWait/Connect sẽ áp dụng lại khi DOM ổn định.
        }
        catch (Exception ex)
        {
            _log.Warn("[VM_VIDEO_POLICY] Không áp dụng được video policy: " + ex.Message);
        }
    }

    public async Task BringToFrontAsync(CancellationToken ct = default)
    {
        LogCdpStart("BringToFront", Page?.Url ?? "");
        await Cdp.CallAsync("Page.bringToFront", ct: ct);
        LogCdpDone("BringToFront", Page?.Url ?? "");
    }

    public async Task<string> GetCurrentLiveIdentityAsync(CancellationToken ct = default)
    {
        const string js = """
(() => {
  const pickAttr = (el) => {
    if (!el || !el.getAttributeNames) return '';
    const names = ['data-e2e', 'data-room-id', 'data-live-room-id', 'data-testid', 'data-id', 'id'];
    for (const name of names) {
      const value = el.getAttribute(name);
      if (value) return `${name}=${value}`;
    }
    return '';
  };
  const cleanHref = (value) => {
    if (!value) return '';
    try {
      const u = new URL(value, location.href);
      u.hash = '';
      return u.toString();
    } catch {
      return String(value).trim();
    }
  };
  const findFirstHref = (selectors) => {
    for (const selector of selectors) {
      const el = document.querySelector(selector);
      if (!el) continue;
      const href = cleanHref(el.href || el.getAttribute('href') || '');
      if (href) return href;
    }
    return '';
  };
  const href = cleanHref(location.href);
  const canonical = cleanHref(document.querySelector('link[rel="canonical"]')?.href || document.querySelector('meta[property="og:url"]')?.content || '');
  const roomSources = [
    href,
    canonical,
    document.documentElement?.outerHTML?.match(/"roomId":"(\d+)"/)?.[1] || '',
    document.body?.innerHTML?.match(/room_id=(\d+)/)?.[1] || '',
    document.querySelector('[data-room-id]')?.getAttribute('data-room-id') || '',
    document.querySelector('[data-live-room-id]')?.getAttribute('data-live-room-id') || ''
  ].filter(Boolean);
  let roomId = '';
  for (const src of roomSources) {
    const text = String(src);
    const m = text.match(/(?:room_id|roomId)=([0-9]{6,})/) || text.match(/\/live\/([0-9]{6,})/) || text.match(/\b([0-9]{10,})\b/);
    if (m) {
      roomId = m[1];
      break;
    }
  }
  const broadcaster = findFirstHref([
    'a[href*="/@"][href*="/live"]',
    'main a[href*="/@"]',
    'aside a[href*="/@"]',
    'a[data-e2e*="anchor"]'
  ]);
  const stableAttr = pickAttr(document.querySelector('[data-room-id],[data-live-room-id],main [data-e2e],main [data-testid],main [data-id],main [id]'));
  const title = (document.title || '').trim();
  return [
    `href=${href}`,
    canonical ? `canonical=${canonical}` : '',
    roomId ? `roomId=${roomId}` : '',
    broadcaster ? `broadcaster=${broadcaster}` : '',
    stableAttr ? `dom=${stableAttr}` : '',
    title ? `title=${title}` : ''
  ].filter(Boolean).join(' | ');
})()
""";
        var r = await EvalAsync(js, ct: ct);
        return r.TryGetProperty("value", out var v) ? (v.GetString() ?? "").Trim() : "";
    }

    public async Task<bool> XPathExistsAsync(string xpath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) return false;
        LogCdpStart("XPathExists", TrimForLog(xpath));
        var js = $"(()=>{{try{{return !!document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;}}catch(e){{return false;}}}})()";
        var r = await EvalAsync(js, ct: ct);
        var ok = r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        LogCdpDone("XPathExists", $"{ok} | {TrimForLog(xpath)}");
        return ok;
    }

    public async Task<string> GetTextAsync(string xpath, CancellationToken ct = default)
    {
        var js = $"(()=>{{const e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return '';return (e.innerText||e.textContent||e.value||'').trim();}})()";
        var r = await EvalAsync(js, ct: ct);
        return r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
    }

    /// <summary>
    /// Phát hiện trang TikTok báo tài khoản đã vi phạm quy tắc và không thể dùng tính năng.
    /// Chỉ đọc DOM/text qua CDP; không dùng ảnh, OCR hay tọa độ. Chuỗi trả về rỗng nếu
    /// không thấy trạng thái này, ngược lại trả marker ngắn để AutomationEngine dừng tool.
    /// </summary>
    public async Task<string> DetectFatalFeatureRestrictionAsync(CancellationToken ct = default)
    {
        const string js = """
(() => {
  const norm = (value) => String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    // Unicode NFD không tách ký tự tiếng Việt 'đ', nên phải chuẩn hóa riêng.
    // Nếu thiếu dòng này, "Bạn đã vi phạm..." thành "ban đa vi pham..."
    // và marker ASCII "ban da vi pham..." sẽ không bao giờ khớp.
    .replace(/đ/g, 'd')
    .replace(/\s+/g, ' ')
    .trim();

  const bodyText = norm(document.body?.innerText || '');
  const hasViolation = bodyText.includes('ban da vi pham cac quy tac');
  const hasFeatureBlocked = bodyText.includes('khong the su dung tinh nang nay');
  if (!hasViolation || !hasFeatureBlocked) return '';

  const retryVisible = Array.from(document.querySelectorAll('button,[role="button"],a')).some((el) => {
    const r = el.getBoundingClientRect();
    const style = getComputedStyle(el);
    if (r.width < 2 || r.height < 2 || style.display === 'none' || style.visibility === 'hidden') return false;
    return norm(el.innerText || el.textContent || '') === 'thu lai';
  });

  return retryVisible ? 'VI_RULES_FEATURE_BLOCKED|RETRY_VISIBLE' : 'VI_RULES_FEATURE_BLOCKED';
})()
""";
        var r = await EvalAsync(js, ct: ct);
        return r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";
    }

    public Task<DomBox?> GetBoxAsync(string xpath, CancellationToken ct = default)
        => GetBoxNoScrollAsync(xpath, ct);

    public async Task<DomBox?> GetBoxNoScrollAsync(string xpath, CancellationToken ct = default)
    {
        var js = $"(()=>{{const e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return null;const r=e.getBoundingClientRect();return {{x:r.left,y:r.top,w:r.width,h:r.height}};}})()";
        var r = await EvalAsync(js, ct: ct);
        if (!r.TryGetProperty("value", out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return new DomBox(v.GetProperty("x").GetDouble(), v.GetProperty("y").GetDouble(), v.GetProperty("w").GetDouble(), v.GetProperty("h").GetDouble());
    }

    public async Task HoverXPathAsync(string xpath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) throw new InvalidOperationException("XPath vùng hover đang trống.");
        var box = await GetBoxNoScrollAsync(xpath, ct) ?? throw new InvalidOperationException($"Không tìm thấy XPath vùng hover: {xpath}");
        var (vw, vh) = await GetViewportSizeAsync(ct);
        if (box.Width < 2 || box.Height < 2 || box.X + box.Width <= 0 || box.Y + box.Height <= 0 || box.X >= vw || box.Y >= vh)
            throw new InvalidOperationException($"Vùng hover có tồn tại nhưng nằm ngoài viewport: {xpath}");

        double cx = Math.Clamp(box.X + box.Width / 2, 1, Math.Max(1, vw - 2));
        double cy = Math.Clamp(box.Y + box.Height / 2, 1, Math.Max(1, vh - 2));
        bool Inside(double x, double y) => x >= box.X && x <= box.X + box.Width && y >= box.Y && y <= box.Y + box.Height;

        var candidates = new (double x, double y)[]
        {
            (2, 2), (Math.Max(2, vw - 3), 2), (2, Math.Max(2, vh - 3)),
            (Math.Max(2, vw - 3), Math.Max(2, vh - 3))
        };
        var outside = candidates.FirstOrDefault(q => !Inside(q.x, q.y));
        if (Inside(outside.x, outside.y))
            outside = (Math.Clamp(box.X - 12, 1, Math.Max(1, vw - 2)), Math.Clamp(box.Y - 12, 1, Math.Max(1, vh - 2)));

        await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = outside.x, y = outside.y, button = "none" }, ct);
        await Task.Delay(80, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = cx, y = cy, button = "none" }, ct);
        await Task.Delay(70, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = Math.Clamp(cx + 9, 1, Math.Max(1, vw - 2)), y = cy, button = "none" }, ct);
        await Task.Delay(70, ct);
        await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = Math.Clamp(cx - 4, 1, Math.Max(1, vw - 2)), y = Math.Clamp(cy + 3, 1, Math.Max(1, vh - 2)), button = "none" }, ct);
    }

    public async Task<int> CountVisibleInteractiveOverXPathAsync(string xpath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) return 0;
        var js = $"(()=>{{const h=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!h)return -1;const hr=h.getBoundingClientRect();let n=0;for(const e of document.querySelectorAll('button,a,[role=button],[aria-label],[data-e2e]')){{const r=e.getBoundingClientRect();if(r.width<2||r.height<2)continue;const s=getComputedStyle(e);if(s.display==='none'||s.visibility==='hidden'||Number(s.opacity||1)<0.05)continue;if(r.right>hr.left&&r.left<hr.right&&r.bottom>hr.top&&r.top<hr.bottom)n++;}}return n;}})()";
        var r = await EvalAsync(js, ct: ct);
        return r.TryGetProperty("value", out var v) && v.TryGetInt32(out var count) ? count : 0;
    }

    public async Task ClickXPathDomSmartAsync(string xpath, int count = 1, int gapMs = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) throw new InvalidOperationException("XPath thao tác đang trống.");
        LogCdpStart("ClickXPathDomSmart", $"count={count} | xpath={TrimForLog(xpath)}");
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            var js = $"(()=>{{let e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return false;const clickable=n=>{{if(!n||n.nodeType!==1)return false;const tag=(n.tagName||'').toLowerCase();const role=(n.getAttribute&&n.getAttribute('role')||'').toLowerCase();if(tag==='button'||tag==='a'||tag==='input'||role==='button'||role==='link'||typeof n.onclick==='function'||n.tabIndex>=0)return true;try{{return getComputedStyle(n).cursor==='pointer';}}catch(_){{return false;}}}};let t=e;for(let k=0;k<7&&t&&!clickable(t);k++)t=t.parentElement;if(!t)t=e;try{{t.click();return true;}}catch(_e){{return false;}}}})()";
            var r = await EvalAsync(js, ct: ct);
            if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Không click được XPath/clickable ancestor: " + xpath);
            if (i + 1 < count) await Task.Delay(gapMs, ct);
        }
        LogCdpDone("ClickXPathDomSmart", $"count={count} | xpath={TrimForLog(xpath)}");
    }

    public async Task ClickXPathDomAsync(string xpath, int count = 1, int gapMs = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) throw new InvalidOperationException("XPath thao tác đang trống.");
        LogCdpStart("ClickXPathDom", $"count={count} | xpath={TrimForLog(xpath)}");
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            var js = $"(()=>{{const e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return false;try{{e.click();return true;}}catch(_e){{return false;}}}})()";
            var r = await EvalAsync(js, ct: ct);
            if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Không click DOM được XPath: " + xpath);
            if (i + 1 < count) await Task.Delay(gapMs, ct);
        }
        LogCdpDone("ClickXPathDom", $"count={count} | xpath={TrimForLog(xpath)}");
    }

    public async Task ClickXPathAsync(string xpath, int count = 1, int gapMs = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath)) throw new InvalidOperationException("XPath thao tác đang trống.");
        LogCdpStart("ClickXPath", $"count={count} | xpath={TrimForLog(xpath)}");
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            var box = await GetBoxNoScrollAsync(xpath, ct) ?? throw new InvalidOperationException($"Không tìm thấy XPath: {xpath}");
            var (vw, vh) = await GetViewportSizeAsync(ct);
            if (box.Width < 2 || box.Height < 2 || box.X + box.Width <= 0 || box.Y + box.Height <= 0 || box.X >= vw || box.Y >= vh)
                throw new InvalidOperationException($"XPath có tồn tại nhưng element nằm ngoài viewport, không click để tránh làm trang cuộn/giật: {xpath}");
            var x = Math.Clamp(box.X + box.Width / 2, 0, Math.Max(0, vw - 1));
            var y = Math.Clamp(box.Y + box.Height / 2, 0, Math.Max(0, vh - 1));
            await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct);
            await Cdp.CallAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct);
            if (i + 1 < count) await Task.Delay(gapMs, ct);
        }
        LogCdpDone("ClickXPath", $"count={count} | xpath={TrimForLog(xpath)}");
    }

    public async Task FocusXPathAsync(string xpath, CancellationToken ct = default)
    {
        var js = $"(()=>{{const e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return false;try{{e.focus({{preventScroll:true}});}}catch(_){{e.focus();}}return document.activeElement===e||e.contains(document.activeElement);}})()";
        var r = await EvalAsync(js, ct: ct);
        if (!r.TryGetProperty("value", out var v) || !v.GetBoolean()) throw new InvalidOperationException("Không focus được XPath: " + xpath);
    }

    public async Task InsertTextAsync(string xpath, string text, CancellationToken ct = default)
    {
        LogCdpStart("InsertText", $"xpath={TrimForLog(xpath)} | text={TrimForLog(text, 80)}");
        await FocusXPathAsync(xpath, ct);
        var clearJs = $"(()=>{{const e=document.evaluate({JsString(xpath)},document,null,XPathResult.FIRST_ORDERED_NODE_TYPE,null).singleNodeValue;if(!e)return false;if(e.isContentEditable){{e.innerHTML='';e.dispatchEvent(new InputEvent('input',{{bubbles:true,inputType:'deleteContentBackward'}}));}}else if('value' in e){{const p=Object.getPrototypeOf(e);const d=Object.getOwnPropertyDescriptor(p,'value');if(d&&d.set)d.set.call(e,'');else e.value='';e.dispatchEvent(new Event('input',{{bubbles:true}}));}}return true;}})()";
        await EvalAsync(clearJs, ct: ct);
        await Cdp.CallAsync("Input.insertText", new { text }, ct);
        LogCdpDone("InsertText", $"xpath={TrimForLog(xpath)} | len={text.Length}");
    }

    public async Task PrepareKeyboardNavigationAsync(CancellationToken ct = default)
    {
        await BringToFrontAsync(ct);
        try
        {
            var r = await EvalAsync("""
(() => {
  try {
    const ae = document.activeElement;
    if (ae && typeof ae.blur === 'function') ae.blur();
    try { document.getSelection?.()?.removeAllRanges?.(); } catch (_) {}

    // TikTok đôi lúc giữ keyboard focus trong chat/sidebar sau nhiều lần reload.
    // Tạo một focus target tạm trên vùng chính để mô phỏng trạng thái giống khi người dùng
    // bấm phím trực tiếp trên trang, nhưng không click chuột và không đổi layout.
    const target = document.querySelector('main') || document.body || document.documentElement;
    let restoreTabIndex = null;
    let hadTabIndex = false;
    if (target && target.nodeType === 1) {
      hadTabIndex = target.hasAttribute('tabindex');
      restoreTabIndex = target.getAttribute('tabindex');
      if (!hadTabIndex) target.setAttribute('tabindex', '-1');
      try { target.focus({ preventScroll: true }); } catch (_) { try { target.focus(); } catch (_) {} }
      if (!hadTabIndex) target.removeAttribute('tabindex');
      else if (restoreTabIndex !== null) target.setAttribute('tabindex', restoreTabIndex);
    }
    try { window.focus(); } catch (_) {}

    const active = document.activeElement;
    return active ? `${active.tagName || ''}#${active.id || ''}.${active.className || ''}` : '(none)';
  } catch (e) {
    return 'focus-error:' + String(e?.message || e);
  }
})()
""", ct: ct);
            var active = r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            _log.Info($"[KEYBOARD_FOCUS_READY] active={TrimForLog(active, 140)} targetUrl={TrimForLog(Page?.Url ?? "", 140)}");
        }
        catch (Exception ex)
        {
            _log.Warn("[KEYBOARD_FOCUS_PREP_FAILED] " + ex.Message);
        }
    }

    public async Task PressArrowDownNavigationAsync(int count = 1, int gapMs = 600, bool recoveryMode = false, CancellationToken ct = default)
    {
        count = Math.Max(1, count);
        LogCdpStart("PressArrowDownNavigation", $"count={count} | mode={(recoveryMode ? "recovery-keyDown" : "normal-rawKeyDown")}");
        await PrepareKeyboardNavigationAsync(ct);

        const string key = "ArrowDown";
        const string code = "ArrowDown";
        const int vk = 40;
        var downType = recoveryMode ? "keyDown" : "rawKeyDown";

        for (int i = 0; i < count; i++)
        {
            await Cdp.CallAsync("Input.dispatchKeyEvent", new
            {
                type = downType,
                key,
                code,
                windowsVirtualKeyCode = vk,
                nativeVirtualKeyCode = vk,
                autoRepeat = false,
                isKeypad = false
            }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new
            {
                type = "keyUp",
                key,
                code,
                windowsVirtualKeyCode = vk,
                nativeVirtualKeyCode = vk,
                autoRepeat = false,
                isKeypad = false
            }, ct);
            if (i + 1 < count) await Task.Delay(gapMs, ct);
        }
        LogCdpDone("PressArrowDownNavigation", $"count={count} | mode={(recoveryMode ? "recovery-keyDown" : "normal-rawKeyDown")}");
    }

    public async Task PressKeyAsync(string key, int count = 1, int gapMs = 600, CancellationToken ct = default)
    {
        if (key == "ArrowDown")
        {
            await PressArrowDownNavigationAsync(count, gapMs, recoveryMode: false, ct);
            return;
        }

        LogCdpStart("PressKey", $"key={key} | count={count}");
        string code = key switch { "Enter" => "Enter", _ => key };
        int vk = key switch { "Enter" => 13, _ => 0 };
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk }, ct);
            if (i + 1 < count) await Task.Delay(gapMs, ct);
        }
        LogCdpDone("PressKey", $"key={key} | count={count}");
    }

    public async Task ReloadAndWaitAsync(int minWaitMs = 1000, int timeoutMs = 15000, CancellationToken ct = default)
    {
        var perf = Stopwatch.StartNew();
        try
        {
        LogCdpStart("ReloadAndWait", $"minWaitMs={minWaitMs} | timeoutMs={timeoutMs}");
        await Cdp.CallAsync("Page.reload", new { ignoreCache = false }, ct);
        await Task.Delay(minWaitMs, ct);
        var started = Environment.TickCount64;
        int stable = 0;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            try
            {
                var r = await EvalAsync("document.readyState", ct: ct);
                var state = r.TryGetProperty("value", out var v) ? v.GetString() : "";
                if (state is "interactive" or "complete") stable++; else stable = 0;
                if (stable >= 3)
                {
                    await ApplyVmRuntimePolicyAsync(ct);
                    LogCdpDone("ReloadAndWait", $"readyState={state}");
                    return;
                }
            }
            // Runtime.evaluate can race the document teardown immediately after F5.
            // That is normal navigation, not a CDP disconnect.  Let actual session/
            // target failures escape so the engine can reconnect only when warranted.
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { stable = 0; }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Chrome chưa ổn định sau F5 trong thời gian chờ.");
        }
        finally
        {
            perf.Stop();
            _log.Info($"[STEP_PERF] step=reloadDomReady elapsedMs={perf.ElapsedMilliseconds}");
        }
    }


    public async Task<(int width, int height)> GetViewportSizeAsync(CancellationToken ct = default)
    {
        var r = await EvalAsync("({w:window.innerWidth,h:window.innerHeight})", ct: ct);
        var v = r.GetProperty("value");
        return (v.GetProperty("w").GetInt32(), v.GetProperty("h").GetInt32());
    }

    public async Task StartXPathPickerAsync(bool preferClickableAncestor = false, CancellationToken ct = default)
    {
        var prefer = preferClickableAncestor ? "true" : "false";
        var js = """
        (()=>{
          if(window.__ttv11PickerCleanup) window.__ttv11PickerCleanup();
          window.__ttv11PickedXPath='';
          const preferClickable=__PREFER_CLICKABLE__;
          const lit=s=>{s=String(s);if(!s.includes("'"))return "'"+s+"'";if(!s.includes('"'))return '"'+s+'"';return 'concat('+s.split("'").map(x=>"'"+x+"'").join(',"\'",')+')';};
          const count=x=>{try{return document.evaluate(x,document,null,XPathResult.ORDERED_NODE_SNAPSHOT_TYPE,null).snapshotLength}catch(e){return 99}};
          const isClickable=n=>{if(!n||n.nodeType!==1)return false;const tag=(n.tagName||'').toLowerCase();const role=(n.getAttribute&&n.getAttribute('role')||'').toLowerCase();if(tag==='button'||tag==='a'||tag==='input'||role==='button'||role==='link'||typeof n.onclick==='function'||n.tabIndex>=0)return true;try{return getComputedStyle(n).cursor==='pointer';}catch(_){return false;}};
          const clickableAncestor=e=>{let n=e;for(let i=0;i<7&&n;i++,n=n.parentElement)if(isClickable(n))return n;return e;};
          const build=e=>{
            if(e.id){const x=`//*[@id=${lit(e.id)}]`;if(count(x)===1)return x;}
            for(const a of ['data-e2e','data-testid','aria-label','name','placeholder','role','title']){
              const v=e.getAttribute&&e.getAttribute(a); if(v){const x=`//${e.tagName.toLowerCase()}[@${a}=${lit(v)}]`;if(count(x)===1)return x;}
            }
            const txt=(e.innerText||'').trim().replace(/\s+/g,' ');
            if(txt&&txt.length<=50){const x=`//${e.tagName.toLowerCase()}[normalize-space(.)=${lit(txt)}]`;if(count(x)===1)return x;}
            const parts=[]; let n=e;
            while(n&&n.nodeType===1&&n!==document.documentElement){
              let i=1,p=n.previousElementSibling; while(p){if(p.tagName===n.tagName)i++;p=p.previousElementSibling;}
              parts.unshift(`${n.tagName.toLowerCase()}[${i}]`); n=n.parentElement;
            }
            return '/html[1]/'+parts.join('/');
          };
          let last=null;
          const over=e=>{if(last)last.style.outline='';last=preferClickable?clickableAncestor(e.target):e.target;last.style.outline='3px solid #ff3b30';};
          const click=e=>{e.preventDefault();e.stopPropagation();e.stopImmediatePropagation();const target=preferClickable?clickableAncestor(e.target):e.target;window.__ttv11PickedXPath=build(target);cleanup();};
          const key=e=>{if(e.key==='Escape'){window.__ttv11PickedXPath='__CANCEL__';cleanup();}};
          const cleanup=()=>{document.removeEventListener('mouseover',over,true);document.removeEventListener('click',click,true);document.removeEventListener('keydown',key,true);if(last)last.style.outline='';delete window.__ttv11PickerCleanup;};
          window.__ttv11PickerCleanup=cleanup;
          document.addEventListener('mouseover',over,true);document.addEventListener('click',click,true);document.addEventListener('keydown',key,true);
          return true;
        })()
        """.Replace("__PREFER_CLICKABLE__", prefer);
        await EvalAsync(js, ct: ct);
    }

    public async Task<string?> PollPickedXPathAsync(CancellationToken ct = default)
    {
        var r = await EvalAsync("window.__ttv11PickedXPath||''", ct: ct);
        var s = r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        return s.Length == 0 ? null : s;
    }

    public async Task<string?> PickXPathAsync(TimeSpan timeout, bool preferClickableAncestor = false, CancellationToken ct = default)
    {
        await StartXPathPickerAsync(preferClickableAncestor, ct);
        _log.Info("Chế độ lấy XPath đang bật: rê chuột lên phần tử trong Chrome và click; Esc để hủy.");
        try
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                ct.ThrowIfCancellationRequested();
                var x = await PollPickedXPathAsync(ct);
                if (x == "__CANCEL__") return null;
                if (!string.IsNullOrWhiteSpace(x)) return x;
                await Task.Delay(150, ct);
            }
            return null;
        }
        finally
        {
            try { await EvalAsync("window.__ttv11PickerCleanup&&window.__ttv11PickerCleanup();true", ct: CancellationToken.None); } catch { }
        }
    }

    /// <summary>
    /// Trả HWND Chrome đang được worker quản lý cho Manager/DWM preview.
    /// Chỉ dùng handle đã cache/khám phá trong lúc connect/launch, tuyệt đối không
    /// spawn PowerShell ở đường status nóng.
    /// </summary>
    public long GetManagedWindowHandleValue()
        => IsLiveWindowHandle(_managedWindowHandle) ? _managedWindowHandle.ToInt64() : 0L;

    public ChromeWindowState GetManagedWindowState(string profileDir, int port)
    {
        if (!IsManagedContext(profileDir, port)) return ChromeWindowState.NotFound;

        if (!IsLiveWindowHandle(_managedWindowHandle))
            _managedWindowHandle = DiscoverManagedWindowHandle();

        if (!IsLiveWindowHandle(_managedWindowHandle)) return ChromeWindowState.NotFound;
        return IsIconic(_managedWindowHandle) ? ChromeWindowState.Minimized : ChromeWindowState.Visible;
    }

    public bool MinimizeManagedWindow(string profileDir, int port)
    {
        if (!IsManagedContext(profileDir, port))
            AttachManagedWindow(profileDir, port);
        if (!IsLiveWindowHandle(_managedWindowHandle))
            _managedWindowHandle = DiscoverManagedWindowHandle();
        return IsLiveWindowHandle(_managedWindowHandle) && ShowWindowAsync(_managedWindowHandle, SW_MINIMIZE);
    }

    public bool RestoreManagedWindow(string profileDir, int port)
    {
        if (!IsManagedContext(profileDir, port))
            AttachManagedWindow(profileDir, port);
        if (!IsLiveWindowHandle(_managedWindowHandle))
            _managedWindowHandle = DiscoverManagedWindowHandle();
        return IsLiveWindowHandle(_managedWindowHandle) && ShowWindowAsync(_managedWindowHandle, SW_RESTORE);
    }

    IntPtr DiscoverManagedWindowHandle()
    {
        if (_managedPids.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (found != IntPtr.Zero) return false;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !_managedPids.Contains((int)pid)) return true;
            if (!IsWindowVisible(hwnd) && !IsIconic(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _http.Dispose();
        _manualCloseGate.Dispose();
    }

    const int SW_MINIMIZE = 6;
    const int SW_RESTORE = 9;
    const uint GW_OWNER = 4;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
