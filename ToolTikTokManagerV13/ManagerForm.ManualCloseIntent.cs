using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const string WorkerManualCloseIntentFileName = "worker_manual_close_intent.json";

    readonly HashSet<string> _consumedManualCloseOperationIds =
        new(StringComparer.OrdinalIgnoreCase);

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

    void ApplyManualCloseTargetShrink(
        string profileName,
        string origin,
        string operationId,
        string source)
    {
        profileName = (profileName ?? "").Trim();
        origin = (origin ?? "").Trim();
        operationId = (operationId ?? "").Trim();

        int oldTarget;
        int newTarget;

        lock (_autoReplacementFixedSlotLock)
        {
            oldTarget = _autoReplacementTargetSlots;

            // Nếu phiên chưa từng có Target thì không tự tạo target âm/ảo.
            if (!_autoReplacementSessionArmed || oldTarget <= 0)
            {
                _log.Info(
                    $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE_IGNORED] profile={profileName} origin={origin} operationId={operationId} source={source} armed={_autoReplacementSessionArmed} target={oldTarget}");
                return;
            }

            newTarget = Math.Max(0, oldTarget - 1);
            _autoReplacementTargetSlots = newTarget;

            // Cho reconcile kế tiếp chạy ngay thay vì chờ đủ 15 giây.
            _autoReplacementNextCapacityReconcileUtc = DateTime.MinValue;
        }

        // Request CAPACITY_RECONCILE chỉ là quota gap tổng quát. Sau khi user chủ động
        // giảm target, bỏ toàn bộ request loại này rồi để reconcile tính lại từ đầu.
        // Request do AutoClose thật (BAN/TIME/FAULT...) vẫn giữ nguyên và sẽ qua slot gate.
        var removedCapacityRequests = 0;

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

            if (removedCapacityRequests > 0)
                SaveAutoReplacementQueueUnsafe();
        }

        _log.Warn(
            $"[AUTO_REPLACE_TARGET_MANUAL_CLOSE] profile={profileName} origin={origin} operationId={operationId} source={source} old={oldTarget} target={newTarget} removedCapacityRequests={removedCapacityRequests}");

        WriteAutoActivityLog(
            action: "GIẢM SUẤT",
            profile: profileName,
            reason: origin,
            result: $"{oldTarget} → {newTarget}",
            detail:
                $"operationId={operationId}; source={source}; "
                + $"đã bỏ {removedCapacityRequests} request CAPACITY_RECONCILE để tính lại.");

        // Nếu vẫn còn thiếu suất thật do một profile khác lỗi tự động, reconcile sẽ
        // tự tạo lại đúng số còn thiếu theo target mới.
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
        var operationId = Guid.NewGuid().ToString("N");

        lock (_autoReplacementFixedSlotLock)
        {
            if (!_consumedManualCloseOperationIds.Add(operationId))
                return;
        }

        ApplyManualCloseTargetShrink(
            ctx.Profile.Name,
            origin,
            operationId,
            "manager_manual_close");
    }
}
