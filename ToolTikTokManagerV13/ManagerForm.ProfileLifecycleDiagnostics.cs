using System.Diagnostics;
using System.Text;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly object _profileLifecycleDiagnosticLock = new();
    bool _profileLifecycleDiagnosticInitialized;
    Button? _profileLifecycleDiagnosticButton;
    string _managerShutdownIntent = "";
    string _managerShutdownIntentDetail = "";
    int _managerShutdownClosingEventCount;

    string ProfileLifecycleDiagnosticDirectory
        => Path.Combine(_baseDir, "logs", "diagnostic");

    string ManagerProfileLifecycleDiagnosticPath
        => Path.Combine(ProfileLifecycleDiagnosticDirectory, "manager-profile-lifecycle.log");

    void InitializeProfileLifecycleDiagnostics()
    {
        if (_profileLifecycleDiagnosticInitialized)
            return;

        _profileLifecycleDiagnosticInitialized = true;

        try
        {
            Directory.CreateDirectory(ProfileLifecycleDiagnosticDirectory);
            AppendManagerProfileLifecycleDiagnostic(
                $"=== SESSION START {DateTime.Now:yyyy-MM-dd HH:mm:ss} | app={AppVersionInfo.Display} | pid={Environment.ProcessId} ===");
        }
        catch { }

        FormClosing += CaptureManagerFormClosingDiagnostic;
        FormClosed += CaptureManagerFormClosedDiagnostic;

        _log.LineWritten += CaptureManagerProfileLifecycleLine;
        InjectProfileLifecycleDiagnosticButton();
    }

    void CaptureManagerProfileLifecycleLine(string line)
    {
        if (!ShouldCaptureManagerProfileLifecycleLine(line))
            return;

        AppendManagerProfileLifecycleDiagnostic(line);
    }

    static bool ShouldCaptureManagerProfileLifecycleLine(string? line)
    {
        line ??= "";
        if (line.Length == 0)
            return false;

        // WARN/ERROR luôn giữ để không mất lỗi bất ngờ ngoài các prefix đã biết.
        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] important =
        [
            "[AUTO_PROFILE_",
            "[AUTO_CLOSE_",
            "[AUTO_REPLACE_",
            "[LOGIN_BAN_",
            "[BAN_",
            "[REUSE_QUEUE_",
            "[WORKER_",
            "[NAME_GUARD_",
            "[CHROME_",
            "[RUNTIME_",
            "[MANAGER_",
            "[VERSION_"
        ];

        return important.Any(token =>
            line.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    void AppendManagerProfileLifecycleDiagnostic(string line)
    {
        ManagerProcessDiagnostics.Append(line);
    }

    static void RotateDiagnosticFileIfNeeded(string path, long maxBytes)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < maxBytes)
                return;

            var previous = path + ".previous";
            File.Move(path, previous, overwrite: true);
        }
        catch { }
    }

    void MarkManagerShutdownIntent(string reason, string? detail = null)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "UNKNOWN" : reason.Trim();
        detail ??= "";

        if (string.IsNullOrWhiteSpace(_managerShutdownIntent))
        {
            _managerShutdownIntent = reason;
            _managerShutdownIntentDetail = detail;
        }

        AppendManagerProfileLifecycleDiagnostic(
            $"[MANAGER_SHUTDOWN_INTENT] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
            $"reason={reason} detail={ManagerProcessDiagnostics.OneLine(detail, 2000)} " +
            $"callerStack={ManagerProcessDiagnostics.OneLine(Environment.StackTrace, 5000)}");
    }

    void CaptureManagerFormClosingDiagnostic(object? sender, FormClosingEventArgs e)
    {
        var eventNo = Interlocked.Increment(ref _managerShutdownClosingEventCount);
        var intent = string.IsNullOrWhiteSpace(_managerShutdownIntent)
            ? ResolveManagerShutdownReason(e.CloseReason)
            : _managerShutdownIntent;
        var detail = string.IsNullOrWhiteSpace(_managerShutdownIntentDetail)
            ? $"closeReason={e.CloseReason}"
            : _managerShutdownIntentDetail;

        AppendManagerProfileLifecycleDiagnostic(
            $"[MANAGER_FORM_CLOSING] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
            $"event={eventNo} phase={(_closing ? "FINAL_OR_REENTRY" : "INITIAL")} reason={intent} " +
            $"closeReason={e.CloseReason} cancelBeforeHandler={e.Cancel} detail={ManagerProcessDiagnostics.OneLine(detail, 2000)} " +
            $"callerStack={ManagerProcessDiagnostics.OneLine(Environment.StackTrace, 5000)}");
    }

    void CaptureManagerFormClosedDiagnostic(object? sender, FormClosedEventArgs e)
    {
        var intent = string.IsNullOrWhiteSpace(_managerShutdownIntent)
            ? ResolveManagerShutdownReason(e.CloseReason)
            : _managerShutdownIntent;

        AppendManagerProfileLifecycleDiagnostic(
            $"[MANAGER_FORM_CLOSED] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} " +
            $"reason={intent} closeReason={e.CloseReason}");
    }

    static string ResolveManagerShutdownReason(CloseReason closeReason)
        => closeReason switch
        {
            CloseReason.WindowsShutDown => "WINDOWS_SHUTDOWN",
            CloseReason.TaskManagerClosing => "TASK_MANAGER_CLOSE",
            CloseReason.ApplicationExitCall => "APPLICATION_EXIT_CALL",
            CloseReason.FormOwnerClosing => "OWNER_FORM_CLOSING",
            CloseReason.MdiFormClosing => "MDI_FORM_CLOSING",
            CloseReason.None => "CLOSE_REASON_NONE",
            CloseReason.UserClosing => "USER_CLOSE_OR_UNMARKED_CODE_CLOSE",
            _ => "UNKNOWN_" + closeReason
        };

    void InjectProfileLifecycleDiagnosticButton()
    {
        if (_profileLifecycleDiagnosticButton is not null
            && !_profileLifecycleDiagnosticButton.IsDisposed)
        {
            return;
        }

        try
        {
            var toolbar = EnumerateProfileLifecycleControls(this)
                .OfType<FlowLayoutPanel>()
                .FirstOrDefault(panel => panel.Controls
                    .OfType<Button>()
                    .Any(button => button.Text.Contains("Dừng tất cả", StringComparison.OrdinalIgnoreCase)));

            if (toolbar is null)
                return;

            _profileLifecycleDiagnosticButton = Button(
                "Nhật ký lỗi",
                (_, _) => OpenProfileLifecycleDiagnosticDirectory(),
                UiButtonKind.Neutral);

            toolbar.Controls.Add(_profileLifecycleDiagnosticButton);
        }
        catch { }
    }

    static IEnumerable<Control> EnumerateProfileLifecycleControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in EnumerateProfileLifecycleControls(child))
                yield return nested;
        }
    }

    void OpenProfileLifecycleDiagnosticDirectory()
    {
        try
        {
            Directory.CreateDirectory(ProfileLifecycleDiagnosticDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ProfileLifecycleDiagnosticDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ModernDialog.ShowMessage(
                this,
                "Không mở được thư mục nhật ký.\r\n\r\n" + ex.Message,
                "Nhật ký lỗi",
                MessageBoxIcon.Warning);
        }
    }
}
