using System.Text;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly object _persistentWorkerDiagnosticLock = new();
    bool _persistentWorkerDiagnosticInitialized;

    void InitializePersistentWorkerDiagnostics()
    {
        if (_persistentWorkerDiagnosticInitialized)
            return;

        _persistentWorkerDiagnosticInitialized = true;

        try
        {
            AppendPersistentWorkerDiagnostic(
                $"=== WORKER START {DateTime.Now:yyyy-MM-dd HH:mm:ss} | profile={DiagnosticProfileName()} | pid={Environment.ProcessId} ===");
        }
        catch { }

        _log.LineWritten += CapturePersistentWorkerDiagnosticLine;
    }

    void CapturePersistentWorkerDiagnosticLine(string line)
    {
        if (!ShouldCapturePersistentWorkerDiagnosticLine(line))
            return;

        AppendPersistentWorkerDiagnostic(line);
    }

    static bool ShouldCapturePersistentWorkerDiagnosticLine(string? line)
    {
        line ??= "";
        if (line.Length == 0)
            return false;

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] important =
        [
            "[TIKTOK_LOGIN",
            "[TIKTOK_STARTUP",
            "[CHROME_CLOSE",
            "[CAPTCHA",
            "[CDP_SESSION",
            "Launching Chrome",
            "Đã mở Chrome"
        ];

        return important.Any(token =>
            line.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    string DiagnosticProfileName()
    {
        var profile = (_startupOptions.ProfileName ?? "").Trim();
        return profile.Length == 0 ? "standalone" : profile;
    }

    string PersistentWorkerDiagnosticPath()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeProfile = new string(DiagnosticProfileName()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());

        return Path.Combine(
            AppContext.BaseDirectory,
            "logs",
            "diagnostic",
            $"worker-profile-{safeProfile}.log");
    }

    void AppendPersistentWorkerDiagnostic(string line)
    {
        try
        {
            lock (_persistentWorkerDiagnosticLock)
            {
                var path = PersistentWorkerDiagnosticPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length >= 3 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);

                File.AppendAllText(
                    path,
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Không cho nhật ký phụ ảnh hưởng Worker/Chrome.
        }
    }
}
