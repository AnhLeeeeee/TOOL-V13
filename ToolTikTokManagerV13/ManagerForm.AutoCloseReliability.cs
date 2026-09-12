using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
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
        ProfileContext ctx)
    {
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
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    ctx.Profile.Name,
                    ctx.Profile.ProfilePath);

                _log.Info(
                    $"[AUTO_CLOSE_CHROME_STRICT_PASS] profile={ctx.Profile.Name} pass={pass}/2 state=profile_path_closed");

                if (pass == 1)
                {
                    var delayMs = Random.Shared.Next(1000, 2001);
                    _log.Info(
                        $"[AUTO_CLOSE_CHROME_STRICT_WAIT] profile={ctx.Profile.Name} delayMs={delayMs} nextPass=2/2");
                    await Task.Delay(delayMs);
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
            var fallbackClosed = await TryConfirmAutoCloseChromeStoppedWithoutCimAsync(ctx, ex.Message);
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

    async Task EnsureAutoCloseChromeStoppedByPathAsync(
        string profileName,
        string profilePath)
    {
        profileName = (profileName ?? "").Trim();
        profilePath = (profilePath ?? "").Trim();

        // V13.7.9 HOTFIX:
        // Chỉ chạy MỘT lượt CIM để xác định chính xác process thuộc ProfilePath.
        // Bản cũ chạy 6 lượt + final; trên VM WMI/CIM chậm, mỗi lượt timeout 5s
        // khiến một profile giữ watchdog khoảng 40-45 giây.
        var probe = await Task.Run(
            () => ChromeProfileNameSyncService.ProbeProfileProcesses(profilePath));

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
        string probeError)
    {
        static bool IsPidAlive(int pid)
        {
            if (pid <= 0) return false;
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

        // Nếu PID top-level cũ còn sống, force-kill đúng cây process đó trước khi
        // xác minh. Không quét/kill Chrome profile khác.
        if (knownChromePid > 0 && IsPidAlive(knownChromePid))
        {
            try
            {
                using var chrome = System.Diagnostics.Process.GetProcessById(knownChromePid);
                chrome.Kill(entireProcessTree: true);
                _log.Warn(
                    $"[AUTO_CLOSE_FALLBACK_KILL_KNOWN_PID] profile={ctx.Profile.Name} pid={knownChromePid} probeError={probeError}");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_FALLBACK_KILL_WARN] profile={ctx.Profile.Name} pid={knownChromePid} error={ex.Message}");
            }
        }

        for (var pass = 1; pass <= 2; pass++)
        {
            var workerAlive = false;
            try
            {
                workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            }
            catch
            {
                workerAlive = ctx.Worker is not null;
            }

            var windowAlive = HasAutoCloseCachedLiveChromeWindow(ctx);
            var cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);
            var pidAlive = IsPidAlive(knownChromePid);
            var opening = ctx.Opening;

            _log.Warn(
                $"[AUTO_CLOSE_FALLBACK_CLOSE_CHECK] profile={ctx.Profile.Name} pass={pass}/2 " +
                $"workerAlive={workerAlive} windowAlive={windowAlive} cdpListening={cdpListening} " +
                $"knownPid={knownChromePid} pidAlive={pidAlive} opening={opening} probeError={probeError}");

            if (workerAlive || windowAlive || cdpListening || pidAlive || opening)
                return false;

            if (pass == 1)
                await Task.Delay(Random.Shared.Next(900, 1401));
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
