using System.Runtime.CompilerServices;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _dashboardCompactPasteInstalled;
    bool _dashboardMergedPaintHooked;

    // Gộp Tổng quan + Thống kê thành một Dashboard duy nhất.
    [ModuleInitializer]
    internal static void BootstrapDashboardCompactPaste()
    {
        EventHandler? idleHandler = null;
        idleHandler = (_, _) =>
        {
            foreach (Form openForm in Application.OpenForms)
            {
                if (openForm is not ManagerForm manager) continue;
                manager.InstallDashboardCompactPaste();
                if (idleHandler is not null)
                    Application.Idle -= idleHandler;
                break;
            }
        };
        Application.Idle += idleHandler;
    }

    void InstallDashboardCompactPaste()
    {
        if (_dashboardCompactPasteInstalled) return;
        _dashboardCompactPasteInstalled = true;

        RemoveLegacyStatisticsTab();
        ApplyDashboardCompactPaste();

        // Handler được gắn sau handler RefreshDashboard có sẵn, nên mỗi tick
        // Dashboard được cập nhật trước rồi mới bổ sung Session/Total/Vòng giờ/Progress.
        _refreshTimer.Tick += (_, _) =>
        {
            RemoveLegacyStatisticsTab();
            ApplyDashboardCompactPaste();
        };

        if (_dashboardTab is not null && !_dashboardTab.IsDisposed)
            _dashboardTab.Enter += (_, _) => ApplyDashboardCompactPaste();
    }

    void RemoveLegacyStatisticsTab()
    {
        try
        {
            if (_statisticsTab is not null && !_statisticsTab.IsDisposed)
            {
                if (_statisticsTab.Parent == _tabs)
                    _tabs.TabPages.Remove(_statisticsTab);

                _statisticsTab.Dispose();
            }
        }
        catch { }

        _statisticsTab = null;
        _statisticsGrid = null;
        _statisticsSummary = null;
        _statisticsShowAllProfilesToggle = null;
    }

    void ApplyDashboardCompactPaste()
    {
        var grid = _dashboardGrid;
        if (grid is null || grid.IsDisposed) return;
        if (_dashboardTab is not null && !ReferenceEquals(_tabs.SelectedTab, _dashboardTab)) return;

        RenameDashboardHeading();
        EnsureMergedDashboardColumns(grid);
        EnsureMergedDashboardPaintHook(grid);

        // Các cột kỹ thuật cũ không còn cần hiển thị trực tiếp.
        HideDashboardColumn(grid, "Chrome");
        HideDashboardColumn(grid, "Step");
        HideDashboardColumn(grid, "Ram");
        HideDashboardColumn(grid, "Detail");
        HideDashboardColumn(grid, "RunTime");
        HideDashboardColumn(grid, "Rounds");

        ConfigureMergedDashboardColumnOrder(grid);

        var rowStats = new List<(DataGridViewRow Row, StatisticsRuntimeSnapshot Stats, double? Rate)>();

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow || row.Tag is not ProfileContext ctx) continue;

            var stats = ReadStatisticsRuntime(ctx);
            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
            catch { }

            long? rounds = workerAlive ? ctx.LastSnapshot?.Rounds : null;
            var rate = CalculateRoundsPerHour(rounds, stats.Session);
            rowStats.Add((row, stats, rate));
        }

        var eligibleRates = rowStats
            .Where(x => x.Stats.Session >= TimeSpan.FromMinutes(5) && x.Rate is >= 0)
            .Select(x => x.Rate!.Value)
            .OrderBy(x => x)
            .ToList();
        var medianRate = CalculateMedian(eligibleRates);

        foreach (var item in rowStats)
        {
            var row = item.Row;
            var ctx = (ProfileContext)row.Tag!;

            SetCompactCellIfChanged(row, "SessionRuntime", FormatMergedRuntime(item.Stats.Session));
            SetCompactCellIfChanged(row, "TotalRuntime", FormatMergedRuntime(item.Stats.Total));
            SetCompactCellIfChanged(row, "RoundsPerHour", FormatRoundsPerHour(item.Rate));

            var progress = BuildDashboardCompactProgress(row);
            SetCompactCellIfChanged(row, "Progress", progress);

            ApplyDashboardCompactRowStyle(row, progress);
            ApplyMergedRuntimeStyles(row);

            if (row.DataGridView?.Columns.Contains("RoundsPerHour") == true)
            {
                var rateCell = row.Cells["RoundsPerHour"];
                rateCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                rateCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

                // Reset trước khi ApplyPerformanceStyle để màu luôn phản ánh dữ liệu hiện tại.
                rateCell.Style.BackColor = Color.White;
                rateCell.Style.ForeColor = Color.FromArgb(55, 76, 103);
                rateCell.ToolTipText = "";
                ApplyPerformanceStyle(rateCell, item.Rate, item.Stats.Session, medianRate);
            }
        }

        UpdateMergedDashboardSummary(rowStats, eligibleRates);
    }

    void RenameDashboardHeading()
    {
        var tab = _dashboardTab;
        if (tab is null || tab.IsDisposed) return;

        foreach (var label in FindControlsRecursive<Label>(tab))
        {
            if (label.Text.StartsWith("TỔNG QUAN HỆ THỐNG", StringComparison.OrdinalIgnoreCase))
            {
                label.Text = $"TỔNG QUAN & HIỆU SUẤT — V{ManagerDisplayVersion}";
                break;
            }
        }
    }

    static IEnumerable<T> FindControlsRecursive<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControlsRecursive<T>(child))
                yield return nested;
        }
    }

    void EnsureMergedDashboardColumns(DataGridView grid)
    {
        if (!grid.Columns.Contains("SessionRuntime"))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SessionRuntime",
                HeaderText = "Phiên hiện tại",
                ReadOnly = true,
                Width = 125,
                ToolTipText = "Thời gian RUNNING của phiên Automation hiện tại."
            });
        }

        if (!grid.Columns.Contains("TotalRuntime"))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "TotalRuntime",
                HeaderText = "Tổng thời gian",
                ReadOnly = true,
                Width = 150,
                ToolTipText = "Tổng thời gian Automation RUNNING đã tích lũy. Đây là thời gian dùng để xét Tự đóng theo giờ."
            });
        }

        if (!grid.Columns.Contains("RoundsPerHour"))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RoundsPerHour",
                HeaderText = "Vòng / giờ",
                ReadOnly = true,
                Width = 105,
                ToolTipText = "Tốc độ của phiên hiện tại. Bắt đầu tô màu đánh giá sau 5 phút chạy."
            });
        }

        if (!grid.Columns.Contains("Progress"))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Progress",
                HeaderText = "Tiến trình",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 330
            });
        }
    }

    static void ConfigureMergedDashboardColumnOrder(DataGridView grid)
    {
        SetDisplayIndex(grid, "Profile", 0);
        SetDisplayIndex(grid, "Account", 1);
        SetDisplayIndex(grid, "RunState", 2);
        SetDisplayIndex(grid, "SessionRuntime", 3);
        SetDisplayIndex(grid, "TotalRuntime", 4);
        SetDisplayIndex(grid, "RoundsPerHour", 5);
        SetDisplayIndex(grid, "Progress", 6);

        if (grid.Columns.Contains("Profile")) grid.Columns["Profile"].Width = 92;
        if (grid.Columns.Contains("Account")) grid.Columns["Account"].Width = 185;
        if (grid.Columns.Contains("RunState")) grid.Columns["RunState"].Width = 112;
        if (grid.Columns.Contains("SessionRuntime")) grid.Columns["SessionRuntime"].Width = 125;
        if (grid.Columns.Contains("TotalRuntime")) grid.Columns["TotalRuntime"].Width = 150;
        if (grid.Columns.Contains("RoundsPerHour")) grid.Columns["RoundsPerHour"].Width = 105;
        if (grid.Columns.Contains("Progress"))
        {
            grid.Columns["Progress"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["Progress"].MinimumWidth = 330;
        }
    }

    static void SetDisplayIndex(DataGridView grid, string name, int index)
    {
        if (!grid.Columns.Contains(name)) return;
        try { grid.Columns[name].DisplayIndex = Math.Min(index, grid.Columns.Count - 1); }
        catch { }
    }

    void EnsureMergedDashboardPaintHook(DataGridView grid)
    {
        if (_dashboardMergedPaintHooked) return;
        _dashboardMergedPaintHooked = true;
        grid.CellPainting += PaintMergedTotalRuntimeCell;
    }

    void PaintMergedTotalRuntimeCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.ColumnIndex < 0)
            return;

        var column = grid.Columns[e.ColumnIndex];
        if (!string.Equals(column.Name, "TotalRuntime", StringComparison.Ordinal))
            return;

        var raw = Convert.ToString(e.FormattedValue) ?? "";
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return;

        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);

        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var font = e.CellStyle.Font ?? grid.Font;

        var flags = TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine;

        var widths = parts
            .Select(p => TextRenderer.MeasureText(e.Graphics, p, font, Size.Empty, flags).Width)
            .ToArray();
        var spaceWidth = TextRenderer.MeasureText(e.Graphics, " ", font, Size.Empty, flags).Width;
        var totalWidth = widths.Sum() + spaceWidth * 2;
        var x = e.CellBounds.Left + Math.Max(4, (e.CellBounds.Width - totalWidth) / 2);
        var yRect = new Rectangle(x, e.CellBounds.Top, e.CellBounds.Width, e.CellBounds.Height);

        var colors = selected
            ? new[] { Color.White, Color.White, Color.White }
            : new[]
            {
                Color.FromArgb(31, 78, 146),   // giờ: xanh đậm
                Color.FromArgb(21, 121, 107),  // phút: xanh ngọc
                Color.FromArgb(176, 103, 28)   // giây: cam nâu
            };

        for (var i = 0; i < parts.Length; i++)
        {
            yRect.X = x;
            yRect.Width = widths[i] + 4;
            TextRenderer.DrawText(e.Graphics, parts[i], font, yRect, colors[i], flags);
            x += widths[i] + spaceWidth;
        }

        e.Handled = true;
    }

    static string FormatMergedRuntime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var hours = (long)Math.Floor(value.TotalHours);
        return $"{hours:00}h {value.Minutes:00}m {value.Seconds:00}s";
    }

    static void ApplyMergedRuntimeStyles(DataGridViewRow row)
    {
        if (row.DataGridView?.Columns.Contains("SessionRuntime") == true)
        {
            var session = row.Cells["SessionRuntime"];
            session.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            session.Style.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            session.Style.ForeColor = Color.FromArgb(76, 88, 104);
            session.Style.BackColor = Color.FromArgb(248, 249, 251);
        }

        if (row.DataGridView?.Columns.Contains("TotalRuntime") == true)
        {
            var total = row.Cells["TotalRuntime"];
            total.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            total.Style.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            total.Style.ForeColor = Color.FromArgb(31, 78, 146);
            total.Style.BackColor = Color.FromArgb(232, 241, 252);
            total.Style.SelectionBackColor = Color.FromArgb(65, 112, 173);
            total.Style.SelectionForeColor = Color.White;
        }
    }

    void UpdateMergedDashboardSummary(
        IReadOnlyList<(DataGridViewRow Row, StatisticsRuntimeSnapshot Stats, double? Rate)> rowStats,
        IReadOnlyList<double> eligibleRates)
    {
        if (_dashboardSummary is null || _dashboardSummary.IsDisposed) return;

        var running = 0;
        var paused = 0;
        var recovering = 0;
        long viewerTotal = 0;
        var viewerCount = 0;

        foreach (var item in rowStats)
        {
            if (item.Row.Tag is not ProfileContext ctx) continue;
            var state = GetEffectiveRuntimeState(ctx);

            if (state == RuntimeStateRunning) running++;
            else if (state == RuntimeStatePaused) paused++;
            else if (state == RuntimeStateRecovering) recovering++;

            var viewer = ctx.LastSnapshot?.Viewer ?? -1;
            if (viewer >= 0)
            {
                viewerTotal += viewer;
                viewerCount++;
            }
        }

        var allCount = _contexts.Count;
        var visibleCount = rowStats.Count;
        var showAll = _dashboardShowAllProfilesToggle?.Checked == true;
        var profileText = showAll
            ? $"Profiles: {allCount}"
            : $"Profiles đang mở: {visibleCount}/{allCount}";

        var averageViewer = viewerCount == 0
            ? "—"
            : FormatDashboardViewer((int)Math.Round((double)viewerTotal / viewerCount));

        var averageRate = eligibleRates.Count == 0
            ? "—"
            : eligibleRates.Average().ToString("0.0");

        var total = TimeSpan.FromSeconds(
            rowStats.Sum(x => Math.Max(0d, x.Stats.Total.TotalSeconds)));

        _dashboardSummary.Text =
            $"{profileText}   |   🟢 Running: {running}   |   🟠 Paused: {paused}   |   " +
            $"🟡 Recovering: {recovering}   |   Viewer TB: {averageViewer}   |   " +
            $"Vòng/giờ TB: {averageRate}   |   Tổng chạy: {FormatStatisticsDuration(total)}";
    }

    static void HideDashboardColumn(DataGridView grid, string name)
    {
        if (grid.Columns.Contains(name))
            grid.Columns[name].Visible = false;
    }

    static string BuildDashboardCompactProgress(DataGridViewRow row)
    {
        var detail = GetCompactCellText(row, "Detail").Trim();
        if (detail.StartsWith("Bước:", StringComparison.OrdinalIgnoreCase))
            detail = detail[5..].Trim();

        if (!string.IsNullOrWhiteSpace(detail) && detail != "—")
            return detail;

        var step = GetCompactCellText(row, "Step").Trim();
        return string.IsNullOrWhiteSpace(step) || step == "—" ? "—" : $"Bước {step}";
    }

    static string GetCompactCellText(DataGridViewRow row, string columnName)
    {
        try
        {
            return row.DataGridView?.Columns.Contains(columnName) == true
                ? Convert.ToString(row.Cells[columnName].Value) ?? ""
                : "";
        }
        catch { return ""; }
    }

    static void SetCompactCellIfChanged(DataGridViewRow row, string columnName, string value)
    {
        if (row.DataGridView?.Columns.Contains(columnName) != true) return;
        var current = Convert.ToString(row.Cells[columnName].Value) ?? "";
        if (!string.Equals(current, value, StringComparison.Ordinal))
            row.Cells[columnName].Value = value;
    }

    static void ApplyDashboardCompactProfileStyle(DataGridViewRow row)
    {
        if (row.DataGridView?.Columns.Contains("Profile") != true) return;
        var profileCell = row.Cells["Profile"];
        profileCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        profileCell.Style.ForeColor = Color.FromArgb(25, 67, 112);
        profileCell.Style.SelectionForeColor = Color.FromArgb(18, 55, 95);
    }

    static void ApplyDashboardCompactRowStyle(DataGridViewRow row, string progress)
    {
        var runState = GetCompactCellText(row, "RunState").Trim();
        var chrome = GetCompactCellText(row, "Chrome").Trim();

        var chromeKnown = !string.IsNullOrWhiteSpace(chrome) && chrome != "—";
        var chromeDisconnected = chromeKnown
            && !string.Equals(chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        var hasError = !string.IsNullOrWhiteSpace(progress)
            && (progress.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || progress.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || progress.Contains("Lỗi", StringComparison.OrdinalIgnoreCase));

        row.DefaultCellStyle.BackColor = Color.White;
        row.DefaultCellStyle.ForeColor = SystemColors.ControlText;

        if (chromeDisconnected || hasError)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 238, 238);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(135, 45, 45);
        }
        else if (string.Equals(runState, RuntimeStateRecovering, StringComparison.Ordinal))
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 218);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(112, 82, 14);
        }
        else if (string.Equals(runState, RuntimeStatePaused, StringComparison.Ordinal))
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 246, 229);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(140, 83, 12);
        }

        ApplyDashboardCompactProfileStyle(row);

        if (row.DataGridView?.Columns.Contains("RunState") == true)
        {
            var stateCell = row.Cells["RunState"];
            stateCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            stateCell.Style.ForeColor = chromeDisconnected || hasError
                ? Color.FromArgb(176, 45, 45)
                : GetRuntimeStateColor(runState);
        }
    }
}
