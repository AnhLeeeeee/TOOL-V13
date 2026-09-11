using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed record AutoReplacementCleanupResult(
        bool Succeeded,
        string Detail);

    sealed record AutoReplacementSlotGateResult(
        bool CanOpenReplacement,
        bool AlreadySatisfied,
        int TargetSlots,
        int OccupiedSlots,
        string Detail);

    readonly object _autoReplacementFixedSlotLock = new();
    int _autoReplacementTargetSlots;

    void CaptureAutoReplacementTargetBeforeAutoClose(
        ProfileContext ctx,
        string reason)
    {
        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return;

        var occupied = CountAutoReplacementOccupiedSlots();

        // Profile đang Tự đóng có thể chưa nằm trong expected set vì một race trạng thái.
        // Bảo đảm chính slot đang đóng vẫn được tính vào target trước khi cleanup.
        if (!IsAutoReplacementSlotCurrentlyCounted(ctx.Profile.Name))
            occupied++;

        lock (_autoReplacementFixedSlotLock)
        {
            if (occupied > _autoReplacementTargetSlots)
            {
                var old = _autoReplacementTargetSlots;
                _autoReplacementTargetSlots = occupied;

                _log.Info(
                    $"[AUTO_REPLACE_TARGET_CAPTURE] old={old} target={_autoReplacementTargetSlots} profile={ctx.Profile.Name} reason={reason}");
            }
        }
    }

    void TrackAutoReplacementTargetRuntimeCommand(
        ProfileContext ctx,
        string command)
    {
        command = (command ?? "").Trim().ToLowerInvariant();
        var profileName = ctx.Profile.Name;

        // start_auto là profile do Tự bù/Auto Profile tạo; không được tự làm target phình lên.
        if (command == "start")
        {
            var occupied = CountAutoReplacementOccupiedSlots();

            lock (_autoReplacementFixedSlotLock)
            {
                if (occupied > _autoReplacementTargetSlots)
                {
                    var old = _autoReplacementTargetSlots;
                    _autoReplacementTargetSlots = occupied;
                    _log.Info(
                        $"[AUTO_REPLACE_TARGET_MANUAL_EXPAND] old={old} target={_autoReplacementTargetSlots} profile={profileName}");
                }
            }

            return;
        }

        if (command != "stop")
            return;

        // STOP do AutoClose/cleanup của profile bù lỗi không phải ý định giảm số suất của user.
        if (_autoCloseInProgressProfiles.Contains(profileName)
            || _autoReplacementClaimedProfiles.Contains(profileName))
        {
            return;
        }

        lock (_autoReplacementFixedSlotLock)
        {
            if (_autoReplacementTargetSlots <= 0)
                return;

            var old = _autoReplacementTargetSlots;
            _autoReplacementTargetSlots = Math.Max(
                CountAutoReplacementOccupiedSlots(),
                _autoReplacementTargetSlots - 1);

            if (old != _autoReplacementTargetSlots)
            {
                _log.Info(
                    $"[AUTO_REPLACE_TARGET_MANUAL_SHRINK] old={old} target={_autoReplacementTargetSlots} profile={profileName}");
            }
        }
    }

    bool IsAutoReplacementSlotCurrentlyCounted(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        if (_autoCloseExpectedRunningProfiles.Contains(profileName)
            || _autoReplacementClaimedProfiles.Contains(profileName))
        {
            return true;
        }

        if (!_contexts.TryGetValue(profileName, out var ctx))
            return false;

        var state = GetEffectiveRuntimeState(ctx);
        return state is RuntimeStateRunning or RuntimeStateRecovering;
    }

    int CountAutoReplacementOccupiedSlots()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _autoCloseExpectedRunningProfiles)
            names.Add(name);

        foreach (var name in _autoReplacementClaimedProfiles)
            names.Add(name);

        foreach (var ctx in _contexts.Values)
        {
            var state = GetEffectiveRuntimeState(ctx);
            if (state is RuntimeStateRunning or RuntimeStateRecovering)
            {
                names.Add(ctx.Profile.Name);
                continue;
            }

            // Safety net: profile bù lỗi có thể đang STOPPED/DISCONNECTED nhưng
            // Worker/tab/Opening vẫn còn. Nếu không tính các runtime vật lý này,
            // slot gate tưởng còn chỗ trống và tiếp tục mở Chrome mới.
            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
            catch { workerAlive = ctx.Worker is not null; }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            if (workerAlive || tabOpen || ctx.Opening)
                names.Add(ctx.Profile.Name);
        }

        return names.Count;
    }

    sealed record AutoReplacementExpectedRuntimeProbe(
        bool Present,
        bool WorkerAlive,
        bool WindowAlive,
        bool CdpListening,
        bool Opening,
        string Source);

    async Task<AutoReplacementExpectedRuntimeProbe> ProbeAutoReplacementExpectedRuntimeAsync(
        string profileName)
    {
        profileName = (profileName ?? "").Trim();

        if (profileName.Length == 0)
        {
            return new AutoReplacementExpectedRuntimeProbe(
                false, false, false, false, false, "empty_name");
        }

        // Profile bù đã claim slot phải tiếp tục được tính ngay cả khi Chrome chưa kịp mở.
        // Đây là reservation thật, không phải marker ExpectedRunning cũ.
        if (_autoReplacementClaimedProfiles.Contains(profileName))
        {
            return new AutoReplacementExpectedRuntimeProbe(
                true, false, false, false, true, "replacement_claimed");
        }

        if (_contexts.TryGetValue(profileName, out var ctx))
        {
            var workerAlive = false;
            try
            {
                workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            }
            catch
            {
                // Không đọc được Worker thì fail-closed: giữ slot, không prune nhầm.
                workerAlive = ctx.Worker is not null;
            }

            var windowAlive = HasAutoCloseCachedLiveChromeWindow(ctx);
            var cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);
            var opening = ctx.Opening;

            return new AutoReplacementExpectedRuntimeProbe(
                workerAlive || windowAlive || cdpListening || opening,
                workerAlive,
                windowAlive,
                cdpListening,
                opening,
                "context");
        }

        // Context đã mất: không còn Worker/window/opening để quan sát. Dùng CDP riêng
        // của profile trong catalog làm tín hiệu runtime. Nếu profile cũng đã biến mất
        // khỏi catalog thì ExpectedRunning chắc chắn là marker cũ.
        try
        {
            var catalog = _profileService.Load();
            var profile = catalog.Profiles.FirstOrDefault(x =>
                x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            if (profile is null)
            {
                return new AutoReplacementExpectedRuntimeProbe(
                    false, false, false, false, false, "missing_context_and_catalog");
            }

            var cdpListening = await IsAutoCloseCdpPortListeningAsync(profile.CdpPort);
            return new AutoReplacementExpectedRuntimeProbe(
                cdpListening,
                false,
                false,
                cdpListening,
                false,
                "catalog_cdp");
        }
        catch (Exception ex)
        {
            // Không xác minh được => giữ marker để tránh mở thừa profile.
            _log.Warn(
                $"[AUTO_REPLACE_EXPECTED_PROBE_WARN] profile={profileName} error={ex.Message} action=KEEP_EXPECTED");

            return new AutoReplacementExpectedRuntimeProbe(
                true, false, false, false, false, "probe_error_keep_expected");
        }
    }

    async Task<int> PruneStaleAutoReplacementExpectedRunningAsync(
        AutoReplacementRequest request)
    {
        // Snapshot trước khi await để không enumerate trực tiếp HashSet qua nhiều nhịp async.
        var expectedNames = _autoCloseExpectedRunningProfiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (expectedNames.Length == 0)
            return 0;

        var staleCandidates = new List<string>();

        // PASS 1: chỉ đánh dấu ứng viên stale, chưa xóa ngay.
        foreach (var profileName in expectedNames)
        {
            if (!_autoCloseExpectedRunningProfiles.Contains(profileName))
                continue;

            var probe = await ProbeAutoReplacementExpectedRuntimeAsync(profileName);

            _log.Info(
                $"[AUTO_REPLACE_EXPECTED_SLOT_PROBE] request={request.Id} profile={profileName} pass=1/2 present={probe.Present} workerAlive={probe.WorkerAlive} windowAlive={probe.WindowAlive} cdpListening={probe.CdpListening} opening={probe.Opening} source={probe.Source}");

            if (!probe.Present)
                staleCandidates.Add(profileName);
        }

        if (staleCandidates.Count == 0)
            return 0;

        // Chậm mà chắc: đợi 1-2 giây rồi xác minh lại tất cả ứng viên stale.
        // Chỉ cần MỘT lần wait chung, không cộng dồn 1-2 giây cho từng profile.
        var delayMs = Random.Shared.Next(1000, 2001);
        _log.Info(
            $"[AUTO_REPLACE_EXPECTED_SLOT_WAIT] request={request.Id} candidates={staleCandidates.Count} delayMs={delayMs} nextPass=2/2");
        await Task.Delay(delayMs);

        var removed = 0;

        // PASS 2: chỉ prune khi runtime vẫn sạch lần thứ hai.
        foreach (var profileName in staleCandidates)
        {
            if (!_autoCloseExpectedRunningProfiles.Contains(profileName))
                continue;

            var probe = await ProbeAutoReplacementExpectedRuntimeAsync(profileName);

            _log.Info(
                $"[AUTO_REPLACE_EXPECTED_SLOT_PROBE] request={request.Id} profile={profileName} pass=2/2 present={probe.Present} workerAlive={probe.WorkerAlive} windowAlive={probe.WindowAlive} cdpListening={probe.CdpListening} opening={probe.Opening} source={probe.Source}");

            if (probe.Present)
            {
                _log.Info(
                    $"[AUTO_REPLACE_EXPECTED_SLOT_KEEP] request={request.Id} profile={profileName} reason=runtime_reappeared");
                continue;
            }

            var hadExpected = _autoCloseExpectedRunningProfiles.Contains(profileName);
            ClearAutoCloseExpectedRunning(
                profileName,
                "auto_replace_slot_gate_stale_runtime_2pass");

            if (hadExpected && !_autoCloseExpectedRunningProfiles.Contains(profileName))
            {
                removed++;
                _log.Warn(
                    $"[AUTO_REPLACE_EXPECTED_SLOT_PRUNED] request={request.Id} profile={profileName} reason=worker_window_cdp_opening_closed_2pass");
            }
        }

        if (removed > 0)
        {
            _log.Warn(
                $"[AUTO_REPLACE_EXPECTED_SLOT_PRUNE_SUMMARY] request={request.Id} removed={removed} before={expectedNames.Length} remaining={_autoCloseExpectedRunningProfiles.Count}");
        }

        return removed;
    }

    AutoReplacementSlotGateResult EvaluateAutoReplacementFixedSlotGate(
        AutoReplacementRequest request)
    {
        var occupied = CountAutoReplacementOccupiedSlots();
        int target;

        lock (_autoReplacementFixedSlotLock)
        {
            // Nếu target chưa được capture (ví dụ Manager adopt Worker cũ),
            // tối thiểu giữ đúng số suất hiện có + request đang cần bù.
            if (_autoReplacementTargetSlots <= 0)
                _autoReplacementTargetSlots = Math.Max(1, occupied + 1);

            target = _autoReplacementTargetSlots;
        }

        if (occupied >= target)
        {
            return new AutoReplacementSlotGateResult(
                false,
                true,
                target,
                occupied,
                $"Đã đủ suất: occupied={occupied}, target={target}. Không mở thêm Chrome/profile bù.");
        }

        // Queue xử lý tuần tự nên chỉ cần một chỗ trống là request hiện tại được phép lấp.
        return new AutoReplacementSlotGateResult(
            true,
            false,
            target,
            occupied,
            $"Có 1 slot trống: occupied={occupied}, target={target}.");
    }

    async Task<AutoReplacementCleanupResult> VerifyAutoReplacementRuntimeClosedTwiceAsync(
        ProfileContext ctx)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();

        // Tự bù ưu tiên chậm mà chắc: chỉ giải phóng slot khi các tín hiệu runtime
        // đều sạch HAI lần liên tiếp. Lượt thứ hai diễn ra sau 1-2 giây để tránh
        // trường hợp Chrome/Worker vừa tắt nhưng còn process/cửa sổ/CDP hồi sinh muộn.
        for (var pass = 1; pass <= 2; pass++)
        {
            var workerAlive = false;
            try
            {
                workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            }
            catch
            {
                // Không xác minh được Worker => fail-closed, chưa cho phép Tự bù.
                workerAlive = ctx.Worker is not null;
            }

            var cachedWindowAlive = HasAutoCloseCachedLiveChromeWindow(ctx);
            var cdpListening = await IsAutoCloseCdpPortListeningAsync(ctx.Profile.CdpPort);
            var opening = ctx.Opening;

            _log.Info(
                $"[AUTO_REPLACE_STABLE_CLOSE_CHECK] profile={profileName} pass={pass}/2 workerAlive={workerAlive} windowAlive={cachedWindowAlive} cdpListening={cdpListening} opening={opening} port={ctx.Profile.CdpPort}");

            if (workerAlive || cachedWindowAlive || cdpListening || opening)
            {
                return new AutoReplacementCleanupResult(
                    false,
                    $"Chưa đóng ổn định ở lượt {pass}/2: "
                    + $"worker={workerAlive}, window={cachedWindowAlive}, "
                    + $"cdp={cdpListening}, opening={opening}. Tự bù sẽ thử lại sau.");
            }

            if (pass == 1)
            {
                var delayMs = Random.Shared.Next(1000, 2001);
                _log.Info(
                    $"[AUTO_REPLACE_STABLE_CLOSE_WAIT] profile={profileName} delayMs={delayMs} nextPass=2/2");
                await Task.Delay(delayMs);
            }
        }

        _log.Info(
            $"[AUTO_REPLACE_STABLE_CLOSE_CONFIRMED] profile={profileName} passes=2/2 window=closed cdp=closed worker=closed");

        return new AutoReplacementCleanupResult(
            true,
            "STABLE_CLOSED: xác minh sạch 2/2 lượt, cách nhau 1-2 giây.");
    }

    async Task<AutoReplacementCleanupResult> VerifyAutoReplacementProfilePathClosedTwiceAsync(
        string profileName,
        string profilePath)
    {
        profileName = (profileName ?? "").Trim();
        profilePath = (profilePath ?? "").Trim();

        // Khi context đã mất nhưng profile vẫn còn catalog, không còn Worker/window/CDP
        // để quan sát ổn định. Vì vậy dùng chính probe theo ProfilePath hai lượt liên tiếp.
        for (var pass = 1; pass <= 2; pass++)
        {
            try
            {
                await EnsureAutoCloseChromeStoppedByPathAsync(profileName, profilePath);
            }
            catch (Exception ex)
            {
                return new AutoReplacementCleanupResult(
                    false,
                    $"ProfilePath chưa sạch ở lượt {pass}/2: {ex.Message}");
            }

            _log.Info(
                $"[AUTO_REPLACE_STABLE_PATH_CHECK] profile={profileName} pass={pass}/2 state=closed");

            if (pass == 1)
            {
                var delayMs = Random.Shared.Next(1000, 2001);
                _log.Info(
                    $"[AUTO_REPLACE_STABLE_PATH_WAIT] profile={profileName} delayMs={delayMs} nextPass=2/2");
                await Task.Delay(delayMs);
            }
        }

        _log.Info(
            $"[AUTO_REPLACE_STABLE_PATH_CONFIRMED] profile={profileName} passes=2/2");

        return new AutoReplacementCleanupResult(
            true,
            "STABLE_CLOSED: ProfilePath sạch 2/2 lượt, cách nhau 1-2 giây.");
    }

    async Task<AutoReplacementCleanupResult> EnsureAutoReplacementSourceCleanupAsync(
        AutoReplacementRequest request)
    {
        var profileName = (request.ClosedProfileName ?? "").Trim();
        if (profileName.Length == 0)
            return new AutoReplacementCleanupResult(false, "Thiếu tên profile cũ.");

        // Context còn tồn tại: dọn Worker/tab nếu AutoClose trước đó bị ngắt giữa chừng.
        if (_contexts.TryGetValue(profileName, out var ctx))
        {
            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; } catch { workerAlive = ctx.Worker is not null; }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            if (workerAlive || tabOpen || ctx.Opening)
            {
                try
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                }
                catch (Exception ex)
                {
                    return new AutoReplacementCleanupResult(
                        false,
                        "Không dọn được Worker/tab profile cũ: " + ex.Message);
                }
            }

            if (_autoCloseVerifiedCleanProfiles.Contains(profileName))
            {
                // AutoClose đã có bằng chứng đóng sạch trong CHÍNH phiên Manager này.
                // Worker/tab/opening ở trên cũng đã được dọn xong, vì vậy không cần
                // chạy CIM/PowerShell lần nữa chỉ để xác minh lại cùng một việc.
                _log.Info(
                    $"[AUTO_REPLACE_CLEAN_BARRIER_FASTPASS] profile={profileName} source=verified_clean_session");
            }
            else
            {
                try
                {
                    // Không có bằng chứng sạch => vẫn bắt buộc probe theo ProfilePath.
                    // Nếu probe UNKNOWN/timeout/còn PID, trả false để queue bù CHỜ,
                    // tuyệt đối không mở Chrome/profile mới chồng lên profile cũ.
                    await EnsureAutoCloseChromeStoppedAsync(ctx);
                    _autoCloseVerifiedCleanProfiles.Add(profileName);

                    _log.Info(
                        $"[AUTO_REPLACE_CLEAN_BARRIER_CONFIRMED] profile={profileName} source=profile_path_probe");
                }
                catch (Exception ex)
                {
                    return new AutoReplacementCleanupResult(false, ex.Message);
                }
            }

            // Xác minh lại Worker và tab sau cleanup.
            try
            {
                if (ctx.Worker is not null && !ctx.Worker.HasExited)
                    return new AutoReplacementCleanupResult(false, "Worker profile cũ vẫn còn chạy.");
            }
            catch
            {
                return new AutoReplacementCleanupResult(false, "Không xác minh được Worker profile cũ đã thoát.");
            }

            if (ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs)
            {
                return new AutoReplacementCleanupResult(false, "Tab Manager profile cũ vẫn còn mở.");
            }

            // Safety gate RIÊNG cho Tự bù: dù AutoClose đã đánh dấu clean trong session,
            // vẫn bắt buộc quan sát runtime sạch 2 lần liên tiếp cách nhau 1-2 giây.
            // Điều này tránh mở profile bù ngay đúng lúc Chrome cũ đang shutdown chậm
            // hoặc một process/window/CDP xuất hiện lại muộn.
            var stableClose = await VerifyAutoReplacementRuntimeClosedTwiceAsync(ctx);
            if (!stableClose.Succeeded)
                return stableClose;

            return new AutoReplacementCleanupResult(
                true,
                "CLEANUP_DONE: Chrome/Worker ổn định sạch 2/2 lượt, tab=removed.");
        }

        // Context đã bị xóa (ví dụ BAN/TIME bật auto-delete): nếu catalog cũng không còn
        // thì deletion flow đã dọn xong và request được phép bù.
        var catalog = _profileService.Load();
        var profile = catalog.Profiles.FirstOrDefault(x =>
            x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new AutoReplacementCleanupResult(
                true,
                "CLEANUP_DONE: profile đã được xóa khỏi catalog.");
        }

        // Context mất nhưng profile vẫn còn catalog: xác minh ProfilePath HAI lượt
        // cách nhau 1-2 giây trước khi cho phép mở profile bù.
        var stablePathClose = await VerifyAutoReplacementProfilePathClosedTwiceAsync(
            profileName,
            profile.ProfilePath);

        if (!stablePathClose.Succeeded)
            return stablePathClose;

        return new AutoReplacementCleanupResult(
            true,
            "CLEANUP_DONE: context không còn, ProfilePath sạch ổn định 2/2 lượt.");
    }
}
