using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
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
            return false;
        }

        var perf = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (normal, snapshot) = await _inputGuard.ConfirmNormalAsync(inputXPath, _s.InputGuard, ct);
            if (normal)
            {
                ResetInputGuardConsecutive("ô nhập bình thường");
                _log.Info($"[INPUT_GUARD_OK] point={pointName} placeholder=\"{snapshot.Placeholder}\"");
                return false;
            }

            // Giữ nguyên step hiện tại. Sau khi xử lý chuyển LIVE xong, vòng chính sẽ
            // quay lại đúng bước 1/5 và kiểm tra DOM thêm lần nữa trước khi click.
            _inputGuardConsecutiveCount = Math.Max(1, _inputGuardConsecutiveCount + 1);

            while (_running && !ct.IsCancellationRequested)
            {
                await WaitIfPausedAsync(ct);
                await StopIfFatalTikTokRestrictionAsync($"InputGuard recovery {pointName}", ct);
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
