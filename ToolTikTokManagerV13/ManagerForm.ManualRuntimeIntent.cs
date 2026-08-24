namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _manualRuntimeIntentGuardInitialized;

    void InitializeManualRuntimeIntentGuard()
    {
        if (_manualRuntimeIntentGuardInitialized)
            return;

        _manualRuntimeIntentGuardInitialized = true;

        // Chỉ chạy khi TabPage đã THỰC SỰ bị gỡ khỏi Manager.
        // Nếu người dùng bấm X nhưng Cancel hộp xác nhận thì ControlRemoved
        // không xảy ra, expected-running vẫn giữ nguyên.
        _tabs.ControlRemoved += (_, e) =>
        {
            try
            {
                if (e.Control is not TabPage page)
                    return;

                if (page.Tag is not ProfileContext ctx)
                    return;

                ClearAutoCloseExpectedRunning(
                    ctx.Profile.Name,
                    "profile_tab_removed");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_MANUAL_INTENT_WARN] error={ex.Message}");
            }
        };
    }

    void MarkAutoCloseExpectedRunning(
        string profileName,
        string source)
    {
        profileName =
            (profileName ?? "").Trim();

        if (profileName.Length == 0)
            return;

        var added =
            _autoCloseExpectedRunningProfiles.Add(
                profileName);

        _autoCloseNotRunningSinceUtc.Remove(
            profileName);

        if (added)
        {
            _log.Info(
                $"[AUTO_CLOSE_EXPECTED_ON] profile={profileName} source={source}");
        }
    }

    void ClearAutoCloseExpectedRunning(
        string profileName,
        string source)
    {
        profileName =
            (profileName ?? "").Trim();

        if (profileName.Length == 0)
            return;

        var removed =
            _autoCloseExpectedRunningProfiles.Remove(
                profileName);

        var clearedFaultTimer =
            _autoCloseNotRunningSinceUtc.Remove(
                profileName);

        if (removed || clearedFaultTimer)
        {
            _log.Info(
                $"[AUTO_CLOSE_EXPECTED_OFF] profile={profileName} source={source} expectedRemoved={removed} faultTimerCleared={clearedFaultTimer}");
        }
    }
}
