using System.Collections.Concurrent;
using System.Text;

namespace ToolTikTokV11.Utils;

/// <summary>
/// Logger Worker tối ưu cho VM.
/// - Mỗi profile chỉ giữ tối đa 500 dòng log gần nhất trên ổ đĩa.
/// - Chia log thành các segment tối đa 50 dòng để tránh rewrite file sau mỗi dòng mới.
/// - Ghi theo buffer để tránh I/O đồng bộ trên hot path.
/// - WARN/ERROR luôn được ghi; PERF/CDP chi tiết có thể tắt bằng VerboseDiagnosticsEnabled.
/// </summary>
public sealed class Logger : IDisposable
{
    const int MaxSegmentLines = 50;
    const int MaxSavedLines = 500;
    const long MaxActiveLogBytes = 1L * 1024 * 1024;
    const long MaxTotalLogBytes = 2L * 1024 * 1024;
    static readonly TimeSpan MaxLogAge = TimeSpan.FromHours(6);
    static readonly TimeSpan BufferedFlushInterval = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    static readonly ConcurrentDictionary<string, System.Threading.Timer> CleanupTimers = new(StringComparer.OrdinalIgnoreCase);

    readonly string _dir;
    readonly object _lock = new();
    readonly System.Threading.Timer _flushTimer;
    StreamWriter? _writer;
    FileStream? _writerStream;
    string _activePath = "";
    long _activeBytes;
    int _activeLineCount;
    bool _disposed;

    public bool VerboseDiagnosticsEnabled { get; set; } = true;
    public event Action<string>? LineWritten;

    public Logger(string baseDir)
    {
        _dir = Path.GetFullPath(Path.Combine(baseDir, "logs"));
        Directory.CreateDirectory(_dir);
        CleanupLogs(_dir);
        TrimDirectoryToLatestLines(_dir, MaxSavedLines);

        _flushTimer = new System.Threading.Timer(_ => FlushBuffered(), null, BufferedFlushInterval, BufferedFlushInterval);
        ScheduleLogCleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public void Info(string text) => Write("INFO", text);
    public void Warn(string text) => Write("WARN", text);
    public void Error(string text) => Write("ERROR", text);

    public void Write(string level, string text)
    {
        if (!VerboseDiagnosticsEnabled
            && level.Equals("INFO", StringComparison.OrdinalIgnoreCase)
            && IsVerboseDiagnostic(text))
            return;

        var now = DateTime.Now;
        var line = $"[{now:HH:mm:ss}] [{level}] {text}";
        var incomingBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

        lock (_lock)
        {
            ThrowIfDisposed();
            var path = Path.Combine(_dir, $"{now:yyyy-MM-dd}.log");
            EnsureWriter(path);

            if (_activeLineCount >= MaxSegmentLines || _activeBytes + incomingBytes > MaxActiveLogBytes)
            {
                RotateActive(path);
                EnsureWriter(path);
            }

            _writer!.WriteLine(line);
            _activeBytes += incomingBytes;
            _activeLineCount++;
            if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                _writer.Flush();
        }

        LineWritten?.Invoke(line);
    }

    static bool IsVerboseDiagnostic(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.StartsWith("[PERF", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[STEP_PERF]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[LOOP_PERF]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("CDP START ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("CDP DONE ", StringComparison.OrdinalIgnoreCase);
    }

    void EnsureWriter(string path)
    {
        if (_writer is not null && path.Equals(_activePath, StringComparison.OrdinalIgnoreCase)) return;

        CloseWriterNoThrow();

        // Khi sang ngày/file mới, dành sẵn chỗ cho một segment mới để tổng log không vượt 500 dòng.
        if (!File.Exists(path))
            TrimDirectoryToLatestLines(_dir, MaxSavedLines - MaxSegmentLines);

        _writerStream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.SequentialScan);
        _activeBytes = _writerStream.Length;
        _activeLineCount = SafeCountLines(path);
        _writer = new StreamWriter(_writerStream, new UTF8Encoding(true), 16 * 1024, leaveOpen: false)
        {
            AutoFlush = false
        };
        _activePath = path;
    }

    void RotateActive(string activePath)
    {
        try
        {
            if (activePath.Equals(_activePath, StringComparison.OrdinalIgnoreCase))
                CloseWriterNoThrow();

            if (!File.Exists(activePath)) return;
            var directory = Path.GetDirectoryName(activePath)!;
            var baseName = Path.GetFileNameWithoutExtension(activePath);
            var archive = Path.Combine(directory, $"{baseName}-{DateTime.Now:HHmmss_fff}.log");
            File.Move(activePath, archive);

            // Giữ tối đa 450 dòng cũ; segment đang bắt đầu có thể thêm tối đa 50 dòng.
            TrimDirectoryToLatestLines(_dir, MaxSavedLines - MaxSegmentLines);
            CleanupLogs(_dir);
        }
        catch (FileNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static int SafeCountLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var count = 0;
            foreach (var _ in File.ReadLines(path)) count++;
            return count;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    static void TrimDirectoryToLatestLines(string directory, int maxLines)
    {
        if (maxLines < 0 || !Directory.Exists(directory)) return;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(directory)
                .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var remaining = maxLines;
        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file.FullName); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (remaining <= 0)
            {
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }

            if (lines.Length <= remaining)
            {
                remaining -= lines.Length;
                continue;
            }

            try
            {
                var kept = lines[^remaining..];
                var temp = file.FullName + ".trim.tmp";
                File.WriteAllLines(temp, kept, new UTF8Encoding(true));
                File.Move(temp, file.FullName, true);
                remaining = 0;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    void FlushBuffered()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try { _writer?.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    void CloseWriterNoThrow()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerStream = null;
        _activePath = "";
        _activeBytes = 0;
        _activeLineCount = 0;
    }

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Logger));
    }

    void ScheduleLogCleanup()
    {
        CleanupTimers.GetOrAdd(_dir, root => new System.Threading.Timer(_ =>
        {
            try { CleanupLogs(root); }
            catch { }
        }, null, CleanupInterval, CleanupInterval));
    }

    static void CleanupLogs(string logRoot)
    {
        var root = Path.GetFullPath(logRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return;

        DirectoryInfo rootInfo;
        try { rootInfo = new DirectoryInfo(root); }
        catch { return; }
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return;

        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (DateTime.UtcNow - info.LastWriteTimeUtc > MaxLogAge)
                {
                    info.Delete();
                    continue;
                }
                files.Add(info);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        TrimToSize(files, MaxTotalLogBytes);
    }

    static void TrimToSize(List<FileInfo> files, long maxBytes)
    {
        long total = 0;
        foreach (var file in files)
        {
            try { total += file.Exists ? file.Length : 0; } catch { }
        }
        if (total <= maxBytes) return;

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes) break;
            try
            {
                if (!file.Exists) continue;
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _flushTimer.Dispose(); } catch { }
            CloseWriterNoThrow();
        }
        GC.SuppressFinalize(this);
    }
}
