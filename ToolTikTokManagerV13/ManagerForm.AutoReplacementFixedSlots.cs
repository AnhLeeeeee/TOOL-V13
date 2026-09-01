using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed record AutoReplacementCleanupResult(
        bool Succeeded,
        string Detail);

    sealed record AutoReplacementSlotGateResult(
        bool CanOpenReplacement,
        bool AlreadySatisfied,
        int TargetSlots,
        int OccupiedSlots,
        string Detail);

    readonly object _autoReplacementFixedSlotLock = new();
    int _autoReplacementTargetSlots;

    void CaptureAutoReplacementTargetBeforeAutoClose(
        ProfileContext ctx,
        string reason)
    {
        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return;

        var occupied = CountAutoReplacementOccupiedSlots();

        // Profile đang Tự đóng có thể chưa nằm trong expected set vì một race trạng thái.
        // Bảo đảm chính slot đang đóng vẫn được tính vào target trước khi cleanup.
        if (!IsAutoReplacementSlotCurrentlyCounted(ctx.Profile.Name))
            occupied++;

        lock (_autoReplacementFixedSlotLock)
        {
            if (occupied > _autoReplacementTargetSlots)
            {
                var old = _autoReplacementTargetSlots;
                _autoReplacementTargetSlots = occupied;

                _log.Info(
                    $"[AUTO_REPLACE_TARGET_CAPTURE] old={old} target={_autoReplacementTargetSlots} profile={ctx.Profile.Name} reason={reason}");
            }
        }
    }

    void TrackAutoReplacementTargetRuntimeCommand(
        ProfileContext ctx,
        string command)
    {
        command = (command ?? "").Trim().ToLowerInvariant();
        var profileName = ctx.Profile.Name;

        // start_auto là profile do Tự bù/Auto Profile tạo; không được tự làm target phình lên.
        if (command == "start")
        {
            var occupied = CountAutoReplacementOccupiedSlots();

            lock (_autoReplacementFixedSlotLock)
            {
                if (occupied > _autoReplacementTargetSlots)
                {
                    var old = _autoReplacementTargetSlots;
                    _autoReplacementTargetSlots = occupied;
                    _log.Info(
                        $"[AUTO_REPLACE_TARGET_MANUAL_EXPAND] old={old} target={_autoReplacementTargetSlots} profile={profileName}");
                }
            }

            return;
        }

        if (command != "stop")
            return;

        // STOP do AutoClose/cleanup của profile bù lỗi không phải ý định giảm số suất của user.
        if (_autoCloseInProgressProfiles.Contains(profileName)
            || _autoReplacementClaimedProfiles.Contains(profileName))
        {
            return;
        }

        lock (_autoReplacementFixedSlotLock)
        {
            if (_autoReplacementTargetSlots <= 0)
                return;

            var old = _autoReplacementTargetSlots;
            _autoReplacementTargetSlots = Math.Max(
                CountAutoReplacementOccupiedSlots(),
                _autoReplacementTargetSlots - 1);

            if (old != _autoReplacementTargetSlots)
            {
                _log.Info(
                    $"[AUTO_REPLACE_TARGET_MANUAL_SHRINK] old={old} target={_autoReplacementTargetSlots} profile={profileName}");
            }
        }
    }

    bool IsAutoReplacementSlotCurrentlyCounted(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        if (_autoCloseExpectedRunningProfiles.Contains(profileName)
            || _autoReplacementClaimedProfiles.Contains(profileName))
        {
            return true;
        }

        if (!_contexts.TryGetValue(profileName, out var ctx))
            return false;

        var state = GetEffectiveRuntimeState(ctx);
        return state is RuntimeStateRunning or RuntimeStateRecovering;
    }

    int CountAutoReplacementOccupiedSlots()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _autoCloseExpectedRunningProfiles)
            names.Add(name);

        foreach (var name in _autoReplacementClaimedProfiles)
            names.Add(name);

        foreach (var ctx in _contexts.Values)
        {
            var state = GetEffectiveRuntimeState(ctx);
            if (state is RuntimeStateRunning or RuntimeStateRecovering)
            {
                names.Add(ctx.Profile.Name);
                continue;
            }

            // Safety net: profile bù lỗi có thể đang STOPPED/DISCONNECTED nhưng
            // Worker/tab/Opening vẫn còn. Nếu không tính các runtime vật lý này,
            // slot gate tưởng còn chỗ trống và tiếp tục mở Chrome mới.
            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
            catch { workerAlive = ctx.Worker is not null; }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            if (workerAlive || tabOpen || ctx.Opening)
                names.Add(ctx.Profile.Name);
        }

        return names.Count;
    }

    AutoReplacementSlotGateResult EvaluateAutoReplacementFixedSlotGate(
        AutoReplacementRequest request)
    {
        var occupied = CountAutoReplacementOccupiedSlots();
        int target;

        lock (_autoReplacementFixedSlotLock)
        {
            // Nếu target chưa được capture (ví dụ Manager adopt Worker cũ),
            // tối thiểu giữ đúng số suất hiện có + request đang cần bù.
            if (_autoReplacementTargetSlots <= 0)
                _autoReplacementTargetSlots = Math.Max(1, occupied + 1);

            target = _autoReplacementTargetSlots;
        }

        if (occupied >= target)
        {
            return new AutoReplacementSlotGateResult(
                false,
                true,
                target,
                occupied,
                $"Đã đủ suất: occupied={occupied}, target={target}. Không mở thêm Chrome/profile bù.");
        }

        // Queue xử lý tuần tự nên chỉ cần một chỗ trống là request hiện tại được phép lấp.
        return new AutoReplacementSlotGateResult(
            true,
            false,
            target,
            occupied,
            $"Có 1 slot trống: occupied={occupied}, target={target}.");
    }

    async Task<AutoReplacementCleanupResult> EnsureAutoReplacementSourceCleanupAsync(
        AutoReplacementRequest request)
    {
        var profileName = (request.ClosedProfileName ?? "").Trim();
        if (profileName.Length == 0)
            return new AutoReplacementCleanupResult(false, "Thiếu tên profile cũ.");

        // Context còn tồn tại: dọn Worker/tab nếu AutoClose trước đó bị ngắt giữa chừng.
        if (_contexts.TryGetValue(profileName, out var ctx))
        {
            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; } catch { workerAlive = ctx.Worker is not null; }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            if (workerAlive || tabOpen || ctx.Opening)
            {
                try
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                }
                catch (Exception ex)
                {
                    return new AutoReplacementCleanupResult(
                        false,
                        "Không dọn được Worker/tab profile cũ: " + ex.Message);
                }
            }

            try
            {
                await EnsureAutoCloseChromeStoppedAsync(ctx);
            }
            catch (Exception ex)
            {
                return new AutoReplacementCleanupResult(false, ex.Message);
            }

            // Xác minh lại Worker và tab sau cleanup.
            try
            {
                if (ctx.Worker is not null && !ctx.Worker.HasExited)
                    return new AutoReplacementCleanupResult(false, "Worker profile cũ vẫn còn chạy.");
            }
            catch
            {
                return new AutoReplacementCleanupResult(false, "Không xác minh được Worker profile cũ đã thoát.");
            }

            if (ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs)
            {
                return new AutoReplacementCleanupResult(false, "Tab Manager profile cũ vẫn còn mở.");
            }

            return new AutoReplacementCleanupResult(
                true,
                "CLEANUP_DONE: Chrome=0 process, Worker=closed, tab=removed.");
        }

        // Context đã bị xóa (ví dụ BAN/TIME bật auto-delete): nếu catalog cũng không còn
        // thì deletion flow đã dọn xong và request được phép bù.
        var catalog = _profileService.Load();
        var profile = catalog.Profiles.FirstOrDefault(x =>
            x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new AutoReplacementCleanupResult(
                true,
                "CLEANUP_DONE: profile đã được xóa khỏi catalog.");
        }

        // Context mất nhưng profile vẫn còn catalog: vẫn phải verify process bằng ProfilePath.
        try
        {
            await EnsureAutoCloseChromeStoppedByPathAsync(
                profileName,
                profile.ProfilePath);
        }
        catch (Exception ex)
        {
            return new AutoReplacementCleanupResult(false, ex.Message);
        }

        return new AutoReplacementCleanupResult(
            true,
            "CLEANUP_DONE: context không còn, Chrome đã xác minh 0 process.");
    }
}
