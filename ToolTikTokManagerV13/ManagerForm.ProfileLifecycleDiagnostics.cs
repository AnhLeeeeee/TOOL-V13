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
            "[RUNTIME_"
        ];

        return important.Any(token =>
            line.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    void AppendManagerProfileLifecycleDiagnostic(string line)
    {
        try
        {
            lock (_profileLifecycleDiagnosticLock)
            {
                Directory.CreateDirectory(ProfileLifecycleDiagnosticDirectory);
                RotateDiagnosticFileIfNeeded(ManagerProfileLifecycleDiagnosticPath, 4 * 1024 * 1024);
                File.AppendAllText(
                    ManagerProfileLifecycleDiagnosticPath,
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Nhật ký chẩn đoán không bao giờ được làm gián đoạn tool.
        }
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
