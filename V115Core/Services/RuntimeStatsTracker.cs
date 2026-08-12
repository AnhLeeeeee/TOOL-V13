using System.Text.Json;
using System.Text.Json.Serialization;
using ToolTikTokV11.Utils;

namespace ToolTikTokV11.Services;

/// <summary>
/// Persists actual AutomationEngine running intervals for one Worker/DataRoot.
/// The UI only reads snapshots; interval accounting is based on UTC transition times.
/// </summary>
public sealed class RuntimeStatsTracker : IDisposable
{
    const int FileVersion = 1;
    static readonly TimeSpan CheckpointPeriod = TimeSpan.FromSeconds(30);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly object _gate = new();
    readonly string _filePath;
    readonly Logger _log;
    readonly System.Threading.Timer _checkpointTimer;

    double _totalRunSeconds;
    double _todayRunSeconds;
    double _sessionRunSeconds;
    DateOnly _todayDate;
    AutomationRunState _state = AutomationRunState.Stopped;
    DateTimeOffset? _activeSegmentStartedUtc;
    bool _disposed;

    public RuntimeStatsTracker(string dataRoot, Logger log)
    {
        _filePath = Path.Combine(dataRoot, "runtime_stats.json");
        _log = log;
        _todayDate = GetLocalDate(DateTimeOffset.UtcNow);

        lock (_gate)
        {
            var recoveredActiveRun = LoadLocked();
            if (recoveredActiveRun)
            {
                // The latest checkpoint already contains all completed time.  Do not
                // treat offline time after a crash as automation runtime.
                _log.Warn("[RUNTIME_STATS_RECOVERED] Worker trước đó kết thúc khi đang Running; đã chốt dữ liệu tại checkpoint gần nhất.");
                TrySaveLocked();
            }
        }
        _checkpointTimer = new System.Threading.Timer(_ => Checkpoint(), null, CheckpointPeriod, CheckpointPeriod);
    }

    public void ApplyEngineState(AutomationRunState nextState)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            EnsureTodayLocked(now);

            if (_state == nextState) return;

            if (_state == AutomationRunState.Running)
                AccumulateActiveIntervalLocked(now);

            if (nextState == AutomationRunState.Running)
            {
                // A transition from Stopped always creates a fresh session.  Resume
                // from Paused keeps the existing session total.
                if (_state == AutomationRunState.Stopped) _sessionRunSeconds = 0;
                _activeSegmentStartedUtc = now;
            }
            else
            {
                _activeSegmentStartedUtc = null;
            }

            _state = nextState;
            TrySaveLocked();
        }
    }

    public RuntimeStatsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            EnsureTodayLocked(now);
            var session = _sessionRunSeconds;
            var today = _todayRunSeconds;
            var total = _totalRunSeconds;

            if (_state == AutomationRunState.Running && _activeSegmentStartedUtc is { } started)
            {
                var seconds = PositiveSeconds(started, now);
                session += seconds;
                total += seconds;
                today += SecondsWithinLocalDay(started, now, _todayDate);
            }

            return new RuntimeStatsSnapshot(
                TimeSpan.FromSeconds(Math.Max(0, session)),
                TimeSpan.FromSeconds(Math.Max(0, today)),
                TimeSpan.FromSeconds(Math.Max(0, total)),
                _state == AutomationRunState.Running);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            EnsureTodayLocked(now);
            if (_state == AutomationRunState.Running) AccumulateActiveIntervalLocked(now);
            TrySaveLocked();
        }
    }

    void Checkpoint()
    {
        lock (_gate)
        {
            if (_disposed || _state != AutomationRunState.Running) return;
            var now = DateTimeOffset.UtcNow;
            EnsureTodayLocked(now);
            AccumulateActiveIntervalLocked(now);
            TrySaveLocked();
        }
    }

    void AccumulateActiveIntervalLocked(DateTimeOffset now)
    {
        if (_activeSegmentStartedUtc is not { } started) return;
        var seconds = PositiveSeconds(started, now);
        if (seconds > 0)
        {
            _totalRunSeconds += seconds;
            _sessionRunSeconds += seconds;
            _todayRunSeconds += SecondsWithinLocalDay(started, now, _todayDate);
        }
        // Advance even after a zero-duration interval, so future checkpoints stay
        // bounded and a future clock correction cannot create a negative delta.
        if (now >= started) _activeSegmentStartedUtc = now;
    }

    bool LoadLocked()
    {
        if (!File.Exists(_filePath)) return false;
        try
        {
            var document = JsonSerializer.Deserialize<RuntimeStatsDocument>(File.ReadAllText(_filePath));
            if (document is null) return false;

            _totalRunSeconds = Math.Max(0, document.TotalRunSeconds);
            _todayRunSeconds = Math.Max(0, document.TodayRunSeconds);
            if (!DateOnly.TryParse(document.TodayDate, out _todayDate))
                _todayDate = GetLocalDate(DateTimeOffset.UtcNow);
            EnsureTodayLocked(DateTimeOffset.UtcNow);

            // A Worker process never resumes a previous session automatically.
            _sessionRunSeconds = 0;
            _state = AutomationRunState.Stopped;
            _activeSegmentStartedUtc = null;
            return document.IsRunning;
        }
        catch (Exception ex)
        {
            _totalRunSeconds = _todayRunSeconds = _sessionRunSeconds = 0;
            _todayDate = GetLocalDate(DateTimeOffset.UtcNow);
            _state = AutomationRunState.Stopped;
            _activeSegmentStartedUtc = null;
            _log.Warn("[RUNTIME_STATS_LOAD] Không đọc được runtime_stats.json; khởi tạo thống kê trống. " + ex.Message);
            return false;
        }
    }

    void EnsureTodayLocked(DateTimeOffset now)
    {
        var current = GetLocalDate(now);
        if (_todayDate == current) return;
        _todayDate = current;
        _todayRunSeconds = 0;
    }

    void TrySaveLocked()
    {
        try
        {
            var document = new RuntimeStatsDocument
            {
                Version = FileVersion,
                TotalRunSeconds = _totalRunSeconds,
                TodayRunSeconds = _todayRunSeconds,
                TodayDate = _todayDate.ToString("yyyy-MM-dd"),
                ActiveSessionSeconds = _sessionRunSeconds,
                IsRunning = _state == AutomationRunState.Running,
                ActiveRunStartedUtc = _activeSegmentStartedUtc,
                LastCheckpointUtc = DateTimeOffset.UtcNow
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("[RUNTIME_STATS_SAVE] Không thể checkpoint runtime_stats.json: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _checkpointTimer.Dispose();
            var now = DateTimeOffset.UtcNow;
            if (_state == AutomationRunState.Running) AccumulateActiveIntervalLocked(now);
            TrySaveLocked();
            _disposed = true;
        }
    }

    static double PositiveSeconds(DateTimeOffset start, DateTimeOffset end)
        => end <= start ? 0 : (end - start).TotalSeconds;

    static DateOnly GetLocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(utc.ToLocalTime().DateTime);

    static double SecondsWithinLocalDay(DateTimeOffset start, DateTimeOffset end, DateOnly date)
    {
        if (end <= start) return 0;
        var dayStartLocal = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var nextDayStartLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var dayStartUtc = new DateTimeOffset(dayStartLocal, TimeZoneInfo.Local.GetUtcOffset(dayStartLocal)).ToUniversalTime();
        var nextDayStartUtc = new DateTimeOffset(nextDayStartLocal, TimeZoneInfo.Local.GetUtcOffset(nextDayStartLocal)).ToUniversalTime();
        var overlapStart = start > dayStartUtc ? start : dayStartUtc;
        var overlapEnd = end < nextDayStartUtc ? end : nextDayStartUtc;
        return PositiveSeconds(overlapStart, overlapEnd);
    }

    sealed class RuntimeStatsDocument
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("totalRunSeconds")] public double TotalRunSeconds { get; set; }
        [JsonPropertyName("todayRunSeconds")] public double TodayRunSeconds { get; set; }
        [JsonPropertyName("todayDate")] public string TodayDate { get; set; } = "";
        [JsonPropertyName("activeSessionSeconds")] public double ActiveSessionSeconds { get; set; }
        [JsonPropertyName("isRunning")] public bool IsRunning { get; set; }
        [JsonPropertyName("activeRunStartedUtc")] public DateTimeOffset? ActiveRunStartedUtc { get; set; }
        [JsonPropertyName("lastCheckpointUtc")] public DateTimeOffset LastCheckpointUtc { get; set; }
    }
}

public readonly record struct RuntimeStatsSnapshot(TimeSpan Session, TimeSpan Today, TimeSpan Total, bool IsRunning);
