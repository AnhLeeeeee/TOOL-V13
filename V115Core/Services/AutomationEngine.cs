using ToolTikTokV12.Utils;
using System.Text.Json;
using System.Xml.XPath;
using ToolTikTokV11.Models;
using ToolTikTokV11.Utils;

namespace ToolTikTokV11.Services;

public enum AutomationRunState { Stopped, Running, Paused }

/// <summary>
/// V13 giữ state machine 8 bước và toàn bộ transition/recovery từ V12.5, nhưng thay
/// image-scan vùng lỗi runtime bằng InputGuard: đọc trực tiếp trạng thái ô nhập qua DOM/CDP
/// ngay trước Click 1 và Click 2. Live cũ và Viewer đều đọc trực tiếp từ DOM/XPath;
/// không còn Tesseract/OCR/screenshot runtime. F5 định kỳ và transition lock giữ nguyên.
/// </summary>
public sealed partial class AutomationEngine
{
    public readonly record struct PeriodicF5Snapshot(bool Running, bool Enabled, bool Executing, DateTime DueAt);
    public sealed record OldLiveEntry(
        string Id,
        string IdentityKey,
        string DisplayName,
        string Username,
        string Href,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        string Source);
    public sealed record OldLiveEntrySnapshot(string Id, string DisplayName, string IdentityKey, TimeSpan Age, TimeSpan Remaining);
    public sealed record OldLiveDiagnosticsSnapshot(
        int ActiveCount,
        DateTime? LastSavedAt,
        DateTime? LastMatchAt,
        bool? LastMatchFound,
        string LastObservedIdentity,
        IReadOnlyList<OldLiveEntrySnapshot> Entries);

    const int EnterReactionScanMs = 2000;
    const int VerifyAfterF5Ms = 1500;
    // ArrowDown vẫn có 2 giây settle riêng. Sau Reload chỉ cần nhịp CDP ngắn
    // rồi xác nhận DOM ready, không cộng thêm một sleep cố định 2 giây.
    const int F5WaitMs = 1000;
    const int MultiActionGapMs = 600;
    const int LiveVerifyPollMs = 250;
    const int LiveFastChangePollMs = 100;
    const int LiveVerifyTimeoutMs = 5000;
    // ArrowDown CDP có thể mất hiệu lực sau nhiều lần reload dù session vẫn báo gửi key thành công.
    // Retry theo từng lần, reconnect/focus lại giống hiệu ứng người dùng dừng rồi chạy lại.
    const int ArrowDownAttemptWaitMs = 1600;
    const int ArrowDownRecoveryAttempts = 3;
    const int ArrowDownPostResetAttemptWaitMs = 2600;
    const int ArrowDownResetReloadWaitMs = 1000;
    const int ArrowDownRetryDelayMs = 300;
    const int PriorityPauseMs = 5000;
    // Bộ F5 cứu trang độc lập: không đổi LIVE. Bình thường reload trang hiện tại mỗi 30 phút;
    // nếu renderer/tab crash (Aw, Snap!/Out of Memory/CDP target crashed) thì xử lý ngay.
    const int PageMaintenanceReloadMinutes = 30;
    const int PageHealthProbeIntervalMs = 2000;
    const int PageRecoveryReloadWaitMs = 1200;
    const int OldLiveScanIntervalMs = 1500;
    const int OldLiveScanRetryMs = 2500;
    const int ViewerReadRetryCount = 5;
    const int ViewerReadRetryDelayMs = 350;
    const int ViewerGateRetryCooldownMs = 1000;
    const int ViewerLowStreakFeedResetThreshold = 5;
    // VM/chrome chậm: document.readyState có thể đã complete nhưng TikTok React/DOM LIVE
    // vẫn chưa hydrate xong. Chờ tín hiệu DOM thực tế trước khi Viewer/InputGuard kết luận lỗi.
    const int PageReadyPollMs = 500;
    const int PageReadyTimeoutMs = 25000;
    const int PageReadyRetryAttempts = 2;
    const int PageReadyRetryPauseMs = 2000;
    const string OldLiveDirectoryName = "live_cu_tam";
    const string OldLiveManifestFileName = "old_live_identity_manifest.json";
    const int RequiredXPathRecoveryMaxAttempts = 3;
    static readonly TimeSpan RequiredXPathRecoveryWait = TimeSpan.FromSeconds(10);
    const int ConsecutiveRecoveryFailurePauseThreshold = 10;
    static readonly TimeSpan[] CdpReconnectBackoff =
    [
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    readonly string _baseDir;
    readonly ChromeController _chrome;
    readonly Logger _log;
    readonly ChatInputGuard _inputGuard;
    readonly LiveAccountIdentityProbe _oldLiveIdentityProbe;
    readonly Random _rng = new();
    readonly object _periodicSnapshotLock = new();
    static readonly JsonSerializerOptions OldLiveStoreJson = new() { WriteIndented = true };

    CancellationTokenSource? _cts;
    Task? _task;
    volatile bool _paused;
    volatile bool _running;
    bool _transitioning;

    AppSettings _s = new();
    List<string> _contents = [];
    int _contentIndex;
    int _step = 1; // V10: buocHienTai 1..8
    long _rounds;
    int _lastViewerValue = -1; // snapshot nhẹ cho Manager/Chrome Monitor
    int _consecutiveLowViewerLives; // reset nguồn đề xuất sau N LIVE liên tiếp <= ngưỡng
    System.Diagnostics.Stopwatch? _loopPerf;
    long _loopPerfTotalMs;
    long _loopPerfCount;

    DateTime _periodicDue = DateTime.MaxValue;
    DateTime _candidateCaptureAt = DateTime.MaxValue;
    readonly List<OldLiveEntry> _activeOldLives = [];
    bool _oldLiveManifestLoaded;
    DateTime _nextOldLiveScan = DateTime.MaxValue;
    DateTime _stopAt = DateTime.MaxValue;
    DateTime _pageMaintenanceDue = DateTime.MaxValue;
    DateTime _nextPageHealthProbe = DateTime.MinValue;
    string _lastHealthyTikTokUrl = "";
    bool _pageRecoveryExecuting;
    bool _periodicExecuting;
    PeriodicF5Snapshot _periodicSnapshot = new(false, false, false, DateTime.MaxValue);
    DateTime? _lastOldLiveSavedAt;
    DateTime? _lastOldLiveMatchAt;
    bool? _lastOldLiveMatchFound;
    string _lastOldLiveMatchIdentity = "";

    int _inputGuardConsecutiveCount;
    int _consecutiveRecoveryFailures;
    readonly Dictionary<string, DateTime> _problemLast = new(StringComparer.Ordinal);

    enum RecoveryDecision { RetryStep, SkipLive, SkipStep }

    sealed record PersistedOldLiveEntry(string Id, string IdentityKey, string DisplayName, string Username, string Href, DateTime CreatedAt, DateTime ExpiresAt, string Source);

    sealed class RecoverableAutomationException : Exception
    {
        public string Code { get; }
        public string Context { get; }
        public RecoveryDecision Decision { get; }

        public RecoverableAutomationException(string code, string context, string message, RecoveryDecision decision, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Context = context;
            Decision = decision;
        }
    }

    public bool Running => _running;
    public bool Paused => _paused;
    public AutomationRunState RunState => !_running ? AutomationRunState.Stopped : _paused ? AutomationRunState.Paused : AutomationRunState.Running;
    public long Rounds => _rounds;
    public int CurrentStep => Math.Clamp(_step, 1, 8);
    public int LastViewerValue => Volatile.Read(ref _lastViewerValue);
    public Task CompletionTask => _task ?? Task.CompletedTask;
    public event Action<string>? Status;
    public event Action<string>? Problem;
    public event Action? StateChanged;
    public event Action<AutomationRunState>? RunStateChanged;

    public AutomationEngine(string baseDir, ChromeController chrome, Logger log)
    {
        _baseDir = baseDir;
        _chrome = chrome;
        _log = log;
        _inputGuard = new ChatInputGuard(chrome, log);
        _oldLiveIdentityProbe = new LiveAccountIdentityProbe(chrome);
        LoadPersistedOldLives();
    }

    public PeriodicF5Snapshot GetPeriodicF5Snapshot()
    {
        lock (_periodicSnapshotLock) return _periodicSnapshot;
    }

    public OldLiveDiagnosticsSnapshot GetOldLiveDiagnosticsSnapshot()
    {
        if (!_running) CleanupExpiredOldLives();
        var now = DateTime.Now;
        var entries = _activeOldLives
            .OrderBy(e => e.ExpiresAt)
            .Select(e => new OldLiveEntrySnapshot(
                e.Id,
                e.DisplayName,
                e.IdentityKey,
                now - e.CreatedAt,
                e.ExpiresAt - now))
            .ToList();
        return new OldLiveDiagnosticsSnapshot(
            entries.Count,
            _lastOldLiveSavedAt,
            _lastOldLiveMatchAt,
            _lastOldLiveMatchFound,
            _lastOldLiveMatchIdentity,
            entries);
    }

    void SyncPeriodicSnapshot()
    {
        lock (_periodicSnapshotLock)
            _periodicSnapshot = new PeriodicF5Snapshot(_running, _s.PeriodicF5Minutes > 0, _periodicExecuting, _periodicDue);
    }

    public void Start(AppSettings settings, List<string> contents)
    {
        if (_running) return;
        if (!_chrome.Connected) throw new InvalidOperationException("Hãy kết nối Chrome V13 trước khi bắt đầu.");
        if (contents.Count == 0) throw new InvalidOperationException("Danh sách nội dung đang trống.");
        if (string.IsNullOrWhiteSpace(settings.XPathPoint1) || string.IsNullOrWhiteSpace(settings.XPathPoint2))
            throw new InvalidOperationException("V13 cần XPath Điểm 1 và XPath Điểm 2 trước khi chạy.");
        if (settings.InputGuard.Enabled && string.IsNullOrWhiteSpace(settings.InputGuard.NormalPlaceholderText))
            throw new InvalidOperationException("V13 InputGuard đang bật nhưng chữ placeholder bình thường đang trống.");
        if (settings.OldLive.Enabled && string.IsNullOrWhiteSpace(settings.OldLive.IdentityXPath))
            throw new InvalidOperationException($"{AppVersionInfo.Display} Live cũ đang bật nhưng XPath tài khoản LIVE đang trống.");

        _s = settings;
        _contents = contents;
        _contentIndex = 0;
        _step = 1;
        _rounds = 0;
        Volatile.Write(ref _lastViewerValue, -1);
        _consecutiveLowViewerLives = 0;
        _loopPerf = System.Diagnostics.Stopwatch.StartNew();
        _loopPerfTotalMs = 0;
        _loopPerfCount = 0;
        _paused = false;
        _running = true;
        _transitioning = false;
        ResetPostEnterCommentRestrictionState();
        ResetInputGuardConsecutive("khởi động");

        var now = DateTime.Now;
        // V13.4.1 Viewer Gate: không còn lịch đọc người xem định kỳ.
        // Nếu Viewer bật, số người xem được kiểm tra trực tiếp trước mỗi Click 1/2.
        _stopAt = _s.TimerStopMinutes > 0 ? now.AddMinutes(_s.TimerStopMinutes) : DateTime.MaxValue;
        EnsureOldLivesReadyForRun();
        // Runtime không còn lập lịch quét ảnh vùng lỗi/STOP/ban acc.

        _lastHealthyTikTokUrl = _chrome.Page?.Url ?? "";
        _nextPageHealthProbe = now;
        ResetPageMaintenanceDue("khởi động");
        ResetPeriodicDue("khởi động", cancelCandidate: true);
        SyncPeriodicSnapshot();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info($"{AppVersionInfo.Display} bắt đầu. InputGuard và Live cũ đều đọc trạng thái trực tiếp bằng DOM/XPath; flow click/phím/F5/chuyển LIVE giữ nguyên.");
        SetStatus("ĐANG CHẠY", $"{AppVersionInfo.Display} XPath-only + DOM Input Guard đã bắt đầu.");
        NotifyStateChanged();
    }

    public void TogglePause()
    {
        if (!_running) return;
        _paused = !_paused;
        _log.Info(_paused ? "Tool đã tạm dừng." : "Tool tiếp tục.");
        SetStatus(_paused ? "TẠM DỪNG" : "ĐANG CHẠY", _paused ? "F9 để tiếp tục." : "Đã tiếp tục.");
        SyncPeriodicSnapshot();
        NotifyStateChanged();
    }

    void AutoPause(string reason)
    {
        if (!_running) return;
        _paused = true;
        _periodicExecuting = false;
        _log.Error("[AUTO_PAUSE] " + reason);
        SetStatus("TẠM DỪNG DO LỖI LIÊN TIẾP", reason);
        SyncPeriodicSnapshot();
        NotifyStateChanged();
    }

    public void Stop(string reason = "Người dùng dừng tool")
    {
        if (!_running) return;
        _running = false;
        _paused = false;
        _periodicExecuting = false;
        _log.Warn("DỪNG TOOL: " + reason);
        SetStatus("ĐÃ DỪNG", reason);
        try { _cts?.Cancel(); } catch { }
        SyncPeriodicSnapshot();
        NotifyStateChanged();
    }

    public async Task<bool> WaitForStopAsync(TimeSpan timeout)
    {
        var task = CompletionTask;
        if (task.IsCompleted) return true;
        var done = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == task;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    await WaitIfPausedAsync(ct);
                    if (!_running) break;
                    if (DateTime.Now >= _stopAt)
                    {
                        Stop("Đã hết thời gian hẹn giờ chạy.");
                        break;
                    }

                    // Crash renderer phải được xử lý trước mọi DOM/XPath guard. Khi Chrome hiện
                    // “Ôi, hỏng!/Out of Memory”, Runtime.evaluate có thể không còn hoạt động.
                    if (await HandleImmediatePageCrashRecoveryAsync(ct)) continue;

                    // Stop guard DOM: trạng thái vi phạm/tính năng bị khóa là lỗi cấp tài khoản,
                    // không được coi như ô nhập bất thường rồi tiếp tục đổi LIVE.
                    await StopIfFatalTikTokRestrictionAsync("ranh giới vòng chính", ct);

                    // Nếu Enter vừa bị TikTok từ chối bằng toast “Bạn hiện bị cấm bình luận”,
                    // khóa workflow tại đây cho đến khi đã thực hiện xong một lần chuyển LIVE.
                    // Không để Viewer/InputGuard bình thường vô tình cho gửi lại ngay trên LIVE cũ.
                    if (await HandlePendingPostEnterCommentRestrictionAsync(ct)) continue;

                    // V13 không còn quét ảnh ưu tiên/STOP runtime. Các timer còn lại vẫn chỉ
                    // xử lý ở ranh giới giữa các bước, không chen giữa click/dán/Enter.
                    if (await HandleOldLiveExpiryAndScanAsync(ct)) continue;
                    if (await HandlePeriodicCaptureAndF5Async(ct)) continue;
                    if (await HandlePageMaintenanceReloadAsync(ct)) continue;

                    // Viewer không còn chạy theo chu kỳ thời gian. Nếu bật, Viewer Gate được
                    // kiểm tra ngay trong bước 1/5 trước mỗi Click. InputGuard vẫn giữ nguyên.
                    await ExecuteOneStepAsync(ct);
                    ResetRecoveryFailures("workflow chính đã chạy thành công");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (RecoverableAutomationException ex)
                {
                    await RecoverAndContinueAsync(ex, ct);
                }
                catch (Exception ex)
                {
                    await HandleUnexpectedAutomationExceptionAsync(ex, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _running = false;
            _periodicExecuting = false;
            SyncPeriodicSnapshot();
            NotifyStateChanged();
        }
    }

    void NotifyStateChanged()
    {
        RunStateChanged?.Invoke(RunState);
        StateChanged?.Invoke();
    }

    async Task WaitIfPausedAsync(CancellationToken ct)
    {
        if (!_paused) return;
        var start = DateTime.Now;
        while (_running && _paused) await Task.Delay(200, ct);
        var pausedFor = DateTime.Now - start;
        // V10 không trừ thời gian người dùng Pause vào bộ đếm F5 định kỳ.
        ShiftPeriodicClock(pausedFor);
    }

    // Hai khoảng delay này là cấu hình nghiệp vụ theo từng profile.  Tuyệt đối
    // không cap runtime: người dùng nhập 1500-2800 thì vẫn random đúng khoảng đó.
    int ActionDelay() => ConfiguredRandomDelay(_s.DelayMinMs, _s.DelayMaxMs);
    int NormalCdpDelay() => ConfiguredRandomDelay(_s.DelayMinMs, _s.DelayMaxMs);
    int NormalLoopDelay() => ConfiguredRandomDelay(_s.LoopMinMs, _s.LoopMaxMs);

    int ConfiguredRandomDelay(int configuredMin, int configuredMax)
    {
        var min = Math.Max(0, Math.Min(configuredMin, configuredMax));
        var max = Math.Max(min, Math.Max(configuredMin, configuredMax));
        return min == max ? min : (int)_rng.NextInt64(min, (long)max + 1);
    }

    void SetStatus(string title, string text) => Status?.Invoke(title + "\n" + text);

    void ReportProblem(string code, string context, string detail, bool error = false, int throttleSeconds = 15)
    {
        var key = code + "|" + context + "|" + detail;
        var now = DateTime.Now;
        if (_problemLast.TryGetValue(key, out var last) && (now - last).TotalSeconds < throttleSeconds) return;
        _problemLast[key] = now;
        var msg = $"[{code}] {context} — {detail}";
        if (error) _log.Error(msg); else _log.Warn(msg);
        Problem?.Invoke(msg);
        SetStatus(error ? "LỖI" : "CẢNH BÁO", msg);
    }

    async Task StopIfFatalTikTokRestrictionAsync(string context, CancellationToken ct)
    {
        if (!_running || !_chrome.Connected) return;

        string marker;
        try
        {
            marker = await _chrome.DetectFatalFeatureRestrictionAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Guard phụ không được làm thay đổi recovery hiện tại nếu CDP vừa rớt đúng lúc quét.
            // Các đường EnsureCdpRecovered/RecoverAndContinue vẫn chịu trách nhiệm phần đó.
            if (!IsLikelyCdpIssue(ex))
                ReportProblem("TIKTOK_FATAL_PAGE_CHECK_FAILED", context,
                    "Không kiểm tra được trạng thái trang vi phạm: " + ex.Message, throttleSeconds: 60);
            return;
        }

        if (string.IsNullOrWhiteSpace(marker)) return;

        const string reason = "TikTok báo tài khoản đã vi phạm quy tắc và hiện không thể sử dụng tính năng này. Tool đã dừng để tránh tiếp tục thao tác.";
        ReportProblem("TIKTOK_FEATURE_BLOCKED_STOP", context,
            $"{reason} marker={marker}", error: true, throttleSeconds: 300);
        Stop(reason);

        // Stop() đã cancel token của LoopAsync. Ném OCE để mọi flow lồng nhau (Viewer,
        // InputGuard, transition/recovery) thoát ngay thay vì chạy thêm một thao tác.
        throw new OperationCanceledException(reason, ct);
    }

    void ResetRecoveryFailures(string reason)
    {
        if (_consecutiveRecoveryFailures <= 0) return;
        _log.Info($"Reset bộ đếm recovery liên tiếp sau {_consecutiveRecoveryFailures} LIVE/bước lỗi: {reason}.");
        _consecutiveRecoveryFailures = 0;
    }

    void IncreaseRecoveryFailures(string reason)
    {
        _consecutiveRecoveryFailures++;
        _log.Warn($"[RECOVERY_FAILED] count={_consecutiveRecoveryFailures}/{ConsecutiveRecoveryFailurePauseThreshold} reason={reason}");
        if (_consecutiveRecoveryFailures >= ConsecutiveRecoveryFailurePauseThreshold)
            AutoPause($"{_consecutiveRecoveryFailures} LIVE liên tiếp không thể phục hồi. Tool đã tạm dừng để tránh vòng lỗi vô hạn.");
    }

    async Task RecoverAndContinueAsync(RecoverableAutomationException ex, CancellationToken ct)
    {
        _log.Warn($"[RECOVERY_START] code={ex.Code} context={ex.Context} reason={ex.Message}");
        SetStatus("ĐANG TỰ PHỤC HỒI", $"{ex.Context}: {ex.Message}");

        // Nếu exception thực chất đến từ renderer/tab crash (Out of Memory/Aw, Snap/target crashed),
        // ưu tiên cứu đúng trang hiện tại. Không chuyển LIVE và không tăng bộ đếm lỗi trước khi
        // reload/restart Chrome đã được thử.
        if (await TryRecoverRendererCrashFromFailureAsync($"{ex.Code}/{ex.Context}", ex, ct))
            return;

        // LIVE/XPath failures are part of normal TikTok churn.  Reconnect and mark
        // Chrome unavailable only when the CDP session/target has actually gone away.
        var cdpSessionLost = IsLikelyCdpIssue(ex);
        if (cdpSessionLost && !await EnsureCdpRecoveredAsync($"{ex.Code}/{ex.Context}", ct))
        {
            IncreaseRecoveryFailures($"CDP session lost: {ex.Code}/{ex.Context}");
            return;
        }

        switch (ex.Decision)
        {
            case RecoveryDecision.RetryStep:
                _log.Warn($"[RECOVERY_OK] code={ex.Code} context={ex.Context} action=retry-step");
                return;
            case RecoveryDecision.SkipStep:
                if (cdpSessionLost) IncreaseRecoveryFailures($"{ex.Code}/{ex.Context} -> skip-step");
                await SkipCurrentStepAsync(ex.Code, ex.Context, ex.Message, ct);
                return;
            default:
                if (cdpSessionLost) IncreaseRecoveryFailures($"{ex.Code}/{ex.Context} -> skip-live");
                await SkipCurrentLiveAsync(ex.Code, ex.Context, ex.Message, ct);
                return;
        }
    }

    async Task HandleUnexpectedAutomationExceptionAsync(Exception ex, CancellationToken ct)
    {
        _log.Error("Lỗi engine V13 đã được chặn để tránh chết LoopAsync: " + ex);

        // Crash Chrome xảy ra giữa Click/Dán/Enter có thể ném exception trước khi vòng chính
        // kịp chạy PAGE_HEALTH_PROBE. Chặn ngay tại đây để không AutoPause/SkipLive nhầm.
        if (await TryRecoverRendererCrashFromFailureAsync("LoopAsync/unexpected", ex, ct))
            return;

        if (IsLikelyRecoverableAutomationError(ex))
        {
            var wrapped = new RecoverableAutomationException("UNEXPECTED_RECOVERABLE", "LoopAsync", ex.Message, RecoveryDecision.SkipLive, ex);
            await RecoverAndContinueAsync(wrapped, ct);
            return;
        }

        AutoPause("Lỗi nội bộ nghiêm trọng: " + ex.Message);
    }

    bool IsLikelyRecoverableAutomationError(Exception ex)
        => ex is InvalidOperationException
        || ex is IOException
        || ex is TimeoutException
        || IsLikelyCdpIssue(ex);

    bool IsLikelyCdpIssue(Exception ex) => _chrome.IsCdpSessionLost(ex);

    async Task<bool> TryRecoverRendererCrashFromFailureAsync(string context, Exception? cause, CancellationToken ct)
    {
        if (_pageRecoveryExecuting) return false;

        ChromeController.PageHealthSnapshot health;
        try
        {
            health = await _chrome.ProbePageHealthAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception probeEx)
        {
            var hinted = cause is not null && (_chrome.IsRendererCrashLike(cause) || _chrome.IsCdpSessionLost(cause));
            if (!hinted) return false;
            health = new ChromeController.PageHealthSnapshot(false, true,
                "EXCEPTION_CRASH_HINT", _lastHealthyTikTokUrl);
            _log.Warn($"[PAGE_CRASH_PROBE_FALLBACK] context={context} reason={probeEx.Message}");
        }

        var crashLike = health.CrashLike ||
            (cause is not null && _chrome.IsRendererCrashLike(cause));
        if (!crashLike) return false;

        _pageRecoveryExecuting = true;
        var resumeStep = PageRecoveryRestartStep;
        _step = resumeStep;
        var fallback = !string.IsNullOrWhiteSpace(_lastHealthyTikTokUrl)
            ? _lastHealthyTikTokUrl
            : health.Url;
        SetStatus("RECOVERING_CHROME", $"{context}: Chrome/tab lỗi ({health.Reason}) → đang tự phục hồi.");
        _log.Warn($"[PAGE_CRASH_FAILURE_INTERCEPT] context={context} reason={health.Reason} action=reload-then-restart restartStep={resumeStep}");

        try
        {
            try
            {
                await _chrome.RecoverCurrentPageAsync(fallback, ct);
                _log.Warn($"[PAGE_CRASH_FAILURE_RECOVERED] context={context} method=reload-or-navigate restartStep={resumeStep}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception recoverEx)
            {
                _log.Warn($"[PAGE_CRASH_PRIMARY_RECOVERY_FAILED] context={context} reason={recoverEx.Message} action=restart-managed-chrome");
                await _chrome.RestartManagedChromeForRecoveryAsync(fallback, ct);
                _log.Warn($"[PAGE_CRASH_FAILURE_RECOVERED] context={context} method=restart-managed-chrome restartStep={resumeStep}");
            }

            ResetPageMaintenanceDue("sau tự cứu renderer crash");
            _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);
            ResetInputGuardConsecutive("sau tự cứu renderer crash");
            ResetRecoveryFailures("renderer crash đã tự phục hồi");
            SetStatus("ĐÃ CỨU TRANG", $"Chrome đã phục hồi; tiếp tục từ bước {resumeStep}/8.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception finalEx)
        {
            ReportProblem("PAGE_CRASH_HARD_RECOVERY_FAILED", "Chrome renderer",
                $"{context}: reload/navigate/restart Chrome đều chưa phục hồi được: {finalEx.Message}",
                error: true, throttleSeconds: 15);
            _log.Error($"[PAGE_CRASH_HARD_RECOVERY_FAILED] context={context} {finalEx}");
            return false;
        }
        finally
        {
            _pageRecoveryExecuting = false;
        }
    }

    async Task<bool> EnsureCdpRecoveredAsync(string context, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= CdpReconnectBackoff.Length; attempt++)
        {
            var delay = CdpReconnectBackoff[attempt - 1];
            _log.Warn($"[CDP_RECONNECT_START] context={context} attempt={attempt}/{CdpReconnectBackoff.Length} delayMs={(int)delay.TotalMilliseconds}");
            SetStatus("ĐANG RECONNECT CDP", $"{context}: attempt {attempt}/{CdpReconnectBackoff.Length}");
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

            try
            {
                await _chrome.ReconnectAsync(ct);
                _log.Warn($"[CDP_RECONNECTED] context={context} attempt={attempt}/{CdpReconnectBackoff.Length}");
                SetStatus("ĐANG CHẠY", $"CDP đã phục hồi: {context}");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"[CDP_RECONNECT_FAILED] context={context} attempt={attempt}/{CdpReconnectBackoff.Length} reason={ex.Message}");
            }
        }
        return false;
    }

    async Task SkipCurrentLiveAsync(string code, string context, string reason, CancellationToken ct)
    {
        _log.Warn($"[LIVE_SKIP] code={code} context={context} reason={reason}");
        SetStatus("ĐANG BỎ QUA LIVE LỖI", $"{context}: {reason}");
        _step = CurrentRestartStep;

        if (!_chrome.Connected && !await EnsureCdpRecoveredAsync($"LIVE_SKIP/{context}", ct))
        {
            IncreaseRecoveryFailures($"CDP session lost while skipping LIVE: {context}");
            return;
        }

        try
        {
            var action = _s.UseArrowDownForLiveSwitch ? TransitionAction.ArrowDown : TransitionAction.ClickXPath;
            var ok = await TransitionAsync($"bỏ qua LIVE lỗi {context}", action, _s.XPathPeriodicAction, 1, scheduledPeriodic: false, ct, F5WaitMs);
            if (ok)
            {
                _log.Warn($"[RECOVERY_OK] code={code} context={context} action=skip-live");
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[LIVE_SKIP] context={context} transition failed: {ex.Message}");
        }

        // A TikTok LIVE can reject a navigation or keep a stale DOM even though CDP
        // remains healthy.  Do not turn that normal, recoverable case into PAUSED.
        ReportProblem("LIVE_SKIP_UNCONFIRMED", context,
            "Đã retry bỏ qua LIVE nhưng chưa xác nhận được LIVE mới. Sẽ tiếp tục vòng chính và thử lại ở ranh giới an toàn.",
            throttleSeconds: 15);
        await Task.Delay(ArrowDownRetryDelayMs, ct);
    }

    Task SkipCurrentStepAsync(string code, string context, string reason, CancellationToken ct)
    {
        _log.Warn($"[STEP_SKIP] code={code} context={context} reason={reason}");
        SetStatus("ĐANG TỰ PHỤC HỒI", $"{context}: bỏ qua bước hiện tại.");
        _step = _step switch
        {
            >= 1 and < 8 => _step + 1,
            _ => 1
        };
        return Task.CompletedTask;
    }

    async Task ClickRequiredXPathAsync(string context, string xpath, CancellationToken ct)
    {
        await EnsureRequiredXPathWithRecoveryAsync(context, xpath, ct);
        try
        {
            _log.Info($"{context}: bắt đầu click XPath.");
            await _chrome.ClickXPathAsync(xpath, ct: ct);
            _log.Info($"{context}: click XPath xong.");
        }
        catch (Exception ex) { throw new RecoverableAutomationException("CLICK_REQUIRED_XPATH_FAILED", context, $"Click XPath thất bại: {ex.Message}", RecoveryDecision.SkipLive, ex); }
    }

    async Task InsertRequiredXPathAsync(string context, string xpath, string text, CancellationToken ct)
    {
        await EnsureRequiredXPathWithRecoveryAsync(context, xpath, ct);
        try
        {
            _log.Info($"{context}: bắt đầu nhập nội dung dài {text.Length} ký tự.");
            await _chrome.InsertTextAsync(xpath, text, ct);
            _log.Info($"{context}: nhập nội dung xong.");
        }
        catch (Exception ex) { throw new RecoverableAutomationException("INSERT_REQUIRED_XPATH_FAILED", context, $"Nhập chữ qua XPath thất bại: {ex.Message}", RecoveryDecision.SkipLive, ex); }
    }

    async Task EnsureRequiredXPathWithRecoveryAsync(string context, string xpath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateRequiredXPathOrThrow(context, xpath);

        _log.Info($"{context}: kiểm tra XPath bắt buộc trước khi tiếp tục workflow.");
        if (await RequiredXPathExistsAsync(context, xpath, ct)) return;

        // Nếu nhiều DOM/XPath biến mất vì tab vừa Out of Memory thì không được dùng luồng
        // "đổi LIVE để tìm lại XPath" ngay. Cứu renderer trước, rồi kiểm tra lại đúng LIVE.
        if (await TryRecoverRendererCrashFromFailureAsync($"XPath missing/{context}", null, ct))
        {
            if (await RequiredXPathExistsAsync(context, xpath, ct)) return;
        }

        for (int attempt = 1; attempt <= RequiredXPathRecoveryMaxAttempts; attempt++)
        {
            _log.Warn($"[XPATH_RECOVERY_START] context={context} xpath={xpath} attempt={attempt}/{RequiredXPathRecoveryMaxAttempts}");
            SetStatus("RECOVERY XPATH", $"{context} thiếu XPath, đang recovery {attempt}/{RequiredXPathRecoveryMaxAttempts}.");

            var action = _s.UseArrowDownForLiveSwitch ? TransitionAction.ArrowDown : TransitionAction.ClickXPath;
            _log.Warn($"[XPATH_RECOVERY_TRANSITION] {(_s.UseArrowDownForLiveSwitch ? "ArrowDown CDP -> F5" : "Live switch hien tai -> F5")}");

            var transitioned = await TransitionAsync(
                $"XPATH recovery {context} {attempt}/{RequiredXPathRecoveryMaxAttempts}",
                action,
                _s.XPathPeriodicAction,
                1,
                scheduledPeriodic: false,
                ct,
                (int)RequiredXPathRecoveryWait.TotalMilliseconds);

            if (!transitioned)
            {
                _log.Warn($"[XPATH_RECOVERY_RECHECK] context={context} result=False attempt={attempt}/{RequiredXPathRecoveryMaxAttempts}");
                continue;
            }

            _log.Warn($"[XPATH_RECOVERY_WAIT] Chờ {RequiredXPathRecoveryWait.TotalSeconds:0} giây cho LIVE ổn định");
            var recovered = await RequiredXPathExistsAsync(context, xpath, ct);
            _log.Warn($"[XPATH_RECOVERY_RECHECK] context={context} result={recovered} attempt={attempt}/{RequiredXPathRecoveryMaxAttempts}");
            if (recovered)
            {
                _log.Warn($"[XPATH_RECOVERY_OK] context={context} attempt={attempt}/{RequiredXPathRecoveryMaxAttempts}");
                _log.Info($"[XPATH_RECOVERY_OK] {context} đã xuất hiện lại sau recovery {attempt}/{RequiredXPathRecoveryMaxAttempts}.");
                SetStatus("RECOVERY THÀNH CÔNG", $"{context} đã xuất hiện lại sau recovery {attempt}/{RequiredXPathRecoveryMaxAttempts}.");
                return;
            }
        }

        _log.Error($"[XPATH_RECOVERY_FAILED] context={context} xpath={xpath} attempts={RequiredXPathRecoveryMaxAttempts}");
        ReportProblem("LIVE_UNUSABLE", context, $"Không tìm thấy XPath quan trọng sau {RequiredXPathRecoveryMaxAttempts} lần tự phục hồi. XPath={xpath}", error: true, throttleSeconds: 15);
        throw new RecoverableAutomationException("LIVE_UNUSABLE", context, $"XPath bắt buộc không phục hồi được sau {RequiredXPathRecoveryMaxAttempts} lần.", RecoveryDecision.SkipLive);
    }

    void ValidateRequiredXPathOrThrow(string context, string xpath)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            ReportProblem("XPATH_CONFIG_MISSING", context, "XPath bắt buộc đang trống.", error: true, throttleSeconds: 30);
            throw new InvalidOperationException($"[{context}] chưa cấu hình XPath.");
        }

        try
        {
            XPathExpression.Compile(xpath);
        }
        catch (XPathException ex)
        {
            ReportProblem("XPATH_INVALID", context, $"XPath không hợp lệ: {xpath}. Chi tiết: {ex.Message}", error: true, throttleSeconds: 30);
            throw new InvalidOperationException($"[{context}] XPath không hợp lệ: {xpath}", ex);
        }
    }

    async Task<bool> RequiredXPathExistsAsync(string context, string xpath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _chrome.XPathExistsAsync(xpath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (_chrome.IsCdpSessionLost(ex))
        {
            ReportProblem("CDP_SESSION_LOST", context, "CDP/session/target thực sự mất khi kiểm tra XPath bắt buộc.", error: true, throttleSeconds: 15);
            throw new RecoverableAutomationException("CDP_SESSION_LOST", context, "CDP/session/target thực sự mất khi kiểm tra XPath bắt buộc.", RecoveryDecision.SkipLive, ex);
        }
    }

    async Task<(bool Ready, string Signal)> ProbeLivePageReadyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Ưu tiên các XPath nhẹ. Chỉ cần MỘT tín hiệu LIVE đã render là đủ để bắt đầu
        // kiểm tra trạng thái nghiệp vụ; không yêu cầu ô nhập phải tồn tại vì chính
        // InputGuard cần phân biệt LIVE bị khóa bình luận với trang chưa load.
        var probes = new List<(string Name, string XPath)>();
        if (_s.OldLive.Enabled && !string.IsNullOrWhiteSpace(_s.OldLive.IdentityXPath))
            probes.Add(("old-live-identity", _s.OldLive.IdentityXPath));
        if (_s.Viewer.Enabled && !string.IsNullOrWhiteSpace(_s.Viewer.XPath))
            probes.Add(("viewer", _s.Viewer.XPath));
        if (!string.IsNullOrWhiteSpace(CurrentInputXPath))
            probes.Add((CurrentPointName + "-input", CurrentInputXPath));

        foreach (var probe in probes
            .Where(x => !string.IsNullOrWhiteSpace(x.XPath))
            .GroupBy(x => x.XPath.Trim(), StringComparer.Ordinal)
            .Select(g => g.First()))
        {
            try
            {
                if (await _chrome.XPathExistsAsync(probe.XPath, ct))
                    return (true, "xpath:" + probe.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!_chrome.IsCdpSessionLost(ex))
            {
                _log.Warn($"[PAGE_READY_PROBE_WARN] signal={probe.Name} reason={ex.Message}");
            }
        }

        try
        {
            var identity = await GetCurrentLiveIdentityAsync(ct);
            if (HasReliableLiveIdentity(identity))
                return (true, "live-identity:" + TrimIdentityForLog(GetLivePageChangeKey(identity), 100));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!_chrome.IsCdpSessionLost(ex))
        {
            _log.Warn($"[PAGE_READY_PROBE_WARN] signal=live-identity reason={ex.Message}");
        }

        return (false, "none");
    }

    async Task<bool> WaitForLivePageReadyAsync(string source, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= PageReadyRetryAttempts; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _log.Info($"[PAGE_READY_WAIT] source={source} attempt={attempt}/{PageReadyRetryAttempts} timeoutMs={PageReadyTimeoutMs} pollMs={PageReadyPollMs}");
            SetStatus("ĐANG CHỜ TRANG LIVE", $"{source} • lần {attempt}/{PageReadyRetryAttempts} • tối đa {PageReadyTimeoutMs / 1000}s");

            while (sw.ElapsedMilliseconds < PageReadyTimeoutMs)
            {
                await WaitIfPausedAsync(ct);
                var probe = await ProbeLivePageReadyAsync(ct);
                if (probe.Ready)
                {
                    _log.Info($"[PAGE_READY] source={source} attempt={attempt}/{PageReadyRetryAttempts} elapsedMs={sw.ElapsedMilliseconds} signal={probe.Signal}");
                    return true;
                }

                await Task.Delay(PageReadyPollMs, ct);
            }

            _log.Warn($"[PAGE_READY_TIMEOUT] source={source} attempt={attempt}/{PageReadyRetryAttempts} waitedMs={sw.ElapsedMilliseconds} action=NO_F5");
            if (attempt < PageReadyRetryAttempts)
            {
                _log.Warn($"[PAGE_READY_RETRY] source={source} nextAttempt={attempt + 1}/{PageReadyRetryAttempts} waitMs={PageReadyRetryPauseMs} action=WAIT_ONLY");
                await Task.Delay(PageReadyRetryPauseMs, ct);
            }
        }

        ReportProblem("PAGE_READY_TIMEOUT", source,
            $"TikTok chưa render xong tín hiệu LIVE sau {PageReadyRetryAttempts} lần chờ × {PageReadyTimeoutMs / 1000}s. Không F5/không đổi LIVE; vòng chính sẽ kiểm tra lại.",
            throttleSeconds: 20);
        return false;
    }

    string CurrentInputXPath => _step <= 4 ? _s.XPathPoint1 : _s.XPathPoint2;
    int CurrentRestartStep => _step <= 4 ? 1 : 5;
    string CurrentPointName => _step <= 4 ? "điểm 1" : "điểm 2";

    void AdvanceContentAfterSuccessfulSend(string pointName)
    {
        if (_contents.Count == 0) return;
        var used = _contentIndex + 1;
        _contentIndex = (_contentIndex + 1) % _contents.Count;
        _log.Info($"[CONTENT_ADVANCE] point={pointName} used={used}/{_contents.Count} next={_contentIndex + 1}/{_contents.Count}");
        NotifyStateChanged();
    }

    async Task ExecuteOneStepAsync(CancellationToken ct)
    {
        var stepAtStart = _step;
        var stepPerf = System.Diagnostics.Stopwatch.StartNew();
        var content = _contents[_contentIndex];
        try
        {
            switch (_step)
            {
                case 1:
                {
                    SetStatus("BƯỚC 1/8", $"Kiểm tra người xem + ô nhập → Click ô 1 • nội dung {_contentIndex + 1}/{_contents.Count}");
                    if (!await WaitForLivePageReadyAsync("trước bước 1/8", ct)) return;
                    if (!await EnsureViewerGateBeforeActionAsync("trước Click điểm 1", ct)) return;
                    if (await GuardAndProcessBeforeClickAsync(_s.XPathPoint1, "điểm 1", ct)) return;
                    _log.Info($"Nội dung {_contentIndex + 1}/{_contents.Count}: ô nhập 1 bình thường, bắt đầu click.");
                    await ClickRequiredXPathAsync("Điểm/ô nhập 1", _s.XPathPoint1, ct);
                    _step = 2;
                    await Task.Delay(NormalCdpDelay(), ct);
                    break;
                }
                case 2:
                    SetStatus("BƯỚC 2/8", $"Nhập nội dung {_contentIndex + 1}/{_contents.Count} vào ô 1");
                    await InsertRequiredXPathAsync("Điểm/ô nhập 1", _s.XPathPoint1, content, ct);
                    _step = 3;
                    await Task.Delay(NormalCdpDelay(), ct);
                    break;

                case 3:
                {
                    SetStatus("BƯỚC 3/8", "Enter ô 1 • theo dõi phản hồi cấm bình luận");
                    await _chrome.PressKeyAsync("Enter", ct: ct);
                    if (await WatchPostEnterCommentRestrictionAsync("điểm 1", restartStep: 1, ct)) return;
                    AdvanceContentAfterSuccessfulSend("điểm 1");
                    _step = 4;
                    break;
                }
                case 4:
                    SetStatus("BƯỚC 4/8", "Hoàn tất điểm 1 • chuyển sang điểm 2");
                    _step = 5;
                    break;

                case 5:
                {
                    SetStatus("BƯỚC 5/8", $"Kiểm tra người xem + ô nhập → Click ô 2 • nội dung {_contentIndex + 1}/{_contents.Count}");
                    if (!await WaitForLivePageReadyAsync("trước bước 5/8", ct)) return;
                    if (!await EnsureViewerGateBeforeActionAsync("trước Click điểm 2", ct)) return;
                    if (await GuardAndProcessBeforeClickAsync(_s.XPathPoint2, "điểm 2", ct)) return;
                    _log.Info($"Nội dung {_contentIndex + 1}/{_contents.Count}: ô nhập 2 bình thường, bắt đầu click.");
                    await ClickRequiredXPathAsync("Điểm/ô nhập 2", _s.XPathPoint2, ct);
                    _step = 6;
                    await Task.Delay(NormalCdpDelay(), ct);
                    break;
                }
                case 6:
                    SetStatus("BƯỚC 6/8", $"Nhập nội dung {_contentIndex + 1}/{_contents.Count} vào ô 2");
                    await InsertRequiredXPathAsync("Điểm/ô nhập 2", _s.XPathPoint2, content, ct);
                    _step = 7;
                    await Task.Delay(NormalCdpDelay(), ct);
                    break;

                case 7:
                {
                    SetStatus("BƯỚC 7/8", "Enter ô 2 • theo dõi phản hồi cấm bình luận");
                    await _chrome.PressKeyAsync("Enter", ct: ct);
                    if (await WatchPostEnterCommentRestrictionAsync("điểm 2", restartStep: 5, ct)) return;
                    AdvanceContentAfterSuccessfulSend("điểm 2");
                    _step = 8;
                    break;
                }
                case 8:
                {
                    SetStatus("BƯỚC 8/8", "Hoàn tất vòng • nội dung đã tăng sau từng lần gửi");
                    _rounds++;
                    _step = 1;
                    SetStatus("ĐANG CHẠY", $"Hoàn tất vòng {_rounds}. Nội dung tiếp theo {_contentIndex + 1}/{_contents.Count}.");
                    NotifyStateChanged();
                    await Task.Delay(NormalLoopDelay(), ct);
                    if (_loopPerf is not null)
                    {
                        var totalMs = _loopPerf.ElapsedMilliseconds;
                        _loopPerfTotalMs += totalMs;
                        _loopPerfCount++;
                        _log.Info($"[LOOP_PERF] totalMs={totalMs} avgMs={_loopPerfTotalMs / _loopPerfCount} round={_rounds}");
                        _loopPerf.Restart();
                    }
                    break;
                }

                default:
                    _step = 1;
                    break;
            }
        }
        finally
        {
            stepPerf.Stop();
            _log.Info($"[STEP_PERF] step={stepAtStart} elapsedMs={stepPerf.ElapsedMilliseconds}");
        }
    }

    void ShiftPeriodicClock(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero) return;
        if (_s.PeriodicF5Minutes > 0)
        {
            if (_periodicDue != DateTime.MaxValue) _periodicDue += delta;
            if (_candidateCaptureAt != DateTime.MaxValue) _candidateCaptureAt += delta;
        }
        if (_pageMaintenanceDue != DateTime.MaxValue) _pageMaintenanceDue += delta;
        if (_nextPageHealthProbe != DateTime.MinValue) _nextPageHealthProbe += delta;
        SyncPeriodicSnapshot();
    }

    int PageRecoveryRestartStep => _step switch
    {
        1 or 2 or 3 => 1,
        4 or 5 or 6 or 7 => 5,
        _ => 1
    };

    void ResetPageMaintenanceDue(string reason)
    {
        _pageMaintenanceDue = DateTime.Now.AddMinutes(PageMaintenanceReloadMinutes);
        _log.Info($"[PAGE_MAINTENANCE_SCHEDULED] minutes={PageMaintenanceReloadMinutes} due={_pageMaintenanceDue:HH:mm:ss} reason={reason}");
    }

    async Task<bool> HandleImmediatePageCrashRecoveryAsync(CancellationToken ct)
    {
        if (_pageRecoveryExecuting || DateTime.Now < _nextPageHealthProbe) return false;
        _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);

        ChromeController.PageHealthSnapshot health;
        try
        {
            health = await _chrome.ProbePageHealthAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[PAGE_HEALTH_PROBE_FAILED] reason={ex.Message}");
            return false;
        }

        if (health.Healthy)
        {
            if (!string.IsNullOrWhiteSpace(health.Url) &&
                health.Url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
                _lastHealthyTikTokUrl = health.Url;
            return false;
        }

        if (!health.CrashLike) return false;

        _pageRecoveryExecuting = true;
        var resumeStep = PageRecoveryRestartStep;
        _step = resumeStep;
        SetStatus("ĐANG CỨU TRANG", $"Phát hiện Chrome lỗi ({health.Reason}) → reload ngay.");
        _log.Warn($"[PAGE_CRASH_DETECTED] reason={health.Reason} url={health.Url} action=RECOVER_NOW restartStep={resumeStep}");
        try
        {
            var fallback = !string.IsNullOrWhiteSpace(_lastHealthyTikTokUrl)
                ? _lastHealthyTikTokUrl
                : health.Url;
            await _chrome.RecoverCurrentPageAsync(fallback, ct);
            ResetPageMaintenanceDue("vừa tự cứu trang crash");
            _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);
            ResetInputGuardConsecutive("sau tự cứu trang crash");
            _log.Warn($"[PAGE_CRASH_RECOVERED] restartStep={resumeStep} action=RECHECK_WORKFLOW");
            SetStatus("ĐÃ CỨU TRANG", $"Chrome đã reload; tiếp tục từ bước {resumeStep}/8.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[PAGE_CRASH_RECOVERY_RELOAD_FAILED] reason={ex.Message} action=restart-managed-chrome");
            try
            {
                var fallback = !string.IsNullOrWhiteSpace(_lastHealthyTikTokUrl)
                    ? _lastHealthyTikTokUrl
                    : health.Url;
                await _chrome.RestartManagedChromeForRecoveryAsync(fallback, ct);
                ResetPageMaintenanceDue("restart Chrome sau renderer crash");
                _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);
                ResetInputGuardConsecutive("sau restart Chrome renderer crash");
                ResetRecoveryFailures("restart Chrome renderer crash thành công");
                _log.Warn($"[PAGE_CRASH_RECOVERED] restartStep={resumeStep} method=restart-managed-chrome action=RECHECK_WORKFLOW");
                SetStatus("ĐÃ CỨU TRANG", $"Chrome đã được mở lại; tiếp tục từ bước {resumeStep}/8.");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception restartEx)
            {
                ReportProblem("PAGE_CRASH_RECOVERY_FAILED", "Chrome renderer",
                    "Reload và restart Chrome đều chưa tự phục hồi được: " + restartEx.Message,
                    error: true, throttleSeconds: 10);
                _log.Error($"[PAGE_CRASH_RECOVERY_FAILED] reload={ex.Message}; restart={restartEx}");
                // Không Stop() ở đây. Vòng chính sẽ tiếp tục probe/retry; chỉ cơ chế chống
                // lỗi liên tiếp hiện có mới tạm dừng nếu Chrome thực sự không thể phục hồi.
                await Task.Delay(1000, ct);
                return true;
            }
        }
        finally
        {
            _pageRecoveryExecuting = false;
        }
    }

    async Task<bool> HandlePageMaintenanceReloadAsync(CancellationToken ct)
    {
        if (_pageRecoveryExecuting || DateTime.Now < _pageMaintenanceDue) return false;

        // Bước 4/8 chỉ đổi state nội bộ sau một lần gửi thành công. Cho nó hoàn tất trước,
        // tránh reset về sai điểm và không bao giờ chen reload giữa Click/Dán/Enter.
        if (_step is 4 or 8) return false;

        _pageRecoveryExecuting = true;
        var resumeStep = PageRecoveryRestartStep;
        _step = resumeStep;
        SetStatus("F5 BẢO TRÌ 30 PHÚT", $"Reload trang hiện tại • tiếp tục bước {resumeStep}/8.");
        _log.Info($"[PAGE_MAINTENANCE_RELOAD_START] intervalMin={PageMaintenanceReloadMinutes} restartStep={resumeStep}");
        try
        {
            try
            {
                await _chrome.ReloadAndWaitAsync(PageRecoveryReloadWaitMs, 15000, ct);
            }
            catch (Exception ex)
            {
                _log.Warn($"[PAGE_MAINTENANCE_RELOAD_FALLBACK] normalReloadFailed={ex.Message}");
                await _chrome.RecoverCurrentPageAsync(_lastHealthyTikTokUrl, ct);
            }

            if (!await WaitForLivePageReadyAsync("sau F5 bảo trì", ct))
            {
                _pageMaintenanceDue = DateTime.Now.AddMinutes(1);
                _log.Warn("[PAGE_MAINTENANCE_PAGE_NOT_READY] F5 đã hoàn tất nhưng TikTok chưa hydrate xong; không F5 thêm, sẽ kiểm tra lại ở vòng chính.");
                return true;
            }

            ResetPageMaintenanceDue("F5 bảo trì thành công");
            _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);
            ResetInputGuardConsecutive("sau F5 bảo trì");
            _log.Info($"[PAGE_MAINTENANCE_RELOAD_OK] restartStep={resumeStep}");
            SetStatus("ĐANG CHẠY", $"F5 bảo trì xong • tiếp tục bước {resumeStep}/8.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ReportProblem("PAGE_MAINTENANCE_RELOAD_FAILED", "F5 bảo trì",
                "Reload định kỳ 30 phút thất bại: " + ex.Message,
                error: true, throttleSeconds: 30);
            _pageMaintenanceDue = DateTime.Now.AddMinutes(1);
            return true;
        }
        finally
        {
            _pageRecoveryExecuting = false;
        }
    }

    async Task<bool> HandlePeriodicCaptureAndF5Async(CancellationToken ct)
    {
        if (_s.PeriodicF5Minutes <= 0) return false;
        var now = DateTime.Now;

        if (_s.OldLive.Enabled && string.IsNullOrWhiteSpace(_s.OldLive.IdentityXPath) && now >= _candidateCaptureAt)
        {
            ReportProblem("XPATH_OLDLIVE_IDENTITY_MISSING", "Live cũ", "Đã bật Live cũ nhưng XPath tài khoản LIVE đang trống. Bỏ qua lưu định danh T-10s.", error: true, throttleSeconds: 30);
            _candidateCaptureAt = DateTime.MaxValue;
            return true;
        }

        if (_s.OldLive.Enabled && now >= _candidateCaptureAt)
        {
            try
            {
                _log.Info("[OLD_LIVE_IDENTITY_SAVE_START]");
                var identity = await ProbeOldLiveIdentityAsync(ct);
                _candidateCaptureAt = DateTime.MaxValue;
                AddOldLiveEntry(identity, "PERIODIC_T_MINUS_10");
                _nextOldLiveScan = DateTime.Now.AddMilliseconds(OldLiveScanRetryMs);
                _log.Info($"[OLD_LIVE_IDENTITY_SAVE_OK] identity={identity.IdentityKey} display={identity.DisplayName}");
                _log.Info("F5 định kỳ còn <=10 giây: đã lưu định danh tài khoản hiện tại vào danh sách Live cũ active.");
                SetStatus("LIVE CŨ T-10s", $"Đã lưu {identity.DisplayName} vào danh sách Live cũ active.");
                return true;
            }
            catch (Exception ex)
            {
                if (now < _periodicDue)
                {
                    _candidateCaptureAt = DateTime.Now.AddSeconds(1);
                    _log.Warn("F5 định kỳ còn <=10 giây nhưng chưa đọc được định danh Live cũ; sẽ thử lại: " + ex.Message);
                    return true;
                }

                _candidateCaptureAt = DateTime.MaxValue;
                _log.Warn("Đã đến hạn F5 định kỳ nhưng chưa đọc được định danh Live cũ; tiếp tục F5: " + ex.Message);
            }
        }

        if (now < _periodicDue) return false;

        // V10 cho bước 4/8 hoàn tất trước để tránh lặp lại bình luận vừa gửi xong.
        if (_step is 4 or 8) return false;

        if (!_s.UseArrowDownForLiveSwitch && string.IsNullOrWhiteSpace(_s.XPathPeriodicAction))
        {
            ReportProblem("XPATH_PERIODIC_MISSING", "F5 định kỳ", "XPath nút chuyển live đang trống. Đã lùi 30 giây và bỏ qua lần này; không dùng tọa độ fallback.", error: true, throttleSeconds: 30);
            _periodicDue = now.AddSeconds(30);
            _candidateCaptureAt = _periodicDue.AddSeconds(-10);
            SyncPeriodicSnapshot();
            return true;
        }

        _step = CurrentRestartStep;
        var periodicAction = _s.UseArrowDownForLiveSwitch ? TransitionAction.ArrowDown : TransitionAction.ClickXPath;
        _periodicExecuting = true;
        SyncPeriodicSnapshot();
        bool periodicOk;
        try
        {
            periodicOk = await TransitionAsync("F5 định kỳ", periodicAction, _s.XPathPeriodicAction, 1, scheduledPeriodic: true, ct, F5WaitMs);
        }
        finally
        {
            _periodicExecuting = false;
            SyncPeriodicSnapshot();
        }
        if (!periodicOk)
        {
            _log.Warn("F5 định kỳ chưa thực hiện được; các định danh Live cũ đã lưu vẫn được giữ nguyên tới khi từng entry hết TTL. Sẽ thử lại sau 30 giây.");
            _periodicDue = DateTime.Now.AddSeconds(30);
            _candidateCaptureAt = _periodicDue.AddSeconds(-10);
            SyncPeriodicSnapshot();
            return true;
        }

        if (_s.OldLive.Enabled && _activeOldLives.Count > 0)
            _log.Info("F5 định kỳ hoàn tất; giữ nguyên mọi định danh Live cũ active cho tới khi từng entry hết TTL.");

        // Viewer Gate sẽ tự kiểm tra ngay trước Click kế tiếp; không còn đặt lịch Viewer định kỳ.
        if (!_s.Viewer.Enabled) await Task.Delay(ActionDelay(), ct);
        return true;
    }

    void ResetPeriodicDue(string reason, bool cancelCandidate)
    {
        if (_s.PeriodicF5Minutes <= 0)
        {
            _periodicDue = DateTime.MaxValue;
            _candidateCaptureAt = DateTime.MaxValue;
            SyncPeriodicSnapshot();
            return;
        }
        _periodicDue = DateTime.Now.AddMinutes(_s.PeriodicF5Minutes);
        _candidateCaptureAt = _periodicDue.AddSeconds(-10);
        _log.Info($"Đã reset bộ đếm F5 định kỳ về {_s.PeriodicF5Minutes} phút: {reason}");
        SyncPeriodicSnapshot();
    }

    async Task<bool> HandleOldLiveExpiryAndScanAsync(CancellationToken ct)
    {
        CleanupExpiredOldLives();
        if (_activeOldLives.Count == 0) return false;
        var now = DateTime.Now;
        if (now < _nextOldLiveScan || _transitioning) return false;
        if (_s.PeriodicF5Minutes <= 0) return false;
        // Giữ nguyên lịch cũ: không chen Live cũ vào 3 giây sát F5 định kỳ hoặc bước 4/8.
        if (_periodicDue != DateTime.MaxValue && _periodicDue - now <= TimeSpan.FromSeconds(3)) return false;
        if (_step is 4 or 8) return false;
        _nextOldLiveScan = now.AddMilliseconds(OldLiveScanIntervalMs);

        try
        {
            _log.Info($"[OLD_LIVE_IDENTITY_SCAN_START] activeCount={_activeOldLives.Count}");
            var current = await ProbeOldLiveIdentityAsync(ct);
            _lastOldLiveMatchAt = DateTime.Now;
            _lastOldLiveMatchIdentity = $"{current.DisplayName} | {current.IdentityKey}";

            var matchedEntry = _activeOldLives
                .OrderBy(e => e.ExpiresAt)
                .FirstOrDefault(e => string.Equals(e.IdentityKey, current.IdentityKey, StringComparison.OrdinalIgnoreCase));

            if (matchedEntry is null)
            {
                _lastOldLiveMatchFound = false;
                _log.Info($"[OLD_LIVE_IDENTITY_NO_MATCH] current={current.IdentityKey} display={current.DisplayName}");
                return false;
            }

            var remaining = matchedEntry.ExpiresAt - DateTime.Now;
            var age = DateTime.Now - matchedEntry.CreatedAt;
            _lastOldLiveMatchFound = true;
            _log.Warn($"[OLD_LIVE_IDENTITY_MATCH] id={matchedEntry.Id} identity={matchedEntry.IdentityKey} display={matchedEntry.DisplayName} age={age:c} remaining={remaining:c}");

            _log.Warn("LIVE CŨ: định danh tài khoản đã KHỚP. Thực hiện lại vòng chuyển live + F5; không gia hạn entry đang dùng.");
            var xp = string.IsNullOrWhiteSpace(_s.OldLive.ActionXPath) ? _s.XPathPeriodicAction : _s.OldLive.ActionXPath;
            if (!_s.UseArrowDownForLiveSwitch && string.IsNullOrWhiteSpace(xp))
            {
                ReportProblem("XPATH_OLDLIVE_ACTION_MISSING", "Live cũ", "Đã phát hiện Live cũ nhưng XPath nút chuyển live đang trống. Đã bỏ qua hành động; không dùng tọa độ fallback.", error: true, throttleSeconds: 30);
                return false;
            }
            _step = CurrentRestartStep;
            var oldLiveAction = _s.UseArrowDownForLiveSwitch ? TransitionAction.ArrowDown : TransitionAction.ClickXPath;
            var oldLiveTransitioned = await TransitionAsync("phát hiện LIVE CŨ", oldLiveAction, xp, 1, scheduledPeriodic: false, ct, F5WaitMs);
            if (!oldLiveTransitioned)
            {
                _nextOldLiveScan = DateTime.Now.AddMilliseconds(OldLiveScanRetryMs);
                return false;
            }
            _nextOldLiveScan = DateTime.Now.AddMilliseconds(OldLiveScanRetryMs);
            // Viewer Gate sẽ kiểm tra LIVE mới trước Click kế tiếp.
            return true;
        }
        catch (Exception ex)
        {
            ReportProblem("OLDLIVE_IDENTITY_SCAN_ERROR", "Live cũ", ex.Message, throttleSeconds: 20);
            return false;
        }
    }

    async Task<LiveAccountIdentityProbe.Snapshot> ProbeOldLiveIdentityAsync(CancellationToken ct)
    {
        var xpath = _s.OldLive.IdentityXPath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(xpath))
            throw new InvalidOperationException("Live cũ: XPath tài khoản LIVE đang trống.");

        var snapshot = await _oldLiveIdentityProbe.ProbeAsync(xpath, ct);
        if (!snapshot.IsValid)
            throw new InvalidOperationException("Live cũ: không đọc được định danh tài khoản từ XPath. " + snapshot.Reason);
        return snapshot;
    }

    string OldLiveDirectoryPath => Path.Combine(_baseDir, OldLiveDirectoryName);
    string OldLiveManifestPath => Path.Combine(OldLiveDirectoryPath, OldLiveManifestFileName);

    void EnsureOldLivesReadyForRun()
    {
        LoadPersistedOldLives();
        CleanupExpiredOldLives();
        _nextOldLiveScan = _s.OldLive.Enabled && _activeOldLives.Count > 0
            ? DateTime.Now.AddMilliseconds(OldLiveScanRetryMs)
            : DateTime.MaxValue;
    }

    void LoadPersistedOldLives()
    {
        if (_oldLiveManifestLoaded) return;
        _oldLiveManifestLoaded = true;
        if (!File.Exists(OldLiveManifestPath)) return;

        try
        {
            var entries = JsonSerializer.Deserialize<List<PersistedOldLiveEntry>>(File.ReadAllText(OldLiveManifestPath), OldLiveStoreJson) ?? [];
            foreach (var persisted in entries)
            {
                if (string.IsNullOrWhiteSpace(persisted.Id) || string.IsNullOrWhiteSpace(persisted.IdentityKey)) continue;
                if (_activeOldLives.Any(e => e.Id.Equals(persisted.Id, StringComparison.OrdinalIgnoreCase))) continue;
                _activeOldLives.Add(new OldLiveEntry(
                    persisted.Id,
                    persisted.IdentityKey.Trim(),
                    string.IsNullOrWhiteSpace(persisted.DisplayName) ? persisted.IdentityKey.Trim() : persisted.DisplayName.Trim(),
                    persisted.Username?.Trim() ?? "",
                    persisted.Href?.Trim() ?? "",
                    persisted.CreatedAt,
                    persisted.ExpiresAt,
                    persisted.Source ?? "RESTORE"));
            }
            if (_activeOldLives.Count > 0)
                _log.Info($"[OLD_LIVE_IDENTITY_RESTORE] restored={_activeOldLives.Count} manifest={OldLiveManifestPath}");
            CleanupExpiredOldLives();
        }
        catch (Exception ex)
        {
            _log.Warn($"[OLD_LIVE_IDENTITY_RESTORE] cannot read manifest: {ex.Message}");
        }
    }

    void PersistOldLiveManifest()
    {
        try
        {
            Directory.CreateDirectory(OldLiveDirectoryPath);
            var entries = _activeOldLives
                .OrderBy(e => e.ExpiresAt)
                .Select(e => new PersistedOldLiveEntry(e.Id, e.IdentityKey, e.DisplayName, e.Username, e.Href, e.CreatedAt, e.ExpiresAt, e.Source))
                .ToList();
            var temporaryPath = OldLiveManifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, OldLiveStoreJson));
            File.Move(temporaryPath, OldLiveManifestPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"[OLD_LIVE_IDENTITY_STORE] cannot persist manifest: {ex.Message}");
        }
    }

    void AddOldLiveEntry(LiveAccountIdentityProbe.Snapshot identity, string source)
    {
        var now = DateTime.Now;
        var entry = new OldLiveEntry(
            GenerateOldLiveId(now),
            identity.IdentityKey,
            identity.DisplayName,
            identity.Username,
            identity.Href,
            now,
            now.AddMinutes(Math.Max(1, _s.OldLive.KeepMinutes)),
            source);
        _activeOldLives.Add(entry);
        _lastOldLiveSavedAt = now;
        _log.Info($"[OLD_LIVE_IDENTITY_ADDED] id={entry.Id} identity={entry.IdentityKey} display={entry.DisplayName} activeCount={_activeOldLives.Count} expiresAt={entry.ExpiresAt:O}");
        CleanupExpiredOldLives();
        PersistOldLiveManifest();
    }

    void CleanupExpiredOldLives()
    {
        var now = DateTime.Now;
        var removed = false;
        foreach (var entry in _activeOldLives.Where(e => now >= e.ExpiresAt).ToList())
        {
            _activeOldLives.Remove(entry);
            _log.Info($"[OLD_LIVE_IDENTITY_EXPIRED] id={entry.Id} identity={entry.IdentityKey} expiresAt={entry.ExpiresAt:O}");
            removed = true;
        }
        if (_activeOldLives.Count == 0)
            _nextOldLiveScan = DateTime.MaxValue;
        if (removed) PersistOldLiveManifest();
    }

    public int ClearOldLivesManually()
    {
        if (_running) throw new InvalidOperationException("Hãy dừng tool trước khi xóa danh sách Live cũ active.");
        EnsureOldLivesReadyForRun();
        var count = _activeOldLives.Count;
        _activeOldLives.Clear();
        _nextOldLiveScan = DateTime.MaxValue;
        PersistOldLiveManifest();
        _log.Warn($"[OLD_LIVE_IDENTITY_MANUAL_CLEAR] removed={count}");
        return count;
    }

    static string GenerateOldLiveId(DateTime now) => $"old_live_{now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";

    async Task<(int value, string raw)> ReadViewerWithRetryAsync(string source, CancellationToken ct, int attempts = ViewerReadRetryCount)
    {
        attempts = Math.Clamp(attempts, 1, ViewerReadRetryCount);
        (int value, string raw) last = (-1, "");

        for (int attempt = 1; attempt <= attempts && _running; attempt++)
        {
            await WaitIfPausedAsync(ct);
            last = await ReadViewerAsync(ct);
            if (last.value >= 0)
            {
                if (attempt > 1)
                    _log.Info($"[VIEWER_GATE_RENDERED] source={source} attempt={attempt}/{attempts} value={last.value} raw={last.raw}");
                return last;
            }

            if (attempt < attempts)
            {
                _log.Warn($"[VIEWER_GATE_WAIT_RENDER] source={source} attempt={attempt}/{attempts} waitMs={ViewerReadRetryDelayMs}");
                await Task.Delay(ViewerReadRetryDelayMs, ct);
            }
        }

        return last;
    }

    async Task<bool> EnsureViewerGateBeforeActionAsync(string source, CancellationToken ct)
    {
        if (!_s.Viewer.Enabled) return true;

        SetStatus("KIỂM TRA NGƯỜI XEM", $"{source} • bắt buộc đọc XPath trước khi thao tác");
        var (value, raw) = await ReadViewerWithRetryAsync(source, ct);

        if (value < 0)
        {
            _log.Warn($"[VIEWER_GATE_UNREADABLE] source={source} Không đọc được người xem sau {ViewerReadRetryCount} lần; KHÔNG cho workflow Click/Dán/Enter. Chuyển LIVE để tìm LIVE đọc được.");
            return await FindAcceptableViewerLiveAsync(source + " / không đọc được Viewer", null, ct);
        }

        _log.Info($"[VIEWER_GATE_READ] source={source} value={value} threshold={_s.Viewer.Threshold} raw={raw}");
        if (value > _s.Viewer.Threshold)
        {
            ResetLowViewerStreak($"Viewer đạt ngưỡng tại {source}");
            _log.Info($"[VIEWER_GATE_OK] source={source} value={value} > threshold={_s.Viewer.Threshold}");
            return true;
        }

        // Giữ xác nhận thấp cũ để tránh một lần DOM chớp số thấp gây chuyển LIVE nhầm.
        var lowCount = 1;
        while (lowCount < Math.Max(1, _s.Viewer.ConfirmLow))
        {
            await Task.Delay(2000, ct);
            await WaitIfPausedAsync(ct);
            var (confirm, rawConfirm) = await ReadViewerWithRetryAsync(source + " / xác nhận thấp", ct);
            if (confirm < 0)
            {
                _log.Warn($"[VIEWER_GATE_CONFIRM_UNREADABLE] source={source} Không đọc được khi xác nhận thấp; KHÔNG cho workflow tiếp tục.");
                return await FindAcceptableViewerLiveAsync(source + " / xác nhận Viewer không đọc được", null, ct);
            }

            lowCount++;
            _log.Info($"[VIEWER_GATE_CONFIRM_LOW] source={source} {lowCount}/{_s.Viewer.ConfirmLow} value={confirm} raw={rawConfirm}");
            if (confirm > _s.Viewer.Threshold)
            {
                ResetLowViewerStreak($"Viewer đạt ngưỡng khi xác nhận tại {source}");
                _log.Info($"[VIEWER_GATE_OK] source={source} xác nhận lại value={confirm} > threshold={_s.Viewer.Threshold}");
                return true;
            }
            value = confirm;
        }

        return await FindAcceptableViewerLiveAsync(source, value, ct);
    }

    bool IsActiveOldLiveRecommendation(TikTokRecommendedLiveCandidate candidate)
    {
        if (_activeOldLives.Count == 0) return false;
        var username = (candidate.Username ?? "").Trim().TrimStart('@').ToLowerInvariant();
        var href = (candidate.Href ?? "").Trim().TrimEnd('/').ToLowerInvariant();

        foreach (var entry in _activeOldLives)
        {
            if (!string.IsNullOrWhiteSpace(username)
                && string.Equals(entry.IdentityKey, "user:" + username, StringComparison.OrdinalIgnoreCase))
                return true;

            var oldHref = (entry.Href ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(href)
                && !string.IsNullOrWhiteSpace(oldHref)
                && string.Equals(href, oldHref, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    async Task<bool> TryOpenBestRecommendedViewerLiveAsync(string source, CancellationToken ct)
    {
        if (!_s.Viewer.Enabled || !_running) return false;

        try
        {
            var candidates = await _chrome.GetTikTokRecommendedLivesAsync(ct);
            if (candidates.Count == 0)
            {
                _log.Info($"[VIEWER_RECOMMENDED_SCAN] source={source} found=0 result=fallback-current-switch-flow");
                return false;
            }

            var parsed = candidates
                .Select(c => new { Candidate = c, Viewer = ViewerCountParser.Parse(c.ViewerText, _log) })
                .Where(x => x.Viewer >= 0)
                .OrderByDescending(x => x.Viewer)
                .ToList();

            foreach (var item in parsed)
            {
                _log.Info($"[VIEWER_RECOMMENDED_CANDIDATE] source={source} user={item.Candidate.Username} viewer={item.Viewer} raw={item.Candidate.ViewerText} href={item.Candidate.Href}");
            }

            var best = parsed
                .Where(x => x.Viewer > _s.Viewer.Threshold)
                .Where(x => !IsActiveOldLiveRecommendation(x.Candidate))
                .FirstOrDefault();

            if (best is null)
            {
                var oldLiveSkipped = parsed.Count(x => x.Viewer > _s.Viewer.Threshold && IsActiveOldLiveRecommendation(x.Candidate));
                _log.Info($"[VIEWER_RECOMMENDED_SCAN] source={source} found={parsed.Count} threshold={_s.Viewer.Threshold} suitable=0 oldLiveSkipped={oldLiveSkipped} result=fallback-current-switch-flow");
                return false;
            }

            SetStatus("CHỌN LIVE ĐỀ XUẤT", $"{best.Candidate.Username} • {best.Viewer} người • > {_s.Viewer.Threshold}");
            _log.Warn($"[VIEWER_RECOMMENDED_PICK] source={source} user={best.Candidate.Username} viewer={best.Viewer} threshold={_s.Viewer.Threshold} href={best.Candidate.Href} action=navigate-direct");

            await _chrome.NavigateAndWaitAsync(best.Candidate.Href, Math.Max(900, _s.Viewer.WaitAfterF5Sec * 1000), 15000, ct);
            await StopIfFatalTikTokRestrictionAsync($"sau chọn LIVE đề xuất: {source}", ct);
            if (!await WaitForLivePageReadyAsync($"sau chọn LIVE đề xuất: {source}", ct))
            {
                _log.Warn($"[VIEWER_RECOMMENDED_PAGE_NOT_READY] source={source} action=RETURN_FALSE_NO_F5");
                return false;
            }
            ResetPeriodicDue("Viewer recommended LIVE direct navigation", cancelCandidate: true);
            ResetPageMaintenanceDue("Viewer recommended LIVE direct navigation");
            ResetInputGuardConsecutive("sau chọn LIVE đề xuất");
            Volatile.Write(ref _lastViewerValue, best.Viewer);
            ResetLowViewerStreak($"đã chọn LIVE đề xuất {best.Viewer} người");

            _log.Warn($"[VIEWER_RECOMMENDED_OPENED] source={source} user={best.Candidate.Username} sidebarViewer={best.Viewer} result=allow-workflow-without-enter-then-rescan");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIEWER_RECOMMENDED_SCAN_ERROR] source={source} error={ex.Message}; fallback=logic-chuyen-live-hien-tai");
            return false;
        }
    }

    async Task<bool> FindAcceptableViewerLiveAsync(string source, int? initialLow, CancellationToken ct)
    {
        var max = Math.Max(1, _s.Viewer.MaxF5);
        if (initialLow.HasValue)
        {
            _log.Warn($"[VIEWER_GATE_LOW] source={source} value={initialLow.Value} <= threshold={_s.Viewer.Threshold}; tối đa {max} vòng ↓ + F5 để tìm LIVE đủ người.");
            RecordLowViewerLive(source + " / LIVE hiện tại", initialLow.Value);
            var resetResult = await MaybeResetLowViewerRecommendationFeedAsync(source + " / LIVE hiện tại", ct);
            if (resetResult == true) return true;
        }
        else
        {
            _log.Warn($"[VIEWER_GATE_NO_VALUE] source={source}; tối đa {max} vòng ↓ + F5 để tìm LIVE có Viewer đọc được và > ngưỡng.");
        }

        // Ưu tiên cột “Nhà sáng tạo LIVE đề xuất” trước mọi lần chuyển bằng ArrowDown.
        // Viewer của recommendation được đọc ngay trên sidebar; không cần vào LIVE rồi mới quét lại.
        if (await TryOpenBestRecommendedViewerLiveAsync(source + " / trước chuyển LIVE", ct))
            return true;

        for (int i = 1; i <= max && _running; i++)
        {
            await WaitIfPausedAsync(ct);
            SetStatus("TÌM LIVE ĐỦ NGƯỜI", $"{source} • vòng {i}/{max} • LIVE thấp {_consecutiveLowViewerLives}/{ViewerLowStreakFeedResetThreshold}");

            if (i > 1 && await TryOpenBestRecommendedViewerLiveAsync($"{source} / trước ArrowDown vòng {i}/{max}", ct))
                return true;

            var transitioned = await TransitionAsync(
                $"Viewer Gate {source} vòng {i}/{max}",
                TransitionAction.ArrowDown,
                "",
                1,
                scheduledPeriodic: false,
                ct,
                Math.Max(0, _s.Viewer.WaitAfterF5Sec * 1000));

            if (!transitioned)
            {
                ReportProblem("VIEWER_GATE_TRANSITION_FAILED", "Người xem",
                    $"{source}: vòng {i}/{max} không chuyển LIVE được. Workflow vẫn bị khóa và sẽ thử lại ở vòng chính.",
                    error: true, throttleSeconds: 15);
                await Task.Delay(ViewerGateRetryCooldownMs, ct);
                return false;
            }

            var (value, raw) = await ReadViewerWithRetryAsync($"{source} / sau chuyển {i}/{max}", ct);
            if (value < 0)
            {
                _log.Warn($"[VIEWER_GATE_AFTER_SWITCH_UNREADABLE] source={source} vòng={i}/{max}; Viewer chưa đọc được sau retry → KHÔNG mở workflow, chuyển LIVE tiếp.");
                continue;
            }

            _log.Info($"[VIEWER_GATE_AFTER_SWITCH] source={source} vòng={i}/{max} value={value} threshold={_s.Viewer.Threshold} raw={raw}");
            if (value > _s.Viewer.Threshold)
            {
                ResetLowViewerStreak($"đã tìm được LIVE đủ người ở vòng {i}/{max}");
                _log.Info($"[VIEWER_GATE_RECOVERED] source={source} vòng={i}/{max} value={value} > threshold={_s.Viewer.Threshold}; cho phép workflow tiếp tục.");
                return true;
            }

            RecordLowViewerLive($"{source} / sau chuyển {i}/{max}", value);
            var resetResult = await MaybeResetLowViewerRecommendationFeedAsync($"{source} / vòng {i}/{max}", ct);
            if (resetResult == true) return true;
        }

        ReportProblem("VIEWER_GATE_NO_SAFE_LIVE", "Người xem",
            $"{source}: chưa tìm được LIVE có người xem > {_s.Viewer.Threshold} sau {max} vòng. Không Click/Dán/Enter; vòng chính sẽ kiểm tra lại.",
            throttleSeconds: 10);
        await Task.Delay(ViewerGateRetryCooldownMs, ct);
        return false;
    }

    void RecordLowViewerLive(string source, int value)
    {
        _consecutiveLowViewerLives++;
        _log.Warn($"[VIEWER_LOW_STREAK] source={source} value={value} threshold={_s.Viewer.Threshold} streak={_consecutiveLowViewerLives}/{ViewerLowStreakFeedResetThreshold}");
    }

    void ResetLowViewerStreak(string reason)
    {
        if (_consecutiveLowViewerLives > 0)
            _log.Info($"[VIEWER_LOW_STREAK_RESET] previous={_consecutiveLowViewerLives} reason={reason}");
        _consecutiveLowViewerLives = 0;
    }

    async Task<bool?> MaybeResetLowViewerRecommendationFeedAsync(string source, CancellationToken ct)
    {
        if (_consecutiveLowViewerLives < ViewerLowStreakFeedResetThreshold)
            return null;

        var streak = _consecutiveLowViewerLives;

        // Đủ 5 LIVE thấp: quét lại sidebar trước. Chỉ hard-reset /live khi sidebar
        // không có LIVE đề xuất nào > ngưỡng (và không thuộc Live cũ active).
        SetStatus("QUÉT LẠI LIVE ĐỀ XUẤT", $"{streak} LIVE thấp liên tiếp • tìm LIVE > {_s.Viewer.Threshold} trước khi reset");
        _log.Warn($"[VIEWER_FEED_RESET_PRECHECK_RECOMMENDED] source={source} streak={streak} threshold={_s.Viewer.Threshold}");
        if (await TryOpenBestRecommendedViewerLiveAsync(source + " / trước hard-reset", ct))
            return true;

        _consecutiveLowViewerLives = 0; // Hard-reset chỉ xảy ra sau khi sidebar không có LIVE phù hợp.
        SetStatus("LÀM MỚI ĐỀ XUẤT LIVE", $"{streak} LIVE liên tiếp ≤ {_s.Viewer.Threshold} người → sidebar không phù hợp → tải lại /live");
        _log.Warn($"[VIEWER_FEED_RESET_START] source={source} streak={streak} threshold={_s.Viewer.Threshold} action=navigate:/live reason=no-suitable-sidebar-live");

        try
        {
            await _chrome.ResetTikTokLiveRecommendationFeedAsync(ct);
            await StopIfFatalTikTokRestrictionAsync($"sau reset nguồn đề xuất Viewer: {source}", ct);
            if (!await WaitForLivePageReadyAsync($"sau reset nguồn đề xuất Viewer: {source}", ct))
            {
                _log.Warn($"[VIEWER_FEED_RESET_PAGE_NOT_READY] source={source} action=RETURN_FALSE_NO_EXTRA_F5");
                return false;
            }
            ResetPeriodicDue("Viewer low-streak hard reset /live", cancelCandidate: true);
            ResetPageMaintenanceDue("Viewer low-streak hard reset /live");
            ResetInputGuardConsecutive("sau reset nguồn đề xuất Viewer");

            if (await TryOpenBestRecommendedViewerLiveAsync(source + " / sau reset /live", ct))
                return true;

            var (value, raw) = await ReadViewerWithRetryAsync(source + " / sau reset /live", ct);
            if (value < 0)
            {
                _log.Warn($"[VIEWER_FEED_RESET_DONE] source={source} result=viewer-unreadable; sẽ tiếp tục chuyển LIVE bình thường.");
                return false;
            }

            _log.Info($"[VIEWER_FEED_RESET_VIEWER] source={source} value={value} threshold={_s.Viewer.Threshold} raw={raw}");
            if (value > _s.Viewer.Threshold)
            {
                ResetLowViewerStreak("LIVE đầu tiên sau reset /live đã đủ người");
                _log.Warn($"[VIEWER_FEED_RESET_RECOVERED] source={source} value={value} > threshold={_s.Viewer.Threshold}; cho phép workflow tiếp tục.");
                return true;
            }

            // LIVE đầu tiên của nguồn đề xuất mới vẫn thấp: bắt đầu một streak mới từ 1,
            // không reset /live ngay lần nữa.
            RecordLowViewerLive(source + " / LIVE đầu tiên sau reset /live", value);
            _log.Warn($"[VIEWER_FEED_RESET_DONE] source={source} result=still-low value={value}; streak mới={_consecutiveLowViewerLives}/{ViewerLowStreakFeedResetThreshold}.");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ReportProblem("VIEWER_FEED_RESET_FAILED", "Người xem",
                $"Đã có {streak} LIVE thấp liên tiếp nhưng chưa tải lại được /live: {ex.Message}. Sẽ tiếp tục tìm LIVE và chỉ thử hard-reset lại sau 5 LIVE thấp mới.",
                error: IsLikelyCdpIssue(ex), throttleSeconds: 15);
            _log.Warn($"[VIEWER_FEED_RESET_FAILED] source={source} reason={ex.Message}");
            return false;
        }
    }

    async Task<(int value, string raw)> ReadViewerAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Viewer.XPath))
        {
            ReportProblem("VIEWER_XPATH_MISSING", "Người xem", "Chưa cấu hình XPath người xem", error: true, throttleSeconds: 30);
            Volatile.Write(ref _lastViewerValue, -1);
            return (-1, "");
        }

        try
        {
            if (!await _chrome.XPathExistsAsync(_s.Viewer.XPath, ct))
            {
                ReportProblem("VIEWER_XPATH_NOT_FOUND", "Người xem", $"Không tìm thấy XPath người xem trên trang hiện tại: {_s.Viewer.XPath}", throttleSeconds: 30);
                Volatile.Write(ref _lastViewerValue, -1);
                return (-1, "");
            }

            var text = await _chrome.GetTextAsync(_s.Viewer.XPath, ct);
            var value = ViewerCountParser.Parse(text, _log);
            Volatile.Write(ref _lastViewerValue, value >= 0 ? value : -1);
            if (value >= 0) return (value, text);

            ReportProblem("VIEWER_PARSE_FAILED", "Người xem", $"raw=\"{text}\"", throttleSeconds: 30);
            return (-1, text);
        }
        catch (Exception ex)
        {
            ReportProblem("VIEWER_READ_ERROR", "Người xem", $"Đọc XPath người xem lỗi: {ex.Message}", throttleSeconds: 20);
            Volatile.Write(ref _lastViewerValue, -1);
            return (-1, "");
        }
    }

    enum TransitionAction { ArrowDown, ClickXPath }
    sealed record LiveSwitchVerification(bool Changed, string BeforeIdentity, string AfterIdentity, int Attempt, long ElapsedMs, bool PageRecovered = false);

    static string TrimIdentityForLog(string identity, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "(empty)";
        identity = identity.Replace("\r", " ").Replace("\n", " ").Trim();
        return identity.Length <= max ? identity : identity[..max] + "...";
    }

    async Task<string> GetCurrentLiveIdentityAsync(CancellationToken ct)
    {
        var identity = await _chrome.GetCurrentLiveIdentityAsync(ct);
        return string.IsNullOrWhiteSpace(identity) ? "(unknown)" : identity;
    }

    static bool HasReliableLiveIdentity(string identity)
        => !string.IsNullOrWhiteSpace(identity)
        && !identity.Equals("(unknown)", StringComparison.Ordinal)
        && (identity.Contains("roomId=", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("broadcaster=", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("/live/", StringComparison.OrdinalIgnoreCase));

    async Task<LiveSwitchVerification> WaitForLiveChangedAsync(string source, string beforeIdentity, int attempt, int maxAttempts, CancellationToken ct)
    {
        _log.Info($"[LIVE_VERIFY_WAIT] source={source} attempt={attempt}/{maxAttempts} timeoutMs={LiveVerifyTimeoutMs} intervalMs={LiveVerifyPollMs}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string latestIdentity = beforeIdentity;
        while (sw.ElapsedMilliseconds < LiveVerifyTimeoutMs)
        {
            await Task.Delay(LiveVerifyPollMs, ct);
            latestIdentity = await GetCurrentLiveIdentityAsync(ct);
            if (!string.Equals(latestIdentity, beforeIdentity, StringComparison.Ordinal))
                return new LiveSwitchVerification(true, beforeIdentity, latestIdentity, attempt, sw.ElapsedMilliseconds);
        }
        return new LiveSwitchVerification(false, beforeIdentity, latestIdentity, attempt, sw.ElapsedMilliseconds);
    }

    static string ExtractLiveIdentityField(string identity, string field)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "";
        var marker = field + "=";
        var start = identity.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += marker.Length;
        var end = identity.IndexOf(" | ", start, StringComparison.Ordinal);
        if (end < 0) end = identity.Length;
        return identity[start..end].Trim();
    }

    static string GetLivePageChangeKey(string identity)
    {
        // Chỉ dùng roomId/URL để biết TikTok đã bắt đầu sang LIVE khác.
        // Không dùng title/broadcaster/text DOM vì các phần đó có thể đổi khi cùng một LIVE đang render.
        var roomId = ExtractLiveIdentityField(identity, "roomId");
        if (!string.IsNullOrWhiteSpace(roomId)) return "roomId=" + roomId;

        var href = ExtractLiveIdentityField(identity, "href");
        if (!string.IsNullOrWhiteSpace(href)) return "href=" + href;

        var canonical = ExtractLiveIdentityField(identity, "canonical");
        if (!string.IsNullOrWhiteSpace(canonical)) return "canonical=" + canonical;

        return "";
    }

    async Task<LiveSwitchVerification> WaitForPageChangeBeforeReloadAsync(string source, string beforeIdentity, int maxWaitMs, int attempt, CancellationToken ct)
    {
        maxWaitMs = Math.Max(LiveFastChangePollMs, maxWaitMs);
        var beforeKey = GetLivePageChangeKey(beforeIdentity);
        var beforeTitle = ExtractLiveIdentityField(beforeIdentity, "title");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var latestIdentity = beforeIdentity;

        _log.Info($"[LIVE_SWITCH_WAIT_PAGE_CHANGE] source={source} attempt={attempt} maxWaitMs={maxWaitMs} pollMs={LiveFastChangePollMs} beforeKey={beforeKey} beforeTitle={TrimIdentityForLog(beforeTitle, 100)}");

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            await Task.Delay(LiveFastChangePollMs, ct);
            latestIdentity = await GetCurrentLiveIdentityAsync(ct);
            var latestKey = GetLivePageChangeKey(latestIdentity);
            var latestTitle = ExtractLiveIdentityField(latestIdentity, "title");

            var stableKeyChanged = !string.IsNullOrWhiteSpace(beforeKey)
                && !string.IsNullOrWhiteSpace(latestKey)
                && !string.Equals(beforeKey, latestKey, StringComparison.OrdinalIgnoreCase);
            var titleChanged = !string.IsNullOrWhiteSpace(beforeTitle)
                && !string.IsNullOrWhiteSpace(latestTitle)
                && !string.Equals(beforeTitle, latestTitle, StringComparison.Ordinal);

            if (stableKeyChanged || titleChanged)
            {
                var trigger = stableKeyChanged ? "roomId/url" : "title";
                _log.Info($"[LIVE_SWITCH_PAGE_CHANGED] source={source} attempt={attempt} elapsedMs={sw.ElapsedMilliseconds} trigger={trigger} beforeKey={beforeKey} afterKey={latestKey} action=F5_NGAY");
                return new LiveSwitchVerification(true, beforeIdentity, latestIdentity, attempt, sw.ElapsedMilliseconds);
            }
        }

        _log.Warn($"[LIVE_SWITCH_PAGE_CHANGE_TIMEOUT] source={source} attempt={attempt} waitedMs={sw.ElapsedMilliseconds} beforeKey={beforeKey} latestKey={GetLivePageChangeKey(latestIdentity)} action=CHUA_F5");
        return new LiveSwitchVerification(false, beforeIdentity, latestIdentity, attempt, sw.ElapsedMilliseconds);
    }

    async Task<LiveSwitchVerification> ReloadAfterConfirmedArrowDownAsync(string source, LiveSwitchVerification changed, int waitAfterReloadMs, CancellationToken ct)
    {
        await _chrome.ReloadAndWaitAsync(Math.Max(0, waitAfterReloadMs), 15000, ct);
        _log.Info($"[LIVE_SWITCH_DOM_READY] source={source} preReloadChanged=True attempt={changed.Attempt}");
        await StopIfFatalTikTokRestrictionAsync($"sau chuyển LIVE: {source}", ct);
        var afterIdentity = await GetCurrentLiveIdentityAsync(ct);
        _log.Info($"[LIVE_SWITCH_CONFIRMED] source={source} attempt={changed.Attempt} before={TrimIdentityForLog(changed.BeforeIdentity)} after={TrimIdentityForLog(afterIdentity)}");
        return new LiveSwitchVerification(true, changed.BeforeIdentity, afterIdentity, changed.Attempt, changed.ElapsedMs);
    }

    async Task<bool> TryRecoverRendererCrashAfterArrowDownNoChangeAsync(string source, int resumeStep, CancellationToken ct)
    {
        if (_pageRecoveryExecuting) return false;

        ChromeController.PageHealthSnapshot health;
        try
        {
            _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_CHECK] source={source} reason=ArrowDown vẫn không đổi sau CDP reconnect");
            health = await _chrome.ProbePageHealthAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_CHECK_FAILED] source={source} reason={ex.Message}");
            return false;
        }

        if (!health.CrashLike)
        {
            _log.Info($"[LIVE_SWITCH_PAGE_CRASH_NOT_FOUND] source={source} health={health.Reason}");
            return false;
        }

        _pageRecoveryExecuting = true;
        var fallback = !string.IsNullOrWhiteSpace(_lastHealthyTikTokUrl)
            ? _lastHealthyTikTokUrl
            : health.Url;
        _step = resumeStep;
        SetStatus("ĐANG CỨU TRANG", $"{source}: phát hiện {health.Reason} → reload/restart Chrome nếu cần.");
        _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_DETECTED] source={source} reason={health.Reason} url={health.Url} resumeStep={resumeStep}");

        try
        {
            var needRestart = false;
            try
            {
                _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RELOAD_START] source={source} fallback={fallback}");
                await _chrome.RecoverCurrentPageAsync(fallback, ct);

                var afterReload = await _chrome.ProbePageHealthAsync(ct);
                if (!afterReload.Healthy || afterReload.CrashLike)
                {
                    needRestart = true;
                    _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RELOAD_STILL_BAD] source={source} health={afterReload.Reason} url={afterReload.Url} action=restart-managed-chrome");
                }
                else
                {
                    _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RELOAD_OK] source={source} health={afterReload.Reason}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                needRestart = true;
                _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RELOAD_FAILED] source={source} reason={ex.Message} action=restart-managed-chrome");
            }

            if (needRestart)
            {
                await _chrome.RestartManagedChromeForRecoveryAsync(fallback, ct);
                var afterRestart = await _chrome.ProbePageHealthAsync(ct);
                if (!afterRestart.Healthy || afterRestart.CrashLike)
                    throw new InvalidOperationException($"Chrome đã restart nhưng trang vẫn chưa khỏe: {afterRestart.Reason}");

                _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RESTART_OK] source={source} health={afterRestart.Reason}");
            }

            _step = resumeStep;
            ResetPageMaintenanceDue("sau tự cứu OOM trong ArrowDown");
            _nextPageHealthProbe = DateTime.Now.AddMilliseconds(PageHealthProbeIntervalMs);
            ResetInputGuardConsecutive("sau tự cứu OOM trong ArrowDown");
            ResetRecoveryFailures("OOM trong ArrowDown đã tự phục hồi");
            SetStatus("ĐÃ CỨU TRANG", $"Chrome đã phục hồi; tiếp tục lại bước {resumeStep}/8.");
            _log.Warn($"[LIVE_SWITCH_PAGE_CRASH_RECOVERY_DONE] source={source} resumeStep={resumeStep} action=resume-same-step");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ReportProblem("LIVE_SWITCH_PAGE_CRASH_RECOVERY_FAILED", source,
                "Đã phát hiện renderer/Out of Memory nhưng reload và restart Chrome vẫn chưa phục hồi được: " + ex.Message,
                error: true, throttleSeconds: 15);
            _log.Error($"[LIVE_SWITCH_PAGE_CRASH_RECOVERY_FAILED] source={source} {ex}");
            return false;
        }
        finally
        {
            _pageRecoveryExecuting = false;
        }
    }

    async Task<LiveSwitchVerification> TryArrowDownSwitchAsync(string source, int count, int waitAfterReloadMs, CancellationToken ct)
    {
        var resumeStep = _step;
        var originalBefore = await GetCurrentLiveIdentityAsync(ct);
        _log.Info($"[LIVE_VERIFY_BEFORE] source={source} key={GetLivePageChangeKey(originalBefore)} identity={TrimIdentityForLog(originalBefore)} requestedCount={Math.Clamp(count, 1, 4)}");

        // Không bắn ArrowDown x2/x3 liên tiếp nữa. Mỗi lần chỉ gửi 1 phím rồi quan sát.
        // Nếu lần đầu không tác dụng, reconnect CDP + focus lại trang, mô phỏng đúng hiện tượng
        // người dùng dừng/chạy lại tool thì phím xuống hoạt động trở lại.
        for (int attempt = 1; attempt <= ArrowDownRecoveryAttempts; attempt++)
        {
            if (attempt == 2)
            {
                _log.Warn($"[LIVE_SWITCH_CDP_RECOVERY_START] source={source} reason=ArrowDown lần 1 không đổi LIVE; reconnect target hiện tại trước retry.");
                await _chrome.ReconnectAsync(ct);
                _log.Warn($"[LIVE_SWITCH_CDP_RECOVERY_OK] source={source} action=reconnect+refresh-target");
            }

            var beforeAttempt = await GetCurrentLiveIdentityAsync(ct);
            var recoveryMode = attempt >= 2;
            await _chrome.PressArrowDownNavigationAsync(1, 0, recoveryMode, ct);
            _log.Info($"[LIVE_KEY_SENT] source={source} attempt={attempt}/{ArrowDownRecoveryAttempts} key=ArrowDown mode={(recoveryMode ? "recovery-keyDown" : "normal-rawKeyDown")}");

            var changed = await WaitForPageChangeBeforeReloadAsync(source, beforeAttempt, ArrowDownAttemptWaitMs, attempt, ct);
            if (changed.Changed)
                return await ReloadAfterConfirmedArrowDownAsync(source, changed, waitAfterReloadMs, ct);

            // Tầng tự cứu nặng hơn: sau khi đã reconnect CDP mà ArrowDown vẫn không làm LIVE đổi,
            // kiểm tra renderer crash/OOM ngay thay vì tiếp tục kẹt trong chuỗi timeout.
            if (attempt >= 2 && await TryRecoverRendererCrashAfterArrowDownNoChangeAsync(source, resumeStep, ct))
            {
                return new LiveSwitchVerification(false, originalBefore, beforeAttempt, attempt, changed.ElapsedMs, PageRecovered: true);
            }

            if (attempt < ArrowDownRecoveryAttempts)
            {
                _log.Warn($"[LIVE_SWITCH_ARROW_RETRY] source={source} attempt={attempt}/{ArrowDownRecoveryAttempts} result=no-change waitMs={ArrowDownRetryDelayMs}");
                await Task.Delay(ArrowDownRetryDelayMs, ct);
            }
        }

        // Ba lần phím CDP đều không làm trang đổi. Trước F5 reset bàn phím, kiểm tra crash thêm
        // một lần để không dùng Page.reload kiểu reset trên renderer đã Out of Memory.
        if (await TryRecoverRendererCrashAfterArrowDownNoChangeAsync(source + " / trước F5 reset", resumeStep, ct))
            return new LiveSwitchVerification(false, originalBefore, originalBefore, ArrowDownRecoveryAttempts, 0, PageRecovered: true);

        // Không phải crash/OOM: giữ nguyên logic cũ, F5 đúng một lần để reset document/keyboard handler.
        _log.Warn($"[LIVE_SWITCH_KEY_STALE_RESET] source={source} ArrowDown không tác dụng sau {ArrowDownRecoveryAttempts} lần; F5 một lần để reset trang rồi thử lại.");
        await _chrome.ReloadAndWaitAsync(ArrowDownResetReloadWaitMs, 15000, ct);
        await StopIfFatalTikTokRestrictionAsync($"sau reset phím LIVE: {source}", ct);
        await _chrome.ReconnectAsync(ct);
        _log.Warn($"[LIVE_SWITCH_KEY_STALE_RECONNECTED] source={source} target đã refresh sau F5 reset.");

        var beforeFinal = await GetCurrentLiveIdentityAsync(ct);
        await _chrome.PressArrowDownNavigationAsync(1, 0, recoveryMode: true, ct);
        _log.Info($"[LIVE_KEY_SENT] source={source} attempt=post-reset key=ArrowDown mode=recovery-keyDown");
        var finalChanged = await WaitForPageChangeBeforeReloadAsync(source, beforeFinal, ArrowDownPostResetAttemptWaitMs, ArrowDownRecoveryAttempts + 1, ct);
        if (finalChanged.Changed)
            return await ReloadAfterConfirmedArrowDownAsync(source, finalChanged, waitAfterReloadMs, ct);

        ReportProblem("LIVE_SWITCH_KEY_UNRESPONSIVE", source,
            "ArrowDown CDP vẫn không làm LIVE đổi sau reconnect + focus + F5 reset. Không F5 lặp cùng LIVE; Viewer/InputGuard vẫn khóa workflow và vòng sau sẽ thử lại.",
            throttleSeconds: 10);
        return new LiveSwitchVerification(false, originalBefore, finalChanged.AfterIdentity, ArrowDownRecoveryAttempts + 1, finalChanged.ElapsedMs);
    }

    async Task<LiveSwitchVerification> TryClickSwitchAsync(string source, string xpath, int count, CancellationToken ct)
    {
        string beforeIdentity = await GetCurrentLiveIdentityAsync(ct);
        _log.Info($"[LIVE_VERIFY_BEFORE] source={source} identity={TrimIdentityForLog(beforeIdentity)}");

        if (!await ClickLiveSwitchAsync(source, xpath, Math.Clamp(count, 1, 4), ct))
            return new LiveSwitchVerification(false, beforeIdentity, beforeIdentity, 1, 0);

        _log.Info($"[LIVE_KEY_SENT] source={source} attempt=1/1 action=ClickXPath count={Math.Clamp(count, 1, 4)}");
        var verify = await WaitForLiveChangedAsync(source, beforeIdentity, 1, 1, ct);
        if (verify.Changed)
        {
            _log.Info($"[LIVE_SWITCH_CONFIRMED] source={source} attempt=1/1 before={TrimIdentityForLog(verify.BeforeIdentity)} after={TrimIdentityForLog(verify.AfterIdentity)} elapsed={verify.ElapsedMs}ms");
            return verify;
        }

        _log.Warn($"[LIVE_SWITCH_NOT_CHANGED] source={source} attempt=1/1 before={TrimIdentityForLog(beforeIdentity)} after={TrimIdentityForLog(verify.AfterIdentity)} elapsed={verify.ElapsedMs}ms");
        return verify;
    }

    async Task<bool> ClickLiveSwitchAsync(string source, string xpath, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            ReportProblem("XPATH_ACTION_MISSING", source, "XPath nút chuyển live đang trống. Bỏ qua vòng chuyển live; không dùng tọa độ fallback.", error: true, throttleSeconds: 30);
            return false;
        }

        if (await _chrome.XPathExistsAsync(xpath, ct))
        {
            try
            {
                await _chrome.ClickXPathDomSmartAsync(xpath, count, MultiActionGapMs, ct);
                return true;
            }
            catch (Exception ex)
            {
                ReportProblem("LIVE_SWITCH_CLICK_FAILED", source, $"Đã tìm thấy XPath nút chuyển live nhưng click clickable ancestor thất bại. XPath={xpath}; chi tiết: {ex.Message}", error: true, throttleSeconds: 15);
                return false;
            }
        }

        if (!_s.SwitchNeedsHover)
        {
            ReportProblem("LIVE_SWITCH_NOT_FOUND", source, $"Không tìm thấy XPath nút chuyển live: {xpath}. Chế độ hover đang tắt nên bỏ qua vòng chuyển live.", error: true, throttleSeconds: 15);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_s.XPathHoverArea))
        {
            ReportProblem("HOVER_TARGET_MISSING", source, "Đã bật nút cần hover nhưng XPath vùng hover đang trống. Bỏ qua vòng chuyển live.", error: true, throttleSeconds: 30);
            return false;
        }
        if (!await _chrome.XPathExistsAsync(_s.XPathHoverArea, ct))
        {
            ReportProblem("HOVER_TARGET_NOT_FOUND", source, $"Không tìm thấy XPath vùng hover LIVE: {_s.XPathHoverArea}. Bỏ qua vòng chuyển live.", error: true, throttleSeconds: 15);
            return false;
        }

        int beforeControls = 0;
        try { beforeControls = await _chrome.CountVisibleInteractiveOverXPathAsync(_s.XPathHoverArea, ct); } catch { }

        try
        {
            SetStatus("ĐANG HIỆN NÚT LIVE", "Hover ảo ngoài → giữa LIVE → dịch nhẹ, chờ control TikTok xuất hiện.");
            await _chrome.HoverXPathAsync(_s.XPathHoverArea, ct);
            await Task.Delay(Math.Clamp(_s.HoverDelayMs, 0, 3000), ct);
        }
        catch (Exception ex)
        {
            ReportProblem("HOVER_TARGET_FAILED", source, $"Tìm thấy vùng hover nhưng không kích hoạt được hover ảo. XPath={_s.XPathHoverArea}; chi tiết: {ex.Message}", error: true, throttleSeconds: 15);
            return false;
        }

        var deadline = Environment.TickCount64 + 2500;
        bool found = false;
        do
        {
            if (await _chrome.XPathExistsAsync(xpath, ct)) { found = true; break; }
            await Task.Delay(100, ct);
        } while (Environment.TickCount64 < deadline);

        if (!found)
        {
            int afterControls = beforeControls;
            try { afterControls = await _chrome.CountVisibleInteractiveOverXPathAsync(_s.XPathHoverArea, ct); } catch { }
            if (afterControls <= beforeControls)
            {
                ReportProblem("HOVER_CONTROL_NOT_SHOWN", source, $"Đã hover ảo vào vùng LIVE nhưng không thấy control tương tác mới xuất hiện sau 2.5 giây. XPath hover={_s.XPathHoverArea}. Hãy dùng nút ‘Thử hover’ để kiểm tra vùng hover.", error: true, throttleSeconds: 15);
            }
            else
            {
                ReportProblem("LIVE_SWITCH_NOT_FOUND", source, $"Control LIVE đã thay đổi sau hover nhưng vẫn không tìm thấy XPath nút chuyển live: {xpath}. Hãy lấy lại XPath nút; picker V13 sẽ tự chọn clickable ancestor thay vì SVG.", error: true, throttleSeconds: 15);
            }
            return false;
        }

        try
        {
            await _chrome.ClickXPathDomSmartAsync(xpath, count, MultiActionGapMs, ct);
            return true;
        }
        catch (Exception ex)
        {
            ReportProblem("LIVE_SWITCH_CLICK_FAILED", source, $"Nút chuyển live đã xuất hiện nhưng click clickable ancestor thất bại. XPath={xpath}; chi tiết: {ex.Message}", error: true, throttleSeconds: 15);
            return false;
        }
    }

    async Task<bool> TransitionAsync(string source, TransitionAction action, string xpath, int count, bool scheduledPeriodic,
        CancellationToken ct, int waitAfterReloadMs = F5WaitMs)
    {
        if (_transitioning)
        {
            ReportProblem("TRANSITION_LOCKED", source, "Đang có một vòng chuyển live/recovery khác.", throttleSeconds: 5);
            return false;
        }
        _transitioning = true;
        bool completed = false;
        SetStatus("ĐANG CHUYỂN LIVE", source);
        _log.Info($"BẮT ĐẦU KHÓA CHUYỂN LIVE: {source}");
        try
        {
            LiveSwitchVerification verify = await ExecuteTransitionAttemptAsync(source, action, xpath, count, waitAfterReloadMs, ct);
            if (!verify.Changed)
            {
                if (verify.PageRecovered)
                {
                    _log.Warn($"[LIVE_SWITCH_RECOVERY_RESUME] source={source} pageRecovered=true step={_step} action=return-to-main-loop");
                    SetStatus("ĐÃ CỨU TRANG", $"{source}: Chrome đã phục hồi; quay lại đúng bước {_step}/8 để kiểm tra lại.");
                    return false;
                }

                ReportProblem("LIVE_SWITCH_FAILED", source, "Đã retry chuyển LIVE nhưng chưa xác nhận LIVE mới; đây là lỗi recoverable, sẽ bỏ qua/retry ở vòng kế tiếp.", throttleSeconds: 10);
                return false;
            }

            if (!await WaitForLivePageReadyAsync($"sau chuyển LIVE: {source}", ct))
            {
                _log.Warn($"[LIVE_SWITCH_PAGE_NOT_READY] source={source} changed=true action=RETURN_TO_MAIN_LOOP_NO_EXTRA_F5");
                return false;
            }

            ResetPeriodicDue(source + " da xac nhan sang LIVE moi va F5 xong", cancelCandidate: !scheduledPeriodic);
            ResetPageMaintenanceDue(source + " vừa F5 sau chuyển LIVE");
            completed = true;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var cdpSessionLost = IsLikelyCdpIssue(ex);
            ReportProblem("TRANSITION_FAILED", source, ex.Message, error: cdpSessionLost, throttleSeconds: 10);
            if (cdpSessionLost && !await EnsureCdpRecoveredAsync($"transition/{source}", ct)) return false;
            if (!cdpSessionLost) await Task.Delay(ArrowDownRetryDelayMs, ct);

            try
            {
                if (cdpSessionLost) await _chrome.BringToFrontAsync(ct);
                LiveSwitchVerification verify = await ExecuteTransitionAttemptAsync(source + " retry", action, xpath, count, waitAfterReloadMs, ct);
                if (!verify.Changed)
                {
                    if (verify.PageRecovered)
                    {
                        _log.Warn($"[LIVE_SWITCH_RECOVERY_RESUME] source={source} pageRecovered=true step={_step} action=return-to-main-loop-after-retry");
                        SetStatus("ĐÃ CỨU TRANG", $"{source}: Chrome đã phục hồi; quay lại đúng bước {_step}/8 để kiểm tra lại.");
                        return false;
                    }

                    ReportProblem("LIVE_SWITCH_FAILED", source, "Đã retry chuyển LIVE nhưng chưa xác nhận LIVE mới; sẽ tiếp tục cơ chế bỏ qua LIVE lỗi.", throttleSeconds: 10);
                    return false;
                }

                if (!await WaitForLivePageReadyAsync($"sau retry chuyển LIVE: {source}", ct))
                {
                    _log.Warn($"[LIVE_SWITCH_PAGE_NOT_READY] source={source} retry=true changed=true action=RETURN_TO_MAIN_LOOP_NO_EXTRA_F5");
                    return false;
                }

                _log.Warn($"[RECOVERY_OK] transition={source} action=retry-after-cdp-reconnect");
                ResetPeriodicDue(source + " da xac nhan sang LIVE moi sau retry reconnect", cancelCandidate: !scheduledPeriodic);
                ResetPageMaintenanceDue(source + " vừa F5 sau retry chuyển LIVE");
                completed = true;
                return true;
            }
            catch (Exception retryEx)
            {
                ReportProblem("TRANSITION_RETRY_FAILED", source, retryEx.Message, error: true, throttleSeconds: 10);
                return false;
            }
        }
        finally
        {
            _transitioning = false;
            _log.Info($"MỞ KHÓA CHUYỂN LIVE: {source}");
            // Nếu vòng chuyển thất bại vì XPath, giữ nguyên trạng thái LỖI/CẢNH BÁO
            // do ReportProblem vừa hiển thị thay vì ghi đè bằng “hoàn tất”.
            if (completed) SetStatus("ĐANG CHẠY", source + " hoàn tất.");
        }
    }

    async Task<LiveSwitchVerification> ExecuteTransitionAttemptAsync(string source, TransitionAction action, string xpath, int count,
        int waitAfterReloadMs, CancellationToken ct)
    {
        if (action == TransitionAction.ArrowDown)
            return await TryArrowDownSwitchAsync(source, count, waitAfterReloadMs, ct);

        var verify = await TryClickSwitchAsync(source, xpath, count, ct);
        if (!verify.Changed) return verify;

        await _chrome.ReloadAndWaitAsync(Math.Max(0, waitAfterReloadMs), 15000, ct);
        _log.Info($"[LIVE_SWITCH_DOM_READY] source={source} action=ClickXPath");
        await StopIfFatalTikTokRestrictionAsync($"sau chuyển LIVE: {source}", ct);
        return verify;
    }


}
