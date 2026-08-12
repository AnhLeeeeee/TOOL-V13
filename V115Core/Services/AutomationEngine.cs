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
    const int LiveVerifyTimeoutMs = 5000;
    const int ArrowDownSettleBeforeReloadMs = 2000;
    const int ArrowDownRetryAttempts = 2;
    const int ArrowDownRetryDelayMs = 750;
    const int PriorityPauseMs = 5000;
    const int OldLiveScanIntervalMs = 1500;
    const int OldLiveScanRetryMs = 2500;
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
    System.Diagnostics.Stopwatch? _loopPerf;
    long _loopPerfTotalMs;
    long _loopPerfCount;

    DateTime _periodicDue = DateTime.MaxValue;
    DateTime _candidateCaptureAt = DateTime.MaxValue;
    readonly List<OldLiveEntry> _activeOldLives = [];
    bool _oldLiveManifestLoaded;
    DateTime _nextOldLiveScan = DateTime.MaxValue;
    DateTime _nextViewer = DateTime.MaxValue;
    DateTime _stopAt = DateTime.MaxValue;
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
            throw new InvalidOperationException("V13.4.1 Live cũ đang bật nhưng XPath tài khoản LIVE đang trống.");

        _s = settings;
        _contents = contents;
        _contentIndex = 0;
        _step = 1;
        _rounds = 0;
        _loopPerf = System.Diagnostics.Stopwatch.StartNew();
        _loopPerfTotalMs = 0;
        _loopPerfCount = 0;
        _paused = false;
        _running = true;
        _transitioning = false;
        ResetInputGuardConsecutive("khởi động");

        var now = DateTime.Now;
        // Khi vừa bắt đầu tool, nếu bật kiểm tra người xem thì đọc XPath ngay ở vòng đầu tiên
        // để có thể đi thẳng vào chuỗi ↓ + F5 hiện có trước khi gửi nội dung.
        _nextViewer = _s.Viewer.Enabled ? now : DateTime.MaxValue;
        _stopAt = _s.TimerStopMinutes > 0 ? now.AddMinutes(_s.TimerStopMinutes) : DateTime.MaxValue;
        EnsureOldLivesReadyForRun();
        // Runtime không còn lập lịch quét ảnh vùng lỗi/STOP/ban acc.

        ResetPeriodicDue("khởi động", cancelCandidate: true);
        SyncPeriodicSnapshot();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info("V13.4.1 bắt đầu. InputGuard và Live cũ đều đọc trạng thái trực tiếp bằng DOM/XPath; flow click/phím/F5/chuyển LIVE giữ từ V12.5.");
        SetStatus("ĐANG CHẠY", "V13.4.1 XPath-only + DOM Input Guard đã bắt đầu.");
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

                    // V13 không còn quét ảnh ưu tiên/STOP runtime. Các timer còn lại vẫn chỉ
                    // xử lý ở ranh giới giữa các bước, không chen giữa click/dán/Enter.
                    if (await HandleOldLiveExpiryAndScanAsync(ct)) continue;
                    if (await HandlePeriodicCaptureAndF5Async(ct)) continue;
                    if (await HandleViewerDueAsync(ct)) continue;

                    // V13: InputGuard DOM chạy ngay trước hai bước Click (1 và 5).
                    // Không screenshot/image-match vùng lỗi trong runtime chính.
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

    string CurrentInputXPath => _step <= 4 ? _s.XPathPoint1 : _s.XPathPoint2;
    int CurrentRestartStep => _step <= 4 ? 1 : 5;
    string CurrentPointName => _step <= 4 ? "điểm 1" : "điểm 2";

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
                    SetStatus("BƯỚC 1/8", $"Kiểm tra ô nhập → Click ô 1 • nội dung {_contentIndex + 1}/{_contents.Count}");
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
                    SetStatus("BƯỚC 3/8", "Enter ô 1 • chờ TikTok phản hồi");
                    await _chrome.PressKeyAsync("Enter", ct: ct);
                    _step = 4;
                    // Vẫn giữ đúng khoảng chờ phản hồi cũ; chỉ dời full-scan sang ngay
                    // trước Click kế tiếp để không thay đổi nhịp nghiệp vụ.
                    await Task.Delay(EnterReactionScanMs, ct);
                    break;
                }
                case 4:
                    SetStatus("BƯỚC 4/8", "Hoàn tất điểm 1 • chuyển sang điểm 2");
                    _step = 5;
                    break;

                case 5:
                {
                    SetStatus("BƯỚC 5/8", $"Kiểm tra ô nhập → Click ô 2 • nội dung {_contentIndex + 1}/{_contents.Count}");
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
                    SetStatus("BƯỚC 7/8", "Enter ô 2 • chờ TikTok phản hồi");
                    await _chrome.PressKeyAsync("Enter", ct: ct);
                    _step = 8;
                    await Task.Delay(EnterReactionScanMs, ct);
                    break;
                }
                case 8:
                {
                    SetStatus("BƯỚC 8/8", "Hoàn tất vòng • chuẩn bị nội dung tiếp theo");
                    _rounds++;
                    var used = _contentIndex + 1;
                    _contentIndex = (_contentIndex + 1) % _contents.Count;
                    _step = 1;
                    SetStatus("ĐANG CHẠY", $"Hoàn tất vòng {_rounds} với nội dung {used}/{_contents.Count}. Tiếp theo {_contentIndex + 1}/{_contents.Count}.");
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
        if (delta <= TimeSpan.Zero || _s.PeriodicF5Minutes <= 0) return;
        if (_periodicDue != DateTime.MaxValue) _periodicDue += delta;
        if (_candidateCaptureAt != DateTime.MaxValue) _candidateCaptureAt += delta;
        SyncPeriodicSnapshot();
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

        if (_s.Viewer.Enabled) _nextViewer = DateTime.Now;
        else await Task.Delay(ActionDelay(), ct);
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
            if (_s.Viewer.Enabled) _nextViewer = DateTime.Now;
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

    async Task<bool> HandleViewerDueAsync(CancellationToken ct)
    {
        if (!_s.Viewer.Enabled || DateTime.Now < _nextViewer) return false;
        await RunViewerCheckNowAsync(ct, _rounds == 0 && _step == 1 ? "kiểm tra lúc khởi động" : "kiểm tra định kỳ");
        return true;
    }

    async Task RunViewerCheckNowAsync(CancellationToken ct, string source)
    {
        if (!_s.Viewer.Enabled) return;
        SetStatus("ĐỌC NGƯỜI XEM", $"{source} • đang đọc XPath người xem");
        var (value, raw) = await ReadViewerAsync(ct);
        if (value < 0)
        {
            _log.Warn($"Không đọc được người xem từ XPath ({source}); bỏ qua lần kiểm tra này.");
            ScheduleNextViewer();
            return;
        }

        _log.Info($"Kiểm tra người xem ({source}): {value}; ngưỡng={_s.Viewer.Threshold}; raw={raw}");
        if (value > _s.Viewer.Threshold)
        {
            ScheduleNextViewer();
            return;
        }

        // V10: kết quả thấp đầu tiên được xác nhận ngay trong cùng một lượt, cách nhau 2 giây.
        int lowCount = 1;
        while (lowCount < Math.Max(1, _s.Viewer.ConfirmLow))
        {
            await Task.Delay(2000, ct);
            await WaitIfPausedAsync(ct);
            var (confirm, rawConfirm) = await ReadViewerAsync(ct);
            if (confirm < 0)
            {
                _log.Warn("Không đọc được khi xác nhận số người xem thấp; bỏ qua lần này.");
                ScheduleNextViewer();
                return;
            }
            lowCount++;
            _log.Info($"Xác nhận thấp {lowCount}/{_s.Viewer.ConfirmLow}: {confirm}; raw={rawConfirm}");
            if (confirm > _s.Viewer.Threshold)
            {
                ScheduleNextViewer();
                return;
            }
            value = confirm;
        }

        await HandleLowViewerLoopAsync(value, ct);
        if (_running) ScheduleNextViewer();
    }

    void ScheduleNextViewer() => _nextViewer = _s.Viewer.Enabled
        ? DateTime.Now.AddSeconds(Math.Max(1, _s.Viewer.IntervalSec))
        : DateTime.MaxValue;

    async Task HandleLowViewerLoopAsync(int initial, CancellationToken ct)
    {
        int max = Math.Max(1, _s.Viewer.MaxF5);
        _log.Warn($"Bắt đầu xử lý người xem thấp {initial} ≤ {_s.Viewer.Threshold}; tối đa {max} vòng ↓ + F5.");

        for (int i = 1; i <= max && _running; i++)
        {
            await WaitIfPausedAsync(ct);
            if (!await TransitionAsync($"người xem thấp vòng {i}/{max}", TransitionAction.ArrowDown, "", 1, scheduledPeriodic: false, ct,
                Math.Max(0, _s.Viewer.WaitAfterF5Sec * 1000)))
            {
                ReportProblem("VIEWER_TRANSITION_FAILED", "Người xem thấp", $"Vòng {i}/{max} không thực hiện được thao tác chuyển live; đã dừng riêng chuỗi xử lý người xem.", error: true, throttleSeconds: 15);
                return;
            }

            var (value, raw) = await ReadViewerAsync(ct);
            if (value < 0)
            {
                _log.Warn("Sau F5 vẫn không đọc được người xem từ XPath; kết thúc riêng phần kiểm tra người xem và tiếp tục vòng chính.");
                return;
            }
            _log.Info($"Sau F5 vòng {i}/{max}: {value} người xem; raw={raw}");
            if (value > _s.Viewer.Threshold) return;
        }

        await SkipCurrentLiveAsync("VIEWER_LOW_PERSISTENT", "Người xem thấp", $"Số người xem vẫn không vượt {_s.Viewer.Threshold} sau {max} vòng ↓ + F5.", ct);
    }

    async Task<(int value, string raw)> ReadViewerAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Viewer.XPath))
        {
            ReportProblem("VIEWER_XPATH_MISSING", "Người xem", "Chưa cấu hình XPath người xem", error: true, throttleSeconds: 30);
            return (-1, "");
        }

        try
        {
            if (!await _chrome.XPathExistsAsync(_s.Viewer.XPath, ct))
            {
                ReportProblem("VIEWER_XPATH_NOT_FOUND", "Người xem", $"Không tìm thấy XPath người xem trên trang hiện tại: {_s.Viewer.XPath}", throttleSeconds: 30);
                return (-1, "");
            }

            var text = await _chrome.GetTextAsync(_s.Viewer.XPath, ct);
            var value = ViewerCountParser.Parse(text, _log);
            if (value >= 0) return (value, text);

            ReportProblem("VIEWER_PARSE_FAILED", "Người xem", $"raw=\"{text}\"", throttleSeconds: 30);
            return (-1, text);
        }
        catch (Exception ex)
        {
            ReportProblem("VIEWER_READ_ERROR", "Người xem", $"Đọc XPath người xem lỗi: {ex.Message}", throttleSeconds: 20);
            return (-1, "");
        }
    }

    enum TransitionAction { ArrowDown, ClickXPath }
    sealed record LiveSwitchVerification(bool Changed, string BeforeIdentity, string AfterIdentity, int Attempt, long ElapsedMs);

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

    async Task<LiveSwitchVerification> TryArrowDownSwitchAsync(string source, int count, int waitAfterReloadMs, CancellationToken ct)
    {
        string beforeIdentity = await GetCurrentLiveIdentityAsync(ct);
        _log.Info($"[LIVE_VERIFY_BEFORE] source={source} identity={TrimIdentityForLog(beforeIdentity)}");
        var canVerifyIdentity = HasReliableLiveIdentity(beforeIdentity);

        for (int attempt = 1; attempt <= ArrowDownRetryAttempts; attempt++)
        {
            await _chrome.PressKeyAsync("ArrowDown", Math.Clamp(count, 1, 4), MultiActionGapMs, ct);
            _log.Info($"[LIVE_KEY_SENT] source={source} attempt={attempt}/{ArrowDownRetryAttempts} key=ArrowDown count={Math.Clamp(count, 1, 4)}");

            // Keep the stable V11.5 sequence.  Checking XPath/identity before this
            // reload races TikTok's virtualized live player and was the source of
            // false LIVE_SWITCH_FAILED loops in V12.5.
            await Task.Delay(ArrowDownSettleBeforeReloadMs, ct);
            _log.Info($"[LIVE_SWITCH_SETTLED] source={source} attempt={attempt}/{ArrowDownRetryAttempts} waitMs={ArrowDownSettleBeforeReloadMs}");
            await _chrome.ReloadAndWaitAsync(Math.Max(0, waitAfterReloadMs), 15000, ct);
            _log.Info($"[LIVE_SWITCH_DOM_READY] source={source} attempt={attempt}/{ArrowDownRetryAttempts}");

            var afterIdentity = await GetCurrentLiveIdentityAsync(ct);
            if (!canVerifyIdentity || !HasReliableLiveIdentity(afterIdentity))
            {
                // Some TikTok layouts expose no stable room id.  A successful
                // ArrowDown + reload + DOM-ready sequence is usable; the following
                // XPath scan is the authoritative readiness check in that layout.
                _log.Warn($"[LIVE_SWITCH_UNVERIFIED] source={source} attempt={attempt}/{ArrowDownRetryAttempts} before={TrimIdentityForLog(beforeIdentity)} after={TrimIdentityForLog(afterIdentity)}");
                return new LiveSwitchVerification(true, beforeIdentity, afterIdentity, attempt, ArrowDownSettleBeforeReloadMs);
            }

            if (!string.Equals(afterIdentity, beforeIdentity, StringComparison.Ordinal))
            {
                _log.Info($"[LIVE_SWITCH_CONFIRMED] source={source} attempt={attempt}/{ArrowDownRetryAttempts} before={TrimIdentityForLog(beforeIdentity)} after={TrimIdentityForLog(afterIdentity)}");
                return new LiveSwitchVerification(true, beforeIdentity, afterIdentity, attempt, ArrowDownSettleBeforeReloadMs);
            }

            _log.Warn($"[LIVE_SWITCH_NOT_CHANGED] source={source} attempt={attempt}/{ArrowDownRetryAttempts} before={TrimIdentityForLog(beforeIdentity)} after={TrimIdentityForLog(afterIdentity)}");
            if (attempt < ArrowDownRetryAttempts)
            {
                _log.Warn($"[LIVE_SWITCH_RETRY] source={source} nextAttempt={attempt + 1}/{ArrowDownRetryAttempts} waitMs={ArrowDownRetryDelayMs}");
                await Task.Delay(ArrowDownRetryDelayMs, ct);
            }
        }

        return new LiveSwitchVerification(false, beforeIdentity, beforeIdentity, ArrowDownRetryAttempts, LiveVerifyTimeoutMs);
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
                ReportProblem("LIVE_SWITCH_FAILED", source, "Đã retry chuyển LIVE nhưng chưa xác nhận LIVE mới; đây là lỗi recoverable, sẽ bỏ qua/retry ở vòng kế tiếp.", throttleSeconds: 10);
                return false;
            }

            ResetPeriodicDue(source + " da xac nhan sang LIVE moi va F5 xong", cancelCandidate: !scheduledPeriodic);
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
                    ReportProblem("LIVE_SWITCH_FAILED", source, "Đã retry chuyển LIVE nhưng chưa xác nhận LIVE mới; sẽ tiếp tục cơ chế bỏ qua LIVE lỗi.", throttleSeconds: 10);
                    return false;
                }

                _log.Warn($"[RECOVERY_OK] transition={source} action=retry-after-cdp-reconnect");
                ResetPeriodicDue(source + " da xac nhan sang LIVE moi sau retry reconnect", cancelCandidate: !scheduledPeriodic);
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
        return verify;
    }


}
