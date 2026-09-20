using System.Text;

namespace ToolTikTokV11.Services;

internal static class ProxyWorkerDiagnostics
{
    static readonly object Sync = new();

    public static void Write(ProxyLaunchProfile? config, string eventName, string? detail = null)
    {
        var dir = config?.DiagnosticsDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            var now = DateTime.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{Clean(eventName, 80)}]";
            var safeDetail = Clean(detail, 1200);
            if (safeDetail.Length > 0) line += " " + safeDetail;
            var path = Path.Combine(dir, $"proxy-worker-{Environment.ProcessId}-{now:yyyy-MM-dd}.log");
            lock (Sync)
            {
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Không để chức năng nhật ký tác động đến luồng chạy Worker.
        }
    }

    static string Clean(string? value, int max)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }
}
