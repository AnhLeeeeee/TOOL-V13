using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CommentVisibilityMonitor;

internal sealed class CommentCheckHistoryStore
{
    readonly string _dataDir;
    readonly string _historyPath;
    readonly object _gate = new();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public CommentCheckHistoryStore(string dataDir)
    {
        _dataDir = dataDir;
        _historyPath = Path.Combine(_dataDir, "comment_check_history.jsonl");
    }

    public string HistoryPath => _historyPath;

    public void Append(CommentCheckSessionHistory session)
    {
        Directory.CreateDirectory(_dataDir);
        var line = JsonSerializer.Serialize(session, JsonOptions);
        lock (_gate)
            File.AppendAllText(_historyPath, line + Environment.NewLine, new UTF8Encoding(false));
    }

    public List<CommentCheckSessionHistory> LoadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_historyPath)) return new List<CommentCheckSessionHistory>();
            var result = new List<CommentCheckSessionHistory>();
            foreach (var line in File.ReadLines(_historyPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<CommentCheckSessionHistory>(line, JsonOptions);
                    if (item is not null) result.Add(item);
                }
                catch
                {
                    // Một dòng hỏng không được làm mất khả năng xem/xuất các phiên còn lại.
                }
            }
            return result;
        }
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
        sb.AppendLine("BatDau,KetThuc,PRF,TaiKhoan,ThoiLuongGiay,DaGui,DaCheck,Hien,Mat,KhongRo,TyLeHien,LoaiPhien,LyDoKetThuc");
        foreach (var x in sessions)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(x.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(x.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(x.Profile), Csv(x.Username), x.DurationSeconds.ToString("0"), x.Sent.ToString(), x.Resolved.ToString(),
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
