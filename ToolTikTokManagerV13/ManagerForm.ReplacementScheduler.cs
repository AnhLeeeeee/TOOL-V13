using System.Text;
using System.Text.Json;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class TimeReplacementRequest
    {
        public string ProfileName { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime DueUtc { get; set; } = DateTime.UtcNow;
        public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
        public bool Granted { get; set; }
        public DateTime? GrantedUtc { get; set; }
        public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
        public string LastWaitReason { get; set; } = "";
    }

    sealed class TimeReplacementSchedulerDocument
    {
        public int Version { get; set; } = 1;
        public DateTime? NextTimeSlotUtc { get; set; }
        public List<TimeReplacementRequest> Pending { get; set; } = new();
    }

    static readonly TimeSpan TimeReplacementSpacing = TimeSpan.FromMinutes(30);
    static readonly TimeSpan TimeReplacementSupplyRetry = TimeSpan.FromMinutes(5);

    readonly object _timeReplacementLock = new();
    readonly Dictionary<string, TimeReplacementRequest> _timeReplacementPending =
        new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _replacementOperationGate = new(1, 1);

    bool _timeReplacementSchedulerInitialized;
    DateTime? _nextTimeReplacementSlotUtc;

    string TimeReplacementSchedulerPath
        => Path.Combine(_baseDir, "manager_time_replacement_scheduler.json");

    void InitializeTimeReplacementScheduler()
    {
        if (_timeReplacementSchedulerInitialized)
            return;

        _timeReplacementSchedulerInitialized = true;

        try
        {
            if (File.Exists(TimeReplacementSchedulerPath))
            {
                var document = JsonSerializer.Deserialize<TimeReplacementSchedulerDocument>(
                    File.ReadAllText(TimeReplacementSchedulerPath));

                if (document is not null)
                {
                    _nextTimeReplacementSlotUtc = document.NextTimeSlotUtc;

                    foreach (var request in document.Pending ?? new List<TimeReplacementRequest>())
                    {
                        var profileName = (request.ProfileName ?? "").Trim();
                        if (profileName.Length == 0)
                            continue;

                        request.ProfileName = profileName;
                        request.Reason = NormalizeTimeReplacementReason(request.Reason);
                        if (!request.Reason.StartsWith("TIME_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Sau restart, request đã GRANTED nhưng transaction bị ngắt phải
                        // được retry cleanup ngay, không ăn thêm một slot 30 phút.
                        if (request.Granted)
                            request.NextAttemptUtc = DateTime.UtcNow;

                        _timeReplacementPending[profileName] = request;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[TIME_REPLACE_STATE_READ_WARN] error={ex.Message}");
            _timeReplacementPending.Clear();
            _nextTimeReplacementSlotUtc = null;
        }

        _log.Info(
            $"[TIME_REPLACE_INIT] spacing={TimeReplacementSpacing.TotalMinutes:0}m pending={_timeReplacementPending.Count} nextSlot={(_nextTimeReplacementSlotUtc.HasValue ? _nextTimeReplacementSlotUtc.Value.ToString("O") : "NOW")}");
    }

    void NotifyTimeReplacementSchedulerSettingsChanged(
        bool oldCloseOnRunTime,
        bool oldStaggerTime30Minutes)
    {
        InitializeTimeReplacementScheduler();

        var oldEnabled = oldCloseOnRunTime && oldStaggerTime30Minutes;
        var newEnabled = _autoCloseSettings.CloseOnRunTime
            && _autoCloseSettings.StaggerTimeReplacement30Minutes;

        if (oldEnabled == newEnabled
            && oldCloseOnRunTime == _autoCloseSettings.CloseOnRunTime)
        {
            return;
        }

        var removed = 0;
        lock (_timeReplacementLock)
        {
            if (!_autoCloseSettings.CloseOnRunTime)
            {
                // Tắt hẳn TIME: bỏ toàn bộ lịch TIME đã lưu. Transaction đang chạy
                // (nếu có) vẫn tự hoàn tất bằng call stack hiện tại.
                removed = _timeReplacementPending.Count;
                _timeReplacementPending.Clear();
                _nextTimeReplacementSlotUtc = null;
            }
            else if (!_autoCloseSettings.StaggerTimeReplacement30Minutes)
            {
                // Chỉ tắt giãn 30 phút: bỏ các request CHƯA được cấp slot để watchdog
                // tick kế tiếp xử lý TIME trực tiếp. Request GRANTED đang cleanup được giữ.
                var waiting = _timeReplacementPending
                    .Where(x => !x.Value.Granted)
                    .Select(x => x.Key)
                    .ToList();

                foreach (var profileName in waiting)
                {
                    if (_timeReplacementPending.Remove(profileName))
                        removed++;
                }

                _nextTimeReplacementSlotUtc = null;
            }
            else if (!oldEnabled && newEnabled)
            {
                // Bật lại: lần TIME đầu tiên đủ điều kiện có thể lấy slot ngay;
                // các slot sau mới cách 30 phút.
                _nextTimeReplacementSlotUtc = null;
            }

            SaveTimeReplacementSchedulerUnsafe();
        }

        var state = newEnabled ? "ON" : "OFF";
        _log.Info(
            $"[TIME_REPLACE_MODE] state={state} closeOnTime={_autoCloseSettings.CloseOnRunTime} stagger30m={_autoCloseSettings.StaggerTimeReplacement30Minutes} removedWaiting={removed}");

        WriteAutoActivityLog(
            action: "GIÃN THAY TIME",
            profile: "-",
            account: "-",
            reason: "MANUAL_SETTING",
            result: state == "ON" ? "BẬT" : "TẮT",
            detail: state == "ON"
                ? $"Các lần thay TIME sẽ cách nhau {TimeReplacementSpacing.TotalMinutes:0} phút. BAN vẫn thay ngay."
                : "TIME đủ giờ sẽ thay ngay như trước; BAN vẫn thay ngay.");
    }

    static string NormalizeTimeReplacementReason(string? reason)
        => (reason ?? "").Trim().ToUpperInvariant();

    void QueueTimeReplacement(
        ProfileContext ctx,
        string reason,
        string detail)
    {
        if (_closing || IsDisposed || Disposing)
            return;

        InitializeTimeReplacementScheduler();

        var profileName = ctx.Profile.Name;
        reason = NormalizeTimeReplacementReason(reason);

        if (!reason.StartsWith("TIME_", StringComparison.OrdinalIgnoreCase))
            return;

        TimeReplacementRequest? queued = null;

        lock (_timeReplacementLock)
        {
            if (_timeReplacementPending.TryGetValue(profileName, out var existing))
            {
                // Giữ DueUtc ban đầu để FIFO không bị thay đổi bởi watchdog 1 giây.
                existing.Reason = reason;
                existing.Detail = detail ?? existing.Detail;
                SaveTimeReplacementSchedulerUnsafe();
                return;
            }

            queued = new TimeReplacementRequest
            {
                ProfileName = profileName,
                Reason = reason,
                Detail = detail ?? "",
                DueUtc = DateTime.UtcNow,
                QueuedUtc = DateTime.UtcNow,
                Granted = false,
                NextAttemptUtc = DateTime.UtcNow
            };

            _timeReplacementPending[profileName] = queued;
            SaveTimeReplacementSchedulerUnsafe();
        }

        var nextSlot = GetEffectiveNextTimeSlotUtc();

        _log.Info(
            $"[TIME_REPLACE_WAITING] profile={profileName} reason={reason} nextSlot={nextSlot:O} spacing={TimeReplacementSpacing.TotalMinutes:0}m action=KEEP_RUNNING");

        WriteAutoActivityLog(
            action: "CHỜ THAY TIME",
            profile: profileName,
            account: ResolveAutoActivityAccount(profileName),
            reason: reason,
            result: "ĐÃ XẾP HÀNG",
            detail: $"Profile vẫn tiếp tục chạy. Slot TIME kế tiếp: {nextSlot.ToLocalTime():dd/MM HH:mm:ss}.");
    }

    void CancelTimeReplacementForBan(string profileName, string detail)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        InitializeTimeReplacementScheduler();

        TimeReplacementRequest? removed = null;

        lock (_timeReplacementLock)
        {
            if (_timeReplacementPending.Remove(profileName, out var request))
            {
                removed = request;
                SaveTimeReplacementSchedulerUnsafe();
            }
        }

        if (removed is null)
            return;

        _log.Warn(
            $"[TIME_REPLACE_CANCELLED_BY_BAN] profile={profileName} granted={removed.Granted} due={removed.DueUtc:O} detail={detail}");

        WriteAutoActivityLog(
            action: "CHỜ THAY TIME",
            profile: profileName,
            account: ResolveAutoActivityAccount(profileName),
            reason: "BAN",
            result: "HỦY - BAN ƯU TIÊN",
            detail: "Đã bỏ khỏi hàng chờ TIME; BAN chuyển sang luồng thay ngay.");
    }

    bool HasPendingTimeReplacement(string profileName)
    {
        lock (_timeReplacementLock)
            return _timeReplacementPending.ContainsKey(profileName);
    }

    async Task ProcessTimeReplacementSchedulerAsync()
    {
        if (!_autoCloseSettings.CloseOnRunTime
            || _closing
            || IsDisposed
            || Disposing)
        {
            return;
        }

        InitializeTimeReplacementScheduler();

        TimeReplacementRequest? request;
        var nowUtc = DateTime.UtcNow;

        lock (_timeReplacementLock)
        {
            // Request đã được cấp slot nhưng cleanup chưa xong có quyền retry trước,
            // không phải chờ thêm 30 phút.
            request = _timeReplacementPending.Values
                .Where(x => x.Granted && x.NextAttemptUtc <= nowUtc)
                .OrderBy(x => x.GrantedUtc ?? x.DueUtc)
                .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                .FirstOrDefault();

            if (request is null)
            {
                // Tắt thủ công giãn TIME: không cấp slot mới. Request đã GRANTED ở trên
                // vẫn được phép hoàn tất cleanup/retry để không bỏ dở transaction.
                if (!_autoCloseSettings.StaggerTimeReplacement30Minutes)
                    return;

                var nextSlot = GetEffectiveNextTimeSlotUtcUnsafe();
                if (nowUtc < nextSlot)
                    return;

                request = _timeReplacementPending.Values
                    .Where(x => !x.Granted && x.NextAttemptUtc <= nowUtc)
                    .OrderBy(x => x.DueUtc)
                    .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                    .FirstOrDefault();
            }
        }

        if (request is null)
            return;

        var profileName = request.ProfileName;

        if (!_contexts.TryGetValue(profileName, out var ctx))
        {
            RemoveTimeReplacementRequest(profileName, "context_missing");
            return;
        }

        // BAN có thể được phát hiện giữa hai tick; không để TIME đóng trước BAN watcher.
        var decision = ResolveAutoCloseReasonDecision(
            profileName,
            request.Reason,
            request.Detail);

        if (NormalizeAutoCloseReason(decision.Reason) == "BAN")
        {
            CancelTimeReplacementForBan(profileName, decision.Detail);
            QueueAutoCloseForBan(ctx, decision.Detail);
            return;
        }

        if (_autoCloseInProgressProfiles.Contains(profileName))
            return;

        if (_autoCloseCleanupRetryUtc.TryGetValue(profileName, out var cleanupRetryUtc)
            && nowUtc < cleanupRetryUtc)
        {
            UpdateTimeReplacementNextAttempt(profileName, cleanupRetryUtc, "auto_close_cleanup_pending");
            return;
        }

        if (!request.Granted)
        {
            var supply = await HasTimeReplacementSupplyAsync();
            if (!supply.Available)
            {
                var retryUtc = DateTime.UtcNow.Add(TimeReplacementSupplyRetry);
                UpdateTimeReplacementNextAttempt(profileName, retryUtc, supply.Detail);

                _log.Warn(
                    $"[TIME_REPLACE_WAIT_SUPPLY] profile={profileName} retry={retryUtc:O} detail={supply.Detail}");

                WriteAutoActivityLog(
                    action: "CHỜ THAY TIME",
                    profile: profileName,
                    account: ResolveAutoActivityAccount(profileName),
                    reason: request.Reason,
                    result: "CHỜ TÀI KHOẢN BÙ",
                    detail: $"{supply.Detail} Kiểm tra lại sau {TimeReplacementSupplyRetry.TotalMinutes:0} phút; profile cũ vẫn chạy.");
                return;
            }

            var grantedUtc = DateTime.UtcNow;

            lock (_timeReplacementLock)
            {
                if (!_timeReplacementPending.TryGetValue(profileName, out var current)
                    || current.Granted)
                {
                    return;
                }

                current.Granted = true;
                current.GrantedUtc = grantedUtc;
                current.NextAttemptUtc = grantedUtc;
                current.LastWaitReason = "";

                // BAN không đụng vào đồng hồ này. Chỉ việc CẤP SLOT TIME mới tiến lịch.
                _nextTimeReplacementSlotUtc = grantedUtc.Add(TimeReplacementSpacing);
                SaveTimeReplacementSchedulerUnsafe();
                request = current;
            }

            _log.Info(
                $"[TIME_REPLACE_GRANTED] profile={profileName} reason={request.Reason} granted={grantedUtc:O} nextSlot={_nextTimeReplacementSlotUtc:O}");

            WriteAutoActivityLog(
                action: "CHỜ THAY TIME",
                profile: profileName,
                account: ResolveAutoActivityAccount(profileName),
                reason: request.Reason,
                result: "ĐẾN LƯỢT THAY",
                detail: $"Đã cấp slot TIME. Slot TIME kế tiếp cách {TimeReplacementSpacing.TotalMinutes:0} phút.");
        }

        await AutoCloseProfileAsync(
            ctx,
            request.Reason,
            request.Detail,
            source: request.GrantedUtc.HasValue
                ? "time_replacement_scheduler"
                : "time_replacement_scheduler_retry");

        // AutoCloseProfileAsync trả về cả khi cleanup pending. Chỉ xóa request khi
        // runtime cũ thực sự đã biến mất và không còn lịch cleanup retry.
        var workerAlive = false;
        try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
        catch { workerAlive = ctx.Worker is not null; }

        var tabOpen = ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        var cleanupPending = _autoCloseCleanupRetryUtc.TryGetValue(profileName, out var retryAt);

        if (!workerAlive && !tabOpen && !ctx.Opening && !cleanupPending)
        {
            RemoveTimeReplacementRequest(profileName, "auto_close_done");
            return;
        }

        UpdateTimeReplacementNextAttempt(
            profileName,
            cleanupPending ? retryAt : DateTime.UtcNow.Add(AutoCloseCleanupRetryDelay),
            cleanupPending ? "cleanup_pending" : "runtime_still_present");
    }

    async Task<(bool Available, string Detail)> HasTimeReplacementSupplyAsync()
    {
        // Nếu người dùng chỉ muốn Tự đóng TIME mà không bật Tự bù thì không cần
        // giữ profile cũ chỉ vì kho bù đang trống.
        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return (true, "Tự bù đang tắt; chỉ cần cấp slot Tự đóng TIME.");

        try
        {
            await RefreshReusableProfileQueueAsync("time_replacement_preflight");
            var reusableCount = GetReusableProfileQueueCount();
            if (reusableCount > 0)
                return (true, $"Có {reusableCount} profile dùng lại khả dụng.");
        }
        catch (Exception ex)
        {
            _log.Warn($"[TIME_REPLACE_PREFLIGHT_REUSE_WARN] error={ex.Message}");
        }

        try
        {
            var hasNewAccount = await RunAccountPoolIoAsync(
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                        _accountPoolService.ReloadCurrentExcel();

                    _accountPoolService.EnsureAutoColumns();

                    var accounts = _accountPoolService.Load();
                    var states = _accountPoolService.LoadAutoStates();

                    return accounts.Any(account =>
                        !account.IsAssigned
                        && !string.IsNullOrWhiteSpace(account.Password)
                        && !TikTokAccountPoolService.IsBanNoteValue(account.Note)
                        && (!states.TryGetValue(account.Id, out var state)
                            || (!state.IsReady && !state.IsInProgress)));
                },
                CancellationToken.None);

            if (hasNewAccount)
                return (true, "Có tài khoản chưa gán để tạo profile mới.");
        }
        catch (Exception ex)
        {
            _log.Warn($"[TIME_REPLACE_PREFLIGHT_ACCOUNT_WARN] error={ex.Message}");
            return (false, "Không kiểm tra được kho tài khoản: " + ex.Message);
        }

        return (false, "Không có profile dùng lại hoặc tài khoản chưa gán hợp lệ.");
    }

    async Task<IDisposable> EnterReplacementOperationAsync(
        string profileName,
        string reason)
    {
        var waited = _replacementOperationGate.CurrentCount == 0;
        if (waited)
        {
            _log.Info(
                $"[REPLACEMENT_OPERATION_WAIT] profile={profileName} reason={reason} detail=another_transaction_running");
        }

        await _replacementOperationGate.WaitAsync();

        _log.Info(
            $"[REPLACEMENT_OPERATION_ENTER] profile={profileName} reason={reason} waited={waited}");

        return new ReplacementOperationLease(
            _replacementOperationGate,
            () => _log.Info(
                $"[REPLACEMENT_OPERATION_EXIT] profile={profileName} reason={reason}"));
    }

    sealed class ReplacementOperationLease : IDisposable
    {
        readonly SemaphoreSlim _gate;
        readonly Action _onDispose;
        int _disposed;

        public ReplacementOperationLease(SemaphoreSlim gate, Action onDispose)
        {
            _gate = gate;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _onDispose(); } catch { }
            _gate.Release();
        }
    }

    void RemoveTimeReplacementRequest(string profileName, string source)
    {
        TimeReplacementRequest? removed = null;

        lock (_timeReplacementLock)
        {
            if (_timeReplacementPending.Remove(profileName, out var request))
            {
                removed = request;
                SaveTimeReplacementSchedulerUnsafe();
            }
        }

        if (removed is not null)
        {
            _log.Info(
                $"[TIME_REPLACE_REMOVE] profile={profileName} source={source} granted={removed.Granted}");
        }
    }

    void UpdateTimeReplacementNextAttempt(
        string profileName,
        DateTime nextAttemptUtc,
        string reason)
    {
        lock (_timeReplacementLock)
        {
            if (!_timeReplacementPending.TryGetValue(profileName, out var request))
                return;

            request.NextAttemptUtc = nextAttemptUtc;
            request.LastWaitReason = reason ?? "";
            SaveTimeReplacementSchedulerUnsafe();
        }
    }

    DateTime GetEffectiveNextTimeSlotUtc()
    {
        lock (_timeReplacementLock)
            return GetEffectiveNextTimeSlotUtcUnsafe();
    }

    DateTime GetEffectiveNextTimeSlotUtcUnsafe()
        => _nextTimeReplacementSlotUtc ?? DateTime.UtcNow;

    void SaveTimeReplacementSchedulerUnsafe()
    {
        try
        {
            var document = new TimeReplacementSchedulerDocument
            {
                Version = 1,
                NextTimeSlotUtc = _nextTimeReplacementSlotUtc,
                Pending = _timeReplacementPending.Values
                    .OrderBy(x => x.DueUtc)
                    .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true });

            var temp = TimeReplacementSchedulerPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, TimeReplacementSchedulerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"[TIME_REPLACE_STATE_WRITE_WARN] error={ex.Message}");
        }
    }
}
