using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    static bool IsTransientInputDuringLiveStabilization(ChatInputGuard.Snapshot snapshot)
    {
        // "Có nội dung ngoài workflow" là trạng thái thực, không phải hydrate chậm.
        // Các trạng thái còn lại có thể xuất hiện vài giây khi TikTok vừa đổi/navigate LIVE.
        if (!snapshot.Empty && snapshot.Exists)
            return false;

        return !snapshot.Exists
            || !snapshot.Visible
            || !snapshot.Editable
            || snapshot.Disabled
            || !snapshot.HasExpectedPlaceholder
            || snapshot.Reason.Contains("CDP", StringComparison.OrdinalIgnoreCase)
            || snapshot.Reason.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> QueueVisibleCommentRestrictionBeforeInputAsync(string pointName, CancellationToken ct)
    {
        string marker;
        try
        {
            marker = await DetectCommentRestrictionToastAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!IsLikelyCdpIssue(ex))
            {
                ReportProblem(
                    "COMMENT_RESTRICTION_PRE_INPUT_CHECK_FAILED",
                    pointName,
                    "Không kiểm tra được toast cấm bình luận trong thời gian ổn định LIVE: " + ex.Message,
                    throttleSeconds: 60);
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(marker))
            return false;

        _step = CurrentRestartStep;
        _postEnterCommentRestrictionPending = true;
        _postEnterCommentRestrictionContext = pointName;
        ClearLiveTargetStabilization("phát hiện cấm bình luận trước InputGuard");

        _log.Warn(
            $"[COMMENT_RESTRICTION_DETECTED_PRE_INPUT] point={pointName} marker={marker} " +
            $"action=SWITCH_LIVE restartStep={_step}");
        ReportProblem(
            "COMMENT_RESTRICTION_DETECTED",
            pointName,
            "TikTok báo ‘Bạn hiện bị cấm bình luận’. Bỏ qua thời gian ổn định và chuyển LIVE ngay.",
            throttleSeconds: 5);
        SetStatus("BỊ CẤM BÌNH LUẬN", $"{pointName}: phát hiện toast trong lúc chờ LIVE ổn định → chuyển LIVE.");
        return true;
    }

    async Task<bool?> ConfirmStabilizedLiveViewerAsync(string pointName, CancellationToken ct)
    {
        if (!_s.Viewer.Enabled)
            return true;

        var (value, raw) = await ReadViewerWithRetryAsync(
            $"xác minh LIVE sau ổn định tại {pointName}",
            ct);

        if (value < 0)
        {
            _log.Warn(
                $"[LIVE_TARGET_STABILIZE_VIEWER_UNREADABLE] point={pointName} " +
                $"action=KEEP_LOCK_OR_RECHECK_NEXT_LOOP");
            return null;
        }

        if (value <= _s.Viewer.Threshold)
        {
            _log.Warn(
                $"[LIVE_TARGET_STABILIZE_VIEWER_LOW] point={pointName} value={value} " +
                $"threshold={_s.Viewer.Threshold} raw={raw} action=RECHECK_VIEWER_GATE");
            ClearLiveTargetStabilization("Viewer thực tế không còn đủ ngưỡng");
            return false;
        }

        _log.Info(
            $"[LIVE_TARGET_STABILIZE_VIEWER_OK] point={pointName} value={value} " +
            $"threshold={_s.Viewer.Threshold} raw={raw}");
        ClearLiveTargetStabilization("ô nhập + Viewer đã xác minh");
        return true;
    }

    async Task<(bool Handled, bool AllowWorkflow, ChatInputGuard.Snapshot Snapshot)> TryStabilizeLiveInputAsync(
        string inputXPath,
        string pointName,
        ChatInputGuard.Snapshot initialSnapshot,
        CancellationToken ct)
    {
        var stabilization = await GetLiveTargetStabilizationAsync(ct);
        if (!stabilization.Active || !IsTransientInputDuringLiveStabilization(initialSnapshot))
            return (false, false, initialSnapshot);

        var snapshot = initialSnapshot;
        _log.Warn(
            $"[LIVE_TARGET_STABILIZE_INPUT_WAIT] point={pointName} key={stabilization.Key} " +
            $"sidebarOrGateViewer={stabilization.Viewer} reason={snapshot.Reason} " +
            $"remainingMs={stabilization.RemainingMs} action=RETRY_INPUT_NO_SWITCH");

        while (_running && !ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            await StopIfFatalTikTokRestrictionAsync($"ổn định LIVE trước InputGuard {pointName}", ct);

            // Một restriction thật phải thắng grace period ngay lập tức.
            if (await QueueVisibleCommentRestrictionBeforeInputAsync(pointName, ct))
                return (true, false, snapshot);

            stabilization = await GetLiveTargetStabilizationAsync(ct);
            if (!stabilization.Active)
                break;

            var waitMs = Math.Min(LiveTargetStabilizePollMs, Math.Max(1, stabilization.RemainingMs));
            await Task.Delay(waitMs, ct);

            snapshot = await _inputGuard.ProbeAsync(
                inputXPath,
                _s.InputGuard.NormalPlaceholderText,
                ct);

            if (snapshot.IsNormal)
            {
                // Xác nhận lại theo đúng số lần đọc InputGuard cấu hình trước khi cho thao tác.
                var confirmed = await _inputGuard.ConfirmNormalAsync(inputXPath, _s.InputGuard, ct);
                snapshot = confirmed.snapshot;
                if (confirmed.normal)
                {
                    var viewerOk = await ConfirmStabilizedLiveViewerAsync(pointName, ct);
                    if (viewerOk == true)
                    {
                        ResetInputGuardConsecutive("LIVE vừa chọn đã ổn định");
                        _log.Info(
                            $"[LIVE_TARGET_STABILIZE_READY] point={pointName} " +
                            $"placeholder=\"{snapshot.Placeholder}\" action=ALLOW_WORKFLOW");
                        return (true, true, snapshot);
                    }

                    if (viewerOk == false)
                    {
                        // Viewer Gate ở vòng chính kế tiếp sẽ tự tìm LIVE khác.
                        return (true, false, snapshot);
                    }

                    // Input đã ổn nhưng Viewer DOM vẫn đang render. Giữ lock và thử tiếp
                    // tới hết grace period thay vì chuyển LIVE chỉ vì một nhịp XPath trống.
                    _log.Warn(
                        $"[LIVE_TARGET_STABILIZE_WAIT_VIEWER] point={pointName} " +
                        $"remainingMs={stabilization.RemainingMs}");
                    continue;
                }
            }

            if (!IsTransientInputDuringLiveStabilization(snapshot))
            {
                _log.Warn(
                    $"[LIVE_TARGET_STABILIZE_ABORT] point={pointName} reason={snapshot.Reason} " +
                    $"action=USE_NORMAL_INPUT_GUARD");
                ClearLiveTargetStabilization("InputGuard gặp trạng thái không phải hydrate chậm");
                return (false, false, snapshot);
            }

            _log.Info(
                $"[LIVE_TARGET_STABILIZE_RETRY] point={pointName} reason={snapshot.Reason} " +
                $"remainingMs={stabilization.RemainingMs}");
        }

        ClearLiveTargetStabilization("hết grace nhưng ô nhập chưa ổn định");
        _log.Warn(
            $"[LIVE_TARGET_STABILIZE_TIMEOUT] point={pointName} reason={snapshot.Reason} " +
            $"action=FALLBACK_NORMAL_INPUT_GUARD");
        return (false, false, snapshot);
    }

    async Task<bool> GuardAndProcessBeforeClickAsync(string inputXPath, string pointName, CancellationToken ct)
    {
        // Một navigation có thể xảy ra ngay bên trong Viewer Gate (ví dụ chọn LIVE đề xuất).
        // Vì vậy kiểm tra PAGE_READY thêm đúng tại ranh giới InputGuard để không diễn giải
        // "DOM chưa hydrate" thành "ô nhập bất thường" rồi đổi LIVE/F5 nhầm.
        if (!await WaitForLivePageReadyAsync($"trước InputGuard {pointName}", ct))
        {
            _log.Warn($"[INPUT_GUARD_PAGE_NOT_READY] point={pointName} action=SKIP_CHECK_NO_SWITCH");
            return true;
        }

        // Chặn trang vi phạm trước khi InputGuard diễn giải việc mất ô nhập là một LIVE lỗi
        // thông thường và cố chuyển LIVE tiếp.
        await StopIfFatalTikTokRestrictionAsync($"InputGuard {pointName}", ct);

        if (!_s.InputGuard.Enabled)
        {
            ResetInputGuardConsecutive("InputGuard tắt");
            ClearLiveTargetStabilization("InputGuard tắt");
            return false;
        }

        var perf = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (normal, snapshot) = await _inputGuard.ConfirmNormalAsync(inputXPath, _s.InputGuard, ct);
            if (normal)
            {
                var stabilization = await GetLiveTargetStabilizationAsync(ct);
                if (stabilization.Active)
                {
                    bool? viewerOk = null;
                    while (_running && !ct.IsCancellationRequested && stabilization.Active)
                    {
                        if (await QueueVisibleCommentRestrictionBeforeInputAsync(pointName, ct))
                            return true;

                        viewerOk = await ConfirmStabilizedLiveViewerAsync(pointName, ct);
                        if (viewerOk == true)
                            break;
                        if (viewerOk == false)
                            return true;

                        // Ô nhập đã sẵn nhưng XPath Viewer còn re-render: tiếp tục giữ LIVE,
                        // không trả về vòng chính để Viewer Gate đổi LIVE vội.
                        stabilization = await GetLiveTargetStabilizationAsync(ct);
                        if (!stabilization.Active)
                            break;

                        var waitMs = Math.Min(
                            LiveTargetStabilizePollMs,
                            Math.Max(1, stabilization.RemainingMs));
                        _log.Info(
                            $"[LIVE_TARGET_STABILIZE_WAIT_VIEWER] point={pointName} " +
                            $"remainingMs={stabilization.RemainingMs} waitMs={waitMs}");
                        await Task.Delay(waitMs, ct);
                        stabilization = await GetLiveTargetStabilizationAsync(ct);
                    }

                    if (viewerOk != true)
                    {
                        _log.Warn(
                            $"[LIVE_TARGET_STABILIZE_VIEWER_TIMEOUT] point={pointName} " +
                            $"action=SKIP_CLICK_RECHECK_VIEWER_GATE");
                        return true;
                    }
                }

                ResetInputGuardConsecutive("ô nhập bình thường");
                _log.Info($"[INPUT_GUARD_OK] point={pointName} placeholder=\"{snapshot.Placeholder}\"");
                return false;
            }

            var stabilized = await TryStabilizeLiveInputAsync(
                inputXPath,
                pointName,
                snapshot,
                ct);
            snapshot = stabilized.Snapshot;
            if (stabilized.Handled)
                return !stabilized.AllowWorkflow;

            // Giữ nguyên step hiện tại. Sau khi xử lý chuyển LIVE xong, vòng chính sẽ
            // quay lại đúng bước 1/5 và kiểm tra DOM thêm lần nữa trước khi click.
            _inputGuardConsecutiveCount = Math.Max(1, _inputGuardConsecutiveCount + 1);

            while (_running && !ct.IsCancellationRequested)
            {
                await WaitIfPausedAsync(ct);
                await StopIfFatalTikTokRestrictionAsync($"InputGuard recovery {pointName}", ct);

                if (await QueueVisibleCommentRestrictionBeforeInputAsync(pointName, ct))
                    return true;

                var count = Math.Min(Math.Clamp(_s.InputGuard.ConsecutiveMax, 1, 4), _inputGuardConsecutiveCount);
                var source = $"ô nhập bất thường tại {pointName}";
                _log.Warn($"[INPUT_GUARD_SWITCH] point={pointName} reason={snapshot.Reason} consecutive={_inputGuardConsecutiveCount} actionCount={count}");
                SetStatus("Ô NHẬP BẤT THƯỜNG", $"{pointName}: {snapshot.Reason} → chuyển LIVE ×{count} rồi F5.");

                var afterReload = _s.Viewer.Enabled ? Math.Max(0, _s.Viewer.WaitAfterF5Sec * 1000) : F5WaitMs;
                bool transitioned;
                if (_s.UseArrowDownForLiveSwitch)
                {
                    transitioned = await TransitionAsync(source, TransitionAction.ArrowDown, "", count, scheduledPeriodic: false, ct, afterReload);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(_s.XPathPeriodicAction))
                    {
                        ReportProblem("INPUT_GUARD_SWITCH_XPATH_MISSING", source,
                            "Đã tắt ArrowDown nhưng XPath nút chuyển LIVE đang trống. Không click/fallback tọa độ.", error: true, throttleSeconds: 30);
                        await Task.Delay(1500, ct);
                        return true;
                    }
                    transitioned = await TransitionAsync(source, TransitionAction.ClickXPath, _s.XPathPeriodicAction, count, scheduledPeriodic: false, ct, afterReload);
                }

                if (!transitioned)
                {
                    _log.Warn($"[INPUT_GUARD_SWITCH_PENDING] point={pointName} transition chưa xác nhận; cooldown 1500 ms.");
                    await Task.Delay(1500, ct);
                    return true;
                }

                var verify = await _inputGuard.ConfirmNormalAsync(inputXPath, _s.InputGuard, ct);
                snapshot = verify.snapshot;
                if (verify.normal)
                {
                    ResetInputGuardConsecutive("LIVE mới có ô nhập bình thường");
                    _log.Info($"[INPUT_GUARD_RECOVERED] point={pointName} LIVE mới đã có ô nhập bình thường.");

                    // Viewer Gate sẽ tự đọc lại ở đầu đúng bước 1/5 trước khi Click,
                    // nên không đọc Viewer lặp thêm tại đây.

                    if (_running && !_paused) await Task.Delay(ActionDelay(), ct);
                    return true;
                }

                _inputGuardConsecutiveCount = Math.Min(4, _inputGuardConsecutiveCount + 1);
                _log.Warn($"[INPUT_GUARD_PERSIST] point={pointName} LIVE mới vẫn bất thường: {snapshot.Reason}; lần tiếp theo actionCount={Math.Min(Math.Clamp(_s.InputGuard.ConsecutiveMax, 1, 4), _inputGuardConsecutiveCount)}.");
            }

            return true;
        }
        finally
        {
            perf.Stop();
            _log.Info($"[STEP_PERF] step=inputGuard:{pointName} elapsedMs={perf.ElapsedMilliseconds}");
        }
    }

    void ResetInputGuardConsecutive(string reason)
    {
        if (_inputGuardConsecutiveCount > 0)
            _log.Info($"[INPUT_GUARD_RESET] previous={_inputGuardConsecutiveCount} reason={reason}");
        _inputGuardConsecutiveCount = 0;
    }
}
