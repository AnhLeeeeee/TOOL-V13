using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class EmergencyStopStateDocument
    {
        public int Version { get; set; } = 1;
        public bool Halted { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    Button? _autoReplacementManualControlButton;
    bool _autoReplacementManualControlInitialized;
    bool _emergencyStopStateInitialized;
    volatile bool _emergencyStopActive;
    volatile bool _emergencyResumeInProgress;
    readonly object _emergencyStopLock = new();
    CancellationTokenSource _emergencyAutomationCts = new();

    string EmergencyStopStatePath => Path.Combine(_baseDir, "manager_emergency_stop.json");

    bool IsAutomationHalted => _emergencyStopActive;

    void InitializeEmergencyStopState()
    {
        if (_emergencyStopStateInitialized)
            return;

        _emergencyStopStateInitialized = true;

        try
        {
            if (File.Exists(EmergencyStopStatePath))
            {
                var state = JsonSerializer.Deserialize<EmergencyStopStateDocument>(
                    File.ReadAllText(EmergencyStopStatePath, Encoding.UTF8));
                _emergencyStopActive = state?.Halted == true;
            }
        }
        catch (Exception ex)
        {
            _emergencyStopActive = false;
            try { _log.Warn($"[EMERGENCY_STOP_STATE_READ_WARN] error={ex.Message}"); } catch { }
        }

        if (_emergencyStopActive)
        {
            try { _emergencyAutomationCts.Cancel(); } catch { }
            try { _log.Warn("[EMERGENCY_STOP_RESTORED] halted=true persisted=true"); } catch { }
        }
    }

    CancellationToken GetEmergencyAutomationToken()
    {
        InitializeEmergencyStopState();
        lock (_emergencyStopLock)
            return _emergencyAutomationCts.Token;
    }

    CancellationTokenSource CreateEmergencyLinkedCancellationSource()
        => CancellationTokenSource.CreateLinkedTokenSource(GetEmergencyAutomationToken());

    void SaveEmergencyStopState()
    {
        try
        {
            var state = new EmergencyStopStateDocument
            {
                Halted = _emergencyStopActive,
                UpdatedUtc = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions { WriteIndented = true });
            var temp = EmergencyStopStatePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, EmergencyStopStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { _log.Warn($"[EMERGENCY_STOP_STATE_SAVE_WARN] error={ex.Message}"); } catch { }
        }
    }

    bool IsCommandBlockedByEmergencyStop(string? command)
    {
        if (!IsAutomationHalted)
            return false;

        command = (command ?? "").Trim().ToLowerInvariant();

        // Trong trạng thái khẩn cấp chỉ cho phép quan sát, dừng/đóng thủ công và xem Chrome.
        // Các lệnh có thể bắt đầu/tiếp tục automation hoặc điều khiển TikTok bị chặn.
        return command switch
        {
            "status" or "message_reply_status" or "message_reply_log"
                or "message_reply_stop" or "stop" or "pause" or "show"
                or "view_chrome" or "close_chrome" => false,
            _ => true
        };
    }

    bool EnsureAutomationAllowedFromUi(string operation)
    {
        if (!IsAutomationHalted)
            return true;

        try
        {
            ModernDialog.ShowMessage(
                this,
                $"Tool đang ở trạng thái Dừng khẩn cấp.\r\n\r\nKhông thể {operation}. Hãy bấm ‘Tiếp tục’ trước.",
                "Đã dừng khẩn cấp",
                MessageBoxIcon.Warning);
        }
        catch { }
        return false;
    }

    void InitializeAutoReplacementManualControl()
    {
        InitializeEmergencyStopState();

        if (_autoReplacementManualControlInitialized)
        {
            UpdateEmergencyStopButton();
            return;
        }

        var toolbar =
            EnumerateAutoReplacementManualControls(this)
                .OfType<FlowLayoutPanel>()
                .FirstOrDefault(IsAutoReplacementManagerActionToolbar);

        if (toolbar is null)
        {
            _log.Warn("[EMERGENCY_STOP_UI] Không tìm thấy toolbar để thêm nút Dừng khẩn cấp.");
            return;
        }

        _autoReplacementManualControlInitialized = true;

        _autoReplacementManualControlButton = new Button
        {
            AutoSize = true,
            Height = 34,
            MinimumSize = new Size(132, 34),
            Margin = new Padding(5, 3, 5, 3),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        _autoReplacementManualControlButton.FlatAppearance.BorderSize = 1;
        _autoReplacementManualControlButton.Click += async (_, _) =>
            await ToggleEmergencyStopFromUiAsync();

        toolbar.Controls.Add(_autoReplacementManualControlButton);

        try
        {
            if (_autoCloseToolbarButton is not null
                && !_autoCloseToolbarButton.IsDisposed
                && _autoCloseToolbarButton.Parent == toolbar)
            {
                var autoCloseIndex = toolbar.Controls.GetChildIndex(_autoCloseToolbarButton);
                toolbar.Controls.SetChildIndex(
                    _autoReplacementManualControlButton,
                    Math.Min(toolbar.Controls.Count - 1, autoCloseIndex + 1));
            }
        }
        catch { }

        UpdateEmergencyStopButton();
        UpdateAutoCloseToolbarButtonText();

        _log.Info($"[EMERGENCY_STOP_UI_READY] halted={IsAutomationHalted}");
    }

    static bool IsAutoReplacementManagerActionToolbar(FlowLayoutPanel panel)
    {
        var buttons = panel.Controls.OfType<Button>().ToList();

        if (buttons.Any(button =>
                button.Text.Equals("Stop All", StringComparison.OrdinalIgnoreCase)
                || button.Text.Equals("Dừng tất cả", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var hasRun = buttons.Any(button =>
            button.Text.Equals("Auto Run", StringComparison.OrdinalIgnoreCase)
            || button.Text.Equals("Chạy tất cả", StringComparison.OrdinalIgnoreCase));
        var hasDelete = buttons.Any(button =>
            button.Text.Equals("Delete", StringComparison.OrdinalIgnoreCase)
            || button.Text.Equals("Xóa profile", StringComparison.OrdinalIgnoreCase));

        return hasRun && hasDelete;
    }

    static IEnumerable<Control> EnumerateAutoReplacementManualControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in EnumerateAutoReplacementManualControls(child))
                yield return nested;
        }
    }

    async Task ToggleEmergencyStopFromUiAsync()
    {
        if (_emergencyResumeInProgress)
            return;

        if (IsAutomationHalted)
            await ResumeAutomationFromEmergencyStopAsync();
        else
            await EnterEmergencyStopAsync("manual_ui");
    }

    async Task EnterEmergencyStopAsync(string source)
    {
        InitializeEmergencyStopState();
        if (IsAutomationHalted)
            return;

        _emergencyStopActive = true;
        SaveEmergencyStopState();

        CancellationTokenSource? stopCts = null;
        lock (_emergencyStopLock)
        {
            if (!_emergencyAutomationCts.IsCancellationRequested)
                stopCts = _emergencyAutomationCts;
        }
        try { stopCts?.Cancel(); } catch { }

        // Hủy mọi hàng đợi/phiên tự động có token riêng. Không đóng Chrome/PRF.
        try { StopRunStrategySession("emergency_stop"); } catch { }
        try { _nightReserveCts.Cancel(); } catch { }
        try { InvalidateAutoReplacementExecution("emergency_stop"); } catch { }
        _autoReplacementSessionArmed = false;

        var clearedPending = 0;
        lock (_autoReplacementQueueLock)
        {
            clearedPending = _autoReplacementQueue.Count;
            _autoReplacementQueue.Clear();
            try { SaveAutoReplacementQueueUnsafe(); } catch { }
        }

        // Emergency Stop không giữ quota cũ. Sau khi Tiếp tục, người dùng Start thủ công
        // hoặc Auto Run lại thì target được thiết lập lại từ hành động mới.
        lock (_autoReplacementFixedSlotLock)
        {
            _autoReplacementTargetSlots = 0;
            _autoReplacementTargetInitialized = true;
        }

        _autoMessageReplyNextRunUtc.Clear();
        _autoIdentityNextProbeUtc.Clear();

        UpdateEmergencyStopButton();
        UpdateAutoCloseToolbarButtonText();

        _log.Warn(
            $"[EMERGENCY_STOP_ON] source={source} clearedReplacement={clearedPending} target=0 persisted=true");
        WriteAutoActivityLog(
            action: "DỪNG KHẨN CẤP",
            result: "ĐÃ DỪNG",
            detail: $"Hủy automation đang chạy, xóa {clearedPending} suất bù tạm và đặt target=0. Chrome/PRF được giữ nguyên.");

        var workersStopped = await StopAllWorkerAutomationForEmergencyAsync();
        if (!workersStopped)
        {
            _log.Error("[EMERGENCY_STOP_INCOMPLETE] Một hoặc nhiều Worker không xác nhận STOP và cũng không thể force-exit Worker-only.");
            ModernDialog.ShowMessage(
                this,
                "Dừng khẩn cấp đã được bật nhưng có Worker không phản hồi và không thể dừng cưỡng bức. Hãy kiểm tra Log/Task Manager trước khi tiếp tục.",
                "Dừng khẩn cấp chưa hoàn tất",
                MessageBoxIcon.Warning);
        }
    }

    async Task ResumeAutomationFromEmergencyStopAsync()
    {
        InitializeEmergencyStopState();
        if (!IsAutomationHalted || _emergencyResumeInProgress)
            return;

        _emergencyResumeInProgress = true;
        UpdateEmergencyStopButton();

        try
        {
            // QUAN TRỌNG: vẫn giữ Halted=true trong toàn bộ pha đồng bộ. Như vậy mọi
            // timer/job nền tiếp tục bị gate trong lúc Manager đang quét lại runtime.
            // Chỉ mở khóa ở bước cuối sau khi target/queue cũ đã được reset lần nữa.
            try { StopRunStrategySession("emergency_resume_resync"); } catch { }
            try { InvalidateAutoReplacementExecution("emergency_resume_resync"); } catch { }
            _autoReplacementSessionArmed = false;

            lock (_autoReplacementQueueLock)
            {
                _autoReplacementQueue.Clear();
                try { SaveAutoReplacementQueueUnsafe(); } catch { }
            }

            lock (_autoReplacementFixedSlotLock)
            {
                _autoReplacementTargetSlots = 0;
                _autoReplacementTargetInitialized = true;
                _autoReplacementNextCapacityReconcileUtc = DateTime.MinValue;
            }

            _autoMessageReplyNextRunUtc.Clear();
            _autoIdentityNextProbeUtc.Clear();

            // Đồng bộ catalog/context nhưng tuyệt đối không spawn Worker/Chrome.
            try
            {
                var catalog = await Task.Run(() => _profileService.Load());
                RefreshContextsFromCatalog(catalog);
            }
            catch (Exception ex)
            {
                _log.Warn($"[EMERGENCY_RESUME_CATALOG_WARN] error={ex.Message}");
            }

            // Dừng lại lần cuối các Worker còn sống. Emergency state vẫn đang được
            // persist=true nên Worker cũng tự chặn START/RESUME mới trong lúc này.
            // Nếu một Worker không thể dừng kể cả fallback Worker-only thì KHÔNG mở khóa.
            var workersStopped = await StopAllWorkerAutomationForEmergencyAsync();
            if (!workersStopped)
            {
                _log.Error("[EMERGENCY_RESUME_BLOCKED] reason=worker_stop_not_confirmed");
                ModernDialog.ShowMessage(
                    this,
                    "Chưa thể Tiếp tục vì còn Worker không xác nhận đã dừng. Trạng thái Dừng khẩn cấp vẫn được giữ nguyên.",
                    "Chưa thể tiếp tục",
                    MessageBoxIcon.Warning);
                return;
            }

            // Chỉ đọc status để Manager bỏ snapshot cũ trước khi mở khóa. Không gọi
            // EnsureWorkerAsync/Adopt nên không thể sinh process mới ở pha Resume.
            foreach (var ctx in _contexts.Values.ToList())
            {
                if (_closing || IsDisposed || Disposing)
                    return;

                var workerAlive = false;
                try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
                catch { workerAlive = ctx.Worker is not null; }

                if (!workerAlive)
                {
                    if (ctx.Worker is not null)
                        ConfirmRuntimeState(ctx, RuntimeStateStopped, "emergency_resume_worker_not_alive");
                    continue;
                }

                try
                {
                    var raw = await SendPipeAsync(
                        ctx.Profile.Name,
                        "status",
                        TimeSpan.FromSeconds(2));

                    var snapshot = JsonSerializer.Deserialize<WorkerSnapshot>(
                        raw,
                        WorkerSnapshotJson);

                    if (snapshot is null
                        || !snapshot.Profile.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase)
                        || snapshot.CdpPort != ctx.Profile.CdpPort
                        || !IsWorkerReportedRuntimeState(snapshot.RunState))
                    {
                        throw new InvalidDataException("Worker status không hợp lệ sau Dừng khẩn cấp.");
                    }

                    ctx.LastSnapshot = snapshot;
                    ctx.LastStatusRefreshUtc = DateTime.UtcNow;
                    ctx.ConsecutiveStatusPollFailures = 0;
                    ctx.LastStatusPollFailure = "";
                    ApplyWorkerSnapshotRuntimeState(ctx, snapshot);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[EMERGENCY_RESUME_STATUS_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            // Race cuối: một task cũ có thể vừa unwind trong lúc quét. Reset lại queue,
            // generation và quota TRƯỚC khi đổi Halted=false để nó không sống lại.
            try { InvalidateAutoReplacementExecution("emergency_resume_final_reset"); } catch { }
            _autoReplacementSessionArmed = false;

            lock (_autoReplacementQueueLock)
            {
                _autoReplacementQueue.Clear();
                try { SaveAutoReplacementQueueUnsafe(); } catch { }
            }

            lock (_autoReplacementFixedSlotLock)
            {
                _autoReplacementTargetSlots = 0;
                _autoReplacementTargetInitialized = true;
                _autoReplacementNextCapacityReconcileUtc = DateTime.MinValue;
            }

            // Không mang marker phiên cũ qua Resume. Nếu giữ ExpectedRunning/Claimed
            // của trước Emergency Stop thì lần Start thủ công đầu tiên có thể tính nhầm
            // nhiều slot cũ và làm target nhảy từ 0 lên quá cao.
            _autoCloseExpectedRunningProfiles.Clear();
            _autoReplacementClaimedProfiles.Clear();
            _autoReplacementCleanupProfiles.Clear();

            CancellationTokenSource? previous;
            lock (_emergencyStopLock)
            {
                previous = _emergencyAutomationCts;
                _emergencyAutomationCts = new CancellationTokenSource();
            }
            try { previous.Dispose(); } catch { }

            // Dự phòng đêm dùng CTS lâu sống; tạo token mới sau Emergency Stop.
            try
            {
                var oldNight = _nightReserveCts;
                _nightReserveCts = new CancellationTokenSource();
                try { oldNight.Dispose(); } catch { }
            }
            catch { }

            _emergencyStopActive = false;
            SaveEmergencyStopState();

            _log.Info(
                "[EMERGENCY_STOP_OFF] action=resync_then_unlock old_tasks_not_resumed target=0");
            WriteAutoActivityLog(
                action: "DỪNG KHẨN CẤP",
                result: "TIẾP TỤC",
                detail: "Đã quét lại Worker/runtime rồi mới mở khóa. Không chạy lại task/queue cũ; target vẫn 0 cho tới khi Start thủ công hoặc Auto Run mới.");
        }
        finally
        {
            _emergencyResumeInProgress = false;
            UpdateEmergencyStopButton();
            UpdateAutoCloseToolbarButtonText();
            try { RefreshAvailability(); } catch { }
            try { UpdateTitle(); } catch { }
        }
    }

    async Task<bool> StopAllWorkerAutomationForEmergencyAsync()
    {
        var contexts = _contexts.Values
            .Where(c => c.Worker is not null && !c.Worker.HasExited)
            .ToList();

        if (contexts.Count == 0)
            return true;

        var tasks = contexts.Select(async ctx =>
        {
            try
            {
                // Tin nhắn có engine riêng, dừng trước. Lỗi ở đây không ngăn bước
                // dừng AutomationEngine chính và fallback Worker-only phía dưới.
                try
                {
                    await SendPipeAsync(
                        ctx.Profile.Name,
                        "message_reply_stop",
                        TimeSpan.FromSeconds(2));
                }
                catch { }

                var stopConfirmed = false;
                string lastStopError = "";

                // Thử IPC hai lần. emergency_stop ở Worker chỉ trả "stopped" sau
                // khi AutomationEngine đã unwind thật sự; stop_pending sẽ đi tới retry
                // rồi fallback Worker-only nếu vẫn chưa dừng hết.
                for (var attempt = 1; attempt <= 2 && !stopConfirmed; attempt++)
                {
                    try
                    {
                        var reply = await SendPipeAsync(
                            ctx.Profile.Name,
                            "emergency_stop",
                            TimeSpan.FromSeconds(attempt == 1 ? 4 : 3));

                        stopConfirmed = string.Equals(
                            (reply ?? "").Trim(),
                            "stopped",
                            StringComparison.OrdinalIgnoreCase);

                        if (!stopConfirmed)
                        {
                            lastStopError = "reply=" + (reply ?? "<null>");
                            _log.Warn(
                                $"[EMERGENCY_STOP_WORKER_UNCONFIRMED] profile={ctx.Profile.Name} attempt={attempt}/2 {lastStopError}");
                        }
                    }
                    catch (Exception ex)
                    {
                        lastStopError = ex.Message;
                        _log.Warn(
                            $"[EMERGENCY_STOP_WORKER_WARN] profile={ctx.Profile.Name} attempt={attempt}/2 error={ex.Message}");
                    }
                }

                if (stopConfirmed)
                {
                    ConfirmRuntimeState(
                        ctx,
                        RuntimeStateStopped,
                        "emergency_stop_ipc_confirmed");
                    return true;
                }

                // IPC không phản hồi: để đạt đúng nghĩa Dừng khẩn cấp, chấm dứt CHỈ
                // process Worker. Tuyệt đối không Kill(entireProcessTree:true), vì cây
                // process có thể chứa Chrome và yêu cầu Emergency Stop là giữ Chrome.
                var worker = ctx.Worker;
                if (worker is null)
                    return true;

                try
                {
                    if (!worker.HasExited)
                    {
                        _log.Error(
                            $"[EMERGENCY_STOP_WORKER_FORCE_EXIT_ONLY] profile={ctx.Profile.Name} pid={worker.Id} preserveChrome=true ipcError={lastStopError}");
                        worker.Kill(entireProcessTree: false);
                    }

                    if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(4)))
                    {
                        _log.Error(
                            $"[EMERGENCY_STOP_WORKER_FORCE_EXIT_FAILED] profile={ctx.Profile.Name} pid={worker.Id} preserveChrome=true");
                        return false;
                    }

                    ConfirmRuntimeState(
                        ctx,
                        RuntimeStateStopped,
                        "emergency_stop_worker_only_exit");

                    try { worker.Dispose(); } catch { }
                    if (ReferenceEquals(ctx.Worker, worker))
                        ctx.Worker = null;
                    ctx.WorkerWindow = IntPtr.Zero;
                    ctx.Opening = false;

                    return true;
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"[EMERGENCY_STOP_WORKER_FORCE_EXIT_ERROR] profile={ctx.Profile.Name} error={ex.Message} preserveChrome=true");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"[EMERGENCY_STOP_WORKER_FATAL] profile={ctx.Profile.Name} error={ex}");
                return false;
            }
        }).ToArray();

        bool[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _log.Error("[EMERGENCY_STOP_WORKERS_TASK_ERROR] " + ex);
            return false;
        }

        var stopped = results.Count(x => x);
        var allStopped = stopped == results.Length;
        _log.Warn(
            $"[EMERGENCY_STOP_WORKERS_DONE] requested={contexts.Count} stopped={stopped} failed={results.Length - stopped} allStopped={allStopped}");
        return allStopped;
    }

    void UpdateEmergencyStopButton()
    {
        if (_autoReplacementManualControlButton is null
            || _autoReplacementManualControlButton.IsDisposed)
        {
            return;
        }

        if (_emergencyResumeInProgress)
        {
            _autoReplacementManualControlButton.Text = "⏳ Đang kiểm tra...";
            _autoReplacementManualControlButton.Enabled = false;
            _autoReplacementManualControlButton.BackColor = Color.FromArgb(244, 244, 244);
            _autoReplacementManualControlButton.ForeColor = Color.DimGray;
            _autoReplacementManualControlButton.FlatAppearance.BorderColor = Color.Silver;
        }
        else if (IsAutomationHalted)
        {
            _autoReplacementManualControlButton.Text = "▶ Tiếp tục";
            _autoReplacementManualControlButton.Enabled = true;
            _autoReplacementManualControlButton.BackColor = Color.FromArgb(232, 247, 236);
            _autoReplacementManualControlButton.ForeColor = Color.FromArgb(32, 122, 60);
            _autoReplacementManualControlButton.FlatAppearance.BorderColor = Color.FromArgb(112, 184, 128);
        }
        else
        {
            _autoReplacementManualControlButton.Text = "🛑 Dừng khẩn cấp";
            _autoReplacementManualControlButton.Enabled = true;
            _autoReplacementManualControlButton.BackColor = Color.FromArgb(255, 235, 235);
            _autoReplacementManualControlButton.ForeColor = Color.FromArgb(175, 34, 34);
            _autoReplacementManualControlButton.FlatAppearance.BorderColor = Color.FromArgb(221, 92, 92);
        }
    }
}
