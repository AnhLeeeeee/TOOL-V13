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

        // Counter chẩn đoán/tương thích queue cũ: số CREATE đã bắt đầu cho request này.
        // Từ V3 counter này KHÔNG còn là hard-cap; pool kiểm tra mới quyết định tối đa 3 PRF.
        public int CreatedProfileCount { get; set; }

        // Khi chạm giới hạn tạo, request chuyển sang vòng CHỜ -> vét lại toàn bộ PRF chờ.
        // Trong khoảng chờ không được Wake sớm chỉ vì queue có thay đổi.
        public bool CreateLimitReuseWaitActive { get; set; }
        public string CreateLimitWaitReason { get; set; } = "";

        public string LastError { get; set; } = "";

        // Mỗi profile bù chỉ được mở/thử tối đa 1 lần trong SUỐT một request bù.
        // CREATE thất bại KHÔNG được xóa danh sách này: profile vừa thử lỗi/cooldown
        // không được giữ suất hiện tại đứng chờ rồi mở lại. Trong lúc CREATE cooldown,
        // chỉ NGUỒN CHỜ MỚI/chưa từng thử của request này mới được Wake và thử ngay.
        public List<string> AttemptedProfiles { get; set; } = new();

        // Một CREATE thật vừa thất bại và đã đăng ký cooldown. Khi deadline CREATE
        // đến hạn chỉ mở lại quyền cân nhắc CREATE; KHÔNG reset AttemptedProfiles.
        // Nhờ vậy hết PRF chờ chưa thử => CREATE mới, không quay lại PRF vừa fail.
        public bool ReuseSweepBeforeNextCreatePending { get; set; }

        // Request sinh từ thiếu suất sau Start All không có profile nguồn cần cleanup.
        // Request AutoClose bình thường luôn giữ true.
        public bool RequiresSourceCleanup { get; set; } = true;
    }

    sealed class AutoReplacementQueueDocument
    {
        public int Version { get; set; } = 4;
        public List<AutoReplacementRequest> Pending { get; set; } = new();
    }

    sealed class AutoReplacementCreateLimitStateDocument
    {
        public int Version { get; set; } = 3;
        public string SessionId { get; set; } = "";
        public int SessionCreatedCount { get; set; }
        public List<DateTime> CreatedUtc { get; set; } = new();

        // V3: 3 không còn là "tổng số CREATE cho cả deficit". Đây là pool các PRF
        // đang được giữ lại để kiểm tra/xoay vòng. PRF RUNNING khỏe/retire/delete/manual
        // remove sẽ rời pool; timeout/lỗi tạm vẫn ở lại để vòng sau kiểm tra tiếp.
        public List<string> CheckPoolProfiles { get; set; } = new();

        // Legacy V2: giữ field để đọc state cũ nhưng không còn tham gia enforcement.
        // Khi load V3 sẽ reset toàn bộ deficit cũ để tránh kẹt "3/3" sau cập nhật.
        public bool DeficitActive { get; set; }
        public string DeficitId { get; set; } = "";
        public int DeficitCreatedCount { get; set; }
        public DateTime? DeficitStartedUtc { get; set; }
    }

    sealed record AutoReplacementCreateLimitSnapshot(
        bool Enabled,
        int PerSlot,
        int PerHour,
        int PerSession,
        int RetryMinutes);

    sealed record AutoReplacementNoCreateScheduleSnapshot(
        bool Enabled,
        int StartMinute,
        int EndMinute);

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

    // Sentinel execution generation dành riêng cho +Auto Profile khi phải vét PRF
    // trong "Chờ dùng lại" sau một lượt CREATE lỗi. Luồng này dùng lại toàn bộ
    // safety/cleanup của AutoReplacement nhưng KHÔNG phụ thuộc việc Tự bù đang bật.
    // CancellationToken của +Auto Profile vẫn là nguồn dừng chính.
    const int AutoProfileReuseDrainExecutionGeneration = int.MinValue + 4242;

    static readonly TimeSpan AutoReplacementReusableRecoveryInterval = TimeSpan.FromSeconds(8);
    static readonly TimeSpan AutoReplacementFailedProfileCooldown = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AutoReplacementQueueWaitSlice = TimeSpan.FromSeconds(5);

    // Pool kiểm tra: tối đa 3 PRF được giữ lại để xoay kiểm tra. Đây KHÔNG phải
    // giới hạn tổng số CREATE. Khi một PRF trong pool lên RUNNING khỏe hoặc bị loại
    // khỏi vòng đời, slot pool được nhả và nếu vẫn thiếu target thì CREATE mới được phép.
    const int AutoReplacementCheckPoolMax = 3;
    static readonly TimeSpan AutoReplacementCheckPoolRetry = TimeSpan.FromMinutes(2);

    // Một candidate không được giữ queue vô hạn. Deadline 5 phút tính wall-clock từ
    // lúc candidate bắt đầu được xử lý; cleanup dùng luồng riêng và không bị cắt bởi token này.
    static readonly TimeSpan AutoReplacementCandidateCheckTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AutoReplacementReusableStateRetry = TimeSpan.FromSeconds(5);
    static readonly TimeSpan AutoReplacementOperationalRetry = TimeSpan.FromSeconds(10);
    const int AutoReplacementCleanupBarrierRetrySeconds = 15;

    // Profile NAME_SYNC_PENDING đã tồn tại phải được ưu tiên TRƯỚC khi tiêu account mới,
    // nhưng không mở lại ngay sau khi vừa đóng. Mỗi profile cần nghỉ tối thiểu 60 giây
    // kể từ lần queue/probe gần nhất; đồng thời AttemptedProfiles chặn thử lại trong cùng suất.
    static readonly TimeSpan AutoReplacementNameSyncMinRetryAge = TimeSpan.FromSeconds(60);

    readonly List<AutoReplacementRequest> _autoReplacementQueue = new();
    readonly HashSet<string> _autoReplacementRetiredProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoReplacementClaimedProfiles = new(StringComparer.OrdinalIgnoreCase);
    // Profile đang CLEANUP luôn chiếm slot cho tới khi xác minh đóng sạch. Không còn
    // safety-valve kiểu QUARANTINE_AND_CONTINUE vì có thể làm mở dư Chrome khi runtime cũ còn sống.
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
    readonly object _autoReplacementCreateLimitLock = new();

    // Đồng bộ điểm COMMIT của CREATE với checkbox "Chỉ dùng PRF chờ".
    // Không cache trạng thái: mọi chốt CREATE luôn đọc live setting hiện tại.
    // Lock chỉ bao quanh bước chọn/ASSIGN account (rất ngắn), không giữ qua await.
    readonly object _autoReplacementCreateModeGate = new();

    AutoReplacementCreateLimitStateDocument? _autoReplacementCreateLimitState;
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

    string AutoReplacementCreateLimitStatePath
        => Path.Combine(_baseDir, "manager_auto_create_limit_state.json");

    AutoReplacementCreateLimitSnapshot GetAutoReplacementCreateLimitSnapshot()
    {
        // AutoReplacement có thể chạy từ Tự động ngay cả khi dialog Auto Run chưa
        // từng mở trong phiên này. Khi đó đọc file Run Strategy để vẫn dùng đúng
        // ngưỡng user đã Lưu; nếu đã init thì dùng snapshot đang sống để áp dụng ngay.
        var settings = _runStrategyFeatureInitialized
            ? NormalizeRunStrategySettings(_runStrategySettings)
            : LoadRunStrategySettings();

        return new AutoReplacementCreateLimitSnapshot(
            settings.CreateLimitEnabled,
            settings.CreateLimitPerSlot,
            settings.CreateLimitPerHour,
            settings.CreateLimitPerSession,
            settings.CreateLimitReuseRetryMinutes);
    }

    int GetAutoReplacementCheckPoolMax()
        => AutoReplacementCheckPoolMax;

    AutoReplacementNoCreateScheduleSnapshot GetAutoReplacementNoCreateScheduleSnapshot()
    {
        // Giống create-limit: Tự bù có thể chạy trước khi dialog Auto Run được mở.
        // Vì vậy luôn đọc file đã Lưu khi Run Strategy chưa init trong phiên.
        var settings = _runStrategyFeatureInitialized
            ? NormalizeRunStrategySettings(_runStrategySettings)
            : LoadRunStrategySettings();

        return new AutoReplacementNoCreateScheduleSnapshot(
            settings.NoCreateScheduleEnabled,
            settings.NoCreateStartMinute,
            settings.NoCreateEndMinute);
    }

    bool TryGetAutoReplacementNoCreateScheduleBlock(
        out DateTime nextAllowedLocal,
        out string windowText)
    {
        nextAllowedLocal = DateTime.MinValue;
        windowText = "";

        var schedule = GetAutoReplacementNoCreateScheduleSnapshot();
        if (!schedule.Enabled)
            return false;

        var start = Math.Clamp(schedule.StartMinute, 0, (24 * 60) - 1);
        var end = Math.Clamp(schedule.EndMinute, 0, (24 * 60) - 1);

        // UI không cho lưu start == end. Nếu file bị sửa tay/cũ lỗi thì fail-open
        // để tránh khóa CREATE 24/7 ngoài ý muốn.
        if (start == end)
            return false;

        var now = DateTime.Now;
        var minute = (now.Hour * 60) + now.Minute;
        var blocked = start < end
            ? minute >= start && minute < end
            : minute >= start || minute < end;

        if (!blocked)
            return false;

        var endToday = now.Date.AddMinutes(end);
        nextAllowedLocal = start < end
            ? endToday
            : minute >= start
                ? endToday.AddDays(1)
                : endToday;

        windowText =
            $"{start / 60:00}:{start % 60:00}-{end / 60:00}:{end % 60:00}";
        return true;
    }

    bool IsAutomaticNewProfileCreationAllowedNow()
    {
        if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
            return false;

        return !TryGetAutoReplacementNoCreateScheduleBlock(
            out _,
            out _);
    }

    AutoReplacementCreateLimitStateDocument EnsureAutoReplacementCreateLimitStateUnsafe()
    {
        if (_autoReplacementCreateLimitState is not null)
            return _autoReplacementCreateLimitState;

        AutoReplacementCreateLimitStateDocument state;
        try
        {
            if (File.Exists(AutoReplacementCreateLimitStatePath))
            {
                state = JsonSerializer.Deserialize<AutoReplacementCreateLimitStateDocument>(
                            File.ReadAllText(AutoReplacementCreateLimitStatePath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new AutoReplacementCreateLimitStateDocument();
            }
            else
            {
                state = new AutoReplacementCreateLimitStateDocument();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_CREATE_LIMIT_STATE_READ_WARN] error={ex.Message}");
            state = new AutoReplacementCreateLimitStateDocument();
        }

        var loadedVersion = state.Version;
        state.Version = 3;
        state.CreatedUtc ??= new List<DateTime>();
        state.CreatedUtc = state.CreatedUtc
            .Select(x => x.Kind == DateTimeKind.Utc ? x : x.ToUniversalTime())
            .Where(x => x > DateTime.UtcNow.AddDays(-2) && x <= DateTime.UtcNow.AddMinutes(5))
            .OrderBy(x => x)
            .ToList();
        state.SessionCreatedCount = Math.Max(0, state.SessionCreatedCount);
        state.CheckPoolProfiles ??= new List<string>();
        state.CheckPoolProfiles = state.CheckPoolProfiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Migration V2 -> V3: hard-cap deficit 3/3 bị loại hoàn toàn. Không được
        // mang state cũ sang vì đó chính là nguyên nhân Tool treo thiếu target qua đêm.
        state.DeficitActive = false;
        state.DeficitCreatedCount = 0;
        state.DeficitId = "";
        state.DeficitStartedUtc = null;

        if (loadedVersion < 3)
        {
            state.CheckPoolProfiles.Clear();
            _log.Warn(
                $"[AUTO_CHECK_POOL_MIGRATE_V3] oldVersion={loadedVersion} action=CLEAR_LEGACY_DEFICIT_STATE");
        }

        if (string.IsNullOrWhiteSpace(state.SessionId))
            state.SessionId = Guid.NewGuid().ToString("N");

        _autoReplacementCreateLimitState = state;
        return state;
    }

    void SaveAutoReplacementCreateLimitStateUnsafe()
    {
        var temp = AutoReplacementCreateLimitStatePath + ".tmp";
        try
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            var json = JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, AutoReplacementCreateLimitStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_CREATE_LIMIT_STATE_WRITE_WARN] error={ex.Message}");
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { }
        }
    }

    int PruneAutoReplacementCreateHourWindowUnsafe(DateTime nowUtc)
    {
        var state = EnsureAutoReplacementCreateLimitStateUnsafe();
        var cutoff = nowUtc.AddHours(-1);
        var before = state.CreatedUtc.Count;
        state.CreatedUtc.RemoveAll(x => x <= cutoff || x > nowUtc.AddMinutes(5));
        return before - state.CreatedUtc.Count;
    }

    (int Count, string[] Profiles) GetAutoReplacementCheckPoolSnapshot()
    {
        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            var profiles = (state.CheckPoolProfiles ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return (profiles.Length, profiles);
        }
    }

    bool TryReserveAutoReplacementCheckPoolProfile(
        string profileName,
        out int checkingCount)
    {
        profileName = (profileName ?? "").Trim();
        checkingCount = 0;
        if (profileName.Length == 0)
            return false;

        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            state.CheckPoolProfiles ??= new List<string>();
            state.CheckPoolProfiles = state.CheckPoolProfiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (state.CheckPoolProfiles.Any(x =>
                    x.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
            {
                checkingCount = state.CheckPoolProfiles.Count;
                return true;
            }

            if (state.CheckPoolProfiles.Count >= AutoReplacementCheckPoolMax)
            {
                checkingCount = state.CheckPoolProfiles.Count;
                return false;
            }

            state.CheckPoolProfiles.Add(profileName);
            checkingCount = state.CheckPoolProfiles.Count;
            SaveAutoReplacementCreateLimitStateUnsafe();
        }

        _log.Info(
            $"[AUTO_CHECK_POOL_ADD] profile={profileName} checking={checkingCount}/{AutoReplacementCheckPoolMax}");
        return true;
    }

    void ReleaseAutoReplacementCheckPoolProfile(
        string profileName,
        string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var removed = false;
        var remaining = 0;
        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            state.CheckPoolProfiles ??= new List<string>();
            removed = state.CheckPoolProfiles.RemoveAll(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase)) > 0;
            remaining = state.CheckPoolProfiles.Count;
            if (removed)
                SaveAutoReplacementCreateLimitStateUnsafe();
        }

        if (removed)
        {
            _log.Info(
                $"[AUTO_CHECK_POOL_RELEASE] profile={profileName} checking={remaining}/{AutoReplacementCheckPoolMax} source={source}");
        }
    }

    void RebuildAutoReplacementCheckPoolFromReusableState(string source)
    {
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            lock (_reusableProfileQueueLock)
            {
                var queue = EnsureReusableProfileQueueLoadedUnsafe();
                foreach (var entry in queue.Pending)
                {
                    if (!string.IsNullOrWhiteSpace(entry.ProfileName))
                        candidateNames.Add(entry.ProfileName.Trim());
                }

                foreach (var profileName in queue.FailedProfiles.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(profileName))
                        candidateNames.Add(profileName.Trim());
                }
            }

            foreach (var profileName in _autoReplacementClaimedProfiles)
            {
                if (!string.IsNullOrWhiteSpace(profileName))
                    candidateNames.Add(profileName.Trim());
            }

            var keep = new List<string>();
            foreach (var profileName in candidateNames)
            {
                var supply = GetProfileSupplyState(profileName);
                if (supply is null)
                    continue;

                var state = (supply.State ?? "").Trim();
                if (state.Equals("new", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(profileName);
                }
            }

            // Nếu sau migration đã có hơn 3 PRF kiểm tra tồn tại từ bản cũ thì
            // giữ đầy đủ chúng trong state. CREATE sẽ bị khóa cho tới khi pool giảm
            // xuống dưới 3; tuyệt đối không "quên" candidate cũ để rồi tạo thêm.
            keep = keep
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_autoReplacementCreateLimitLock)
            {
                var state = EnsureAutoReplacementCreateLimitStateUnsafe();
                state.CheckPoolProfiles = keep;
                state.DeficitActive = false;
                state.DeficitId = "";
                state.DeficitCreatedCount = 0;
                state.DeficitStartedUtc = null;
                SaveAutoReplacementCreateLimitStateUnsafe();
            }

            _log.Info(
                $"[AUTO_CHECK_POOL_REBUILD] source={source} checking={keep.Count}/{AutoReplacementCheckPoolMax} profiles={string.Join(",", keep)}");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_CHECK_POOL_REBUILD_WARN] source={source} error={ex.Message}");
        }
    }

    // Legacy API: từ V3 capacity phục hồi KHÔNG reset pool. Pool chỉ nhả đúng PRF
    // khi PRF đó RUNNING khỏe hoặc rời vòng đời; như vậy lần thiếu tiếp theo không
    // vô tình tạo thêm 3 PRF mới trong khi 3 PRF cũ vẫn đang chờ kiểm tra.
    void ResetAutoReplacementDeficitCreateBudget(string source)
    {
        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            if (!state.DeficitActive
                && state.DeficitCreatedCount == 0
                && string.IsNullOrWhiteSpace(state.DeficitId))
            {
                return;
            }

            state.DeficitActive = false;
            state.DeficitId = "";
            state.DeficitCreatedCount = 0;
            state.DeficitStartedUtc = null;
            SaveAutoReplacementCreateLimitStateUnsafe();
        }

        _log.Info(
            $"[AUTO_CREATE_DEFICIT_LEGACY_RESET] source={source} action=NO_CHECK_POOL_RESET");
    }

    void ResetAutoReplacementDeficitCreateBudgetIfCapacityRecovered(
        int target,
        int occupied,
        string source)
    {
        if (target <= 0 || occupied < target)
            return;

        ResetAutoReplacementDeficitCreateBudget(
            $"capacity_recovered:{source}:occupied={occupied}:target={target}");
    }

    void TryResetAutoReplacementDeficitCreateBudgetFromLiveCapacity(string source)
    {
        int target;
        bool initialized;
        lock (_autoReplacementFixedSlotLock)
        {
            target = _autoReplacementTargetSlots;
            initialized = _autoReplacementTargetInitialized;
        }

        if (!initialized || target <= 0)
            return;

        var occupied = CountAutoReplacementFulfilledSlots();
        ResetAutoReplacementDeficitCreateBudgetIfCapacityRecovered(
            target, occupied, source);
    }

    public void ResetAutoReplacementCreateLimitSession(string source)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            PruneAutoReplacementCreateHourWindowUnsafe(nowUtc);
            state.SessionId = Guid.NewGuid().ToString("N");
            state.SessionCreatedCount = 0;
            SaveAutoReplacementCreateLimitStateUnsafe();

            _log.Info(
                $"[AUTO_CREATE_LIMIT_SESSION_RESET] source={source} session={state.SessionId} " +
                $"hourCreated={state.CreatedUtc.Count}");
        }
    }

    void ResetAutoReplacementAdvancedCreateLimitCounters(string source)
    {
        lock (_autoReplacementCreateLimitLock)
        {
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            state.SessionId = Guid.NewGuid().ToString("N");
            state.SessionCreatedCount = 0;
            state.CreatedUtc.Clear();
            SaveAutoReplacementCreateLimitStateUnsafe();

            _log.Info(
                $"[AUTO_CREATE_LIMIT_ADVANCED_RESET] source={source} session={state.SessionId} hourCreated=0 sessionCreated=0");
        }
    }

    int GetAutoReplacementRequestCreatedCount(AutoReplacementRequest request)
    {
        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, live?.CreatedProfileCount ?? request.CreatedProfileCount);
        }
    }

    int GetAutoReplacementRequestCreatedCount(string requestId)
    {
        requestId = (requestId ?? "").Trim();
        if (requestId.Length == 0)
            return 0;

        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, live?.CreatedProfileCount ?? 0);
        }
    }

    bool TryGetAutoReplacementCreateLimitBlock(
        AutoReplacementRequest request,
        out string reason,
        out int checkPoolCount,
        out int hourCreated,
        out int sessionCreated)
    {
        reason = "";
        checkPoolCount = 0;
        hourCreated = 0;
        sessionCreated = 0;

        var limit = GetAutoReplacementCreateLimitSnapshot();
        var pool = GetAutoReplacementCheckPoolSnapshot();
        checkPoolCount = pool.Count;

        // Luật chính luôn áp dụng: chỉ giữ tối đa 3 PRF đang xoay kiểm tra.
        // Đây KHÔNG phải tổng số CREATE; khi một PRF rời pool sẽ có chỗ tạo mới.
        if (checkPoolCount >= AutoReplacementCheckPoolMax)
        {
            reason = $"check_pool={checkPoolCount}/{AutoReplacementCheckPoolMax}";
            return true;
        }

        // Giới hạn /giờ và /phiên là lớp nâng cao. Khi OFF thì không đọc counter
        // để quyết định và hoàn toàn không ảnh hưởng nhánh CREATE.
        if (!limit.Enabled)
            return false;

        lock (_autoReplacementCreateLimitLock)
        {
            var nowUtc = DateTime.UtcNow;
            var state = EnsureAutoReplacementCreateLimitStateUnsafe();
            var pruned = PruneAutoReplacementCreateHourWindowUnsafe(nowUtc);
            if (pruned > 0)
                SaveAutoReplacementCreateLimitStateUnsafe();

            hourCreated = state.CreatedUtc.Count;
            sessionCreated = state.SessionCreatedCount;
        }

        if (hourCreated >= limit.PerHour)
        {
            reason = $"hour={hourCreated}/{limit.PerHour}";
            return true;
        }

        if (sessionCreated >= limit.PerSession)
        {
            reason = $"session={sessionCreated}/{limit.PerSession}";
            return true;
        }

        return false;
    }

    bool TryReserveAutoReplacementCreateAttempt(
        AutoReplacementRequest request,
        string profileName,
        out string blockReason)
    {
        blockReason = "";
        var limit = GetAutoReplacementCreateLimitSnapshot();
        var nowUtc = DateTime.UtcNow;

        // Chốt pool trước điểm CREATE thật. Nếu đã có 3 PRF đang chờ/kiểm tra thì
        // không tiêu thêm account; scheduler sẽ xoay lại pool cũ.
        if (!TryReserveAutoReplacementCheckPoolProfile(profileName, out var checkPoolCount))
        {
            blockReason = $"check_pool={checkPoolCount}/{AutoReplacementCheckPoolMax}";
            return false;
        }

        var limitCommitted = false;
        try
        {
            if (limit.Enabled)
            {
                lock (_autoReplacementCreateLimitLock)
                {
                    var state = EnsureAutoReplacementCreateLimitStateUnsafe();
                    PruneAutoReplacementCreateHourWindowUnsafe(nowUtc);

                    if (state.CreatedUtc.Count >= limit.PerHour)
                        blockReason = $"hour={state.CreatedUtc.Count}/{limit.PerHour}";
                    else if (state.SessionCreatedCount >= limit.PerSession)
                        blockReason = $"session={state.SessionCreatedCount}/{limit.PerSession}";

                    if (blockReason.Length == 0)
                    {
                        state.CreatedUtc.Add(nowUtc);
                        state.SessionCreatedCount++;
                        SaveAutoReplacementCreateLimitStateUnsafe();
                        limitCommitted = true;
                    }
                }

                if (blockReason.Length > 0)
                {
                    // Chưa CREATE thật nên phải trả lại slot pool vừa reserve.
                    ReleaseAutoReplacementCheckPoolProfile(
                        profileName,
                        "advanced_limit_block_before_create");
                    return false;
                }
            }

            // Giữ counter per-request chỉ để chẩn đoán/tương thích queue cũ; nó không
            // còn là hard-cap 3/3.
            int requestCreated;
            lock (_autoReplacementQueueLock)
            {
                var live = _autoReplacementQueue.FirstOrDefault(x =>
                    x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
                var target = live ?? request;
                target.CreatedProfileCount = Math.Max(0, target.CreatedProfileCount) + 1;
                target.CreateLimitReuseWaitActive = false;
                target.CreateLimitWaitReason = "";

                if (live is not null)
                    SaveAutoReplacementQueueUnsafe();

                requestCreated = target.CreatedProfileCount;
            }

            int hourCreated = 0;
            int sessionCreated = 0;
            if (limit.Enabled)
            {
                lock (_autoReplacementCreateLimitLock)
                {
                    var state = EnsureAutoReplacementCreateLimitStateUnsafe();
                    hourCreated = state.CreatedUtc.Count;
                    sessionCreated = state.SessionCreatedCount;
                }
            }

            _log.Info(
                $"[AUTO_CHECK_POOL_RESERVE] request={request.Id} profile={profileName} " +
                $"checking={checkPoolCount}/{AutoReplacementCheckPoolMax} requestCreated={requestCreated} " +
                $"advancedLimit={limit.Enabled} hour={hourCreated}/{limit.PerHour} session={sessionCreated}/{limit.PerSession}");

            return true;
        }
        catch
        {
            // Nếu exception xảy ra trước CREATE, tránh giữ một slot pool ma. Counter
            // nâng cao đã commit (nếu có) vẫn giữ nguyên theo semantics cũ.
            ReleaseAutoReplacementCheckPoolProfile(
                profileName,
                limitCommitted ? "reserve_exception_after_limit_commit" : "reserve_exception");
            throw;
        }
    }

    void ScheduleAutoReplacementCreateLimitReuseRetry(
        string requestId,
        string lastError)
    {
        var limit = GetAutoReplacementCreateLimitSnapshot();
        var pool = GetAutoReplacementCheckPoolSnapshot();
        var poolFull = pool.Count >= AutoReplacementCheckPoolMax;

        // Pool đầy 3/3 => không tạo PRF thứ 4, chỉ xoay lại pool khoảng 2 phút/lần.
        // Nếu chỉ vướng giới hạn nâng cao /giờ hoặc /phiên thì dùng RetryMinutes cấu hình.
        var delay = poolFull
            ? AutoReplacementCheckPoolRetry
            : TimeSpan.FromMinutes(limit.RetryMinutes);

        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
            if (request is null)
                return;

            request.AttemptCount++;
            request.LastError = (lastError ?? "").Trim();
            request.AttemptedProfiles ??= new List<string>();
            var previousAttempted = request.AttemptedProfiles.Count;
            request.AttemptedProfiles.Clear();
            request.CreateNotBeforeUtc = null;
            request.CreateLimitReuseWaitActive = true;
            request.CreateLimitWaitReason = request.LastError;
            request.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            SaveAutoReplacementQueueUnsafe();

            _log.Warn(
                $"[AUTO_CREATE_LIMIT_WAIT_REUSE] id={request.Id} closed={request.ClosedProfileName} " +
                $"retryIn={delay:c} mode={(poolFull ? "CHECK_POOL_FULL_ROTATION" : "ADVANCED_LIMIT_WAIT")} " +
                $"checking={pool.Count}/{AutoReplacementCheckPoolMax} clearedAttempted={previousAttempted} reason={request.CreateLimitWaitReason}");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                result: "TẠM DỪNG TẠO / CHỜ PRF",
                detail: poolFull
                    ? $"Đang có đủ {pool.Count}/{AutoReplacementCheckPoolMax} PRF trong pool kiểm tra. Không tạo PRF thứ 4; khoảng 2 phút sẽ xoay kiểm tra lại pool. {request.CreateLimitWaitReason}"
                    : $"Giới hạn CREATE nâng cao đang bật. Nghỉ {limit.RetryMinutes} phút rồi thử lại PRF chờ. {request.CreateLimitWaitReason}");
        }
    }

    void MarkAutoReplacementCreateLimitReuseRoundDue(AutoReplacementRequest request)
    {
        var wasWaiting = false;
        var reason = "";
        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            var target = live ?? request;
            if (!target.CreateLimitReuseWaitActive)
                return;

            wasWaiting = true;
            reason = target.CreateLimitWaitReason;
            target.CreateLimitReuseWaitActive = false;
            target.CreateLimitWaitReason = "";
            if (live is not null)
                SaveAutoReplacementQueueUnsafe();
        }

        if (wasWaiting)
        {
            _log.Info(
                $"[AUTO_CREATE_LIMIT_REUSE_ROUND_BEGIN] id={request.Id} closed={request.ClosedProfileName} " +
                $"action=TRY_ALL_WAITING_PROFILES reason={reason}");
        }
    }

    void NotifyAutoReplacementCreateLimitSettingsChanged(string source)
    {
        if (!_autoReplacementFeatureInitialized)
            return;

        List<AutoReplacementRequest> waiting;
        lock (_autoReplacementQueueLock)
        {
            waiting = _autoReplacementQueue
                .Where(x => x.CreateLimitReuseWaitActive)
                .ToList();
        }

        if (waiting.Count == 0)
            return;

        var wakeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in waiting)
        {
            if (!TryGetAutoReplacementCreateLimitBlock(
                    request,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                wakeIds.Add(request.Id);
            }
        }

        if (wakeIds.Count == 0)
        {
            _log.Info(
                $"[AUTO_CREATE_LIMIT_SETTINGS_APPLIED] source={source} waiting={waiting.Count} woke=0");
            return;
        }

        lock (_autoReplacementQueueLock)
        {
            foreach (var request in _autoReplacementQueue)
            {
                if (!wakeIds.Contains(request.Id))
                    continue;

                request.CreateLimitReuseWaitActive = false;
                request.CreateLimitWaitReason = "";
                request.NextAttemptUtc = DateTime.UtcNow;
            }
            SaveAutoReplacementQueueUnsafe();
        }

        _log.Info(
            $"[AUTO_CREATE_LIMIT_SETTINGS_APPLIED] source={source} waiting={waiting.Count} woke={wakeIds.Count}");

        if (_autoReplacementSessionArmed
            && _autoCloseSettings.OpenReplacementAfterAutoClose
            && !_autoReplacementQueueRunning)
        {
            _ = RunAutoReplacementQueueAsync();
        }
    }

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
            || Disposing)
        {
            return false;
        }

        // +Auto Profile dùng engine kiểm tra PRF bù ngay cả khi tính năng Tự bù
        // không được arm. Chỉ bypass hai cờ của AutoReplacement; mọi hard-stop,
        // cleanup barrier và CancellationToken của caller vẫn giữ nguyên.
        if (generation == AutoProfileReuseDrainExecutionGeneration)
            return true;

        if (!_autoReplacementSessionArmed
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
                RebuildAutoReplacementCheckPoolFromReusableState("manager_shown");
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
        // Báo ngay cho Comment Check rằng lifecycle của PRF cũ đã kết thúc. Không chờ
        // tick 1 giây vì profile có thể bị xóa khỏi catalog rất nhanh sau cleanup.
        EmitCommentCheckManagerStateTelemetry(force: true);

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

        if (IsRunStrategyDailyReplaceAllRefreshBusy())
        {
            if (GetAutoReplacementPendingCount() > 0)
                _log.Info($"[AUTO_REPLACE_QUEUE_HELD_THAY_ALL] pending={GetAutoReplacementPendingCount()} action=REFRESH_OWNS_CAPACITY");
            return;
        }

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

                if (IsRunStrategyDailyReplaceAllRefreshBusy())
                {
                    _log.Info($"[AUTO_REPLACE_QUEUE_PAUSED_THAY_ALL] pending={GetAutoReplacementPendingCount()} action=REFRESH_OWNS_CAPACITY");
                    return;
                }

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

                // Nếu request vừa hết chu kỳ chờ do chạm giới hạn CREATE thì
                // bắt đầu vòng mới bằng cách vét lại toàn bộ PRF chờ.
                MarkAutoReplacementCreateLimitReuseRoundDue(request);

                // Một CREATE thật thất bại phải quay RA tầng ngoài sau cooldown.
                // Chỉ khi deadline CREATE đã đến mới reset AttemptedProfiles, nhờ vậy:
                // - trong lúc cooldown, PRF chờ MỚI xuất hiện vẫn có thể Wake và được thử ngay;
                // - profile vừa lỗi không bị mở lại sớm;
                // - đúng lúc sắp được phép CREATE tiếp theo, toàn bộ hàng chờ được vét lại 1 lượt.
                PrepareAutoReplacementReuseSweepBeforeNextCreateIfDue(request);

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

                // 3C.6.4D - AUTO REPLACE RuntimeGate.
                //
                // Đặt tại ranh giới xử lý một request Tự bù thật sự, sau khi request
                // đã đến hạn và execution generation hợp lệ, nhưng TRƯỚC cleanup/reuse/create.
                //
                // Nhờ vậy auto_replace bao phủ toàn bộ hành động Tự bù:
                // - dùng lại PRF chờ;
                // - NAME_SYNC reuse;
                // - fallback tạo PRF mới.
                //
                // THAY ALL không đi qua đây trong lúc refresh vì queue đã bị HOLD ở đầu hàm.
                var autoReplaceAllowed = RemotePolicyRuntimeGate.IsAllowed(
                    "auto_replace",
                    out var autoReplaceDecision);

                _log.Info(
                    $"[REMOTE_POLICY_AUTO_REPLACE_RUNTIME_CHECK] id={request.Id} " +
                    $"closed={request.ClosedProfileName} reason={request.Reason} " +
                    $"revision={autoReplaceDecision.Revision} mode={autoReplaceDecision.Mode} " +
                    $"wouldBlock={autoReplaceDecision.WouldBlock} enforcement={autoReplaceDecision.EnforcementEnabled} " +
                    $"allowed={autoReplaceAllowed} adminBypass={RemotePolicyRuntimeGate.AdminBypass}");

                // 3C.6 hiện EnforcementEnabled=false nên nhánh này chưa thể xảy ra.
                // Giữ request nguyên vẹn để sau này nếu bật enforcement thì không làm mất suất bù.
                if (!autoReplaceAllowed)
                {
                    _log.Warn(
                        $"[REMOTE_POLICY_AUTO_REPLACE_RUNTIME_BLOCKED] id={request.Id} " +
                        $"closed={request.ClosedProfileName} revision={autoReplaceDecision.Revision} " +
                        $"mode={autoReplaceDecision.Mode} action=PRESERVE_AND_RETRY");

                    SetAutoReplacementUiPhase(
                        "CHỜ POLICY",
                        "Tự bù đang bị khóa",
                        request.Id);

                    ScheduleAutoReplacementOperationalRetry(
                        request.Id,
                        "Remote policy chặn Auto Replace.",
                        AutoReplacementOperationalRetry);

                    continue;
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

                // FAULT_10M có thể đã được xếp hàng, nhưng trong lúc request chờ gate
                // chính profile nguồn lại được Capacity/Reuse mở và xác nhận RUNNING khỏe.
                // Request cũ khi đó đã stale: tuyệt đối không được source-cleanup rồi
                // đóng chính runtime vừa hồi phục (ca 170). BAN/TIME không áp dụng luật
                // này vì đó là kết thúc vòng đời và vẫn phải đóng dù profile có bị mở lại.
                if (SuppressStaleFaultReplacementIfSourceRecovered(request))
                    continue;

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
                var createLimitBlocked = false;
                var createAttempted = false;
                var reuseOnlyCreateBlocked = false;
                var dailyReplaceAllCreateBlocked = false;
                var scheduleCreateBlocked = false;
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
                            else if (IsAutoReplacementCreateBlockedByDailyReplaceAll(
                                         request,
                                         "after_reuse_exhausted"))
                            {
                                dailyReplaceAllCreateBlocked = true;
                                lastError = request.LastError;

                                SetAutoReplacementUiPhase(
                                    "CHỈ BÙ PRF CHỜ",
                                    "THAY ALL · không tạo PRF mới",
                                    request.Id);
                            }
                            else if (TryGetAutoReplacementNoCreateScheduleBlock(
                                         out var scheduleNextLocal,
                                         out var scheduleWindowText))
                            {
                                scheduleCreateBlocked = true;
                                lastError =
                                    $"Khung giờ cấm CREATE: {scheduleWindowText}; chờ đến {scheduleNextLocal:dd/MM HH:mm}.";

                                _log.Warn(
                                    $"[AUTO_CREATE_SCHEDULE_BLOCKED] id={request.Id} closed={request.ClosedProfileName} " +
                                    $"window={scheduleWindowText} nextLocal={scheduleNextLocal:O} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)} action=WAIT_SCHEDULE_END");

                                SetAutoReplacementUiPhase(
                                    "CHỜ HẾT GIỜ CẤM TẠO",
                                    $"{scheduleWindowText} → {scheduleNextLocal:HH:mm}",
                                    request.Id);
                            }
                            else if (TryGetAutoReplacementCreateLimitBlock(
                                         request,
                                         out var createLimitReason,
                                         out var createLimitCheckPoolCount,
                                         out var createLimitHourCount,
                                         out var createLimitSessionCount))
                            {
                                createLimitBlocked = true;
                                var limit = GetAutoReplacementCreateLimitSnapshot();
                                var poolFull = createLimitCheckPoolCount >= AutoReplacementCheckPoolMax;
                                var retryDelay = poolFull
                                    ? AutoReplacementCheckPoolRetry
                                    : TimeSpan.FromMinutes(limit.RetryMinutes);
                                lastError = poolFull
                                    ? $"Pool kiểm tra đã đủ {createLimitCheckPoolCount}/{AutoReplacementCheckPoolMax}; không tạo PRF thứ 4, chuyển sang xoay lại PRF chờ."
                                    : $"Đã chạm giới hạn CREATE nâng cao ({createLimitReason}); tạm dừng tạo và chuyển sang vòng retry PRF chờ.";

                                _log.Warn(
                                    $"[AUTO_CREATE_LIMIT_BLOCK] id={request.Id} closed={request.ClosedProfileName} " +
                                    $"checking={createLimitCheckPoolCount}/{AutoReplacementCheckPoolMax} hour={createLimitHourCount}/{limit.PerHour} " +
                                    $"session={createLimitSessionCount}/{limit.PerSession} retryIn={retryDelay:c} reason={createLimitReason}");

                                SetAutoReplacementUiPhase(
                                    "TẠM DỪNG TẠO",
                                    poolFull
                                        ? $"pool kiểm tra {createLimitCheckPoolCount}/{AutoReplacementCheckPoolMax} · xoay lại sau ~2 phút"
                                        : $"giới hạn nâng cao · retry sau {limit.RetryMinutes} phút",
                                    request.Id);
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

                                // Dựa vào marker do TryCreate đặt, KHÔNG dựa vào trạng thái
                                // checkbox tại đúng micro-tick này. Nhờ vậy nếu user bật rồi tắt
                                // rất nhanh trong lúc await, request không bị hiểu nhầm là CREATE fail.
                                if (!filled
                                    && request.LastError.StartsWith(
                                        "Chế độ CHỈ PRF CHỜ: CREATE hard-gate",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    reuseOnlyCreateBlocked = true;
                                    createAttempted = false;
                                    lastError = request.LastError;

                                    _log.Warn(
                                        $"[AUTO_REPLACE_REUSE_ONLY_TOGGLED_DURING_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                                        $"current={_autoCloseSettings.ReuseOnlyNoCreateProfile} action=route_by_live_setting");
                                }
                                else if (!filled
                                    && request.LastError.StartsWith(
                                        "THAY ALL: CREATE hard-gate",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    dailyReplaceAllCreateBlocked = true;
                                    createAttempted = false;
                                    lastError = request.LastError;

                                    _log.Warn(
                                        $"[AUTO_REPLACE_THAY_ALL_CREATE_ROUTED] id={request.Id} closed={request.ClosedProfileName} action=REUSE_ONLY_RETRY");
                                }
                                else if (!filled
                                    && request.LastError.StartsWith(
                                        "PRF chờ chưa vét xong:",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    reusableGuardBlocked = true;
                                    createAttempted = false;
                                    lastError = request.LastError;

                                    _log.Warn(
                                        $"[AUTO_REPLACE_REUSE_DRAIN_ROUTED] id={request.Id} closed={request.ClosedProfileName} action=SHORT_REUSE_RETRY detail={lastError}");
                                }
                                else if (!filled
                                    && request.LastError.StartsWith(
                                        "Khung giờ cấm CREATE:",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    scheduleCreateBlocked = true;
                                    createAttempted = false;
                                    lastError = request.LastError;

                                    _log.Warn(
                                        $"[AUTO_CREATE_SCHEDULE_ENTERED_DURING_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                                        $"action=WAIT_SCHEDULE_END");
                                }
                                else if (!filled
                                    && TryGetAutoReplacementCreateLimitBlock(
                                        request,
                                        out var postCreateLimitReason,
                                        out var postCreateCheckPoolCount,
                                        out var postCreateHourCount,
                                        out var postCreateSessionCount))
                                {
                                    createLimitBlocked = true;
                                    var limit = GetAutoReplacementCreateLimitSnapshot();
                                    var poolFull = postCreateCheckPoolCount >= AutoReplacementCheckPoolMax;
                                    var retryDelay = poolFull
                                        ? AutoReplacementCheckPoolRetry
                                        : TimeSpan.FromMinutes(limit.RetryMinutes);
                                    lastError = poolFull
                                        ? $"Pool kiểm tra đã đủ {postCreateCheckPoolCount}/{AutoReplacementCheckPoolMax}; xoay lại PRF hiện có trước khi tạo thêm."
                                        : $"Đã chạm giới hạn CREATE nâng cao ({postCreateLimitReason}); tạm dừng tạo và chuyển sang vòng retry PRF chờ.";

                                    _log.Warn(
                                        $"[AUTO_CREATE_LIMIT_REACHED_AFTER_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                                        $"checking={postCreateCheckPoolCount}/{AutoReplacementCheckPoolMax} hour={postCreateHourCount}/{limit.PerHour} " +
                                        $"session={postCreateSessionCount}/{limit.PerSession} retryIn={retryDelay:c} reason={postCreateLimitReason}");
                                }
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
                    TryResetAutoReplacementDeficitCreateBudgetFromLiveCapacity(
                        $"slot_done:{request.Id}");

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
                    else if (reuseOnlyCreateBlocked)
                    {
                        // User đã TẮT lại trước lúc scheduler chạy: không được ngủ 5 phút
                        // hay rơi vào backoff CREATE cũ. Retry gần như ngay; lần kế tiếp
                        // hard-gate đọc live=false nên CREATE fallback được phép trở lại.
                        ScheduleAutoReplacementOperationalRetry(
                            request.Id,
                            lastError,
                            TimeSpan.FromMilliseconds(250));
                    }
                    else if (dailyReplaceAllCreateBlocked
                             || lastError.StartsWith(
                                 "THAY ALL: CREATE hard-gate",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        ScheduleAutoReplacementDailyReplaceAllReuseRetry(request.Id, lastError);
                    }
                    else if (scheduleCreateBlocked
                             || lastError.StartsWith(
                                 "Khung giờ cấm CREATE:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        ScheduleAutoReplacementNoCreateScheduleRetry(
                            request.Id,
                            lastError);
                    }
                    else if (createLimitBlocked)
                    {
                        ScheduleAutoReplacementCreateLimitReuseRetry(request.Id, lastError);
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
                && _autoCloseSettings.OpenReplacementAfterAutoClose
                && !IsRunStrategyDailyReplaceAllRefreshBusy())
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
                        $"[AUTO_REPLACE_FAILED_CHROME] profile={ctx.Profile.Name} attempt=1/2 reply={closeReply}");

                    // Lệnh STOP của Worker chỉ signal engine và trả về ngay. Nếu vòng
                    // Automation chưa unwind xong, close_chrome có thể trả
                    // automation_running dù Manager đã gửi STOP. Cho engine một cửa
                    // sổ ngắn rồi thử graceful close thêm đúng 1 lần trước khi chuyển
                    // sang shutdown Worker + force cleanup theo PID/CDP.
                    if (closeReply.Equals("automation_running", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(900);
                        ThrowIfEmergencyStopRequested("before_close_chrome_retry");

                        closeReply = await SendCloseChromeCommandAsync(ctx);
                        _log.Info(
                            $"[AUTO_REPLACE_FAILED_CHROME] profile={ctx.Profile.Name} attempt=2/2 reply={closeReply}");
                    }
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
                await EnsureAutoCloseChromeStoppedByPathAndPortAsync(
                    profileName,
                    profile.ProfilePath,
                    profile.CdpPort,
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

        // CLEANUP là GLOBAL BARRIER thật sự: khi profile bù lỗi còn bất kỳ dấu hiệu
        // Worker/Chrome nào, tuyệt đối không nhường queue để mở profile khác. Bản cũ
        // có safety-valve sau 4 lượt rồi QUARANTINE_AND_CONTINUE; đó là nguyên nhân
        // tạo 6/5, 7/5 khi Chrome orphan vẫn giữ CDP. Từ đây chỉ có hai cách thoát:
        // 1) cleanup được xác minh sạch; 2) cả phiên automation đang đóng/dừng khẩn cấp.
        _autoReplacementCleanupProfiles.Add(profileName);

        SetAutoReplacementUiPhase(
            "DỌN PRF BÙ LỖI",
            profileName,
            request.Id);

        var attempt = 0;

        while (!_closing
               && !IsDisposed
               && !Disposing)
        {
            if (IsAutomationHalted)
            {
                _log.Warn(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=before_wait reservation=KEPT");
                return;
            }

            attempt++;

            _log.Warn(
                $"[AUTO_REPLACE_CLEANUP_BARRIER_WAIT] blockedProfile={profileName} request={request.Id} " +
                $"attempt={attempt} retryIn={AutoReplacementCleanupBarrierRetrySeconds}s mode=HARD_BLOCK_UNTIL_CLEAN");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                replacementProfile: profileName,
                result: "CHỜ DỌN PROFILE BÙ LỖI",
                detail:
                    $"Profile bù {profileName} chưa đóng sạch; queue Tự bù bị khóa an toàn. " +
                    $"Thử dọn lại lượt {attempt} sau {AutoReplacementCleanupBarrierRetrySeconds}s; " +
                    "không mở profile khác cho tới khi xác minh Worker/Chrome đã tắt.");

            await Task.Delay(TimeSpan.FromSeconds(AutoReplacementCleanupBarrierRetrySeconds));

            if (IsAutomationHalted)
            {
                _log.Warn(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=after_wait reservation=KEPT");
                return;
            }

            try
            {
                if (_contexts.TryGetValue(profileName, out var ctx))
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=context attempt={attempt} action=QUEUE_CAN_CONTINUE");
                    return;
                }

                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);

                if (_contexts.TryGetValue(profileName, out var refreshedCtx))
                {
                    await CloseFailedReplacementRuntimeAsync(refreshedCtx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=refreshed_context attempt={attempt} action=QUEUE_CAN_CONTINUE");
                    return;
                }

                var profile = catalog.Profiles.FirstOrDefault(x =>
                    x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    // Không còn metadata ProfilePath/CDP thì KHÔNG được coi là sạch.
                    // Giữ barrier thay vì tái diễn lỗi cũ "profile_missing => release".
                    barrier = new AutoReplacementCleanupBarrierException(
                        profileName,
                        $"Không còn metadata profile {profileName} để xác minh cleanup; giữ barrier an toàn.",
                        barrier);

                    _log.Error(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_PROFILE_MISSING] profile={profileName} request={request.Id} attempt={attempt} action=KEEP_BLOCKED");
                    continue;
                }

                // Context không còn nhưng catalog vẫn có ProfilePath/CDP: cleanup trực
                // tiếp theo identity profile. Khi CIM timeout, helper sẽ resolve PID
                // listener từ CDP port bằng iphlpapi và kill đúng cây chrome.exe.
                await EnsureAutoCloseChromeStoppedByPathAndPortAsync(
                    profileName,
                    profile.ProfilePath,
                    profile.CdpPort,
                    respectEmergencyStop: true);

                ClearAutoCloseExpectedRunning(
                    profileName,
                    "auto_replacement_barrier_path_port_cleanup_done");
                _autoReplacementCleanupProfiles.Remove(profileName);

                _log.Info(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=profile_path_port attempt={attempt} action=QUEUE_CAN_CONTINUE");
                return;
            }
            catch (OperationCanceledException ex)
                when (IsAutomationHalted
                      || ex.Message.StartsWith("EMERGENCY_STOP_", StringComparison.Ordinal))
            {
                _log.Warn(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_ABORT_EMERGENCY] profile={profileName} stage=cleanup detail={ex.Message} reservation=KEPT");
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

            // Quan trọng: KHÔNG Remove cleanup reservation, KHÔNG ClearExpected,
            // KHÔNG MarkFailed rồi tiếp tục queue. Lỗi cleanup phải fail-closed.
            _log.Error(
                $"[AUTO_REPLACE_CLEANUP_BARRIER_STILL_BLOCKED] blockedProfile={profileName} request={request.Id} " +
                $"attempt={attempt} action=KEEP_BLOCKED error={barrier.Message}");
        }
    }


    static bool IsNameSyncPendingOutcome(AutoProfileProcessOutcome outcome)
    {
        // Trường hợp chuẩn sau khi Manager verify: Save Tên/ảnh đã thành công nhưng
        // tên thực tế vẫn chưa khớp sau 3 probe. Đây chính là NAME_SYNC_PENDING:
        // giữ PRF trong hàng chờ để quét lại sau, đồng thời cooldown resolver sẽ
        // xếp outcome này vào nhóm "Login / tên chưa đổi".
        if (outcome.Status.Equals("PAUSED_NAME_NOT_CHANGED", StringComparison.OrdinalIgnoreCase)
            && outcome.Step.Equals("READY_PENDING_NAME", StringComparison.OrdinalIgnoreCase)
            && outcome.RenameSucceeded
            && !outcome.IdentityVerified)
        {
            return true;
        }

        if (!outcome.Step.Equals("RENAME", StringComparison.OrdinalIgnoreCase))
            return false;

        // Giữ tương thích cho các outcome PAUSED_RENAME cũ/khác. CAPTCHA/config là
        // lỗi cần xử lý riêng, không phải trường hợp TikTok Save xong nhưng tên cập
        // nhật chậm. COOLDOWN cũng KHÔNG phải name-sync: TikTok đã từ chối thao tác
        // đổi tên nên sweep chỉ-PROBE sẽ không bao giờ tự sửa được.
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

        // Chỉ nhánh START/Worker/runtime mới đi vào stabilization. Auto Replace thường
        // vẫn bị chặn bởi deadline wall-clock 5 phút/candidate; THAY ALL force-new giữ
        // grace nội bộ cũ. CAPTCHA, LOGIN, cấu hình hoặc RENAME xử lý riêng.
        if (!outcome.Step.Equals("START_TOOL", StringComparison.OrdinalIgnoreCase))
            return false;

        if (outcome.Status.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    bool IsAutoReplacementCreateBlockedByReuseOnly(
        AutoReplacementRequest request,
        string stage,
        string profileName = "")
    {
        // QUAN TRỌNG: đọc trực tiếp setting hiện tại, không dùng cờ latch/cache.
        // Vì vậy bật => chặn ngay các CREATE chưa commit; tắt => tự mở lại ngay.
        if (!_autoCloseSettings.ReuseOnlyNoCreateProfile)
            return false;

        request.LastError =
            $"Chế độ CHỈ PRF CHỜ: CREATE hard-gate tại {stage}; không tạo profile mới.";

        _log.Warn(
            $"[AUTO_REPLACE_REUSE_ONLY_HARD_GATE] id={request.Id} closed={request.ClosedProfileName} " +
            $"stage={stage} profile={profileName} action=BLOCK_NEW_CREATE");
        return true;
    }

    bool IsAutoReplacementCreateBlockedByDailyReplaceAll(
        AutoReplacementRequest request,
        string stage,
        string profileName = "")
    {
        if (!IsRunStrategyDailyReplaceAllAutoCreateBlocked())
            return false;

        request.LastError =
            $"THAY ALL: CREATE hard-gate tại {stage}; Tự bù chỉ được quét/mở PRF chờ cho tới giờ thay tiếp theo.";

        _log.Warn(
            $"[AUTO_REPLACE_THAY_ALL_CREATE_HARD_GATE] id={request.Id} closed={request.ClosedProfileName} " +
            $"stage={stage} profile={profileName} action=BLOCK_NEW_CREATE_REUSE_ONLY");
        return true;
    }

    bool IsAutoReplacementCreateBlockedBySchedule(
        AutoReplacementRequest request,
        string stage,
        string profileName = "")
    {
        if (!TryGetAutoReplacementNoCreateScheduleBlock(
                out var nextAllowedLocal,
                out var windowText))
        {
            return false;
        }

        request.LastError =
            $"Khung giờ cấm CREATE: {windowText}; chờ đến {nextAllowedLocal:dd/MM HH:mm}. stage={stage}.";

        _log.Warn(
            $"[AUTO_CREATE_SCHEDULE_HARD_GATE] id={request.Id} closed={request.ClosedProfileName} " +
            $"stage={stage} profile={profileName} window={windowText} nextLocal={nextAllowedLocal:O} action=BLOCK_NEW_CREATE");

        return true;
    }

    async Task<(bool Filled, bool BlockCreate, string Detail)>
        DrainAllReusableProfilesBeforeCreateAsync(
            AutoReplacementRequest request,
            int executionGeneration,
            CancellationToken executionToken)
    {
        // Hard gate cuối trước MỌI CREATE (kể cả FRESH/PRIME).
        // Mục tiêu: CREATE chỉ được phép khi đã xác minh và vét hết PRF chờ hiện có.
        // Nếu refresh hàng chờ lỗi / trạng thái còn mơ hồ thì fail-closed: giữ slot,
        // retry ngắn; tuyệt đối không tiêu account mới.
        const int maxPasses = 3;

        for (var pass = 1; pass <= maxPasses; pass++)
        {
            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return (false, true, "execution không còn được phép");

            executionToken.ThrowIfCancellationRequested();

            SetAutoReplacementUiPhase(
                "VÉT PRF CHỜ TRƯỚC CREATE",
                $"lượt {pass}/{maxPasses}",
                request.Id);

            var refreshOk = await RefreshReusableProfileQueueAsync(
                $"before_create_hard_gate_pass_{pass}",
                executionToken);

            if (!refreshOk)
            {
                var detail =
                    $"Không xác minh được hàng chờ ở lượt {pass}; chặn CREATE để tránh tạo mới khi vẫn còn PRF có thể dùng.";
                _log.Warn(
                    $"[AUTO_REPLACE_REUSE_DRAIN_REFRESH_FAILED] id={request.Id} closed={request.ClosedProfileName} pass={pass} action=BLOCK_CREATE");
                return (false, true, detail);
            }

            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return (false, true, "execution không còn được phép sau refresh");

            executionToken.ThrowIfCancellationRequested();

            // Lane thường: hàm này tự đi lần lượt TOÀN BỘ candidate chưa attempted
            // trong snapshot, không dừng ở candidate lỗi cứng hợp lệ.
            bool filled;
            try
            {
                filled = await TryUseReusableProfileQueueAsync(
                    request,
                    executionGeneration,
                    executionToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AutoReplacementCleanupBarrierException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail =
                    $"Lỗi khi vét PRF chờ thường ở lượt {pass}: {ex.Message}";
                _log.Warn(
                    $"[AUTO_REPLACE_REUSE_DRAIN_NORMAL_ERROR] id={request.Id} closed={request.ClosedProfileName} pass={pass} action=BLOCK_CREATE error={ex.Message}");
                return (false, true, detail);
            }

            if (filled)
            {
                _log.Info(
                    $"[AUTO_REPLACE_REUSE_DRAIN_FILLED] id={request.Id} closed={request.ClosedProfileName} pass={pass} lane=normal action=SKIP_CREATE");
                return (true, false, "Đã dùng PRF chờ thường.");
            }

            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return (false, true, "execution không còn được phép sau lane thường");

            executionToken.ThrowIfCancellationRequested();

            // Lane NAME_SYNC_PENDING vẫn phải được xét trước CREATE.
            try
            {
                filled = await TryRecoverNameSyncPendingReusableProfilesOnceAsync(
                    request,
                    executionGeneration,
                    executionToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AutoReplacementCleanupBarrierException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail =
                    $"Lỗi khi vét PRF chờ đồng bộ tên ở lượt {pass}: {ex.Message}";
                _log.Warn(
                    $"[AUTO_REPLACE_REUSE_DRAIN_NAME_SYNC_ERROR] id={request.Id} closed={request.ClosedProfileName} pass={pass} action=BLOCK_CREATE error={ex.Message}");
                return (false, true, detail);
            }

            if (filled)
            {
                _log.Info(
                    $"[AUTO_REPLACE_REUSE_DRAIN_FILLED] id={request.Id} closed={request.ClosedProfileName} pass={pass} lane=name_sync action=SKIP_CREATE");
                return (true, false, "Đã dùng PRF chờ đồng bộ tên.");
            }

            // Refresh lần cuối của pass để bắt PRF vừa trở thành eligible trong lúc sweep.
            // Nếu xuất hiện candidate mới chưa attempted thì lặp ngay một pass nữa thay vì CREATE.
            refreshOk = await RefreshReusableProfileQueueAsync(
                $"before_create_hard_gate_verify_{pass}",
                executionToken);

            if (!refreshOk)
            {
                var detail =
                    $"Không xác minh được hàng chờ sau lượt vét {pass}; chặn CREATE.";
                _log.Warn(
                    $"[AUTO_REPLACE_REUSE_DRAIN_VERIFY_FAILED] id={request.Id} closed={request.ClosedProfileName} pass={pass} action=BLOCK_CREATE");
                return (false, true, detail);
            }

            if (!TryFindUntestedEligibleReusableProfile(
                    request,
                    out var pendingProfile,
                    out var pendingLane,
                    out var pendingDetail))
            {
                _log.Info(
                    $"[AUTO_REPLACE_REUSE_DRAIN_EXHAUSTED] id={request.Id} closed={request.ClosedProfileName} pass={pass} attemptedProfiles={GetAutoReplacementAttemptedProfileCount(request)} action=ALLOW_CREATE");
                return (false, false, "Đã vét hết PRF chờ đủ điều kiện.");
            }

            _log.Info(
                $"[AUTO_REPLACE_REUSE_DRAIN_CONTINUE] id={request.Id} closed={request.ClosedProfileName} pass={pass} profile={pendingProfile} lane={pendingLane} detail={pendingDetail}");
        }

        if (TryFindUntestedEligibleReusableProfile(
                request,
                out var blockedProfile,
                out var blockedLane,
                out var blockedDetail))
        {
            return (
                false,
                true,
                $"Còn PRF chờ chưa thử: {blockedProfile} ({blockedLane}). {blockedDetail}");
        }

        // Nếu queue biến động quá nhanh nhưng cuối cùng không còn candidate chưa thử,
        // CREATE được phép. Mọi lỗi refresh phía trên đều đã fail-closed.
        return (false, false, "Đã vét hết PRF chờ đủ điều kiện.");
    }

    async Task<bool> TryCreateReplacementAsync(
        AutoReplacementRequest request,
        int executionGeneration,
        CancellationToken executionToken,
        bool forceNewProfileOnly = false)
    {
        if (request.LastError.StartsWith(
                "Chế độ CHỈ PRF CHỜ: CREATE hard-gate",
                StringComparison.OrdinalIgnoreCase)
            || request.LastError.StartsWith(
                "Khung giờ cấm CREATE:",
                StringComparison.OrdinalIgnoreCase)
            || request.LastError.StartsWith(
                "PRF chờ chưa vét xong:",
                StringComparison.OrdinalIgnoreCase)
            || request.LastError.StartsWith(
                "THAY ALL: CREATE hard-gate",
                StringComparison.OrdinalIgnoreCase))
        {
            request.LastError = "";
        }

        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByReuseOnly(request, "entry"))
            return false;

        if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByDailyReplaceAll(request, "entry"))
            return false;

        if (IsAutoReplacementCreateBlockedBySchedule(request, "entry"))
            return false;

        executionToken.ThrowIfCancellationRequested();

        // PRE-GATE: vét PRF chờ TRƯỚC cả _autoProfileQueueGate và cooldown CREATE.
        // Đây là điểm quan trọng với Run Strategy FRESH/PRIME: PRF đã có sẵn + login
        // phải được mở ngay, tuyệt đối không bị bắt chờ cooldown vốn chỉ dành cho CREATE mới.
        if (!forceNewProfileOnly)
        {
            var preGateReuse = await DrainAllReusableProfilesBeforeCreateAsync(
                request,
                executionGeneration,
                executionToken);

            if (preGateReuse.Filled)
                return true;

            if (preGateReuse.BlockCreate)
            {
                request.LastError =
                    "PRF chờ chưa vét xong: " + preGateReuse.Detail;

                _log.Warn(
                    $"[AUTO_REPLACE_REUSE_DRAIN_BLOCK_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                    $"stage=before_auto_profile_gate detail={preGateReuse.Detail}");
                return false;
            }
        }
        else
        {
            _log.Info(
                $"[AUTO_REPLACE_FORCE_NEW_PROFILE] id={request.Id} closed={request.ClosedProfileName} stage=before_auto_profile_gate action=SKIP_REUSE_DRAIN");
        }

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

            if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByReuseOnly(request, "after_auto_profile_gate"))
                return false;

            if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByDailyReplaceAll(request, "after_auto_profile_gate"))
                return false;

            if (IsAutoReplacementCreateBlockedBySchedule(request, "after_auto_profile_gate"))
                return false;

            executionToken.ThrowIfCancellationRequested();
            // Một suất Tự bù cần đạt đúng 1 profile RUNNING khỏe. BAN/lỗi/skip
            // vẫn được thử tiếp nhưng bị chặn bởi cầu chì CREATE user cấu hình (mặc định 3/slot).
            // Giữ danh sách account đã thử trong chính suất này để account vừa lỗi
            // nhưng được ReleaseAccount() không bị lấy lại ngay và gây vòng lặp.
            var attemptedAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempt = 0;

            while (!_closing
                   && IsAutoReplacementExecutionAllowed(executionGeneration))
            {
                executionToken.ThrowIfCancellationRequested();

                // Hard gate LIVE ở đầu MỖI vòng account. Nếu user vừa bật
                // "Chỉ dùng PRF chờ" thì dừng trước khi đụng account mới.
                if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByReuseOnly(request, "before_candidate_loop"))
                    return false;

                if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByDailyReplaceAll(request, "before_candidate_loop"))
                    return false;

                if (IsAutoReplacementCreateBlockedBySchedule(request, "before_candidate_loop"))
                    return false;

                // NAME_SYNC_PENDING đã được vét ở tầng ngoài TRƯỚC khi vào hàm này.
                // Sau một CREATE thật thất bại, hàm này sẽ RETURN ra tầng ngoài thay vì
                // CREATE liên tiếp account kế tiếp. Request chờ đúng cooldown, reset vòng
                // attempted rồi vét lại toàn bộ PRF chờ trước khi được vào đây lần nữa.
                // Chỉ các candidate SKIP trước khi mở runtime mới được phép continue nội bộ.
                var startName = DetectNextAutoProfileName();

                // THAY ALL có target CREATE riêng trong cửa sổ refresh hằng ngày.
                // Pool kiểm tra của Auto Replace thường (tối đa 3) và giới hạn nâng cao
                // /giờ,/phiên không được chặn nhánh force-new này; các gate schedule/deadline/cooldown,
                // Global Login, login/name/video/stabilize vẫn giữ nguyên ở pipeline hiện có.
                if (!forceNewProfileOnly
                    && TryGetAutoReplacementCreateLimitBlock(
                        request,
                        out var preCreateLimitReason,
                        out _,
                        out _,
                        out _))
                {
                    _log.Warn(
                        $"[AUTO_CREATE_LIMIT_BLOCK_BEFORE_COOLDOWN] id={request.Id} closed={request.ClosedProfileName} " +
                        $"profile={startName} reason={preCreateLimitReason}");
                    return false;
                }

                // Chờ cooldown TRƯỚC khi BuildAutoProfileQueue. BuildAutoProfileQueue có
                // side effect ASSIGN account ngay, nên tuyệt đối không giữ account mới
                // trong lúc chỉ đang chờ cooldown 4'/7'/15'.
                var createCooldownCompleted = await WaitAutoReplacementCreateCooldownBeforeNewProfileAsync(
                    request,
                    startName,
                    executionGeneration,
                    executionToken);

                if (!createCooldownCompleted)
                    return false;

                // User có thể bật CHỈ PRF CHỜ trong lúc await cooldown. Chặn lại ngay
                // trước điểm COMMIT account. Khi user tắt lại, chốt này đọc live=false
                // và CREATE được phép tiếp tục, không có cờ block bị latch.
                if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByReuseOnly(request, "after_cooldown_before_account_assign", startName))
                    return false;

                if (!forceNewProfileOnly && IsAutoReplacementCreateBlockedByDailyReplaceAll(request, "after_cooldown_before_account_assign", startName))
                    return false;

                if (IsAutoReplacementCreateBlockedBySchedule(request, "after_cooldown_before_account_assign", startName))
                    return false;

                // HARD GATE: ngay trước khi ASSIGN account/CREATE, vét lại TOÀN BỘ hàng chờ.
                // Nhánh FRESH/PRIME không được phép bỏ qua PRF chờ đang eligible.
                if (!forceNewProfileOnly)
                {
                    var preCreateReuse = await DrainAllReusableProfilesBeforeCreateAsync(
                        request,
                        executionGeneration,
                        executionToken);

                    if (preCreateReuse.Filled)
                        return true;

                    if (preCreateReuse.BlockCreate)
                    {
                        request.LastError =
                            "PRF chờ chưa vét xong: " + preCreateReuse.Detail;

                        _log.Warn(
                            $"[AUTO_REPLACE_REUSE_DRAIN_BLOCK_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                            $"profile={startName} detail={preCreateReuse.Detail}");
                        return false;
                    }
                }

                var reuseOnlyBlockedAtAccountCommit = false;
                var dailyReplaceAllBlockedAtAccountCommit = false;
                var scheduleBlockedAtAccountCommit = false;
                var queue = await RunAccountPoolIoAsync(
                    () =>
                    {
                        // Tự bù cũng phải đọc Excel mới nhất trước khi chọn account.
                        // Nếu người dùng vừa note BAN / AutoPrf=DONE thì không lấy lại
                        // account đó chỉ vì catalog JSON vẫn còn snapshot cũ.
                        if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                            _accountPoolService.ReloadCurrentExcel();
                        _accountPoolService.EnsureAutoColumns();

                        // Khung giờ được kiểm tra sát điểm ASSIGN account. Nếu giờ cấm
                        // vừa bắt đầu trong lúc await I/O/cooldown thì không được lấy account mới.
                        if (TryGetAutoReplacementNoCreateScheduleBlock(
                                out _,
                                out _))
                        {
                            scheduleBlockedAtAccountCommit = true;
                            return new List<AutoProfileQueueItem>();
                        }

                        // Atomic COMMIT với thao tác bật/tắt checkbox: nếu checkbox ON
                        // giành lock trước thì KHÔNG gọi BuildAutoProfileQueue => không
                        // ASSIGN account. Nếu Build đã giành lock trước thì candidate đó
                        // được xem là transaction đã bắt đầu; vòng kế tiếp vẫn bị chặn.
                        lock (_autoReplacementCreateModeGate)
                        {
                            if (!forceNewProfileOnly
                                && _autoCloseSettings.ReuseOnlyNoCreateProfile)
                            {
                                reuseOnlyBlockedAtAccountCommit = true;
                                return new List<AutoProfileQueueItem>();
                            }

                            if (!forceNewProfileOnly
                                && IsRunStrategyDailyReplaceAllAutoCreateBlocked())
                            {
                                dailyReplaceAllBlockedAtAccountCommit = true;
                                return new List<AutoProfileQueueItem>();
                            }

                            return BuildAutoProfileQueue(
                                requestedNew: 1,
                                requestedStartName: startName,
                                resumeIncomplete: false,
                                retryPaused: false);
                        }
                    },
                    executionToken);

                if (reuseOnlyBlockedAtAccountCommit)
                {
                    request.LastError =
                        "Chế độ CHỈ PRF CHỜ: CREATE hard-gate tại account_assign_commit; không tạo profile mới.";

                    _log.Warn(
                        $"[AUTO_REPLACE_REUSE_ONLY_HARD_GATE] id={request.Id} closed={request.ClosedProfileName} " +
                        $"stage=account_assign_commit profile={startName} action=BLOCK_NEW_CREATE");
                    return false;
                }

                if (dailyReplaceAllBlockedAtAccountCommit)
                {
                    IsAutoReplacementCreateBlockedByDailyReplaceAll(
                        request,
                        "account_assign_commit",
                        startName);
                    return false;
                }

                if (scheduleBlockedAtAccountCommit)
                {
                    IsAutoReplacementCreateBlockedBySchedule(
                        request,
                        "account_assign_commit",
                        startName);
                    return false;
                }

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

                if (!forceNewProfileOnly
                    && IsAutoReplacementCreateBlockedByDailyReplaceAll(
                        request,
                        "before_create_commit",
                        item.ProfileName))
                {
                    return false;
                }

                // Re-check + reserve quota ngay sát CREATE thật. Đây là chốt chống
                // CREATE vượt quota; ngoài ra sau đúng 1 profile CREATE thật bị lỗi,
                // hàm sẽ return ra tầng ngoài để cooldown + vét lại toàn bộ PRF chờ.
                if (!forceNewProfileOnly)
                {
                    if (!TryReserveAutoReplacementCreateAttempt(
                            request,
                            item.ProfileName,
                            out var createLimitBlockReason))
                    {
                        _log.Warn(
                            $"[AUTO_CREATE_LIMIT_BLOCK_INSIDE_CREATE] id={request.Id} closed={request.ClosedProfileName} " +
                            $"profile={item.ProfileName} reason={createLimitBlockReason}");
                        return false;
                    }
                }
                else
                {
                    _log.Info(
                        $"[RUN_DAILY_REPLACE_ALL_CREATE_LIMIT_BYPASS] outgoing={request.ClosedProfileName} " +
                        $"profile={item.ProfileName} scope=AUTO_REPLACE_GENERIC_LIMITS action=ALLOW_FORCE_NEW");
                }

                attemptedAccountIds.Add(item.Account.Id);
                attempt++;

                // Đánh dấu TRƯỚC khi mở/tạo runtime. Nếu profile này rơi vào
                // NAME_SYNC_PENDING thì các RETRY của cùng suất không được mở lại.
                MarkAutoReplacementProfileAttempted(
                    request,
                    item.ProfileName,
                    "create_new");

                _autoReplacementClaimedProfiles.Add(item.ProfileName);

                using var candidateTimeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(executionToken);
                if (!forceNewProfileOnly)
                    candidateTimeoutCts.CancelAfter(AutoReplacementCandidateCheckTimeout);
                var candidateToken = forceNewProfileOnly
                    ? executionToken
                    : candidateTimeoutCts.Token;

                if (!forceNewProfileOnly)
                {
                    _log.Info(
                        $"[AUTO_CHECK_BEGIN] request={request.Id} profile={item.ProfileName} source=create_new " +
                        $"timeout={AutoReplacementCandidateCheckTimeout:c} checking={GetAutoReplacementCheckPoolSnapshot().Count}/{AutoReplacementCheckPoolMax}");
                }

                try
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CREATE_BEGIN] closed={request.ClosedProfileName} profile={item.ProfileName} account={item.Account.Username} attempt={attempt} mode=one_created_profile_then_reuse_sweep");

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "BẮT ĐẦU",
                        detail: $"Lần thử {attempt}; mỗi profile CREATE thật lỗi sẽ dừng lượt này, chờ cooldown rồi vét lại toàn bộ PRF chờ trước khi CREATE tiếp.");

                    SetAutoReplacementUiPhase(
                        "TẠO PRF MỚI",
                        item.ProfileName,
                        request.Id);

                    var outcome = await ProcessAutoProfileQueueItemAsync(
                        item,
                        autoRename: true,
                        autoVideo: LoadAutoProfileBehaviorSettings().AutoVideoEnabled,
                        autoStart: true,
                        isPaused: static () => false,
                        ct: candidateToken,
                        ui: (step, result, _) =>
                        {
                            SetAutoReplacementUiPhase(
                                "TẠO PRF MỚI",
                                $"{item.ProfileName} · {step}",
                                request.Id);

                            _log.Info(
                                $"[AUTO_REPLACE_CREATE_PROGRESS] profile={item.ProfileName} step={step} result={result}");
                        },
                        // Auto Replace dùng đúng một tầng verify ở Manager: Worker chỉ Save/Confirm
                        // rồi trả về; Manager probe tên thực tế tối đa 3 lần. Nếu Save đã thành công
                        // nhưng tên chưa khớp thì trả PAUSED_NAME_NOT_CHANGED để dùng cooldown
                        // "Login / tên chưa đổi" và vẫn đưa PRF vào lane chờ NAME_SYNC_PENDING.
                        verifyIdentityAfterRename: true,
                        writeIdentityDoneToExcel: true,
                        tolerateIdentityValidationFailure: true,
                        requireIdentityMatchForSuccess: true);

                    if (IsManualCloseSuppressed(item.ProfileName))
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=after_process");
                        await CleanupCreatedReplacementAttemptAsync(
                            item.ProfileName,
                            "manual_close_after_process");
                        ReleaseAutoReplacementCheckPoolProfile(
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
                                candidateToken);

                        if (!healthy)
                        {
                            if (IsManualCloseSuppressed(item.ProfileName))
                            {
                                _log.Warn(
                                    $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=healthy_wait");
                                await CleanupCreatedReplacementAttemptAsync(
                                    item.ProfileName,
                                    "manual_close_during_healthy_wait");
                                ReleaseAutoReplacementCheckPoolProfile(
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
                            ArmAutoReplacementReuseSweepBeforeNextCreate(
                                request,
                                item.ProfileName,
                                "created_started_but_not_healthy");
                            return false;
                        }

                        // Profile tạo mới chỉ được coi là ĐÃ TREO sau khi RUNNING khỏe.
                        MarkProfileSupplyState(item.ProfileName, "used", "auto_replacement_created_running_confirmed");
                        ReleaseAutoReplacementCheckPoolProfile(
                            item.ProfileName,
                            "created_running_confirmed");

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
                        //    đã có thể mở Chrome + Worker. Với Tự thay phải đóng runtime này trước,
                        //    giữ profile non-BAN, cooldown rồi vét lại hàng chờ trước CREATE kế tiếp.
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
                            // Không coi đây là lỗi tài khoản; candidate này không còn thuộc pool.
                            ReleaseAutoReplacementCheckPoolProfile(
                                item.ProfileName,
                                "create_skip_excel_or_active_guard");
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
                        // sau cooldown, PRF này cùng toàn bộ hàng chờ sẽ được vét lại trước CREATE mới.
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
                        ArmAutoReplacementReuseSweepBeforeNextCreate(
                            request,
                            item.ProfileName,
                            "paused_nonban");
                        return false;
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
                                $"step={outcome.Step} action=STABILIZE_WITH_5M_CANDIDATE_DEADLINE");

                            var stabilization = await StabilizeReplacementRuntimeAsync(
                                stabilizeCtx,
                                request,
                                "created_start_recovery",
                                executionGeneration,
                                candidateToken);

                            if (IsManualCloseSuppressed(item.ProfileName))
                            {
                                _log.Warn(
                                    $"[AUTO_REPLACE_CREATE_MANUAL_ABORT] closed={request.ClosedProfileName} profile={item.ProfileName} stage=stabilize");
                                await CleanupCreatedReplacementAttemptAsync(
                                    item.ProfileName,
                                    "manual_close_during_stabilize");
                                ReleaseAutoReplacementCheckPoolProfile(
                                    item.ProfileName,
                                    "manual_close_during_stabilize");
                                return false;
                            }

                            // BAN/TIME có thể xuất hiện ngay trong stabilize. Handler BAN
                            // đã đóng runtime + queue xóa; không được rơi xuống nhánh FAIL
                            // chung vì nhánh đó cleanup/cooldown trước khi finally nhả claim.
                            // Return ngay để finally release _autoReplacementClaimedProfiles.
                            if (IsProfileRetireDeleteBlockedForOpen(item.ProfileName)
                                || (!stabilization.Healthy
                                    && (stabilization.Detail ?? "").StartsWith(
                                        "RETIRE_DELETE_BLOCKED:",
                                        StringComparison.OrdinalIgnoreCase)))
                            {
                                ClearAutoCloseExpectedRunning(
                                    item.ProfileName,
                                    "auto_replace_created_retire_delete_abort");

                                ReleaseAutoReplacementCheckPoolProfile(
                                    item.ProfileName,
                                    "created_retire_delete_abort");

                                _log.Warn(
                                    $"[AUTO_REPLACE_CREATE_ABORT_RETIRE_DELETE] id={request.Id} profile={item.ProfileName} detail={stabilization.Detail} action=RETURN_RELEASE_CLAIM_NO_COOLDOWN");
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
                                ArmAutoReplacementReuseSweepBeforeNextCreate(
                                    request,
                                    item.ProfileName,
                                    "stabilize_name_sync_pending");
                                return false;
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
                                    "auto_replacement_created_recovered_5m");
                                ReleaseAutoReplacementCheckPoolProfile(
                                    item.ProfileName,
                                    "created_recovered_5m");

                                WriteAutoActivityLog(
                                    action: "MỞ PROFILE BÙ",
                                    profile: request.ClosedProfileName,
                                    account: item.Account.Username,
                                    reason: request.Reason,
                                    replacementProfile: item.ProfileName,
                                    result: "THÀNH CÔNG",
                                    detail: "Profile gặp lỗi START ban đầu nhưng đã tự phục hồi trong cửa sổ ổn định.");

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
                        ReleaseAutoReplacementCheckPoolProfile(
                            item.ProfileName,
                            "login_banned");
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
                    ArmAutoReplacementReuseSweepBeforeNextCreate(
                        request,
                        item.ProfileName,
                        "outcome_fail");
                    return false;
                }
                catch (OperationCanceledException)
                    when (!forceNewProfileOnly
                          && candidateTimeoutCts.IsCancellationRequested
                          && !executionToken.IsCancellationRequested
                          && IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[AUTO_CHECK_TIMEOUT_5M] request={request.Id} profile={item.ProfileName} source=create_new " +
                        $"action=CLEANUP_ROTATE checking={GetAutoReplacementCheckPoolSnapshot().Count}/{AutoReplacementCheckPoolMax}");

                    WriteAutoActivityLog(
                        action: "TỰ BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "TIMEOUT 5 PHÚT",
                        detail: "PRF vượt quá 5 phút kiểm tra. Đóng runtime, giữ PRF nếu đã tạo và chuyển sang candidate khác.");

                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            item.ProfileName,
                            "candidate_timeout_5m");
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Error(
                            $"[AUTO_CHECK_TIMEOUT_CLEANUP_ERROR] profile={item.ProfileName} error={cleanupEx}");
                        throw new AutoReplacementCleanupBarrierException(
                            item.ProfileName,
                            $"PRF {item.ProfileName} timeout 5 phút nhưng cleanup chưa hoàn tất.",
                            cleanupEx);
                    }

                    MarkReplacementProfileFailed(
                        item.ProfileName,
                        "candidate_timeout_5m");

                    if (AutoReplacementProfileExistsInCatalog(item.ProfileName))
                    {
                        try
                        {
                            await RefreshReusableProfileQueueAsync(
                                "candidate_timeout_5m",
                                CancellationToken.None);
                        }
                        catch (Exception refreshEx)
                        {
                            _log.Warn(
                                $"[AUTO_CHECK_TIMEOUT_REUSE_REFRESH_WARN] profile={item.ProfileName} error={refreshEx.Message}");
                        }
                    }
                    else
                    {
                        ReleaseAutoReplacementCheckPoolProfile(
                            item.ProfileName,
                            "timeout_before_profile_created");
                    }

                    RegisterAutoReplacementCreateCooldown(
                        null,
                        item.ProfileName,
                        request.Id,
                        "candidate_timeout_5m");
                    ArmAutoReplacementReuseSweepBeforeNextCreate(
                        request,
                        item.ProfileName,
                        "candidate_timeout_5m");
                    return false;
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

                    // Nếu exception xảy ra trước khi profile tồn tại thật thì không được
                    // giữ một slot pool ma. Nếu profile đã tồn tại thì giữ trong pool và
                    // để refresh/cooldown đưa nó về vòng kiểm tra sau.
                    if (!AutoReplacementProfileExistsInCatalog(item.ProfileName))
                    {
                        ReleaseAutoReplacementCheckPoolProfile(
                            item.ProfileName,
                            "exception_before_profile_created:" + ex.GetType().Name);
                    }
                    else
                    {
                        try
                        {
                            await RefreshReusableProfileQueueAsync(
                                "create_exception_keep_check_pool",
                                CancellationToken.None);
                        }
                        catch (Exception refreshEx)
                        {
                            _log.Warn(
                                $"[AUTO_CHECK_POOL_EXCEPTION_REFRESH_WARN] profile={item.ProfileName} error={refreshEx.Message}");
                        }
                    }

                    // Exception ngoài outcome vẫn là một lần CREATE thật đã được thử.
                    // Không có tín hiệu login/BAN đáng tin => dùng cooldown Normal.
                    RegisterAutoReplacementCreateCooldown(
                        null,
                        item.ProfileName,
                        request.Id,
                        "exception:" + ex.GetType().Name);
                    ArmAutoReplacementReuseSweepBeforeNextCreate(
                        request,
                        item.ProfileName,
                        "exception:" + ex.GetType().Name);
                    return false;
                }
                finally
                {
                    var claimReleased = _autoReplacementClaimedProfiles.Remove(item.ProfileName);
                    if (claimReleased && IsProfileRetireDeleteBlockedForOpen(item.ProfileName))
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_CLAIM_RELEASE_RETIRE_DELETE] profile={item.ProfileName} source=create_candidate action=DELETE_JOB_CAN_CONTINUE");
                    }
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
        var setupHoldWasActive = false;
        var graceResetAfterSetupStart = false;

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

        while (!_closing)
        {
            // Candidate có thể bị BAN/TIME trong chính cửa sổ ổn định.
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

            // Thời gian Tên/ảnh -> VIDEO đang giữ ACCOUNT_SETUP_HOLD không được tính
            // vào stabilize grace. Vẫn giữ nguyên poll/recovery hiện có; chỉ chặn việc
            // deadline cũ làm TIMEOUT/cleanup một PRF vừa setup xong. Khi hold được nhả,
            // bắt đầu lại trọn vẹn grace 45s (hoặc grace tương ứng của policy hiện tại).
            var setupHoldActive = IsManagedAccountSetupHoldActive(ctx);
            if (setupHoldActive)
            {
                if (!setupHoldWasActive)
                {
                    _log.Info(
                        $"[AUTO_REPLACE_STABILIZE_SETUP_HOLD] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                        $"gracePaused=true grace={healthyConfirmTimeoutSeconds}s");
                }

                setupHoldWasActive = true;
                healthySinceUtc = null;
            }
            else
            {
                if (setupHoldWasActive)
                {
                    deadlineUtc = DateTime.UtcNow.AddSeconds(healthyConfirmTimeoutSeconds);
                    setupHoldWasActive = false;
                    graceResetAfterSetupStart = true;
                    healthySinceUtc = null;

                    _log.Info(
                        $"[AUTO_REPLACE_STABILIZE_GRACE_RESET] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                        $"reason=account_setup_hold_released grace={healthyConfirmTimeoutSeconds}s deadline={deadlineUtc:O}");
                }

                if (DateTime.UtcNow >= deadlineUtc)
                    break;
            }

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
                    ctx.LastRunActivationUtc = DateTime.UtcNow;
                    _log.Info(
                        $"[AUTO_REPLACE_STABILIZE_OK] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                        $"stable={stableFor:c} recoveryAttempts={recoveryAttempt} activation={ctx.LastRunActivationUtc:O}");
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
                                : $"Đang chờ profile {ctx.Profile.Name} ổn định...");
                    }
                    catch (Exception ex)
                    {
                        lastFault = "reopen_worker:" + ex.Message;
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_REOPEN_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                    }

                    if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                    {
                        ClearAutoCloseExpectedRunning(
                            ctx.Profile.Name,
                            "auto_replace_retire_delete_after_reopen_worker");
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_reopen_worker action=RELEASE_CLAIM");
                        return new AutoReplacementStabilizationResult(
                            false, false, false,
                            "RETIRE_DELETE_BLOCKED: profile bị hard-retired trong lúc mở lại Worker.");
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

                    // OpenChromeForProfileAsync có thể vừa nhận account_banned và đã
                    // đóng runtime. Thoát NGAY trong cùng iteration để finally của caller
                    // nhả _autoReplacementClaimedProfiles; không được rơi xuống START.
                    if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                    {
                        ClearAutoCloseExpectedRunning(
                            ctx.Profile.Name,
                            "auto_replace_retire_delete_after_reopen_chrome");
                        _log.Warn(
                            $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_reopen_chrome action=RELEASE_CLAIM");
                        return new AutoReplacementStabilizationResult(
                            false, false, false,
                            "RETIRE_DELETE_BLOCKED: TikTok xác nhận BAN/retired trong lúc mở Chrome.");
                    }

                    state = GetEffectiveRuntimeState(ctx);
                    chromeConnected = string.Equals(
                        ctx.LastSnapshot?.Chrome,
                        "CONNECTED",
                        StringComparison.OrdinalIgnoreCase);
                }

                if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                {
                    ClearAutoCloseExpectedRunning(
                        ctx.Profile.Name,
                        "auto_replace_retire_delete_before_recovery_start");
                    _log.Warn(
                        $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=before_recovery_start action=RELEASE_CLAIM");
                    return new AutoReplacementStabilizationResult(
                        false, false, false,
                        "RETIRE_DELETE_BLOCKED: profile đã hard-retired trước recovery Start.");
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

                        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name)
                            || string.Equals(reply, "hard_retired", StringComparison.OrdinalIgnoreCase))
                        {
                            ClearAutoCloseExpectedRunning(
                                ctx.Profile.Name,
                                "auto_replace_retire_delete_after_recovery_start");
                            _log.Warn(
                                $"[AUTO_REPLACE_STABILIZE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_recovery_start reply={reply} action=RELEASE_CLAIM");
                            return new AutoReplacementStabilizationResult(
                                false, false, false,
                                "RETIRE_DELETE_BLOCKED: profile bị hard-retired trong Name/VIDEO/Start recovery.");
                        }

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

                        // StartWithNameGuardAsync tự Arm HOLD ở đầu và chỉ nhả HOLD sau
                        // Tên/ảnh -> VIDEO trước khi gửi start_auto. Toàn bộ chuỗi này có
                        // thể nằm trong một await nên vòng stabilize không quan sát được
                        // cạnh ON/OFF ở phía trên. Reset đúng một lần sau lần Start hợp lệ
                        // đầu tiên để grace thực sự bắt đầu sau setup, không phải từ lúc mở PRF.
                        if (!graceResetAfterSetupStart
                            && !IsNameGuardTransientStartReply(reply)
                            && !string.Equals(reply, "emergency_stopped", StringComparison.OrdinalIgnoreCase)
                            && !IsManagedAccountSetupHoldActive(ctx))
                        {
                            deadlineUtc = DateTime.UtcNow.AddSeconds(healthyConfirmTimeoutSeconds);
                            graceResetAfterSetupStart = true;
                            setupHoldWasActive = false;
                            healthySinceUtc = null;

                            _log.Info(
                                $"[AUTO_REPLACE_STABILIZE_GRACE_RESET] id={request.Id} profile={ctx.Profile.Name} source={source} " +
                                $"reason=start_with_name_guard_completed grace={healthyConfirmTimeoutSeconds}s deadline={deadlineUtc:O}");
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
            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                _log.Warn(
                    $"[AUTO_REPLACE_PROBE_GRACE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=loop_begin");
                return false;
            }

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

                    if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                    {
                        _log.Warn(
                            $"[AUTO_REPLACE_PROBE_GRACE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_open_worker");
                        return false;
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

                        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                        {
                            _log.Warn(
                                $"[AUTO_REPLACE_PROBE_GRACE_RETIRE_DELETE_ABORT] id={request.Id} profile={ctx.Profile.Name} source={source} stage=after_open_chrome");
                            return false;
                        }

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

    void ArmAutoReplacementReuseSweepBeforeNextCreate(
        AutoReplacementRequest request,
        string profileName,
        string source)
    {
        // CREATE candidate này đã được thử thật trong chính suất bù hiện tại.
        // Nếu profile được giữ lại/đưa vào Chờ dùng lại (ví dụ NAME_SYNC_PENDING),
        // tuyệt đối không để nó tự giữ suất rồi chờ 60s/5p để mở lại trong cùng request.
        MarkAutoReplacementProfileAttempted(
            request,
            profileName,
            "create_failed_same_request:" + source);

        var armed = false;

        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            var target = live ?? request;
            if (!target.ReuseSweepBeforeNextCreatePending)
            {
                target.ReuseSweepBeforeNextCreatePending = true;
                armed = true;
            }

            if (live is not null)
                SaveAutoReplacementQueueUnsafe();
        }

        if (armed)
        {
            _log.Info(
                $"[AUTO_REPLACE_CREATE_REUSE_SWEEP_ARMED] id={request.Id} closed={request.ClosedProfileName} " +
                $"profile={profileName} source={source} action=KEEP_ATTEMPTED_TRY_ONLY_NEW_REUSE");
        }
    }

    void PrepareAutoReplacementReuseSweepBeforeNextCreateIfDue(
        AutoReplacementRequest request)
    {
        var nowUtc = DateTime.UtcNow;
        var preservedAttempted = 0;
        DateTime? createDeadlineUtc = null;
        var started = false;

        lock (_autoReplacementQueueLock)
        {
            var live = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));

            var target = live ?? request;
            if (!target.ReuseSweepBeforeNextCreatePending)
                return;

            createDeadlineUtc = target.CreateNotBeforeUtc;

            // Nếu WakeAutoReplacementForReusableSupply đánh thức request sớm trong
            // cooldown thì giữ nguyên cờ này. Đến đúng deadline CREATE mới bắt đầu
            // một VÒNG QUÉT TOÀN BỘ hàng chờ trước CREATE kế tiếp.
            if (createDeadlineUtc.HasValue
                && createDeadlineUtc.Value > nowUtc)
            {
                return;
            }

            target.AttemptedProfiles ??= new List<string>();
            preservedAttempted = target.AttemptedProfiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            // Policy mới: trước mỗi CREATE phải quét lại TOÀN BỘ hàng chờ, kể cả
            // NAME_SYNC_PENDING đã được probe ở vòng trước. Các profile lỗi cứng vẫn
            // tự bị lọc bởi retired/cooldown/failed-state nên không tạo vòng lặp nguy hiểm.
            target.AttemptedProfiles.Clear();
            target.ReuseSweepBeforeNextCreatePending = false;
            started = true;

            if (live is not null)
                SaveAutoReplacementQueueUnsafe();
        }

        if (started)
        {
            _log.Info(
                $"[AUTO_REPLACE_CREATE_REUSE_SWEEP_ROUND_BEGIN] id={request.Id} closed={request.ClosedProfileName} " +
                $"clearedAttempted={preservedAttempted} createDeadline={createDeadlineUtc:O} " +
                "action=RESCAN_ALL_REUSE_BEFORE_NEXT_CREATE");
        }
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

    bool SuppressStaleFaultReplacementIfSourceRecovered(
        AutoReplacementRequest request)
    {
        if (!request.RequiresSourceCleanup
            || !string.Equals(
                NormalizeAutoCloseReason(request.Reason),
                "FAULT_10M",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var profileName = (request.ClosedProfileName ?? "").Trim();
        if (profileName.Length == 0
            || !_contexts.TryGetValue(profileName, out var ctx))
        {
            return false;
        }

        // Nếu profile vừa đi vào một vòng đời cứng mới (BAN/TIME/Tự xóa) thì
        // request FAULT cũ không được phép gỡ retired của vòng đời mới đó.
        if (_autoCloseInProgressProfiles.Contains(profileName)
            || IsProfileRetireDeleteBlockedForOpen(profileName))
        {
            return false;
        }

        // Activation marker CHỈ được ghi bởi START/RESUME thành công hoặc một lượt
        // Stabilize xác nhận khỏe. Vì vậy marker sau QueuedUtc là bằng chứng profile
        // đã có một đời runtime mới sau khi FAULT request cũ được sinh ra.
        // Cho phép một tolerance nhỏ vì Capacity/Reuse có thể xác nhận START ngay
        // trước khi callback FAULT cũ kịp persist request. Một activation cách queue
        // vài giây vẫn thuộc cùng đời runtime mới; activation cũ hàng phút/giờ thì không.
        var activationFreshEnough = request.QueuedUtc - TimeSpan.FromSeconds(10);
        if (ctx.LastRunActivationUtc == DateTime.MinValue
            || ctx.LastRunActivationUtc <= activationFreshEnough)
        {
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var state = GetEffectiveRuntimeState(ctx);
        if (!IsAutoCloseHealthyRunning(ctx, state, nowUtc))
            return false;

        RemoveAutoReplacementRequest(request.Id);
        ClearAutoReplacementUiPhase(request.Id);

        // FAULT_10M chỉ là soft-retire. Khi runtime đã thật sự hồi phục thì gỡ cờ
        // retired để profile đang khỏe không bị các vòng supply sau coi là profile chết.
        _autoReplacementRetiredProfiles.Remove(profileName);
        MarkProfileSupplyState(
            profileName,
            "used",
            "fault_request_stale_runtime_recovered");

        _log.Warn(
            $"[AUTO_REPLACE_STALE_FAULT_SUPPRESSED] id={request.Id} profile={profileName} " +
            $"queued={request.QueuedUtc:O} activation={ctx.LastRunActivationUtc:O} state={state} action=KEEP_HEALTHY_RUNTIME");

        WriteAutoActivityLog(
            action: "SUẤT BÙ",
            profile: profileName,
            reason: request.Reason,
            result: "BỎ QUA - REQUEST CŨ",
            detail:
                $"FAULT_10M được tạo lúc {request.QueuedUtc:O}, nhưng profile đã được Start/Resume lại và RUNNING khỏe lúc {ctx.LastRunActivationUtc:O}; giữ runtime hiện tại, không cleanup/đóng lại.");

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

    void ScheduleAutoReplacementNoCreateScheduleRetry(
        string requestId,
        string lastError)
    {
        // Đọc lại LIVE setting ngay lúc schedule retry. Nếu user vừa tắt/đổi
        // khung giờ khiến hiện tại không còn bị chặn, retry gần như ngay và
        // tuyệt đối không giữ một cờ block cũ.
        if (!TryGetAutoReplacementNoCreateScheduleBlock(
                out var nextAllowedLocal,
                out var windowText))
        {
            ScheduleAutoReplacementOperationalRetry(
                requestId,
                lastError,
                TimeSpan.FromMilliseconds(250));
            return;
        }

        var nextUtc = nextAllowedLocal.ToUniversalTime();
        var nowUtc = DateTime.UtcNow;
        if (nextUtc <= nowUtc)
            nextUtc = nowUtc.AddMilliseconds(250);

        var clearedAttempted = 0;
        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.LastError = string.IsNullOrWhiteSpace(lastError)
                ? $"Khung giờ cấm CREATE: {windowText}; chờ đến {nextAllowedLocal:dd/MM HH:mm}."
                : lastError.Trim();

            // Không tính giờ cấm là CREATE fail và không tăng AttemptCount/quota.
            // Sau nhiều giờ chờ, cho quét lại toàn bộ PRF chờ trước khi cân nhắc CREATE.
            request.AttemptedProfiles ??= new List<string>();
            clearedAttempted = request.AttemptedProfiles.Count;
            request.AttemptedProfiles.Clear();
            request.NextAttemptUtc = nextUtc;
            SaveAutoReplacementQueueUnsafe();
        }

        _log.Warn(
            $"[AUTO_CREATE_SCHEDULE_WAIT] id={requestId} window={windowText} nextLocal={nextAllowedLocal:O} " +
            $"clearedAttempted={clearedAttempted} countedAsCreateFail=false");
    }

    void NotifyAutoReplacementNoCreateScheduleSettingsChanged(string source)
    {
        if (!_autoReplacementFeatureInitialized)
            return;

        var isBlocked = TryGetAutoReplacementNoCreateScheduleBlock(
            out var nextAllowedLocal,
            out var windowText);

        var nowUtc = DateTime.UtcNow;
        var targetUtc = isBlocked
            ? nextAllowedLocal.ToUniversalTime()
            : nowUtc;

        if (targetUtc < nowUtc)
            targetUtc = nowUtc;

        var touched = 0;
        var woke = 0;

        lock (_autoReplacementQueueLock)
        {
            foreach (var request in _autoReplacementQueue)
            {
                if (!request.LastError.StartsWith(
                        "Khung giờ cấm CREATE:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                touched++;
                request.NextAttemptUtc = targetUtc;
                if (!isBlocked)
                    woke++;
            }

            if (touched > 0)
                SaveAutoReplacementQueueUnsafe();
        }

        _log.Info(
            $"[AUTO_CREATE_SCHEDULE_SETTINGS_APPLIED] source={source} blockedNow={isBlocked} " +
            $"window={windowText} touched={touched} woke={woke} nextLocal={(isBlocked ? nextAllowedLocal.ToString("O") : "-")}");

        if (woke > 0)
            _ = RunAutoReplacementQueueAsync();
    }

    void NotifyAutoReplacementReuseOnlySettingChanged(
        bool previousValue,
        bool currentValue,
        string source)
    {
        if (previousValue == currentValue)
            return;

        _log.Info(
            $"[AUTO_REPLACE_REUSE_ONLY_SETTING_CHANGED] source={source} previous={previousValue} current={currentValue}");

        // BẬT: không cancel/đóng cưỡng bức candidate đã qua điểm COMMIT account;
        // mọi candidate CHƯA commit sẽ bị các hard-gate live chặn. Vòng account kế
        // tiếp cũng bị chặn, nên không thể tiếp tục tạo hàng loạt.
        if (currentValue)
            return;

        // TẮT: đánh thức NGAY các request đang ngủ 5 phút chỉ vì chế độ CHỈ PRF CHỜ.
        // Đây là phần chống lỗi "đã tắt nhưng vẫn bị chặn tạo". Không đụng các timer
        // cleanup/create-cooldown thật; những timer đó vẫn phải được tôn trọng.
        var now = DateTime.UtcNow;
        var woke = 0;

        lock (_autoReplacementQueueLock)
        {
            foreach (var request in _autoReplacementQueue)
            {
                if (request.NextAttemptUtc <= now)
                    continue;

                if (!request.LastError.StartsWith(
                        "Chế độ CHỈ PRF CHỜ",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                request.NextAttemptUtc = now;
                woke++;
            }

            if (woke > 0)
                SaveAutoReplacementQueueUnsafe();
        }

        _log.Info(
            $"[AUTO_REPLACE_REUSE_ONLY_DISABLED_WAKE] source={source} woke={woke} action=ALLOW_CREATE_FALLBACK");

        if (woke > 0)
            _ = RunAutoReplacementQueueAsync();
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

    void ScheduleAutoReplacementDailyReplaceAllReuseRetry(string requestId, string lastError)
    {
        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.AttemptCount++;
            request.LastError = (lastError ?? "").Trim();
            request.AttemptedProfiles ??= new List<string>();
            var previousAttempted = request.AttemptedProfiles.Count;
            request.AttemptedProfiles.Clear();

            var delay = TimeSpan.FromMinutes(2);
            request.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            SaveAutoReplacementQueueUnsafe();

            _log.Warn(
                $"[AUTO_REPLACE_THAY_ALL_REUSE_ROUND] id={request.Id} closed={request.ClosedProfileName} " +
                $"round={request.AttemptCount} clearedAttempted={previousAttempted} retryIn={delay:c} next={request.NextAttemptUtc:O} action=REUSE_ALL_NO_CREATE");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                result: "CHỜ PRF",
                detail: $"THAY ALL: đã quét hết PRF chờ ({previousAttempted} PRF). Nghỉ 2 phút rồi quét lại toàn bộ; không CREATE mới ngoài giờ thay.");
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

                // Chỉ đánh thức request đang chờ nhánh tạo mới HOẶC đang chờ hết
                // khung giờ cấm CREATE. PRF chờ vẫn được phép mở trong giờ cấm.
                var waitingForSchedule =
                    request.LastError.StartsWith(
                        "Khung giờ cấm CREATE:",
                        StringComparison.OrdinalIgnoreCase);

                if (!waitingForSchedule
                    && (!request.CreateNotBeforeUtc.HasValue
                        || request.CreateNotBeforeUtc.Value <= now))
                {
                    continue;
                }

                // V14.3.7: không còn Wake chỉ vì queue có entry. Chỉ Wake khi request
                // này có ÍT NHẤT 1 PRF chưa từng thử và thực sự có thể xét NGAY.
                // PRF đã attempted, đang cooldown, NAME_SYNC chưa đủ tuổi hoặc state
                // OPENING/UNKNOWN không được tự đánh thức request lặp hàng nghìn vòng.
                if (!HasReadyUnattemptedReusableSupplyForWake(
                        request,
                        out var readyProfile,
                        out var readyLane))
                {
                    continue;
                }

                request.NextAttemptUtc = now;
                woke++;

                _log.Info(
                    $"[AUTO_REPLACE_REUSE_SUPPLY_WAKE_READY] id={request.Id} source={source} " +
                    $"profile={readyProfile} lane={readyLane}");
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
            Version = 4,
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

    bool AutoReplacementProfileExistsInCatalog(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        try
        {
            return _profileService.Load().Profiles.Any(x =>
                (x.Name ?? "").Trim().Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // Không đọc được catalog thì fail-closed: coi như profile có thể tồn tại,
            // giữ slot pool để tránh CREATE chồng. Lượt refresh sau sẽ tự reconcile.
            _log.Warn(
                $"[AUTO_CHECK_POOL_CATALOG_PROBE_WARN] profile={profileName} error={ex.Message}");
            return true;
        }
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

        var saved = false;
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
                saved = true;
            }
        }
        catch (Exception ex)
        {
            // State phụ không được phép làm hỏng luồng profile chính.
            _log.Warn(
                $"[AUTO_REPLACE_SUPPLY_STATE_WARN] profile={profileName} state={state} error={ex.Message}");
        }

        // RUNNING khỏe (used) hoặc kết thúc vòng đời (retired) phải nhả ngay slot
        // pool kiểm tra để nếu vẫn thiếu target Tool có thể bổ sung candidate mới.
        if (saved
            && (state.Equals("used", StringComparison.OrdinalIgnoreCase)
                || state.Equals("retired", StringComparison.OrdinalIgnoreCase)))
        {
            ReleaseAutoReplacementCheckPoolProfile(
                profileName,
                "supply_state:" + state + ":" + source);
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
