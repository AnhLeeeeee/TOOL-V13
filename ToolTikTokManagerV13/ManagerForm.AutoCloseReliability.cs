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
        var profileName =
            ctx.Profile.Name;

        var profilePath =
            ctx.Profile.ProfilePath;

        var delaysMs =
            new[] { 250, 400, 650, 900, 1200, 1500 };

        for (var attempt = 1;
             attempt <= delaysMs.Length;
             attempt++)
        {
            var stillInUse =
                await Task.Run(
                    () => IsAutoCloseChromeProfileInUse(
                        profilePath));

            if (!stillInUse)
            {
                _log.Info(
                    $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} attempt={attempt}/{delaysMs.Length}");

                return;
            }

            IReadOnlyList<int> stoppedPids =
                Array.Empty<int>();

            try
            {
                stoppedPids =
                    await Task.Run(
                        () => ChromeProfileNameSyncService
                            .StopChromeUsingProfile(profilePath));
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_FORCE_WARN] profile={profileName} attempt={attempt}/{delaysMs.Length} error={ex.Message}");
            }

            _log.Warn(
                $"[AUTO_CLOSE_CHROME_FORCE] profile={profileName} attempt={attempt}/{delaysMs.Length} stopped={stoppedPids.Count} pids={string.Join(",", stoppedPids)}");

            await Task.Delay(
                delaysMs[attempt - 1]);
        }

        var finalInUse =
            await Task.Run(
                () => IsAutoCloseChromeProfileInUse(
                    profilePath));

        if (finalInUse)
        {
            throw new InvalidOperationException(
                $"Chrome của profile {profileName} vẫn còn chạy sau khi đã retry/kill theo đúng ProfilePath. "
                + "Không tạo suất bù để tránh tích tụ Chrome.");
        }

        _log.Info(
            $"[AUTO_CLOSE_CHROME_VERIFIED_CLOSED] profile={profileName} attempt=final");
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
