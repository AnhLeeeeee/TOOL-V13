using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const string WorkerManualCloseIntentFileName = "worker_manual_close_intent.json";

    readonly HashSet<string> _consumedManualCloseOperationIds =
        new(StringComparer.OrdinalIgnoreCase);

    // Manual close phải thắng mọi cơ chế Tự bù. Giữ một suppression ngắn hạn để
    // candidate đang STABILIZING/NAME_SYNC không tự mở lại ngay sau khi user vừa đóng.
    // START thủ công sẽ xóa suppression ngay lập tức.
    readonly Dictionary<string, DateTime> _manualCloseSuppressedUntilUtc =
        new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan ManualCloseSuppressionWindow = TimeSpan.FromMinutes(10);

    sealed class WorkerManualCloseIntentDocument
    {
        public int Version { get; set; }
        public string ProfileName { get; set; } = "";
        public int WorkerPid { get; set; }
        public string Origin { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    void TryConsumeWorkerManualCloseIntent(
        ProfileContext ctx,
        int? expectedWorkerPid = null,
        string source = "runtime_poll")
    {
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            var path = Path.Combine(dataRoot, WorkerManualCloseIntentFileName);
            if (!File.Exists(path))
                return;

            WorkerManualCloseIntentDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<WorkerManualCloseIntentDocument>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[MANUAL_CLOSE_INTENT_READ_FAILED] profile={ctx.Profile.Name} source={source} error={ex.Message}");
                return;
            }

            if (document is null
                || !string.Equals(
                    (document.Origin ?? "").Trim(),
                    "USER_X_CLOSE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var profileName = (document.ProfileName ?? "").Trim();
            if (!profileName.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase))
                return;

            int currentPid;
            try
            {
                currentPid = expectedWorkerPid
                    ?? (ctx.Worker is null ? 0 : ctx.Worker.Id);
            }
            catch
            {
                currentPid = expectedWorkerPid ?? 0;
            }

            // Marker cũ của Worker trước tuyệt đối không được làm giảm target.
            if (currentPid <= 0 || document.WorkerPid != currentPid)
                return;

            var operationId = (document.OperationId ?? "").Trim();
            if (operationId.Length == 0)
                return;

            // X trực tiếp cửa sổ Worker cũng chỉ giảm target nếu profile thật sự đang
            // thuộc quota hiện tại. Worker của một tab mở để xem nhưng chưa Start không
            // được làm target giảm nhầm.
            if (!IsManualCloseTargetMember(ctx))
            {
                _log.Info(
                    $"[MANUAL_CLOSE_INTENT_NOT_TARGET_MEMBER] profile={ctx.Profile.Name} source={source} operationId={operationId}");
                try { File.Delete(path); } catch { }
                return;
            }

            // Marker rất cũ cũng không được áp dụng cho phiên hiện tại.
            var age = DateTime.UtcNow - document.CreatedUtc;
            if (document.CreatedUtc == default
                || age < TimeSpan.FromMinutes(-1)
                || age > TimeSpan.FromMinutes(10))
            {
                return;
            }

            lock (_autoReplacementFixedSlotLock)
            {
                if (!_consumedManualCloseOperationIds.Add(operationId))
                    return;
            }

            ApplyManualCloseTargetShrink(
                ctx.Profile.Name,
                "USER_X_CLOSE",
                operationId,
                source);

            try { File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[MANUAL_CLOSE_INTENT_CONSUME_WARN] profile={ctx.Profile.Name} source={source} error={ex.Message}");
        }
    }

    bool IsManualCloseSuppressed(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        lock (_autoReplacementFixedSlotLock)
        {
            if (!_manualCloseSuppressedUntilUtc.TryGetValue(profileName, out var untilUtc))
                return false;

            if (DateTime.UtcNow < untilUtc)
                return true;

            _manualCloseSuppressedUntilUtc.Remove(profileName);
            return false;
        }
    }

    void ClearManualCloseSuppression(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var removed = false;
        lock (_autoReplacementFixedSlotLock)
            removed = _manualCloseSuppressedUntilUtc.Remove(profileName);

        if (removed)
        {
            _log.Info(
                $"[MANUAL_CLOSE_SUPPRESSION_CLEARED] profile={profileName} source={source}");
        }
    }

    bool IsManualCloseTargetMember(ProfileContext ctx)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        if (_autoCloseExpectedRunningProfiles.Contains(profileName)
            || _autoReplacementClaimedProfiles.Contains(profileName)
            || _autoReplacementCleanupProfiles.Contains(profileName))
        {
            return true;
        }

        var state = GetEffectiveRuntimeState(ctx);
        return state is RuntimeStateRunning or RuntimeStateRecovering or RuntimeStatePaused;
    }

    void ApplyManualCloseTargetShrink(
        string profileName,
        string origin,
        string operationId,
        string source)
    {
        profileName = (profileName ?? "").Trim();
        origin = (origin ?? "").Trim();
        operationId = (operationId ?? "").Trim();

        if (profileName.Length == 0)
            return;

        int oldTarget;
        int newTarget;
        bool targetChanged;

        lock (_autoReplacementFixedSlotLock)
        {
            // Một thao tác Stop rồi X tab ngay sau đó vẫn chỉ được giảm đúng 1 suất.
            // START thủ công sẽ clear suppression, khi đó lần Stop tiếp theo mới được
            // xem là một manual-close mới.
            if (_manualCloseSuppressedUntilUtc.TryGetValue(profileName, out var untilUtc)
                && DateTime.UtcNow < untilUtc)
            {
                _log.Info(
                    $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE_DUPLICATE] profile={profileName} origin={origin} operationId={operationId} source={source} target={_autoReplacementTargetSlots}");
                return;
            }

            _manualCloseSuppressedUntilUtc[profileName] =
                DateTime.UtcNow.Add(ManualCloseSuppressionWindow);

            oldTarget = _autoReplacementTargetSlots;
            targetChanged = _autoReplacementSessionArmed
                            && _autoReplacementTargetInitialized
                            && oldTarget > 0;

            newTarget = targetChanged
                ? Math.Max(0, oldTarget - 1)
                : oldTarget;

            if (targetChanged)
            {
                _autoReplacementTargetSlots = newTarget;
                _autoReplacementTargetInitialized = true;
            }

            // Cho reconcile kế tiếp chạy ngay; reconcile đang chạy cũng sẽ re-read
            // target trước pass 2 nên không thể dùng quota cũ để mở bù lại.
            _autoReplacementNextCapacityReconcileUtc = DateTime.MinValue;
        }

        // Manual close là intent mạnh: bỏ request quota tổng quát và cả request AutoClose
        // của chính profile nếu có race user bấm X đúng lúc AutoClose vừa xếp hàng.
        var removedCapacityRequests = 0;
        var removedSourceRequests = 0;

        lock (_autoReplacementQueueLock)
        {
            removedCapacityRequests = _autoReplacementQueue.RemoveAll(x =>
                string.Equals(
                    (x.Reason ?? "").Trim(),
                    "CAPACITY_RECONCILE",
                    StringComparison.OrdinalIgnoreCase)
                || (x.ClosedProfileName ?? "").StartsWith(
                    "CAPACITY_GAP_",
                    StringComparison.OrdinalIgnoreCase));

            removedSourceRequests = _autoReplacementQueue.RemoveAll(x =>
                string.Equals(
                    (x.ClosedProfileName ?? "").Trim(),
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

            if (removedCapacityRequests > 0 || removedSourceRequests > 0)
                SaveAutoReplacementQueueUnsafe();
        }

        ClearAutoCloseExpectedRunning(
            profileName,
            "manual_close:" + origin);

        if (!targetChanged)
        {
            _log.Info(
                $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE_NO_TARGET_CHANGE] profile={profileName} origin={origin} operationId={operationId} source={source} armed={_autoReplacementSessionArmed} initialized={_autoReplacementTargetInitialized} target={oldTarget} removedCapacityRequests={removedCapacityRequests} removedSourceRequests={removedSourceRequests}");
        }
        else
        {
            _log.Warn(
                $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE] profile={profileName} origin={origin} operationId={operationId} source={source} old={oldTarget} target={newTarget} removedCapacityRequests={removedCapacityRequests} removedSourceRequests={removedSourceRequests}");

            WriteAutoActivityLog(
                action: "GIẢM SUẤT",
                profile: profileName,
                reason: origin,
                result: $"{oldTarget} → {newTarget}",
                detail:
                    $"operationId={operationId}; source={source}; "
                    + $"bỏ {removedCapacityRequests} CAPACITY_RECONCILE; "
                    + $"bỏ {removedSourceRequests} request của chính profile.");
        }

        // Chạy reconcile lại theo target MỚI. Nếu target đã về 0 thì hàm reconcile
        // thoát ngay; fixed-slot gate cũng không được phép tự khởi tạo target=1 nữa.
        if (!_closing
            && _autoReplacementSessionArmed
            && _autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await Task.Delay(Random.Shared.Next(1000, 2001));
                    await MaybeReconcileAutoReplacementCapacityAsync(
                        "manual_close_target_changed",
                        force: true);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_MANUAL_CLOSE_RECONCILE_WARN] profile={profileName} error={ex.Message}");
                }
            }));
        }
    }

    void RegisterManagerManualCloseIntent(
        ProfileContext ctx,
        string origin)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();
        if (profileName.Length == 0)
            return;

        // Đóng một tab STOPPED chưa từng chiếm target không được làm giảm quota.
        // CLAIMED/STABILIZING và PAUSED đều là member thật của target nên vẫn xử lý.
        if (!IsManualCloseTargetMember(ctx))
        {
            _log.Info(
                $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE_NOT_MEMBER] profile={profileName} origin={origin} target={_autoReplacementTargetSlots}");
            return;
        }

        var operationId = Guid.NewGuid().ToString("N");

        lock (_autoReplacementFixedSlotLock)
        {
            if (!_consumedManualCloseOperationIds.Add(operationId))
                return;
        }

        ApplyManualCloseTargetShrink(
            profileName,
            origin,
            operationId,
            "manager_manual_close");
    }
}
