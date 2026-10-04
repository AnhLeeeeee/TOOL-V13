using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CommentVisibilityMonitor;

internal static class DiagnosticExporter
{
    public static string Export(string dataDir, string observerCoreLogDir, object snapshot)
    {
        dataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(dataDir);

        var exportDir = Path.Combine(dataDir, "DiagnosticExports");
        Directory.CreateDirectory(exportDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tempDir = Path.Combine(exportDir, ".tmp_" + stamp + "_" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(exportDir, $"CommentCheck_Diagnostic_{stamp}.zip");

        Directory.CreateDirectory(tempDir);
        try
        {
            WriteText(Path.Combine(tempDir, "diagnostic_state.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

            WriteText(Path.Combine(tempDir, "environment.txt"), BuildEnvironmentText());

            SafeCopyFile(Path.Combine(dataDir, "monitor.log"), Path.Combine(tempDir, "monitor.log"));
            SafeCopyFile(Path.Combine(dataDir, "results.jsonl"), Path.Combine(tempDir, "results.jsonl"));
            SafeCopyFile(Path.Combine(dataDir, "crash.log"), Path.Combine(tempDir, "crash.log"));

            var coreOut = Path.Combine(tempDir, "ObserverChromeCore");
            SafeCopyLogDirectory(observerCoreLogDir, coreOut);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    public static void AppendCrash(string dataDir, string source, Exception? ex, string? extra = null)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] source={source}");
            if (!string.IsNullOrWhiteSpace(extra)) sb.AppendLine(extra);
            if (ex is not null) sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 80));
            File.AppendAllText(Path.Combine(dataDir, "crash.log"), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    static string BuildEnvironmentText()
    {
        var asm = typeof(DiagnosticExporter).Assembly.GetName();
        var sb = new StringBuilder();
        sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        sb.AppendLine($"App: {asm.Name}");
        sb.AppendLine($"AppVersion: {asm.Version}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"64BitProcess: {Environment.Is64BitProcess}");
        sb.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
        return sb.ToString();
    }

    static void WriteText(string path, string text)
    {
        try { File.WriteAllText(path, text ?? "", new UTF8Encoding(false)); } catch { }
    }

    static void SafeCopyLogDirectory(string sourceDir, string destDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(destDir);

            foreach (var src in Directory.EnumerateFiles(sourceDir, "*.log", SearchOption.TopDirectoryOnly))
            {
                var dst = Path.Combine(destDir, Path.GetFileName(src));
                SafeCopyFile(src, dst);
            }
        }
        catch { }
    }

    static void SafeCopyFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = new FileStream(
                source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write,
                FileShare.Read);
            input.CopyTo(output);
        }
        catch { }
    }
}
