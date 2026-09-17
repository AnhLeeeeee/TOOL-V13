using System.Runtime.InteropServices;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    [StructLayout(LayoutKind.Sequential)]
    struct AutoCloseTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    const int AutoCloseAfInet = 2;
    const int AutoCloseTcpTableOwnerPidListener = 3;
    const int AutoCloseErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    sealed class AutoCloseCleanupPendingException : InvalidOperationException
    {
        public bool ProbeUnavailable { get; }

        public AutoCloseCleanupPendingException(
            string message,
            bool probeUnavailable = false)
            : base(message)
        {
            ProbeUnavailable = probeUnavailable;
        }

        public AutoCloseCleanupPendingException(
            string message,
            Exception inner,
            bool probeUnavailable = false)
            : base(message, inner)
        {
            ProbeUnavailable = probeUnavailable;
        }
    }

    sealed record AutoCloseProgressWatchState(
        long Rounds,
        int Step,
        DateTime LastProgressUtc);

    readonly Dictionary<string, AutoCloseProgressWatchState>
        _autoCloseProgressWatchByProfile =
            new(StringComparer.OrdinalIgnoreCase);

    string ObserveAutoCloseProgressFault(
        ProfileContext ctx,
        DateTime nowUtc,
        TimeSpan threshold)
    {
        var profileName = ctx.Profile.Name;
        var snapshot = ctx.LastSnapshot;

        if (snapshot is null)
        {
            ResetAutoCloseProgressWatch(
                profileName,
                "snapshot_missing");
            return "";
        }

        var rounds = snapshot.Rounds;
        var step = snapshot.Step;

        if (!_autoCloseProgressWatchByProfile.TryGetValue(
                profileName,
                out var previous))
        {
            _autoCloseProgressWatchByProfile[profileName] =
                new AutoCloseProgressWatchState(
                    rounds,
                    step,
                    nowUtc);

            _log.Info(
                $"[AUTO_CLOSE_PROGRESS_BASELINE] profile={profileName} rounds={rounds} step={step}");

            return "";
        }

        // Tiến triển thật = vòng tăng hoặc bước Automation thay đổi.
        // Không dùng TotalRunSeconds vì nó vẫn tăng khi engine bị kẹt.
        if (rounds != previous.Rounds
            || step != previous.Step)
        {
            _autoCloseProgressWatchByProfile[profileName] =
                new AutoCloseProgressWatchState(
                    rounds,
                    step,
                    nowUtc);

            return "";
        }

        var stalledFor =
            nowUtc - previous.LastProgressUtc;

        if (stalledFor < threshold)
            return "";

        return
            $"no_progress={stalledFor:c}; rounds={rounds}; step={step}; "
            + $"detail={CompactAutoCloseFaultDetail(snapshot.Detail)}";
    }

    void ResetAutoCloseProgressWatch(
        string profileName,
        string source)
    {
        profileName =
            (profileName ?? "").Trim();

        if (profileName.Length == 0)
            return;

        if (_autoCloseProgressWatchByProfile.Remove(profileName))
        {
            _log.Info(
                $"[AUTO_CLOSE_PROGRESS_RESET] profile={profileName} source={source}");
        }
    }

    bool HasAutoCloseCachedLiveChromeWindow(
        ProfileContext ctx)
    {
        try
        {
            var hwndValue =
                ctx.LastSnapshot?.ChromeWindowHandle ?? 0;

            if (hwndValue <= 0)
                return false;

            return ChromeMonitorWindowActions.IsValid(
                new IntPtr(hwndValue));
        }
        catch
        {
            return false;
        }
    }

    bool IsAutoCloseChromeProfileInUse(
        string profilePath)
    {
        try
        {
            return ChromeProfileNameSyncService.IsProfileInUse(
                profilePath);
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_CLOSE_CHROME_IN_USE_CHECK_WARN] path={profilePath} error={ex.Message}");

            // Không khẳng định Chrome đang còn nếu không kiểm tra được.
            return false;
        }
    }

    async Task EnsureAutoCloseChromeStoppedAsync(
        ProfileContext ctx,
        bool respectEmergencyStop = false)
    {
        void ThrowIfEmergencyStopRequested(string phase)
        {
            if (!respectEmergencyStop || !IsAutomationHalted)
                return;

            _log.Warn(
                $"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=chrome_cleanup:{phase}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_AUTOCLOSE: chrome_cleanup:{phase}");
        }

        ThrowIfEmergencyStopRequested("begin");
        // CHẬM MÀ CHẮC: Worker/window/CDP chỉ là tín hiệu nhanh để log, KHÔNG còn
        // được dùng để kết luận Chrome đã đóng. Chrome có thể mất CDP/cửa sổ trước
        // nhưng process theo đúng --user-data-dir/ProfilePath vẫn còn sống.
        //
        // Mọi đường AutoClose / Tự bù / NameGuard cleanup phải xác minh ProfilePath
        // HAI lượt liên tiếp. Lượt 1 sẽ force-kill đúng PID nếu còn; lượt 2 sau
        // 1-2 giây xác nhận không có process hồi sinh/chậm thoát trước khi nhường slot.
        var workerAlive = false;
        try
        {
            workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
        }
        catch
        {
            workerAlive = ctx.Worker is not null;
        }

        var cachedWindowAlive = HasAutoCloseCachedLiveChromeWindow(ctx);
        var cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);

        _log.Info(
            $"[AUTO_CLOSE_CHROME_STRICT_BEGIN] profile={ctx.Profile.Name} workerAlive={workerAlive} windowAlive={cachedWindowAlive} cdpListening={cdpListening} port={ctx.Profile.CdpPort} path={ctx.Profile.ProfilePath}");

        try
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                ThrowIfEmergencyStopRequested($"before_pass_{pass}");
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    ctx.Profile.Name,
                    ctx.Profile.ProfilePath,
                    respectEmergencyStop);

                _log.Info(
                    $"[AUTO_CLOSE_CHROME_STRICT_PASS] profile={ctx.Profile.Name} pass={pass}/2 state=profile_path_closed");

                if (pass == 1)
                {
                    var delayMs = Random.Shared.Next(1000, 2001);
                    _log.Info(
                        $"[AUTO_CLOSE_CHROME_STRICT_WAIT] profile={ctx.Profile.Name} delayMs={delayMs} nextPass=2/2");
                    await Task.Delay(delayMs);
                    ThrowIfEmergencyStopRequested("between_passes");
                }
            }

            _log.Info(
                $"[AUTO_CLOSE_CHROME_STRICT_CONFIRMED] profile={ctx.Profile.Name} passes=2/2 processCount=0");
        }
        catch (AutoCloseCleanupPendingException ex) when (ex.ProbeUnavailable)
        {
            // CIM/PowerShell UNKNOWN không đồng nghĩa Chrome còn sống. Khi chính probe
            // hệ thống bị timeout, dùng các tín hiệu runtime độc lập (Worker, cửa sổ,
            // CDP, PID top-level đã biết) hai lượt liên tiếp. Chỉ fallback nếu TẤT CẢ
            // đều sạch; nếu còn bất kỳ tín hiệu sống nào vẫn fail-closed như cũ.
            ThrowIfEmergencyStopRequested("before_fallback");
            var fallbackClosed = await TryConfirmAutoCloseChromeStoppedWithoutCimAsync(
                ctx,
                ex.Message,
                respectEmergencyStop);
            if (!fallbackClosed)
                throw;

            _log.Warn(
                $"[AUTO_CLOSE_CHROME_CLEANUP_FALLBACK_CONFIRMED] profile={ctx.Profile.Name} " +
                $"reason=process_probe_unavailable action=ALLOW_CLEANUP_CONTINUE");
        }
    }

    static async Task<bool> IsAutoCloseCdpPortListeningAsync(int port)
    {
        if (port <= 0 || port > 65535)
            return false;

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    static int? TryGetAutoCloseCdpListenerPid(int port)
    {
        if (port <= 0 || port > 65535)
            return null;

        var size = 0;
        var first = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            false,
            AutoCloseAfInet,
            AutoCloseTcpTableOwnerPidListener,
            0);

        if (first != AutoCloseErrorInsufficientBuffer || size <= sizeof(int))
            return null;

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(
                    table,
                    ref size,
                    false,
                    AutoCloseAfInet,
                    AutoCloseTcpTableOwnerPidListener,
                    0) != 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<AutoCloseTcpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<AutoCloseTcpRowOwnerPid>(
                    IntPtr.Add(table, sizeof(int) + i * rowSize));

                var localPort = (ushort)(
                    ((row.LocalPort & 0xFF) << 8)
                    | ((row.LocalPort >> 8) & 0xFF));

                if (localPort == port && row.ProcessId > 0)
                    return unchecked((int)row.ProcessId);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static bool IsAutoCloseProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    static bool IsAutoCloseChromePid(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    async Task<bool> TryForceKillAutoCloseChromeTreeAsync(
        string profileName,
        int pid,
        string source,
        bool respectEmergencyStop = false)
    {
        if (pid <= 0)
            return true;

        if (respectEmergencyStop && IsAutomationHalted)
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_AUTOCLOSE: chrome_force_kill:{source}");

        if (!IsAutoCloseProcessAlive(pid))
            return true;

        // Chỉ kill PID được xác minh là chrome.exe. PID từ CDP listener là ownership
        // mạnh vì mỗi profile Manager có một CdpPort riêng, nhưng vẫn kiểm tra process
        // name để không bao giờ kill nhầm một service khác nếu port bị tái sử dụng.
        if (!IsAutoCloseChromePid(pid))
        {
            _log.Warn(
                $"[AUTO_CLOSE_FORCE_PID_REJECTED] profile={profileName} pid={pid} source={source} reason=not_chrome");
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            _log.Warn(
                $"[AUTO_CLOSE_FORCE_TREE_KILL] profile={profileName} pid={pid} source={source} method=Process.KillTree");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_CLOSE_FORCE_TREE_KILL_WARN] profile={profileName} pid={pid} source={source} method=Process.KillTree error={ex.Message}");
        }

        var untilUtc = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < untilUtc)
        {
            if (!IsAutoCloseProcessAlive(pid))
                return true;

            await Task.Delay(120);

            if (respectEmergencyStop && IsAutomationHalted)
                throw new OperationCanceledException(
                    $"EMERGENCY_STOP_AUTOCLOSE: chrome_force_kill_wait:{source}");
        }

        // Fallback Windows taskkill khi Process.Kill(entireProcessTree) không hạ được
        // cây Chrome. Chỉ chạy với PID chrome.exe đã xác minh ở trên.
        try
        {
            using var killer = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(
                    "taskkill.exe",
                    $"/PID {pid} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            killer.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await killer.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try { killer.Kill(entireProcessTree: true); } catch { }
            }

            _log.Warn(
                $"[AUTO_CLOSE_FORCE_TREE_KILL] profile={profileName} pid={pid} source={source} method=taskkill");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_CLOSE_FORCE_TREE_KILL_WARN] profile={profileName} pid={pid} source={source} method=taskkill error={ex.Message}");
        }

        untilUtc = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < untilUtc)
        {
            if (!IsAutoCloseProcessAlive(pid))
                return true;

            await Task.Delay(150);

            if (respectEmergencyStop && IsAutomationHalted)
                throw new OperationCanceledException(
                    $"EMERGENCY_STOP_AUTOCLOSE: chrome_taskkill_wait:{source}");
        }

        return !IsAutoCloseProcessAlive(pid);
    }

    async Task EnsureAutoCloseChromeStoppedByPathAndPortAsync(
        string profileName,
        string profilePath,
        int cdpPort,
        bool respectEmergencyStop = false)
    {
        try
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    profileName,
                    profilePath,
                    respectEmergencyStop);

                if (pass == 1)
                    await Task.Delay(Random.Shared.Next(900, 1401));
            }

            return;
        }
        catch (AutoCloseCleanupPendingException ex) when (ex.ProbeUnavailable)
        {
            _log.Warn(
                $"[AUTO_CLOSE_PATH_PORT_FALLBACK_BEGIN] profile={profileName} port={cdpPort} probeError={ex.Message}");
        }

        if (cdpPort <= 0 || cdpPort > 65535)
        {
            throw new AutoCloseCleanupPendingException(
                $"Không có CDP port hợp lệ để xác minh Chrome profile {profileName} sau khi CIM/probe lỗi. "
                + "Giữ CLEANUP_PENDING; không được mở profile bù khác.",
                probeUnavailable: true);
        }

        for (var pass = 1; pass <= 2; pass++)
        {
            if (respectEmergencyStop && IsAutomationHalted)
                throw new OperationCanceledException(
                    "EMERGENCY_STOP_AUTOCLOSE: chrome_path_port_fallback");

            var listenerPid = TryGetAutoCloseCdpListenerPid(cdpPort);
            if (listenerPid is > 0)
            {
                await TryForceKillAutoCloseChromeTreeAsync(
                    profileName,
                    listenerPid.Value,
                    $"cdp_port_{cdpPort}_pass_{pass}",
                    respectEmergencyStop);
            }

            await Task.Delay(pass == 1 ? 900 : 250);

            var cdpListening = await IsAutoCloseCdpPortListeningAsync(cdpPort);
            var latestPid = TryGetAutoCloseCdpListenerPid(cdpPort);
            var pidAlive = latestPid is > 0 && IsAutoCloseProcessAlive(latestPid.Value);

            _log.Warn(
                $"[AUTO_CLOSE_PATH_PORT_FALLBACK_CHECK] profile={profileName} pass={pass}/2 port={cdpPort} cdpListening={cdpListening} listenerPid={(latestPid?.ToString() ?? "-")} pidAlive={pidAlive}");

            if (cdpListening || pidAlive)
            {
                if (pass == 2)
                {
                    throw new AutoCloseCleanupPendingException(
                        $"Chrome profile {profileName} vẫn còn CDP/PID trên port {cdpPort} sau force-kill. "
                        + "Giữ CLEANUP_PENDING; không được mở profile bù khác.",
                        probeUnavailable: true);
                }

                continue;
            }
        }

        _log.Warn(
            $"[AUTO_CLOSE_PATH_PORT_FALLBACK_CONFIRMED] profile={profileName} port={cdpPort} passes=2/2 action=ALLOW_CLEANUP_CONTINUE");
    }

    async Task EnsureAutoCloseChromeStoppedByPathAsync(
        string profileName,
        string profilePath,
        bool respectEmergencyStop = false)
    {
        profileName = (profileName ?? "").Trim();
        profilePath = (profilePath ?? "").Trim();

        void ThrowIfEmergencyStopRequested(string phase)
        {
            if (!respectEmergencyStop || !IsAutomationHalted)
                return;

            _log.Warn(
                $"[AUTO_CLOSE_ABORT_EMERGENCY] profile={profileName} phase=chrome_path:{phase}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_AUTOCLOSE: chrome_path:{phase}");
        }

        ThrowIfEmergencyStopRequested("before_probe");

        // V13.7.9 HOTFIX:
        // Chỉ chạy MỘT lượt CIM để xác định chính xác process thuộc ProfilePath.
        // Bản cũ chạy 6 lượt + final; trên VM WMI/CIM chậm, mỗi lượt timeout 5s
        // khiến một profile giữ watchdog khoảng 40-45 giây.
        var probe = await Task.Run(
            () => ChromeProfileNameSyncService.ProbeProfileProcesses(profilePath));

        ThrowIfEmergencyStopRequested("after_probe");

        if (!probe.Succeeded)
        {
            var error = string.IsNullOrWhiteSpace(probe.Error)
                ? "probe_failed"
                : probe.Error;

            _log.Warn(
                $"[AUTO_CLOSE_CHROME_CLEANUP_PENDING] profile={profileName} error={error} action=RETRY_LATER");

            throw new AutoCloseCleanupPendingException(
                $"Chưa xác minh được Chrome profile {profileName} đã đóng (probe={error}). "
                + "Chuyển CLEANUP_PENDING để thử lại sau; chưa tạo suất bù.",
                probeUnavailable: true);
        }

        if (probe.ProcessIds.Count == 0)
        {
            _log.Info(
                $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} processCount=0 probe=single");
            return;
        }

        // Probe đã trả đúng PID theo ProfilePath, vì vậy kill trực tiếp các PID đã
        // được xác minh. Không gọi StopChromeUsingProfile() lần nữa vì hàm đó lại
        // chạy thêm một vòng PowerShell/CIM.
        var detectedPids = probe.ProcessIds
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();

        var killSent = new List<int>();

        foreach (var pid in detectedPids)
        {
            ThrowIfEmergencyStopRequested($"before_kill_pid_{pid}");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (process.HasExited)
                    continue;

                process.Kill(entireProcessTree: true);
                killSent.Add(pid);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_FORCE_WARN] profile={profileName} pid={pid} error={ex.Message}");
            }
        }

        _log.Warn(
            $"[AUTO_CLOSE_CHROME_FORCE_KNOWN_PIDS] profile={profileName} detected={string.Join(",", detectedPids)} killSent={string.Join(",", killSent)}");

        static bool IsPidAlive(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        var deadlineUtc = DateTime.UtcNow.AddSeconds(2.5);
        int[] remaining;

        do
        {
            ThrowIfEmergencyStopRequested("wait_known_pids");
            remaining = detectedPids
                .Where(IsPidAlive)
                .ToArray();

            if (remaining.Length == 0)
            {
                _log.Info(
                    $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} processCount=0 method=known_pid_kill");
                return;
            }

            await Task.Delay(150);
        }
        while (DateTime.UtcNow < deadlineUtc);

        remaining = detectedPids
            .Where(IsPidAlive)
            .ToArray();

        if (remaining.Length > 0)
        {
            throw new AutoCloseCleanupPendingException(
                $"Chrome profile {profileName} vẫn còn process [{string.Join(",", remaining)}] sau force-kill. "
                + "Chuyển CLEANUP_PENDING; chưa tạo suất bù.");
        }

        _log.Info(
            $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} processCount=0 method=known_pid_kill_final");
    }

    async Task<bool> TryConfirmAutoCloseChromeStoppedWithoutCimAsync(
        ProfileContext ctx,
        string probeError,
        bool respectEmergencyStop = false)
    {
        void ThrowIfEmergencyStopRequested(string phase)
        {
            if (!respectEmergencyStop || !IsAutomationHalted)
                return;

            _log.Warn(
                $"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=chrome_fallback:{phase}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_AUTOCLOSE: chrome_fallback:{phase}");
        }

        ThrowIfEmergencyStopRequested("begin");

        var knownChromePid = 0;
        var hwndValue = ctx.LastSnapshot?.ChromeWindowHandle ?? 0;
        if (hwndValue > 0)
        {
            try
            {
                GetWindowThreadProcessId(new IntPtr(hwndValue), out var pid);
                if (pid > 0)
                    knownChromePid = (int)pid;
            }
            catch { }
        }

        // CIM/PowerShell có thể timeout khi máy đang tải cao. Lúc đó không chỉ
        // "quan sát" CDP nữa: CdpPort là duy nhất theo profile, nên lấy PID owner
        // trực tiếp từ bảng TCP của Windows (iphlpapi) rồi kill đúng cây chrome.exe.
        // Đây là đường cứu chính cho tình huống Worker đã chết nhưng Chrome orphan
        // vẫn giữ CDP khiến các bản trước QUARANTINE rồi mở dư 6/5, 7/5.
        var firstListenerPid = TryGetAutoCloseCdpListenerPid(ctx.Profile.CdpPort);

        if (knownChromePid > 0)
        {
            ThrowIfEmergencyStopRequested("before_known_pid_kill");
            await TryForceKillAutoCloseChromeTreeAsync(
                ctx.Profile.Name,
                knownChromePid,
                "cached_window_pid",
                respectEmergencyStop);
        }

        if (firstListenerPid is > 0 && firstListenerPid.Value != knownChromePid)
        {
            ThrowIfEmergencyStopRequested("before_listener_pid_kill");
            await TryForceKillAutoCloseChromeTreeAsync(
                ctx.Profile.Name,
                firstListenerPid.Value,
                $"cdp_listener_{ctx.Profile.CdpPort}",
                respectEmergencyStop);
        }

        for (var pass = 1; pass <= 2; pass++)
        {
            ThrowIfEmergencyStopRequested($"before_pass_{pass}");

            var workerAlive = false;
            try
            {
                workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            }
            catch
            {
                workerAlive = ctx.Worker is not null;
            }

            var opening = ctx.Opening;
            var windowAlive = HasAutoCloseCachedLiveChromeWindow(ctx);
            var cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);
            var listenerPid = TryGetAutoCloseCdpListenerPid(ctx.Profile.CdpPort);

            // Chrome có thể vừa spawn/re-parent nên listener PID có thể đổi sau lần
            // kill đầu. Nếu port vẫn sống, resolve lại PID rồi kill thêm đúng cây đó.
            if (cdpListening && listenerPid is > 0)
            {
                await TryForceKillAutoCloseChromeTreeAsync(
                    ctx.Profile.Name,
                    listenerPid.Value,
                    $"cdp_listener_retry_pass_{pass}",
                    respectEmergencyStop);

                await Task.Delay(350);
                cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);
                listenerPid = TryGetAutoCloseCdpListenerPid(ctx.Profile.CdpPort);
            }

            var knownPidAlive = IsAutoCloseProcessAlive(knownChromePid);
            var listenerPidAlive = listenerPid is > 0
                && IsAutoCloseProcessAlive(listenerPid.Value);

            _log.Warn(
                $"[AUTO_CLOSE_FALLBACK_CLOSE_CHECK] profile={ctx.Profile.Name} pass={pass}/2 " +
                $"workerAlive={workerAlive} windowAlive={windowAlive} cdpListening={cdpListening} " +
                $"knownPid={knownChromePid} knownPidAlive={knownPidAlive} " +
                $"listenerPid={(listenerPid?.ToString() ?? "-")} listenerPidAlive={listenerPidAlive} " +
                $"opening={opening} probeError={probeError}");

            if (workerAlive
                || windowAlive
                || cdpListening
                || knownPidAlive
                || listenerPidAlive
                || opening)
            {
                return false;
            }

            if (pass == 1)
            {
                await Task.Delay(Random.Shared.Next(900, 1401));
                ThrowIfEmergencyStopRequested("between_passes");
            }
        }

        return true;
    }

    bool IsAutoCloseRuntimeStillPresent(
        ProfileContext ctx)
    {
        try
        {
            if (ctx.Worker is not null
                && !ctx.Worker.HasExited)
            {
                return true;
            }
        }
        catch
        {
            if (ctx.Worker is not null)
                return true;
        }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        if (tabOpen)
            return true;

        return IsAutoCloseChromeProfileInUse(
            ctx.Profile.ProfilePath);
    }
}
