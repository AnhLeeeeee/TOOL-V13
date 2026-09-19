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
        public bool WasRunning { get; set; }
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

            if (document is null)
                return;

            var origin = (document.Origin ?? "").Trim().ToUpperInvariant();
            if (origin is not ("USER_X_CLOSE" or "USER_STOP" or "USER_START"))
                return;

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

            if (origin == "USER_START")
            {
                ApplyWorkerManualStartIntent(ctx, operationId, source);
                try { File.Delete(path); } catch { }
                return;
            }

            // USER_STOP được ghi TRƯỚC khi Worker gọi _engine.Stop(), nên WasRunning=true
            // là bằng chứng trực tiếp profile đang chiếm một suất tại thời điểm user bấm
            // Dừng. Điều này tránh race: đến lúc Manager đọc marker, runtime đã STOPPED.
            // Marker V1 cũ không có WasRunning thì vẫn dùng fallback runtime hiện tại.
            if (!document.WasRunning && !IsManualCloseTargetMember(ctx))
            {
                _log.Info(
                    $"[MANUAL_CLOSE_INTENT_NOT_TARGET_MEMBER] profile={ctx.Profile.Name} origin={origin} source={source} operationId={operationId} wasRunning={document.WasRunning}");
                try { File.Delete(path); } catch { }
                return;
            }

            ApplyManualCloseTargetShrink(
                ctx.Profile.Name,
                origin,
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

    void ApplyWorkerManualStartIntent(
        ProfileContext ctx,
        string operationId,
        string source)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();
        if (profileName.Length == 0)
            return;

        // Người dùng bấm Start trực tiếp trong Worker sau một lần manual Stop.
        // Khôi phục quota theo đúng occupied hiện tại và bỏ suppression cũ.
        ClearManualCloseSuppression(profileName, "worker_user_start");
        MarkAutoCloseExpectedRunning(profileName, "worker_user_start");
        TrackAutoReplacementTargetRuntimeCommand(
            ctx,
            "start",
            explicitUserStartIntent: true);
        ArmAutoReplacementSession("worker_user_start:" + profileName);

        _log.Info(
            $"[MANUAL_START_INTENT_APPLIED] profile={profileName} operationId={operationId} source={source} target={_autoReplacementTargetSlots}");
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
        if (state is RuntimeStateRunning or RuntimeStateRecovering or RuntimeStatePaused)
            return true;

        // PHẢI khớp với CountAutoReplacementOccupiedSlots(): một profile có thể đã
        // báo STOPPED nhưng tab/Worker/Opening vẫn còn và vẫn đang chiếm một slot vật lý.
        // Nếu manual-close bỏ qua trường hợp này thì target không giảm; ngay sau khi
        // tab/Worker biến mất, capacity reconcile sẽ hiểu nhầm là thiếu suất và mở bù.
        var workerAlive = false;
        try
        {
            workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
        }
        catch
        {
            // Fail-closed giống bộ đếm slot: còn object Worker thì xem như vẫn chiếm suất.
            workerAlive = ctx.Worker is not null;
        }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        var physicallyOccupiesSlot = workerAlive || tabOpen || ctx.Opening;
        if (!physicallyOccupiesSlot)
            return false;

        // Không giảm nhầm target khi đây chỉ là một tab/Worker mở thêm để xem ngoài quota.
        // Chỉ coi fallback vật lý là target-member nếu bỏ chính slot này sẽ làm occupied
        // tụt xuống dưới target hiện tại. Trường hợp occupied > target nghĩa là vẫn còn
        // đủ slot khác để giữ quota, nên đóng tab phụ không được co target.
        int targetSlots;
        bool targetInitialized;
        lock (_autoReplacementFixedSlotLock)
        {
            targetSlots = _autoReplacementTargetSlots;
            targetInitialized = _autoReplacementTargetInitialized;
        }

        if (!_autoReplacementSessionArmed || !targetInitialized || targetSlots <= 0)
            return false;

        var occupiedSlots = CountAutoReplacementOccupiedSlots();
        return Math.Max(0, occupiedSlots - 1) < targetSlots;
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

        // Target vừa co xuống thì mọi lượt Tự bù/Auto Run bootstrap đang chạy với
        // snapshot target CŨ phải dừng ngay. Nếu không, một request đã qua slot-gate
        // trước lúc user đóng vẫn có thể mở PRF mới dù target đã giảm.
        if (targetChanged)
        {
            InvalidateAutoReplacementExecution(
                $"manual_close_target_shrink:{profileName}:{origin}");

            CancellationTokenSource? runAllStartToCancel = null;
            CancellationTokenSource? runStrategyToCancel = null;
            int oldRunStrategyTarget = 0;
            int newRunStrategyTarget = 0;

            lock (_runStrategyLock)
            {
                if (!_runAllStartCts.IsCancellationRequested)
                    runAllStartToCancel = _runAllStartCts;

                if (_runStrategySessionActive && _runStrategyTargetSlots > 0)
                {
                    oldRunStrategyTarget = _runStrategyTargetSlots;
                    _runStrategyTargetSlots = Math.Max(0, _runStrategyTargetSlots - 1);
                    newRunStrategyTarget = _runStrategyTargetSlots;

                    if (_runStrategyTargetSlots == 0)
                    {
                        _runStrategySessionActive = false;
                        if (!_runStrategyCts.IsCancellationRequested)
                            runStrategyToCancel = _runStrategyCts;
                    }
                }
            }

            try { runAllStartToCancel?.Cancel(); } catch { }
            try { runStrategyToCancel?.Cancel(); } catch { }

            if (oldRunStrategyTarget > 0)
            {
                _log.Info(
                    $"[RUN_STRATEGY_TARGET_MANUAL_CLOSE] profile={profileName} old={oldRunStrategyTarget} target={newRunStrategyTarget} origin={origin}");
            }

            // Manual close đã giảm fixed target bằng engine cũ; chỉ đồng bộ snapshot
            // để nếu Manager restart sau đó thì không hồi target cũ trở lại.
            PersistRunStrategySessionTarget(
                newTarget,
                active: newTarget > 0,
                source: $"manual_close:{profileName}:{origin}");
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
