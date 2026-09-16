using System.Text.Json;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const string ManagedManualCloseIntentFileName = "worker_manual_close_intent.json";
    bool _managedShutdownRequested;
    bool _manualCloseIntentWritten;
    bool _manualStopIntentPending;

    sealed class ManagedManualCloseIntentDocument
    {
        public int Version { get; set; } = 2;
        public string ProfileName { get; set; } = "";
        public int WorkerPid { get; set; }
        public string Origin { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public bool WasRunning { get; set; }
    }

    // Implement the optional hooks declared in V115Core/MainForm.cs. These are invoked
    // only for a Start/Stop initiated from the visible Worker UI (or its user hotkey).
    // IPC commands from Manager call _engine directly and therefore never enter here.
    partial void OnManagedUserStopIntent()
    {
        if (!_managedMode)
            return;

        var wasRunning = _engine.Running || _engine.Paused;
        if (!wasRunning)
            return;

        _manualStopIntentPending = true;
        TryWriteManualRuntimeIntent("USER_STOP", wasRunning: true);
    }

    partial void OnManagedUserStartIntent()
    {
        if (!_managedMode)
            return;

        _manualStopIntentPending = false;
        _manualCloseIntentWritten = false;
        TryWriteManualRuntimeIntent("USER_START", wasRunning: _engine.Running || _engine.Paused);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Chỉ coi là đóng thủ công khi:
        // 1) Worker đang chạy dưới Manager,
        // 2) không có lệnh shutdown do Manager gửi trước đó,
        // 3) WinForms báo người dùng đóng cửa sổ.
        //
        // Các đường AutoClose/NameGuard/Manager shutdown đều gửi command "shutdown"
        // và đặt _managedShutdownRequested=true trước khi gọi Close(), nên không thể
        // bị nhầm thành USER_X_CLOSE.
        if (_managedMode
            && !_managedShutdownRequested
            && !_manualCloseIntentWritten
            && e.CloseReason == CloseReason.UserClosing)
        {
            // Nếu user vừa bấm Stop rồi bấm X trước khi Manager kịp đọc marker Stop,
            // giữ bằng chứng rằng profile thực sự đang RUNNING tại thời điểm user Stop.
            var wasRunning = _engine.Running || _engine.Paused || _manualStopIntentPending;
            _manualCloseIntentWritten = TryWriteManualRuntimeIntent(
                "USER_X_CLOSE",
                wasRunning);
        }

        base.OnFormClosing(e);
    }

    bool TryWriteManualRuntimeIntent(string origin, bool wasRunning)
    {
        try
        {
            var dataRoot = (_startupOptions.DataRoot ?? "").Trim();
            if (dataRoot.Length == 0)
                return false;

            Directory.CreateDirectory(dataRoot);

            var document = new ManagedManualCloseIntentDocument
            {
                ProfileName = (_startupOptions.ProfileName ?? "").Trim(),
                WorkerPid = Environment.ProcessId,
                Origin = (origin ?? "").Trim(),
                OperationId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                WasRunning = wasRunning
            };

            var path = Path.Combine(dataRoot, ManagedManualCloseIntentFileName);
            var temp = path + ".tmp";

            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

            File.Move(temp, path, overwrite: true);

            _log.Warn(
                $"[MANUAL_RUNTIME_INTENT] profile={document.ProfileName} origin={document.Origin} operationId={document.OperationId} pid={document.WorkerPid} wasRunning={document.WasRunning}");
            return true;
        }
        catch (Exception ex)
        {
            // Không chặn thao tác của người dùng chỉ vì ghi marker thất bại.
            try { _log.Warn("[MANUAL_RUNTIME_INTENT_WRITE_FAILED] " + ex.Message); } catch { }
            return false;
        }
    }
}
