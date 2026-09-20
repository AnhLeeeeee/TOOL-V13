using System.Diagnostics;
using System.Text;

namespace ToolTikTokManagerV13.Proxy;

/// <summary>
/// Nhật ký riêng cho module Proxy. Tách khỏi log Manager để khi Proxy có sự cố
/// có thể xem lại nhanh mà không phải lọc hàng nghìn dòng log khác.
/// </summary>
public sealed class ProxyDiagnostics
{
    readonly object _sync = new();
    readonly string _logDirectory;

    public ProxyDiagnostics(string baseDir)
    {
        _logDirectory = Path.Combine(Path.GetFullPath(baseDir), "proxy_data", "logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public void Write(string eventName, string? detail = null)
    {
        try
        {
            var now = DateTime.Now;
            var safeEvent = Clean(eventName, 80);
            var safeDetail = Clean(detail, 1200);
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{safeEvent}]" + (safeDetail.Length == 0 ? "" : " " + safeDetail);
            var path = Path.Combine(_logDirectory, $"proxy-manager-{now:yyyy-MM-dd}.log");
            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                TrimIfNeeded(path);
            }
        }
        catch
        {
            // Nhật ký không bao giờ được làm hỏng luồng Proxy chính.
        }
    }

    public string ReadRecentText(int maxLines = 500)
    {
        maxLines = Math.Clamp(maxLines, 20, 2000);
        try
        {
            var files = new DirectoryInfo(_logDirectory)
                .GetFiles("proxy-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            var newestFirst = new List<string>(maxLines);
            foreach (var file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file.FullName, Encoding.UTF8); }
                catch { continue; }

                for (var i = lines.Length - 1; i >= 0 && newestFirst.Count < maxLines; i--)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i])) newestFirst.Add(lines[i]);
                }
                if (newestFirst.Count >= maxLines) break;
            }
            return string.Join(Environment.NewLine, newestFirst);
        }
        catch (Exception ex)
        {
            return "Không đọc được nhật ký Proxy: " + ex.Message;
        }
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logDirectory}\"") { UseShellExecute = true });
        }
        catch { }
    }

    static string Clean(string? value, int max)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    static void TrimIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 2 * 1024 * 1024) return;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var keep = lines.Length <= 1200 ? lines : lines[^1200..];
            File.WriteAllLines(path, keep, new UTF8Encoding(false));
        }
        catch { }
    }
}
