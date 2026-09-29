using System.Diagnostics;
using System.Drawing;
using System.Text.Json;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const string ManagedAccountSetupHoldFileName = "manager_account_setup_hold.json";
    bool _managedSetupDeferredStartRequested;
    bool _managedSetupDeferredStartSuppressDialogs = true;
    string _managedSetupLastDeferredPhase = "";

    bool IsManagedAccountSetupHoldActive()
    {
        if (!_managedMode) return false;

        try
        {
            var dataRoot = (_startupOptions.DataRoot ?? "").Trim();
            if (dataRoot.Length == 0) return false;
            var path = Path.Combine(dataRoot, ManagedAccountSetupHoldFileName);
            if (!File.Exists(path)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ManagerPid", out var pidProp)
                || !pidProp.TryGetInt32(out var managerPid)
                || managerPid <= 0)
                return false;

            try
            {
                using var owner = Process.GetProcessById(managerPid);
                if (!owner.HasExited) return true;
            }
            catch { }

            // Manager cũ đã chết: fail-open và dọn marker stale.
            try { File.Delete(path); } catch { }
            _log.Warn($"[ACCOUNT_SETUP_HOLD_STALE_CLEARED] managerPid={managerPid}");
            return false;
        }
        catch (Exception ex)
        {
            // Marker hỏng không được khóa thao tác tay vĩnh viễn.
            _log.Warn($"[ACCOUNT_SETUP_HOLD_READ_WARN] {ex.Message}");
            return false;
        }
    }

    bool DeferStartForManagedAccountSetup(string phase, bool suppressDialogs)
    {
        if (!IsManagedAccountSetupHoldActive()) return false;

        _managedSetupDeferredStartRequested = true;
        _managedSetupDeferredStartSuppressDialogs &= suppressDialogs;

        if (!string.Equals(_managedSetupLastDeferredPhase, phase, StringComparison.Ordinal))
        {
            _managedSetupLastDeferredPhase = phase;
            _log.Info($"[ACCOUNT_SETUP_START_DEFERRED] phase={phase} videoRunning={IsVideoOperationRunning}");
        }

        SetChromeStatus(
            "Trạng thái Chrome: 🟡 Đang chuẩn bị tài khoản...", Color.Goldenrod,
            "TikTok: 🟡 Tên/ảnh → VIDEO → mới Bắt đầu", Color.Goldenrod);
        return true;
    }


    string CancelManagedAccountSetupHold()
    {
        try
        {
            var dataRoot = (_startupOptions.DataRoot ?? "").Trim();
            if (dataRoot.Length > 0)
            {
                var path = Path.Combine(dataRoot, ManagedAccountSetupHoldFileName);
                if (File.Exists(path)) File.Delete(path);
                try
                {
                    var temp = path + ".tmp";
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[ACCOUNT_SETUP_CANCEL_DELETE_WARN] {ex.Message}");
        }

        // Khác setup_release: hủy intent Start đã defer, tuyệt đối không chạy lại.
        var deferred = _managedSetupDeferredStartRequested;
        _managedSetupDeferredStartRequested = false;
        _managedSetupDeferredStartSuppressDialogs = true;
        _managedSetupLastDeferredPhase = "";

        _log.Warn($"[ACCOUNT_SETUP_CANCELLED] deferredStart={deferred} action=NO_REPLAY");
        return "cancelled";
    }

    string ReleaseManagedAccountSetupHold()
    {
        try
        {
            var dataRoot = (_startupOptions.DataRoot ?? "").Trim();
            if (dataRoot.Length > 0)
            {
                var path = Path.Combine(dataRoot, ManagedAccountSetupHoldFileName);
                if (File.Exists(path)) File.Delete(path);
                try
                {
                    var temp = path + ".tmp";
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[ACCOUNT_SETUP_RELEASE_DELETE_WARN] {ex.Message}");
        }

        var deferred = _managedSetupDeferredStartRequested;
        var suppressDialogs = _managedSetupDeferredStartSuppressDialogs;
        _managedSetupDeferredStartRequested = false;
        _managedSetupDeferredStartSuppressDialogs = true;
        _managedSetupLastDeferredPhase = "";

        _log.Info($"[ACCOUNT_SETUP_RELEASED] deferredStart={deferred} engineRunning={_engine.Running} videoRunning={IsVideoOperationRunning}");

        if (deferred && !_engine.Running && !IsVideoOperationRunning && !IsManagerEmergencyStopActive())
        {
            // Chạy sau khi IPC hiện tại trả về để không giữ request setup_release
            // suốt quá trình Search LIVE/startup dài.
            BeginInvoke(new Action(() => { _ = StartAsync(suppressDialogs); }));
            return "released_start_queued";
        }

        return "released";
    }
}
