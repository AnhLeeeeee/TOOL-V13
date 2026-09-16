using System.Text;
using System.Text.Json;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoReplacementCleanupBarrierException : InvalidOperationException
    {
        public string ProfileName { get; }

        public AutoReplacementCleanupBarrierException(
            string profileName,
            string message,
            Exception inner)
            : base(message, inner)
        {
            ProfileName = (profileName ?? "").Trim();
        }
    }

    sealed class AutoReplacementRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ClosedProfileName { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
        public int AttemptCount { get; set; }
        public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;

        // Retry dài chỉ dành cho NHÁNH TẠO PRF MỚI. Trong thời gian này request
        // vẫn có thể được đánh thức ngay nếu xuất hiện PRF Chờ dùng lại phù hợp.
        public DateTime? CreateNotBeforeUtc { get; set; }

        public string LastError { get; set; } = "";

        // Mỗi profile bù chỉ được mở/thử tối đa 1 lần trong cùng một suất bù.
        // Danh sách này sống xuyên suốt các RETRY của chính request để profile
        // vừa kiểm tra tên/chạy lỗi không bị mở lại liên tục khi TikTok chưa kịp đồng bộ.
        public List<string> AttemptedProfiles { get; set; } = new();

        // Request sinh từ thiếu suất sau Start All không có profile nguồn cần cleanup.
        // Request AutoClose bình thường luôn giữ true.
        public bool RequiresSourceCleanup { get; set; } = true;
    }

    sealed class AutoReplacementQueueDocument
    {
        public int Version { get; set; } = 3;
        public List<AutoReplacementRequest> Pending { get; set; } = new();
    }

    sealed record AutoReplacementCandidate(
        ProfileContext Context,
        TikTokAccountPoolItem Account,
        string SupplyState,
        TimeSpan TotalRuntime,
        int Priority);

    sealed record AutoReplacementStabilizationResult(
        bool Healthy,
        bool NameSyncPending,
        bool HardFailed,
        string Detail);

    sealed class ProfileSupplyStateDocument
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ProfileSupplyStateEntry> Profiles { get; set; } = new();
    }

    sealed class ProfileSupplyStateEntry
    {
        public string State { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    const double AutoReplacementTreoThresholdMinutes = 30.0;
    const int AutoReplacementHealthyConfirmTimeoutSeconds = AutoCloseNotRunningMinutes * 60;
    const int AutoReplacementHealthyStableSeconds = 30;
    static readonly TimeSpan AutoReplacementStabilizationRecoveryInterval = TimeSpan.FromSeconds(30);

    // PRF lấy từ Chờ dùng lại đã tồn tại + đã đăng nhập từ trước: không được
    // giữ slot trong grace 10 phút như PRF vừa tạo/login. Chỉ cho một cửa sổ
    // ngắn để Worker/Chrome khởi động và xác nhận RUNNING ổn định.
    const int AutoReplacementReusableHealthyConfirmTimeoutSeconds = 45;
    const int AutoReplacementReusableHealthyStableSeconds = 4;
    const int AutoReplacementReusableStartCommandTimeoutSeconds = 20;
    static readonly TimeSpan AutoReplacementReusableRecoveryInterval = TimeSpan.FromSeconds(8);
    static readonly TimeSpan AutoReplacementFailedProfileCooldown = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AutoReplacementQueueWaitSlice = TimeSpan.FromSeconds(5);
    static readonly TimeSpan AutoReplacementReusableStateRetry = TimeSpan.FromSeconds(5);
    static readonly TimeSpan AutoReplacementOperationalRetry = TimeSpan.FromSeconds(10);
    const int AutoReplacementCleanupBarrierRetrySeconds = 15;
    const int AutoReplacementCleanupBarrierMaxAttempts = 4;

    // Profile NAME_SYNC_PENDING đã tồn tại phải được ưu tiên TRƯỚC khi tiêu account mới,
    // nhưng không mở lại ngay sau khi vừa đóng. Mỗi profile cần nghỉ tối thiểu 60 giây
    // kể từ lần queue/probe gần nhất; đồng thời AttemptedProfiles chặn thử lại trong cùng suất.
    static readonly TimeSpan AutoReplacementNameSyncMinRetryAge = TimeSpan.FromSeconds(60);

    readonly List<AutoReplacementRequest> _autoReplacementQueue = new();
    readonly HashSet<string> _autoReplacementRetiredProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoReplacementClaimedProfiles = new(StringComparer.OrdinalIgnoreCase);
    // Profile đang CLEANUP vẫn chiếm slot cho tới khi xác minh đóng sạch hoặc hết
    // safety-valve. Capacity reconcile không được mở bù chồng lên runtime đang dọn.
    readonly HashSet<string> _autoReplacementCleanupProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoReplacementFailedProfileRetryUtc = new(StringComparer.OrdinalIgnoreCase);
    readonly object _autoReplacementQueueLock = new();
    readonly object _profileSupplyStateLock = new();
    readonly object _autoReplacementExecutionLock = new();

    // Cooldown CHỈ dành cho nhánh tạo PRF MỚI của Tự bù / Run Strategy.
    // PRF đã có sẵn trong Chờ dùng lại không đi qua gate này nên vẫn được mở ngay.
    // Deadline dùng chung cho mọi suất tạo mới để khi một suất vừa tạo xong/lỗi,
    // suất kế tiếp cũng phải tôn trọng đúng cấu hình ở cửa sổ "+ Auto Profile".
    readonly object _autoReplacementCreateCooldownLock = new();
    DateTime _autoReplacementCreateNotBeforeUtc = DateTime.MinValue;
    AutoProfileCooldownKind _autoReplacementCreateCooldownKind = AutoProfileCooldownKind.Normal;
    TimeSpan _autoReplacementCreateCooldownBase = TimeSpan.Zero;
    TimeSpan _autoReplacementCreateCooldownActual = TimeSpan.Zero;
    int _autoReplacementCreateConsecutiveLoginErrors;

    CancellationTokenSource _autoReplacementExecutionCts = new();
    int _autoReplacementExecutionGeneration;
    bool _autoReplacementFeatureInitialized;
    bool _autoReplacementQueueRunning;
    bool _autoReplacementSessionArmed;

    // Trạng thái quan sát (UI-only) của luồng Tự bù. Không tham gia quyết định logic.
    // Mục tiêu là giúp phân biệt Tool đang thật sự xử lý/chờ retry với trường hợp bị treo.
    readonly object _autoReplacementUiPhaseLock = new();
    string _autoReplacementUiPhase = "";
    string _autoReplacementUiDetail = "";
    string _autoReplacementUiRequestId = "";
    DateTime _autoReplacementUiPhaseUtc = DateTime.MinValue;

    void RegisterAutoReplacementCreateCooldown(
        AutoProfileProcessOutcome? outcome,
        string profileName,
        string requestId,
        string source)
    {
        var settings = LoadAutoProfileCooldownSettings();
        AutoProfileCooldownKind kind;
        TimeSpan baseDelay;
        TimeSpan actualDelay;
        DateTime notBeforeUtc;
        int consecutiveLoginErrors;

        lock (_autoReplacementCreateCooldownLock)
        {
            kind = ResolveAutoProfileCooldownKind(
                outcome,
                ref _autoReplacementCreateConsecutiveLoginErrors);

            baseDelay = GetAutoProfileBaseCooldown(settings, kind);
            actualDelay = ApplyAutoProfileCooldownJitter(
                baseDelay,
                settings.JitterSeconds);

            _autoReplacementCreateCooldownKind = kind;
            _autoReplacementCreateCooldownBase = baseDelay;
            _autoReplacementCreateCooldownActual = actualDelay;
            _autoReplacementCreateNotBeforeUtc = DateTime.UtcNow.Add(actualDelay);

            notBeforeUtc = _autoReplacementCreateNotBeforeUtc;
            consecutiveLoginErrors = _autoReplacementCreateConsecutiveLoginErrors;
        }

        _log.Info(
            $"[AUTO_REPLACE_CREATE_COOLDOWN_SET] request={requestId} profile={profileName} source={source} " +
            $"kind={kind} base={baseDelay:c} jitter=±{settings.JitterSeconds}s actual={actualDelay:c} " +
            $"nextCreate={notBeforeUtc:O} consecutiveLoginErrors={consecutiveLoginErrors}");
    }

    async Task<bool> WaitAutoReplacementCreateCooldownBeforeNewProfileAsync(
        AutoReplacementRequest request,
        string nextProfileName,
        int executionGeneration,
        CancellationToken executionToken)
    {
        var logged = false;
        AutoProfileCooldownKind loggedKind = AutoProfileCooldownKind.Normal;
        DateTime loggedDeadline = DateTime.MinValue;

        while (true)
        {
            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return false;

            executionToken.ThrowIfCancellationRequested();

            DateTime notBeforeUtc;
            AutoProfileCooldownKind kind;
            TimeSpan baseDelay;
            TimeSpan actualDelay;

            lock (_autoReplacementCreateCooldownLock)
            {
                notBeforeUtc = _autoReplacementCreateNotBeforeUtc;
                kind = _autoReplacementCreateCooldownKind;
                baseDelay = _autoReplacementCreateCooldownBase;
                actualDelay = _autoReplacementCreateCooldownActual;
            }

            var remaining = notBeforeUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                if (logged)
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CREATE_COOLDOWN_END] request={request.Id} profile={nextProfileName} " +
                        $"kind={loggedKind} deadline={loggedDeadline:O}");
                }

                return true;
            }

            if (!logged)
            {
                logged = true;
                loggedKind = kind;
                loggedDeadline = notBeforeUtc;

                _log.Info(
                    $"[AUTO_REPLACE_CREATE_COOLDOWN_BEGIN] request={request.Id} profile={nextProfileName} " +
                    $"kind={kind} base={baseDelay:c} actual={actualDelay:c} remaining={remaining:c} deadline={notBeforeUtc:O}");
            }

            var reason = DescribeAutoProfileCooldownKind(kind);
            SetAutoReplacementUiPhase(
                "CHỜ COOLDOWN TẠO PRF",
                $"{reason} · {FormatAutoReplacementUiWait(remaining)} · kế tiếp {nextProfileName}",
                request.Id);

            var slice = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;

            if (slice > TimeSpan.Zero)
                await Task.Delay(slice, executionToken);
        }
    }

    void SetAutoReplacementUiPhase(
        string phase,
        string detail = "",
        string requestId = "")
    {
        phase = (phase ?? "").Trim();
        detail = (detail ?? "").Trim();
        requestId = (requestId ?? "").Trim();

        lock (_autoReplacementUiPhaseLock)
        {
            // Nếu chỉ detail thay đổi trong cùng phase/request thì vẫn reset tuổi vì
            // đây là một bước mới (ví dụ từ a18 sang a19).
            if (!string.Equals(_autoReplacementUiPhase, phase, StringComparison.Ordinal)
                || !string.Equals(_autoReplacementUiDetail, detail, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_autoReplacementUiRequestId, requestId, StringComparison.OrdinalIgnoreCase))
            {
                _autoReplacementUiPhaseUtc = DateTime.UtcNow;
            }

            _autoReplacementUiPhase = phase;
            _autoReplacementUiDetail = detail;
            _autoReplacementUiRequestId = requestId;

            if (phase.Length == 0)
                _autoReplacementUiPhaseUtc = DateTime.MinValue;
        }
    }

    void ClearAutoReplacementUiPhase(string requestId = "")
    {
        requestId = (requestId ?? "").Trim();

        lock (_autoReplacementUiPhaseLock)
        {
            if (requestId.Length > 0
                && _autoReplacementUiRequestId.Length > 0
                && !_autoReplacementUiRequestId.Equals(requestId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _autoReplacementUiPhase = "";
            _autoReplacementUiDetail = "";
            _autoReplacementUiRequestId = "";
            _autoReplacementUiPhaseUtc = DateTime.MinValue;
        }
    }

    (string Phase, string Detail, TimeSpan Age) GetAutoReplacementUiPhaseSnapshot()
    {
        lock (_autoReplacementUiPhaseLock)
        {
            var age = _autoReplacementUiPhaseUtc == DateTime.MinValue
                ? TimeSpan.Zero
                : DateTime.UtcNow - _autoReplacementUiPhaseUtc;

            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;

            return (_autoReplacementUiPhase, _autoReplacementUiDetail, age);
        }
    }

    (int Generation, CancellationToken Token) CaptureAutoReplacementExecution()
    {
        lock (_autoReplacementExecutionLock)
        {
            return (
                _autoReplacementExecutionGeneration,
                _autoReplacementExecutionCts.Token);
        }
    }

    bool IsAutoReplacementExecutionAllowed(int generation)
    {
        if (IsAutomationHalted
            || _closing
            || IsDisposed
            || Disposing
            || !_autoReplacementSessionArmed
            || !_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            return false;
        }

        lock (_autoReplacementExecutionLock)
        {
            return generation == _autoReplacementExecutionGeneration
                   && !_autoReplacementExecutionCts.IsCancellationRequested;
        }
    }

    int InvalidateAutoReplacementExecution(string source)
    {
        CancellationTokenSource previous;
        int generation;

        lock (_autoReplacementExecutionLock)
        {
            previous = _autoReplacementExecutionCts;
            _autoReplacementExecutionCts = new CancellationTokenSource();
            generation = ++_autoReplacementExecutionGeneration;
        }

        try
        {
            previous.Cancel();
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_REPLACE_HARD_STOP_CANCEL_WARN] source={source} generation={generation} error={ex.Message}");
        }

        _log.Warn(
            $"[AUTO_REPLACE_HARD_STOP_INVALIDATE] source={source} generation={generation}");

        return generation;
    }

    string ProfileSupplyStatePath => Path.Combine(_baseDir, "manager_profile_supply.json");
    string AutoReplacementQueuePath => Path.Combine(_baseDir, "manager_auto_replacement_queue.json");

    void InitializeAutoReplacementFeature()
    {
        if (_autoReplacementFeatureInitialized)
            return;

        _autoReplacementFeatureInitialized = true;

        // V13.6.9 HOTFIX: queue bù chỉ có hiệu lực trong đúng phiên Manager hiện tại.
        // Nếu Manager bị đóng/crash/End Task khi còn suất bù dở, phiên sau bỏ toàn bộ
        // backlog cũ để tránh START/RESUME vô tình giải phóng hàng loạt profile bù.
        var discardedCount = 0;

        try
        {
            var previous = LoadAutoReplacementQueueDocument();
            discardedCount = previous.Pending
                .Count(x => !string.IsNullOrWhiteSpace(x.ClosedProfileName));
        }
        catch (Exception ex)
        {
            // LoadAutoReplacementQueueDocument hiện đã fail-safe, nhưng vẫn giữ lớp bảo vệ
            // này để việc dọn queue không bao giờ làm Manager lỗi lúc khởi động.
            _log.Warn($"[AUTO_REPLACE_STARTUP_CLEAR_READ_WARN] {ex.Message}");
        }

        lock (_autoReplacementQueueLock)
        {
            _autoReplacementQueue.Clear();

            try
            {
                // Ghi lại file rỗng thay vì chỉ xóa file: các hàm retry trong phiên
                // hiện tại vẫn dùng cùng một định dạng queue và không cần nhánh đặc biệt.
                SaveAutoReplacementQueueUnsafe();
            }
            catch (Exception ex)
            {
                _log.Warn($"[AUTO_REPLACE_STARTUP_CLEAR_WRITE_WARN] {ex.Message}");
            }
        }

        _autoReplacementSessionArmed = false;

        if (discardedCount > 0)
        {
            _log.Warn(
                $"[AUTO_REPLACE_QUEUE_CLEARED_ON_STARTUP] discarded={discardedCount} path={AutoReplacementQueuePath} currentPending=0");

            WriteAutoActivityLog(
                action: "HỆ THỐNG BÙ",
                result: "ĐÃ XÓA QUEUE CŨ",
                detail: $"Mở Manager: bỏ {discardedCount} suất bù còn lại từ phiên trước.");
        }
        else
        {
            _log.Info(
                $"[AUTO_REPLACE_QUEUE_EMPTY_ON_STARTUP] path={AutoReplacementQueuePath} currentPending=0");
        }

        UpdateAutoCloseToolbarButtonText();

        // Reconcile nhẹ mỗi ~15 giây để phiên Tự bù giữ đúng số suất mục tiêu.
        // Hàm bên trong có throttle + busy guard nên Tick 1 giây không tạo tải đáng kể.
        _refreshTimer.Tick += async (_, _) => await MaybeReconcileAutoReplacementCapacityAsync();

        // Queue "dùng lại" là queue riêng, KHÔNG bị xóa cùng suất bù cũ.
        // Quét sau khi form hiển thị để không làm chậm thời điểm tạo Handle/UI.
        Shown += async (_, _) =>
        {
            try
            {
                await RefreshReusableProfileQueueAsync("manager_shown");
            }
            catch (Exception ex)
            {
                _log.Warn($"[REUSE_QUEUE_STARTUP_WARN] {ex.Message}");
            }
        };
    }

    void ArmAutoReplacementSession(string source)
    {
        if (IsAutomationHalted || _closing || IsDisposed || Disposing)
            return;

        if (!_autoReplacementFeatureInitialized)
            InitializeAutoReplacementFeature();

        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return;

        source = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();

        if (!_autoReplacementSessionArmed)
        {
            _autoReplacementSessionArmed = true;
            _log.Info(
                $"[AUTO_REPLACE_SESSION_ARMED] source={source} pending={GetAutoReplacementPendingCount()}");
            UpdateAutoCloseToolbarButtonText();
        }

        if (GetAutoReplacementPendingCount() > 0)
            _ = RunAutoReplacementQueueAsync();
    }

    void NotifyAutoReplacementSettingsChanged()
    {
        if (!_autoReplacementFeatureInitialized)
            return;

        if (_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            if (_autoReplacementSessionArmed)
            {
                _log.Info(
                    $"[AUTO_REPLACE_RESUME_SETTING] pending={GetAutoReplacementPendingCount()} armed=true");
                _ = RunAutoReplacementQueueAsync();
            }
            else
            {
                _log.Info(
                    $"[AUTO_REPLACE_RESUME_SETTING_HELD] pending={GetAutoReplacementPendingCount()} armed=false action=wait_for_start_or_new_auto_close");
            }
        }
        else
        {
            _log.Info(
                $"[AUTO_REPLACE_PAUSE_SETTING] pending={GetAutoReplacementPendingCount()} preserved=true");
        }

        UpdateAutoCloseToolbarButtonText();
    }

    void QueueAutoReplacementAfterAutoClose(string closedProfileName, string reason)
    {
        if (IsAutomationHalted || _closing || IsDisposed || Disposing)
            return;

        closedProfileName = (closedProfileName ?? "").Trim();
        reason = (reason ?? "").Trim();

        if (closedProfileName.Length == 0)
            return;

        // Manual-close thắng AutoClose kể cả khi hai flow race nhau ở cuối cleanup.
        // Nếu user đã chủ động đóng profile trong lúc AutoClose đang chạy, không retire
        // profile và tuyệt đối không sinh request bù từ callback AutoClose muộn này.
        if (IsManualCloseSuppressed(closedProfileName))
        {
            _log.Warn(
                $"[AUTO_REPLACE_SKIP_AFTER_MANUAL_CLOSE] closed={closedProfileName} reason={reason} action=NO_RETIRE_NO_QUEUE");
            return;
        }

        // Profile đã Tự đóng là profile ĐÃ TREO/ĐÃ DÙNG.
        // Ghi bền vững để sau khi mở lại Manager cũng không bị lấy làm profile bù.
        _autoReplacementRetiredProfiles.Add(closedProfileName);
        MarkProfileSupplyState(closedProfileName, "retired", "auto_close:" + reason);

        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return;

        InitializeAutoReplacementFeature();

        AutoReplacementRequest queued;

        lock (_autoReplacementQueueLock)
        {
            var duplicate = _autoReplacementQueue.FirstOrDefault(x =>
                x.ClosedProfileName.Equals(closedProfileName, StringComparison.OrdinalIgnoreCase));

            if (duplicate is not null)
            {
                _log.Info(
                    $"[AUTO_REPLACE_QUEUE_DUPLICATE] closed={closedProfileName} id={duplicate.Id} pending={_autoReplacementQueue.Count}");

                // Duplicate chỉ có thể thuộc phiên hiện tại vì backlog của phiên trước
                // đã được xóa khi Manager khởi động. ARM để request hiện tại tiếp tục xử lý.
                ArmAutoReplacementSession("new_auto_close_duplicate:" + reason);
                return;
            }

            queued = new AutoReplacementRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                ClosedProfileName = closedProfileName,
                Reason = reason,
                QueuedUtc = DateTime.UtcNow,
                NextAttemptUtc = DateTime.UtcNow
            };

            _autoReplacementQueue.Add(queued);
            SaveAutoReplacementQueueUnsafe();
        }

        _log.Info(
            $"[AUTO_REPLACE_QUEUE] id={queued.Id} closed={closedProfileName} reason={reason} pending={GetAutoReplacementPendingCount()} persisted=true");

        WriteAutoActivityLog(
            action: "SUẤT BÙ",
            profile: closedProfileName,
            account: ResolveAutoActivityAccount(closedProfileName),
            reason: reason,
            result: "ĐÃ XẾP HÀNG",
            detail: $"pending={GetAutoReplacementPendingCount()}");

        // Sự kiện Tự đóng mới trong phiên hiện tại cho phép bù.
        ArmAutoReplacementSession("new_auto_close:" + reason);
    }

    async Task RunAutoReplacementQueueAsync()
    {
        if (IsAutomationHalted)
            return;

        if (!_autoReplacementSessionArmed)
        {
            if (GetAutoReplacementPendingCount() > 0)
                _log.Info($"[AUTO_REPLACE_QUEUE_HELD] pending={GetAutoReplacementPendingCount()} armed=false");
            return;
        }

        if (_autoReplacementQueueRunning)
            return;

        _autoReplacementQueueRunning = true;

        try
        {
            while (!_closing && !IsDisposed && !Disposing)
            {
                if (IsAutomationHalted)
                    return;

                if (!_autoReplacementSessionArmed)
                {
                    _log.Info(
                        $"[AUTO_REPLACE_QUEUE_SESSION_PAUSED] pending={GetAutoReplacementPendingCount()} armed=false");
                    return;
                }

                if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
                {
                    _log.Info(
                        $"[AUTO_REPLACE_QUEUE_PAUSED] pending={GetAutoReplacementPendingCount()} preserved=true");
                    return;
                }

                AutoReplacementRequest? request = null;
                TimeSpan wait = TimeSpan.Zero;

                lock (_autoReplacementQueueLock)
                {
                    if (_autoReplacementQueue.Count == 0)
                        return;

                    var nowUtc = DateTime.UtcNow;

                    request = _autoReplacementQueue
                        .Where(x => x.NextAttemptUtc <= nowUtc)
                        .OrderBy(x => x.NextAttemptUtc)
                        .ThenBy(x => x.QueuedUtc)
                        .FirstOrDefault();

                    if (request is null)
                    {
                        var earliest = _autoReplacementQueue.Min(x => x.NextAttemptUtc);
                        wait = earliest > nowUtc
                            ? earliest - nowUtc
                            : TimeSpan.FromMilliseconds(500);
                    }
                }

                if (request is null)
                {
                    var delay = wait <= TimeSpan.Zero
                        ? TimeSpan.FromMilliseconds(500)
                        : wait > AutoReplacementQueueWaitSlice
                            ? AutoReplacementQueueWaitSlice
                            : wait;

                    await Task.Delay(delay);
                    continue;
                }

                SetAutoReplacementUiPhase(
                    "XỬ LÝ SUẤT",
                    request.Reason,
                    request.Id);

                var execution =
                    CaptureAutoReplacementExecution();

                if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_BEFORE_REQUEST] id={request.Id} generation={execution.Generation}");
                    return;
                }

                // Queue Tự bù có gate RIÊNG để các suất bù không đè nhau.
                // Gate này KHÔNG chặn AutoClose; TIME/BAN/FAULT vẫn được đóng đúng lượt
                // ngay cả khi một suất bù đang tạo PRF mới hoặc chờ cleanup.
                using var replacementOperationLease = await EnterReplacementOperationAsync(
                    request.ClosedProfileName,
                    "AUTO_REPLACE:" + request.Reason);

                if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_AFTER_OPERATION_GATE] id={request.Id} generation={execution.Generation}");
                    return;
                }

                // CLEANUP BARRIER: request do AutoClose sinh ra phải chờ profile cũ sạch thật.
                // Request CAPACITY_RECONCILE chỉ đại diện cho một slot bị thiếu sau Start All,
                // không có profile nguồn nên bỏ qua source-cleanup và đi thẳng tới slot gate.
                if (request.RequiresSourceCleanup)
                {
                    SetAutoReplacementUiPhase(
                        "DỌN PRF CŨ",
                        request.ClosedProfileName,
                        request.Id);

                    var cleanup = await EnsureAutoReplacementSourceCleanupAsync(request);

                    if (!cleanup.Succeeded)
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_CLEANUP_BLOCKED] id={request.Id} closed={request.ClosedProfileName} detail={cleanup.Detail}");

                        WriteAutoActivityLog(
                            action: "TỰ BÙ",
                            profile: request.ClosedProfileName,
                            reason: request.Reason,
                            result: "CHỜ DỌN PROFILE CŨ",
                            detail: cleanup.Detail);

                        ClearAutoReplacementUiPhase(request.Id);
                        ScheduleAutoReplacementOperationalRetry(
                            request.Id,
                            "Cleanup chưa hoàn tất: " + cleanup.Detail,
                            AutoReplacementOperationalRetry);

                        continue;
                    }
                }
                else
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CAPACITY_CLEANUP_BYPASS] id={request.Id} slot={request.ClosedProfileName} reason={request.Reason}");
                }

                if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_AFTER_SOURCE_CLEANUP] id={request.Id} generation={execution.Generation}");
                    return;
                }

                // SLOT-GATE HEALING: ExpectedRunning là intent marker, không phải bằng chứng
                // runtime vật lý. Nếu marker cũ bị sót sau khi Chrome/Worker đã đóng,
                // CountAutoReplacementOccupiedSlots() có thể tưởng đã đủ suất và xóa
                // request Tự bù. Xác minh các marker nghi stale 2 lượt trước khi đếm.
                SetAutoReplacementUiPhase(
                    "KIỂM TRA SUẤT",
                    "xác minh slot đang chạy",
                    request.Id);

                await PruneStaleAutoReplacementExpectedRunningAsync(request);

                if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_AFTER_SLOT_HEAL] id={request.Id} generation={execution.Generation}");
                    return;
                }

                var slotGate = EvaluateAutoReplacementFixedSlotGate(request);

                if (slotGate.AlreadySatisfied)
                {
                    ClearAutoReplacementUiPhase(request.Id);
                    RemoveAutoReplacementRequest(request.Id);

                    _log.Warn(
                        $"[AUTO_REPLACE_SLOT_SUPPRESSED] id={request.Id} closed={request.ClosedProfileName} target={slotGate.TargetSlots} occupied={slotGate.OccupiedSlots} detail={slotGate.Detail}");

                    WriteAutoActivityLog(
                        action: "SUẤT BÙ",
                        profile: request.ClosedProfileName,
                        reason: request.Reason,
                        result: "BỎ QUA - ĐỦ SUẤT",
                        detail: slotGate.Detail);

                    continue;
                }

                if (!slotGate.CanOpenReplacement)
                {
                    ClearAutoReplacementUiPhase(request.Id);
                    ScheduleAutoReplacementOperationalRetry(
                        request.Id,
                        slotGate.Detail,
                        AutoReplacementReusableStateRetry);
                    continue;
                }

                var filled = false;
                var lastError = "";
                var reusableGuardBlocked = false;
                var createDeferredByCooldown = false;
                var createAttempted = false;
                AutoReplacementCleanupBarrierException? cleanupBarrier = null;

                try
                {
                    // V13.7.0: ƯU TIÊN profile đã có trong queue dùng lại.
                    // Queue được tạo bằng runtime_stats.json (<1h) + Ghi chú trống,
                    // không quét PowerShell/IsProfileInUse trên toàn bộ catalog.
                    _log.Info(
                        $"[AUTO_REPLACE_REUSE_FIRST] id={request.Id} closed={request.ClosedProfileName} reusePending={GetReusableProfileQueueCount()}");

                    SetAutoReplacementUiPhase(
                        "TÌM PRF CHỜ",
                        $"{GetReusableProfileQueueCount()} PRF",
                        request.Id);

                    filled = await TryOpenNextExistingReplacementAsync(
                        request,
                        execution.Generation,
                        execution.Token);

                    if (!filled)
                    {
                        if (SuppressAutoReplacementRequestIfTargetSatisfied(
                                request,
                                "after_reuse"))
                        {
                            continue;
                        }

                        if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                        {
                            _log.Warn(
                                $"[AUTO_REPLACE_HARD_STOP_BEFORE_NAME_SYNC_REUSE] id={request.Id} generation={execution.Generation}");
                            return;
                        }

                        // V13.9.9: profile đã tồn tại luôn được ưu tiên trước account mới.
                        // Sau lane dùng lại bình thường, vét NAME_SYNC_PENDING đủ tuổi đúng
                        // 1 lần/profile/suất. Profile vừa tạo/kiểm tra chưa đủ 60s sẽ bỏ qua.
                        _log.Info(
                            $"[AUTO_REPLACE_NAME_SYNC_BEFORE_NEW] id={request.Id} closed={request.ClosedProfileName} pending={GetNameSyncPendingReusableProfileCount()} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)}");

                        SetAutoReplacementUiPhase(
                            "KIỂM TRA CHỜ TÊN",
                            $"{GetNameSyncPendingReusableProfileCount()} PRF",
                            request.Id);

                        filled = await TryRecoverNameSyncPendingReusableProfilesOnceAsync(
                            request,
                            execution.Generation,
                            execution.Token);
                    }

                    if (!filled)
                    {
                        if (SuppressAutoReplacementRequestIfTargetSatisfied(
                                request,
                                "after_name_sync_reuse"))
                        {
                            continue;
                        }

                        if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                        {
                            _log.Warn(
                                $"[AUTO_REPLACE_HARD_STOP_BEFORE_FALLBACK_NEW] id={request.Id} generation={execution.Generation}");
                            return;
                        }

                        if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
                        {
                            // Chế độ dọn kho: tuyệt đối không tiêu account mới. Sau khi đã
                            // thử mỗi PRF chờ tối đa 1 lần trong vòng hiện tại, giữ slot
                            // ở trạng thái CHỜ và bắt đầu một vòng mới sau 5 phút.
                            lastError =
                                "Chế độ CHỈ PRF CHỜ: đã thử hết profile chờ phù hợp; không tạo profile mới.";

                            _log.Warn(
                                $"[AUTO_REPLACE_REUSE_ONLY_EXHAUSTED] id={request.Id} closed={request.ClosedProfileName} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)} action=wait_new_round");
                        }
                        else
                        {
                            // Safety guard cuối: bình thường hai sweep phía trên phải đã
                            // thử/loại hợp lệ toàn bộ PRF dùng được NGAY BÂY GIỜ. Nếu vẫn
                            // còn một PRF hợp lệ chưa được attempted thì đây là dấu hiệu
                            // sweep bị rơi qua do lỗi/race; giữ suất để retry, không được
                            // tiêu account mới và tạo PRF mới.
                            if (TryFindUntestedEligibleReusableProfile(
                                    request,
                                    out var untestedProfile,
                                    out var untestedLane,
                                    out var untestedDetail))
                            {
                                reusableGuardBlocked = true;
                                lastError =
                                    $"Còn PRF chờ hợp lệ chưa được thử: {untestedProfile} ({untestedLane}). {untestedDetail}";

                                _log.Warn(
                                    $"[AUTO_REPLACE_REUSE_GUARD_BLOCK_NEW] id={request.Id} closed={request.ClosedProfileName} profile={untestedProfile} lane={untestedLane} detail={untestedDetail} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)}");
                            }
                            else if (TryGetAutoReplacementCreateCooldown(
                                         request,
                                         out var createWait))
                            {
                                // Retry dài chỉ khóa NHÁNH TẠO MỚI. Nếu trong lúc chờ có
                                // PRF dùng lại xuất hiện, WakeAutoReplacementForReusableSupply
                                // sẽ đánh thức request để dùng PRF đó ngay, không chờ hết timer.
                                createDeferredByCooldown = true;
                                lastError =
                                    $"Chưa có PRF chờ phù hợp; chờ {FormatAutoReplacementUiWait(createWait)} trước khi thử tạo PRF mới lại.";

                                SetAutoReplacementUiPhase(
                                    "CHỜ TẠO PRF MỚI",
                                    FormatAutoReplacementUiWait(createWait),
                                    request.Id);
                            }
                            else
                            {
                                // Chỉ sau khi đã vét profile có sẵn (thường + NAME_SYNC_PENDING)
                                // mới tiêu account chưa gán để tạo profile mới.
                                _log.Info(
                                    $"[AUTO_REPLACE_REUSE_EXHAUSTED_FALLBACK_NEW] id={request.Id} closed={request.ClosedProfileName} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)}");

                                SetAutoReplacementUiPhase(
                                    "TẠO PRF MỚI",
                                    "đã vét PRF chờ phù hợp",
                                    request.Id);

                                createAttempted = true;
                                filled = await TryCreateReplacementAsync(
                                    request,
                                    execution.Generation,
                                    execution.Token);
                            }
                        }
                    }

                    if (!filled
                        && SuppressAutoReplacementRequestIfTargetSatisfied(
                            request,
                            "after_fallback_new"))
                    {
                        continue;
                    }

                    if (!filled && string.IsNullOrWhiteSpace(lastError))
                    {
                        lastError =
                            "Không dùng lại được profile chờ và chưa tạo được profile mới từ tài khoản chưa gán; giữ suất bù để thử lại.";
                    }
                }
                catch (OperationCanceledException)
                    when (execution.Token.IsCancellationRequested
                          || !IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_CANCELLED] id={request.Id} closed={request.ClosedProfileName} generation={execution.Generation}");
                    return;
                }
                catch (AutoReplacementCleanupBarrierException ex)
                {
                    cleanupBarrier = ex;
                    lastError = ex.Message;

                    _log.Warn(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_BLOCK] id={request.Id} closed={request.ClosedProfileName} blockedProfile={ex.ProfileName} error={ex.Message}");

                    WriteAutoActivityLog(
                        action: "TỰ BÙ",
                        profile: request.ClosedProfileName,
                        reason: request.Reason,
                        replacementProfile: ex.ProfileName,
                        result: "CHỜ DỌN PROFILE BÙ LỖI",
                        detail: ex.Message);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    _log.Error(
                        $"[AUTO_REPLACE_ERROR] id={request.Id} closed={request.ClosedProfileName} reason={request.Reason} error={ex}");

                    WriteAutoActivityLog(
                        action: "TỰ BÙ",
                        profile: request.ClosedProfileName,
                        reason: request.Reason,
                        result: "LỖI",
                        detail: ex.Message);
                }

                if (!IsAutoReplacementExecutionAllowed(execution.Generation))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_AFTER_ATTEMPT] id={request.Id} generation={execution.Generation} filled={filled}");
                    return;
                }

                if (filled)
                {
                    ClearAutoReplacementUiPhase(request.Id);
                    RemoveAutoReplacementRequest(request.Id);

                    _log.Info(
                        $"[AUTO_REPLACE_SLOT_DONE] id={request.Id} closed={request.ClosedProfileName} pending={GetAutoReplacementPendingCount()}");
                }
                else
                {
                    ClearAutoReplacementUiPhase(request.Id);

                    if (_autoCloseSettings.ReuseOnlyNoCreateProfile
                        && lastError.StartsWith("Chế độ CHỈ PRF CHỜ", StringComparison.OrdinalIgnoreCase))
                    {
                        ScheduleAutoReplacementReuseOnlyRoundRetry(request.Id, lastError);
                    }
                    else if (cleanupBarrier is not null)
                    {
                        ScheduleAutoReplacementOperationalRetry(
                            request.Id,
                            lastError,
                            TimeSpan.FromSeconds(AutoReplacementCleanupBarrierRetrySeconds));
                    }
                    else if (reusableGuardBlocked)
                    {
                        // Có PRF chờ nhưng state còn OPENING/UNKNOWN hoặc sweep vừa race:
                        // chỉ chờ ngắn để kiểm tra lại, tuyệt đối không đẩy sang timer tạo mới.
                        ScheduleAutoReplacementOperationalRetry(
                            request.Id,
                            lastError,
                            AutoReplacementReusableStateRetry);
                    }
                    else if (createDeferredByCooldown)
                    {
                        ScheduleAutoReplacementAtCreateDeadline(request.Id, lastError);
                    }
                    else if (createAttempted)
                    {
                        ScheduleAutoReplacementCreateRetry(request.Id, lastError);
                    }
                    else
                    {
                        ScheduleAutoReplacementOperationalRetry(
                            request.Id,
                            lastError,
                            AutoReplacementOperationalRetry);
                    }

                    // Cleanup profile bù lỗi là GLOBAL BARRIER của queue bù.
                    // Chưa dọn sạch A thì RunAutoReplacementQueueAsync đứng tại đây,
                    // không được mở B/C từ request khác.
                    if (cleanupBarrier is not null)
                        await WaitForReplacementCleanupBarrierAsync(cleanupBarrier, request);
                }
            }
        }
        finally
        {
            _autoReplacementQueueRunning = false;

            if (GetAutoReplacementPendingCount() > 0
                && !_closing
                && _autoReplacementSessionArmed
                && _autoCloseSettings.OpenReplacementAfterAutoClose)
            {
                _ = RunAutoReplacementQueueAsync();
            }
        }
    }

    async Task<bool> TryOpenNextExistingReplacementAsync(
        AutoReplacementRequest request,
        int executionGeneration,
        CancellationToken executionToken)
    {
        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();

        // Không dùng scan MỚI/TEST cũ nữa vì scan đó gọi
        // ChromeProfileNameSyncService.IsProfileInUse() cho từng profile.
        // Queue dùng lại đã được lọc trước bằng file nhỏ runtime_stats.json.
        return await TryUseReusableProfileQueueAsync(
            request,
            executionGeneration,
            executionToken);
    }

    async Task CloseFailedReplacementRuntimeAsync(ProfileContext ctx)
    {
        void ThrowIfEmergencyStopRequested(string phase)
        {
            if (!IsAutomationHalted)
                return;

            _log.Warn(
                $"[AUTO_REPLACE_CLEANUP_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase={phase}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_REPLACEMENT_CLEANUP: {phase}");
        }

        ThrowIfEmergencyStopRequested("begin");

        var cleanupProfileName = (ctx.Profile.Name ?? "").Trim();
        if (cleanupProfileName.Length > 0)
            _autoReplacementCleanupProfiles.Add(cleanupProfileName);

        WriteAutoDiagnosticEvent(
            ctx,
            "auto_replacement",
            "REPLACEMENT_FAILED",
            "CLOSE_REQUEST",
            "Profile bù lỗi bắt đầu cleanup barrier.");

        try
        {
            // Một profile bù thất bại phải được dọn SẠCH trước khi queue được phép
            // mở profile bù khác. Không nuốt cleanup failure.
            ThrowIfEmergencyStopRequested("before_stop_worker");

            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                try
                {
                    await SendCommandAsync(
                        ctx,
                        "stop",
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_FAILED_STOP_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }

                ThrowIfEmergencyStopRequested("before_close_chrome");

                try
                {
                    var closeReply = await SendCloseChromeCommandAsync(ctx);
                    _log.Info(
                        $"[AUTO_REPLACE_FAILED_CHROME] profile={ctx.Profile.Name} reply={closeReply}");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_FAILED_CHROME_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            // Shutdown Worker trước để không còn nguồn tái sinh Chrome. Sau đó chỉ
            // chạy một cleanup probe theo đúng ProfilePath.
            ThrowIfEmergencyStopRequested("before_shutdown_worker");
            await EnsureAutoCloseWorkerStoppedAsync(ctx, respectEmergencyStop: true);
            ThrowIfEmergencyStopRequested("before_chrome_cleanup");
            await EnsureAutoCloseChromeStoppedAsync(ctx, respectEmergencyStop: true);
            ThrowIfEmergencyStopRequested("before_remove_tab");

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            _log.Info(
                $"[AUTO_REPLACE_FAILED_CLEANUP_DONE] profile={ctx.Profile.Name} chrome=0 worker=closed tab=removed");

            ClearAutoCloseExpectedRunning(
                ctx.Profile.Name,
                "auto_replacement_failed_cleanup_done");

            if (cleanupProfileName.Length > 0)
                _autoReplacementCleanupProfiles.Remove(cleanupProfileName);

            WriteAutoDiagnosticEvent(
                ctx,
                "auto_replacement",
                "REPLACEMENT_FAILED",
                "CLOSED",
                "chrome=0; worker=closed; tab=removed");
        }
        catch (OperationCanceledException ex)
            when (IsAutomationHalted
                  || ex.Message.StartsWith("EMERGENCY_STOP_", StringComparison.Ordinal))
        {
            throw;
        }
        catch (AutoReplacementCleanupBarrierException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"[AUTO_REPLACE_FAILED_CLEANUP_BLOCKED] profile={ctx.Profile.Name} error={ex}");

            WriteAutoDiagnosticEvent(
                ctx,
                "auto_replacement",
                "REPLACEMENT_FAILED",
                "CLEANUP_BLOCKED",
                $"exception={ex.GetType().Name}; message={ex.Message}");

            throw new AutoReplacementCleanupBarrierException(
                ctx.Profile.Name,
                $"Profile bù {ctx.Profile.Name} chưa cleanup hoàn tất; chặn mở profile bù khác.",
                ex);
        }
    }

    async Task CleanupCreatedReplacementAttemptAsync(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        if (IsAutomationHalted)
        {
            _log.Warn(
                $"[AUTO_REPLACE_CREATE_CLEANUP_ABORT_EMERGENCY] profile={profileName} source={source}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_REPLACEMENT_CLEANUP: created:{source}");
        }

        _autoReplacementCleanupProfiles.Add(profileName);

        if (!_contexts.TryGetValue(profileName, out var ctx))
        {
            try
            {
                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);
                _contexts.TryGetValue(profileName, out ctx);
            }
            catch { }
        }

        if (ctx is null)
        {
            try
            {
                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);

                if (_contexts.TryGetValue(profileName, out ctx))
                {
                    _log.Warn($"[AUTO_REPLACE_FAILED_CLEANUP_BEGIN] profile={profileName} source={source}:refreshed_context");
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    return;
                }

                var profile = catalog.Profiles.FirstOrDefault(x =>
                    x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    _autoReplacementCleanupProfiles.Remove(profileName);
                    return;
                }

                _log.Warn($"[AUTO_REPLACE_FAILED_CLEANUP_BEGIN] profile={profileName} source={source}:path_only");
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    profileName,
                    profile.ProfilePath,
                    respectEmergencyStop: true);
                ClearAutoCloseExpectedRunning(
                    profileName,
                    "auto_replacement_path_cleanup_done");
                _autoReplacementCleanupProfiles.Remove(profileName);
                return;
            }
            catch (OperationCanceledException ex)
                when (IsAutomationHalted
                      || ex.Message.StartsWith("EMERGENCY_STOP_", StringComparison.Ordinal))
            {
                throw;
            }
            catch (AutoReplacementCleanupBarrierException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AutoReplacementCleanupBarrierException(
                    profileName,
                    $"Profile bù {profileName} chưa cleanup hoàn tất; chặn mở profile bù khác.",
                    ex);
            }
        }

        _log.Warn($"[AUTO_REPLACE_FAILED_CLEANUP_BEGIN] profile={profileName} source={source}");
        await CloseFailedReplacementRuntimeAsync(ctx);
    }

    async Task WaitForReplacementCleanupBarrierAsync(
        AutoReplacementCleanupBarrierException barrier,
        AutoReplacementRequest request)
    {
        var profileName = (barrier.ProfileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        SetAutoReplacementUiPhase(
            "DỌN PRF BÙ LỖI",
            profileName,
            request.Id);

        // Không profile bù lỗi nào được phép khóa GLOBAL queue vô hạn. Thử cleanup
        // tối đa 4 lượt (~60s chưa tính thời gian probe). Nếu vẫn UNKNOWN, cô lập
        // profile lỗi bằng cooldown và trả quyền điều khiển cho queue để các suất bù
        // khác còn được xử lý.
        for (var attempt = 1;
             attempt <= AutoReplacementCleanupBarrierMaxAttempts
             && !_closing
             && !IsDisposed
             && !Disposing;
             attempt++)
        {
            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=before_wait");
                return;
            }

            _log.Warn(
                $"[AUTO_REPLACE_CLEANUP_BARRIER_WAIT] blockedProfile={profileName} request={request.Id} " +
                $"attempt={attempt}/{AutoReplacementCleanupBarrierMaxAttempts} retryIn={AutoReplacementCleanupBarrierRetrySeconds}s");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                replacementProfile: profileName,
                result: "CHỜ DỌN PROFILE BÙ LỖI",
                detail:
                    $"Profile bù {profileName} chưa đóng sạch; thử dọn lại " +
                    $"{attempt}/{AutoReplacementCleanupBarrierMaxAttempts} sau {AutoReplacementCleanupBarrierRetrySeconds}s.");

            await Task.Delay(TimeSpan.FromSeconds(AutoReplacementCleanupBarrierRetrySeconds));

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=after_wait");
                return;
            }

            try
            {
                if (_contexts.TryGetValue(profileName, out var ctx))
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=context attempt={attempt}");
                    return;
                }

                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);

                if (_contexts.TryGetValue(profileName, out var refreshedCtx))
                {
                    await CloseFailedReplacementRuntimeAsync(refreshedCtx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=refreshed_context attempt={attempt}");
                    return;
                }

                var profile = catalog.Profiles.FirstOrDefault(x =>
                    x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    _autoReplacementCleanupProfiles.Remove(profileName);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=profile_missing attempt={attempt}");
                    return;
                }

                // Context không còn: chỉ còn khả năng Chrome mồ côi theo ProfilePath.
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    profileName,
                    profile.ProfilePath,
                    respectEmergencyStop: true);

                ClearAutoCloseExpectedRunning(
                    profileName,
                    "auto_replacement_barrier_path_cleanup_done");
                _autoReplacementCleanupProfiles.Remove(profileName);
                _log.Info(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=profile_path attempt={attempt}");
                return;
            }
            catch (OperationCanceledException ex)
                when (IsAutomationHalted
                      || ex.Message.StartsWith("EMERGENCY_STOP_", StringComparison.Ordinal))
            {
                _log.Warn($"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=cleanup detail={ex.Message}");
                return;
            }
            catch (AutoReplacementCleanupBarrierException ex)
            {
                barrier = ex;
            }
            catch (AutoCloseCleanupPendingException ex)
            {
                barrier = new AutoReplacementCleanupBarrierException(
                    profileName,
                    $"Profile bù {profileName} vẫn chưa cleanup hoàn tất.",
                    ex);
            }
            catch (Exception ex)
            {
                barrier = new AutoReplacementCleanupBarrierException(
                    profileName,
                    $"Profile bù {profileName} cleanup retry lỗi.",
                    ex);
            }
        }

        if (_closing || IsDisposed || Disposing)
            return;

        // Safety valve: lỗi cleanup của MỘT profile không được làm chết cả hệ thống
        // Tự bù. Profile đó vào cooldown; request hiện tại đã được ScheduleRetry ở
        // caller nên queue sẽ quay lại sau, trong khi các request khác được tiếp tục.
        // Chỉ tại đây mới giải phóng reservation CLEANUP sau khi đã thử đủ barrier.
        _autoReplacementCleanupProfiles.Remove(profileName);
        ClearAutoCloseExpectedRunning(profileName, "cleanup_barrier_exhausted_quarantine");
        MarkReplacementProfileFailed(profileName, "cleanup_barrier_exhausted");

        var finalDetail =
            $"Profile bù {profileName} vẫn chưa xác minh cleanup sau " +
            $"{AutoReplacementCleanupBarrierMaxAttempts} lượt. Đã cô lập profile này vào cooldown; " +
            "không khóa hàng Tự bù vô hạn. Các suất bù khác tiếp tục được xử lý.";

        _log.Error(
            $"[AUTO_REPLACE_CLEANUP_BARRIER_RELEASED] blockedProfile={profileName} request={request.Id} " +
            $"attempts={AutoReplacementCleanupBarrierMaxAttempts} action=QUARANTINE_AND_CONTINUE error={barrier.Message}");

        WriteAutoActivityLog(
            action: "TỰ BÙ",
            profile: request.ClosedProfileName,
            reason: request.Reason,
            replacementProfile: profileName,
            result: "CÔ LẬP PROFILE BÙ LỖI",
            detail: finalDetail);
    }



    static bool IsNameSyncPendingOutcome(AutoProfileProcessOutcome outcome)
    {
        if (!outcome.Step.Equals("RENAME", StringComparison.OrdinalIgnoreCase))
            return false;

        // CAPTCHA/config là lỗi cần xử lý riêng, không phải trường hợp TikTok
        // Save xong nhưng tên cập nhật chậm. COOLDOWN cũng KHÔNG phải name-sync:
        // TikTok đã từ chối thao tác đổi tên nên sweep chỉ-PROBE sẽ không bao giờ
        // tự sửa được. Để COOLDOWN quay về lane reuse thường; Name Guard sẽ thử
        // thao tác tên lại ở một lượt mở sau.
        if (!outcome.Status.StartsWith("PAUSED_RENAME", StringComparison.OrdinalIgnoreCase))
            return false;

        if (outcome.Status.Equals("PAUSED_RENAME_CONFIG", StringComparison.OrdinalIgnoreCase)
            || outcome.Status.Equals("PAUSED_RENAME_COOLDOWN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    static bool IsAutoReplacementRuntimeStabilizationEligible(AutoProfileProcessOutcome outcome)
    {
        if (!outcome.Paused)
            return false;

        // Chỉ grace 10 phút cho lỗi START/Worker/runtime. CAPTCHA, LOGIN, cấu hình
        // hoặc RENAME cần luồng xử lý riêng và không được giữ slot giả 10 phút.
        if (!outcome.Step.Equals("START_TOOL", StringComparison.OrdinalIgnoreCase))
            return false;

        if (outcome.Status.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    async Task<bool> TryCreateReplacementAsync(
        AutoReplacementRequest request,
        int executionGeneration,
        CancellationToken executionToken)
    {
        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();
        // Tự bù chỉ dùng tài khoản CHƯA GÁN và luôn tạo profile MỚI.
        // BuildAutoProfileQueue(requestedNew: 1, resumeIncomplete: false) sẽ lấy
        // tài khoản chưa gán + có mật khẩu theo thứ tự Excel, sau đó ASSIGN ngay
        // để các suất bù khác không thể lấy trùng tài khoản.
        // Dùng cùng gate với cửa sổ "+ Auto Profile" để không có hai luồng
        // đồng thời tranh account / tên profile / Chrome trên VM.
        await _autoProfileQueueGate.WaitAsync(executionToken);

        try
        {
            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return false;

            executionToken.ThrowIfCancellationRequested();
            // Một suất Tự bù cần đạt đúng 1 profile RUNNING khỏe. BAN/lỗi/skip
            // không được làm mất quota như logic cũ giới hạn 3 lần thử.
            // Giữ danh sách account đã thử trong chính suất này để account vừa lỗi
            // nhưng được ReleaseAccount() không bị lấy lại ngay và gây vòng lặp.
            var attemptedAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempt = 0;

            while (!_closing
                   && IsAutoReplacementExecutionAllowed(executionGeneration))
            {
                executionToken.ThrowIfCancellationRequested();

                // NAME_SYNC_PENDING đã được vét ở tầng ngoài TRƯỚC khi vào hàm này.
                // Không recovery xen kẽ lúc đang tạo mới để tránh vừa tạo xong lại mở
                // chính profile đó kiểm tra tên trong cùng một suất bù.
                var startName = DetectNextAutoProfileName();

                var queue = await RunAccountPoolIoAsync(
                    () =>
                    {
                        // Tự bù cũng phải đọc Excel mới nhất trước khi chọn account.
                        // Nếu người dùng vừa note BAN / AutoPrf=DONE thì không lấy lại
                        // account đó chỉ vì catalog JSON vẫn còn snapshot cũ.
                        if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                            _accountPoolService.ReloadCurrentExcel();
                        _accountPoolService.EnsureAutoColumns();

                        return BuildAutoProfileQueue(
                            requestedNew: 1,
                            requestedStartName: startName,
                            resumeIncomplete: false,
                            retryPaused: false);
                    },
                    executionToken);

                // BuildAutoProfileQueue hiện nạp toàn bộ candidate phù hợp. Chọn account
                // đầu tiên chưa được thử trong suất Tự bù hiện tại. Điều này đặc biệt
                // quan trọng khi CREATE_PROFILE lỗi trước khi profile tồn tại và account
                // được trả về trạng thái chưa gán.
                var item = queue.FirstOrDefault(x =>
                    !x.ResumeExisting
                    && !attemptedAccountIds.Contains(x.Account.Id));

                if (item is null)
                {
                    var detail = queue.Count == 0
                        ? "Không còn tài khoản chưa gán có mật khẩu để tạo profile bù."
                        : $"Đã thử hết {attemptedAccountIds.Count} tài khoản phù hợp trong suất bù này; chưa có profile RUNNING khỏe.";

                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_EXHAUSTED] closed={request.ClosedProfileName} attempted={attemptedAccountIds.Count} queue={queue.Count} reason=no_remaining_candidate");

                    WriteAutoActivityLog(
                        action: "TỰ BÙ",
                        profile: request.ClosedProfileName,
                        reason: request.Reason,
                        result: "HẾT KHO / CHỜ",
                        detail: detail);

                    return false;
                }

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_HARD_STOP_BEFORE_NEW_CANDIDATE] closed={request.ClosedProfileName} profile={item.ProfileName} generation={executionGeneration}");
                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                // Chỉ CREATE profile mới mới phải chờ cooldown. Các lane mở PRF đã có
                // (TryUseReusableProfileQueueAsync / TryOpenNextExistingReplacementAsync)
                // không đi qua đây nên không bị chậm bởi 4'/7'/15' của Auto Profile.
                var createCooldownCompleted = await WaitAutoReplacementCreateCooldownBeforeNewProfileAsync(
                    request,
                    item.ProfileName,
                    executionGeneration,
                    executionToken);

                if (!createCooldownCompleted)
                    return false;

                attemptedAccountIds.Add(item.Account.Id);
                attempt++;

                // Đánh dấu TRƯỚC khi mở/tạo runtime. Nếu profile này rơi vào
                // NAME_SYNC_PENDING thì các RETRY của cùng suất không được mở lại.
                MarkAutoReplacementProfileAttempted(
                    request,
                    item.ProfileName,
                    "create_new");

                _autoReplacementClaimedProfiles.Add(item.ProfileName);

                try
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CREATE_BEGIN] closed={request.ClosedProfileName} profile={item.ProfileName} account={item.Account.Username} attempt={attempt} mode=until_success_or_exhausted");

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "BẮT ĐẦU",
                        detail: $"Lần thử {attempt}; tiếp tục đến khi có 1 profile RUNNING khỏe hoặc hết kho.");

                    SetAutoReplacementUiPhase(
                        "TẠO PRF MỚI",
                        item.ProfileName,
                        request.Id);

                    var outcome = await ProcessAutoProfileQueueItemAsync(
                        item,
                        autoRename: true,
                        autoStart: true,
                        isPaused: static () => false,
                        ct: executionToken,
                        ui: (step, result, _) =>
                        {
                            SetAutoReplacementUiPhase(
                                "TẠO PRF MỚI",
                                $"{item.ProfileName} · {step}",
                                request.Id);

                            _log.Info(
                                $"[AUTO_REPLACE_CREATE_PROGRESS] profile={item.ProfileName} step={step} result={result}");
                        });

                    if (IsManualCloseSuppressed(item.ProfileName))
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=after_process");
                        await CleanupCreatedReplacementAttemptAsync(
                            item.ProfileName,
                            "manual_close_after_process");
                        return false;
                    }

                    if (outcome.Success)
                    {
                        if (!_contexts.TryGetValue(item.ProfileName, out var createdCtx))
                        {
                            try
                            {
                                var catalog = _profileService.Load();
                                RefreshContextsFromCatalog(catalog);
                                _contexts.TryGetValue(item.ProfileName, out createdCtx);
                            }
                            catch { }
                        }

                        SetAutoReplacementUiPhase(
                            "CHỜ PRF MỚI ỔN ĐỊNH",
                            item.ProfileName,
                            request.Id);

                        var healthy = createdCtx is not null
                            && await WaitForReplacementHealthyRunningAsync(
                                createdCtx,
                                request,
                                "created",
                                executionGeneration,
                                executionToken);

                        if (!healthy)
                        {
                            if (IsManualCloseSuppressed(item.ProfileName))
                            {
                                _log.Warn(
                                    $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=healthy_wait");
                                await CleanupCreatedReplacementAttemptAsync(
                                    item.ProfileName,
                                    "manual_close_during_healthy_wait");
                                return false;
                            }

                            MarkReplacementProfileFailed(item.ProfileName, "created_started_but_not_healthy");

                            _log.Warn(
                                $"[AUTO_REPLACE_CREATE_FAIL] profile={item.ProfileName} step=confirm_running");

                            WriteAutoActivityLog(
                                action: "MỞ PROFILE BÙ",
                                profile: request.ClosedProfileName,
                                account: item.Account.Username,
                                reason: request.Reason,
                                replacementProfile: item.ProfileName,
                                result: "LỖI",
                                detail: "Profile đã tạo/Start nhưng không xác nhận RUNNING khỏe trong thời gian quy định.");

                            await CleanupCreatedReplacementAttemptAsync(
                                item.ProfileName,
                                "created_started_but_not_healthy");

                            RegisterAutoReplacementCreateCooldown(
                                outcome,
                                item.ProfileName,
                                request.Id,
                                "created_started_but_not_healthy");
                            continue;
                        }

                        // Profile tạo mới chỉ được coi là ĐÃ TREO sau khi RUNNING khỏe.
                        MarkProfileSupplyState(item.ProfileName, "used", "auto_replacement_created_running_confirmed");

                        _log.Info(
                            $"[AUTO_REPLACE_CREATE_OK] closed={request.ClosedProfileName} replacement={item.ProfileName} account={item.Account.Username} confirmed=healthy_running");

                        WriteAutoActivityLog(
                            action: "MỞ PROFILE BÙ",
                            profile: request.ClosedProfileName,
                            account: item.Account.Username,
                            reason: request.Reason,
                            replacementProfile: item.ProfileName,
                            result: "THÀNH CÔNG",
                            detail: $"Profile {item.ProfileName} đã RUNNING khỏe 30 giây.");

                        // Dù suất hiện tại đã đủ, lần CREATE mới kế tiếp (nếu còn thiếu
                        // target khác) vẫn phải nghỉ đúng "Giữa PRF" như + Auto Profile.
                        RegisterAutoReplacementCreateCooldown(
                            outcome,
                            item.ProfileName,
                            request.Id,
                            "created_healthy_success");
                        return true;
                    }

                    if (outcome.Skipped)
                    {
                        // Skipped có 2 nghĩa:
                        // 1) SKIPPED_EXCEL/ACTIVE_GUARD xảy ra trước khi mở runtime => chỉ bỏ qua.
                        // 2) Paused=true là LOGIN/CAPTCHA/2FA/Worker/START... lỗi sau khi profile
                        //    đã có thể mở Chrome + Worker. Với Tự thay phải đóng runtime này trước
                        //    khi thử account tiếp theo, NHƯNG KHÔNG xóa/retire profile non-BAN.
                        if (!outcome.Paused)
                        {
                            _log.Info(
                                $"[AUTO_REPLACE_CREATE_SKIP_EXCEL] profile={item.ProfileName} account={item.Account.Username} status={outcome.Status} step={outcome.Step} note={outcome.Note}");

                            WriteAutoActivityLog(
                                action: "MỞ PROFILE BÙ",
                                profile: request.ClosedProfileName,
                                account: item.Account.Username,
                                reason: request.Reason,
                                replacementProfile: item.ProfileName,
                                result: "BỎ QUA",
                                detail: outcome.Note);

                            // Excel vừa thay đổi sau lúc dựng queue (BAN/DONE/đổi mapping).
                            // Không coi đây là lỗi tài khoản; thử lấy ứng viên mới ở vòng kế tiếp.
                            continue;
                        }

                        _log.Warn(
                            $"[AUTO_REPLACE_CREATE_NONBAN_FAIL] profile={item.ProfileName} account={item.Account.Username} status={outcome.Status} step={outcome.Step} action=close_runtime_keep_profile");

                        WriteAutoActivityLog(
                            action: "MỞ PROFILE BÙ",
                            profile: request.ClosedProfileName,
                            account: item.Account.Username,
                            reason: request.Reason,
                            replacementProfile: item.ProfileName,
                            result: "LỖI - ĐÓNG KHÔNG XÓA",
                            detail: $"status={outcome.Status}; step={outcome.Step}; đóng Chrome + Worker, giữ nguyên profile để có thể xử lý/resume sau.");

                        await CleanupCreatedReplacementAttemptAsync(
                            item.ProfileName,
                            $"nonban_paused:{outcome.Status}:{outcome.Step}");

                        // TikTok đôi lúc đã nhận Save tên nhưng trang Hồ sơ chưa phản ánh ngay.
                        // Giữ PRF trong CHÍNH "Chờ dùng lại" với lane NAME_SYNC_PENDING;
                        // lượt bù bình thường vẫn tiếp tục account mới, recovery chỉ vét sau.
                        if (IsNameSyncPendingOutcome(outcome))
                        {
                            QueueReusableProfileNameSyncPending(
                                item,
                                $"status={outcome.Status}; step={outcome.Step}; note={outcome.Note}");
                        }

                        // CỐ Ý KHÔNG gọi QueueAutoDeleteRetiredProfileAfterExcelNote ở đây.
                        // Profile/account lỗi non-BAN được giữ lại; chỉ runtime bị đóng.
                        RegisterAutoReplacementCreateCooldown(
                            outcome,
                            item.ProfileName,
                            request.Id,
                            "paused_nonban");
                        continue;
                    }

                    if (IsAutoReplacementRuntimeStabilizationEligible(outcome))
                    {
                        if (!_contexts.TryGetValue(item.ProfileName, out var stabilizeCtx))
                        {
                            try
                            {
                                var catalog = _profileService.Load();
                                RefreshContextsFromCatalog(catalog);
                                _contexts.TryGetValue(item.ProfileName, out stabilizeCtx);
                            }
                            catch { }
                        }

                        if (stabilizeCtx is not null)
                        {
                            _log.Warn(
                                $"[AUTO_REPLACE_CREATE_START_GRACE] profile={item.ProfileName} status={outcome.Status} " +
                                $"step={outcome.Step} action=STABILIZE_10M");

                            var stabilization = await StabilizeReplacementRuntimeAsync(
                                stabilizeCtx,
                                request,
                                "created_start_recovery",
                                executionGeneration,
                                executionToken);

                            if (IsManualCloseSuppressed(item.ProfileName))
                            {
                                _log.Warn(
                                    $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=stabilize");
                                await CleanupCreatedReplacementAttemptAsync(
                                    item.ProfileName,
                                    "manual_close_during_stabilize");
                                return false;
                            }

                            if (stabilization.NameSyncPending)
                            {
                                await CleanupCreatedReplacementAttemptAsync(
                                    item.ProfileName,
                                    "created_start_recovery_name_sync_pending");
                                QueueReusableProfileNameSyncPending(
                                    item,
                                    "created_start_recovery:name_sync_pending");
                                RegisterAutoReplacementCreateCooldown(
                                    outcome,
                                    item.ProfileName,
                                    request.Id,
                                    "stabilize_name_sync_pending");
                                continue;
                            }

                            if (stabilization.Healthy)
                            {
                                try
                                {
                                    await RunAccountPoolIoAsync(
                                        () => _accountPoolService.SetAutoProfileResult(item.Account.Id, "DONE"),
                                        CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _log.Warn(
                                        $"[AUTO_REPLACE_CREATE_RECOVERY_DONE_WRITE_WARN] profile={item.ProfileName} error={ex.Message}");
                                }

                                MarkProfileSupplyState(
                                    item.ProfileName,
                                    "used",
                                    "auto_replacement_created_recovered_10m");

                                WriteAutoActivityLog(
                                    action: "MỞ PROFILE BÙ",
                                    profile: request.ClosedProfileName,
                                    account: item.Account.Username,
                                    reason: request.Reason,
                                    replacementProfile: item.ProfileName,
                                    result: "THÀNH CÔNG",
                                    detail: "Profile gặp lỗi START ban đầu nhưng đã tự phục hồi trong cửa sổ ổn định 10 phút.");

                                RegisterAutoReplacementCreateCooldown(
                                    outcome,
                                    item.ProfileName,
                                    request.Id,
                                    "stabilize_recovered_success");
                                return true;
                            }

                            _log.Warn(
                                $"[AUTO_REPLACE_CREATE_START_GRACE_EXPIRED] profile={item.ProfileName} hard={stabilization.HardFailed} detail={stabilization.Detail}");
                        }
                    }

                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_FAIL] profile={item.ProfileName} status={outcome.Status} step={outcome.Step} note={outcome.Note}");

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "LỖI",
                        detail: $"status={outcome.Status}; step={outcome.Step}; note={outcome.Note}");

                    // FIX: ProcessAutoProfileQueueItemAsync có thể thất bại SAU khi đã mở
                    // Worker/Chrome (LOGIN/RENAME/START...). Trước đây nhánh này không dọn
                    // runtime, rồi lập tức thử profile khác => Chrome/tab tích tụ 5 -> 9...
                    // Phải cleanup + verify xong mới được chuyển ứng viên.
                    await CleanupCreatedReplacementAttemptAsync(
                        item.ProfileName,
                        $"outcome_fail:{outcome.Status}:{outcome.Step}");

                    if (IsNameSyncPendingOutcome(outcome))
                    {
                        QueueReusableProfileNameSyncPending(
                            item,
                            $"status={outcome.Status}; step={outcome.Step}; note={outcome.Note}");
                    }

                    // LOGIN_BANNED đã được note=ban + retire trong Auto Profile.
                    // Chỉ sau khi cleanup candidate hoàn tất mới cho Tự xóa BAN chạy,
                    // tránh xóa catalog/folder song song với cleanup của Tự bù.
                    if (outcome.Status.Equals("LOGIN_BANNED", StringComparison.OrdinalIgnoreCase))
                    {
                        QueueAutoDeleteRetiredProfileAfterExcelNote(
                            item.ProfileName,
                            "BAN");
                    }

                    // Cùng đúng bộ phân loại cooldown của + Auto Profile:
                    // login lỗi -> LoginError; BAN / lỗi login lần 3 -> Protection;
                    // RENAME/START/healthy fail thông thường -> Normal.
                    RegisterAutoReplacementCreateCooldown(
                        outcome,
                        item.ProfileName,
                        request.Id,
                        "outcome_fail");
                }
                catch (OperationCanceledException)
                    when (executionToken.IsCancellationRequested
                          || !IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_HARD_STOP] closed={request.ClosedProfileName} profile={item.ProfileName} generation={executionGeneration}");

                    // Dừng khẩn cấp = đóng băng hiện trạng: không đóng candidate
                    // đang mở. Các hard-stop khác vẫn cleanup như logic cũ.
                    if (!IsAutomationHalted)
                    {
                        try
                        {
                            await CleanupCreatedReplacementAttemptAsync(
                                item.ProfileName,
                                "manual_hard_stop");
                        }
                        catch (Exception cleanupEx)
                        {
                            _log.Warn(
                                $"[AUTO_REPLACE_CREATE_HARD_STOP_CLEANUP_WARN] profile={item.ProfileName} error={cleanupEx.Message}");
                        }
                    }
                    else
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_CREATE_EMERGENCY_PRESERVE] profile={item.ProfileName} action=leave_runtime_as_is");
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_ERROR] profile={item.ProfileName} error={ex.Message}");

                    if (ex is AutoReplacementCleanupBarrierException)
                        throw;

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "LỖI",
                        detail: ex.Message);

                    // Exception cũng có thể xảy ra sau khi Chrome đã mở. Không được
                    // nuốt lỗi rồi bỏ claim vì như vậy queue sẽ mở thêm profile mới.
                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            item.ProfileName,
                            "exception:" + ex.GetType().Name);
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Error(
                            $"[AUTO_REPLACE_CREATE_CLEANUP_ERROR] profile={item.ProfileName} error={cleanupEx}");
                        throw new InvalidOperationException(
                            $"Profile bù {item.ProfileName} lỗi và cleanup chưa hoàn tất; chặn mở profile kế tiếp.",
                            cleanupEx);
                    }

                    // Exception ngoài outcome vẫn là một lần CREATE thật đã được thử.
                    // Không có tín hiệu login/BAN đáng tin => dùng cooldown Normal.
                    RegisterAutoReplacementCreateCooldown(
                        null,
                        item.ProfileName,
                        request.Id,
                        "exception:" + ex.GetType().Name);
                }
                finally
                {
                    _autoReplacementClaimedProfiles.Remove(item.ProfileName);
                }
            }

            return false;
        }
        finally
        {
            _autoProfileQueueGate.Release();
        }
    }

    async Task<AutoReplacementStabilizationResult> StabilizeReplacementRuntimeAsync(
        ProfileContext ctx,
        AutoReplacementRequest request,
        string source,
        int executionGeneration,
        CancellationToken executionToken)
    {
        var isReusableProfileOpen = source.Equals(
            "reuse_queue",
            StringComparison.OrdinalIgnoreCase);

        var healthyConfirmTimeoutSeconds = isReusableProfileOpen
            ? AutoReplacementReusableHealthyConfirmTimeoutSeconds
            : AutoReplacementHealthyConfirmTimeoutSeconds;
        var healthyStableSeconds = isReusableProfileOpen
            ? AutoReplacementReusableHealthyStableSeconds
            : AutoReplacementHealthyStableSeconds;
        var recoveryInterval = isReusableProfileOpen
            ? AutoReplacementReusableRecoveryInterval
            : AutoReplacementStabilizationRecoveryInterval;
        var startCommandTimeout = TimeSpan.FromSeconds(
            isReusableProfileOpen
                ? AutoReplacementReusableStartCommandTimeoutSeconds
                : 100);

        var deadlineUtc = DateTime.UtcNow.AddSeconds(healthyConfirmTimeoutSeconds);
        var nextRecoveryUtc = DateTime.MinValue;
        DateTime? healthySinceUtc = null;
        string lastFault = "";
        var recoveryAttempt = 0;

        // PRF vừa tạo/login vẫn có grace dài như cũ. Riêng PRF Chờ dùng lại
        // đã tồn tại từ trước chỉ được cửa sổ ngắn để mở Worker/Chrome + RUNNING;
        // nếu không lên được thì nhả candidate và thử PRF khác.
        MarkAutoCloseExpectedRunning(
            ctx.Profile.Name,
            "auto_replace_stabilizing:" + source);

        _log.Info(
            $"[AUTO_REPLACE_STABILIZE_BEGIN] id={request.Id} profile={ctx.Profile.Name} source={source} " +
            $"policy={(isReusableProfileOpen ? "REUSE_FAST" : "CREATED_GRACE")} " +
            $"stable={healthyStableSeconds}s grace={healthyConfirmTimeoutSeconds}s");

        while (!_closing && DateTime.UtcNow < deadlineUtc)
        {
            // Candidate có thể bị BAN/TIME trong chính cửa sổ ổn định 10 phút.
            // Không được recovery/reopen nó nữa, đặc biệt khi job xóa đã được arm.
            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                ClearAutoCloseExpectedRunning(
                    ctx.Profile.Name,
                    "auto_replace_retire_delete_during_stabilize");

                _log.Warn(
                    $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source}");

                return new AutoReplacementStabilizationResult(
                    false, false, false,
                    "RETIRE_DELETE_BLOCKED: profile đang Tự đóng/Tự xóa hoặc đã hard-retired.");
            }

            if (IsManualCloseSuppressed(ctx.Profile.Name))
            {
                ClearAutoCloseExpectedRunning(
                    ctx.Profile.Name,
                    "auto_replace_manual_close_during_stabilize");

                _log.Warn(
                    $"[AUTO_REPLACE_STABILIZE_MANUAL_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source}");

                return new AutoReplacementStabilizationResult(
                    false, false, true,
                    "MANUAL_CLOSE: user đã chủ động đóng profile trong lúc ổn định.");
            }

            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                throw new OperationCanceledException(executionToken);

            executionToken.ThrowIfCancellationRequested();

            var pollOk = false;
            try
            {
                await RefreshStatusAsync(ctx);
                pollOk = true;
            }
            catch (Exception ex)
            {
                lastFault = "status_poll:" + ex.Message;
            }

            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                ClearAutoCloseExpectedRunning(
                    ctx.Profile.Name,
                    "auto_replace_retire_delete_after_poll");

                _log.Warn(
                    $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_poll");

                return new AutoReplacementStabilizationResult(
                    false, false, false,
                    "RETIRE_DELETE_BLOCKED: profile chuyển sang Tự đóng/Tự xóa trong lúc kiểm tra trạng thái.");
            }

            var nowUtc = DateTime.UtcNow;
            var state = GetEffectiveRuntimeState(ctx);
            var healthy = pollOk && IsAutoCloseHealthyRunning(ctx, state, nowUtc);

            if (healthy)
            {
                healthySinceUtc ??= nowUtc;
                var stableFor = nowUtc - healthySinceUtc.Value;
                if (stableFor >= TimeSpan.FromSeconds(healthyStableSeconds))
                {
                    _log.Info(
                        $"[AUTO_REPLACE_STABILIZE_OK] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                        $"stable={stableFor:c} recoveryAttempts={recoveryAttempt}");
                    return new AutoReplacementStabilizationResult(
                        true, false, false,
                        $"RUNNING khỏe {stableFor:c}.");
                }
            }
            else
            {
                healthySinceUtc = null;
                var described = DescribeAutoCloseRuntimeFault(ctx, state, nowUtc);
                if (!string.IsNullOrWhiteSpace(described))
                    lastFault = described;
            }

            // Chỉ recovery CHÍNH profile này; không tạo profile mới trong lúc claim.
            // PRF reuse dùng nhịp recovery ngắn; PRF vừa tạo/login vẫn giữ nhịp dài
            // như cũ. Không spam START khi runtime đang RUNNING/RECOVERING.
            if (!healthy && nowUtc >= nextRecoveryUtc)
            {
                nextRecoveryUtc = nowUtc.Add(recoveryInterval);
                recoveryAttempt++;

                var workerAlive = IsNameGuardWorkerAlive(ctx);
                var chromeConnected = string.Equals(
                    ctx.LastSnapshot?.Chrome,
                    "CONNECTED",
                    StringComparison.OrdinalIgnoreCase);

                _log.Warn(
                    $"[AUTO_REPLACE_STABILIZE_RECOVERY] id={request.Id} profile={ctx.Profile.Name} " +
                    $"source={source} attempt={recoveryAttempt} state={state} workerAlive={workerAlive} " +
                    $"chromeConnected={chromeConnected} fault={lastFault}");

                if (!workerAlive)
                {
                    try
                    {
                        await OpenProfileAsync(
                            ctx,
                            isReusableProfileOpen
                                ? $"Đang mở lại profile chờ {ctx.Profile.Name}..."
                                : $"Đang chờ profile {ctx.Profile.Name} ổn định (tối đa 10 phút)...");
                    }
                    catch (Exception ex)
                    {
                        lastFault = "reopen_worker:" + ex.Message;
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_REOPEN_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                    }
                }

                try { await RefreshStatusAsync(ctx); } catch { }
                state = GetEffectiveRuntimeState(ctx);
                workerAlive = IsNameGuardWorkerAlive(ctx);
                chromeConnected = string.Equals(
                    ctx.LastSnapshot?.Chrome,
                    "CONNECTED",
                    StringComparison.OrdinalIgnoreCase);

                if (workerAlive && !chromeConnected)
                {
                    try
                    {
                        await OpenChromeForProfileAsync(ctx);
                        try { await RefreshStatusAsync(ctx); } catch { }
                    }
                    catch (Exception ex)
                    {
                        lastFault = "reopen_chrome:" + ex.Message;
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_CHROME_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                    }

                    state = GetEffectiveRuntimeState(ctx);
                    chromeConnected = string.Equals(
                        ctx.LastSnapshot?.Chrome,
                        "CONNECTED",
                        StringComparison.OrdinalIgnoreCase);
                }

                if (workerAlive
                    && state is not (RuntimeStateRunning or RuntimeStateRecovering))
                {
                    try
                    {
                        var reply = await StartWithNameGuardAsync(
                            ctx,
                            "start_auto",
                            startCommandTimeout,
                            suppressStatus: true);

                        if (IsNameGuardNameSyncPendingStartReply(reply))
                        {
                            ClearAutoCloseExpectedRunning(
                                ctx.Profile.Name,
                                "auto_replace_name_sync_pending");

                            _log.Warn(
                                $"[AUTO_REPLACE_STABILIZE_DEFER_NAME_SYNC] id={request.Id} profile={ctx.Profile.Name} source={source}");

                            return new AutoReplacementStabilizationResult(
                                false, true, false,
                                "Tên đã Save nhưng TikTok chưa đồng bộ; defer profile sang lượt bù khác.");
                        }

                        if (string.Equals(reply, "name_guard_blocked", StringComparison.OrdinalIgnoreCase))
                        {
                            ClearAutoCloseExpectedRunning(
                                ctx.Profile.Name,
                                "auto_replace_name_guard_hard_block");

                            return new AutoReplacementStabilizationResult(
                                false, false, true,
                                "Name Guard block cứng; không tiếp tục grace runtime.");
                        }

                        _log.Info(
                            $"[AUTO_REPLACE_STABILIZE_START_REPLY] id={request.Id} profile={ctx.Profile.Name} source={source} reply={reply}");
                    }
                    catch (Exception ex)
                    {
                        lastFault = "recovery_start:" + ex.Message;
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_START_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), executionToken);
        }

        _log.Warn(
            $"[AUTO_REPLACE_STABILIZE_TIMEOUT] id={request.Id} profile={ctx.Profile.Name} source={source} " +
            $"policy={(isReusableProfileOpen ? "REUSE_FAST" : "CREATED_GRACE")} " +
            $"grace={healthyConfirmTimeoutSeconds}s fault={lastFault}");

        var timeoutText = isReusableProfileOpen
            ? $"{healthyConfirmTimeoutSeconds} giây"
            : $"{healthyConfirmTimeoutSeconds / 60} phút";

        return new AutoReplacementStabilizationResult(
            false, false, false,
            $"Không RUNNING khỏe sau {timeoutText}. fault={lastFault}");
    }

    async Task<bool> WaitForReplacementHealthyRunningAsync(
        ProfileContext ctx,
        AutoReplacementRequest request,
        string source,
        int executionGeneration,
        CancellationToken executionToken)
    {
        var result = await StabilizeReplacementRuntimeAsync(
            ctx, request, source, executionGeneration, executionToken);
        return result.Healthy;
    }

    async Task<bool> WaitForReplacementProbeReadyAsync(
        ProfileContext ctx,
        AutoReplacementRequest request,
        string source,
        int executionGeneration,
        CancellationToken executionToken)
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(AutoReplacementHealthyConfirmTimeoutSeconds);
        var nextRecoveryUtc = DateTime.MinValue;
        var recoveryAttempt = 0;
        string lastFault = "";

        _log.Info(
            $"[AUTO_REPLACE_PROBE_GRACE_BEGIN] id={request.Id} profile={ctx.Profile.Name} source={source} " +
            $"grace={AutoReplacementHealthyConfirmTimeoutSeconds}s");

        while (!_closing && DateTime.UtcNow < deadlineUtc)
        {
            if (IsManualCloseSuppressed(ctx.Profile.Name))
            {
                _log.Warn(
                    $"[AUTO_REPLACE_PROBE_GRACE_MANUAL_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source}");
                return false;
            }

            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                throw new OperationCanceledException(executionToken);

            executionToken.ThrowIfCancellationRequested();

            try { await RefreshStatusAsync(ctx); } catch (Exception ex) { lastFault = ex.Message; }

            var workerAlive = IsNameGuardWorkerAlive(ctx);
            var chromeConnected = string.Equals(
                ctx.LastSnapshot?.Chrome,
                "CONNECTED",
                StringComparison.OrdinalIgnoreCase);

            if (workerAlive && chromeConnected)
            {
                _log.Info(
                    $"[AUTO_REPLACE_PROBE_GRACE_READY] id={request.Id} profile={ctx.Profile.Name} source={source} attempts={recoveryAttempt}");
                return true;
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc >= nextRecoveryUtc)
            {
                nextRecoveryUtc = nowUtc.Add(AutoReplacementStabilizationRecoveryInterval);
                recoveryAttempt++;

                _log.Warn(
                    $"[AUTO_REPLACE_PROBE_GRACE_RECOVERY] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                    $"attempt={recoveryAttempt} workerAlive={workerAlive} chromeConnected={chromeConnected} fault={lastFault}");

                if (!workerAlive)
                {
                    try
                    {
                        await OpenProfileAsync(
                            ctx,
                            $"Đang chờ Worker profile {ctx.Profile.Name} ổn định...");
                    }
                    catch (Exception ex)
                    {
                        lastFault = "open_worker:" + ex.Message;
                    }
                }

                try { await RefreshStatusAsync(ctx); } catch { }
                workerAlive = IsNameGuardWorkerAlive(ctx);
                chromeConnected = string.Equals(
                    ctx.LastSnapshot?.Chrome,
                    "CONNECTED",
                    StringComparison.OrdinalIgnoreCase);

                if (workerAlive && !chromeConnected)
                {
                    try
                    {
                        await OpenChromeForProfileAsync(ctx);
                        try { await RefreshStatusAsync(ctx); } catch { }
                    }
                    catch (Exception ex)
                    {
                        lastFault = "open_chrome:" + ex.Message;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), executionToken);
        }

        _log.Warn(
            $"[AUTO_REPLACE_PROBE_GRACE_TIMEOUT] id={request.Id} profile={ctx.Profile.Name} source={source} " +
            $"grace={AutoReplacementHealthyConfirmTimeoutSeconds}s fault={lastFault}");
        return false;
    }

    bool IsReplacementProfileCoolingDown(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        if (!_autoReplacementFailedProfileRetryUtc.TryGetValue(profileName, out var retryUtc))
            return false;

        if (DateTime.UtcNow < retryUtc)
            return true;

        _autoReplacementFailedProfileRetryUtc.Remove(profileName);
        return false;
    }

    void MarkReplacementProfileFailed(string profileName, string reason)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var retryUtc = DateTime.UtcNow.Add(AutoReplacementFailedProfileCooldown);
        _autoReplacementFailedProfileRetryUtc[profileName] = retryUtc;

        _log.Warn(
            $"[AUTO_REPLACE_PROFILE_COOLDOWN] profile={profileName} retry={retryUtc:O} reason={reason}");
    }

    bool HasAutoReplacementProfileBeenAttempted(
        AutoReplacementRequest request,
        string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            var attempted = live?.AttemptedProfiles ?? request.AttemptedProfiles;
            attempted ??= new List<string>();

            return attempted.Any(x =>
                string.Equals(
                    (x ?? "").Trim(),
                    profileName,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    int GetAutoReplacementAttemptedProfileCount(AutoReplacementRequest request)
    {
        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            return (live?.AttemptedProfiles ?? request.AttemptedProfiles ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
    }

    void MarkAutoReplacementProfileAttempted(
        AutoReplacementRequest request,
        string profileName,
        string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var added = false;

        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            var target = live ?? request;
            target.AttemptedProfiles ??= new List<string>();

            if (!target.AttemptedProfiles.Any(x =>
                    string.Equals(
                        (x ?? "").Trim(),
                        profileName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                target.AttemptedProfiles.Add(profileName);
                added = true;

                if (live is not null)
                    SaveAutoReplacementQueueUnsafe();
            }
        }

        if (added)
        {
            _log.Info(
                $"[AUTO_REPLACE_PROFILE_ATTEMPT_ONCE] id={request.Id} closed={request.ClosedProfileName} profile={profileName} source={source} attemptedCount={GetAutoReplacementAttemptedProfileCount(request)}");
        }
    }

    bool SuppressAutoReplacementRequestIfTargetSatisfied(
        AutoReplacementRequest request,
        string source)
    {
        var gate = EvaluateAutoReplacementFixedSlotGate(request);
        if (!gate.AlreadySatisfied)
            return false;

        RemoveAutoReplacementRequest(request.Id);

        _log.Warn(
            $"[AUTO_REPLACE_DYNAMIC_TARGET_SUPPRESSED] id={request.Id} closed={request.ClosedProfileName} source={source} target={gate.TargetSlots} occupied={gate.OccupiedSlots}");

        WriteAutoActivityLog(
            action: "SUẤT BÙ",
            profile: request.ClosedProfileName,
            reason: request.Reason,
            result: "BỎ QUA - TARGET ĐÃ GIẢM",
            detail: $"source={source}; target={gate.TargetSlots}; occupied={gate.OccupiedSlots}. Manual close/target change đã làm suất này không còn cần thiết.");

        return true;
    }

    int GetAutoReplacementPendingCount()
    {
        lock (_autoReplacementQueueLock)
            return _autoReplacementQueue.Count;
    }

    void RemoveAutoReplacementRequest(string requestId)
    {
        lock (_autoReplacementQueueLock)
        {
            _autoReplacementQueue.RemoveAll(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            SaveAutoReplacementQueueUnsafe();
        }
    }

    void ScheduleAutoReplacementReuseOnlyRoundRetry(string requestId, string lastError)
    {
        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.AttemptCount++;
            request.LastError = (lastError ?? "").Trim();

            // Một VÒNG: mỗi profile tối đa 1 lần. Hết vòng thì nghỉ đủ lâu cho TikTok
            // đồng bộ tên rồi mới cho phép các profile cũ tham gia vòng kế tiếp.
            request.AttemptedProfiles ??= new List<string>();
            var previousAttempted = request.AttemptedProfiles.Count;
            request.AttemptedProfiles.Clear();

            var delay = TimeSpan.FromMinutes(5);
            request.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            SaveAutoReplacementQueueUnsafe();

            _log.Warn(
                $"[AUTO_REPLACE_REUSE_ONLY_NEW_ROUND] id={request.Id} closed={request.ClosedProfileName} " +
                $"round={request.AttemptCount} clearedAttempted={previousAttempted} retryIn={delay:c} next={request.NextAttemptUtc:O}");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                result: "CHỜ PRF",
                detail: $"Chỉ dùng PRF chờ; đã hết một vòng ({previousAttempted} PRF). Nghỉ 5 phút rồi quét lại, không tạo PRF mới.");
        }
    }

    bool TryGetAutoReplacementCreateCooldown(
        AutoReplacementRequest request,
        out TimeSpan wait)
    {
        wait = TimeSpan.Zero;

        DateTime? requestDeadlineUtc;
        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            requestDeadlineUtc = live?.CreateNotBeforeUtc ?? request.CreateNotBeforeUtc;
        }

        DateTime sharedDeadlineUtc;
        lock (_autoReplacementCreateCooldownLock)
            sharedDeadlineUtc = _autoReplacementCreateNotBeforeUtc;

        var effectiveDeadlineUtc = requestDeadlineUtc ?? DateTime.MinValue;
        if (sharedDeadlineUtc > effectiveDeadlineUtc)
            effectiveDeadlineUtc = sharedDeadlineUtc;

        if (effectiveDeadlineUtc == DateTime.MinValue)
            return false;

        wait = effectiveDeadlineUtc - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
            return false;
        }

        return true;
    }

    void ScheduleAutoReplacementOperationalRetry(
        string requestId,
        string lastError,
        TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
            delay = TimeSpan.FromSeconds(1);

        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.LastError = (lastError ?? "").Trim();
            request.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            SaveAutoReplacementQueueUnsafe();

            _log.Info(
                $"[AUTO_REPLACE_OPERATIONAL_RETRY] id={request.Id} closed={request.ClosedProfileName} retryIn={delay:c} next={request.NextAttemptUtc:O} error={request.LastError}");
        }
    }

    void ScheduleAutoReplacementAtCreateDeadline(
        string requestId,
        string lastError)
    {
        DateTime sharedDeadlineUtc;
        lock (_autoReplacementCreateCooldownLock)
            sharedDeadlineUtc = _autoReplacementCreateNotBeforeUtc;

        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.LastError = (lastError ?? "").Trim();
            var now = DateTime.UtcNow;
            var deadline = request.CreateNotBeforeUtc ?? now;
            if (sharedDeadlineUtc > deadline)
                deadline = sharedDeadlineUtc;

            // Ghi deadline dùng chung vào request để WakeAutoReplacementForReusableSupply
            // vẫn có thể đánh thức request này nếu PRF chờ xuất hiện trong lúc CREATE bị khóa.
            request.CreateNotBeforeUtc = deadline;
            request.NextAttemptUtc = deadline > now ? deadline : now;
            SaveAutoReplacementQueueUnsafe();

            _log.Info(
                $"[AUTO_REPLACE_WAIT_CREATE_DEADLINE] id={request.Id} closed={request.ClosedProfileName} createAt={request.CreateNotBeforeUtc:O} next={request.NextAttemptUtc:O}");
        }
    }

    void ScheduleAutoReplacementCreateRetry(string requestId, string lastError)
    {
        DateTime sharedCreateDeadlineUtc;
        lock (_autoReplacementCreateCooldownLock)
            sharedCreateDeadlineUtc = _autoReplacementCreateNotBeforeUtc;

        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.AttemptCount++;
            request.LastError = (lastError ?? "").Trim();

            var now = DateTime.UtcNow;
            TimeSpan delay;
            var source = "fallback_backoff";

            if (sharedCreateDeadlineUtc > now)
            {
                // TryCreateReplacementAsync vừa thực sự thử CREATE một PRF và đã
                // đặt cooldown theo đúng cấu hình + Auto Profile (Normal/Login/Bảo vệ).
                // Không chồng thêm backoff 2/3/5 phút vì như vậy sẽ làm sai mốc user đặt.
                request.CreateNotBeforeUtc = sharedCreateDeadlineUtc;
                delay = sharedCreateDeadlineUtc - now;
                source = "auto_profile_cooldown";
            }
            else
            {
                // Không có CREATE thật ngay trước đó (ví dụ kho account đang rỗng):
                // giữ backoff vận hành cũ để không quét Excel liên tục.
                delay = request.AttemptCount switch
                {
                    <= 1 => TimeSpan.FromMinutes(2),
                    2 => TimeSpan.FromMinutes(3),
                    _ => TimeSpan.FromMinutes(5)
                };
                request.CreateNotBeforeUtc = now.Add(delay);
            }

            request.NextAttemptUtc = request.CreateNotBeforeUtc.Value;
            SaveAutoReplacementQueueUnsafe();

            _log.Warn(
                $"[AUTO_REPLACE_CREATE_RETRY] id={request.Id} closed={request.ClosedProfileName} attempt={request.AttemptCount} " +
                $"source={source} createRetryIn={delay:c} createAt={request.CreateNotBeforeUtc:O} error={request.LastError}");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                result: "CHỜ TẠO PRF MỚI",
                detail: source == "auto_profile_cooldown"
                    ? $"Đang tôn trọng cooldown + Auto Profile còn {FormatAutoReplacementUiWait(delay)}; PRF chờ nếu xuất hiện vẫn được mở ngay. lỗi={request.LastError}"
                    : $"Không có CREATE thật ngay trước đó; backoff kiểm tra kho {delay.TotalMinutes:0} phút. lỗi={request.LastError}");
        }
    }

    void WakeAutoReplacementForReusableSupply(string source)
    {
        if (IsAutomationHalted
            || !_autoReplacementFeatureInitialized
            || !_autoReplacementSessionArmed
            || !_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            return;
        }

        var woke = 0;
        var now = DateTime.UtcNow;

        lock (_autoReplacementQueueLock)
        {
            foreach (var request in _autoReplacementQueue)
            {
                if (request.NextAttemptUtc <= now)
                    continue;

                // Chỉ đánh thức request đang chờ nhánh tạo mới. Retry cleanup/slot-gate
                // ngắn giữ nguyên để không mở PRF trước khi hàng rào an toàn hoàn tất.
                if (!request.CreateNotBeforeUtc.HasValue
                    || request.CreateNotBeforeUtc.Value <= now)
                {
                    continue;
                }

                request.NextAttemptUtc = now;
                woke++;
            }

            if (woke > 0)
                SaveAutoReplacementQueueUnsafe();
        }

        if (woke <= 0)
            return;

        _log.Info(
            $"[AUTO_REPLACE_REUSE_SUPPLY_WAKE] source={source} woke={woke}");
        _ = RunAutoReplacementQueueAsync();
    }

    AutoReplacementQueueDocument LoadAutoReplacementQueueDocument()
    {
        try
        {
            if (!File.Exists(AutoReplacementQueuePath))
                return new AutoReplacementQueueDocument();

            var loaded = JsonSerializer.Deserialize<AutoReplacementQueueDocument>(
                File.ReadAllText(AutoReplacementQueuePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            loaded ??= new AutoReplacementQueueDocument();
            loaded.Pending ??= new List<AutoReplacementRequest>();
            foreach (var request in loaded.Pending)
                request.AttemptedProfiles ??= new List<string>();
            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_REPLACE_QUEUE_READ_WARN] {ex.Message}");
            return new AutoReplacementQueueDocument();
        }
    }

    void SaveAutoReplacementQueueUnsafe()
    {
        var document = new AutoReplacementQueueDocument
        {
            Version = 3,
            Pending = _autoReplacementQueue
                .OrderBy(x => x.QueuedUtc)
                .ToList()
        };

        var json = JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions { WriteIndented = true });

        var temp = AutoReplacementQueuePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, AutoReplacementQueuePath, overwrite: true);
    }

    // Được Auto Profile gọi khi profile thực sự vừa được tạo.
    // Profile này được coi là MỚI cho đến khi chạy test hoặc được đưa vào treo chính thức.
    void MarkAutoReplacementProfileCreated(string profileName)
    {
        MarkProfileSupplyState(profileName, "new", "auto_profile_created");
    }

    bool TryClassifyReplacementSupply(
        ProfileContext ctx,
        out string supplyState,
        out TimeSpan totalRuntime,
        out int priority)
    {
        supplyState = "";
        totalRuntime = TimeSpan.Zero;
        priority = int.MaxValue;

        var persisted = GetProfileSupplyState(ctx.Profile.Name);

        if (persisted is not null
            && (persisted.State.Equals("used", StringComparison.OrdinalIgnoreCase)
                || persisted.State.Equals("retired", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var stats = ReadStatisticsRuntime(ctx);
        totalRuntime = stats.Total;

        // Quy ước TEST: tổng Automation dưới 30 phút.
        // Đủ 30 phút trở lên thì coi như profile đã từng TREO, không đưa vào kho bù nữa.
        if (totalRuntime >= TimeSpan.FromMinutes(AutoReplacementTreoThresholdMinutes))
        {
            MarkProfileSupplyState(
                ctx.Profile.Name,
                "used",
                $"runtime_ge_{AutoReplacementTreoThresholdMinutes:0}_minutes");
            return false;
        }

        if (totalRuntime <= TimeSpan.Zero)
        {
            supplyState = "NEW";
            priority = 0;
            if (persisted is null || !persisted.State.Equals("new", StringComparison.OrdinalIgnoreCase))
                MarkProfileSupplyState(ctx.Profile.Name, "new", "inferred_no_runtime");
            return true;
        }

        supplyState = "TEST";
        priority = 1;
        if (persisted is null || !persisted.State.Equals("test", StringComparison.OrdinalIgnoreCase))
            MarkProfileSupplyState(ctx.Profile.Name, "test", "inferred_runtime_under_30_minutes");
        return true;
    }

    ProfileSupplyStateEntry? GetProfileSupplyState(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return null;

        lock (_profileSupplyStateLock)
        {
            var document = LoadProfileSupplyStateDocumentUnsafe();
            if (!document.Profiles.TryGetValue(profileName, out var entry))
                return null;

            return new ProfileSupplyStateEntry
            {
                State = entry.State ?? "",
                Source = entry.Source ?? "",
                UpdatedUtc = entry.UpdatedUtc
            };
        }
    }

    void MarkProfileSupplyState(string profileName, string state, string source)
    {
        profileName = (profileName ?? "").Trim();
        state = (state ?? "").Trim().ToLowerInvariant();
        source = (source ?? "").Trim();

        if (profileName.Length == 0 || state.Length == 0)
            return;

        try
        {
            lock (_profileSupplyStateLock)
            {
                var document = LoadProfileSupplyStateDocumentUnsafe();
                document.Version = 1;
                document.Profiles[profileName] = new ProfileSupplyStateEntry
                {
                    State = state,
                    Source = source,
                    UpdatedUtc = DateTime.UtcNow
                };
                SaveProfileSupplyStateDocumentUnsafe(document);
            }
        }
        catch (Exception ex)
        {
            // State phụ không được phép làm hỏng luồng profile chính.
            _log.Warn(
                $"[AUTO_REPLACE_SUPPLY_STATE_WARN] profile={profileName} state={state} error={ex.Message}");
        }
    }

    ProfileSupplyStateDocument LoadProfileSupplyStateDocumentUnsafe()
    {
        try
        {
            if (!File.Exists(ProfileSupplyStatePath))
                return NewProfileSupplyStateDocument();

            var loaded = JsonSerializer.Deserialize<ProfileSupplyStateDocument>(
                File.ReadAllText(ProfileSupplyStatePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (loaded is null)
                return NewProfileSupplyStateDocument();

            loaded.Profiles = new Dictionary<string, ProfileSupplyStateEntry>(
                loaded.Profiles ?? new Dictionary<string, ProfileSupplyStateEntry>(),
                StringComparer.OrdinalIgnoreCase);

            return loaded;
        }
        catch
        {
            return NewProfileSupplyStateDocument();
        }
    }

    static ProfileSupplyStateDocument NewProfileSupplyStateDocument()
    {
        return new ProfileSupplyStateDocument
        {
            Version = 1,
            Profiles = new Dictionary<string, ProfileSupplyStateEntry>(StringComparer.OrdinalIgnoreCase)
        };
    }

    void SaveProfileSupplyStateDocumentUnsafe(ProfileSupplyStateDocument document)
    {
        var json = JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions { WriteIndented = true });

        var temp = ProfileSupplyStatePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, ProfileSupplyStatePath, overwrite: true);
    }

}
