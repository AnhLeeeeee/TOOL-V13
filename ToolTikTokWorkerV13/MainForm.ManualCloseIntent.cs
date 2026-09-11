using System.Text.Json;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const string ManagedManualCloseIntentFileName = "worker_manual_close_intent.json";
    bool _managedShutdownRequested;
    string? _manualCloseIntentOperationId;
    bool _manualCloseIntentWritten;

    sealed class ManagedManualCloseIntentDocument
    {
        public int Version { get; set; } = 1;
        public string ProfileName { get; set; } = "";
        public int WorkerPid { get; set; }
        public string Origin { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
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
            _manualCloseIntentWritten = TryWriteManualCloseIntent("USER_X_CLOSE");
        }

        base.OnFormClosing(e);
    }

    bool TryWriteManualCloseIntent(string origin)
    {
        try
        {
            var dataRoot = (_startupOptions.DataRoot ?? "").Trim();
            if (dataRoot.Length == 0)
                return false;

            Directory.CreateDirectory(dataRoot);

            _manualCloseIntentOperationId ??= Guid.NewGuid().ToString("N");

            var document = new ManagedManualCloseIntentDocument
            {
                ProfileName = (_startupOptions.ProfileName ?? "").Trim(),
                WorkerPid = Environment.ProcessId,
                Origin = (origin ?? "").Trim(),
                OperationId = _manualCloseIntentOperationId,
                CreatedUtc = DateTime.UtcNow
            };

            var path = Path.Combine(dataRoot, ManagedManualCloseIntentFileName);
            var temp = path + ".tmp";

            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

            File.Move(temp, path, overwrite: true);

            _log.Warn(
                $"[MANUAL_CLOSE_INTENT] profile={document.ProfileName} origin={document.Origin} operationId={document.OperationId} pid={document.WorkerPid}");
            return true;
        }
        catch (Exception ex)
        {
            // Không chặn người dùng đóng Worker chỉ vì log intent thất bại.
            try { _log.Warn("[MANUAL_CLOSE_INTENT_WRITE_FAILED] " + ex.Message); } catch { }
            return false;
        }
    }
}
