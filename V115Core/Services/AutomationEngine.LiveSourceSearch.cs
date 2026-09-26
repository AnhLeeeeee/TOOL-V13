namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    const int LowPairCyclesBeforeForcedSearch = 5;

    int _lowPairSourceCycleCount;

    /// <summary>
    /// MainForm gắn callback dùng chung cơ chế TikTok Search đang có. Engine chỉ yêu cầu
    /// "lấy một đầu vào LIVE mới bằng Search", không nhân đôi logic đọc card/từ khóa.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? RuntimeLiveSearchAsync { get; set; }

    int RegisterLowPairSourceCycle(string source, int streak)
    {
        if (_lowPairSourceCycleCount < LowPairCyclesBeforeForcedSearch)
            _lowPairSourceCycleCount++;

        _log.Warn(
            $"[LOW_PAIR_CYCLE] source={source} lowLives={streak} " +
            $"cycle={_lowPairSourceCycleCount}/{LowPairCyclesBeforeForcedSearch}");
        return _lowPairSourceCycleCount;
    }

    void ResetLowPairSourceCycle(string reason)
    {
        if (_lowPairSourceCycleCount > 0)
        {
            _log.Info(
                $"[LOW_PAIR_CYCLE_RESET] previous={_lowPairSourceCycleCount}/{LowPairCyclesBeforeForcedSearch} reason={reason}");
        }
        _lowPairSourceCycleCount = 0;
    }

    async Task<bool> TryOpenRuntimeKeywordSearchSourceAsync(
        string source,
        bool forced,
        CancellationToken ct)
    {
        var search = RuntimeLiveSearchAsync;
        if (search is null)
        {
            _log.Warn(
                $"[LIVE_SOURCE_SEARCH_UNAVAILABLE] source={source} forced={forced} reason=no-runtime-search-bridge");
            return false;
        }

        SetStatus("TÌM LIVE BẰNG SEARCH",
            forced
                ? "Đến lượt Search bắt buộc • đang tìm đầu vào chuỗi LIVE mới"
                : "Sidebar không có LIVE phù hợp • đang Search từ khóa");
        _log.Warn(
            $"[LIVE_SOURCE_SEARCH_REQUEST] source={source} forced={forced} " +
            $"lowPairCycle={_lowPairSourceCycleCount}/{LowPairCyclesBeforeForcedSearch}");

        bool opened;
        try
        {
            opened = await search(source, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[LIVE_SOURCE_SEARCH_ERROR] source={source} forced={forced} error={ex.Message}");
            return false;
        }

        if (!opened)
        {
            _log.Warn(
                $"[LIVE_SOURCE_SEARCH_MISS] source={source} forced={forced} " +
                $"lowPairCycle={_lowPairSourceCycleCount}/{LowPairCyclesBeforeForcedSearch}");
            return false;
        }

        // Search đã thật sự mở một LIVE mới: coi đây là đầu vào nguồn mới và bắt đầu
        // chu kỳ 5 lượt lại từ đầu, kể cả Viewer của LIVE vừa mở tụt xuống ngay sau đó.
        ResetLowPairSourceCycle("Search đã mở đầu vào LIVE mới");
        _currentLiveFromRecommended = false;
        ResetPeriodicDue("Runtime Search đã mở LIVE mới", cancelCandidate: true);
        ResetPageMaintenanceDue("Runtime Search đã mở LIVE mới");
        ResetInputGuardConsecutive("sau Runtime Search");

        // Search card đã lọc theo Viewer, nhưng số có thể thay đổi trong lúc mở LIVE.
        // Đọc lại một lần bằng chính Viewer Gate để tuyệt đối không cho Click/Dán/Enter
        // nếu LIVE vừa Search đã tụt thấp hoặc chưa render số người xem.
        var (value, raw) = await ReadViewerAfterLiveSwitchAsync(
            source + " / xác minh Viewer sau Search",
            ViewerAfterSwitchInitialSettleMs,
            ct);

        if (value < 0)
        {
            _log.Warn(
                $"[LIVE_SOURCE_SEARCH_OPENED_VIEWER_UNREADABLE] source={source} " +
                "action=KEEP_WORKFLOW_LOCKED_AND_CONTINUE_CHAIN");
            return false;
        }

        Volatile.Write(ref _lastViewerValue, value);
        if (value <= _s.Viewer.Threshold)
        {
            RecordLowViewerLive(source + " / LIVE đầu vào Search", value);
            _log.Warn(
                $"[LIVE_SOURCE_SEARCH_OPENED_VIEWER_LOW] source={source} value={value} " +
                $"threshold={_s.Viewer.Threshold} raw={raw} action=CONTINUE_ARROW_CHAIN");
            return false;
        }

        ResetLowViewerStreak("LIVE đầu vào Search đủ Viewer");
        await ArmLiveTargetStabilizationAsync(
            source + " / LIVE đầu vào Search",
            value,
            ct);
        _log.Warn(
            $"[LIVE_SOURCE_SEARCH_OPENED] source={source} value={value} threshold={_s.Viewer.Threshold} " +
            "result=STABILIZE_THEN_CONTINUE");
        return true;
    }
}
