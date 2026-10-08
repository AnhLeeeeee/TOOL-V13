using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CommentVisibilityMonitor;

internal sealed class CommentCheckHistoryStore
{
    readonly string _dataDir;
    readonly string _historyPath;
    readonly object _gate = new();
    readonly Action<string>? _log;

    static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(3);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public CommentCheckHistoryStore(string dataDir, Action<string>? log = null)
    {
        _dataDir = dataDir;
        _historyPath = Path.Combine(_dataDir, "comment_check_history.jsonl");
        _log = log;
    }

    public string HistoryPath => _historyPath;

    public void Append(CommentCheckSessionHistory session)
    {
        Directory.CreateDirectory(_dataDir);
        var line = JsonSerializer.Serialize(session, JsonOptions);

        lock (_gate)
        {
            File.AppendAllText(
                _historyPath,
                line + Environment.NewLine,
                new UTF8Encoding(false));

            // Tool có thể chạy liên tục nhiều ngày, nên dọn ngay sau mỗi phiên mới.
            // Cleanup fail không được làm mất phiên vừa append hoặc làm crash Check CMT.
            TryCleanupExpiredLocked("after_append");
        }
    }

    public List<CommentCheckSessionHistory> LoadAll()
    {
        lock (_gate)
        {
            // Load module / mở cửa sổ lịch sử cũng tự dọn record quá 72 giờ.
            TryCleanupExpiredLocked("load");
            return LoadAllLocked();
        }
    }

    List<CommentCheckSessionHistory> LoadAllLocked()
    {
        if (!File.Exists(_historyPath))
            return new List<CommentCheckSessionHistory>();

        var result = new List<CommentCheckSessionHistory>();
        var lineNumber = 0;

        foreach (var line in File.ReadLines(_historyPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var item = JsonSerializer.Deserialize<CommentCheckSessionHistory>(
                    line,
                    JsonOptions);

                if (item is not null)
                    result.Add(item);
            }
            catch (Exception ex)
            {
                Warn(
                    $"[HISTORY_LOAD_INVALID_JSON] line={lineNumber} " +
                    $"detail={OneLine(ex.Message)}");
            }
        }

        return result;
    }

    void TryCleanupExpiredLocked(string trigger)
    {
        try
        {
            CleanupExpiredLocked(trigger);
        }
        catch (Exception ex)
        {
            Warn(
                $"[HISTORY_RETENTION_CLEANUP_WARN] trigger={trigger} " +
                $"detail={OneLine(ex.Message)}");

            try
            {
                var temp = _historyPath + ".tmp";
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { }
        }
    }

    void CleanupExpiredLocked(string trigger)
    {
        if (!File.Exists(_historyPath))
            return;

        var cutoff = DateTimeOffset.Now - HistoryRetention;
        var keptLines = new List<string>();
        var expired = 0;
        var invalid = 0;
        var unknownTimestamp = 0;
        var changed = false;
        var lineNumber = 0;

        foreach (var line in File.ReadLines(_historyPath, Encoding.UTF8))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                changed = true;
                continue;
            }

            CommentCheckSessionHistory? item;
            try
            {
                item = JsonSerializer.Deserialize<CommentCheckSessionHistory>(
                    line,
                    JsonOptions);
            }
            catch (Exception ex)
            {
                invalid++;
                changed = true;
                Warn(
                    $"[HISTORY_RETENTION_INVALID_JSON] trigger={trigger} " +
                    $"line={lineNumber} detail={OneLine(ex.Message)}");
                continue;
            }

            if (item is null)
            {
                invalid++;
                changed = true;
                Warn(
                    $"[HISTORY_RETENTION_INVALID_JSON] trigger={trigger} " +
                    $"line={lineNumber} detail=deserialize_null");
                continue;
            }

            if (!TryGetRetentionTimestamp(item, line, out var timestamp))
            {
                // Không đoán/xóa record legacy nếu hoàn toàn không có mốc thời gian.
                unknownTimestamp++;
                keptLines.Add(line);
                continue;
            }

            if (timestamp < cutoff)
            {
                expired++;
                changed = true;
                continue;
            }

            keptLines.Add(line);
        }

        if (!changed)
            return;

        Directory.CreateDirectory(_dataDir);
        var tempPath = _historyPath + ".tmp";

        File.WriteAllLines(
            tempPath,
            keptLines,
            new UTF8Encoding(false));

        // Cùng thư mục/volume: file cũ chỉ bị thay sau khi temp đã ghi xong.
        File.Move(
            tempPath,
            _historyPath,
            overwrite: true);

        Warn(
            $"[HISTORY_RETENTION_CLEANUP] trigger={trigger} " +
            $"retentionHours=72 kept={keptLines.Count} expired={expired} " +
            $"invalid={invalid} unknownTimestamp={unknownTimestamp} " +
            $"cutoff={cutoff:O}");
    }

    static bool TryGetRetentionTimestamp(
        CommentCheckSessionHistory item,
        string rawJson,
        out DateTimeOffset timestamp)
    {
        if (item.EndedAt != default)
        {
            timestamp = item.EndedAt;
            return true;
        }

        if (item.StartedAt != default)
        {
            timestamp = item.StartedAt;
            return true;
        }

        // Fallback cho schema cũ nếu từng lưu CreatedAt nhưng model hiện tại không có field này.
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (TryReadDateTimeOffset(doc.RootElement, "CreatedAt", out timestamp))
                return true;
        }
        catch
        {
            // JSON đã deserialize hợp lệ ở trên; fallback metadata lỗi thì chỉ bỏ qua timestamp.
        }

        timestamp = default;
        return false;
    }

    static bool TryReadDateTimeOffset(
        JsonElement root,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return DateTimeOffset.TryParse(text, out value);
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var unix))
        {
            try
            {
                value = unix > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }
            catch { }
        }

        return false;
    }

    void Warn(string text)
    {
        try { _log?.Invoke(text); } catch { }
    }

    static string OneLine(string? value, int max = 220)
    {
        value ??= "";
        value = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return value.Length <= max
            ? value
            : value[..max];
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_historyPath)) File.Delete(_historyPath);
        }
    }

    public string ExportZip()
    {
        var sessions = LoadAll()
            .OrderBy(x => x.StartedAt)
            .ToList();

        Directory.CreateDirectory(_dataDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var temp = Path.Combine(_dataDir, "history_export_" + stamp + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(temp);
        try
        {
            WriteSummaryCsv(Path.Combine(temp, "summary.csv"), sessions);
            WriteContentSummaryCsv(Path.Combine(temp, "content_summary.csv"), sessions);
            WriteSessionsCsv(Path.Combine(temp, "sessions.csv"), sessions);
            WriteCommentsCsv(Path.Combine(temp, "comments.csv"), sessions);
            File.WriteAllText(
                Path.Combine(temp, "history.json"),
                JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            var zip = Path.Combine(_dataDir, $"CommentCheck_History_{stamp}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(temp, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zip;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    static void WriteSummaryCsv(string path, IReadOnlyList<CommentCheckSessionHistory> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PRF,TaiKhoan,SoPhien,TongCMT,Hien,Mat,KhongRo,TyLeHien");
        foreach (var g in sessions.GroupBy(x => new { x.Profile, x.Username })
                                  .OrderBy(x => x.Key.Profile, StringComparer.OrdinalIgnoreCase))
        {
            var visible = g.Sum(x => x.Visible);
            var missing = g.Sum(x => x.Missing);
            var unknown = g.Sum(x => x.Unknown);
            var known = visible + missing;
            var rate = known > 0 ? visible * 100.0 / known : (double?)null;
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(g.Key.Profile), Csv(g.Key.Username), g.Count().ToString(),
                g.Sum(x => x.Resolved).ToString(), visible.ToString(), missing.ToString(), unknown.ToString(),
                rate.HasValue ? rate.Value.ToString("0.0") + "%" : ""
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }


    static void WriteContentSummaryCsv(string path, IReadOnlyList<CommentCheckSessionHistory> sessions)
    {
        var comments = sessions.SelectMany(s => s.Comments.Select(c => new { Session = s, Comment = c })).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("NoiDung,Tong,Hien,Mat,KhongRo,TyLeHien,SoPRF");
        foreach (var g in comments.GroupBy(x => x.Comment.Content ?? "", StringComparer.Ordinal)
                                  .OrderByDescending(x => x.Count()))
        {
            var visible = g.Count(x => x.Comment.Result.Equals("Visible", StringComparison.OrdinalIgnoreCase));
            var missing = g.Count(x => x.Comment.Result.Equals("Missing", StringComparison.OrdinalIgnoreCase));
            var unknown = g.Count() - visible - missing;
            var known = visible + missing;
            var rate = known > 0 ? visible * 100.0 / known : (double?)null;
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(g.Key), g.Count().ToString(), visible.ToString(), missing.ToString(), unknown.ToString(),
                rate.HasValue ? rate.Value.ToString("0.0") + "%" : "",
                g.Select(x => x.Session.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString()
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static void WriteSessionsCsv(string path, IReadOnlyList<CommentCheckSessionHistory> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BatDau,KetThuc,PRF,TaiKhoan,OpenSessionId,ProfileOpenedAt,ThoiLuongGiay,DaGui,DaCheck,Hien,Mat,KhongRo,TyLeHien,LoaiPhien,LyDoKetThuc");
        foreach (var x in sessions)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(x.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(x.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(x.Profile), Csv(x.Username), Csv(x.OpenSessionId),
                Csv(x.ProfileOpenedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                x.DurationSeconds.ToString("0"), x.Sent.ToString(), x.Resolved.ToString(),
                x.Visible.ToString(), x.Missing.ToString(), x.Unknown.ToString(),
                x.VisibilityRate.HasValue ? x.VisibilityRate.Value.ToString("0.0") + "%" : "",
                x.Manual ? "CHECK_NGAY" : "AUTO", Csv(x.EndReason)
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static void WriteCommentsCsv(string path, IReadOnlyList<CommentCheckSessionHistory> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ThoiGian,SessionId,PRF,TaiKhoan,ContentIndex,SendId,NoiDung,KetQua,Mode,LiveUrl,LatencyGiay,TimeoutGiay");
        foreach (var s in sessions)
        {
            foreach (var c in s.Comments.OrderBy(x => x.Timestamp))
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(c.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")), Csv(s.SessionId), Csv(s.Profile), Csv(s.Username),
                    c.ContentIndex.ToString(), c.SendId.ToString(), Csv(c.Content), Csv(c.Result), Csv(c.MatchMode), Csv(c.LiveUrl),
                    c.LatencySeconds?.ToString("0.00") ?? "", c.TimeoutSeconds.ToString("0.0")
                }));
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains('"')) value = value.Replace("\"", "\"\"");
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value + "\"" : value;
    }
}

internal sealed class CommentCheckSessionHistory
{
    public string SessionId { get; set; } = "";
    public string OpenSessionId { get; set; } = "";
    public DateTimeOffset? ProfileOpenedAt { get; set; }
    public string Profile { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int Sent { get; set; }
    public int Resolved { get; set; }
    public int Visible { get; set; }
    public int Missing { get; set; }
    public int Unknown { get; set; }
    public double? VisibilityRate { get; set; }
    public string EndReason { get; set; } = "";
    public bool Manual { get; set; }
    public List<CommentCheckCommentHistory> Comments { get; set; } = new();
}

internal sealed class CommentCheckCommentHistory
{
    public DateTimeOffset Timestamp { get; set; }
    public long SendId { get; set; }
    public int ContentIndex { get; set; }
    public string Username { get; set; } = "";
    public string Content { get; set; } = "";
    public string Result { get; set; } = "";
    public string MatchMode { get; set; } = "";
    public string LiveUrl { get; set; } = "";
    public double? LatencySeconds { get; set; }
    public double TimeoutSeconds { get; set; }
}
