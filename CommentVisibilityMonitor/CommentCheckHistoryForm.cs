using System.Diagnostics;

namespace CommentVisibilityMonitor;

internal sealed class CommentCheckHistoryForm : Form
{
    readonly CommentCheckHistoryStore _store;
    readonly Action<string> _log;
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    readonly Label _summary = new() { AutoSize = true, Margin = new Padding(8, 8, 8, 0) };
    List<CommentCheckSessionHistory> _sessions = new();

    public CommentCheckHistoryForm(CommentCheckHistoryStore store, Action<string> log)
    {
        _store = store;
        _log = log;
        Text = "Lịch sử Check CMT";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1100;
        Height = 650;
        MinimumSize = new Size(850, 500);
        Font = new Font("Segoe UI", 9F);

        _grid.Columns.Add("Start", "Bắt đầu");
        _grid.Columns.Add("Profile", "PRF");
        _grid.Columns.Add("Username", "Tài khoản");
        _grid.Columns.Add("Duration", "Thời lượng");
        _grid.Columns.Add("Resolved", "Đã check");
        _grid.Columns.Add("Visible", "Hiện");
        _grid.Columns.Add("Missing", "Mất");
        _grid.Columns.Add("Unknown", "Không rõ");
        _grid.Columns.Add("Rate", "Tỷ lệ phiên");
        _grid.Columns.Add("ProfileAverage", "TB PRF");
        _grid.Columns.Add("Reason", "Kết thúc");
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenDetails(e.RowIndex); };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), WrapContents = false };
        var refresh = new Button { Text = "Tải lại", AutoSize = true };
        var export = new Button { Text = "XUẤT LỊCH SỬ", AutoSize = true };
        var clear = new Button { Text = "XÓA LỊCH SỬ", AutoSize = true };
        var close = new Button { Text = "Đóng", AutoSize = true };
        refresh.Click += (_, _) => Reload();
        export.Click += (_, _) => Export();
        clear.Click += (_, _) => ClearHistory();
        close.Click += (_, _) => Close();
        toolbar.Controls.AddRange(new Control[] { refresh, export, clear, close, _summary });

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Shown += (_, _) => Reload();
    }

    void Reload()
    {
        _sessions = _store.LoadAll().OrderByDescending(x => x.StartedAt).ToList();
        var profileAverages = _sessions
            .GroupBy(x => x.Profile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var visible = g.Sum(x => x.Visible);
                    var missing = g.Sum(x => x.Missing);
                    var known = visible + missing;
                    return known > 0 ? visible * 100.0 / known : (double?)null;
                },
                StringComparer.OrdinalIgnoreCase);

        _grid.Rows.Clear();
        foreach (var s in _sessions)
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, s.DurationSeconds));
            profileAverages.TryGetValue(s.Profile, out var profileAverage);
            var rowIndex = _grid.Rows.Add(
                s.StartedAt.ToLocalTime().ToString("HH:mm:ss dd/MM"),
                s.Profile,
                s.Username,
                $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}",
                s.Resolved,
                s.Visible,
                s.Missing,
                s.Unknown,
                s.VisibilityRate.HasValue ? s.VisibilityRate.Value.ToString("0.0") + "%" : "—",
                profileAverage.HasValue ? profileAverage.Value.ToString("0.0") + "%" : "—",
                s.EndReason);
            _grid.Rows[rowIndex].Tag = s;
        }

        var visible = _sessions.Sum(x => x.Visible);
        var missing = _sessions.Sum(x => x.Missing);
        var unknown = _sessions.Sum(x => x.Unknown);
        var known = visible + missing;
        _summary.Text = $"{_sessions.Count} phiên | CMT: {visible + missing + unknown} | Hiện: {visible} | Mất: {missing} | Không rõ: {unknown} | Tỷ lệ: {(known > 0 ? (visible * 100.0 / known).ToString("0.0") + "%" : "—")}";
    }

    void OpenDetails(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _sessions.Count) return;
        if (_grid.Rows[rowIndex].Tag is not CommentCheckSessionHistory session) return;
        using var form = new Form
        {
            Text = $"Chi tiết {session.Profile} — {session.StartedAt.ToLocalTime():HH:mm:ss dd/MM}",
            StartPosition = FormStartPosition.CenterParent,
            Width = 1150,
            Height = 620,
            Font = new Font("Segoe UI", 9F)
        };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add("Time", "Thời gian");
        grid.Columns.Add("Index", "CMT");
        grid.Columns.Add("Content", "Nội dung");
        grid.Columns.Add("Result", "Kết quả");
        grid.Columns.Add("Mode", "Mode");
        grid.Columns.Add("Live", "LIVE");
        foreach (var c in session.Comments.OrderBy(x => x.Timestamp))
            grid.Rows.Add(c.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"), c.ContentIndex, c.Content, TranslateResult(c.Result), c.MatchMode, c.LiveUrl);
        form.Controls.Add(grid);
        form.ShowDialog(this);
    }

    void Export()
    {
        try
        {
            var zip = _store.ExportZip();
            _log("[HISTORY_EXPORT_OK] " + zip);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{zip}\"", UseShellExecute = true });
            }
            catch { }
            MessageBox.Show(this, "Đã xuất lịch sử Check CMT:\n" + zip, "Xuất lịch sử", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log("[HISTORY_EXPORT_ERROR] " + ex);
            MessageBox.Show(this, "Không xuất được lịch sử:\n" + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ClearHistory()
    {
        var answer = MessageBox.Show(this, "Xóa toàn bộ lịch sử Check CMT đã lưu?", "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        try
        {
            _store.Clear();
            _log("[HISTORY_CLEAR] user_confirmed=true");
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không xóa được lịch sử:\n" + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string TranslateResult(string value)
        => value.Equals("Visible", StringComparison.OrdinalIgnoreCase) ? "HIỆN"
         : value.Equals("Missing", StringComparison.OrdinalIgnoreCase) ? "MẤT"
         : "KHÔNG RÕ";
}
