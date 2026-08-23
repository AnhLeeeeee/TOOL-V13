using System.Text;
using System.Text.Json;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
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

                // Cho tiến trình đóng cũ nhả hẳn Worker/Chrome trước khi mở profile bù.
                await Task.Delay(650);

                if (_closing
                    || !_autoReplacementSessionArmed
                    || !_autoCloseSettings.OpenReplacementAfterAutoClose)
                {
                    return;
                }

                var filled = false;
                var lastError = "";

                try
                {
                    // V13.6.6+: Tự bù KHÔNG quét/tái sử dụng profile MỚI/TEST nữa.
                    // Mỗi suất bù lấy thẳng tài khoản chưa gán và tạo một profile mới.
                    // Việc này loại bỏ vòng quét IsProfileInUse() trên toàn bộ catalog,
                    // tránh PowerShell/Get-CimInstance lặp theo từng profile làm nghẽn UI Manager.
                    _log.Info(
                        $"[AUTO_REPLACE_NEW_ACCOUNT_ONLY] id={request.Id} closed={request.ClosedProfileName} mode=create_new_from_unassigned_account");

                    filled = await TryCreateReplacementAsync(request);

                    if (!filled)
                        lastError = "Chưa tạo được profile mới từ tài khoản chưa gán; giữ suất bù để thử lại.";
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

    async Task<bool> TryOpenNextExistingReplacementAsync(AutoReplacementRequest request)
    {
        var catalog = _profileService.Load();
        RefreshContextsFromCatalog(catalog);

        var accounts = _accountPoolService.Load();

        Dictionary<string, TikTokAccountPoolService.TikTokAccountAutoState> autoStates;
        try
        {
            autoStates = _accountPoolService.LoadAutoStates();
        }
        catch
        {
            autoStates = new Dictionary<string, TikTokAccountPoolService.TikTokAccountAutoState>(
                StringComparer.OrdinalIgnoreCase);
        }

        var candidates = new List<AutoReplacementCandidate>();

        foreach (var profile in catalog.Profiles)
        {
            if (_closing)
                return false;

            if (_autoReplacementRetiredProfiles.Contains(profile.Name)
                || _autoReplacementClaimedProfiles.Contains(profile.Name)
                || IsReplacementProfileCoolingDown(profile.Name))
            {
                continue;
            }

            if (!_contexts.TryGetValue(profile.Name, out var ctx))
                continue;

            // Chỉ lấy profile đang thật sự rảnh.
            if (ctx.Tab is not null
                || (ctx.Worker is not null && !ctx.Worker.HasExited)
                || ctx.Opening
                || ChromeProfileNameSyncService.IsProfileInUse(ctx.Profile.ProfilePath))
            {
                continue;
            }

            // Profile bù phải là profile đã có tài khoản hợp lệ để có thể mở và chạy ngay.
            var assignedAccount = accounts.FirstOrDefault(a =>
                a.AssignedProfile.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

            if (assignedAccount is null
                || string.IsNullOrWhiteSpace(assignedAccount.Password))
            {
                continue;
            }

            if (string.Equals((assignedAccount.Note ?? "").Trim(), "ban", StringComparison.OrdinalIgnoreCase))
            {
                _autoReplacementRetiredProfiles.Add(profile.Name);
                MarkProfileSupplyState(profile.Name, "retired", "account_note_ban");
                _log.Info(
                    $"[AUTO_REPLACE_SKIP_EXISTING] profile={profile.Name} reason=account_note_ban");
                continue;
            }

            if (autoStates.TryGetValue(assignedAccount.Id, out var autoState)
                && autoState.IsPausedOrError)
            {
                _log.Info(
                    $"[AUTO_REPLACE_SKIP_EXISTING] profile={profile.Name} reason=auto_state_blocked status={autoState.Status} step={autoState.Step}");
                continue;
            }

            if (!TryClassifyReplacementSupply(ctx, out var supplyState, out var totalRuntime, out var priority))
                continue;

            candidates.Add(new AutoReplacementCandidate(
                ctx,
                assignedAccount,
                supplyState,
                totalRuntime,
                priority));
        }

        // Ưu tiên profile MỚI trước, rồi TEST. Trong cùng nhóm thì lấy theo thứ tự tên tự nhiên.
        foreach (var candidate in candidates
                     .OrderBy(x => x.Priority)
                     .ThenBy(x => x.Context.Profile.Name, NaturalProfileNameOrder))
        {
            if (_closing)
                return false;

            var ctx = candidate.Context;
            var profileName = ctx.Profile.Name;
            _autoReplacementClaimedProfiles.Add(profileName);

            try
            {
                _log.Info(
                    $"[AUTO_REPLACE_OPEN_SUPPLY] closed={request.ClosedProfileName} replacement={profileName} supply={candidate.SupplyState} total={candidate.TotalRuntime:c} account={candidate.Account.Username}");

                var opened = await OpenProfileAsync(
                    ctx,
                    $"Tự bù cho {request.ClosedProfileName}: mở profile {candidate.SupplyState}...");

                if (!opened && (ctx.Worker is null || ctx.Worker.HasExited))
                {
                    MarkReplacementProfileFailed(profileName, "open_worker");
                    _log.Warn(
                        $"[AUTO_REPLACE_EXISTING_FAIL] profile={profileName} step=open_worker");
                    continue;
                }

                // start_auto không bật dialog, phù hợp hàng đợi tự động.
                var reply = await SendCommandAsync(
                    ctx,
                    "start_auto",
                    TimeSpan.FromSeconds(100));

                if (!string.Equals(reply, "started", StringComparison.OrdinalIgnoreCase))
                {
                    MarkReplacementProfileFailed(profileName, "start:" + reply);
                    _log.Warn(
                        $"[AUTO_REPLACE_EXISTING_FAIL] profile={profileName} step=start reply={reply}");

                    await CloseFailedReplacementRuntimeAsync(ctx);
                    continue;
                }

                // Không coi "started" là đã bù xong ngay. Chờ RUNNING khỏe liên tục
                // 30 giây (timeout 60 giây) để tránh vừa mở xong đã lỗi trang/Chrome.
                var healthy = await WaitForReplacementHealthyRunningAsync(
                    ctx,
                    request,
                    "existing");

                if (!healthy)
                {
                    MarkReplacementProfileFailed(profileName, "started_but_not_healthy");
                    _log.Warn(
                        $"[AUTO_REPLACE_EXISTING_FAIL] profile={profileName} step=confirm_running");

                    await CloseFailedReplacementRuntimeAsync(ctx);
                    continue;
                }

                // Chỉ sau khi RUNNING khỏe mới đánh dấu ĐÃ TREO và hoàn tất suất bù.
                MarkProfileSupplyState(profileName, "used", "auto_replacement_running_confirmed");

                _log.Info(
                    $"[AUTO_REPLACE_EXISTING_OK] closed={request.ClosedProfileName} replacement={profileName} supply={candidate.SupplyState} confirmed=healthy_running");

                return true;
            }
            catch (Exception ex)
            {
                MarkReplacementProfileFailed(profileName, "exception:" + ex.Message);
                _log.Warn(
                    $"[AUTO_REPLACE_EXISTING_ERROR] profile={profileName} error={ex.Message}");

                try { await CloseFailedReplacementRuntimeAsync(ctx); } catch { }
            }
            finally
            {
                _autoReplacementClaimedProfiles.Remove(profileName);
            }
        }

        return false;
    }

    async Task CloseFailedReplacementRuntimeAsync(ProfileContext ctx)
    {
        var chromeClosed = false;

        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                try { await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(5)); } catch { }

                try
                {
                    var closeReply = await SendCloseChromeCommandAsync(ctx);
                    chromeClosed = closeReply is "closed" or "not_running";
                }
                catch { }
            }

            if (!chromeClosed)
            {
                try
                {
                    await Task.Run(
                        () => ChromeProfileNameSyncService.StopChromeUsingProfile(
                            ctx.Profile.ProfilePath));
                }
                catch { }
            }

            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                try { await SendPipeAsync(ctx.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch { }

                var worker = ctx.Worker;
                if (worker is not null && !worker.HasExited)
                {
                    try
                    {
                        if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(5)))
                            worker.Kill(true);
                    }
                    catch { }
                }
            }
        }
        finally
        {
            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);
        }
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
            const int maxCreateAttemptsPerSlot = 3;

            for (var attempt = 1; attempt <= maxCreateAttemptsPerSlot; attempt++)
            {
                if (_closing)
                    return false;

                var startName = DetectNextAutoProfileName();

                var queue = await RunAccountPoolIoAsync(
                    () => BuildAutoProfileQueue(
                        requestedNew: 1,
                        requestedStartName: startName,
                        resumeIncomplete: false,
                        retryPaused: false),
                    CancellationToken.None);

                if (queue.Count == 0)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_NONE] closed={request.ClosedProfileName} reason=no_unassigned_account_with_password");

                    WriteAutoActivityLog(
                        action: "TỰ BÙ",
                        profile: request.ClosedProfileName,
                        reason: request.Reason,
                        result: "CHỜ",
                        detail: "Không còn tài khoản chưa gán có mật khẩu để tạo profile bù.");

                    return false;
                }

                var item = queue[0];
                _autoReplacementClaimedProfiles.Add(item.ProfileName);

                try
                {
                    _log.Info(
                        $"[AUTO_REPLACE_CREATE_BEGIN] closed={request.ClosedProfileName} profile={item.ProfileName} account={item.Account.Username} attempt={attempt}/{maxCreateAttemptsPerSlot}");

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "BẮT ĐẦU",
                        detail: $"Lần thử {attempt}/{maxCreateAttemptsPerSlot}");

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

                            if (createdCtx is not null)
                            {
                                try { await CloseFailedReplacementRuntimeAsync(createdCtx); } catch { }
                            }

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

                    // Auto Profile đã note checkpoint lỗi/CAPTCHA. Thử account/profile kế tiếp
                    // cho cùng một suất bù, giống tư tưởng lỗi profile nào thì chuyển profile khác.
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_REPLACE_CREATE_ERROR] profile={item.ProfileName} error={ex.Message}");

                    WriteAutoActivityLog(
                        action: "MỞ PROFILE BÙ",
                        profile: request.ClosedProfileName,
                        account: item.Account.Username,
                        reason: request.Reason,
                        replacementProfile: item.ProfileName,
                        result: "LỖI",
                        detail: ex.Message);
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
