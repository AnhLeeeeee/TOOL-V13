using System.Drawing.Drawing2D;

namespace CommentVisibilityMonitor;

internal sealed class CommentCheckStatisticsForm : Form
{
    readonly CommentCheckHistoryStore _store;
    readonly Func<IReadOnlyList<string>> _openProfilesProvider;
    readonly Action<string> _log;

    readonly ComboBox _range = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 155
    };
    readonly Button _refresh = new() { Text = "↻ Làm mới", AutoSize = true };
    readonly Label _rangeNote = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };

    readonly Label _openValue = MakeCardValue();
    readonly Label _scannedValue = MakeCardValue();
    readonly Label _sentValue = MakeCardValue();
    readonly Label _visibleValue = MakeCardValue();
    readonly Label _averageValue = MakeCardValue();
    readonly Label _goodValue = MakeCardValue();
    readonly Label _mediumValue = MakeCardValue();
    readonly Label _lowValue = MakeCardValue();

    readonly Label _openSub = MakeCardSub();
    readonly Label _scannedSub = MakeCardSub();
    readonly Label _sentSub = MakeCardSub();
    readonly Label _visibleSub = MakeCardSub();
    readonly Label _averageSub = MakeCardSub();
    readonly Label _goodSub = MakeCardSub();
    readonly Label _mediumSub = MakeCardSub();
    readonly Label _lowSub = MakeCardSub();

    readonly DataGridView _profileGrid = MakeGrid();
    readonly DataGridView _contentGrid = MakeGrid();
    readonly DataGridView _openSessionGrid = MakeGrid();
    readonly DataGridView _dailyGrid = MakeGrid();
    readonly DataGridView _attentionGrid = MakeGrid();
    readonly DataGridView _bestGrid = MakeGrid();

    readonly Label _detailTitle = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 11F, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 90, 160),
        Margin = new Padding(8, 8, 8, 6)
    };
    readonly Label _detailInfo = new()
    {
        AutoSize = true,
        MaximumSize = new Size(360, 0),
        Margin = new Padding(8, 4, 8, 8)
    };
    readonly TrendGraphPanel _trend = new() { Dock = DockStyle.Fill, MinimumSize = new Size(280, 180) };

    readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };

    List<CommentCheckSessionHistory> _sessions = new();
    List<ProfileAggregate> _profileRows = new();
    bool _refreshingUi;

    public CommentCheckStatisticsForm(
        CommentCheckHistoryStore store,
        Func<IReadOnlyList<string>> openProfilesProvider,
        Action<string> log)
    {
        _store = store;
        _openProfilesProvider = openProfilesProvider;
        _log = log;

        Text = "Thống kê CMT TikTok";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1450;
        Height = 820;
        MinimumSize = new Size(1100, 650);
        Font = new Font("Segoe UI", 9F);

        _range.Items.AddRange(new object[] { "Hôm nay", "24 giờ", "3 ngày (mặc định)" });
        _range.SelectedIndex = 2;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        toolbar.Controls.Add(new Label { Text = "Khoảng thời gian:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        toolbar.Controls.Add(_range);
        toolbar.Controls.Add(_refresh);
        toolbar.Controls.Add(_rangeNote);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 94,
            Padding = new Padding(8, 4, 8, 6),
            ColumnCount = 8,
            RowCount = 1
        };
        for (var i = 0; i < 8; i++)
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5F));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        cards.Controls.Add(CreateCard("PRF đang mở", _openValue, _openSub), 0, 0);
        cards.Controls.Add(CreateCard("Đã quét", _scannedValue, _scannedSub), 1, 0);
        cards.Controls.Add(CreateCard("Tổng CMT gửi", _sentValue, _sentSub), 2, 0);
        cards.Controls.Add(CreateCard("Tổng CMT thấy", _visibleValue, _visibleSub), 3, 0);
        cards.Controls.Add(CreateCard("Tỷ lệ trung bình", _averageValue, _averageSub), 4, 0);
        cards.Controls.Add(CreateCard("PRF tốt (≥50%)", _goodValue, _goodSub), 5, 0);
        cards.Controls.Add(CreateCard("Trung bình (20–50%)", _mediumValue, _mediumSub), 6, 0);
        cards.Controls.Add(CreateCard("Thấp (<20%)", _lowValue, _lowSub), 7, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildOverviewTab());
        tabs.TabPages.Add(BuildProfileTab());
        tabs.TabPages.Add(BuildContentTab());
        tabs.TabPages.Add(BuildOpenSessionTab());
        tabs.TabPages.Add(BuildDailyTab());

        Controls.Add(tabs);
        Controls.Add(cards);
        Controls.Add(toolbar);

        _refresh.Click += (_, _) => RefreshStatistics();
        _range.SelectedIndexChanged += (_, _) => RefreshStatistics();
        _profileGrid.SelectionChanged += (_, _) =>
        {
            if (!_refreshingUi)
                UpdateSelectedProfileDetails();
        };
        _refreshTimer.Tick += (_, _) => RefreshStatistics(preserveSelection: true, quiet: true);
        Shown += (_, _) =>
        {
            RefreshStatistics();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) => _refreshTimer.Stop();
    }

    TabPage BuildOverviewTab()
    {
        var page = new TabPage("Tổng quan");
        ConfigureCompactProfileGrid(_attentionGrid);
        ConfigureCompactProfileGrid(_bestGrid);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(MakeSectionTitle("PRF tỷ lệ thấp cần chú ý"), 0, 0);
        layout.Controls.Add(MakeSectionTitle("PRF tỷ lệ tốt"), 1, 0);
        layout.Controls.Add(_attentionGrid, 0, 1);
        layout.Controls.Add(_bestGrid, 1, 1);
        page.Controls.Add(layout);
        return page;
    }

    TabPage BuildProfileTab()
    {
        var page = new TabPage("Thống kê theo PRF");
        AddColumns(_profileGrid,
            ("Profile", "PRF", 68),
            ("Username", "Tài khoản", 145),
            ("Sessions", "Số phiên", 70),
            ("Sent", "Tổng gửi", 72),
            ("Visible", "Tổng thấy", 72),
            ("Average", "TB toàn bộ", 82),
            ("Last3", "TB 3 phiên", 82),
            ("Last5", "TB 5 phiên", 82),
            ("Latest", "Phiên gần nhất", 92),
            ("Trend", "Xu hướng", 68),
            ("Rating", "Đánh giá", 100));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2
        };
        split.Panel1.Padding = new Padding(6);
        split.Panel1.Controls.Add(_profileGrid);

        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 4
        };
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        detail.Controls.Add(_detailTitle, 0, 0);
        detail.Controls.Add(_detailInfo, 0, 1);
        detail.Controls.Add(new Label
        {
            Text = "Tỷ lệ hiển thị theo từng phiên",
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(8, 4, 8, 2)
        }, 0, 2);
        detail.Controls.Add(_trend, 0, 3);
        split.Panel2.Controls.Add(detail);

        page.Controls.Add(split);
        page.SizeChanged += (_, _) =>
        {
            if (page.ClientSize.Width < 900) return;
            var desired = Math.Max(560, page.ClientSize.Width - 370);
            var maximum = page.ClientSize.Width - 300 - split.SplitterWidth;
            if (maximum > 560)
                split.SplitterDistance = Math.Min(desired, maximum);
        };
        return page;
    }

    TabPage BuildContentTab()
    {
        var page = new TabPage("Theo nội dung");
        AddColumns(_contentGrid,
            ("Index", "#", 42),
            ("Content", "Nội dung CMT", 360),
            ("Sent", "Tổng gửi", 90),
            ("Visible", "Tổng thấy", 90),
            ("Missing", "Mất", 80),
            ("Unknown", "Không rõ", 80),
            ("Rate", "Tỷ lệ", 90),
            ("Profiles", "Số PRF thử", 90));
        page.Controls.Add(_contentGrid);
        return page;
    }

    TabPage BuildOpenSessionTab()
    {
        var page = new TabPage("Theo lần mở Chrome");
        AddColumns(_openSessionGrid,
            ("Profile", "PRF", 75),
            ("Username", "Tài khoản", 150),
            ("OpenNo", "Lần mở", 75),
            ("OpenedAt", "Thời gian mở", 125),
            ("Sessions", "Số phiên", 75),
            ("Sent", "Gửi", 70),
            ("Visible", "Thấy", 70),
            ("Missing", "Mất", 70),
            ("Rate", "Tỷ lệ", 85),
            ("LastCheck", "Lần check cuối", 125),
            ("Note", "Ghi chú", 180));
        page.Controls.Add(_openSessionGrid);
        return page;
    }

    TabPage BuildDailyTab()
    {
        var page = new TabPage("Lịch sử theo ngày");
        AddColumns(_dailyGrid,
            ("Date", "Ngày", 90),
            ("Sessions", "Số phiên", 80),
            ("Profiles", "Số PRF", 80),
            ("Sent", "Tổng gửi", 90),
            ("Visible", "Tổng thấy", 90),
            ("Missing", "Mất", 80),
            ("Unknown", "Không rõ", 80),
            ("Rate", "Tỷ lệ", 90),
            ("Good", "Tốt", 70),
            ("Medium", "TB", 70),
            ("Low", "Thấp", 70));
        page.Controls.Add(_dailyGrid);
        return page;
    }

    public void RefreshStatistics()
        => RefreshStatistics(preserveSelection: true, quiet: false);

    void RefreshStatistics(bool preserveSelection, bool quiet)
    {
        var profileState = CaptureGridViewState(_profileGrid, row => CellText(row, "Profile"));
        var attentionState = CaptureGridViewState(_attentionGrid, row => CellText(row, "Profile"));
        var bestState = CaptureGridViewState(_bestGrid, row => CellText(row, "Profile"));
        var contentState = CaptureGridViewState(_contentGrid, row => CellText(row, "Content"));
        var openSessionState = CaptureGridViewState(_openSessionGrid, OpenSessionGridKey);
        var dailyState = CaptureGridViewState(_dailyGrid, row => CellText(row, "Date"));

        try
        {
            _refreshingUi = true;

            _sessions = FilterByRange(_store.LoadAll())
                .OrderBy(x => x.StartedAt)
                .ToList();
            _profileRows = BuildProfileAggregates(_sessions);
            var openProfiles = _openProfilesProvider()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateCards(openProfiles);
            FillProfileGrid(profileState, quiet);
            FillOverviewGrids(attentionState, bestState, quiet);
            FillContentGrid(contentState, quiet);
            FillOpenSessionGrid(openSessionState, quiet);
            FillDailyGrid(dailyState, quiet);

            if (preserveSelection)
            {
                RestoreGridViewState(_profileGrid, profileState, row => CellText(row, "Profile"));
                RestoreGridViewState(_attentionGrid, attentionState, row => CellText(row, "Profile"));
                RestoreGridViewState(_bestGrid, bestState, row => CellText(row, "Profile"));
                RestoreGridViewState(_contentGrid, contentState, row => CellText(row, "Content"));
                RestoreGridViewState(_openSessionGrid, openSessionState, OpenSessionGridKey);
                RestoreGridViewState(_dailyGrid, dailyState, row => CellText(row, "Date"));
            }

            UpdateSelectedProfileDetails();
            _rangeNote.Text = $"Lịch sử chi tiết giữ 3 ngày • cập nhật {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _log("[STATISTICS_REFRESH_ERROR] " + ex);
            if (!quiet)
                MessageBox.Show(this, "Không tải được thống kê:\n" + ex.Message, "Thống kê Check CMT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _refreshingUi = false;
        }
    }

    IEnumerable<CommentCheckSessionHistory> FilterByRange(IEnumerable<CommentCheckSessionHistory> source)
    {
        var now = DateTimeOffset.Now;
        return _range.SelectedIndex switch
        {
            0 => source.Where(x => x.StartedAt.ToLocalTime().Date == now.Date),
            1 => source.Where(x => x.StartedAt >= now.AddHours(-24)),
            _ => source.Where(x => x.StartedAt >= now.AddDays(-3))
        };
    }

    void UpdateCards(IReadOnlyCollection<string> openProfiles)
    {
        var scanned = _profileRows.Select(x => x.Profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sent = _sessions.Sum(x => Math.Max(0, x.Sent));
        var visible = _sessions.Sum(x => Math.Max(0, x.Visible));
        var missing = _sessions.Sum(x => Math.Max(0, x.Missing));
        var known = visible + missing;
        var average = known > 0 ? visible * 100.0 / known : (double?)null;

        var classified = _profileRows.Where(x => x.Rating != "CHƯA ĐỦ MẪU").ToList();
        var good = classified.Count(x => x.Rating == "TỐT");
        var medium = classified.Count(x => x.Rating == "TRUNG BÌNH");
        var low = classified.Count(x => x.Rating == "THẤP");
        var denominator = Math.Max(1, classified.Count);

        _openValue.Text = openProfiles.Count.ToString();
        _openSub.Text = $"hiển thị {openProfiles.Count}/{openProfiles.Count}";
        _scannedValue.Text = scanned.Count.ToString();
        _scannedSub.Text = $"Chưa quét: {openProfiles.Count(x => !scanned.Contains(x))}";
        _sentValue.Text = sent.ToString("N0");
        _sentSub.Text = $"{_sessions.Count} phiên";
        _visibleValue.Text = visible.ToString("N0");
        _visibleSub.Text = $"Mất: {missing:N0}";
        _averageValue.Text = FormatRate(average);
        _averageSub.Text = "tất cả PRF";
        _goodValue.Text = good.ToString();
        _goodSub.Text = classified.Count == 0 ? "—" : $"{good * 100.0 / denominator:0.0}%";
        _mediumValue.Text = medium.ToString();
        _mediumSub.Text = classified.Count == 0 ? "—" : $"{medium * 100.0 / denominator:0.0}%";
        _lowValue.Text = low.ToString();
        _lowSub.Text = classified.Count == 0 ? "—" : $"{low * 100.0 / denominator:0.0}%";
    }

    void FillProfileGrid(GridViewState viewState, bool preserveOrder)
    {
        var rows = _profileRows
            .OrderBy(x => x.Average ?? double.MaxValue)
            .ThenBy(x => x.Profile, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preserveOrder)
            rows = KeepExistingOrder(rows, viewState, x => x.Profile);

        _profileGrid.Rows.Clear();
        foreach (var row in rows)
        {
            var index = _profileGrid.Rows.Add(
                row.Profile,
                row.Username,
                row.SessionCount,
                row.Sent,
                row.Visible,
                FormatRate(row.Average),
                FormatRate(row.Last3),
                FormatRate(row.Last5),
                FormatRate(row.Latest),
                row.Trend,
                row.Rating);
            _profileGrid.Rows[index].Tag = row;
            ApplyRatingStyle(_profileGrid.Rows[index].Cells["Rating"], row.Rating);
            ApplyRateStyle(_profileGrid.Rows[index].Cells["Average"], row.Average);
        }

        if (_profileGrid.Rows.Count == 0)
        {
            _detailTitle.Text = "Chưa có dữ liệu";
            _detailInfo.Text = "Chưa có phiên Check CMT hoàn thành trong khoảng thời gian đã chọn.";
            _trend.SetValues(Array.Empty<double?>());
            return;
        }

        // Lần mở đầu tiên chưa có view-state để khôi phục: chọn dòng đầu.
        if (string.IsNullOrWhiteSpace(viewState.SelectedKey) && viewState.SelectedRowIndex < 0)
        {
            _profileGrid.Rows[0].Selected = true;
            _profileGrid.CurrentCell = _profileGrid.Rows[0].Cells[0];
        }
    }

    void UpdateSelectedProfileDetails()
    {
        if (_profileGrid.CurrentRow?.Tag is not ProfileAggregate row) return;
        _detailTitle.Text = $"Chi tiết PRF: {row.Profile}";
        _detailInfo.Text =
            $"Tài khoản: {row.Username}\n" +
            $"Số phiên: {row.SessionCount}\n" +
            $"Tổng CMT gửi: {row.Sent:N0}\n" +
            $"Tổng CMT thấy: {row.Visible:N0}\n" +
            $"Tỷ lệ TB: {FormatRate(row.Average)}\n" +
            $"Đánh giá: {row.Rating}";
        _trend.SetValues(row.Sessions
            .OrderBy(x => x.StartedAt)
            .Select(x => x.VisibilityRate)
            .ToArray());
    }

    void FillOverviewGrids(GridViewState attentionState, GridViewState bestState, bool preserveOrder)
    {
        var attentionRows = _profileRows
            .Where(x => x.Average.HasValue)
            .OrderBy(x => x.Average)
            .Take(10)
            .ToList();
        var bestRows = _profileRows
            .Where(x => x.Average.HasValue)
            .OrderByDescending(x => x.Average)
            .Take(10)
            .ToList();

        if (preserveOrder)
        {
            attentionRows = KeepExistingOrder(attentionRows, attentionState, x => x.Profile);
            bestRows = KeepExistingOrder(bestRows, bestState, x => x.Profile);
        }

        _attentionGrid.Rows.Clear();
        _bestGrid.Rows.Clear();

        foreach (var row in attentionRows)
            AddCompactProfileRow(_attentionGrid, row);

        foreach (var row in bestRows)
            AddCompactProfileRow(_bestGrid, row);
    }

    static void AddCompactProfileRow(DataGridView grid, ProfileAggregate row)
    {
        var i = grid.Rows.Add(row.Profile, row.Username, row.SessionCount, FormatRate(row.Average), FormatRate(row.Latest), row.Trend, row.Rating);
        grid.Rows[i].Tag = row.Profile;
        ApplyRatingStyle(grid.Rows[i].Cells["Rating"], row.Rating);
        ApplyRateStyle(grid.Rows[i].Cells["Average"], row.Average);
    }

    void FillContentGrid(GridViewState viewState, bool preserveOrder)
    {
        var rows = _sessions
            .SelectMany(s => s.Comments.Select(c => new { s.Profile, Comment = c }))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Comment.Content) ? "(trống)" : x.Comment.Content.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var visible = g.Count(x => IsVisible(x.Comment.Result));
                var missing = g.Count(x => IsMissing(x.Comment.Result));
                var unknown = g.Count() - visible - missing;
                return new
                {
                    Content = g.Key,
                    Sent = g.Count(),
                    Visible = visible,
                    Missing = missing,
                    Unknown = unknown,
                    Rate = Rate(visible, missing),
                    Profiles = g.Select(x => x.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                };
            })
            .OrderByDescending(x => x.Sent)
            .ThenBy(x => x.Content, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (preserveOrder)
            rows = KeepExistingOrder(rows, viewState, x => x.Content);

        _contentGrid.Rows.Clear();
        var number = 1;
        foreach (var row in rows)
        {
            var i = _contentGrid.Rows.Add(number++, row.Content, row.Sent, row.Visible, row.Missing, row.Unknown, FormatRate(row.Rate), row.Profiles);
            _contentGrid.Rows[i].Tag = row.Content;
            ApplyRateStyle(_contentGrid.Rows[i].Cells["Rate"], row.Rate);
        }
    }

    void FillOpenSessionGrid(GridViewState viewState, bool preserveOrder)
    {
        var rows = new List<OpenSessionAggregate>();

        foreach (var profileGroup in _sessions
                     .GroupBy(x => x.Profile, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groups = profileGroup
                .GroupBy(x => string.IsNullOrWhiteSpace(x.OpenSessionId) ? "__LEGACY__" : x.OpenSessionId, StringComparer.Ordinal)
                .Select(g => new
                {
                    Key = g.Key,
                    Sessions = g.OrderBy(x => x.StartedAt).ToList(),
                    OpenedAt = g.Where(x => x.ProfileOpenedAt.HasValue).Select(x => x.ProfileOpenedAt!.Value).DefaultIfEmpty(g.Min(x => x.StartedAt)).Min()
                })
                .OrderBy(x => x.OpenedAt)
                .ToList();

            var openNo = 0;
            foreach (var group in groups)
            {
                var legacy = group.Key == "__LEGACY__";
                if (!legacy) openNo++;
                var list = group.Sessions;
                var visible = list.Sum(x => x.Visible);
                var missing = list.Sum(x => x.Missing);
                var username = list.Select(x => x.Username).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                rows.Add(new OpenSessionAggregate
                {
                    StableKey = profileGroup.Key + "|" + group.Key,
                    Profile = profileGroup.Key,
                    Username = username,
                    OpenNo = legacy ? "Cũ" : $"#{openNo}",
                    OpenedAt = legacy ? "—" : group.OpenedAt.ToLocalTime().ToString("dd/MM HH:mm:ss"),
                    SessionCount = list.Count,
                    Sent = list.Sum(x => x.Sent),
                    Visible = visible,
                    Missing = missing,
                    Rate = Rate(visible, missing),
                    LastCheck = list.Max(x => x.EndedAt).ToLocalTime().ToString("dd/MM HH:mm:ss"),
                    Note = legacy ? "Dữ liệu trước bản vá: chưa xác định lần mở Chrome" : ""
                });
            }
        }

        if (preserveOrder)
            rows = KeepExistingOrder(rows, viewState, x => x.StableKey);

        _openSessionGrid.Rows.Clear();
        foreach (var row in rows)
        {
            var i = _openSessionGrid.Rows.Add(
                row.Profile,
                row.Username,
                row.OpenNo,
                row.OpenedAt,
                row.SessionCount,
                row.Sent,
                row.Visible,
                row.Missing,
                FormatRate(row.Rate),
                row.LastCheck,
                row.Note);
            _openSessionGrid.Rows[i].Tag = row.StableKey;
            ApplyRateStyle(_openSessionGrid.Rows[i].Cells["Rate"], row.Rate);
        }
    }

    void FillDailyGrid(GridViewState viewState, bool preserveOrder)
    {
        var rows = _sessions
            .GroupBy(x => x.StartedAt.ToLocalTime().Date)
            .OrderByDescending(x => x.Key)
            .Select(day =>
            {
                var sessions = day.ToList();
                var visible = sessions.Sum(x => x.Visible);
                var missing = sessions.Sum(x => x.Missing);
                var unknown = sessions.Sum(x => x.Unknown);
                var perProfile = BuildProfileAggregates(sessions);
                var classified = perProfile.Where(x => x.Rating != "CHƯA ĐỦ MẪU").ToList();
                return new
                {
                    Key = day.Key.ToString("dd/MM/yyyy"),
                    Date = day.Key.ToString("dd/MM/yyyy"),
                    Sessions = sessions.Count,
                    Profiles = sessions.Select(x => x.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Sent = sessions.Sum(x => x.Sent),
                    Visible = visible,
                    Missing = missing,
                    Unknown = unknown,
                    Rate = Rate(visible, missing),
                    Good = classified.Count(x => x.Rating == "TỐT"),
                    Medium = classified.Count(x => x.Rating == "TRUNG BÌNH"),
                    Low = classified.Count(x => x.Rating == "THẤP")
                };
            })
            .ToList();

        if (preserveOrder)
            rows = KeepExistingOrder(rows, viewState, x => x.Key);

        _dailyGrid.Rows.Clear();
        foreach (var row in rows)
        {
            var i = _dailyGrid.Rows.Add(
                row.Date,
                row.Sessions,
                row.Profiles,
                row.Sent,
                row.Visible,
                row.Missing,
                row.Unknown,
                FormatRate(row.Rate),
                row.Good,
                row.Medium,
                row.Low);
            _dailyGrid.Rows[i].Tag = row.Key;
            ApplyRateStyle(_dailyGrid.Rows[i].Cells["Rate"], row.Rate);
        }
    }

    static List<ProfileAggregate> BuildProfileAggregates(IEnumerable<CommentCheckSessionHistory> sessions)
    {
        return sessions
            .Where(x => !string.IsNullOrWhiteSpace(x.Profile))
            .GroupBy(x => x.Profile, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.StartedAt).ToList();
                var visible = ordered.Sum(x => Math.Max(0, x.Visible));
                var missing = ordered.Sum(x => Math.Max(0, x.Missing));
                var username = ordered.Select(x => x.Username).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                var last3 = ordered.TakeLast(3).ToList();
                var last5 = ordered.TakeLast(5).ToList();
                var previous3 = ordered.Skip(Math.Max(0, ordered.Count - 6)).Take(Math.Max(0, Math.Min(3, ordered.Count - 3))).ToList();
                var recentRate = CombinedRate(last3);
                var previousRate = CombinedRate(previous3);
                var trend = "→";
                if (recentRate.HasValue && previousRate.HasValue)
                {
                    if (recentRate.Value >= previousRate.Value + 3.0) trend = "↑";
                    else if (recentRate.Value <= previousRate.Value - 3.0) trend = "↓";
                }

                return new ProfileAggregate
                {
                    Profile = g.Key,
                    Username = username,
                    Sessions = ordered,
                    SessionCount = ordered.Count,
                    Sent = ordered.Sum(x => Math.Max(0, x.Sent)),
                    Visible = visible,
                    Missing = missing,
                    Average = Rate(visible, missing),
                    Last3 = CombinedRate(last3),
                    Last5 = CombinedRate(last5),
                    Latest = ordered.LastOrDefault()?.VisibilityRate,
                    Trend = trend,
                    Rating = Rating(ordered.Count, visible, missing)
                };
            })
            .ToList();
    }

    static double? CombinedRate(IEnumerable<CommentCheckSessionHistory> sessions)
    {
        var list = sessions.ToList();
        if (list.Count == 0) return null;
        return Rate(list.Sum(x => Math.Max(0, x.Visible)), list.Sum(x => Math.Max(0, x.Missing)));
    }

    static double? Rate(int visible, int missing)
    {
        var known = visible + missing;
        return known <= 0 ? null : visible * 100.0 / known;
    }

    static string Rating(int sessionCount, int visible, int missing)
    {
        var known = visible + missing;
        if (sessionCount < 3 && known < 50) return "CHƯA ĐỦ MẪU";
        var rate = Rate(visible, missing);
        if (!rate.HasValue) return "CHƯA ĐỦ MẪU";
        if (rate.Value >= 50.0) return "TỐT";
        if (rate.Value >= 20.0) return "TRUNG BÌNH";
        return "THẤP";
    }

    static bool IsVisible(string result)
        => result.Equals("Visible", StringComparison.OrdinalIgnoreCase);

    static bool IsMissing(string result)
        => result.Equals("Missing", StringComparison.OrdinalIgnoreCase);

    static string FormatRate(double? rate)
        => rate.HasValue ? rate.Value.ToString("0.0") + "%" : "—";

    static string CellText(DataGridViewRow row, string columnName)
    {
        try
        {
            return row.Cells[columnName].Value?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    static string OpenSessionGridKey(DataGridViewRow row)
    {
        if (row.Tag is string stableKey && !string.IsNullOrWhiteSpace(stableKey))
            return stableKey;
        return CellText(row, "Profile") + "|" + CellText(row, "OpenNo") + "|" + CellText(row, "OpenedAt");
    }

    static GridViewState CaptureGridViewState(DataGridView grid, Func<DataGridViewRow, string> keySelector)
    {
        var state = new GridViewState();
        try
        {
            state.RowOrder = grid.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(keySelector)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToList();

            if (grid.CurrentRow is { IsNewRow: false } currentRow)
            {
                state.SelectedKey = keySelector(currentRow);
                state.SelectedRowIndex = currentRow.Index;
                state.CurrentColumnName = grid.CurrentCell?.OwningColumn?.Name ?? "";
            }

            var firstIndex = grid.FirstDisplayedScrollingRowIndex;
            if (firstIndex >= 0 && firstIndex < grid.Rows.Count)
            {
                state.FirstVisibleRowIndex = firstIndex;
                state.FirstVisibleKey = keySelector(grid.Rows[firstIndex]);
            }

            state.HorizontalOffset = grid.HorizontalScrollingOffset;
        }
        catch
        {
            // View-state chỉ để giữ trải nghiệm đọc; lỗi capture không được làm hỏng thống kê.
        }
        return state;
    }

    static void RestoreGridViewState(DataGridView grid, GridViewState state, Func<DataGridViewRow, string> keySelector)
    {
        if (grid.Rows.Count == 0) return;

        try
        {
            DataGridViewRow? selectedRow = null;
            if (!string.IsNullOrWhiteSpace(state.SelectedKey))
            {
                selectedRow = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => !row.IsNewRow &&
                        string.Equals(keySelector(row), state.SelectedKey, StringComparison.OrdinalIgnoreCase));
            }

            if (selectedRow == null && state.SelectedRowIndex >= 0)
                selectedRow = grid.Rows[Math.Min(state.SelectedRowIndex, grid.Rows.Count - 1)];

            grid.ClearSelection();
            if (selectedRow != null)
            {
                selectedRow.Selected = true;
                var column = !string.IsNullOrWhiteSpace(state.CurrentColumnName) && grid.Columns.Contains(state.CurrentColumnName)
                    ? grid.Columns[state.CurrentColumnName]
                    : grid.Columns.Count > 0 ? grid.Columns[0] : null;
                if (column != null)
                    grid.CurrentCell = selectedRow.Cells[column.Index];
            }

            var firstRow = !string.IsNullOrWhiteSpace(state.FirstVisibleKey)
                ? grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => !row.IsNewRow &&
                        string.Equals(keySelector(row), state.FirstVisibleKey, StringComparison.OrdinalIgnoreCase))
                : null;

            var firstIndex = firstRow?.Index ??
                (state.FirstVisibleRowIndex >= 0 ? Math.Min(state.FirstVisibleRowIndex, grid.Rows.Count - 1) : -1);
            if (firstIndex >= 0)
                grid.FirstDisplayedScrollingRowIndex = firstIndex;

            if (state.HorizontalOffset >= 0)
                grid.HorizontalScrollingOffset = state.HorizontalOffset;
        }
        catch
        {
            // Không để việc khôi phục scroll/selection làm gián đoạn refresh dữ liệu.
        }
    }

    static List<T> KeepExistingOrder<T>(IEnumerable<T> source, GridViewState state, Func<T, string> keySelector)
    {
        var rows = source.ToList();
        if (rows.Count <= 1 || state.RowOrder.Count == 0)
            return rows;

        var oldOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < state.RowOrder.Count; i++)
        {
            var key = state.RowOrder[i];
            if (!string.IsNullOrWhiteSpace(key) && !oldOrder.ContainsKey(key))
                oldOrder[key] = i;
        }

        return rows
            .Select((row, index) =>
            {
                var hasOldPosition = oldOrder.TryGetValue(keySelector(row), out var oldPosition);
                return new
                {
                    Row = row,
                    OriginalIndex = index,
                    HasOldPosition = hasOldPosition,
                    OldPosition = oldPosition
                };
            })
            .OrderBy(x => x.HasOldPosition ? 0 : 1)
            .ThenBy(x => x.HasOldPosition ? x.OldPosition : x.OriginalIndex)
            .Select(x => x.Row)
            .ToList();
    }

    static Panel CreateCard(string title, Label value, Label sub)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray
        };
        value.Dock = DockStyle.Top;
        value.Height = 32;
        sub.Dock = DockStyle.Fill;
        panel.Controls.Add(sub);
        panel.Controls.Add(value);
        panel.Controls.Add(titleLabel);
        return panel;
    }

    static Label MakeCardValue()
        => new()
        {
            Text = "—",
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };

    static Label MakeCardSub()
        => new()
        {
            Text = "—",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.TopCenter
        };

    static Label MakeSectionTitle(string text)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 90, 160),
            Margin = new Padding(4, 6, 4, 4)
        };

    static DataGridView MakeGrid()
        => new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Color.White
        };

    static void AddColumns(DataGridView grid, params (string Name, string Header, int MinWidth)[] columns)
    {
        foreach (var column in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = column.Name,
                HeaderText = column.Header,
                MinimumWidth = column.MinWidth,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
    }

    static void ConfigureCompactProfileGrid(DataGridView grid)
    {
        AddColumns(grid,
            ("Profile", "PRF", 65),
            ("Username", "Tài khoản", 130),
            ("Sessions", "Phiên", 55),
            ("Average", "TB", 65),
            ("Latest", "Gần nhất", 70),
            ("Trend", "Xu hướng", 60),
            ("Rating", "Đánh giá", 90));
    }

    static void ApplyRateStyle(DataGridViewCell cell, double? rate)
    {
        if (!rate.HasValue) return;
        cell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        cell.Style.ForeColor = rate.Value >= 50
            ? Color.ForestGreen
            : rate.Value >= 20
                ? Color.DarkOrange
                : Color.Red;
    }

    static void ApplyRatingStyle(DataGridViewCell cell, string rating)
    {
        cell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        cell.Style.ForeColor = rating switch
        {
            "TỐT" => Color.ForestGreen,
            "TRUNG BÌNH" => Color.DarkOrange,
            "THẤP" => Color.Red,
            _ => Color.DimGray
        };
    }

    sealed class GridViewState
    {
        public string SelectedKey { get; set; } = "";
        public int SelectedRowIndex { get; set; } = -1;
        public string CurrentColumnName { get; set; } = "";
        public string FirstVisibleKey { get; set; } = "";
        public int FirstVisibleRowIndex { get; set; } = -1;
        public int HorizontalOffset { get; set; }
        public List<string> RowOrder { get; set; } = new();
    }

    sealed class OpenSessionAggregate
    {
        public string StableKey { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public string OpenNo { get; set; } = "";
        public string OpenedAt { get; set; } = "";
        public int SessionCount { get; set; }
        public int Sent { get; set; }
        public int Visible { get; set; }
        public int Missing { get; set; }
        public double? Rate { get; set; }
        public string LastCheck { get; set; } = "";
        public string Note { get; set; } = "";
    }

    sealed class ProfileAggregate
    {
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public List<CommentCheckSessionHistory> Sessions { get; set; } = new();
        public int SessionCount { get; set; }
        public int Sent { get; set; }
        public int Visible { get; set; }
        public int Missing { get; set; }
        public double? Average { get; set; }
        public double? Last3 { get; set; }
        public double? Last5 { get; set; }
        public double? Latest { get; set; }
        public string Trend { get; set; } = "→";
        public string Rating { get; set; } = "CHƯA ĐỦ MẪU";
    }

    sealed class TrendGraphPanel : Panel
    {
        readonly List<double?> _values = new();

        public TrendGraphPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
        }

        public void SetValues(IEnumerable<double?> values)
        {
            _values.Clear();
            _values.AddRange(values);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = ClientRectangle;
            if (rect.Width < 80 || rect.Height < 80) return;

            const int left = 38;
            const int right = 14;
            const int top = 16;
            const int bottom = 30;
            var plot = new Rectangle(left, top, Math.Max(1, rect.Width - left - right), Math.Max(1, rect.Height - top - bottom));

            using var gridPen = new Pen(Color.Gainsboro, 1F);
            using var axisPen = new Pen(Color.Gray, 1F);
            using var linePen = new Pen(Color.DodgerBlue, 2F);
            using var pointBrush = new SolidBrush(Color.DodgerBlue);
            using var textBrush = new SolidBrush(Color.DimGray);
            using var font = new Font("Segoe UI", 8F);

            for (var pct = 0; pct <= 100; pct += 20)
            {
                var y = plot.Bottom - (int)Math.Round(plot.Height * pct / 100.0);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                g.DrawString(pct + "%", font, textBrush, 2, y - 7);
            }
            g.DrawRectangle(axisPen, plot);

            var usable = _values
                .Select((value, index) => new { value, index })
                .Where(x => x.value.HasValue)
                .ToList();
            if (usable.Count == 0)
            {
                g.DrawString("Chưa có dữ liệu phiên", font, textBrush, plot.Left + 8, plot.Top + 8);
                return;
            }

            PointF? previous = null;
            foreach (var item in usable)
            {
                var x = _values.Count <= 1
                    ? plot.Left + plot.Width / 2F
                    : plot.Left + plot.Width * item.index / (float)(_values.Count - 1);
                var bounded = Math.Clamp(item.value!.Value, 0.0, 100.0);
                var y = plot.Bottom - (float)(plot.Height * bounded / 100.0);
                var point = new PointF(x, y);
                if (previous.HasValue) g.DrawLine(linePen, previous.Value, point);
                g.FillEllipse(pointBrush, x - 3.5F, y - 3.5F, 7F, 7F);
                previous = point;
            }

            var labelStep = Math.Max(1, (int)Math.Ceiling(_values.Count / 8.0));
            for (var i = 0; i < _values.Count; i += labelStep)
            {
                var x = _values.Count <= 1
                    ? plot.Left + plot.Width / 2F
                    : plot.Left + plot.Width * i / (float)(_values.Count - 1);
                g.DrawString("#" + (i + 1), font, textBrush, x - 8, plot.Bottom + 6);
            }
        }
    }
}
