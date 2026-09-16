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
        if (IsAutomationHalted)
            ResumeAutomationFromEmergencyStop();
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

        await StopAllWorkerAutomationForEmergencyAsync();
    }

    void ResumeAutomationFromEmergencyStop()
    {
        InitializeEmergencyStopState();
        if (!IsAutomationHalted)
            return;

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

        // Không phục hồi queue/task cũ. Chỉ mở khóa để các hành động MỚI được chạy.
        _autoReplacementSessionArmed = false;
        _autoMessageReplyNextRunUtc.Clear();
        _autoIdentityNextProbeUtc.Clear();

        UpdateEmergencyStopButton();
        UpdateAutoCloseToolbarButtonText();

        _log.Info("[EMERGENCY_STOP_OFF] action=unlock_only old_tasks_not_resumed target=0");
        WriteAutoActivityLog(
            action: "DỪNG KHẨN CẤP",
            result: "TIẾP TỤC",
            detail: "Đã mở khóa automation. Không chạy lại task/queue cũ; target vẫn 0 cho tới khi Start thủ công hoặc Auto Run mới.");
    }

    async Task StopAllWorkerAutomationForEmergencyAsync()
    {
        var contexts = _contexts.Values
            .Where(c => c.Worker is not null && !c.Worker.HasExited)
            .ToList();

        if (contexts.Count == 0)
            return;

        var tasks = contexts.Select(async ctx =>
        {
            try
            {
                // Tin nhắn có engine riêng, dừng trước.
                try
                {
                    await SendPipeAsync(
                        ctx.Profile.Name,
                        "message_reply_stop",
                        TimeSpan.FromSeconds(2));
                }
                catch { }

                // Lệnh stop chỉ dừng AutomationEngine; không đóng Chrome và không shutdown Worker.
                try
                {
                    await SendPipeAsync(
                        ctx.Profile.Name,
                        "stop",
                        TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    _log.Warn($"[EMERGENCY_STOP_WORKER_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }
            catch { }
        }).ToArray();

        try { await Task.WhenAll(tasks); } catch { }
        _log.Warn($"[EMERGENCY_STOP_WORKERS_DONE] requested={contexts.Count}");
    }

    void UpdateEmergencyStopButton()
    {
        if (_autoReplacementManualControlButton is null
            || _autoReplacementManualControlButton.IsDisposed)
        {
            return;
        }

        if (IsAutomationHalted)
        {
            _autoReplacementManualControlButton.Text = "▶ Tiếp tục";
            _autoReplacementManualControlButton.BackColor = Color.FromArgb(232, 247, 236);
            _autoReplacementManualControlButton.ForeColor = Color.FromArgb(32, 122, 60);
            _autoReplacementManualControlButton.FlatAppearance.BorderColor = Color.FromArgb(112, 184, 128);
        }
        else
        {
            _autoReplacementManualControlButton.Text = "🛑 Dừng khẩn cấp";
            _autoReplacementManualControlButton.BackColor = Color.FromArgb(255, 235, 235);
            _autoReplacementManualControlButton.ForeColor = Color.FromArgb(175, 34, 34);
            _autoReplacementManualControlButton.FlatAppearance.BorderColor = Color.FromArgb(221, 92, 92);
        }
    }
}
