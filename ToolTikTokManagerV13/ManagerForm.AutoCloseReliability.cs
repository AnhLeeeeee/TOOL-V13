using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
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
        await EnsureAutoCloseChromeStoppedByPathAsync(
            ctx.Profile.Name,
            ctx.Profile.ProfilePath);
    }

    async Task EnsureAutoCloseChromeStoppedByPathAsync(
        string profileName,
        string profilePath)
    {
        profileName = (profileName ?? "").Trim();
        profilePath = (profilePath ?? "").Trim();

        var delaysMs =
            new[] { 250, 450, 700, 1000, 1400, 1800 };

        string lastProbeError = "";

        for (var attempt = 1; attempt <= delaysMs.Length; attempt++)
        {
            var probe = await Task.Run(
                () => ChromeProfileNameSyncService.ProbeProfileProcesses(profilePath));

            if (!probe.Succeeded)
            {
                lastProbeError = probe.Error;
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_VERIFY_UNKNOWN] profile={profileName} attempt={attempt}/{delaysMs.Length} error={probe.Error} action=FAIL_CLOSED");

                await Task.Delay(delaysMs[attempt - 1]);
                continue;
            }

            if (probe.ProcessIds.Count == 0)
            {
                _log.Info(
                    $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} attempt={attempt}/{delaysMs.Length} processCount=0");
                return;
            }

            IReadOnlyList<int> stoppedPids = Array.Empty<int>();

            try
            {
                stoppedPids = await Task.Run(
                    () => ChromeProfileNameSyncService.StopChromeUsingProfile(profilePath));
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_FORCE_WARN] profile={profileName} attempt={attempt}/{delaysMs.Length} error={ex.Message}");
            }

            _log.Warn(
                $"[AUTO_CLOSE_CHROME_FORCE] profile={profileName} attempt={attempt}/{delaysMs.Length} detected={string.Join(",", probe.ProcessIds)} stopped={string.Join(",", stoppedPids)}");

            await Task.Delay(delaysMs[attempt - 1]);
        }

        var finalProbe = await Task.Run(
            () => ChromeProfileNameSyncService.ProbeProfileProcesses(profilePath));

        if (!finalProbe.Succeeded)
        {
            throw new InvalidOperationException(
                $"Không xác minh được Chrome profile {profileName} đã đóng (probe={(finalProbe.Error.Length > 0 ? finalProbe.Error : lastProbeError)}). "
                + "Cleanup Barrier chặn Tự bù để tránh mở thêm Chrome.");
        }

        if (finalProbe.ProcessIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Chrome profile {profileName} vẫn còn process [{string.Join(",", finalProbe.ProcessIds)}]. "
                + "Cleanup Barrier chặn Tự bù để tránh tích tụ Chrome.");
        }

        _log.Info(
            $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} attempt=final processCount=0");
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
