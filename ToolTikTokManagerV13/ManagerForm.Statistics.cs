using System.Text.Json;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class StatisticsMarker { }

    sealed record StatisticsRuntimeSnapshot(
        TimeSpan Session,
        TimeSpan Today,
        TimeSpan Total,
        bool HasData);

    readonly StatisticsMarker _statisticsMarker = new();
    TabPage? _statisticsTab;
    DataGridView? _statisticsGrid;
    Label? _statisticsSummary;
    DateTime _statisticsLastRefreshUtc = DateTime.MinValue;
    bool _statisticsInitialized;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        InitializeStatisticsTab();
    }

    void InitializeStatisticsTab()
    {
        if (_statisticsInitialized) return;
        _statisticsInitialized = true;

        EnsureStatisticsTab();
        RefreshStatistics(force: true);

        // Chỉ đọc runtime_stats.json khi người dùng đang xem thẻ Thống kê.
        // Không tạo thêm IPC/status polling riêng nên gần như không tăng tải khi chạy nhiều profile.
        _refreshTimer.Tick += (_, _) =>
        {
            if (_statisticsTab is null
                || _statisticsTab.IsDisposed
                || !ReferenceEquals(_tabs.SelectedTab, _statisticsTab))
                return;

            RefreshStatistics();
        };
    }

    void EnsureStatisticsTab()
    {
        if (_statisticsTab is not null
            && !_statisticsTab.IsDisposed
            && _statisticsTab.Parent == _tabs)
            return;

        var page = new TabPage("📈 Thống kê")
        {
            Tag = _statisticsMarker,
            BackColor = UiTheme.Canvas
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
            BackColor = UiTheme.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = UiTheme.Card,
            Margin = new Padding(0, 0, 0, 8)
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "THỐNG KÊ HIỆU SUẤT CHẠY",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Location = new Point(12, 8)
        };

        _statisticsSummary = new Label
        {
            AutoSize = true,
            Text = "Đang tải thống kê...",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(55, 76, 103),
            Location = new Point(14, 40)
        };

        var refresh = new Button
        {
            Text = "Làm mới",
            AutoSize = true,
            Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        UiTheme.StyleButton(refresh, UiButtonKind.Neutral);
        refresh.Click += (_, _) => RefreshStatistics(force: true);

        void LayoutRefreshButton()
        {
            refresh.Left = Math.Max(360, header.ClientSize.Width - refresh.Width - 12);
            refresh.Top = 20;
        }

        header.Controls.Add(title);
        header.Controls.Add(_statisticsSummary);
        header.Controls.Add(refresh);
        header.Resize += (_, _) => LayoutRefreshButton();
        LayoutRefreshButton();

        _statisticsGrid = BuildStatisticsGrid();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_statisticsGrid, 0, 1);
        page.Controls.Add(root);

        page.Enter += (_, _) => RefreshStatistics(force: true);

        _statisticsTab = page;

        // Tổng quan luôn ở vị trí đầu. Thống kê nằm ngay sau Tổng quan và trước các profile/+.
        var insertIndex = _dashboardTab is not null && _dashboardTab.Parent == _tabs
            ? Math.Min(1, _tabs.TabPages.Count)
            : 0;
        _tabs.TabPages.Insert(insertIndex, page);
    }

    DataGridView BuildStatisticsGrid()
    {
        var grid = new DataGridView
        {
            Name = "RuntimeStatisticsGrid",
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 36,
            RowTemplate = { Height = 34 }
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 239, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(35, 63, 98);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(215, 232, 252);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 50, 75);

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Profile",
            HeaderText = "Profile",
            Width = 120,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(25, 67, 112),
                SelectionForeColor = Color.FromArgb(18, 55, 95)
            }
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Account",
            HeaderText = "Tài khoản TikTok",
            Width = 220
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Session",
            HeaderText = "Phiên hiện tại",
            Width = 145,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
<<<<<<< HEAD
            Name = "Rounds",
            HeaderText = "Vòng",
            Width = 90,
            ToolTipText = "Số vòng hoàn tất của phiên Automation hiện tại.",
=======
            Name = "RoundsPerHour",
            HeaderText = "Vòng / giờ",
            Width = 130,
            ToolTipText = "Tốc độ trung bình của phiên hiện tại: số vòng hoàn tất / số giờ chạy Automation.",
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            }
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "RoundsPerHour",
            HeaderText = "Vòng / giờ",
            Width = 125,
            ToolTipText = "Tốc độ trung bình của phiên hiện tại = số vòng hoàn tất / số giờ Automation thực chạy.",
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            }
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Total",
            HeaderText = "Tổng",
            Width = 155,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "RunState",
            HeaderText = "Trạng thái",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 150
        });

        LogGridSchema(
            grid,
            "RuntimeStatisticsGrid",
            "Profile",
            "Account",
            "Session",
<<<<<<< HEAD
            "Rounds",
=======
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
            "RoundsPerHour",
            "Total",
            "RunState");

        return grid;
    }

    void RefreshStatistics(bool force = false)
    {
        var grid = _statisticsGrid;
        if (grid is null || grid.IsDisposed) return;

        var nowUtc = DateTime.UtcNow;
        if (!force && nowUtc - _statisticsLastRefreshUtc < TimeSpan.FromSeconds(2))
            return;
        _statisticsLastRefreshUtc = nowUtc;

        var selectedProfile = grid.SelectedRows.Count > 0
            ? Convert.ToString(grid.SelectedRows[0].Cells["Profile"].Value)
            : null;

<<<<<<< HEAD
        var rows = new List<(
            ProfileContext Context,
            StatisticsRuntimeSnapshot Stats,
            string Account,
            string State,
            long? Rounds,
            double? RoundsPerHour)>();

=======
        var rows = new List<(ProfileContext Context, StatisticsRuntimeSnapshot Stats, string Account, string State, long? Rounds)>();
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
        foreach (var ctx in _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder))
        {
            var stats = ReadStatisticsRuntime(ctx);
            var account = GetDashboardAccount(ctx);
            if (string.IsNullOrWhiteSpace(account)) account = "—";

            var workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            var state = workerAlive ? GetLastConfirmedRuntimeState(ctx) : RuntimeStateStopped;
            if (string.IsNullOrWhiteSpace(state)
                || string.Equals(state, RuntimeStateUnknown, StringComparison.OrdinalIgnoreCase))
            {
                state = workerAlive ? "UNKNOWN" : RuntimeStateStopped;
            }

<<<<<<< HEAD
            // Rounds của Worker reset về 0 mỗi lần Bắt đầu, nên số vòng và Vòng/giờ
            // luôn đại diện cho chính phiên Automation đang hiển thị ở cột Phiên hiện tại.
            long? rounds = workerAlive ? ctx.LastSnapshot?.Rounds : null;
            var roundsPerHour = CalculateRoundsPerHour(rounds, stats.Session);

            rows.Add((ctx, stats, account, state, rounds, roundsPerHour));
=======
            var rounds = ctx.LastSnapshot?.Rounds;
            rows.Add((ctx, stats, account, state, rounds));
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
        }

        // Chỉ dùng profile đã chạy ít nhất 5 phút để tạo mốc so sánh.
        // Median ít bị một profile quá nhanh/chậm kéo lệch hơn average.
        var eligibleRates = rows
            .Where(x => x.Stats.Session >= TimeSpan.FromMinutes(5)
                        && x.RoundsPerHour is >= 0)
            .Select(x => x.RoundsPerHour!.Value)
            .OrderBy(x => x)
            .ToList();

        var medianRate = CalculateMedian(eligibleRates);

        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();

            foreach (var item in rows)
            {
                var index = grid.Rows.Add(
                    item.Context.Profile.Name,
                    item.Account,
                    FormatStatisticsDuration(item.Stats.Session),
<<<<<<< HEAD
                    item.Rounds is >= 0 ? item.Rounds.Value.ToString() : "—",
                    FormatRoundsPerHour(item.RoundsPerHour),
=======
                    FormatRoundsPerHour(item.Rounds, item.Stats.Session),
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
                    FormatStatisticsDuration(item.Stats.Total),
                    item.State);

                var row = grid.Rows[index];
                row.Tag = item.Context;

                ApplyPerformanceStyle(
                    row.Cells["RoundsPerHour"],
                    item.RoundsPerHour,
                    item.Stats.Session,
                    medianRate);

                if (!string.IsNullOrWhiteSpace(selectedProfile)
                    && string.Equals(
                        item.Context.Profile.Name,
                        selectedProfile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected = true;
                    grid.CurrentCell = row.Cells["Profile"];
                }
            }
        }
        finally
        {
            grid.ResumeLayout();
        }

        if (_statisticsSummary is not null && !_statisticsSummary.IsDisposed)
        {
<<<<<<< HEAD
            var running = rows.Count(x =>
                string.Equals(x.State, RuntimeStateRunning, StringComparison.OrdinalIgnoreCase));

            var totalAll = TimeSpan.FromSeconds(
                rows.Sum(x => Math.Max(0, x.Stats.Total.TotalSeconds)));

            var averageRate = eligibleRates.Count > 0
                ? eligibleRates.Average()
                : (double?)null;

            _statisticsSummary.Text =
                $"Profile: {rows.Count}   |   Đang chạy: {running}   |   " +
                $"Vòng/giờ TB: {(averageRate is null ? "—" : averageRate.Value.ToString("0.0"))}   |   " +
                $"Tổng thời gian chạy: {FormatStatisticsDuration(totalAll)}   |   " +
                "Màu hiệu suất tính sau 5 phút chạy";
=======
            var running = rows.Count(x => string.Equals(x.State, RuntimeStateRunning, StringComparison.OrdinalIgnoreCase));
            var totalAll = TimeSpan.FromSeconds(rows.Sum(x => Math.Max(0, x.Stats.Total.TotalSeconds)));
            _statisticsSummary.Text =
                $"Profile: {rows.Count}   |   Đang chạy: {running}   |   Tổng thời gian chạy: {FormatStatisticsDuration(totalAll)}   " +
                "|   Vòng/giờ tính theo phiên hiện tại";
>>>>>>> fecffd8ad1b1803e545acc90536bc4e9698fe692
        }
    }

    StatisticsRuntimeSnapshot ReadStatisticsRuntime(ProfileContext ctx)
    {
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            var path = Path.Combine(dataRoot, "runtime_stats.json");
            if (!File.Exists(path))
                return new StatisticsRuntimeSnapshot(
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    HasData: false);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            static double ReadSeconds(JsonElement element, string name)
            {
                if (!element.TryGetProperty(name, out var value)) return 0;
                if (value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var number))
                    return Math.Max(0, number);
                return 0;
            }

            var totalSeconds = ReadSeconds(root, "totalRunSeconds");
            var todaySeconds = ReadSeconds(root, "todayRunSeconds");
            var sessionSeconds = ReadSeconds(root, "activeSessionSeconds");

            var todayDate = "";
            if (root.TryGetProperty("todayDate", out var todayElement)
                && todayElement.ValueKind == JsonValueKind.String)
            {
                todayDate = todayElement.GetString() ?? "";
            }

            var localToday = DateTime.Today.ToString("yyyy-MM-dd");
            if (!string.Equals(todayDate, localToday, StringComparison.Ordinal))
                todaySeconds = 0;

            var fileSaysRunning =
                root.TryGetProperty("isRunning", out var runningElement)
                && runningElement.ValueKind == JsonValueKind.True;

            var workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            var managerSaysRunning =
                workerAlive
                && string.Equals(
                    GetLastConfirmedRuntimeState(ctx),
                    RuntimeStateRunning,
                    StringComparison.OrdinalIgnoreCase);

            // activeSessionSeconds trong file là checkpoint gần nhất.
            // Nếu profile đang RUNNING thật, cộng phần từ checkpoint tới hiện tại
            // để bảng gần realtime mà không cần thêm IPC.
            if (fileSaysRunning
                && managerSaysRunning
                && root.TryGetProperty("activeRunStartedUtc", out var activeElement)
                && activeElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    activeElement.GetString(),
                    out var activeStartedUtc))
            {
                var now = DateTimeOffset.UtcNow;
                if (activeStartedUtc < now)
                {
                    var deltaSeconds = (now - activeStartedUtc).TotalSeconds;
                    sessionSeconds += deltaSeconds;
                    totalSeconds += deltaSeconds;

                    var localMidnight = DateTime.Today;
                    var midnightOffset = new DateTimeOffset(
                        localMidnight,
                        TimeZoneInfo.Local.GetUtcOffset(localMidnight))
                        .ToUniversalTime();

                    var todayStart =
                        activeStartedUtc > midnightOffset
                            ? activeStartedUtc
                            : midnightOffset;

                    if (todayStart < now)
                        todaySeconds += (now - todayStart).TotalSeconds;
                }
            }

            // Worker chưa mở thì không có phiên hiện tại đang tồn tại trong Manager.
            // Tổng vẫn đọc từ file để giữ lịch sử.
            if (!workerAlive)
                sessionSeconds = 0;

            return new StatisticsRuntimeSnapshot(
                TimeSpan.FromSeconds(Math.Max(0, sessionSeconds)),
                TimeSpan.FromSeconds(Math.Max(0, todaySeconds)),
                TimeSpan.FromSeconds(Math.Max(0, totalSeconds)),
                HasData: true);
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[STATISTICS_READ] profile={ctx.Profile.Name} error={ex.Message}");

            return new StatisticsRuntimeSnapshot(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                HasData: false);
        }
    }

    static double? CalculateRoundsPerHour(long? rounds, TimeSpan session)
    {
        if (rounds is null || rounds < 0 || session.TotalSeconds <= 0)
            return null;

        var rate = rounds.Value / session.TotalHours;
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            return null;

        return rate;
    }

    static string FormatRoundsPerHour(double? rate)
        => rate is null ? "—" : rate.Value.ToString("0.0");

    static double? CalculateMedian(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return null;

        var middle = sorted.Count / 2;
        if ((sorted.Count & 1) == 1)
            return sorted[middle];

        return (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    static void ApplyPerformanceStyle(
        DataGridViewCell cell,
        double? rate,
        TimeSpan session,
        double? medianRate)
    {
        // Chưa đủ 5 phút: không kết luận nhanh/chậm để tránh số liệu đầu phiên bị phóng đại.
        if (rate is null || session < TimeSpan.FromMinutes(5) || medianRate is null)
        {
            cell.Style.BackColor = Color.FromArgb(242, 244, 247);
            cell.Style.ForeColor = Color.FromArgb(105, 111, 121);
            cell.ToolTipText = "Chưa đủ 5 phút chạy để đánh giá hiệu suất.";
            return;
        }

        var median = medianRate.Value;

        // Nếu toàn bộ nhóm chưa có vòng nào thì không gắn cảnh báo đỏ.
        if (median <= 0)
        {
            cell.Style.BackColor = Color.FromArgb(242, 244, 247);
            cell.Style.ForeColor = Color.FromArgb(105, 111, 121);
            cell.ToolTipText = "Chưa có đủ vòng hoàn tất để so sánh hiệu suất.";
            return;
        }

        var ratio = rate.Value / median;

        if (ratio >= 0.90d)
        {
            // Tốt / ngang mặt bằng chung.
            cell.Style.BackColor = Color.FromArgb(226, 244, 232);
            cell.Style.ForeColor = Color.FromArgb(34, 112, 61);
            cell.ToolTipText =
                $"Hiệu suất tốt: {rate.Value:0.0} vòng/giờ, bằng {ratio * 100:0}% trung vị.";
        }
        else if (ratio >= 0.70d)
        {
            // Chậm nhẹ.
            cell.Style.BackColor = Color.FromArgb(255, 246, 218);
            cell.Style.ForeColor = Color.FromArgb(145, 99, 16);
            cell.ToolTipText =
                $"Hiệu suất hơi thấp: {rate.Value:0.0} vòng/giờ, bằng {ratio * 100:0}% trung vị.";
        }
        else
        {
            // Chậm đáng chú ý.
            cell.Style.BackColor = Color.FromArgb(255, 230, 230);
            cell.Style.ForeColor = Color.FromArgb(174, 54, 54);
            cell.ToolTipText =
                $"Hiệu suất thấp: {rate.Value:0.0} vòng/giờ, bằng {ratio * 100:0}% trung vị.";
        }
    }

    static string FormatRoundsPerHour(long? rounds, TimeSpan session)
    {
        if (rounds is null || rounds < 0 || session.TotalSeconds <= 0)
            return "—";

        var rate = rounds.Value / session.TotalHours;
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            return "—";

        return rate.ToString("0.0");
    }

    static string FormatStatisticsDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var totalHours = (long)Math.Floor(value.TotalHours);
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
