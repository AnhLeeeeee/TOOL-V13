using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly object _profileLifecycleDiagnosticLock = new();
    bool _profileLifecycleDiagnosticInitialized;
    Button? _profileLifecycleDiagnosticButton;
    ContextMenuStrip? _profileLifecycleDiagnosticMenu;
    string _managerShutdownIntent = "";
    string _managerShutdownIntentDetail = "";
    int _managerShutdownClosingEventCount;

    string ToolLogDirectory
        => Path.Combine(_baseDir, "logs");

    string ProfileLifecycleDiagnosticDirectory
        => Path.Combine(ToolLogDirectory, "diagnostic");

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
        var buttonReady = _profileLifecycleDiagnosticButton is not null
            && !_profileLifecycleDiagnosticButton.IsDisposed;

        if (buttonReady)
            return;

        try
        {
            var toolbar = EnumerateProfileLifecycleControls(this)
                .OfType<FlowLayoutPanel>()
                .FirstOrDefault(panel => panel.Controls
                    .OfType<Button>()
                    .Any(button => button.Text.Contains("Dừng tất cả", StringComparison.OrdinalIgnoreCase)));

            if (toolbar is null)
                return;

            _profileLifecycleDiagnosticMenu?.Dispose();
            _profileLifecycleDiagnosticMenu = new ContextMenuStrip();
            _profileLifecycleDiagnosticMenu.Items.Add(
                "Mở thư mục Log",
                null,
                (_, _) => OpenToolLogDirectory());
            _profileLifecycleDiagnosticMenu.Items.Add(
                "Xuất log chẩn đoán...",
                null,
                async (_, _) => await ExportDiagnosticLogsAsync());

            _profileLifecycleDiagnosticButton = Button(
                "Log ▼",
                (_, _) => ShowProfileLifecycleDiagnosticMenu(),
                UiButtonKind.Neutral);

            toolbar.Controls.Add(_profileLifecycleDiagnosticButton);
        }
        catch { }
    }

    void ShowProfileLifecycleDiagnosticMenu()
    {
        var button = _profileLifecycleDiagnosticButton;
        var menu = _profileLifecycleDiagnosticMenu;
        if (button is null || button.IsDisposed || menu is null || menu.IsDisposed)
            return;

        menu.Show(button, new Point(0, button.Height));
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

    void OpenToolLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(ToolLogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ToolLogDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ModernDialog.ShowMessage(
                this,
                "Không mở được thư mục Log.\r\n\r\n" + ex.Message,
                "Mở thư mục Log",
                MessageBoxIcon.Warning);
        }
    }

    async Task ExportDiagnosticLogsAsync()
    {
        var logButton = _profileLifecycleDiagnosticButton;
        if (logButton is not null)
            logButton.Enabled = false;

        try
        {
            Directory.CreateDirectory(ToolLogDirectory);

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                desktop = _baseDir;

            using var saveDialog = new SaveFileDialog
            {
                Title = "Chọn vị trí lưu log chẩn đoán",
                Filter = "ZIP (*.zip)|*.zip",
                DefaultExt = "zip",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true,
                InitialDirectory = desktop,
                FileName = $"ToolTikTok_Diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            };

            if (saveDialog.ShowDialog(this) != DialogResult.OK
                || string.IsNullOrWhiteSpace(saveDialog.FileName))
            {
                return;
            }

            var archivePath = Path.GetFullPath(saveDialog.FileName);
            var result = await Task.Run(() => CreateDiagnosticLogArchive(archivePath));

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{archivePath}\"",
                    UseShellExecute = true
                });
            }
            catch { }

            var skippedNote = result.Skipped.Count == 0
                ? ""
                : $"\r\nBỏ qua {result.Skipped.Count} file đang khóa/không đọc được; danh sách đã được ghi trong ZIP.";

            ModernDialog.ShowMessage(
                this,
                $"Đã xuất gói chẩn đoán gồm {result.Included} file Log.\r\n\r\n" +
                $"{archivePath}{skippedNote}\r\n\r\n" +
                "Khi cần kiểm tra lỗi, chỉ cần gửi file ZIP này.",
                "Xuất log chẩn đoán",
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ModernDialog.ShowMessage(
                this,
                "Không xuất được log chẩn đoán.\r\n\r\n" + ex.Message,
                "Xuất log chẩn đoán",
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (logButton is not null && !logButton.IsDisposed)
                logButton.Enabled = true;
        }
    }

    (int Included, List<string> Skipped) CreateDiagnosticLogArchive(string archivePath)
    {
        var skipped = new List<string>();
        var included = 0;

        using var archiveStream = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

        IEnumerable<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(ToolLogDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            skipped.Add($"Không liệt kê được thư mục Log: {ex.Message}");
            files = Array.Empty<string>();
        }

        foreach (var file in files)
        {
            if (string.Equals(
                    Path.GetFullPath(file),
                    Path.GetFullPath(archivePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(ToolLogDirectory, file);

            try
            {
                using var input = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var entryName = "logs/" + relative.Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using var output = entry.Open();
                input.CopyTo(output);
                included++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var infoEntry = archive.CreateEntry("diagnostic-info.txt", CompressionLevel.Fastest);
        using (var writer = new StreamWriter(infoEntry.Open(), new UTF8Encoding(false)))
        {
            writer.WriteLine($"Tool TikTok diagnostic export");
            writer.WriteLine($"Version: {AppVersionInfo.Display}");
            writer.WriteLine($"Exported local time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            writer.WriteLine($"Manager PID: {Environment.ProcessId}");
            writer.WriteLine($"Base directory: {_baseDir}");
            writer.WriteLine($"Log directory: {ToolLogDirectory}");
            writer.WriteLine($"Included files: {included}");
            writer.WriteLine($"Skipped files: {skipped.Count}");

            if (skipped.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Skipped details:");
                foreach (var item in skipped)
                    writer.WriteLine("- " + item);
            }
        }

        return (included, skipped);
    }
}
