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

        // Chỉ đọc các file runtime_stats.json khi người dùng đang xem thẻ Thống kê.
        // Tránh tạo thêm IPC/status polling và không tăng tải khi chạy nhiều profile.
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
            Text = "THỐNG KÊ THỜI GIAN CHẠY",
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
            Width = 150,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Today",
            HeaderText = "Hôm nay",
            Width = 150,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Total",
            HeaderText = "Tổng",
            Width = 170,
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
            MinimumWidth = 160
        });

        LogGridSchema(
            grid,
            "RuntimeStatisticsGrid",
            "Profile",
            "Account",
            "Session",
            "Today",
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

        var rows = new List<(ProfileContext Context, StatisticsRuntimeSnapshot Stats, string Account, string State)>();
        foreach (var ctx in _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder))
        {
            var stats = ReadStatisticsRuntime(ctx);
            var account = GetDashboardAccount(ctx);
            if (string.IsNullOrWhiteSpace(account)) account = "—";

            var workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
            var state = workerAlive ? GetLastConfirmedRuntimeState(ctx) : RuntimeStateStopped;
            if (string.IsNullOrWhiteSpace(state) || string.Equals(state, RuntimeStateUnknown, StringComparison.OrdinalIgnoreCase))
                state = workerAlive ? "UNKNOWN" : RuntimeStateStopped;

            rows.Add((ctx, stats, account, state));
        }

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
                    FormatStatisticsDuration(item.Stats.Today),
                    FormatStatisticsDuration(item.Stats.Total),
                    item.State);

                grid.Rows[index].Tag = item.Context;

                if (!string.IsNullOrWhiteSpace(selectedProfile)
                    && string.Equals(item.Context.Profile.Name, selectedProfile, StringComparison.OrdinalIgnoreCase))
                {
                    grid.Rows[index].Selected = true;
                    grid.CurrentCell = grid.Rows[index].Cells["Profile"];
                }
            }
        }
        finally
        {
            grid.ResumeLayout();
        }

        if (_statisticsSummary is not null && !_statisticsSummary.IsDisposed)
        {
            var running = rows.Count(x => string.Equals(x.State, RuntimeStateRunning, StringComparison.OrdinalIgnoreCase));
            var todayAll = TimeSpan.FromSeconds(rows.Sum(x => Math.Max(0, x.Stats.Today.TotalSeconds)));
            _statisticsSummary.Text =
                $"Profile: {rows.Count}   |   Đang chạy: {running}   |   Tổng thời gian hôm nay: {FormatStatisticsDuration(todayAll)}   " +
                "|   Chỉ tính thời gian Automation ở trạng thái RUNNING";
        }
    }

    StatisticsRuntimeSnapshot ReadStatisticsRuntime(ProfileContext ctx)
    {
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            var path = Path.Combine(dataRoot, "runtime_stats.json");
            if (!File.Exists(path))
                return new StatisticsRuntimeSnapshot(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, HasData: false);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            static double ReadSeconds(JsonElement element, string name)
            {
                if (!element.TryGetProperty(name, out var value)) return 0;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
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
            // Nếu profile đang RUNNING thật, cộng phần thời gian từ checkpoint tới hiện tại
            // để bảng gần như realtime mà không cần thêm IPC.
            if (fileSaysRunning
                && managerSaysRunning
                && root.TryGetProperty("activeRunStartedUtc", out var activeElement)
                && activeElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(activeElement.GetString(), out var activeStartedUtc))
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
                        TimeZoneInfo.Local.GetUtcOffset(localMidnight)).ToUniversalTime();
                    var todayStart = activeStartedUtc > midnightOffset ? activeStartedUtc : midnightOffset;
                    if (todayStart < now)
                        todaySeconds += (now - todayStart).TotalSeconds;
                }
            }

            // Worker chưa mở thì không có "phiên hiện tại" đang tồn tại trong Manager.
            // Hôm nay/Tổng vẫn đọc từ file để người dùng xem được lịch sử của mọi profile.
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
            _log.Warn($"[STATISTICS_READ] profile={ctx.Profile.Name} error={ex.Message}");
            return new StatisticsRuntimeSnapshot(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, HasData: false);
        }
    }

    static string FormatStatisticsDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var totalHours = (long)Math.Floor(value.TotalHours);
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
