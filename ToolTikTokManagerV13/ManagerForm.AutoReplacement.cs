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
        public string LastError { get; set; } = "";
    }

    sealed class AutoReplacementQueueDocument
    {
        public int Version { get; set; } = 1;
        public List<AutoReplacementRequest> Pending { get; set; } = new();
    }

    sealed record AutoReplacementCandidate(
        ProfileContext Context,
        TikTokAccountPoolItem Account,
        string SupplyState,
        TimeSpan TotalRuntime,
        int Priority);

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
    const int AutoReplacementHealthyConfirmTimeoutSeconds = 60;
    const int AutoReplacementHealthyStableSeconds = 30;
    static readonly TimeSpan AutoReplacementFailedProfileCooldown = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AutoReplacementQueueWaitSlice = TimeSpan.FromSeconds(5);

    // Nếu TikTok Save tên nhưng giao diện cập nhật chậm, vẫn tiếp tục tạo PRF mới.
    // Chỉ sau 30 phút thiếu suất, hoặc sau mỗi 5 lần thử mới khi đã có PRF chờ tên,
    // mới vét lane NAME_SYNC_PENDING một lượt rồi quay lại logic tạo mới hiện tại.
    static readonly TimeSpan AutoReplacementNameSyncRecoveryAfter = TimeSpan.FromMinutes(30);
    const int AutoReplacementNameSyncRecoveryAfterNewAttempts = 5;

    readonly List<AutoReplacementRequest> _autoReplacementQueue = new();
    readonly HashSet<string> _autoReplacementRetiredProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoReplacementClaimedProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoReplacementFailedProfileRetryUtc = new(StringComparer.OrdinalIgnoreCase);
    readonly object _autoReplacementQueueLock = new();
    readonly object _profileSupplyStateLock = new();
    bool _autoReplacementFeatureInitialized;
    bool _autoReplacementQueueRunning;
    bool _autoReplacementSessionArmed;

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
        if (_closing || IsDisposed || Disposing)
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
        if (_closing || IsDisposed || Disposing)
            return;

        closedProfileName = (closedProfileName ?? "").Trim();
        reason = (reason ?? "").Trim();

        if (closedProfileName.Length == 0)
            return;

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

                // Queue Tự bù có gate RIÊNG để các suất bù không đè nhau.
                // Gate này KHÔNG chặn AutoClose; TIME/BAN/FAULT vẫn được đóng đúng lượt
                // ngay cả khi một suất bù đang tạo PRF mới hoặc chờ cleanup.
                using var replacementOperationLease = await EnterReplacementOperationAsync(
                    request.ClosedProfileName,
                    "AUTO_REPLACE:" + request.Reason);

                // CLEANUP BARRIER: profile cũ phải sạch thật trước khi được phép bù.
                // Không dùng delay cố định để đoán Chrome đã đóng.
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

                    ScheduleAutoReplacementRetry(
                        request.Id,
                        "Cleanup chưa hoàn tất: " + cleanup.Detail);

                    continue;
                }

                if (_closing
                    || !_autoReplacementSessionArmed
                    || !_autoCloseSettings.OpenReplacementAfterAutoClose)
                {
                    return;
                }

                // SLOT-GATE HEALING: ExpectedRunning là intent marker, không phải bằng chứng
                // runtime vật lý. Nếu marker cũ bị sót sau khi Chrome/Worker đã đóng,
                // CountAutoReplacementOccupiedSlots() có thể tưởng đã đủ suất và xóa
                // request Tự bù. Xác minh các marker nghi stale 2 lượt trước khi đếm.
                await PruneStaleAutoReplacementExpectedRunningAsync(request);

                var slotGate = EvaluateAutoReplacementFixedSlotGate(request);

                if (slotGate.AlreadySatisfied)
                {
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
                    ScheduleAutoReplacementRetry(
                        request.Id,
                        slotGate.Detail);
                    continue;
                }

                var filled = false;
                var lastError = "";
                AutoReplacementCleanupBarrierException? cleanupBarrier = null;

                try
                {
                    // V13.7.0: ƯU TIÊN profile đã có trong queue dùng lại.
                    // Queue được tạo bằng runtime_stats.json (<1h) + Ghi chú trống,
                    // không quét PowerShell/IsProfileInUse trên toàn bộ catalog.
                    _log.Info(
                        $"[AUTO_REPLACE_REUSE_FIRST] id={request.Id} closed={request.ClosedProfileName} reusePending={GetReusableProfileQueueCount()}");

                    filled = await TryOpenNextExistingReplacementAsync(request);

                    if (!filled)
                    {
                        // Logic bù cũ được giữ nguyên: nếu không có PRF có sẵn đủ
                        // điều kiện thì chuyển ngay sang tạo PRF mới từ account chưa gán.
                        // Việc kho bù có/không có tuyệt đối không ảnh hưởng quyết định
                        // Tự đóng của profile nguồn.
                        _log.Info(
                            $"[AUTO_REPLACE_REUSE_EMPTY_FALLBACK_NEW] id={request.Id} closed={request.ClosedProfileName}");

                        filled = await TryCreateReplacementAsync(request);
                    }

                    if (!filled)
                    {
                        lastError =
                            "Không dùng lại được profile chờ và chưa tạo được profile mới từ tài khoản chưa gán; giữ suất bù để thử lại.";
                    }
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

                if (_closing)
                    return;

                if (filled)
                {
                    RemoveAutoReplacementRequest(request.Id);

                    _log.Info(
                        $"[AUTO_REPLACE_SLOT_DONE] id={request.Id} closed={request.ClosedProfileName} pending={GetAutoReplacementPendingCount()}");
                }
                else
                {
                    ScheduleAutoReplacementRetry(request.Id, lastError);

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
        AutoReplacementRequest request)
    {
        // Không dùng scan MỚI/TEST cũ nữa vì scan đó gọi
        // ChromeProfileNameSyncService.IsProfileInUse() cho từng profile.
        // Queue dùng lại đã được lọc trước bằng file nhỏ runtime_stats.json.
        return await TryUseReusableProfileQueueAsync(request);
    }

    async Task CloseFailedReplacementRuntimeAsync(ProfileContext ctx)
    {
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
            await EnsureAutoCloseWorkerStoppedAsync(ctx);
            await EnsureAutoCloseChromeStoppedAsync(ctx);

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            _log.Info(
                $"[AUTO_REPLACE_FAILED_CLEANUP_DONE] profile={ctx.Profile.Name} chrome=0 worker=closed tab=removed");

            WriteAutoDiagnosticEvent(
                ctx,
                "auto_replacement",
                "REPLACEMENT_FAILED",
                "CLOSED",
                "chrome=0; worker=closed; tab=removed");
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
                    return;

                _log.Warn($"[AUTO_REPLACE_FAILED_CLEANUP_BEGIN] profile={profileName} source={source}:path_only");
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    profileName,
                    profile.ProfilePath);
                return;
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

        const int retrySeconds = 20;

        while (!_closing && !IsDisposed && !Disposing)
        {
            _log.Warn(
                $"[AUTO_REPLACE_CLEANUP_BARRIER_WAIT] blockedProfile={profileName} request={request.Id} retryIn={retrySeconds}s");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                replacementProfile: profileName,
                result: "CHỜ DỌN PROFILE BÙ LỖI",
                detail: $"Profile bù {profileName} chưa đóng sạch; chưa mở profile bù khác. Thử lại sau {retrySeconds}s.");

            await Task.Delay(TimeSpan.FromSeconds(retrySeconds));

            try
            {
                if (_contexts.TryGetValue(profileName, out var ctx))
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=context");
                    return;
                }

                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);

                if (_contexts.TryGetValue(profileName, out var refreshedCtx))
                {
                    await CloseFailedReplacementRuntimeAsync(refreshedCtx);
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=refreshed_context");
                    return;
                }

                var profile = catalog.Profiles.FirstOrDefault(x =>
                    x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=profile_missing");
                    return;
                }

                // Context không còn: chỉ còn khả năng Chrome mồ côi theo ProfilePath.
                await EnsureAutoCloseChromeStoppedByPathAsync(
                    profileName,
                    profile.ProfilePath);

                _log.Info(
                    $"[AUTO_REPLACE_CLEANUP_BARRIER_CLEARED] profile={profileName} source=profile_path");
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
    }


    static bool IsNameSyncPendingOutcome(AutoProfileProcessOutcome outcome)
    {
        if (!outcome.Step.Equals("RENAME", StringComparison.OrdinalIgnoreCase))
            return false;

        // CAPTCHA/config là lỗi cần xử lý riêng, không phải trường hợp TikTok
        // Save xong nhưng tên cập nhật chậm. Các PAUSED_RENAME còn lại đều đáng
        // giữ lại để một sweep sau chỉ PROBE tên thực tế.
        if (!outcome.Status.StartsWith("PAUSED_RENAME", StringComparison.OrdinalIgnoreCase))
            return false;

        return !outcome.Status.Equals("PAUSED_RENAME_CONFIG", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> TryCreateReplacementAsync(AutoReplacementRequest request)
    {
        // Tự bù chỉ dùng tài khoản CHƯA GÁN và luôn tạo profile MỚI.
        // BuildAutoProfileQueue(requestedNew: 1, resumeIncomplete: false) sẽ lấy
        // tài khoản chưa gán + có mật khẩu theo thứ tự Excel, sau đó ASSIGN ngay
        // để các suất bù khác không thể lấy trùng tài khoản.
        // Dùng cùng gate với cửa sổ "+ Auto Profile" để không có hai luồng
        // đồng thời tranh account / tên profile / Chrome trên VM.
        await _autoProfileQueueGate.WaitAsync();

        try
        {
            // Một suất Tự bù cần đạt đúng 1 profile RUNNING khỏe. BAN/lỗi/skip
            // không được làm mất quota như logic cũ giới hạn 3 lần thử.
            // Giữ danh sách account đã thử trong chính suất này để account vừa lỗi
            // nhưng được ReleaseAccount() không bị lấy lại ngay và gây vòng lặp.
            var attemptedAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempt = 0;
            var newAttemptsSinceNameSyncSweep = 0;
            var deadlineSweepCompleted = false;

            while (!_closing)
            {
                var nameSyncPending = GetNameSyncPendingReusableProfileCount();
                var replacementAge = DateTime.UtcNow - request.QueuedUtc;
                var dueByAge = replacementAge >= AutoReplacementNameSyncRecoveryAfter
                    && !deadlineSweepCompleted;
                var dueByAttempts = newAttemptsSinceNameSyncSweep >= AutoReplacementNameSyncRecoveryAfterNewAttempts;

                if (nameSyncPending > 0 && (dueByAge || dueByAttempts))
                {
                    _log.Info(
                        $"[NAME_SYNC_RECOVERY_TRIGGER] closed={request.ClosedProfileName} pending={nameSyncPending} age={replacementAge:c} newAttemptsSinceSweep={newAttemptsSinceNameSyncSweep} trigger={(dueByAge ? "30m" : "5_attempts")}");

                    var recovered = await TryRecoverNameSyncPendingReusableProfilesOnceAsync(request);
                    newAttemptsSinceNameSyncSweep = 0;
                    if (dueByAge)
                        deadlineSweepCompleted = true;

                    if (recovered)
                        return true;

                    // Vét xong một lượt vẫn chưa có tên cập nhật: quay về đúng logic cũ,
                    // tiếp tục tạo PRF mới. Không lặp recovery liên tục.
                }

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
                    CancellationToken.None);

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

                attemptedAccountIds.Add(item.Account.Id);
                attempt++;
                newAttemptsSinceNameSyncSweep++;
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

                    var outcome = await ProcessAutoProfileQueueItemAsync(
                        item,
                        autoRename: true,
                        autoStart: true,
                        isPaused: static () => false,
                        ct: CancellationToken.None,
                        ui: (step, result, _) =>
                        {
                            _log.Info(
                                $"[AUTO_REPLACE_CREATE_PROGRESS] profile={item.ProfileName} step={step} result={result}");
                        });

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

                        var healthy = createdCtx is not null
                            && await WaitForReplacementHealthyRunningAsync(
                                createdCtx,
                                request,
                                "created");

                        if (!healthy)
                        {
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
                        continue;
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

    async Task<bool> WaitForReplacementHealthyRunningAsync(
        ProfileContext ctx,
        AutoReplacementRequest request,
        string source)
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(AutoReplacementHealthyConfirmTimeoutSeconds);
        DateTime? healthySinceUtc = null;
        string lastFault = "";

        _log.Info(
            $"[AUTO_REPLACE_CONFIRM_BEGIN] id={request.Id} profile={ctx.Profile.Name} source={source} stable={AutoReplacementHealthyStableSeconds}s timeout={AutoReplacementHealthyConfirmTimeoutSeconds}s");

        while (!_closing && DateTime.UtcNow < deadlineUtc)
        {
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

            var nowUtc = DateTime.UtcNow;
            var state = GetEffectiveRuntimeState(ctx);
            var healthy = pollOk && IsAutoCloseHealthyRunning(ctx, state, nowUtc);

            if (healthy)
            {
                healthySinceUtc ??= nowUtc;

                var stableFor = nowUtc - healthySinceUtc.Value;
                if (stableFor >= TimeSpan.FromSeconds(AutoReplacementHealthyStableSeconds))
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CONFIRM_OK] id={request.Id} profile={ctx.Profile.Name} source={source} stable={stableFor:c}");
                    return true;
                }
            }
            else
            {
                healthySinceUtc = null;

                var described = DescribeAutoCloseRuntimeFault(ctx, state, nowUtc);
                if (!string.IsNullOrWhiteSpace(described))
                    lastFault = described;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        _log.Warn(
            $"[AUTO_REPLACE_CONFIRM_TIMEOUT] id={request.Id} profile={ctx.Profile.Name} source={source} fault={lastFault}");

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

    void ScheduleAutoReplacementRetry(string requestId, string lastError)
    {
        lock (_autoReplacementQueueLock)
        {
            var request = _autoReplacementQueue.FirstOrDefault(x =>
                x.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return;

            request.AttemptCount++;
            request.LastError = (lastError ?? "").Trim();

            var delay = request.AttemptCount switch
            {
                <= 1 => TimeSpan.FromMinutes(2),
                2 => TimeSpan.FromMinutes(3),
                _ => TimeSpan.FromMinutes(5)
            };

            request.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            SaveAutoReplacementQueueUnsafe();

            _log.Warn(
                $"[AUTO_REPLACE_SLOT_RETRY] id={request.Id} closed={request.ClosedProfileName} attempt={request.AttemptCount} retryIn={delay:c} next={request.NextAttemptUtc:O} error={request.LastError}");

            WriteAutoActivityLog(
                action: "TỰ BÙ",
                profile: request.ClosedProfileName,
                reason: request.Reason,
                result: "RETRY",
                detail: $"Lần {request.AttemptCount}; thử lại sau {delay.TotalMinutes:0} phút; lỗi={request.LastError}");
        }
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
            Version = 1,
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
