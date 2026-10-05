namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    static readonly TimeSpan RunStrategyDailyReplaceAllRetryDelay =
        TimeSpan.FromMinutes(2);

    // THAY ALL chỉ được quyền CREATE PRF mới trong 2 giờ kể từ Giờ thay.
    // Ví dụ Giờ thay=11:00 -> cửa sổ CREATE là [11:00, 13:00).
    static readonly TimeSpan RunStrategyDailyReplaceAllCreateWindow =
        TimeSpan.FromHours(2);

    readonly object _runStrategyDailyReplaceAllLock = new();
    readonly List<string> _runStrategyDailyReplaceAllVictims = new();

    bool _runStrategyDailyReplaceAllRuntimeArmed;
    bool _runStrategyDailyReplaceAllBootstrapPending;
    bool _runStrategyDailyReplaceAllCycleActive;
    DateTime _runStrategyDailyReplaceAllLastCompletedLocalDate = DateTime.MinValue;
    DateTime _runStrategyDailyReplaceAllCycleScheduledLocalDate = DateTime.MinValue;
    DateTime _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;

    bool IsRunStrategyDailyReplaceAllActive()
    {
        if (!_runStrategyAutoRunStartedThisManagerSession)
            return false;

        RunAllStrategySettings settings;
        lock (_runStrategyLock)
            settings = _runStrategySettings;

        bool runtimeArmed;
        lock (_runStrategyDailyReplaceAllLock)
            runtimeArmed = _runStrategyDailyReplaceAllRuntimeArmed;

        return runtimeArmed
               && settings.Mode == RunAllStrategyMode.PrimeFresh
               && settings.DailyReplaceAll
               && settings.PrimeModeArmed
               && !settings.PrimeModeSuspended;
    }

    // Trong bootstrap đầu phiên hoặc đúng phiên THAY ALL hằng ngày, bộ điều phối
    // riêng đang nắm quyền tạo PRF MỚI HOÀN TOÀN. Capacity/queue Tự bù thông thường
    // phải đứng ngoài để không chen một PRF reuse vào giữa chuỗi đóng 1 -> mở 1.
    bool IsRunStrategyDailyReplaceAllRefreshBusy()
    {
        if (!IsRunStrategyDailyReplaceAllActive())
            return false;

        lock (_runStrategyDailyReplaceAllLock)
        {
            return _runStrategyDailyReplaceAllBootstrapPending
                   || _runStrategyDailyReplaceAllCycleActive;
        }
    }

    // Ngoài phiên THAY ALL, Tự bù VẪN hoạt động và quét toàn bộ PRF chờ.
    // Chỉ nhánh CREATE mới bị chặn. Tạo mới chỉ do bộ điều phối THAY ALL gọi với
    // forceNewProfileOnly=true tại giờ thay/khởi tạo target.
    bool IsRunStrategyDailyReplaceAllAutoCreateBlocked()
        => IsRunStrategyDailyReplaceAllActive();

    void ResetRunStrategyDailyReplaceAllRuntime(string source)
    {
        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllRuntimeArmed = false;
            _runStrategyDailyReplaceAllBootstrapPending = false;
            _runStrategyDailyReplaceAllCycleActive = false;
            _runStrategyDailyReplaceAllLastCompletedLocalDate = DateTime.MinValue;
            _runStrategyDailyReplaceAllCycleScheduledLocalDate = DateTime.MinValue;
            _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
            _runStrategyDailyReplaceAllVictims.Clear();
        }

        UpdateAutoCloseToolbarButtonText();
        _log.Info($"[RUN_DAILY_REPLACE_ALL_RESET] source={source}");
    }

    void PrepareRunStrategyDailyReplaceAllStart(int target, string source)
    {
        InitializeAutoReplacementFeature();
        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllRuntimeArmed = true;
            // Giữ capacity/queue thường đứng ngoài ngay từ trước StartAll/fill đầu phiên.
            // Initialize... sẽ hạ cờ này ngay nếu target đã đủ.
            _runStrategyDailyReplaceAllBootstrapPending = true;
        }

        // Bỏ request cũ của chiến lược trước. Sau Start, THAY ALL vẫn arm Tự bù,
        // nhưng Tự bù chỉ được reuse PRF chờ; CREATE mới bị hard-gate riêng.
        ClearPendingAutoReplacementForDailyReplaceAll(source);
        ArmAutoReplacementSession("daily_replace_all:" + source);
        UpdateAutoCloseToolbarButtonText();

        _log.Info(
            $"[RUN_DAILY_REPLACE_ALL_PREPARE] source={source} target={target} autoRefill=REUSE_ONLY autoCreateOutsideRefresh=OFF createMode=FORCE_NEW_EXISTING_PIPELINE");
    }

    void ClearPendingAutoReplacementForDailyReplaceAll(string source)
    {
        var discarded = 0;
        lock (_autoReplacementQueueLock)
        {
            discarded = _autoReplacementQueue.Count;
            _autoReplacementQueue.Clear();
            SaveAutoReplacementQueueUnsafe();
        }

        InvalidateAutoReplacementExecution("daily_replace_all:" + source);
        ClearAutoReplacementUiPhase();

        if (discarded > 0)
        {
            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_CLEAR_REPLACEMENT_QUEUE] source={source} discarded={discarded}");
        }
    }

    (DateTime Start, DateTime End) GetRunStrategyDailyReplaceAllCreateWindow(
        RunAllStrategySettings settings,
        DateTime now)
    {
        var start = now.Date.AddHours(settings.PrimeStartHour);
        return (start, start.Add(RunStrategyDailyReplaceAllCreateWindow));
    }

    bool IsRunStrategyDailyReplaceAllCreateWindowOpen(
        RunAllStrategySettings settings,
        DateTime now,
        out DateTime windowStart,
        out DateTime windowEnd)
    {
        (windowStart, windowEnd) = GetRunStrategyDailyReplaceAllCreateWindow(settings, now);
        return now >= windowStart && now < windowEnd;
    }

    void ExpireRunStrategyDailyReplaceAllForToday(
        RunAllStrategySettings settings,
        string source)
    {
        var now = GetToolNow().LocalDateTime;
        var (_, windowEnd) = GetRunStrategyDailyReplaceAllCreateWindow(settings, now);
        int remainingVictims;
        DateTime cycleDate;

        lock (_runStrategyDailyReplaceAllLock)
        {
            remainingVictims = _runStrategyDailyReplaceAllVictims.Count;
            cycleDate = _runStrategyDailyReplaceAllCycleScheduledLocalDate == DateTime.MinValue
                ? now.Date
                : _runStrategyDailyReplaceAllCycleScheduledLocalDate.Date;

            _runStrategyDailyReplaceAllBootstrapPending = false;
            _runStrategyDailyReplaceAllCycleActive = false;
            _runStrategyDailyReplaceAllVictims.Clear();
            _runStrategyDailyReplaceAllCycleScheduledLocalDate = DateTime.MinValue;
            _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
            _runStrategyDailyReplaceAllLastCompletedLocalDate = now.Date;
        }

        // Hết cửa sổ CREATE: trả quyền về Tự bù thường. THAY ALL vẫn active nên
        // hard-gate CREATE ngoài refresh tiếp tục chặn; queue chỉ được reuse PRF chờ.
        ArmAutoReplacementSession("daily_replace_all_window_closed");
        UpdateAutoCloseToolbarButtonText();
        _log.Warn(
            $"[RUN_DAILY_REPLACE_ALL_WINDOW_CLOSED] source={source} now={now:yyyy-MM-dd HH:mm:ss} " +
            $"deadline={windowEnd:yyyy-MM-dd HH:mm:ss} cycleDate={cycleDate:yyyy-MM-dd} " +
            $"remainingVictims={remainingVictims} filled={CountAutoReplacementFulfilledSlots()} " +
            "action=STOP_DAILY_REFRESH_NO_MORE_CREATE_TODAY autoRefill=REUSE_ONLY");

        if (!_closing && GetAutoReplacementPendingCount() > 0)
            _ = RunAutoReplacementQueueAsync();
    }

    void InitializeRunStrategyDailyReplaceAllRuntime(
        RunAllStrategySettings settings,
        int target,
        int filled,
        string source)
    {
        var now = GetToolNow().LocalDateTime;
        var (scheduledToday, createDeadlineToday) =
            GetRunStrategyDailyReplaceAllCreateWindow(settings, now);
        var createWindowOpen = now >= scheduledToday && now < createDeadlineToday;

        // Trước Giờ thay chỉ ARM. Sau deadline 2 giờ cũng chỉ ARM/reuse, tuyệt đối
        // không bootstrap CREATE. Chỉ đúng trong cửa sổ [Giờ thay, Giờ thay+2h)
        // mới cho bootstrap phần target còn thiếu bằng create pipeline hiện tại.
        var bootstrapPending = createWindowOpen && filled < target;

        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllRuntimeArmed = true;
            _runStrategyDailyReplaceAllBootstrapPending = bootstrapPending;
            _runStrategyDailyReplaceAllCycleActive = false;
            _runStrategyDailyReplaceAllVictims.Clear();
            _runStrategyDailyReplaceAllCycleScheduledLocalDate = DateTime.MinValue;
            _runStrategyDailyReplaceAllNextAttemptUtc = bootstrapPending
                ? DateTime.UtcNow.Add(RunStrategyDailyReplaceAllRetryDelay)
                : DateTime.MinValue;

            // Trước giờ thay: để scheduler được phép mở cycle hôm nay khi tới giờ.
            // Trong/qua cửa sổ hôm nay: coi lần Start hiện tại là baseline hôm nay;
            // đặc biệt sau deadline thì không được "bù muộn" bằng CREATE.
            _runStrategyDailyReplaceAllLastCompletedLocalDate =
                now < scheduledToday
                    ? now.Date.AddDays(-1)
                    : now.Date;
        }

        UpdateAutoCloseToolbarButtonText();
        _log.Info(
            $"[RUN_DAILY_REPLACE_ALL_ARMED] source={source} window={scheduledToday:HH:mm}-{createDeadlineToday:HH:mm} target={target} " +
            $"filled={filled} bootstrapPending={bootstrapPending} baselineDate={_runStrategyDailyReplaceAllLastCompletedLocalDate:yyyy-MM-dd} " +
            "autoRefill=REUSE_ALL autoCreateOutsideRefresh=OFF");
    }

    async Task<int> EnsureRunStrategyDailyReplaceAllStartupTargetAsync(
        RunAllStrategySettings settings,
        int target,
        CancellationToken token)
    {
        target = Math.Max(0, target);
        SetRunAllDesiredTarget(target, "daily_replace_all_fill");
        ArmAutoReplacementSession("daily_replace_all_fill");

        var safety = 0;
        while (!_closing && safety++ < Math.Max(1, target + 3))
        {
            token.ThrowIfCancellationRequested();

            var occupied = CountAutoReplacementFulfilledSlots();
            if (occupied >= target)
                return occupied;

            var createNow = GetToolNow().LocalDateTime;
            if (!IsRunStrategyDailyReplaceAllCreateWindowOpen(
                    settings,
                    createNow,
                    out var windowStart,
                    out var windowEnd))
            {
                _log.Warn(
                    $"[RUN_DAILY_REPLACE_ALL_CREATE_WINDOW_BLOCKED] now={createNow:yyyy-MM-dd HH:mm:ss} " +
                    $"window={windowStart:yyyy-MM-dd HH:mm:ss}->{windowEnd:yyyy-MM-dd HH:mm:ss} " +
                    $"target={target} filled={occupied} action=NO_CREATE");
                return occupied;
            }

            var slot = occupied + 1;
            var created = await TryCreateRunStrategyReplacementAsync(
                $"THAY_ALL_SLOT_{slot:00}",
                "THAY_ALL",
                token,
                forceNewProfileOnly: true);

            if (!created)
                return CountAutoReplacementFulfilledSlots();
        }

        return CountAutoReplacementFulfilledSlots();
    }

    async Task CheckRunStrategyDailyReplaceAllAsync(
        RunAllStrategySettings settings,
        int target,
        CancellationToken token)
    {
        if (!IsRunStrategyDailyReplaceAllActive())
            return;

        token.ThrowIfCancellationRequested();
        target = Math.Max(1, target);
        var now = GetToolNow().LocalDateTime;
        var (scheduled, createDeadline) =
            GetRunStrategyDailyReplaceAllCreateWindow(settings, now);

        bool bootstrapPending;
        bool cycleActive;
        DateTime nextAttemptUtc;
        DateTime lastCompletedDate;

        lock (_runStrategyDailyReplaceAllLock)
        {
            bootstrapPending = _runStrategyDailyReplaceAllBootstrapPending;
            cycleActive = _runStrategyDailyReplaceAllCycleActive;
            nextAttemptUtc = _runStrategyDailyReplaceAllNextAttemptUtc;
            lastCompletedDate = _runStrategyDailyReplaceAllLastCompletedLocalDate;
        }

        // Deadline là hard stop cho CREATE của ngày hôm nay. Nếu cycle/bootstrap
        // còn dang dở thì kết thúc phần THAY ALL hôm nay, không đóng thêm PRF cũ và
        // trả về chế độ Tự bù reuse-only cho tới cửa sổ ngày mai.
        if (now >= createDeadline)
        {
            if (bootstrapPending || cycleActive || lastCompletedDate.Date < now.Date)
                ExpireRunStrategyDailyReplaceAllForToday(settings, "deadline_reached");
            return;
        }

        if (DateTime.UtcNow < nextAttemptUtc)
            return;

        if (bootstrapPending)
        {
            // Hard-gate chống race/stale state: ngoài cửa sổ CREATE đều không được
            // gọi create pipeline, kể cả cờ bootstrap bị giữ nhầm.
            if (now < scheduled)
            {
                lock (_runStrategyDailyReplaceAllLock)
                {
                    _runStrategyDailyReplaceAllBootstrapPending = false;
                    _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
                }

                UpdateAutoCloseToolbarButtonText();
                _log.Warn(
                    $"[RUN_DAILY_REPLACE_ALL_PREHOUR_CREATE_BLOCKED] now={now:yyyy-MM-dd HH:mm:ss} " +
                    $"window={scheduled:yyyy-MM-dd HH:mm:ss}->{createDeadline:yyyy-MM-dd HH:mm:ss} " +
                    $"target={target} action=ARM_ONLY_NO_CREATE");
                return;
            }

            var bootstrapFilled = await EnsureRunStrategyDailyReplaceAllStartupTargetAsync(
                settings,
                target,
                token);

            if (bootstrapFilled >= target)
            {
                lock (_runStrategyDailyReplaceAllLock)
                {
                    _runStrategyDailyReplaceAllBootstrapPending = false;
                    _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
                    _runStrategyDailyReplaceAllLastCompletedLocalDate = now.Date;
                }

                UpdateAutoCloseToolbarButtonText();
                _log.Info(
                    $"[RUN_DAILY_REPLACE_ALL_BOOTSTRAP_DONE] target={target} filled={bootstrapFilled} " +
                    $"baselineDate={_runStrategyDailyReplaceAllLastCompletedLocalDate:yyyy-MM-dd} autoRefill=REUSE_ALL autoCreateOutsideRefresh=OFF");
            }
            else
            {
                var afterAttempt = GetToolNow().LocalDateTime;
                if (afterAttempt >= createDeadline)
                {
                    ExpireRunStrategyDailyReplaceAllForToday(settings, "bootstrap_deadline_reached");
                    return;
                }

                lock (_runStrategyDailyReplaceAllLock)
                    _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.UtcNow.Add(RunStrategyDailyReplaceAllRetryDelay);

                _log.Warn(
                    $"[RUN_DAILY_REPLACE_ALL_BOOTSTRAP_WAIT] target={target} filled={bootstrapFilled} retryIn={RunStrategyDailyReplaceAllRetryDelay:c} " +
                    $"deadline={createDeadline:HH:mm:ss}");
            }

            return;
        }

        if (!cycleActive)
        {
            if (now < scheduled || lastCompletedDate.Date >= now.Date)
                return;

            StartRunStrategyDailyReplaceAllCycle(settings, target, now.Date);
        }

        await ContinueRunStrategyDailyReplaceAllCycleAsync(
            settings,
            target,
            token);
    }

    void StartRunStrategyDailyReplaceAllCycle(
        RunAllStrategySettings settings,
        int target,
        DateTime scheduledLocalDate)
    {
        // Đánh dấu cycle trước để capacity/queue thường không chen vào đúng khe giữa
        // lúc bắt đầu thay dàn và lúc danh sách victim được chụp.
        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllCycleActive = true;
            _runStrategyDailyReplaceAllCycleScheduledLocalDate = scheduledLocalDate.Date;
            _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
            _runStrategyDailyReplaceAllVictims.Clear();
        }

        // Thu hồi mọi request bù cũ trước khi chụp dàn cần thay. Trong suốt cycle,
        // capacity/queue thường sẽ tạm đứng ngoài; bộ điều phối làm đúng đóng 1 -> mở 1.
        ClearPendingAutoReplacementForDailyReplaceAll("daily_cycle_start");
        ArmAutoReplacementSession("daily_replace_all_cycle_start");
        ResetAutoReplacementCreateLimitSession("daily_replace_all_cycle_start");

        var victims = GetRunStrategyActiveContexts()
            .Select(ctx => ctx.Profile.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, NaturalProfileNameOrder)
            .ToList();

        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllVictims.Clear();
            _runStrategyDailyReplaceAllVictims.AddRange(victims);
        }

        UpdateAutoCloseToolbarButtonText();
        var cycleWindowStart = scheduledLocalDate.Date.AddHours(settings.PrimeStartHour);
        var cycleWindowEnd = cycleWindowStart.Add(RunStrategyDailyReplaceAllCreateWindow);
        _log.Warn(
            $"[RUN_DAILY_REPLACE_ALL_CYCLE_BEGIN] date={scheduledLocalDate:yyyy-MM-dd} " +
            $"window={cycleWindowStart:HH:mm}-{cycleWindowEnd:HH:mm} target={target} victims={victims.Count} " +
            "action=ONE_OUT_ONE_NEW_THEN_FILL_REMAINDER");
    }

    async Task ContinueRunStrategyDailyReplaceAllCycleAsync(
        RunAllStrategySettings settings,
        int target,
        CancellationToken token)
    {
        string? victimName;
        DateTime cycleDate;
        lock (_runStrategyDailyReplaceAllLock)
        {
            victimName = _runStrategyDailyReplaceAllVictims.FirstOrDefault();
            cycleDate = _runStrategyDailyReplaceAllCycleScheduledLocalDate;
        }

        var cycleNow = GetToolNow().LocalDateTime;
        if (!IsRunStrategyDailyReplaceAllCreateWindowOpen(
                settings,
                cycleNow,
                out _,
                out var cycleWindowEnd))
        {
            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_CYCLE_WINDOW_BLOCKED] now={cycleNow:yyyy-MM-dd HH:mm:ss} " +
                $"deadline={cycleWindowEnd:yyyy-MM-dd HH:mm:ss} action=STOP_CYCLE_NO_CREATE");
            ExpireRunStrategyDailyReplaceAllForToday(settings, "cycle_window_closed");
            return;
        }

        // Pha 1: đúng yêu cầu đóng 1 -> mở 1. Chỉ khi PRF cũ đã bị delete pipeline
        // hiện tại xóa sạch mới tạo đúng 1 PRF MỚI HOÀN TOÀN rồi mới sang victim kế.
        if (!string.IsNullOrWhiteSpace(victimName))
        {
            token.ThrowIfCancellationRequested();

            if (!IsRunStrategyDailyReplaceAllVictimDeleted(victimName))
            {
                if (_autoRetiredProfileDeleteInProgress.Contains(victimName))
                    return;

                var noted = await MarkRunStrategyDailyReplaceAllExcelNoteAsync(
                    victimName,
                    token);

                if (!noted)
                {
                    lock (_runStrategyDailyReplaceAllLock)
                        _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.UtcNow.Add(RunStrategyDailyReplaceAllRetryDelay);

                    _log.Warn(
                        $"[RUN_DAILY_REPLACE_ALL_NOTE_WAIT] profile={victimName} retryIn={RunStrategyDailyReplaceAllRetryDelay:c}");
                    return;
                }

                QueueAutoDeleteRetiredProfileAfterExcelNote(
                    victimName,
                    "THAY_ALL");

                _log.Warn(
                    $"[RUN_DAILY_REPLACE_ALL_DELETE_QUEUED] profile={victimName} note=thay_all pipeline=EXISTING_AUTO_RETIRED_DELETE next=OPEN_ONE_FRESH_AFTER_DELETE");
                return;
            }

            var occupiedBeforePairCreate = CountAutoReplacementFulfilledSlots();
            if (occupiedBeforePairCreate < target)
            {
                var created = await TryCreateRunStrategyReplacementAsync(
                    "THAY_ALL_REPLACE_" + victimName,
                    "THAY_ALL",
                    token,
                    forceNewProfileOnly: true);

                if (!created)
                {
                    lock (_runStrategyDailyReplaceAllLock)
                        _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.UtcNow.Add(RunStrategyDailyReplaceAllRetryDelay);

                    _log.Warn(
                        $"[RUN_DAILY_REPLACE_ALL_PAIR_CREATE_WAIT] victim={victimName} target={target} " +
                        $"occupied={CountAutoReplacementFulfilledSlots()} retryIn={RunStrategyDailyReplaceAllRetryDelay:c} pipeline=EXISTING_AUTO_PROFILE");
                    return;
                }
            }

            lock (_runStrategyDailyReplaceAllLock)
            {
                if (_runStrategyDailyReplaceAllVictims.Count > 0
                    && _runStrategyDailyReplaceAllVictims[0].Equals(victimName, StringComparison.OrdinalIgnoreCase))
                {
                    _runStrategyDailyReplaceAllVictims.RemoveAt(0);
                }
                else
                {
                    _runStrategyDailyReplaceAllVictims.RemoveAll(name =>
                        name.Equals(victimName, StringComparison.OrdinalIgnoreCase));
                }

                _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
            }

            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_PAIR_DONE] victim={victimName} action=DELETE_ONE_OPEN_ONE " +
                $"occupied={CountAutoReplacementFulfilledSlots()}/{target}");
            return;
        }

        // Pha 2: toàn bộ dàn cũ đã được xử lý. Nếu đầu ngày thiếu PRF hoặc một lượt
        // tạo trước đó không lấp đủ target, lúc này mới được mở liên tục phần còn thiếu.
        SetRunAllDesiredTarget(target, "daily_replace_all_after_all_old_deleted");
        ArmAutoReplacementSession("daily_replace_all_after_all_old_deleted");

        var filled = await EnsureRunStrategyDailyReplaceAllStartupTargetAsync(
            settings,
            target,
            token);

        if (filled < target)
        {
            lock (_runStrategyDailyReplaceAllLock)
                _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.UtcNow.Add(RunStrategyDailyReplaceAllRetryDelay);

            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_FINAL_FILL_WAIT] target={target} filled={filled} retryIn={RunStrategyDailyReplaceAllRetryDelay:c} " +
                "pipeline=EXISTING_AUTO_PROFILE");
            return;
        }

        lock (_runStrategyDailyReplaceAllLock)
        {
            _runStrategyDailyReplaceAllLastCompletedLocalDate = cycleDate.Date;
            _runStrategyDailyReplaceAllCycleActive = false;
            _runStrategyDailyReplaceAllVictims.Clear();
            _runStrategyDailyReplaceAllCycleScheduledLocalDate = DateTime.MinValue;
            _runStrategyDailyReplaceAllNextAttemptUtc = DateTime.MinValue;
        }

        UpdateAutoCloseToolbarButtonText();
        _log.Warn(
            $"[RUN_DAILY_REPLACE_ALL_CYCLE_DONE] date={cycleDate:yyyy-MM-dd} target={target} filled={filled} " +
            $"autoRefill=REUSE_ALL autoCreateOutsideRefresh=OFF next={cycleDate.Date.AddDays(1).AddHours(settings.PrimeStartHour):yyyy-MM-dd HH:mm}");

        // Nếu BAN/FAULT phát sinh đúng lúc cycle chạy thì request đã được giữ lại.
        // Trả quyền cho queue sau khi dàn mới đủ; slot gate sẽ tự loại request dư.
        if (!_closing && GetAutoReplacementPendingCount() > 0)
            _ = RunAutoReplacementQueueAsync();
    }

    bool IsRunStrategyDailyReplaceAllVictimDeleted(string profileName)
    {
        if (_autoRetiredProfileDeleted.Contains(profileName))
            return true;

        try
        {
            var exists = _profileService.Load().Profiles.Any(profile =>
                profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                _autoRetiredProfileDeleted.Add(profileName);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_DELETE_VERIFY_WARN] profile={profileName} error={ex.Message}");
        }

        return false;
    }

    async Task<bool> MarkRunStrategyDailyReplaceAllExcelNoteAsync(
        string profileName,
        CancellationToken token)
    {
        try
        {
            return await RunAccountPoolIoAsync(
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                        _accountPoolService.ReloadCurrentExcel();

                    var items = _accountPoolService.Load();
                    var account = items.FirstOrDefault(item =>
                        item.AssignedProfile.Equals(
                            profileName,
                            StringComparison.OrdinalIgnoreCase));

                    if (account is null)
                    {
                        _log.Warn(
                            $"[RUN_DAILY_REPLACE_ALL_NOTE_MISSING_ACCOUNT] profile={profileName}");
                        return false;
                    }

                    var currentNote = (account.Note ?? "").Trim();
                    if (!currentNote.Equals("ban", StringComparison.OrdinalIgnoreCase))
                    {
                        _accountPoolService.Upsert(
                            account with { Note = "thay_all" });
                    }

                    if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                        _accountPoolService.ReloadCurrentExcel();

                    var verified = _accountPoolService.Load().FirstOrDefault(item =>
                        item.Id.Equals(account.Id, StringComparison.OrdinalIgnoreCase));

                    verified ??= _accountPoolService.Load().FirstOrDefault(item =>
                        item.SourceRow == account.SourceRow
                        && item.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase));

                    var verifiedNote = (verified?.Note ?? "").Trim();
                    var ok = verifiedNote.Equals("thay_all", StringComparison.OrdinalIgnoreCase)
                             || verifiedNote.Equals("ban", StringComparison.OrdinalIgnoreCase);

                    if (ok)
                    {
                        _log.Info(
                            $"[RUN_DAILY_REPLACE_ALL_NOTE_OK] profile={profileName} user={account.Username} note={verifiedNote} banPriority={verifiedNote.Equals("ban", StringComparison.OrdinalIgnoreCase)}");
                    }
                    else
                    {
                        _log.Warn(
                            $"[RUN_DAILY_REPLACE_ALL_NOTE_VERIFY_FAIL] profile={profileName} user={account.Username} actual={verifiedNote}");
                    }

                    return ok;
                },
                token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_DAILY_REPLACE_ALL_NOTE_ERROR] profile={profileName} error={ex.Message}");
            return false;
        }
    }
}
