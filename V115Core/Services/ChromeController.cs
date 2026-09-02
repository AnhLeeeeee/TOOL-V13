using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToolTikTokV11.Utils;
using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed record CdpPage(string Id, string Title, string Url, string WebSocketDebuggerUrl);
public sealed record DomBox(double X, double Y, double Width, double Height);
public sealed record CdpVersionInfo(string Browser, string WebSocketDebuggerUrl);
public sealed record ManagedChromeCloseResult(bool WasRunning, bool Closed, IReadOnlyList<int> RemainingPids, bool CdpReady, string Method);
public sealed record ManagedChromeWindowResolution(int CachedPid, int ResolvedPid, long WindowHandle, string Reason);
public sealed record TikTokStartupResult(string State, string Message, bool LoggedIn, bool LiveOpened);
public sealed record TikTokRecommendedLiveCandidate(string Href, string Username, string ViewerText, string Label);
public sealed record TikTokProfileIdentityUpdateResult(bool NameChanged, bool AvatarChanged, bool BioChanged, bool NameCooldown, bool AlreadyConfigured, bool Skipped, string Message);
public enum ChromeWindowState { NotFound, Visible, Minimized }

public sealed partial class ChromeController : IAsyncDisposable
{
    public sealed record PageHealthSnapshot(bool Healthy, bool CrashLike, string Reason, string Url);

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

    /// <summary>
    /// Nhận diện riêng lỗi renderer/tab crash (Aw, Snap!/Out of Memory/target crashed).
    /// Dùng để AutomationEngine ưu tiên reload/restart Chrome thay vì coi đây là lỗi XPath
    /// rồi chuyển LIVE hoặc tạm dừng tool.
    /// </summary>
    public bool IsRendererCrashLike(Exception ex)
    {
        var text = ex.ToString();
        return LooksLikeRendererCrashText(text)
            || text.Contains("Inspector.targetCrashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Target.targetCrashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("renderer crashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("renderer process gone", StringComparison.OrdinalIgnoreCase);
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
            "--no-first-run --no-default-browser-check " +
            "--lang=vi --accept-lang=vi-VN,vi,en-US,en --start-maximized " + backgroundFlags +
            TikTokUrl;
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
            .Where(x => CommandLineUsesProfile(x.CommandLine, normalized)
                && CommandLineUsesRemoteDebuggingPort(x.CommandLine, port))
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
        var listenerPid = TryGetListeningProcessId(port);
        var processCacheChanged = false;
        if (listenerPid is > 0)
        {
            if (_managedPids.Count != 1 || !_managedPids.Contains(listenerPid.Value))
            {
                _managedPids.Clear();
                _managedPids.Add(listenerPid.Value);
                processCacheChanged = true;
            }
        }
        else if (_managedPids.Count == 0 || !_managedPids.Any(IsProcessRunning))
        {
            var refreshedPids = FindChromeProcessIds(_managedProfileDir, port);
            _managedPids.Clear();
            foreach (var pid in refreshedPids) _managedPids.Add(pid);
            processCacheChanged = true;
        }

        if (processCacheChanged) _managedWindowHandle = IntPtr.Zero;
        if (!IsLiveWindowHandle(_managedWindowHandle))
            _managedWindowHandle = DiscoverManagedWindowHandle();
        sw.Stop();
        if (processCacheChanged)
            _log.Info($"[CHROME_WINDOW_CACHE_REFRESH] port={port} listenerPid={(listenerPid?.ToString() ?? "-")} pids={string.Join(',', _managedPids)} hwnd={GetManagedWindowHandleValue()}");
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

    static bool CommandLineUsesRemoteDebuggingPort(string commandLine, int port)
    {
        var match = Regex.Match(commandLine ?? "", "--remote-debugging-port(?:=|\\s+)(?<port>\\d+)", RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["port"].Value, out var parsed)
            && parsed == port;
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


    public async Task<TikTokProfileIdentityUpdateResult> UpdateTikTokProfileIdentityAsync(
        string? username,
        string? displayName,
        string? avatarPath,
        string? bio,
        bool skipAllIfNameCooldown = false,
        IReadOnlyCollection<string>? knownDisplayNames = null,
        bool verifyExistingState = false,
        bool fastNameGuardMode = false,
        CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        avatarPath = (avatarPath ?? "").Trim();
        bio = (bio ?? "").Trim();
        if (bio.Length > 80) bio = bio[..80];
        if (displayName.Length == 0 && avatarPath.Length == 0 && bio.Length == 0)
            throw new InvalidOperationException("Không có tên, ảnh hoặc tiểu sử nào được yêu cầu cập nhật.");
        if (avatarPath.Length > 0)
        {
            avatarPath = Path.GetFullPath(avatarPath);
            if (!File.Exists(avatarPath)) throw new FileNotFoundException("Không tìm thấy file avatar.", avatarPath);
        }
        if (!Connected) throw new InvalidOperationException("Chrome chưa kết nối CDP.");

        var originalUrl = Page?.Url ?? "";
        var knownNames = (knownDisplayNames ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Regex.Replace(x.Trim(), @"\s+", " "))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (displayName.Length > 0 && !knownNames.Contains(displayName, StringComparer.OrdinalIgnoreCase)) knownNames.Add(displayName);
        _log.Info($"[TIKTOK_IDENTITY_UPDATE_START] name={(displayName.Length > 0 ? "yes" : "no")} avatar={(avatarPath.Length > 0 ? Path.GetFileName(avatarPath) : "no")} bio={(bio.Length > 0 ? "yes" : "no")} cooldownCheck={skipAllIfNameCooldown} verifyExisting={verifyExistingState} fastNameGuard={fastNameGuardMode} knownNames={knownNames.Count}");

        async Task<JsonElement> Eval(string js) => await EvalAsync(js, ct: ct);
        static bool IsTrue(JsonElement result)
            => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;
        static string ReadString(JsonElement result)
            => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "") : "";

        async Task<bool> WaitBoolAsync(string js, int timeoutMs = 12000, int delayMs = 250)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try { if (IsTrue(await Eval(js))) return true; } catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
                await Task.Delay(delayMs, ct);
            }
            return false;
        }

        // 1) Ưu tiên dựng URL trang cá nhân trực tiếp từ Username đã lưu của profile.
        //    Ví dụ "trnh.tnhh.mai82" -> https://www.tiktok.com/@trnh.tnhh.mai82
        //    Nếu Username trống/không giống handle TikTok hoặc điều hướng trực tiếp thất bại,
        //    mới fallback sang link Hồ sơ/Profile trong DOM như logic cũ.
        string NormalizeTikTokHandle(string raw)
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
            // Tránh nhầm email/số điện thoại với @username TikTok.
            if (raw.Contains('@') || raw.Any(char.IsWhiteSpace)) return "";
            if (!raw.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_')) return "";
            return raw;
        }

        async Task<string> FindProfileHrefFromDomAsync()
        {
            var result = await Eval("""
(() => {
  const clean = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const candidates = [
    document.querySelector('a[data-e2e="nav-profile"]'),
    document.querySelector('[data-e2e="nav-profile"] a'),
    ...document.querySelectorAll('a[href*="/@"]')
  ].filter(Boolean);
  for (const a of candidates) {
    const href = a.href || a.getAttribute?.('href') || '';
    const text = clean(`${a.innerText || a.textContent || ''} ${a.getAttribute?.('aria-label') || ''} ${a.getAttribute?.('data-e2e') || ''}`);
    if (!href || !href.includes('/@')) continue;
    if (a.matches?.('[data-e2e="nav-profile"]') || a.closest?.('[data-e2e="nav-profile"]') || text === 'profile' || text === 'hồ sơ' || text.includes('nav-profile')) return href;
  }
  return '';
})()
""");
            return ReadString(result);
        }

        var profileHref = "";
        var directHandle = NormalizeTikTokHandle(username);
        var usedDirectProfileHref = false;

        static string NormalizeIdentityDisplayName(string value)
            => Regex.Replace((value ?? "").Trim(), @"\s+", " ");

        bool IsExpectedDisplayName(string currentName, bool acceptAnyKnownName)
        {
            var current = NormalizeIdentityDisplayName(currentName);
            if (current.Length == 0) return false;

            if (!acceptAnyKnownName)
                return string.Equals(current, NormalizeIdentityDisplayName(displayName), StringComparison.OrdinalIgnoreCase);

            return knownNames.Any(x =>
                string.Equals(current, NormalizeIdentityDisplayName(x), StringComparison.OrdinalIgnoreCase));
        }

        async Task<string> ReadProfileDisplayNameAsync()
        {
            var result = await Eval($$"""
(() => {
  const wantedHandle = {{JsString(directHandle)}}.toLowerCase();
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  const selectors = [
    '[data-e2e="user-title"]'
  ];
  for (const selector of selectors) {
    const el = document.querySelector(selector);
    if (!visible(el)) continue;
    const text = norm(el.innerText || el.textContent || '');
    if (text) return text;
  }

  const headings = [...document.querySelectorAll('main h1, main h2, h1[data-e2e*="user"], h2[data-e2e*="user"]')]
    .filter(visible)
    .map(el => norm(el.innerText || el.textContent || ''))
    .filter(t => t && t.length <= 100)
    .filter(t => {
      const low = t.toLowerCase().replace(/^@/, '');
      return !wantedHandle || low !== wantedHandle;
    });
  return headings[0] || '';
})()
""");
            return ReadString(result);
        }
        if (fastNameGuardMode
            && !string.IsNullOrWhiteSpace(Page?.Url)
            && (Page?.Url ?? "").StartsWith("https://www.tiktok.com/@", StringComparison.OrdinalIgnoreCase))
        {
            // Name Guard vừa đi vào đúng href Hồ sơ để đọc tên. Tận dụng luôn trang
            // hiện tại, không navigate lại lần nữa trước khi mở Edit profile.
            profileHref = Page!.Url;
            _log.Info($"[TIKTOK_IDENTITY_FAST_REUSE_PROFILE] href={profileHref}");
        }
        else if (!string.IsNullOrWhiteSpace(directHandle))
        {
            profileHref = "https://www.tiktok.com/@" + Uri.EscapeDataString(directHandle);
            try
            {
                _log.Info($"[TIKTOK_IDENTITY_PROFILE_DIRECT] username={directHandle} href={profileHref}");
                await NavigateAndWaitAsync(profileHref, fastNameGuardMode ? 250 : 900, fastNameGuardMode ? 7000 : 18000, ct);
                usedDirectProfileHref = true;
            }
            catch (Exception ex)
            {
                _log.Warn($"[TIKTOK_IDENTITY_PROFILE_DIRECT_FAILED] username={directHandle} message={ex.Message}");
                profileHref = "";
            }
        }

        if (string.IsNullOrWhiteSpace(profileHref))
        {
            profileHref = await FindProfileHrefFromDomAsync();
            if (string.IsNullOrWhiteSpace(profileHref))
                throw new InvalidOperationException("Không xác định được trang Hồ sơ TikTok. Username của profile đang trống/không hợp lệ và cũng không tìm thấy link Hồ sơ/Profile trong DOM.");

            _log.Info($"[TIKTOK_IDENTITY_PROFILE_DOM_FALLBACK] href={profileHref}");
            await NavigateAndWaitAsync(profileHref, fastNameGuardMode ? 250 : 900, fastNameGuardMode ? 7000 : 18000, ct);
        }

        // 1.4) TikTok là SPA nên ngay sau lần đổi tên trước, trang hồ sơ có thể vẫn
        // giữ nickname cũ trong cache. Với Auto Identity, nếu Excel chưa DONE thì
        // kiểm tra tên hiện tại; nếu chưa khớp, chờ rồi F5 đúng trang hồ sơ một lần
        // trước khi mở Sửa hồ sơ/cooldown. Nhờ vậy tài khoản đã tự đổi hoặc lần trước
        // đổi thành công nhưng chưa kịp ghi Excel sẽ được công nhận là thành công.
        if (verifyExistingState && displayName.Length > 0)
        {
            for (var verifyAttempt = 1; verifyAttempt <= 2; verifyAttempt++)
            {
                string currentName = "";
                try { currentName = await ReadProfileDisplayNameAsync(); }
                catch (Exception ex)
                {
                    _log.Warn($"[TIKTOK_IDENTITY_NAME_PRECHECK_READ_WARN] attempt={verifyAttempt}/2 {ex.Message}");
                }

                var matched = IsExpectedDisplayName(currentName, acceptAnyKnownName: true);
                _log.Info($"[TIKTOK_IDENTITY_NAME_PRECHECK] attempt={verifyAttempt}/2 currentName={currentName} matched={matched}");
                if (matched)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(originalUrl)
                            && originalUrl.StartsWith("https://www.tiktok.com/", StringComparison.OrdinalIgnoreCase))
                            await NavigateAndWaitAsync(originalUrl, 700, 12000, ct);
                    }
                    catch { }

                    _log.Info($"[TIKTOK_IDENTITY_NAME_ALREADY_MATCHED] currentName={currentName}; xác nhận trên profile page sau reload nếu cần, cho phép Manager ghi DONE.");
                    return new TikTokProfileIdentityUpdateResult(
                        false, false, false, false, true, true,
                        $"Tên TikTok hiện tại đã đúng ({currentName}). Không đổi lại; ghi DONE vào Excel.");
                }

                if (verifyAttempt < 2)
                {
                    try
                    {
                        _log.Info("[TIKTOK_IDENTITY_NAME_PRECHECK_RELOAD] delayMs=2500 attempt=1/1");
                        await Task.Delay(2500, ct);
                        await ReloadAndWaitAsync(1000, 18000, ct);
                        await Task.Delay(1200, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.Warn("[TIKTOK_IDENTITY_NAME_PRECHECK_RELOAD_WARN] " + ex.Message);
                    }
                }
            }
        }

        // 1.5) Với Auto Identity, xác nhận trạng thái ngay trên TRANG HỒ SƠ trước.
        // Mục tiêu: tài khoản đã được đổi tên/ảnh từ lần trước nhưng Excel chưa có DONE
        // thì chỉ đọc DOM profile page, ghi DONE rồi quay lại trang ban đầu; KHÔNG mở
        // popup "Sửa hồ sơ" chỉ để kiểm tra. Nếu dữ liệu trên profile page không đủ chắc
        // chắn hoặc chưa khớp, luồng mới tiếp tục xuống bước Edit profile để cập nhật thật.
        if (verifyExistingState)
        {
            try
            {
                var profileSnapshotJson = ReadString(await Eval($$"""
(() => {
  const wantedHandle = {{JsString(directHandle)}}.toLowerCase();
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();
  const normLower = s => norm(s).toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  const firstText = selectors => {
    for (const selector of selectors) {
      const el = document.querySelector(selector);
      if (!visible(el)) continue;
      const text = norm(el.innerText || el.textContent || '');
      if (text) return text;
    }
    return '';
  };

  // Tên hiển thị TikTok nằm ở data-e2e="user-title".
  // Không dùng user-subtitle vì đó là @username/handle.
  let currentName = firstText([
    '[data-e2e="user-title"]'
  ]);
  if (!currentName) {
    const headings = [...document.querySelectorAll('main h1, main h2, h1[data-e2e*="user"], h2[data-e2e*="user"]')]
      .filter(visible)
      .map(el => norm(el.innerText || el.textContent || ''))
      .filter(t => t && t.length <= 100)
      .filter(t => {
        const low = t.toLowerCase().replace(/^@/, '');
        return !wantedHandle || (low !== wantedHandle && low !== '@' + wantedHandle);
      });
    currentName = headings[0] || '';
  }

  let currentBio = firstText([
    '[data-e2e="user-bio"]',
    '[data-e2e="profile-bio"]',
    '[data-e2e*="bio"]'
  ]);

  const avatarCandidates = [
    document.querySelector('img[data-e2e="user-avatar"]'),
    document.querySelector('[data-e2e="user-avatar"] img'),
    document.querySelector('[data-e2e*="avatar"] img'),
    ...document.querySelectorAll('main img')
  ].filter((el, i, arr) => el && arr.indexOf(el) === i && visible(el));

  let avatar = null;
  for (const img of avatarCandidates) {
    const r = img.getBoundingClientRect();
    if (r.width < 42 || r.height < 42) continue;
    const meta = normLower(`${img.currentSrc || img.src || ''} ${img.alt || ''} ${img.getAttribute('data-e2e') || ''}`);
    const explicitAvatar = !!img.closest?.('[data-e2e*="avatar"]') || /avatar|profile photo|ảnh đại diện|ảnh hồ sơ/.test(meta);
    const looksLikeHeaderAvatar = r.width <= 180 && r.height <= 180
      && Math.abs(r.width - r.height) <= Math.max(12, Math.min(r.width, r.height) * 0.22)
      && r.top >= 0 && r.top < 520;
    if (!explicitAvatar && !looksLikeHeaderAvatar) continue;
    let score = Math.min(r.width, r.height);
    if (explicitAvatar) score += 500;
    if (looksLikeHeaderAvatar) score += 100;
    if (!avatar || score > avatar.score) avatar = { el: img, score };
  }

  const avatarSrc = avatar ? String(avatar.el.currentSrc || avatar.el.src || '') : '';
  const avatarMeta = normLower(`${avatarSrc} ${avatar?.el?.alt || ''} ${avatar?.el?.getAttribute?.('data-e2e') || ''}`);
  const avatarLooksDefault = !avatarSrc
    || /default[-_ ]?avatar|avatar[-_ ]?default|default[-_ ]?profile|placeholder|no[-_ ]?avatar|anonymous|user[-_ ]?default/.test(avatarMeta)
    || avatarSrc.startsWith('data:image/svg+xml');

  return JSON.stringify({
    currentName,
    currentBio,
    nameFound: !!currentName,
    bioFound: !!currentBio,
    avatarSrc,
    avatarFound: !!avatarSrc,
    avatarLooksDefault
  });
})()
"""));

                using var profileDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(profileSnapshotJson) ? "{}" : profileSnapshotJson);
                var profileRoot = profileDoc.RootElement;
                var currentProfileName = profileRoot.TryGetProperty("currentName", out var pn) ? (pn.GetString() ?? "") : "";
                var currentProfileBio = profileRoot.TryGetProperty("currentBio", out var pb) ? (pb.GetString() ?? "") : "";
                var profileNameFound = profileRoot.TryGetProperty("nameFound", out var pnf) && pnf.ValueKind == JsonValueKind.True;
                var profileBioFound = profileRoot.TryGetProperty("bioFound", out var pbf) && pbf.ValueKind == JsonValueKind.True;
                var profileAvatarFound = profileRoot.TryGetProperty("avatarFound", out var paf) && paf.ValueKind == JsonValueKind.True;
                var profileAvatarDefault = profileRoot.TryGetProperty("avatarLooksDefault", out var pad) && pad.ValueKind == JsonValueKind.True;

                static string NormProfileText(string value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ");
                var profileNameOk = displayName.Length == 0
                    || (profileNameFound && knownNames.Any(x => string.Equals(NormProfileText(x), NormProfileText(currentProfileName), StringComparison.OrdinalIgnoreCase)));
                var profileAvatarOk = avatarPath.Length == 0 || (profileAvatarFound && !profileAvatarDefault);
                var profileBioOk = bio.Length == 0
                    || (profileBioFound && string.Equals(NormProfileText(currentProfileBio), NormProfileText(bio), StringComparison.Ordinal));
                var hasProfileCheck = displayName.Length > 0 || avatarPath.Length > 0 || bio.Length > 0;

                _log.Info($"[TIKTOK_IDENTITY_PROFILE_CHECK] currentName={currentProfileName} nameFound={profileNameFound} nameOk={profileNameOk} avatarFound={profileAvatarFound} avatarDefault={profileAvatarDefault} avatarOk={profileAvatarOk} bioFound={profileBioFound} bioOk={profileBioOk}");

                // Auto Identity V13.5: TÊN là điều kiện xác nhận chính.
                // Chỉ cần tên đang hiển thị trùng một tên trong danh sách cấu hình thì coi như
                // tài khoản đã được setup, ghi DONE và tuyệt đối không mở form Sửa hồ sơ nữa.
                // Ảnh/tiểu sử không còn được dùng để chặn fast-path này theo yêu cầu vận hành.
                var skipByMatchedName = displayName.Length > 0 && profileNameOk;

                // Nếu người dùng tắt cập nhật tên hoàn toàn thì vẫn giữ cách xác nhận cũ cho
                // riêng ảnh/tiểu sử, để không làm mất chức năng của cấu hình avatar/bio-only.
                var skipByOtherConfiguredState = displayName.Length == 0
                    && hasProfileCheck && profileAvatarOk && profileBioOk;

                if (skipByMatchedName || skipByOtherConfiguredState)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(originalUrl)
                            && originalUrl.StartsWith("https://www.tiktok.com/", StringComparison.OrdinalIgnoreCase))
                            await NavigateAndWaitAsync(originalUrl, 700, 12000, ct);
                    }
                    catch { }
                    if (skipByMatchedName)
                        _log.Info($"[TIKTOK_IDENTITY_NAME_ALREADY_MATCHED] currentName={currentProfileName}; trùng danh sách tên cấu hình, bỏ qua toàn bộ Sửa hồ sơ và cho phép Manager ghi DONE.");
                    else
                        _log.Info("[TIKTOK_IDENTITY_PROFILE_ALREADY_CONFIGURED] profile page đã đủ điều kiện; không mở Edit profile, cho phép Manager ghi DONE.");

                    return new TikTokProfileIdentityUpdateResult(
                        false, false, false, false, true, true,
                        skipByMatchedName
                            ? $"Tên TikTok hiện tại đã trùng danh sách cấu hình ({currentProfileName}). Bỏ qua Sửa hồ sơ; ghi DONE vào Excel."
                            : "Đã thiết lập sẵn trên TikTok. Tool xác nhận trực tiếp từ trang hồ sơ, không mở Sửa hồ sơ; ghi DONE vào Excel.");
                }
            }
            catch (Exception ex)
            {
                // Đây chỉ là fast-path không xâm lấn. Nếu TikTok đổi DOM hoặc dữ liệu
                // profile page không đọc chắc chắn được, tiếp tục dùng Edit profile như cũ.
                _log.Warn("[TIKTOK_IDENTITY_PROFILE_CHECK_FAILED] " + ex.Message);
            }
        }

        // 2) Mở Edit profile / Chỉnh sửa hồ sơ.
        async Task<bool> TryClickEditProfileAsync(int timeoutMs = 10000)
            => await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const direct = document.querySelector('[data-e2e="edit-profile-entrance"], button[data-e2e*="edit-profile"], [role="button"][data-e2e*="edit-profile"]');
  if (direct) { direct.click(); return true; }
  const all = [...document.querySelectorAll('button,[role="button"],a')];
  const hit = all.find(el => {
    const t = norm(`${el.innerText || el.textContent || ''} ${el.getAttribute('aria-label') || ''}`);
    return t === 'edit profile' || t === 'chỉnh sửa hồ sơ' || t === 'sửa hồ sơ' || t === 'edit';
  });
  if (!hit) return false;
  hit.click();
  return true;
})()
""", timeoutMs, 350);

        var editClicked = await TryClickEditProfileAsync(fastNameGuardMode ? 5000 : 10000);
        if (!editClicked && usedDirectProfileHref)
        {
            // Nếu Username cũ/sai, thử lấy link Hồ sơ thật từ sidebar trước khi báo lỗi.
            var domProfileHref = await FindProfileHrefFromDomAsync();
            if (!string.IsNullOrWhiteSpace(domProfileHref)
                && !string.Equals(domProfileHref.TrimEnd('/'), profileHref.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"[TIKTOK_IDENTITY_PROFILE_DIRECT_NO_EDIT] fallback={domProfileHref}");
                profileHref = domProfileHref;
                await NavigateAndWaitAsync(profileHref, fastNameGuardMode ? 250 : 900, fastNameGuardMode ? 7000 : 18000, ct);
                editClicked = await TryClickEditProfileAsync(fastNameGuardMode ? 5000 : 8000);
            }
        }
        if (!editClicked)
        {
            // TikTok đôi lúc vừa đăng nhập xong nhưng session/UI trên profile page chưa đồng bộ:
            // URL đã đúng /@username nhưng nút Edit profile chưa render. Thực tế F5 là đủ.
            // Vì vậy reload trang rồi thử lại trước khi kết luận lỗi.
            var editProfileReloadRetries = fastNameGuardMode ? 1 : 2;

            for (var refreshAttempt = 1;
                 refreshAttempt <= editProfileReloadRetries && !editClicked;
                 refreshAttempt++)
            {
                try
                {
                    _log.Warn(
                        $"[TIKTOK_IDENTITY_EDIT_MISSING_RELOAD] href={profileHref} attempt={refreshAttempt}/{editProfileReloadRetries}");

                    await ReloadAndWaitAsync(fastNameGuardMode ? 500 : 1200, fastNameGuardMode ? 9000 : 18000, ct);
                    await Task.Delay(fastNameGuardMode ? 250 : 700, ct);

                    editClicked = await TryClickEditProfileAsync(fastNameGuardMode ? 5000 : 10000);

                    if (editClicked)
                    {
                        _log.Info(
                            $"[TIKTOK_IDENTITY_EDIT_FOUND_AFTER_RELOAD] href={profileHref} attempt={refreshAttempt}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[TIKTOK_IDENTITY_EDIT_RELOAD_WARN] href={profileHref} attempt={refreshAttempt} message={ex.Message}");
                }
            }
        }

        if (!editClicked)
            throw new InvalidOperationException($"Không tìm thấy nút Chỉnh sửa hồ sơ / Edit profile trên trang {profileHref} sau khi đã F5 thử lại. Hãy kiểm tra Username/session TikTok của profile này.");

        var editorReady = await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const dialogs = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')];
  const scope = dialogs.at(-1) || document;
  const text = norm(scope.innerText || scope.textContent || '');
  return !!scope.querySelector('input,textarea,button') && (text.includes('edit profile') || text.includes('chỉnh sửa hồ sơ') || text.includes('username') || text.includes('tên người dùng') || text.includes('name') || text.includes('tên'));
})()
""", 12000, 300);
        if (!editorReady) throw new InvalidOperationException("Đã bấm Chỉnh sửa hồ sơ nhưng không thấy form chỉnh sửa TikTok.");

        // Điều kiện bỏ qua thứ hai: TikTok đang hiện lịch khóa đổi biệt danh.
        // Chỉ cần có câu "Bạn có thể tiếp tục thay đổi biệt danh sau ..." (hoặc bản tiếng Anh)
        // thì KHÔNG chạm vào bất kỳ ô Tên/Ảnh/Tiểu sử nào. Trả NameCooldown để Manager
        // bỏ qua profile trong phiên hiện tại nhưng KHÔNG ghi DONE vào Excel.
        if (skipAllIfNameCooldown)
        {
            var hasNicknameCooldownHint = false;
            try
            {
                hasNicknameCooldownHint = IsTrue(await Eval("""
(() => {
  const fold = s => (s || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .replace(/\s+/g, ' ').trim().toLowerCase();
  const dialogs = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')];
  const scope = dialogs.at(-1) || document;
  const text = fold(scope.innerText || scope.textContent || '');
  return text.includes('ban co the tiep tuc thay doi biet danh sau')
      || text.includes('you can continue to change your nickname after')
      || text.includes('you can change your nickname again after');
})()
"""));
            }
            catch (Exception ex)
            {
                _log.Warn("[TIKTOK_IDENTITY_COOLDOWN_HINT_CHECK_FAILED] " + ex.Message);
            }

            if (hasNicknameCooldownHint)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(originalUrl)
                        && originalUrl.StartsWith("https://www.tiktok.com/", StringComparison.OrdinalIgnoreCase))
                        await NavigateAndWaitAsync(originalUrl, 700, 12000, ct);
                }
                catch { }

                _log.Info("[TIKTOK_IDENTITY_SKIP_NICKNAME_COOLDOWN_HINT] phát hiện 'Bạn có thể tiếp tục thay đổi biệt danh sau ...'; bỏ qua toàn bộ Sửa hồ sơ trong phiên này, không ghi DONE.");
                return new TikTokProfileIdentityUpdateResult(
                    false, false, false, true, false, true,
                    "TikTok đang khóa thời gian đổi biệt danh (Bạn có thể tiếp tục thay đổi biệt danh sau ...). Bỏ qua Sửa hồ sơ trong phiên hiện tại; không ghi DONE.");
            }
        }

        var nameChanged = false;
        var avatarChanged = false;
        var bioChanged = false;

        // Không đoán ngày cooldown. Chỉ nhận diện câu thông báo cooldown thực tế trong DOM
        // ở bước phía trên; nếu không có câu đó thì tiếp tục kiểm tra/cập nhật như bình thường.

        // XÁC NHẬN TRẠNG THÁI THỰC TẾ TRƯỚC KHI ĐỔI. Chỉ dùng cho Auto Identity.
        // Đây là lớp bảo vệ DONE Excel: nếu trạng thái TikTok đã phù hợp thì không đổi lại và cho phép ghi DONE.
        if (verifyExistingState)
        {
            var snapshotJson = ReadString(await Eval("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const dialogs = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].filter(visible);
  const scope = dialogs.find(d => {
    const t = norm(d.innerText || d.textContent || '');
    return (t.includes('sửa hồ sơ') || t.includes('chỉnh sửa hồ sơ') || t.includes('edit profile'))
        && (t.includes('tiktok id') || t.includes('username') || t.includes('tên') || t.includes('name'));
  }) || dialogs.at(-1) || document;

  const nearbyText = el => {
    const parts = [];
    let n = el.parentElement;
    for (let i = 0; i < 4 && n && n !== scope; i++, n = n.parentElement)
      parts.push(norm(n.innerText || n.textContent || ''));
    return parts.join(' ');
  };

  const textInputs = [...scope.querySelectorAll('input')].filter(el => {
    if (!visible(el) || el.disabled || el.readOnly) return false;
    const type = (el.type || '').toLowerCase();
    return type === '' || type === 'text';
  });
  const scoredNames = textInputs.map(el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    const near = nearbyText(el);
    let score = 0;
    if (/nickname|display.?name|edit.?name/.test(attrs)) score += 120;
    if (/biệt danh|nickname|display name/.test(near)) score += 100;
    if (/(^|\s)tên(\s|$)/.test(near) || /(^|\s)name(\s|$)/.test(near)) score += 60;
    if (/username|user.?name|tiktok.?id|unique.?id|tên người dùng/.test(attrs + ' ' + near)) score -= 300;
    return [el, score];
  }).sort((a,b) => b[1] - a[1]);
  const nameInput = scoredNames.find(x => x[1] > 0)?.[0] || (textInputs.length === 1 ? textInputs[0] : null);
  const currentName = nameInput ? String(nameInput.value || '').trim() : '';

  const areas = [...scope.querySelectorAll('textarea')].filter(el => visible(el) && !el.disabled && !el.readOnly);
  let bioArea = null;
  let bestBio = -999;
  for (const el of areas) {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    const near = nearbyText(el);
    let score = 0;
    if (/bio|biography|signature|tiểu sử/.test(attrs)) score += 120;
    if (/bio|biography|tiểu sử/.test(near)) score += 100;
    if (score > bestBio) { bestBio = score; bioArea = el; }
  }
  const currentBio = bioArea ? String(bioArea.value || '').trim() : '';

  const imgs = [...scope.querySelectorAll('img')].filter(el => {
    if (!visible(el)) return false;
    const r = el.getBoundingClientRect();
    return r.width >= 42 && r.height >= 42;
  });
  let avatar = null;
  let bestAvatar = -999;
  for (const img of imgs) {
    let n = img.parentElement;
    let near = '';
    for (let i = 0; i < 5 && n && n !== scope; i++, n = n.parentElement) near += ' ' + norm(n.innerText || n.textContent || '');
    const r = img.getBoundingClientRect();
    let score = Math.min(r.width, r.height);
    if (/ảnh hồ sơ|ảnh đại diện|profile photo|avatar/.test(near)) score += 300;
    if (score > bestAvatar) { bestAvatar = score; avatar = img; }
  }
  const avatarSrc = avatar ? String(avatar.currentSrc || avatar.src || '') : '';
  const avatarMeta = norm(`${avatarSrc} ${avatar?.alt || ''} ${avatar?.getAttribute?.('data-e2e') || ''}`);
  const avatarLooksDefault = !avatarSrc
    || /default[-_ ]?avatar|avatar[-_ ]?default|default[-_ ]?profile|placeholder|no[-_ ]?avatar|anonymous|user[-_ ]?default/.test(avatarMeta)
    || avatarSrc.startsWith('data:image/svg+xml');

  return JSON.stringify({ currentName, currentBio, avatarSrc, avatarFound: !!avatarSrc, avatarLooksDefault });
})()
"""));

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
                var root = doc.RootElement;
                var currentName = root.TryGetProperty("currentName", out var n) ? (n.GetString() ?? "") : "";
                var currentBio = root.TryGetProperty("currentBio", out var b) ? (b.GetString() ?? "") : "";
                var avatarFound = root.TryGetProperty("avatarFound", out var af) && af.ValueKind == JsonValueKind.True;
                var avatarLooksDefault = root.TryGetProperty("avatarLooksDefault", out var ad) && ad.ValueKind == JsonValueKind.True;

                static string NormText(string value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ");
                var nameOk = displayName.Length == 0
                    || knownNames.Any(x => string.Equals(NormText(x), NormText(currentName), StringComparison.OrdinalIgnoreCase));
                var avatarOk = avatarPath.Length == 0 || (avatarFound && !avatarLooksDefault);
                var bioOk = bio.Length == 0 || string.Equals(NormText(currentBio), NormText(bio), StringComparison.Ordinal);
                var hasAnyCheck = displayName.Length > 0 || avatarPath.Length > 0 || bio.Length > 0;

                _log.Info($"[TIKTOK_IDENTITY_EXISTING_CHECK] currentName={currentName} nameOk={nameOk} avatarFound={avatarFound} avatarDefault={avatarLooksDefault} avatarOk={avatarOk} bioOk={bioOk}");

                if (hasAnyCheck && nameOk && avatarOk && bioOk)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(originalUrl)
                            && originalUrl.StartsWith("https://www.tiktok.com/", StringComparison.OrdinalIgnoreCase))
                            await NavigateAndWaitAsync(originalUrl, 700, 12000, ct);
                    }
                    catch { }
                    _log.Info("[TIKTOK_IDENTITY_ALREADY_CONFIGURED] trạng thái TikTok đã phù hợp; không đổi lại, cho phép Manager ghi DONE.");
                    return new TikTokProfileIdentityUpdateResult(
                        false, false, false, false, true, true,
                        "Đã thiết lập sẵn trên TikTok: tên/ảnh/tiểu sử đang phù hợp. Không đổi lại; ghi DONE vào Excel.");
                }
            }
            catch (Exception ex)
            {
                // Xác nhận trạng thái chỉ là lớp bảo vệ trước khi đổi. Nếu DOM TikTok thay đổi và
                // không đọc được đủ dữ liệu, KHÔNG tự ghi DONE; tiếp tục luồng cập nhật bình thường.
                _log.Warn("[TIKTOK_IDENTITY_EXISTING_CHECK_FAILED] " + ex.Message);
            }
        }

        // Tên và ảnh là hai phần độc lập. Ưu tiên sửa Tên ngay khi form vừa mở
        // (đây là thời điểm DOM ổn định nhất), sau đó mới xử lý avatar.
        // Sau khi popup ảnh đóng sẽ kiểm tra lại Tên; nếu TikTok re-render làm mất giá trị
        // thì đặt lại một lần nữa trước khi bấm Lưu.
        async Task<bool> SetDisplayNameAsync()
        {
            if (displayName.Length == 0) return true;
            var setNameJs = $$"""
(() => {
  const wanted = {{JsString(displayName)}};
  const handle = {{JsString(directHandle)}};
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  const inputs = [...document.querySelectorAll('input')].filter(el => {
    if (!visible(el) || el.disabled || el.readOnly) return false;
    const type = (el.type || '').toLowerCase();
    return type === '' || type === 'text';
  });

  // Tuyệt đối không chọn ô TikTok ID/username. Nếu giá trị đang bằng @username đã lưu, bỏ qua.
  const candidates = inputs.filter(el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    const value = norm(el.value || '');
    if (/username|user.?name|tiktok.?id|unique.?id|tên người dùng/.test(attrs)) return false;
    if (handle && (value === norm(handle) || value === norm('@' + handle))) return false;
    return true;
  });

  const nearbyText = el => {
    const parts = [];
    const parent = el.parentElement;
    if (parent) {
      for (const child of [...parent.children]) {
        if (child === el || child.contains?.(el)) continue;
        const t = norm(child.innerText || child.textContent || '');
        if (t && t.length <= 100) parts.push(t);
      }
    }
    let prev = parent?.previousElementSibling;
    for (let i = 0; i < 3 && prev; i++, prev = prev.previousElementSibling) {
      const t = norm(prev.innerText || prev.textContent || '');
      if (t && t.length <= 100) parts.push(t);
    }
    return parts.join(' ');
  };

  let target = null;
  const scored = candidates.map(el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    const near = nearbyText(el);
    let score = 0;
    if (/nickname|display.?name|edit.?name/.test(attrs)) score += 120;
    if (/^tên$|^name$|biệt danh|nickname|display name/.test(near)) score += 100;
    if (/(^|\s)tên(\s|$)/.test(near) || /(^|\s)name(\s|$)/.test(near)) score += 60;
    if (/username|tiktok.?id|tên người dùng/.test(near)) score -= 300;
    return [el, score];
  }).sort((a,b) => b[1] - a[1]);

  target = scored.find(x => x[1] > 0)?.[0] || null;
  // Trên giao diện TikTok hiện tại TikTok ID là readonly, nên nếu chỉ còn một input editable
  // thì đó chính là ô Tên. Đây cũng là fallback an toàn nhất.
  if (!target && candidates.length === 1) target = candidates[0];
  if (!target) return false;

  target.focus();
  try {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (setter) setter.call(target, wanted); else target.value = wanted;
  } catch (_) { target.value = wanted; }
  target.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: wanted }));
  target.dispatchEvent(new Event('change', { bubbles: true }));
  try { target.blur(); } catch (_) {}
  return String(target.value || '') === wanted;
})()
""";
            return IsTrue(await Eval(setNameJs));
        }

        if (displayName.Length > 0)
        {
            if (!await SetDisplayNameAsync())
                throw new InvalidOperationException("Không tìm thấy hoặc không sửa được ô Tên trong form Sửa hồ sơ. Tool không thay TikTok ID.");
            nameChanged = true;
            _log.Info($"[TIKTOK_IDENTITY_NAME_SET] phase=before-avatar length={displayName.Length}");
            await Task.Delay(350, ct);
        }

        // Luồng: Sửa hồ sơ -> Sửa tên (nếu có) -> Thay ảnh -> Đăng ký ảnh ->
        // kiểm tra lại tên -> Lưu -> Xác nhận biệt danh (nếu có).
        if (avatarPath.Length > 0)
        {
            // Xử lý ảnh sau khi Tên đã được đặt (nếu có).
            var hasFileInput = IsTrue(await Eval("""(() => !!document.querySelector('input[type="file"]'))()"""));
            if (!hasFileInput)
            {
                await Eval("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const all = [...document.querySelectorAll('button,[role="button"],label')].filter(visible);
  const byText = all.find(el => {
    const t = norm(`${el.innerText || el.textContent || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    return /change photo|edit photo|profile photo|avatar|đổi ảnh|thay ảnh|ảnh đại diện|chỉnh sửa ảnh/.test(t);
  });
  const byImage = all.find(el => el.querySelector?.('img'));
  const hit = byText || byImage;
  if (!hit) return false;
  hit.click();
  return true;
})()
""");
                if (!await WaitBoolAsync("""(() => !!document.querySelector('input[type="file"]'))()""", 7000, 250))
                    throw new InvalidOperationException("Không tìm thấy ô chọn file ảnh đại diện sau khi mở phần Avatar.");
            }

            var fileEval = await Cdp.CallAsync("Runtime.evaluate", new
            {
                expression = "(()=>document.querySelector('input[type=\\\"file\\\"][accept*=\\\"image\\\"]') || document.querySelector('input[type=\\\"file\\\"]'))()",
                awaitPromise = false,
                returnByValue = false,
                userGesture = true
            }, ct);
            if (!fileEval.TryGetProperty("result", out var fileResult)
                || !fileResult.TryGetProperty("objectId", out var objectIdElement)
                || string.IsNullOrWhiteSpace(objectIdElement.GetString()))
                throw new InvalidOperationException("Không lấy được DOM object của input file avatar.");

            await Cdp.CallAsync("DOM.setFileInputFiles", new
            {
                files = new[] { avatarPath },
                objectId = objectIdElement.GetString()
            }, ct);

            avatarChanged = true;
            _log.Info($"[TIKTOK_IDENTITY_AVATAR_FILE_SET] file={Path.GetFileName(avatarPath)}");
            await Task.Delay(700, ct);

            // 2) POPUP CHỈNH SỬA ẢNH -> BẮT BUỘC BẤM "ĐĂNG KÝ".
            var cropApplyFound = await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'đăng ký' || bt === 'apply' || bt === 'áp dụng' || bt === 'done' || bt === 'xong')) continue;
    let n = b;
    for (let i = 0; i < 9 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if ((t.includes('chỉnh sửa ảnh') || t.includes('edit photo') || t.includes('edit image'))
          && (t.includes('thu phóng') || t.includes('zoom') || t.includes('hủy') || t.includes('cancel'))) {
        return true;
      }
    }
  }
  return false;
})()
""", 10000, 250);

            if (!cropApplyFound)
                throw new InvalidOperationException("Sau khi chọn ảnh không tìm thấy popup Chỉnh sửa ảnh / nút Đăng ký.");

            var cropApplied = IsTrue(await Eval("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'đăng ký' || bt === 'apply' || bt === 'áp dụng' || bt === 'done' || bt === 'xong')) continue;
    let n = b;
    for (let i = 0; i < 9 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if ((t.includes('chỉnh sửa ảnh') || t.includes('edit photo') || t.includes('edit image'))
          && (t.includes('thu phóng') || t.includes('zoom') || t.includes('hủy') || t.includes('cancel'))) {
        b.click();
        return true;
      }
    }
  }
  return false;
})()
"""));
            if (!cropApplied)
                throw new InvalidOperationException("Đã thấy Chỉnh sửa ảnh nhưng không bấm được nút Đăng ký.");

            _log.Info("[TIKTOK_IDENTITY_AVATAR_APPLY_CLICKED] action=Đăng ký/Apply");

            var cropClosed = await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'đăng ký' || bt === 'apply' || bt === 'áp dụng' || bt === 'done' || bt === 'xong')) continue;
    let n = b;
    for (let i = 0; i < 9 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if ((t.includes('chỉnh sửa ảnh') || t.includes('edit photo') || t.includes('edit image'))
          && (t.includes('thu phóng') || t.includes('zoom') || t.includes('hủy') || t.includes('cancel'))) {
        return false;
      }
    }
  }
  return true;
})()
""", 12000, 250);
            if (!cropClosed)
                throw new InvalidOperationException("Đã bấm Đăng ký ảnh nhưng popup Chỉnh sửa ảnh chưa đóng.");

            await Task.Delay(500, ct);
        }

        if (displayName.Length > 0 && avatarChanged)
        {
            // Upload/crop avatar có thể khiến TikTok re-render form. Kiểm tra lại giá trị Tên;
            // nếu bị mất thì đặt lại, nhưng không bắt buộc ảnh phải được xử lý trước tên.
            var nameStillSet = IsTrue(await Eval($$"""
(() => {
  const wanted = {{JsString(displayName)}};
  const handle = {{JsString(directHandle)}};
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden'; };
  const inputs = [...document.querySelectorAll('input')].filter(el => visible(el) && !el.disabled && !el.readOnly && ['', 'text'].includes((el.type || '').toLowerCase()));
  return inputs.some(el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''}`);
    const value = norm(el.value || '');
    if (/username|user.?name|tiktok.?id|unique.?id|tên người dùng/.test(attrs)) return false;
    if (handle && (value === norm(handle) || value === norm('@' + handle))) return false;
    return String(el.value || '') === wanted;
  });
})()
"""));
            if (!nameStillSet)
            {
                if (!await SetDisplayNameAsync())
                    throw new InvalidOperationException("Ảnh đã cập nhật nhưng TikTok làm mới form và không thể đặt lại ô Tên. Tool không thay TikTok ID.");
                _log.Info($"[TIKTOK_IDENTITY_NAME_SET] phase=after-avatar-reapply length={displayName.Length}");
                await Task.Delay(350, ct);
            }
            else
            {
                _log.Info("[TIKTOK_IDENTITY_NAME_PRESERVED_AFTER_AVATAR]");
            }
        }

        if (bio.Length > 0)
        {
            var bioSet = IsTrue(await Eval($$"""
(() => {
  const wanted = {{JsString(bio)}};
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const areas = [...document.querySelectorAll('textarea')].filter(el => visible(el) && !el.disabled && !el.readOnly);
  if (!areas.length) return false;
  const score = el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    let near = '';
    let n = el.parentElement;
    for (let i = 0; i < 4 && n; i++, n = n.parentElement) near += ' ' + norm(n.innerText || n.textContent || '');
    let s = 0;
    if (/bio|biography|signature|tiểu sử/.test(attrs)) s += 120;
    if (/tiểu sử|bio|biography/.test(near)) s += 100;
    return s;
  };
  const target = areas.map(el => [el, score(el)]).sort((a,b) => b[1]-a[1])[0]?.[0] || null;
  if (!target) return false;
  target.focus();
  try {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
    if (setter) setter.call(target, wanted); else target.value = wanted;
  } catch (_) { target.value = wanted; }
  target.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: wanted }));
  target.dispatchEvent(new Event('change', { bubbles: true }));
  try { target.blur(); } catch (_) {}
  return String(target.value || '') === wanted;
})()
"""));
            if (!bioSet)
                throw new InvalidOperationException("Không tìm thấy hoặc không sửa được ô Tiểu sử/Bio trong form Sửa hồ sơ.");
            bioChanged = true;
            _log.Info($"[TIKTOK_IDENTITY_BIO_SET] length={bio.Length}");
            await Task.Delay(300, ct);
        }

        // 4) BẤM LƯU TRONG FORM "SỬA HỒ SƠ".
        var profileSaved = await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    if (b.disabled || b.getAttribute('aria-disabled') === 'true') continue;
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'lưu' || bt === 'save' || bt === 'save changes' || bt === 'lưu thay đổi')) continue;
    let n = b;
    for (let i = 0; i < 10 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if ((t.includes('sửa hồ sơ') || t.includes('chỉnh sửa hồ sơ') || t.includes('edit profile'))
          && (t.includes('tiktok id') || t.includes('tiểu sử') || t.includes('bio') || t.includes('profile photo') || t.includes('ảnh hồ sơ'))) {
        b.click();
        return true;
      }
    }
  }
  return false;
})()
""", fastNameGuardMode ? 8000 : 12000, fastNameGuardMode ? 250 : 300);
        if (!profileSaved)
            throw new InvalidOperationException("Không tìm thấy nút Lưu/Save đang khả dụng trong form Sửa hồ sơ.");

        _log.Info("[TIKTOK_IDENTITY_PROFILE_SAVE_CLICKED]");

        // 5) SAU LƯU: bấm Xác nhận ở popup "Đặt biệt danh?" nếu có.
        var saveConfirmPresent = await WaitBoolAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'xác nhận' || bt === 'confirm' || bt === 'lưu' || bt === 'save')) continue;
    let n = b;
    for (let i = 0; i < 9 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if (t.includes('đặt biệt danh')
          || t.includes('biệt danh 7 ngày')
          || t.includes('7 ngày 1 lần')
          || t.includes('set nickname')
          || t.includes('nickname')
          || t.includes('lưu hồ sơ')
          || t.includes('save profile')) {
        return true;
      }
    }
  }
  return false;
})()
""", fastNameGuardMode ? 1500 : 5000, 200);

        if (nameChanged && !saveConfirmPresent)
        {
            // TikTok không phải lúc nào cũng hiện popup Xác nhận. Không kết luận FAIL
            // ở đây; bước xác minh sau reload bên dưới mới là nguồn sự thật.
            _log.Warn("[TIKTOK_IDENTITY_FINAL_CONFIRM_NOT_SHOWN] Đã bấm Lưu nhưng không thấy popup Xác nhận; tiếp tục reload + đọc tên thực tế trước khi kết luận.");
        }

        if (saveConfirmPresent)
        {
            var finalSaved = IsTrue(await Eval("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const buttons = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
  for (const b of buttons) {
    if (b.disabled || b.getAttribute('aria-disabled') === 'true') continue;
    const bt = norm(b.innerText || b.textContent || b.getAttribute('aria-label') || '');
    if (!(bt === 'xác nhận' || bt === 'confirm' || bt === 'lưu' || bt === 'save')) continue;
    let n = b;
    for (let i = 0; i < 9 && n; i++, n = n.parentElement) {
      const t = norm(n.innerText || n.textContent || '');
      if (t.includes('đặt biệt danh')
          || t.includes('biệt danh 7 ngày')
          || t.includes('7 ngày 1 lần')
          || t.includes('set nickname')
          || t.includes('nickname')
          || t.includes('lưu hồ sơ')
          || t.includes('save profile')) {
        b.click();
        return true;
      }
    }
  }
  return false;
})()
"""));
            if (!finalSaved)
            {
                // Popup Xác nhận đã xuất hiện rõ ràng nhưng tool không bấm được nút.
                // Đây là một bước bắt buộc của lần Save này: không được trả success/DONE giả.
                _log.Warn("[TIKTOK_IDENTITY_FINAL_CONFIRM_CLICK_MISS] thấy popup nhưng không bấm được Xác nhận; kết luận FAIL cho lần xử lý này.");
                throw new InvalidOperationException(
                    "TikTok đã hiện popup Xác nhận sau khi Lưu nhưng tool không bấm được nút Xác nhận/Confirm.");
            }
            else
            {
                _log.Info("[TIKTOK_IDENTITY_FINAL_CONFIRM_CLICKED] action=Xác nhận/Confirm");
                await Task.Delay(fastNameGuardMode ? 300 : 700, ct);
            }
        }

        await Task.Delay(fastNameGuardMode ? 450 : 1800, ct);
        _log.Info($"[TIKTOK_IDENTITY_UPDATE_SAVED] name={nameChanged} avatar={avatarChanged} bio={bioChanged} cooldown=false finalConfirm={saveConfirmPresent} fastNameGuard={fastNameGuardMode}");

        // Tên TikTok có thể chưa đổi ngay trên SPA dù Save/Confirm đã chạy.
        // Vì vậy không dùng kết quả click làm điều kiện DONE. Với mọi lần có thao tác tên,
        // bắt buộc quay lại profile, chờ 3 giây, F5, chờ DOM ổn định rồi đọc nickname thực tế.
        // Nếu lần đầu chưa cập nhật, lặp thêm một vòng trước khi kết luận lỗi.
        if (!fastNameGuardMode && displayName.Length > 0 && nameChanged)
        {
            var verifiedName = false;
            var lastSeenName = "";
            const int verifyAttempts = 2;

            for (var attempt = 1; attempt <= verifyAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await NavigateAndWaitAsync(profileHref, 1000, 18000, ct);
                    _log.Info($"[TIKTOK_IDENTITY_NAME_VERIFY_WAIT] attempt={attempt}/{verifyAttempts} delayMs=3000");
                    await Task.Delay(3000, ct);

                    await ReloadAndWaitAsync(1100, 18000, ct);
                    await Task.Delay(2200, ct);

                    lastSeenName = await ReadProfileDisplayNameAsync();
                    verifiedName = IsExpectedDisplayName(lastSeenName, acceptAnyKnownName: false);
                    _log.Info($"[TIKTOK_IDENTITY_NAME_VERIFY] attempt={attempt}/{verifyAttempts} currentName={lastSeenName} ok={verifiedName}");
                    if (verifiedName) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Warn($"[TIKTOK_IDENTITY_NAME_VERIFY_WARN] attempt={attempt}/{verifyAttempts} message={ex.Message}");
                }

                if (attempt < verifyAttempts)
                    await Task.Delay(2500, ct);
            }

            if (!verifiedName)
                throw new InvalidOperationException(
                    $"TikTok chưa hiển thị tên mới sau {verifyAttempts} lần chờ + F5. Tên đọc được: '{lastSeenName}'. Giữ trạng thái để retry, không ghi DONE/FAIL giả.");

            _log.Info($"[TIKTOK_IDENTITY_NAME_VERIFIED_AFTER_RELOAD] currentName={lastSeenName} result=success");
        }

        // Chỉ sau khi verify xong mới trả trang về vị trí trước đó.
        if (!fastNameGuardMode
            && !string.IsNullOrWhiteSpace(originalUrl)
            && originalUrl.StartsWith("https://www.tiktok.com/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(originalUrl.TrimEnd('/'), (Page?.Url ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await NavigateAndWaitAsync(originalUrl, 900, 15000, ct);
                _log.Info("[TIKTOK_IDENTITY_RETURN_URL_READY] page=stable-after-verify");
            }
            catch (Exception ex)
            {
                // Trả trang chỉ là thao tác phụ; trạng thái tên đã được verify ở profile page.
                _log.Warn("[TIKTOK_IDENTITY_RETURN_URL] " + ex.Message);
            }
        }

        var pieces = new List<string>();
        if (nameChanged) pieces.Add("tên");
        if (avatarChanged) pieces.Add("ảnh");
        if (bioChanged) pieces.Add("tiểu sử");
        var message = pieces.Count > 0 ? string.Join(" + ", pieces) : "Không có thay đổi";
        return new TikTokProfileIdentityUpdateResult(
            nameChanged, avatarChanged, bioChanged, false, false, false,
            fastNameGuardMode ? "Đã lưu thành công: " + message : "Đã xử lý và xác minh: " + message);
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

    public async Task<IReadOnlyList<TikTokRecommendedLiveCandidate>> GetTikTokRecommendedLivesAsync(CancellationToken ct = default)
    {
        const string js = """
(() => {
  const visible = (el) => {
    if (!el || !el.getBoundingClientRect) return false;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const s = getComputedStyle(el);
    return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity || 1) > 0.05;
  };
  const normalize = (value) => String(value || '').replace(/\s+/g, ' ').trim();
  const cleanLiveHref = (value) => {
    if (!value) return '';
    try {
      const u = new URL(value, location.href);
      if (!/tiktok\.com$/i.test(u.hostname) && !/\.tiktok\.com$/i.test(u.hostname)) return '';
      if (!/^\/@[^/]+\/live\/?$/i.test(u.pathname)) return '';
      u.search = '';
      u.hash = '';
      return u.toString().replace(/\/$/, '');
    } catch { return ''; }
  };
  const currentHref = cleanLiveHref(location.href);
  const headingTexts = ['nhà sáng tạo live đề xuất', 'recommended live creators', 'live creators recommended'];
  const heading = Array.from(document.querySelectorAll('h1,h2,h3,h4,div,span,p'))
    .filter(visible)
    .find(el => {
      const t = normalize(el.innerText || el.textContent).toLocaleLowerCase('vi-VN');
      return t.length <= 90 && headingTexts.some(x => t.includes(x));
    });

  let scope = null;
  if (heading) {
    let n = heading;
    for (let depth = 0; depth < 9 && n; depth++, n = n.parentElement) {
      const links = Array.from(n.querySelectorAll('a[href]'))
        .map(a => cleanLiveHref(a.href || a.getAttribute('href')))
        .filter(Boolean);
      const unique = new Set(links);
      const r = n.getBoundingClientRect?.();
      const leftish = !r || r.left < innerWidth * 0.42;
      if (leftish && unique.size > 0 && unique.size <= 40) {
        scope = n;
        break;
      }
    }
  }

  const anchors = Array.from((scope || document).querySelectorAll('a[href]'));
  const out = [];
  const seen = new Set();
  for (const a of anchors) {
    if (!visible(a)) continue;
    const href = cleanLiveHref(a.href || a.getAttribute('href'));
    if (!href || href === currentHref || seen.has(href)) continue;

    const ar = a.getBoundingClientRect();
    if (!scope && ar.left > innerWidth * 0.36) continue;

    let bestViewer = '';
    let bestX = -1;
    let bestLabel = normalize(a.innerText || a.textContent || a.getAttribute('aria-label') || '');
    let node = a;
    for (let up = 0; up < 6 && node; up++, node = node.parentElement) {
      const nr = node.getBoundingClientRect?.();
      if (nr && nr.height > 180) break;
      const leaves = Array.from(node.querySelectorAll('span,div,strong,p'));
      for (const el of leaves) {
        if (!visible(el) || el.children.length > 0) continue;
        const txt = normalize(el.innerText || el.textContent);
        if (!/^[0-9]+(?:[\.,][0-9]+)?\s*[KMB]?$/i.test(txt)) continue;
        const r = el.getBoundingClientRect();
        const verticalNear = Math.abs((r.top + r.bottom) / 2 - (ar.top + ar.bottom) / 2) <= 55;
        if (!verticalNear) continue;
        if (r.left > bestX) {
          bestX = r.left;
          bestViewer = txt;
        }
      }
      const text = normalize(node.innerText || node.textContent || '');
      if (text.length > bestLabel.length && text.length < 180) bestLabel = text;
    }

    if (!bestViewer) continue;
    const m = href.match(/\/@([^/]+)\/live$/i);
    const username = m ? decodeURIComponent(m[1]) : '';
    seen.add(href);
    out.push({ href, username, viewerText: bestViewer, label: bestLabel });
  }
  return out;
})()
""";

        var result = await EvalAsync(js, ct: ct);
        if (!result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<TikTokRecommendedLiveCandidate>();

        static string ReadString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";

        var list = new List<TikTokRecommendedLiveCandidate>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var href = ReadString(item, "href");
            var viewerText = ReadString(item, "viewerText");
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(viewerText)) continue;
            list.Add(new TikTokRecommendedLiveCandidate(
                href,
                ReadString(item, "username"),
                viewerText,
                ReadString(item, "label")));
        }
        return list;
    }

    public async Task<TikTokStartupResult> PrepareTikTokStartupAsync(
        string username,
        string password,
        string totpSecret,
        bool autoLogin = true,
        bool openLiveWhenReady = true,
        bool stopOnCaptcha = false,
        CancellationToken ct = default)
    {
        const string loginUrl = "https://www.tiktok.com/login/phone-or-email/email";

        // Authentication and LIVE navigation are deliberately separable. Opening a
        // profile runs the normal auto-login flow but stays on TikTok home; pressing
        // Bắt đầu runs the same authentication gate and then opens LIVE. Session-cookie
        // detection is done through CDP so HttpOnly cookies are included.
        try
        {
            if (await HasTikTokSessionCookieAsync(ct))
            {
                return await FinalizeTikTokAuthenticatedAsync(
                    openLiveWhenReady,
                    "Đã đăng nhập; đã mở TikTok LIVE và xử lý màn 'Nhấp để xem LIVE' nếu có.",
                    "Đã đăng nhập; giữ TikTok ở trang chủ, chưa vào LIVE.",
                    ct);
            }
        }
        catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

        if (!autoLogin || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            if (openLiveWhenReady)
                await OpenTikTokLiveReadyAsync(ct);
            else
                await OpenTikTokHomeReadyAsync(ct);
            return new TikTokStartupResult("LOGIN_REQUIRED", "Profile chưa có phiên đăng nhập hoặc chưa lưu tài khoản/mật khẩu.", false, openLiveWhenReady);
        }

        _log.Info("[TIKTOK_LOGIN_START] auto=true usernameConfigured=true totpConfigured=" + (!string.IsNullOrWhiteSpace(totpSecret)));
        await NavigateAndWaitAsync(loginUrl, 1000, 15000, ct);

        var formReady = await WaitForLoginFormAsync(TimeSpan.FromSeconds(15), ct);
        if (!formReady)
        {
            if (await DetectCaptchaAsync(ct))
            {
                if (stopOnCaptcha)
                {
                    _log.Warn("[TIKTOK_LOGIN_CAPTCHA_PAUSE] phase=before-login-form action=RETURN_TO_MANAGER");
                    return new TikTokStartupResult("CAPTCHA_REQUIRED", "Phát hiện CAPTCHA khi đăng nhập; Auto Profile tạm dừng profile này để xử lý sau.", false, false);
                }
                _log.Warn("[TIKTOK_LOGIN_CAPTCHA_WAIT] phase=before-login-form manual_action_required=true action=WAIT_FOR_USER");
                var solved = await WaitForCaptchaResolutionAsync(TimeSpan.FromMinutes(15), ct);
                if (!solved)
                    return new TikTokStartupResult("CAPTCHA_REQUIRED", "CAPTCHA vẫn chưa được xử lý sau thời gian chờ. Hãy xử lý trên Chrome rồi bấm Bắt đầu lại.", false, false);

                // CAPTCHA may have completed an already-started login, may reveal
                // the normal login form, or may go directly to 2FA. Do not
                // navigate away here: keep the current TikTok authentication state.
                if (await HasTikTokSessionCookieAsync(ct))
                {
                    return await FinalizeTikTokAuthenticatedAsync(
                        openLiveWhenReady,
                        "CAPTCHA đã xử lý; TikTok đã đăng nhập và LIVE đã mở.",
                        "CAPTCHA đã xử lý; TikTok đã đăng nhập và đã về trang chủ.",
                        ct);
                }

                if (await DetectTotpChallengeAsync(ct))
                {
                    if (string.IsNullOrWhiteSpace(totpSecret))
                        return new TikTokStartupResult("TOTP_REQUIRED", "TikTok yêu cầu mã 2FA nhưng profile chưa lưu secret TOTP.", false, false);

                    await FillAndSubmitTotpAsync(totpSecret, ct);
                    var afterTotp = await WaitForTikTokLoginCompletionAsync(totpSecret, TimeSpan.FromSeconds(45), openLiveWhenReady, stopOnCaptcha, ct);
                    if (afterTotp is not null) return afterTotp;
                }

                formReady = await WaitForLoginFormAsync(TimeSpan.FromSeconds(15), ct);
            }

            if (!formReady)
            {
                if (await HasTikTokSessionCookieAsync(ct))
                {
                    return await FinalizeTikTokAuthenticatedAsync(
                        openLiveWhenReady,
                        "TikTok đã có phiên đăng nhập; đã mở LIVE.",
                        "TikTok đã có phiên đăng nhập; đã về trang chủ.",
                        ct);
                }
                return new TikTokStartupResult("LOGIN_FORM_NOT_FOUND", "Không tìm thấy form đăng nhập TikTok sau khi chờ CAPTCHA/trang tải xong.", false, false);
            }
        }

        // WaitForLoginFormAsync also notices a session cookie so the wait can
        // finish quickly during redirects. Re-check here before touching the form.
        if (await HasTikTokSessionCookieAsync(ct))
        {
            return await FinalizeTikTokAuthenticatedAsync(
                openLiveWhenReady,
                "TikTok đã đăng nhập trong lúc chờ; đã mở LIVE.",
                "TikTok đã đăng nhập trong lúc chờ; đã về trang chủ.",
                ct);
        }

        await FillTikTokLoginFormAsync(username, password, ct);
        await ClickTikTokLoginSubmitAsync(ct);

        var completion = await WaitForTikTokLoginCompletionAsync(totpSecret, TimeSpan.FromSeconds(45), openLiveWhenReady, stopOnCaptcha, ct);
        if (completion is not null) return completion;

        if (await HasTikTokSessionCookieAsync(ct))
        {
            return await FinalizeTikTokAuthenticatedAsync(
                openLiveWhenReady,
                "Đăng nhập thành công; đã mở TikTok LIVE.",
                "Đăng nhập thành công; đã về trang chủ TikTok, chưa vào LIVE.",
                ct);
        }

        return new TikTokStartupResult("LOGIN_FAILED", "Đăng nhập TikTok chưa thành công sau thời gian chờ.", false, false);
    }

    async Task<TikTokStartupResult?> WaitForTikTokLoginCompletionAsync(
        string totpSecret,
        TimeSpan activeLoginTimeout,
        bool openLiveWhenReady,
        bool stopOnCaptcha,
        CancellationToken ct)
    {
        var loginDeadline = DateTime.UtcNow + activeLoginTimeout;
        var totpTried = false;

        while (DateTime.UtcNow < loginDeadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await HasTikTokSessionCookieAsync(ct))
            {
                _log.Info("[TIKTOK_LOGIN_OK] sessionCookie=true");
                return await FinalizeTikTokAuthenticatedAsync(
                    openLiveWhenReady,
                    "Đăng nhập thành công; đã mở TikTok LIVE và xử lý màn 'Nhấp để xem LIVE' nếu có.",
                    "Đăng nhập thành công; đã về trang chủ TikTok, chưa vào LIVE.",
                    ct);
            }

            if (await DetectCaptchaAsync(ct))
            {
                if (stopOnCaptcha)
                {
                    _log.Warn("[TIKTOK_LOGIN_CAPTCHA_PAUSE] phase=after-submit action=RETURN_TO_MANAGER");
                    return new TikTokStartupResult("CAPTCHA_REQUIRED", "Phát hiện CAPTCHA sau khi gửi đăng nhập; Auto Profile tạm dừng profile này để xử lý sau.", false, false);
                }
                // Old behavior returned CAPTCHA_REQUIRED immediately, which made
                // StartAsync exit. Keep this call alive while the user solves the
                // CAPTCHA manually, then continue the SAME login flow so 2FA can
                // be filled automatically afterwards.
                _log.Warn("[TIKTOK_LOGIN_CAPTCHA_WAIT] phase=after-submit manual_action_required=true action=WAIT_FOR_USER");
                var solved = await WaitForCaptchaResolutionAsync(TimeSpan.FromMinutes(15), ct);
                if (!solved)
                    return new TikTokStartupResult("CAPTCHA_REQUIRED", "CAPTCHA vẫn chưa được xử lý sau thời gian chờ. Hãy xử lý trên Chrome rồi bấm Bắt đầu lại.", false, false);

                _log.Info("[TIKTOK_LOGIN_CAPTCHA_RESUMED] solved=true action=CONTINUE_LOGIN_FLOW");
                // User time spent solving CAPTCHA must not consume the normal
                // login timeout. Give the post-CAPTCHA/2FA flow a fresh window.
                loginDeadline = DateTime.UtcNow.AddSeconds(60);
                continue;
            }

            if (!totpTried && await DetectTotpChallengeAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(totpSecret))
                {
                    _log.Warn("[TIKTOK_LOGIN_2FA] totpRequired=true secretConfigured=false");
                    return new TikTokStartupResult("TOTP_REQUIRED", "TikTok yêu cầu mã 2FA nhưng profile chưa lưu secret TOTP.", false, false);
                }

                await FillAndSubmitTotpAsync(totpSecret, ct);
                totpTried = true;
                loginDeadline = DateTime.UtcNow.AddSeconds(45);
                continue;
            }

            await Task.Delay(450, ct);
        }

        return null;
    }

    async Task<TikTokStartupResult> FinalizeTikTokAuthenticatedAsync(
        bool openLiveWhenReady,
        string liveMessage,
        string homeMessage,
        CancellationToken ct)
    {
        if (openLiveWhenReady)
        {
            await OpenTikTokLiveReadyAsync(ct);
            return new TikTokStartupResult("READY", liveMessage, true, true);
        }

        await OpenTikTokHomeReadyAsync(ct);
        return new TikTokStartupResult("READY", homeMessage, true, false);
    }

    async Task OpenTikTokHomeReadyAsync(CancellationToken ct)
    {
        await NavigateAndWaitAsync(TikTokUrl, 700, 15000, ct);
        _log.Info("[TIKTOK_HOME_READY] authenticated=true liveNavigation=false");
    }

    async Task<bool> WaitForCaptchaResolutionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var clearConfirmations = 0;
        _log.Warn($"[TIKTOK_CAPTCHA_WAIT_START] timeoutSec={(int)timeout.TotalSeconds}");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await HasTikTokSessionCookieAsync(ct))
                {
                    _log.Info("[TIKTOK_CAPTCHA_WAIT_DONE] reason=session-cookie");
                    return true;
                }

                if (!await DetectCaptchaAsync(ct))
                {
                    // Require two consecutive clear reads so a transient iframe
                    // re-render does not resume the login flow too early.
                    clearConfirmations++;
                    if (clearConfirmations >= 2)
                    {
                        _log.Info("[TIKTOK_CAPTCHA_WAIT_DONE] reason=captcha-cleared");
                        return true;
                    }
                }
                else
                {
                    clearConfirmations = 0;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                clearConfirmations = 0;
            }

            await Task.Delay(700, ct);
        }

        _log.Warn("[TIKTOK_CAPTCHA_WAIT_TIMEOUT] captchaStillPresent=true");
        return false;
    }

    async Task OpenTikTokLiveReadyAsync(CancellationToken ct)
    {
        const string liveUrl = "https://www.tiktok.com/live";

        // Startup V13.7.2:
        // Chrome/CDP ổn định -> tiktok.com -> PAGE_READY -> nghỉ 3-5s -> mới vào LIVE.
        await WarmUpTikTokHomeAsync(ct);

        _log.Info(
            "[TIKTOK_LIVE_NAVIGATE_AFTER_HOME_READY] target=https://www.tiktok.com/live");

        await NavigateAndWaitAsync(
            liveUrl,
            900,
            15000,
            ct);

        var health =
            await WaitForHealthyPageAfterNavigateAsync(
                25000,
                ct);

        if (health.Reason.Equals(
                "HTTP_403",
                StringComparison.OrdinalIgnoreCase))
        {
            // Lúc này Page.Url thường đã là /@user/live; Recover sẽ giữ đúng URL đó.
            await RecoverHttp403ViaHomeAsync(
                Page?.Url ?? liveUrl,
                ct);
        }
        else if (!health.Healthy)
        {
            throw new InvalidOperationException(
                $"TikTok LIVE chưa khỏe sau navigation: {health.Reason}");
        }

        await EnsureTikTokLivePlaybackStartedAsync(ct);
    }

    public async Task ResetTikTokLiveRecommendationFeedAsync(CancellationToken ct = default)
    {
        _log.Warn("[TIKTOK_LIVE_FEED_RESET] navigate=https://www.tiktok.com/live reason=viewer-low-streak");
        await OpenTikTokLiveReadyAsync(ct);
        _log.Warn("[TIKTOK_LIVE_FEED_RESET_DONE] /live đã sẵn sàng sau hard reset nguồn đề xuất.");
    }

    async Task EnsureTikTokLivePlaybackStartedAsync(CancellationToken ct)
    {
        // TikTok sometimes opens /live behind an interaction gate such as
        // "Nhấp để xem LIVE". The old startup gate considered the URL itself
        // READY, so automation started while the player was still blocked.
        // Click the prompt through DOM/CDP only; never use Windows coordinates.
        var deadline = DateTime.UtcNow.AddSeconds(12);
        var clickCount = 0;
        var noPromptReads = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string state;
            try
            {
                state = await TryClickTikTokLiveWatchPromptAsync(ct);
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                noPromptReads = 0;
                await Task.Delay(350, ct);
                continue;
            }

            if (state.StartsWith("CLICKED", StringComparison.Ordinal))
            {
                clickCount++;
                noPromptReads = 0;
                _log.Info($"[TIKTOK_LIVE_WATCH_PROMPT] action=CLICK_DOM count={clickCount} detail={TrimForLog(state, 120)}");
                await Task.Delay(1100, ct);
                continue;
            }

            noPromptReads++;
            // After a successful click only two clear polls are needed. When the
            // prompt was not seen yet, observe the page for ~3 seconds because the
            // TikTok player/interaction overlay can render after document.readyState.
            var neededClearReads = clickCount > 0 ? 2 : 6;
            if (noPromptReads >= neededClearReads)
            {
                if (clickCount > 0)
                    await Task.Delay(500, ct);
                _log.Info($"[TIKTOK_LIVE_READY] watchPromptClicks={clickCount} stableClearReads={noPromptReads}");
                return;
            }

            await Task.Delay(500, ct);
        }

        _log.Warn($"[TIKTOK_LIVE_WATCH_PROMPT_TIMEOUT] watchPromptClicks={clickCount}; tiếp tục vì prompt không còn click được/DOM đang thay đổi.");
    }

    async Task<string> TryClickTikTokLiveWatchPromptAsync(CancellationToken ct)
    {
        var r = await EvalAsync("""
(() => {
  const visible = e => {
    if (!e) return false;
    const r = e.getBoundingClientRect();
    const s = getComputedStyle(e);
    return r.width > 2 && r.height > 2 && s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity || 1) > 0.02;
  };
  const norm = x => String(x || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .toLowerCase().replace(/đ/g, 'd').replace(/\s+/g, ' ').trim();
  const isWanted = text => {
    const t = norm(text);
    return t.includes('nhap de xem live')
      || t.includes('nhap de xem livestream')
      || t.includes('click to watch live')
      || t.includes('tap to watch live')
      || t.includes('click to view live');
  };
  const clickable = e => {
    if (!e) return null;
    return e.closest('button,[role="button"],a,[tabindex]') || e;
  };
  const nodes = [...document.querySelectorAll('button,[role="button"],a,div,span')]
    .filter(visible)
    .filter(e => isWanted(e.innerText || e.textContent || e.getAttribute('aria-label') || ''))
    .sort((a,b) => (a.innerText || a.textContent || '').length - (b.innerText || b.textContent || '').length);
  if (!nodes.length) return 'NOT_FOUND';
  const source = nodes[0];
  const target = clickable(source);
  if (!target || !visible(target)) return 'NOT_CLICKABLE';
  const label = norm(source.innerText || source.textContent || source.getAttribute('aria-label') || '').slice(0, 80);
  target.scrollIntoView({block:'center', inline:'center'});
  target.click();
  return 'CLICKED:' + label;
})()
""", ct: ct);
        return r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
    }

    public Task<bool> IsTikTokSessionActiveAsync(CancellationToken ct = default)
        => HasTikTokSessionCookieAsync(ct);

    async Task<bool> HasTikTokSessionCookieAsync(CancellationToken ct)
    {
        try
        {
            var result = await Cdp.CallAsync("Network.getAllCookies", new { }, ct);
            if (!result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return false;
            foreach (var cookie in cookies.EnumerateArray())
            {
                var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                if (!domain.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)) continue;
                var name = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                if (value.Length == 0) continue;
                if (name.Equals("sessionid", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("sessionid_ss", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("sid_tt", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    async Task<bool> WaitForLoginFormAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var r = await EvalAsync("""
(() => {
  const visible = e => { if(!e) return false; const r=e.getBoundingClientRect(); const s=getComputedStyle(e); return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'; };
  const u = document.querySelector('input[name="username"],input[autocomplete="username"],input[type="text"]');
  const p = document.querySelector('input[type="password"],input[name="password"]');
  return !!(u && p && visible(u) && visible(p));
})()
""", ct: ct);
            if (r.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True) return true;
            if (await HasTikTokSessionCookieAsync(ct)) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }

    async Task FillTikTokLoginFormAsync(string username, string password, CancellationToken ct)
    {
        var js = $$"""
(() => {
  const set = (el, value) => {
    if(!el) return false;
    el.focus();
    const proto = el instanceof HTMLInputElement ? HTMLInputElement.prototype : Object.getPrototypeOf(el);
    const desc = Object.getOwnPropertyDescriptor(proto,'value');
    if(desc && desc.set) desc.set.call(el,value); else el.value=value;
    el.dispatchEvent(new Event('input',{bubbles:true}));
    el.dispatchEvent(new Event('change',{bubbles:true}));
    el.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true,key:'Unidentified'}));
    return true;
  };
  const u = document.querySelector('input[name="username"],input[autocomplete="username"],input[type="text"]');
  const p = document.querySelector('input[type="password"],input[name="password"]');
  return set(u,{{JsString(username)}}) && set(p,{{JsString(password)}});
})()
""";
        var r = await EvalAsync(js, ct: ct);
        if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Không điền được tài khoản/mật khẩu vào form TikTok.");
        _log.Info("[TIKTOK_LOGIN_FORM_FILLED] username=true password=true");
    }

    async Task ClickTikTokLoginSubmitAsync(CancellationToken ct)
    {
        var r = await EvalAsync("""
(() => {
  const visible = e => { if(!e) return false; const r=e.getBoundingClientRect(); const s=getComputedStyle(e); return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'; };
  const norm = x => String(x||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/đ/g,'d').trim();
  const candidates = [...document.querySelectorAll('button[type="submit"],button,[role="button"]')].filter(visible);
  const btn = candidates.find(e => e.matches('button[type="submit"]')) || candidates.find(e => ['log in','login','dang nhap'].includes(norm(e.innerText||e.textContent)));
  if(!btn) return false; btn.click(); return true;
})()
""", ct: ct);
        if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Không tìm thấy nút Đăng nhập TikTok.");
        _log.Info("[TIKTOK_LOGIN_SUBMIT] clicked=true");
    }

    public Task<bool> IsCaptchaVisibleAsync(CancellationToken ct = default)
        => DetectCaptchaAsync(ct);

    async Task<bool> DetectCaptchaAsync(CancellationToken ct)
    {
        var r = await EvalAsync("""
(() => {
  const visible = e => {
    if (!e) return false;
    const r=e.getBoundingClientRect();
    const s=getComputedStyle(e);
    return r.width>2 && r.height>2 && s.display!=='none' && s.visibility!=='hidden' && Number(s.opacity||1)>0.02;
  };
  const norm = x => String(x||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/đ/g,'d');
  const text = norm(document.body?.innerText||'');
  const iframeCaptcha = [...document.querySelectorAll('iframe')].some(x => visible(x) && /captcha|verify|challenge/i.test(x.src||x.title||''));
  const domCaptcha = [...document.querySelectorAll('[id*="captcha" i],[class*="captcha" i],[data-e2e*="captcha" i]')].some(visible);
  const textCaptcha = text.includes('captcha') || text.includes('verify to continue') || text.includes('xac minh de tiep tuc') || text.includes('drag the puzzle') || text.includes('keo thanh truot');
  return iframeCaptcha || domCaptcha || textCaptcha;
})()
""", ct: ct);
        return r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
    }

    async Task<bool> DetectTotpChallengeAsync(CancellationToken ct)
    {
        var r = await EvalAsync("""
(() => {
  const visible = e => {
    if (!e) return false;
    const r=e.getBoundingClientRect();
    const s=getComputedStyle(e);
    return r.width>1 && r.height>1 && s.display!=='none' && s.visibility!=='hidden' && Number(s.opacity||1)>0.02;
  };
  const norm = x => String(x||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/đ/g,'d').replace(/\s+/g,' ').trim();
  const text = norm(document.body?.innerText||'');
  const path = String(location.pathname||'').toLowerCase();
  const onTotpUrl = path.includes('/login/2sv/totp') || path.includes('/2sv/totp');
  const inputs = [...document.querySelectorAll('input')].filter(visible);
  const codeInput = inputs.some(e => {
    const attrs = norm((e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.getAttribute('aria-label')||''));
    const maxLen = Number(e.maxLength||0);
    return /one-time-code/i.test(e.autocomplete||'')
      || /code|otp|verify|verification/.test(attrs)
      || attrs.includes('6 chu so')
      || attrs.includes('6 digit')
      || attrs.includes('nhap ma')
      || (e.inputMode==='numeric' && (maxLen===0 || maxLen>=4))
      || maxLen===6;
  });
  const marker = onTotpUrl
    || text.includes('2-step verification')
    || text.includes('two-step verification')
    || text.includes('verification code')
    || text.includes('authenticator')
    || text.includes('xac minh 2 buoc')
    || text.includes('ung dung xac thuc')
    || text.includes('ma xac minh')
    || text.includes('ma 2fa')
    || text.includes('nhap ma gom 6 chu so');
  return codeInput && marker;
})()
""", ct: ct);
        var detected = r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        if (detected) _log.Info("[TIKTOK_LOGIN_2FA_DETECTED] challenge=true");
        return detected;
    }

    async Task FillAndSubmitTotpAsync(string totpSecret, CancellationToken ct)
    {
        var auth = new ToolTikTokV12.Services.TikTokAuthService();
        var remaining = auth.GetTotpSecondsRemaining();
        if (remaining <= 5)
            await Task.Delay((remaining + 1) * 1000, ct);

        var code = auth.GenerateTotp(totpSecret);

        // Fill first. TikTok/React may enable the Continue button only after it
        // has processed the input event, so do not click in the same JS turn.
        var fillJs = $$"""
(() => {
  const visible = e => { if(!e) return false; const r=e.getBoundingClientRect(); const s=getComputedStyle(e); return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'; };
  const norm=x=>String(x||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/đ/g,'d').replace(/\s+/g,' ').trim();
  const inputs=[...document.querySelectorAll('input')].filter(visible);
  const score=e=>{
    const attrs=norm((e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.getAttribute('aria-label')||''));
    let n=0;
    if(/one-time-code/i.test(e.autocomplete||'')) n+=100;
    if(/code|otp|verify|verification/.test(attrs)) n+=60;
    if(attrs.includes('6 chu so')||attrs.includes('6 digit')) n+=80;
    if(attrs.includes('nhap ma')) n+=40;
    if(Number(e.maxLength||0)===6) n+=50;
    if(e.inputMode==='numeric') n+=30;
    return n;
  };
  const input=inputs.map(e=>[e,score(e)]).sort((a,b)=>b[1]-a[1])[0]?.[0];
  if(!input) return 'NO_INPUT';
  input.focus();
  const desc=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value');
  if(desc&&desc.set) desc.set.call(input,{{JsString(code)}}); else input.value={{JsString(code)}};
  input.dispatchEvent(new Event('input',{bubbles:true}));
  input.dispatchEvent(new Event('change',{bubbles:true}));
  input.dispatchEvent(new Event('blur',{bubbles:true}));
  return String(input.value||'')==={{JsString(code)}} ? 'FILLED' : 'VALUE_NOT_SET';
})()
""";
        var fillResult = await EvalAsync(fillJs, ct: ct);
        var fillState = fillResult.TryGetProperty("value", out var fv) ? fv.GetString() ?? "" : "";
        if (fillState != "FILLED")
            throw new InvalidOperationException("Không tự điền được mã 2FA TikTok: " + fillState);

        _log.Info("[TIKTOK_LOGIN_2FA] totpFilled=true");

        // Give the page time to enable/re-render the Continue button, then retry
        // a few times because TikTok UI updates asynchronously.
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await Task.Delay(attempt == 1 ? 350 : 250, ct);
            var clickResult = await EvalAsync("""
(() => {
  const visible = e => { if(!e) return false; const r=e.getBoundingClientRect(); const s=getComputedStyle(e); return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'; };
  const enabled = e => !e.disabled && String(e.getAttribute('aria-disabled')||'').toLowerCase()!=='true';
  const norm=x=>String(x||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/đ/g,'d').replace(/\s+/g,' ').trim();
  const buttons=[...document.querySelectorAll('button[type="submit"],button,[role="button"]')].filter(e=>visible(e)&&enabled(e));
  const labels=['verify','confirm','continue','next','xac minh','tiep','tiep tuc','gui'];
  const btn=buttons.find(e=>labels.includes(norm(e.innerText||e.textContent)))
    || buttons.find(e=>e.matches('button[type="submit"]'));
  if(!btn) return 'BUTTON_NOT_READY';
  btn.click();
  return 'CLICKED';
})()
""", ct: ct);
            var clickState = clickResult.TryGetProperty("value", out var cv) ? cv.GetString() ?? "" : "";
            if (clickState == "CLICKED")
            {
                _log.Info($"[TIKTOK_LOGIN_2FA] totpSubmitted=true attempt={attempt}");
                return;
            }
        }

        throw new InvalidOperationException("Đã điền mã 2FA nhưng nút Tiếp/Xác minh chưa sẵn sàng để bấm.");
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

    static bool LooksLikeRendererCrashText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("aw, snap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ôi, hỏng", StringComparison.OrdinalIgnoreCase)
            || text.Contains("target crashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("inspected target crashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("renderer process", StringComparison.OrdinalIgnoreCase)
            || text.Contains("render process gone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tab crashed", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeCrashUrl(string url)
        => !string.IsNullOrWhiteSpace(url)
           && (url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("chrome://crash", StringComparison.OrdinalIgnoreCase));

    public async Task<PageHealthSnapshot> ProbePageHealthAsync(CancellationToken ct = default)
    {
        var url = Page?.Url ?? "";

        // /json là đường kiểm tra ngoài renderer. URL vẫn có thể giữ nguyên /live trên sad-tab,
        // nên metadata chỉ là lớp đầu; phía dưới còn kiểm tra Runtime/DOM/LayoutMetrics.
        try
        {
            if (_port > 0)
            {
                var pages = await GetPagesAsync(_port);
                var listed = SelectTarget(pages, Page?.Id);
                if (listed is not null)
                {
                    url = listed.Url;
                    if (LooksLikeCrashUrl(listed.Url) || LooksLikeRendererCrashText(listed.Title))
                        return new PageHealthSnapshot(false, true, "CRASH_PAGE_METADATA", listed.Url);
                }
            }
        }
        catch
        {
            // Browser HTTP endpoint có thể chập chờn lúc document đổi; thử session hiện tại tiếp.
        }

        if (!Connected)
            return new PageHealthSnapshot(false, true, "CDP_DISCONNECTED", url);

        try
        {
            var r = await EvalAsync("""
(() => {
  const href = String(location.href || '');
  const title = String(document.title || '');
  const ready = String(document.readyState || '');
  const bodyText = String(document.body?.innerText || document.body?.textContent || '').slice(0, 1800);
  const bodyChildren = Number(document.body?.childElementCount || 0);
  let host = '';
  try { host = String(location.hostname || '').toLowerCase(); } catch {}
  const onTikTok = host === 'tiktok.com' || host.endsWith('.tiktok.com');
  const hasTikTokShell = !onTikTok || !!document.querySelector(
    '#app,#root,[data-e2e],[data-testid],script[src*="tiktok"],link[href*="tiktok"],meta[property="og:site_name"]');
  return { href, title, ready, bodyText, bodyChildren, onTikTok, hasTikTokShell };
})()
""", ct: ct);

            var v = r.GetProperty("value");
            var href = v.TryGetProperty("href", out var hrefEl) ? hrefEl.GetString() ?? "" : "";
            var title = v.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
            var ready = v.TryGetProperty("ready", out var readyEl) ? readyEl.GetString() ?? "" : "";
            var bodyText = v.TryGetProperty("bodyText", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var bodyChildren = v.TryGetProperty("bodyChildren", out var childEl) && childEl.TryGetInt32(out var childCount) ? childCount : 0;
            var onTikTok = v.TryGetProperty("onTikTok", out var tikEl) && tikEl.ValueKind == JsonValueKind.True;
            var hasTikTokShell = v.TryGetProperty("hasTikTokShell", out var shellEl) && shellEl.ValueKind == JsonValueKind.True;

            if (!string.IsNullOrWhiteSpace(href)) url = href;

            var visibleText = title + "\n" + bodyText;

            // HTTP 403 không phải renderer crash, nhưng phải đi vào nhánh tự cứu ngay.
            // Đánh dấu CrashLike=true để engine khóa workflow và gọi RecoverCurrentPageAsync;
            // RecoverCurrentPageAsync sẽ nhận HTTP_403 và dùng HOME -> READY -> LIVE, KHÔNG F5.
            if (LooksLikeHttp403Text(visibleText))
                return new PageHealthSnapshot(false, true, "HTTP_403", url);

            if (LooksLikeCrashUrl(href) || LooksLikeRendererCrashText(visibleText))
                return new PageHealthSnapshot(false, true, "CRASH_PAGE_DOM_TEXT", url);

            // Sad-tab/OOM đôi khi vẫn giữ location.href=/live và Runtime.evaluate trả về được,
            // nhưng renderer không còn TikTok DOM thật. ready=complete + DOM trống là dấu hiệu lỗi.
            if (onTikTok && ready.Equals("complete", StringComparison.OrdinalIgnoreCase)
                && bodyChildren == 0)
                return new PageHealthSnapshot(false, true, "TIKTOK_RENDERER_EMPTY", url);

            if (onTikTok && ready.Equals("complete", StringComparison.OrdinalIgnoreCase)
                && !hasTikTokShell && string.IsNullOrWhiteSpace(bodyText))
                return new PageHealthSnapshot(false, true, "TIKTOK_SHELL_MISSING", url);

            if (ready != "interactive" && ready != "complete")
                return new PageHealthSnapshot(false, false, "DOCUMENT_NOT_READY", url);

            // Lệnh này buộc Page domain hỏi renderer về layout. Trên target/sad-tab đã crash,
            // đây thường là điểm lộ lỗi dù URL và title vẫn còn như cũ.
            try
            {
                await Cdp.CallAsync("Page.getLayoutMetrics", ct: ct);
            }
            catch (Exception layoutEx)
            {
                if (IsCdpSessionLost(layoutEx) || IsRendererCrashLike(layoutEx) || LooksLikeRendererCrashText(layoutEx.ToString()))
                    return new PageHealthSnapshot(false, true, "LAYOUT_RENDERER_CRASHED", url);
                return new PageHealthSnapshot(false, false, "LAYOUT_PROBE_ERROR", url);
            }

            return new PageHealthSnapshot(true, false, "OK", url);
        }
        catch (Exception ex)
        {
            var message = ex.ToString();
            if (IsCdpSessionLost(ex) || IsRendererCrashLike(ex) || LooksLikeRendererCrashText(message))
                return new PageHealthSnapshot(false, true, "RENDERER_OR_TARGET_CRASHED", url);

            if (IsTransientDocumentContextError(ex))
                return new PageHealthSnapshot(false, false, "DOCUMENT_NAVIGATING", url);

            return new PageHealthSnapshot(false, false, "PROBE_ERROR", url);
        }
    }

    static bool LooksLikeHttp403Text(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("HTTP ERROR 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Quyền truy cập www.tiktok.com bị từ chối", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bạn không có quyền xem trang này", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access to www.tiktok.com was denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("You don't have authorization to view this page", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTikTokLiveRecoveryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!(uri.Host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase)
              || uri.Host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/live", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/live", StringComparison.OrdinalIgnoreCase));
    }

    async Task<bool> WaitForTikTokHomePageReadyAsync(
        int timeoutMs,
        CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var stable = 0;

        while (Environment.TickCount64 - started < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var r = await EvalAsync("""
(() => {
  const ready = String(document.readyState || '');
  const href = String(location.href || '');
  const title = String(document.title || '');
  const bodyText = String(document.body?.innerText || document.body?.textContent || '').slice(0, 1600);
  const bodyChildren = Number(document.body?.childElementCount || 0);
  return { ready, href, title, bodyText, bodyChildren };
})()
""", ct: ct);

                var v = r.GetProperty("value");
                var ready = v.TryGetProperty("ready", out var readyEl) ? readyEl.GetString() ?? "" : "";
                var href = v.TryGetProperty("href", out var hrefEl) ? hrefEl.GetString() ?? "" : "";
                var title = v.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var bodyText = v.TryGetProperty("bodyText", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                var bodyChildren = v.TryGetProperty("bodyChildren", out var childEl)
                                   && childEl.TryGetInt32(out var childCount)
                    ? childCount
                    : 0;

                if (LooksLikeHttp403Text(title + "\n" + bodyText))
                {
                    stable = 0;
                }
                else if (IsSafeTikTokRecoveryUrl(href)
                         && ready is "interactive" or "complete"
                         && bodyChildren > 0)
                {
                    stable++;
                    if (stable >= 3)
                        return true;
                }
                else
                {
                    stable = 0;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                stable = 0;
            }

            await Task.Delay(300, ct);
        }

        return false;
    }

    async Task WarmUpTikTokHomeAsync(CancellationToken ct)
    {
        const int attempts = 2;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _log.Info(
                $"[TIKTOK_HOME_WARMUP] attempt={attempt}/{attempts} action=NAVIGATE_HOME");

            await NavigateAndWaitAsync(
                TikTokUrl,
                700,
                15000,
                ct);

            var ready = await WaitForTikTokHomePageReadyAsync(
                25000,
                ct);

            if (!ready)
            {
                _log.Warn(
                    $"[TIKTOK_HOME_WARMUP_NOT_READY] attempt={attempt}/{attempts}");

                if (attempt < attempts)
                {
                    await Task.Delay(1500, ct);
                    continue;
                }

                throw new TimeoutException(
                    "TikTok trang chủ chưa PAGE_READY sau khi chờ.");
            }

            var settleMs = Random.Shared.Next(3000, 5001);

            _log.Info(
                $"[TIKTOK_HOME_WARMUP_READY] attempt={attempt}/{attempts} settleMs={settleMs}");

            await Task.Delay(
                settleMs,
                ct);

            return;
        }
    }

    async Task<PageHealthSnapshot> WaitForHealthyPageAfterNavigateAsync(
        int timeoutMs,
        CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var last = new PageHealthSnapshot(
            false,
            false,
            "NOT_PROBED",
            Page?.Url ?? "");

        while (Environment.TickCount64 - started < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            last = await ProbePageHealthAsync(ct);

            if (last.Healthy)
                return last;

            // 403 là lỗi xác định được ngay, không chờ đủ timeout.
            if (last.Reason.Equals(
                    "HTTP_403",
                    StringComparison.OrdinalIgnoreCase))
            {
                return last;
            }

            await Task.Delay(500, ct);
        }

        return last;
    }

    async Task RecoverHttp403ViaHomeAsync(
        string requestedLiveUrl,
        CancellationToken ct)
    {
        const int attempts = 2;

        var target =
            IsTikTokLiveRecoveryUrl(Page?.Url ?? "")
                ? Page!.Url
                : requestedLiveUrl;

        if (!IsTikTokLiveRecoveryUrl(target))
            target = "https://www.tiktok.com/live";

        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _log.Warn(
                $"[PAGE_RECOVERY_403] attempt={attempt}/{attempts} action=NO_F5 target={TrimForLog(target)}");

            try
            {
                if (!Connected)
                    await ReconnectAsync(ct);

                // Luồng 403: tuyệt đối không F5 trang lỗi.
                // Về trang chủ -> PAGE_READY -> chờ 3-5s -> vào lại đúng LIVE.
                await WarmUpTikTokHomeAsync(ct);

                _log.Warn(
                    $"[PAGE_RECOVERY_403_RENAVIGATE] attempt={attempt}/{attempts} target={TrimForLog(target)}");

                await NavigateAndWaitAsync(
                    target,
                    900,
                    18000,
                    ct);

                var health =
                    await WaitForHealthyPageAfterNavigateAsync(
                        25000,
                        ct);

                if (health.Healthy)
                {
                    _log.Warn(
                        $"[PAGE_RECOVERY_403_OK] attempt={attempt}/{attempts} method=HOME_READY_THEN_RENAVIGATE url={TrimForLog(health.Url)}");

                    return;
                }

                // Nếu /live redirect sang /@user/live rồi chính URL đó bị 403,
                // giữ lại URL cụ thể để lần thử sau quay đúng LIVE đó.
                if (health.Reason.Equals(
                        "HTTP_403",
                        StringComparison.OrdinalIgnoreCase)
                    && IsTikTokLiveRecoveryUrl(Page?.Url ?? ""))
                {
                    target = Page!.Url;
                }

                last = new InvalidOperationException(
                    $"Trang chưa khỏe sau phục hồi 403: {health.Reason}");

                _log.Warn(
                    $"[PAGE_RECOVERY_403_NOT_HEALTHY] attempt={attempt}/{attempts} reason={health.Reason} target={TrimForLog(target)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;

                _log.Warn(
                    $"[PAGE_RECOVERY_403_FAILED] attempt={attempt}/{attempts} reason={ex.Message}");
            }

            if (attempt < attempts)
                await Task.Delay(2000, ct);
        }

        throw new InvalidOperationException(
            "Phục hồi HTTP 403 thất bại sau 2 lần về TikTok trang chủ rồi vào lại LIVE.",
            last);
    }

    static bool IsSafeTikTokRecoveryUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && (uri.Host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase));

    public async Task NavigateAndWaitAsync(string url, int minWaitMs = 1200, int timeoutMs = 15000, CancellationToken ct = default)
    {
        if (!IsSafeTikTokRecoveryUrl(url))
            throw new InvalidOperationException("URL phục hồi không phải TikTok hợp lệ.");

        LogCdpStart("NavigateAndWait", TrimForLog(url));
        await Cdp.CallAsync("Page.navigate", new { url }, ct);
        await Task.Delay(Math.Max(0, minWaitMs), ct);

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
                    LogCdpDone("NavigateAndWait", $"readyState={state}");
                    return;
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                stable = 0;
            }
            await Task.Delay(250, ct);
        }

        throw new TimeoutException("Chrome chưa ổn định sau điều hướng phục hồi.");
    }

    public async Task RecoverCurrentPageAsync(string fallbackUrl, CancellationToken ct = default)
    {
        // HTTP 403: KHÔNG F5. Chrome error page thường giữ nguyên URL LIVE;
        // về tiktok.com cho session ổn định rồi mới navigate lại đúng URL đó.
        try
        {
            var initialHealth = await ProbePageHealthAsync(ct);

            if (initialHealth.Reason.Equals(
                    "HTTP_403",
                    StringComparison.OrdinalIgnoreCase))
            {
                var liveTarget =
                    IsTikTokLiveRecoveryUrl(Page?.Url ?? "")
                        ? Page!.Url
                        : fallbackUrl;

                await RecoverHttp403ViaHomeAsync(
                    liveTarget,
                    ct);

                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[PAGE_RECOVERY_403_PROBE_WARN] reason={ex.Message}");
        }

        Exception? last = null;
        var recoveryUrl = IsSafeTikTokRecoveryUrl(fallbackUrl)
            ? fallbackUrl
            : (IsSafeTikTokRecoveryUrl(Page?.Url ?? "") ? Page!.Url : TikTokUrl);

        const int maxReloadAttempts = 2;
        const int healthTimeoutMs = 25000;
        const int healthPollMs = 500;

        for (int attempt = 1; attempt <= maxReloadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log.Warn($"[PAGE_RECOVERY_RELOAD] attempt={attempt}/{maxReloadAttempts} url={TrimForLog(recoveryUrl)}");

            try
            {
                if (!Connected)
                    await ReconnectAsync(ct);

                await ReloadAndWaitAsync(1200, 18000, ct);

                var started = Environment.TickCount64;
                PageHealthSnapshot lastHealth = new(false, false, "NOT_PROBED", Page?.Url ?? recoveryUrl);
                while (Environment.TickCount64 - started < healthTimeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    lastHealth = await ProbePageHealthAsync(ct);
                    if (lastHealth.Healthy)
                    {
                        _log.Warn($"[PAGE_HEALTH_OK] attempt={attempt}/{maxReloadAttempts} elapsedMs={Environment.TickCount64 - started} url={lastHealth.Url}");
                        _log.Warn($"[PAGE_RECOVERY_OK] attempt={attempt}/{maxReloadAttempts} method=F5");
                        return;
                    }

                    _log.Warn($"[PAGE_HEALTH_WAIT] attempt={attempt}/{maxReloadAttempts} reason={lastHealth.Reason} crashLike={lastHealth.CrashLike}");

                    // Nếu reload vừa rơi thẳng về sad-tab/crash lần nữa thì không chờ đủ 25s.
                    if (lastHealth.CrashLike && Environment.TickCount64 - started >= 2000)
                        break;

                    await Task.Delay(healthPollMs, ct);
                }

                last = new InvalidOperationException($"Trang chưa khỏe sau F5 lần {attempt}: {lastHealth.Reason}");
                _log.Warn($"[PAGE_RECOVERY_RELOAD_NOT_HEALTHY] attempt={attempt}/{maxReloadAttempts} reason={lastHealth.Reason}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                _log.Warn($"[PAGE_RECOVERY_RELOAD_FAILED] attempt={attempt}/{maxReloadAttempts} reason={ex.Message}");

                // F5 có thể làm target/session cũ đóng. Reconnect để lần F5 sau thao tác trên target mới.
                try
                {
                    await ReconnectAsync(ct);
                    _log.Warn($"[PAGE_RECOVERY_RECONNECTED_FOR_RETRY] attempt={attempt}/{maxReloadAttempts}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception reconnectEx)
                {
                    last = reconnectEx;
                    _log.Warn($"[PAGE_RECOVERY_RECONNECT_FAILED] attempt={attempt}/{maxReloadAttempts} reason={reconnectEx.Message}");
                }
            }

            if (attempt < maxReloadAttempts)
                await Task.Delay(3000, ct);
        }

        throw new InvalidOperationException(
            "F5 tự cứu 2 lần nhưng trang TikTok vẫn chưa trở lại trạng thái khỏe.", last);
    }

    public async Task RestartManagedChromeForRecoveryAsync(string fallbackUrl, CancellationToken ct = default)
    {
        var profileDir = _managedProfileDir;
        var port = _managedWindowPort > 0 ? _managedWindowPort : _port;
        if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir))
            throw new InvalidOperationException("Không có Chrome profile managed để restart phục hồi.");
        if (port <= 0)
            throw new InvalidOperationException("Không có cổng CDP managed để restart phục hồi.");

        var recoveryUrl = IsSafeTikTokRecoveryUrl(fallbackUrl) ? fallbackUrl : TikTokUrl;
        _log.Warn($"[PAGE_RECOVERY_CHROME_RESTART_START] port={port} profile={profileDir} url={TrimForLog(recoveryUrl)}");

        try
        {
            await CloseManagedBrowserAsync(profileDir, port, manualRequest: true);
        }
        catch (Exception ex)
        {
            // LaunchAsync còn có bước đóng/kiểm tra owner lần nữa; không kết luận thất bại chỉ
            // vì Browser.close của renderer crash không phản hồi.
            _log.Warn($"[PAGE_RECOVERY_CHROME_CLOSE_WARN] {TrimForLog(ex.Message)}");
        }

        ct.ThrowIfCancellationRequested();
        await LaunchAsync(port, profileDir);
        ct.ThrowIfCancellationRequested();
        await ConnectAsync(port);

        if (IsSafeTikTokRecoveryUrl(recoveryUrl))
            await NavigateAndWaitAsync(recoveryUrl, 1200, 20000, ct);

        _log.Warn($"[PAGE_RECOVERY_CHROME_RESTART_OK] port={port} url={TrimForLog(recoveryUrl)}");
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

    /// <summary>
    /// Dò lại HWND Chrome theo đúng profile + CDP port khi người dùng bấm View.
    /// Hàm này chỉ cập nhật cache HWND/PID, không thay đổi trạng thái cửa sổ.
    /// Không gọi ở đường status polling vì việc dò process có thể tốn thời gian.
    /// </summary>
    public long RefreshManagedWindowHandle(string profileDir, int port)
    {
        if (string.IsNullOrWhiteSpace(profileDir)) return 0L;

        var normalized = Path.GetFullPath(profileDir);
        _managedProfileDir = normalized;
        _managedWindowPort = port;

        // Khi Chrome được Start tự mở hoặc Chrome tự tạo lại browser process,
        // PID cache cũ có thể không còn chứa PID sở hữu top-level window.
        // Chỉ refresh danh sách PID khi người dùng thực sự bấm View.
        var refreshedPids = FindChromeProcessIds(normalized, port);
        _managedPids.Clear();
        foreach (var pid in refreshedPids)
            _managedPids.Add(pid);

        _managedWindowHandle = DiscoverManagedWindowHandle();
        var value = IsLiveWindowHandle(_managedWindowHandle)
            ? _managedWindowHandle.ToInt64()
            : 0L;

        _log.Info($"[CHROME_VIEW_HANDLE_REFRESH] port={port} pids={_managedPids.Count} hwnd={value}");
        return value;
    }

    /// <summary>
    /// Resolve lại Chrome đúng profile theo ownership của CDP port và
    /// --user-data-dir. Cache PID/HWND cũ luôn bị thay thế, kể cả khi lần dò mới
    /// không có kết quả, để không giữ PID của Chrome trước khi restart/recover.
    /// </summary>
    public async Task<ManagedChromeWindowResolution> ResolveManagedWindowAsync(
        string profileDir,
        int port,
        int windowAttempts = 8,
        int retryDelayMs = 250)
    {
        if (string.IsNullOrWhiteSpace(profileDir))
            return new ManagedChromeWindowResolution(0, 0, 0, "profile_path_missing");

        var normalized = NormalizeProfilePath(profileDir);
        var cachedPid = GetCachedManagedProcessId();
        _managedProfileDir = normalized;
        _managedWindowPort = port;

        var resolvedPids = new List<int>();
        var reason = "matching_process_not_found";
        var listenerPid = TryGetListeningProcessId(port);
        if (listenerPid is > 0)
        {
            var listenerOwner = await InspectExistingChromeProcessAsync(listenerPid.Value, normalized);
            if (listenerOwner is not null
                && CommandLineUsesRemoteDebuggingPort(listenerOwner.CommandLine, port))
            {
                resolvedPids.Add(listenerOwner.ProcessId);
                reason = "cdp_listener+profile_path";
            }
            else
            {
                reason = "cdp_listener_profile_mismatch";
            }
        }

        if (resolvedPids.Count == 0)
        {
            var owners = await FindChromeProcessesUsingProfileAsync(normalized, TimeSpan.FromSeconds(3));
            resolvedPids.AddRange(owners
                .Where(owner => CommandLineUsesProfile(owner.CommandLine, normalized)
                    && CommandLineUsesRemoteDebuggingPort(owner.CommandLine, port))
                .Select(owner => owner.ProcessId)
                .Distinct());
            if (resolvedPids.Count > 0) reason = "command_line_profile+cdp_port";
        }

        // Một số máy khách chặn CIM/đọc CommandLine. Khi đó vẫn chỉ chấp nhận
        // đúng PID đang listen CDP port mà Worker hiện đang CONNECTED tới; không
        // bao giờ rơi xuống chọn đại một chrome.exe.
        if (resolvedPids.Count == 0
            && listenerPid is > 0
            && Connected
            && IsChromeProcess(listenerPid.Value))
        {
            resolvedPids.Add(listenerPid.Value);
            reason = "connected_cdp_listener;profile_query_unavailable";
        }

        _managedPids.Clear();
        foreach (var pid in resolvedPids) _managedPids.Add(pid);
        _managedWindowHandle = IntPtr.Zero;

        if (_managedPids.Count == 0)
            return new ManagedChromeWindowResolution(cachedPid, 0, 0, reason);

        windowAttempts = Math.Clamp(windowAttempts, 1, 10);
        retryDelayMs = Math.Clamp(retryDelayMs, 200, 300);
        for (var attempt = 1; attempt <= windowAttempts; attempt++)
        {
            _managedWindowHandle = DiscoverManagedWindowHandle(out var resolvedPid);
            if (IsLiveWindowHandle(_managedWindowHandle))
            {
                var result = new ManagedChromeWindowResolution(
                    cachedPid,
                    resolvedPid,
                    _managedWindowHandle.ToInt64(),
                    $"{reason};attempt={attempt}/{windowAttempts}");
                _log.Info($"[CHROME_VIEW_RESOLVE] port={port} cachedPid={result.CachedPid} resolvedPid={result.ResolvedPid} hwnd={result.WindowHandle} reason={result.Reason}");
                return result;
            }

            if (attempt < windowAttempts) await Task.Delay(retryDelayMs);
        }

        var failed = new ManagedChromeWindowResolution(
            cachedPid,
            resolvedPids.FirstOrDefault(),
            0,
            $"window_not_found;source={reason};attempts={windowAttempts}");
        _log.Warn($"[CHROME_VIEW_RESOLVE] port={port} cachedPid={failed.CachedPid} resolvedPid={failed.ResolvedPid} hwnd=0 reason={failed.Reason}");
        return failed;
    }

    int GetCachedManagedProcessId()
    {
        if (IsLiveWindowHandle(_managedWindowHandle))
        {
            GetWindowThreadProcessId(_managedWindowHandle, out var windowPid);
            if (windowPid > 0) return (int)windowPid;
        }

        return _managedPids.FirstOrDefault();
    }

    static bool IsChromeProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

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
        => DiscoverManagedWindowHandle(out _);

    IntPtr DiscoverManagedWindowHandle(out int resolvedPid)
    {
        resolvedPid = 0;
        if (_managedPids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        var bestPid = 0;
        var bestScore = int.MinValue;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !_managedPids.Contains((int)pid)) return true;

            var classNameBuffer = new StringBuilder(128);
            GetClassName(hwnd, classNameBuffer, classNameBuffer.Capacity);
            var isChromeBrowserWindow = classNameBuffer.ToString().Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase);
            var isVisible = IsWindowVisible(hwnd);
            var isMinimized = IsIconic(hwnd);
            var hasTitle = GetWindowTextLength(hwnd) > 0;

            // Hidden Chrome browser windows must remain eligible. Ignore only
            // unrelated hidden helper/message windows owned by the same PID.
            if (!isChromeBrowserWindow && !isVisible && !isMinimized && !hasTitle) return true;

            var score = (isChromeBrowserWindow ? 100 : 0)
                + (isVisible ? 20 : 0)
                + (isMinimized ? 10 : 0)
                + (hasTitle ? 5 : 0);
            if (score <= bestScore) return true;
            best = hwnd;
            bestPid = (int)pid;
            bestScore = score;
            return true;
        }, IntPtr.Zero);
        resolvedPid = bestPid;
        return best;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
}
