using System.Text;

namespace ToolTikTokManagerV13;

internal static class ManagerProcessDiagnostics
{
    static readonly object Sync = new();

    static string DiagnosticDirectory
        => Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            "logs",
            "diagnostic");

    static string DiagnosticPath
        => Path.Combine(DiagnosticDirectory, "manager-profile-lifecycle.log");

    public static void Append(string line)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DiagnosticDirectory);
                RotateIfNeeded(DiagnosticPath, 4 * 1024 * 1024);
                File.AppendAllText(
                    DiagnosticPath,
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Chẩn đoán tuyệt đối không được làm gián đoạn Manager.
        }
    }

    public static string OneLine(string? value, int maxChars = 7000)
    {
        value ??= "";
        var compact = value
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace("\r", " | ", StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal)
            .Trim();

        return compact.Length <= maxChars
            ? compact
            : compact[..maxChars] + "...<truncated>";
    }

    public static string ExceptionOneLine(Exception? ex)
        => ex is null ? "" : OneLine(ex.ToString());

    static void RotateIfNeeded(string path, long maxBytes)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < maxBytes)
                return;

            File.Move(path, path + ".previous", overwrite: true);
        }
        catch
        {
        }
    }
}
