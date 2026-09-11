using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoCloseCleanupPendingException : InvalidOperationException
    {
        public AutoCloseCleanupPendingException(string message)
            : base(message) { }

        public AutoCloseCleanupPendingException(string message, Exception inner)
            : base(message, inner) { }
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
        // Fast verification path that does not depend on WMI/CIM. On some VMs the
        // ProfilePath probe can intermittently timeout even after Chrome is already
        // gone. That false UNKNOWN used to keep AutoClose/AutoReplacement stuck in
        // CLEANUP_PENDING forever.
        //
        // We only accept this fast path when ALL three runtime signals say the
        // profile is gone: Worker exited, cached Chrome window is invalid, and the
        // profile's dedicated CDP port is no longer listening. If any signal still
        // looks alive we fall back to the strict ProfilePath process probe below.
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

        if (!workerAlive && !cachedWindowAlive && !cdpListening)
        {
            _log.Info(
                $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={ctx.Profile.Name} processCount=0 method=worker_exit+window_closed+cdp_closed port={ctx.Profile.CdpPort}");
            return;
        }

        await EnsureAutoCloseChromeStoppedByPathAsync(
            ctx.Profile.Name,
            ctx.Profile.ProfilePath);
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
                + "Chuyển CLEANUP_PENDING để thử lại sau; chưa tạo suất bù.");
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
