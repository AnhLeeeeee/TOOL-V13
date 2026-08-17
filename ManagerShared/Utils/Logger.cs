using System.Collections.Concurrent;
using System.Text;

namespace ToolTikTokV12.Utils;

/// <summary>
/// Logger Manager tối ưu cho VM.
/// - Mỗi scope log chỉ giữ tối đa 500 dòng gần nhất trên ổ đĩa.
/// - Chia log thành các segment tối đa 50 dòng để không phải rewrite file sau mỗi dòng mới.
/// - Ghi theo buffer để giảm I/O đồng bộ.
/// - Vẫn giữ cleanup theo tuổi/dung lượng làm lớp bảo vệ bổ sung.
/// </summary>
public sealed class Logger : IDisposable
{
    const int MaxSegmentLines = 50;
    const int MaxSavedLines = 500;
    const long MaxActiveLogBytes = 1L * 1024 * 1024;
    const long MaxTotalLogBytes = 4L * 1024 * 1024;
    static readonly TimeSpan MaxLogAge = TimeSpan.FromHours(6);
    static readonly TimeSpan BufferedFlushInterval = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    static readonly ConcurrentDictionary<string, System.Threading.Timer> CleanupTimers = new(StringComparer.OrdinalIgnoreCase);

    readonly string _dir;
    readonly string _logRoot;
    readonly string? _fixedFileName;
    readonly object _lock = new();
    readonly System.Threading.Timer _flushTimer;
    StreamWriter? _writer;
    FileStream? _writerStream;
    string _activePath = "";
    long _activeBytes;
    int _activeLineCount;
    bool _disposed;

    public event Action<string>? LineWritten;

    public Logger(string baseDir, string? scope = null, string? fixedFileName = null)
    {
        _logRoot = Path.GetFullPath(Path.Combine(baseDir, "logs"));
        _dir = string.IsNullOrWhiteSpace(scope) ? _logRoot : Path.Combine(_logRoot, scope);
        _fixedFileName = fixedFileName;
        Directory.CreateDirectory(_dir);

        CleanupExpiredAndOversizeLogs(_logRoot);
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
        var now = DateTime.Now;
        var line = $"[{now:HH:mm:ss}] [{level}] {text}";
        var incomingBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

        lock (_lock)
        {
            ThrowIfDisposed();
            var fileName = string.IsNullOrWhiteSpace(_fixedFileName) ? $"{now:yyyy-MM-dd}.log" : _fixedFileName;
            var path = Path.Combine(_dir, fileName);
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

    void EnsureWriter(string path)
    {
        if (_writer is not null && path.Equals(_activePath, StringComparison.OrdinalIgnoreCase)) return;

        CloseWriterNoThrow();

        // Khi sang file/ngày mới, dành sẵn chỗ cho một segment mới để tổng log không vượt 500 dòng.
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
            var archive = Path.Combine(directory, $"{baseName}-{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
            File.Move(activePath, archive);

            // Giữ tối đa 450 dòng cũ; segment đang bắt đầu có thể thêm tối đa 50 dòng.
            TrimDirectoryToLatestLines(_dir, MaxSavedLines - MaxSegmentLines);
            CleanupExpiredAndOversizeLogs(_logRoot);
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

            // File giao ranh chỉ giữ phần mới nhất cần thiết, các file cũ hơn sẽ bị xóa.
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
        CleanupTimers.GetOrAdd(_logRoot, root => new System.Threading.Timer(_ =>
        {
            try { CleanupExpiredAndOversizeLogs(root); }
            catch { }
        }, null, CleanupInterval, CleanupInterval));
    }

    internal static (int Deleted, long FreedBytes) CleanupExpiredAndOversizeLogs(string logRoot)
    {
        var root = Path.GetFullPath(logRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return (0, 0);

        DirectoryInfo rootInfo;
        try { rootInfo = new DirectoryInfo(root); }
        catch { return (0, 0); }
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return (0, 0);

        var files = EnumerateToolLogFiles(root).ToList();
        var deleted = 0;
        long freed = 0;

        foreach (var file in files.ToArray())
        {
            try
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc <= MaxLogAge) continue;
                var length = file.Length;
                file.Delete();
                files.Remove(file);
                deleted++;
                freed += length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        long total = 0;
        foreach (var file in files)
        {
            try { total += file.Exists ? file.Length : 0; } catch { }
        }

        if (total > MaxTotalLogBytes)
        {
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxTotalLogBytes) break;
                try
                {
                    if (!file.Exists) continue;
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    deleted++;
                    freed += length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return (deleted, freed);
    }

    static IEnumerable<FileInfo> EnumerateToolLogFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            DirectoryInfo directory;
            try { directory = new DirectoryInfo(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            FileInfo[] files;
            DirectoryInfo[] subdirectories;
            try
            {
                files = directory.GetFiles("*.log", SearchOption.TopDirectoryOnly);
                subdirectories = directory.GetDirectories("*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                    yield return file;

            foreach (var subdirectory in subdirectories)
                if ((subdirectory.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(subdirectory.FullName);
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
