using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class NightReserveSettings
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public int CreateStartHour { get; set; } = 12;
        public int CreateEndHour { get; set; } = 18;
        public int TargetCount { get; set; } = 3;
    }

    sealed class NightReserveStateDocument
    {
        public int Version { get; set; } = 1;
        public List<string> Profiles { get; set; } = new();
    }

    readonly object _nightReserveLock = new();
    readonly SemaphoreSlim _nightReserveGate = new(1, 1);
    bool _nightReserveInitialized;
    bool _nightReserveTickBusy;
    DateTime _nightReserveNextCheckUtc = DateTime.MinValue;
    DateTime _nightReservePrimaryStableSinceUtc = DateTime.MinValue;
    int _nightReservePrimaryStableTarget;
    bool _nightReservePrimaryRunIntentActive;
    static readonly TimeSpan NightReservePrimaryStableDelay = TimeSpan.FromSeconds(20);
    CancellationTokenSource _nightReserveCts = new();
    int _nightReserveConsecutiveLoginErrors;
    NightReserveSettings _nightReserveSettings = new();
    NightReserveStateDocument _nightReserveState = new();

    string NightReserveSettingsPath
        => Path.Combine(_baseDir, "manager_night_reserve_settings.json");

    string NightReserveStatePath
        => Path.Combine(_baseDir, "manager_night_reserve_profiles.json");

    void InitializeNightReserveFeature()
    {
        if (_nightReserveInitialized)
            return;

        _nightReserveInitialized = true;

        // Dự phòng đêm dùng trực tiếp phân loại MỚI/TB/CŨ của Run Strategy; MỚI + TB
        // trong hàng chờ đều được tính quota dự phòng mềm; CŨ không tính.
        InitializeRunStrategyFeature();
        _nightReserveSettings = LoadNightReserveSettings();
        _nightReserveState = LoadNightReserveState();

        _refreshTimer.Tick += async (_, _) => await CheckNightReserveAsync();
        FormClosing += (_, _) =>
        {
            try { _nightReserveCts.Cancel(); } catch { }
        };
    }

    NightReserveSettings LoadNightReserveSettings()
    {
        try
        {
            if (!File.Exists(NightReserveSettingsPath))
                return NormalizeNightReserveSettings(new NightReserveSettings());

            var loaded = JsonSerializer.Deserialize<NightReserveSettings>(
                File.ReadAllText(NightReserveSettingsPath, Encoding.UTF8));

            return NormalizeNightReserveSettings(
                loaded ?? new NightReserveSettings());
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_SETTINGS_READ] error={ex.Message}");
            return NormalizeNightReserveSettings(new NightReserveSettings());
        }
    }

    static NightReserveSettings NormalizeNightReserveSettings(
        NightReserveSettings settings)
    {
        settings.Version = 1;
        settings.CreateStartHour = Math.Clamp(settings.CreateStartHour, 0, 23);
        settings.CreateEndHour = Math.Clamp(settings.CreateEndHour, 1, 24);
        settings.TargetCount = Math.Clamp(settings.TargetCount, 1, 20);
        return settings;
    }

    void SaveNightReserveSettings(NightReserveSettings settings)
    {
        settings = NormalizeNightReserveSettings(settings);
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });
        var temp = NightReserveSettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, NightReserveSettingsPath, overwrite: true);
        _nightReserveSettings = settings;
    }

    NightReserveStateDocument LoadNightReserveState()
    {
        try
        {
            if (!File.Exists(NightReserveStatePath))
                return new NightReserveStateDocument();

            var loaded = JsonSerializer.Deserialize<NightReserveStateDocument>(
                File.ReadAllText(NightReserveStatePath, Encoding.UTF8))
                ?? new NightReserveStateDocument();

            loaded.Version = 1;
            loaded.Profiles ??= new List<string>();
            loaded.Profiles = loaded.Profiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_STATE_READ] error={ex.Message}");
            return new NightReserveStateDocument();
        }
    }

    void SaveNightReserveStateUnsafe()
    {
        _nightReserveState.Version = 1;
        _nightReserveState.Profiles = _nightReserveState.Profiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(
            _nightReserveState,
            new JsonSerializerOptions { WriteIndented = true });
        var temp = NightReserveStatePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, NightReserveStatePath, overwrite: true);
    }

    bool IsNightReserveProfile(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        lock (_nightReserveLock)
        {
            return _nightReserveState.Profiles.Any(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    int CountNightReservePrimaryLiveSlots()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in _contexts.Values)
        {
            var state = GetEffectiveRuntimeState(ctx);
            if (state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering)
                names.Add(ctx.Profile.Name);
        }

        return names.Count;
    }

    bool IsNightReserveProfileProtected(string profileName)
    {
        if (!_nightReserveInitialized || !_nightReserveSettings.Enabled)
            return false;

        if (!IsNightReserveProfile(profileName))
            return false;

        // Target chạy chính luôn ưu tiên cao hơn kho dự phòng. Nếu đang thiếu slot
        // thì reserve trở thành buffer mềm và được phép lấy ngay, kể cả PREPARE/PRIME.
        // Chỉ bảo vệ reserve khi target chính đã đủ để tránh rotation tiêu mất kho.
        int target;
        bool targetInitialized;
        lock (_autoReplacementFixedSlotLock)
        {
            target = _autoReplacementTargetSlots;
            targetInitialized = _autoReplacementTargetInitialized;
        }

        if (targetInitialized
            && target > 0
            && CountNightReservePrimaryLiveSlots() < target)
        {
            return false;
        }

        // Khi chưa có target phiên hiện tại, không có nhu cầu chạy chính để mượn reserve.
        // Từ PREPARE trở đi vẫn giữ lại kho cho Giờ vàng như logic cũ.
        var phase = GetRunStrategyPhase(GetToolNow(), _runStrategySettings);
        return !phase.Equals("OFFPEAK", StringComparison.OrdinalIgnoreCase);
    }

    void MarkNightReserveConsumed(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var removed = false;
        lock (_nightReserveLock)
        {
            removed = _nightReserveState.Profiles.RemoveAll(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveNightReserveStateUnsafe();
        }

        if (removed)
        {
            _log.Info(
                $"[NIGHT_RESERVE_CONSUMED] profile={profileName} source={source} remaining={GetNightReserveCountSnapshot()}");
        }
    }

    void AddNightReserveProfile(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var added = false;
        lock (_nightReserveLock)
        {
            if (!_nightReserveState.Profiles.Any(x =>
                    x.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
            {
                _nightReserveState.Profiles.Add(profileName);
                SaveNightReserveStateUnsafe();
                added = true;
            }
        }

        if (added)
        {
            _log.Info(
                $"[NIGHT_RESERVE_ADD] profile={profileName} source={source} count={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");
        }
    }

    (int EffectiveCount, int DedicatedFreshCount, int FreshQueuedCount, int MediumQueuedCount)
        GetNightReserveCountBreakdownSnapshot()
    {
        HashSet<string> dedicatedFresh;
        lock (_nightReserveLock)
        {
            dedicatedFresh = _nightReserveState.Profiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        List<ReusableProfileQueueEntry> supply;
        lock (_reusableProfileQueueLock)
        {
            supply = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x => !string.IsNullOrWhiteSpace(x.ProfileName))
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        var freshLimit = TimeSpan.FromHours(
            Math.Max(1, _runStrategySettings.FreshHours)).TotalSeconds;
        var oldLimit = TimeSpan.FromHours(
            Math.Max(_runStrategySettings.FreshHours + 1, _runStrategySettings.OldHours)).TotalSeconds;

        // Quota reserve được tính theo đúng số PRF THỰC SỰ còn idle trong hàng Chờ
        // sau khi dàn chạy chính đã đủ. Marker Night Reserve chỉ dùng để bảo vệ/trace,
        // tuyệt đối không được cộng quota nếu profile đó đã rời hàng Chờ để OPEN/RUN.
        var freshIdleNames = supply
            .Where(x =>
                x.TotalRunSeconds < freshLimit
                && !IsReusableProfileBusy(x.ProfileName))
            .Select(x => x.ProfileName.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mediumIdleNames = supply
            .Where(x =>
                x.TotalRunSeconds >= freshLimit
                && x.TotalRunSeconds < oldLimit
                && !IsReusableProfileBusy(x.ProfileName))
            .Select(x => x.ProfileName.Trim())
            .Where(x => x.Length > 0 && !freshIdleNames.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dedicatedFreshIdleCount = dedicatedFresh.Count(freshIdleNames.Contains);
        var freshQueuedCount = freshIdleNames.Count - dedicatedFreshIdleCount;

        return (
            freshIdleNames.Count + mediumIdleNames.Count,
            dedicatedFreshIdleCount,
            freshQueuedCount,
            mediumIdleNames.Count);
    }

    int GetNightReserveCountSnapshot()
        => GetNightReserveCountBreakdownSnapshot().EffectiveCount;

    static bool IsHourInsideNightReserveCreateWindow(
        DateTimeOffset now,
        NightReserveSettings settings)
    {
        var hour = now.Hour;
        var start = settings.CreateStartHour;
        var end = settings.CreateEndHour;

        if (end == 24)
            return hour >= start;

        if (start < end)
            return hour >= start && hour < end;

        // Cho phép cấu hình qua nửa đêm nếu sau này cần.
        return hour >= start || hour < end;
    }

    async Task<int> ReconcileNightReserveAsync(
        bool adoptExistingFresh,
        CancellationToken token)
    {
        await RefreshReusableProfileQueueAsync(
            "night_reserve_reconcile",
            token);

        // Kho dự phòng hiệu dụng = PRF MỚI được giữ riêng + toàn bộ PRF hàng chờ
        // thuộc 2 lane MỚI + TB, chỉ sau khi target chạy chính đã đủ. Đây là quota
        // mềm: PRF hàng chờ không bị khóa reserve và vẫn tuân theo cooldown/ưu tiên cũ.
        List<ReusableProfileQueueEntry> supply;
        lock (_reusableProfileQueueLock)
        {
            supply = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x => !string.IsNullOrWhiteSpace(x.ProfileName))
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        var freshLimit = TimeSpan.FromHours(
            Math.Max(1, _runStrategySettings.FreshHours)).TotalSeconds;

        var freshSupplyByName = supply
            .Where(x => x.TotalRunSeconds < freshLimit)
            .GroupBy(x => x.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(e => e.NameSyncPending ? 1 : 0).First(),
                StringComparer.OrdinalIgnoreCase);

        var freshIdle = supply
            .Where(x =>
                x.TotalRunSeconds < freshLimit
                && !IsReusableProfileBusy(x.ProfileName))
            .OrderBy(x => x.NameSyncPending ? 1 : 0)
            .ThenBy(x => x.TotalRunSeconds)
            .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
            .ToList();

        var freshIdleNames = freshIdle
            .Select(x => x.ProfileName.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // PRF TB = từ ngưỡng MỚI đến trước ngưỡng CŨ của Run Strategy.
        // Cả MỚI và TB trong Chờ dùng lại đều tính quota nếu không đang mở/chạy.
        // Cooldown vẫn được tính vì profile vẫn còn trong kho; khi cần chạy chính,
        // engine cũ vẫn tự tuân thủ cooldown. PRF CŨ không tính dự phòng.
        var oldLimit = TimeSpan.FromHours(
            Math.Max(_runStrategySettings.FreshHours + 1, _runStrategySettings.OldHours)).TotalSeconds;

        var mediumIdleNames = supply
            .Where(x =>
                x.TotalRunSeconds >= freshLimit
                && x.TotalRunSeconds < oldLimit
                && !IsReusableProfileBusy(x.ProfileName))
            .Select(x => x.ProfileName.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int adopted = 0;
        int dedicatedCount;
        int effectiveCount;

        lock (_nightReserveLock)
        {
            var before = _nightReserveState.Profiles.Count;

            // Marker chỉ còn hợp lệ khi PRF vẫn là MỚI + idle + còn trong queue supply.
            // Như vậy PRF vừa được mượn lên chạy hoặc đã già khỏi ngưỡng MỚI sẽ tự
            // rời quota reserve ngay cả khi event CONSUMED bị trễ.
            _nightReserveState.Profiles.RemoveAll(name =>
            {
                if (!freshSupplyByName.ContainsKey(name))
                    return true;

                // UNKNOWN/OPENING ngắn hạn chưa đủ bằng chứng reserve đã bị dùng; giữ
                // marker để không tạo dư. RUNNING/PAUSED/RECOVERING thật sẽ không phải
                // transient và sẽ bị loại (hoặc event CONSUMED đã loại trước đó).
                if (IsReusableProfileBusy(name)
                    && !IsReusableProfileTransientBusy(name))
                {
                    return true;
                }

                return false;
            });

            // Hàng chờ MỚI + TB tiếp tục là supply mềm. Marker/protection không đổi,
            // nên logic chọn PRF, cooldown và thứ tự lấy hàng chờ vẫn dùng code cũ.

            // Nếu user giảm target, chỉ prune marker MỚI giữ riêng khi chính số marker
            // đã vượt target. Supply mềm MỚI/TB trong hàng chờ không tự khóa/mở marker.
            if (_nightReserveState.Profiles.Count > _nightReserveSettings.TargetCount)
            {
                var keep = _nightReserveState.Profiles
                    .Where(freshSupplyByName.ContainsKey)
                    .OrderBy(name => freshSupplyByName[name].NameSyncPending ? 1 : 0)
                    .ThenBy(name => freshSupplyByName[name].TotalRunSeconds)
                    .ThenBy(name => name, NaturalProfileNameOrder)
                    .Take(_nightReserveSettings.TargetCount)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _nightReserveState.Profiles.RemoveAll(name => !keep.Contains(name));
            }

            // Không adopt PRF MỚI hàng chờ thành marker reserve nữa chỉ vì nó được
            // tính quota. User yêu cầu MỚI + TB đều là supply dự phòng mềm.
            if (before != _nightReserveState.Profiles.Count)
                SaveNightReserveStateUnsafe();

            // Quy tắc quota: chỉ số PRF MỚI + TB đang THỰC SỰ idle trong hàng Chờ
            // mới được tính. Marker dedicated vẫn giữ nguyên để phục vụ protection/trace,
            // nhưng không thể làm quota "ảo" khi profile đã được lấy lên OPEN/RUN.
            dedicatedCount = _nightReserveState.Profiles.Count(name =>
                freshIdleNames.Contains(name));
            effectiveCount = freshIdleNames.Count + mediumIdleNames.Count;
        }

        var readyCount = freshIdle.Count(x => !x.NameSyncPending);
        var pendingNameCount = freshIdle.Count(x => x.NameSyncPending);
        _log.Info(
            $"[NIGHT_RESERVE_RECONCILE] reserve={effectiveCount}/{_nightReserveSettings.TargetCount} "
            + $"dedicatedFreshIdle={dedicatedCount} freshQueuedOther={Math.Max(0, freshIdleNames.Count - dedicatedCount)} mediumQueued={mediumIdleNames.Count} "
            + $"freshIdle={freshIdle.Count} ready={readyCount} nameSyncPending={pendingNameCount} adopted={adopted} adopt={adoptExistingFresh}");

        return effectiveCount;
    }

    void MarkNightReservePrimaryRunIntent(int target, string source)
    {
        if (target <= 0)
            return;

        _nightReservePrimaryRunIntentActive = true;
        ResetNightReservePrimaryStability("RUN_INTENT:" + source);
        _nightReserveNextCheckUtc = DateTime.MinValue;
        _log.Info($"[NIGHT_RESERVE_RUN_INTENT] active=true target={target} source={source}");
    }

    void ResetNightReservePrimaryRunIntent(string source)
    {
        var wasActive = _nightReservePrimaryRunIntentActive;
        _nightReservePrimaryRunIntentActive = false;
        ResetNightReservePrimaryStability("RUN_INTENT_RESET:" + source);
        _nightReserveNextCheckUtc = DateTime.MinValue;

        if (wasActive)
            _log.Info($"[NIGHT_RESERVE_RUN_INTENT] active=false source={source}");
    }

    void ResetNightReservePrimaryStability(string reason)
    {
        if (_nightReservePrimaryStableSinceUtc != DateTime.MinValue
            || _nightReservePrimaryStableTarget != 0)
        {
            _log.Info(
                $"[NIGHT_RESERVE_PRIMARY_RESET] reason={reason} target={_nightReservePrimaryStableTarget}");
        }

        _nightReservePrimaryStableSinceUtc = DateTime.MinValue;
        _nightReservePrimaryStableTarget = 0;
    }

    bool TryGetNightReservePrimaryReady(
        out int target,
        out int fulfilled,
        out int pending,
        out string reason)
    {
        if (!_nightReservePrimaryRunIntentActive)
        {
            target = 0;
            fulfilled = 0;
            pending = 0;
            reason = "NO_AUTO_RUN_INTENT";
            return false;
        }

        lock (_autoReplacementFixedSlotLock)
        {
            target = _autoReplacementTargetSlots;
            if (!_autoReplacementTargetInitialized || target <= 0)
            {
                fulfilled = 0;
                pending = 0;
                reason = "NO_ACTIVE_TARGET";
                return false;
            }
        }

        // Reserve chỉ được tính SAU nhu cầu chạy chính. Một request bù/open/rotation
        // còn tồn tại nghĩa là inventory chưa ổn định, tuyệt đối chưa tạo dự phòng.
        fulfilled = CountNightReservePrimaryLiveSlots();
        pending = GetAutoReplacementPendingCount();

        if (!_autoReplacementSessionArmed)
        {
            reason = "AUTO_REPLACE_NOT_ARMED";
            return false;
        }

        if (_autoReplacementQueueRunning
            || _autoReplacementStartAllInProgress
            || _runStrategyRotationRunning
            || pending > 0)
        {
            reason = "PRIMARY_BUSY";
            return false;
        }

        if (fulfilled < target)
        {
            reason = $"PRIMARY_DEFICIT_{fulfilled}/{target}";
            return false;
        }

        reason = "READY";
        return true;
    }

    async Task CheckNightReserveAsync()
    {
        if (IsAutomationHalted
            || !_nightReserveInitialized
            || !_nightReserveSettings.Enabled
            || _nightReserveTickBusy
            || _closing
            || IsDisposed
            || Disposing
            || DateTime.UtcNow < _nightReserveNextCheckUtc)
        {
            return;
        }

        if (!IsHourInsideNightReserveCreateWindow(
                GetToolNow(),
                _nightReserveSettings))
        {
            ResetNightReservePrimaryStability("OUTSIDE_CREATE_WINDOW");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(2);
            return;
        }

        // QUY TẮC MỚI: mở Manager chỉ inventory, KHÔNG được tự tạo dự phòng.
        // Chỉ sau khi phiên hiện tại đã có target chạy chính >0, target đã đủ và
        // toàn bộ bù/open/rotation đứng yên thì mới bắt đầu tính phần PRF dư.
        if (!TryGetNightReservePrimaryReady(
                out var target,
                out var fulfilled,
                out var pending,
                out var primaryReason))
        {
            ResetNightReservePrimaryStability(primaryReason);
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (_nightReservePrimaryStableTarget != target
            || _nightReservePrimaryStableSinceUtc == DateTime.MinValue)
        {
            _nightReservePrimaryStableTarget = target;
            _nightReservePrimaryStableSinceUtc = nowUtc;
            _nightReserveNextCheckUtc = nowUtc.AddSeconds(5);
            _log.Info(
                $"[NIGHT_RESERVE_PRIMARY_SETTLE] target={target} fulfilled={fulfilled} pending={pending} "
                + $"wait={NightReservePrimaryStableDelay.TotalSeconds:0}s action=WAIT_BEFORE_RESERVE");
            return;
        }

        var stableFor = nowUtc - _nightReservePrimaryStableSinceUtc;
        if (stableFor < NightReservePrimaryStableDelay)
        {
            var remaining = NightReservePrimaryStableDelay - stableFor;
            _nightReserveNextCheckUtc = nowUtc.AddSeconds(
                Math.Clamp(remaining.TotalSeconds, 2, 5));
            return;
        }

        // Từ đây target chạy chính đã đủ ổn định. Mỗi reconcile dự phòng cách nhau
        // tối thiểu 2 phút; nếu reserve vừa bị mượn lên chạy, gate phía trên sẽ chờ
        // target chính được bù đủ + ổn định lại 20 giây rồi mới bổ sung reserve.
        _nightReserveNextCheckUtc = nowUtc.AddMinutes(2);

        _nightReserveTickBusy = true;
        try
        {
            if (!await _nightReserveGate.WaitAsync(0))
                return;

            try
            {
                var token = _nightReserveCts.Token;

                // Gate dùng chung với +Auto Profile và "Tạo trước PRF chờ".
                // Không bao giờ login/create xen kẽ hai luồng.
                if (!await _autoProfileQueueGate.WaitAsync(0, token))
                {
                    _nightReserveNextCheckUtc = DateTime.UtcNow.AddSeconds(10);
                    return;
                }

                try
                {
                    // Recheck ngay trước inventory vì target có thể vừa mất slot trong
                    // lúc chờ gate. Nếu không còn đủ, trả quyền ưu tiên cho Tự bù.
                    if (!TryGetNightReservePrimaryReady(
                            out target,
                            out fulfilled,
                            out pending,
                            out primaryReason))
                    {
                        ResetNightReservePrimaryStability("PRE_RECONCILE_" + primaryReason);
                        _nightReserveNextCheckUtc = DateTime.UtcNow.AddSeconds(5);
                        return;
                    }

                    var count = await ReconcileNightReserveAsync(
                        adoptExistingFresh: true,
                        token);

                    if (count >= _nightReserveSettings.TargetCount)
                        return;

                    // Recheck lần cuối trước CREATE. Inventory refresh có thể kéo dài và
                    // đúng lúc đó target chính phát sinh thiếu slot. Không bao giờ tạo
                    // reserve trong khi phiên chạy chính đang cần capacity.
                    if (!TryGetNightReservePrimaryReady(
                            out target,
                            out fulfilled,
                            out pending,
                            out primaryReason))
                    {
                        ResetNightReservePrimaryStability("PRE_CREATE_" + primaryReason);
                        _nightReserveNextCheckUtc = DateTime.UtcNow.AddSeconds(5);
                        return;
                    }

                    _log.Info(
                        $"[NIGHT_RESERVE_NEED_CREATE] primary={fulfilled}/{target} reserve={count}/{_nightReserveSettings.TargetCount} action=CREATE_ONE");

                    var outcome = await CreateOneNightReserveProfileAsync(token);
                    if (outcome is not null && outcome.Skipped != true)
                    {
                        var cooldownSettings = LoadAutoProfileCooldownSettings();
                        var kind = ResolveAutoProfileCooldownKind(
                            outcome,
                            ref _nightReserveConsecutiveLoginErrors);

                        await WaitAutoProfileCooldownAsync(
                            cooldownSettings,
                            kind,
                            () => false,
                            token,
                            text => _log.Info($"[NIGHT_RESERVE_COOLDOWN] {text}"),
                            () => _closing);
                    }
                }
                finally
                {
                    _autoProfileQueueGate.Release();
                }
            }
            finally
            {
                _nightReserveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Manager đang đóng.
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_TICK_WARN] {ex.Message}");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(5);
        }
        finally
        {
            _nightReserveTickBusy = false;
        }
    }

    async Task<AutoProfileProcessOutcome?> CreateOneNightReserveProfileAsync(
        CancellationToken token)
    {
        var startName = DetectNextAutoProfileName();
        var identity = LoadIdentityToolState();
        var names = SplitIdentityNames(identity.NamesText);
        if (names.Count == 0)
        {
            _log.Warn("[NIGHT_RESERVE_WAIT] reason=identity_names_empty");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(10);
            return null;
        }

        RememberAutoProfileSequenceStart(startName);

        var queue = await RunAccountPoolIoAsync(
            () =>
            {
                if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                    _accountPoolService.ReloadCurrentExcel();
                _accountPoolService.EnsureAutoColumns();
                return BuildAutoProfileQueue(
                    requestedNew: 1,
                    requestedStartName: startName,
                    resumeIncomplete: false,
                    retryPaused: false);
            },
            token);

        var item = queue.FirstOrDefault(x => !x.ResumeExisting);
        if (item is null)
        {
            _log.Warn("[NIGHT_RESERVE_WAIT] reason=no_unassigned_account");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(10);
            return null;
        }

        _log.Info(
            $"[NIGHT_RESERVE_CREATE_BEGIN] profile={item.ProfileName} account={item.Account.Username} target={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");

        AutoProfileProcessOutcome? outcome = null;
        _autoReplacementCleanupProfiles.Add(item.ProfileName);
        try
        {
            outcome = await ProcessAutoProfileQueueItemAsync(
                item,
                autoRename: true,
                autoStart: false,
                isPaused: () => false,
                ct: token,
                ui: (step, result, _) =>
                    _log.Info(
                        $"[NIGHT_RESERVE_CREATE_STEP] profile={item.ProfileName} step={step} result={result}"),
                verifyIdentityAfterRename: true,
                writeIdentityDoneToExcel: true,
                tolerateIdentityValidationFailure: true);

            await ClosePreparedProfileRuntimeAsync(
                item.ProfileName,
                item.Account.Username);

            if (!outcome.RenameSucceeded)
            {
                _log.Warn(
                    $"[NIGHT_RESERVE_NOT_READY] profile={item.ProfileName} status={outcome.Status} step={outcome.Step} reason=rename_not_succeeded");
                return outcome;
            }

            // PRF đã tạo xong nhưng tên chưa đồng bộ vẫn là supply thật đã tồn tại.
            // Đưa vào NAME_SYNC_PENDING VÀ tính luôn quota reserve để lượt kế tiếp
            // không tạo dư thêm một PRF chỉ vì TikTok/Excel cập nhật tên chậm.
            if (!outcome.IdentityVerified || !outcome.IdentityExcelDone)
            {
                QueueReusableProfileNameSyncPending(
                    item,
                    outcome.IdentityVerified
                        ? "night_reserve_identity_done_pending"
                        : "night_reserve_name_not_verified");

                AddNightReserveProfile(
                    item.ProfileName,
                    "auto_daytime_create_pending_name_sync");

                _log.Info(
                    $"[NIGHT_RESERVE_PENDING] profile={item.ProfileName} account={item.Account.Username} "
                    + $"verified={outcome.IdentityVerified} excelDone={outcome.IdentityExcelDone} action=NAME_SYNC_PENDING_COUNTS_AS_RESERVE");
                return outcome;
            }

            if (!TryAddReusableProfileManual(
                    item.Account.Id,
                    item.Account.Username,
                    item.ProfileName,
                    item.Account.Note,
                    out var queueMessage))
            {
                _log.Warn(
                    $"[NIGHT_RESERVE_QUEUE_FAIL] profile={item.ProfileName} message={queueMessage}");
                return outcome;
            }

            AddNightReserveProfile(
                item.ProfileName,
                "auto_daytime_create_verified");

            _log.Info(
                $"[NIGHT_RESERVE_CREATE_OK] profile={item.ProfileName} account={item.Account.Username} count={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");

            return outcome;
        }
        catch (OperationCanceledException)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            throw;
        }
        catch (AutoProfileLoginBanException ex)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            _log.Warn(
                $"[NIGHT_RESERVE_CREATE_BAN] profile={item.ProfileName} account={item.Account.Username} error={ex.Message}");
            return new AutoProfileProcessOutcome(
                false,
                false,
                "LOGIN_BANNED",
                "WAIT_LOGIN",
                ex.Message);
        }
        catch (Exception ex)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            _log.Warn(
                $"[NIGHT_RESERVE_CREATE_FAIL] profile={item.ProfileName} account={item.Account.Username} error={ex.Message}");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(5);
            return outcome;
        }
        finally
        {
            _autoReplacementCleanupProfiles.Remove(item.ProfileName);
        }
    }

    void ShowNightReserveSettingsDialog(IWin32Window owner)
    {
        InitializeNightReserveFeature();
        var current = LoadNightReserveSettings();

        var form = new Form
        {
            Text = $"Dự phòng PRF ban đêm — {AppVersionInfo.Display}",
            Width = 590,
            Height = 410,
            MinimumSize = new Size(560, 385),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: true);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 18),
            ColumnCount = 3,
            RowCount = 7,
            BackColor = ModernDialog.Canvas
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        var enabled = new CheckBox
        {
            Text = "Tự chuẩn bị PRF dự phòng ban ngày",
            Checked = current.Enabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 91, 152),
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(enabled, 0, 0);
        panel.SetColumnSpan(enabled, 3);

        NumericUpDown Hour(int value, int min, int max) => new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 80,
            Margin = new Padding(0, 6, 0, 0)
        };

        var startHour = Hour(current.CreateStartHour, 0, 23);
        var endHour = Hour(current.CreateEndHour, 1, 24);
        var target = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 20,
            Value = Math.Clamp(current.TargetCount, 1, 20),
            Width = 80,
            Margin = new Padding(0, 6, 0, 0)
        };

        Label Field(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(46, 65, 88),
            Margin = new Padding(0, 10, 8, 0)
        };

        panel.Controls.Add(Field("Bắt đầu tạo dự phòng"), 0, 1);
        panel.Controls.Add(startHour, 1, 1);
        panel.Controls.Add(Field("giờ"), 2, 1);

        panel.Controls.Add(Field("Kết thúc tạo dự phòng"), 0, 2);
        panel.Controls.Add(endHour, 1, 2);
        panel.Controls.Add(Field("giờ · 24 = 00:00"), 2, 2);

        panel.Controls.Add(Field("Số PRF dự phòng cần giữ"), 0, 3);
        panel.Controls.Add(target, 1, 3);
        panel.Controls.Add(Field("PRF"), 2, 3);

        var reserveBreakdown = GetNightReserveCountBreakdownSnapshot();
        var currentCount = new Label
        {
            Text = $"Hiện có: {reserveBreakdown.EffectiveCount} / {current.TargetCount} PRF dự phòng "
                + $"({reserveBreakdown.DedicatedFreshCount} giữ riêng + {reserveBreakdown.FreshQueuedCount} MỚI hàng chờ + {reserveBreakdown.MediumQueuedCount} TB hàng chờ)",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.DarkGreen,
            Margin = new Padding(0, 10, 0, 0)
        };
        panel.Controls.Add(currentCount, 0, 4);
        panel.SetColumnSpan(currentCount, 3);

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            Text =
                "Dự phòng = số PRF MỚI + TB thực sự còn idle trong Chờ dùng lại SAU KHI dàn chạy chính đã đủ target. "
                + "Ví dụ chạy 5 và đặt dự phòng 3 thì Tool giữ 5 PRF chạy + tối thiểu 3 PRF trong Chờ. "
                + "PRF CŨ không tính; logic lấy hàng chờ, cooldown và khung giờ tạo vẫn giữ nguyên.",
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(info, 0, 5);
        panel.SetColumnSpan(info, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0)
        };
        var cancel = new Button { Text = "Hủy", Size = new Size(100, 40) };
        var save = new Button { Text = "Lưu", Size = new Size(110, 40) };
        ModernDialog.StyleSecondaryButton(cancel);
        ModernDialog.StylePrimaryButton(save);
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        panel.Controls.Add(actions, 0, 6);
        panel.SetColumnSpan(actions, 3);

        cancel.Click += (_, _) => form.Close();
        save.Click += async (_, _) =>
        {
            var updated = new NightReserveSettings
            {
                Enabled = enabled.Checked,
                CreateStartHour = (int)startHour.Value,
                CreateEndHour = (int)endHour.Value,
                TargetCount = (int)target.Value
            };

            SaveNightReserveSettings(updated);
            _nightReserveNextCheckUtc = DateTime.MinValue;
            ResetNightReservePrimaryStability("SETTINGS_CHANGED");

            try
            {
                // Lưu cấu hình chỉ inventory/prune marker cũ; KHÔNG adopt supply mới
                // và không tạo reserve trước khi target chạy chính của phiên hiện tại đủ.
                await ReconcileNightReserveAsync(
                    adoptExistingFresh: false,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warn($"[NIGHT_RESERVE_UI_RECONCILE_WARN] {ex.Message}");
            }

            var updatedBreakdown = GetNightReserveCountBreakdownSnapshot();
            currentCount.Text =
                $"Hiện có: {updatedBreakdown.EffectiveCount} / {updated.TargetCount} PRF dự phòng "
                + $"({updatedBreakdown.DedicatedFreshCount} giữ riêng + {updatedBreakdown.FreshQueuedCount} MỚI hàng chờ + {updatedBreakdown.MediumQueuedCount} TB hàng chờ)";

            form.Close();
        };

        form.Controls.Add(panel);
        form.ShowDialog(owner);
    }
}
