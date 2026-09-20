using ToolTikTokV12.Models;

namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyManagerForm : Form
{
    readonly ProxyCoordinator _coordinator;
    readonly Func<IReadOnlyList<TikTokProfileEntry>> _profilesProvider;

    readonly CheckBox _master = new() { Text = "Bật sử dụng Proxy", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 4, 10, 4) };
    GroupBox? _configBox;
    readonly RadioButton _modePerProfile = new() { Text = "Phân bổ theo PRF", AutoSize = true };
    readonly RadioButton _modeManagerShared = new() { Text = "1 Proxy chung toàn Manager", AutoSize = true };
    readonly Label _modeHint = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly CheckBox _limitCount = new() { Text = "Giới hạn số PRF được gán Proxy", AutoSize = true };
    readonly NumericUpDown _profileLimit = new() { Minimum = 1, Maximum = 9999, Width = 84 };
    readonly NumericUpDown _profilesPerProxy = new() { Minimum = 1, Maximum = 9999, Width = 84 };
    readonly CheckBox _autoAssign = new() { Text = "PRF mới tự lấy Proxy khả dụng", AutoSize = true };
    readonly CheckBox _autoReplace = new() { Text = "Tự gán Proxy khác khi Proxy cũ lỗi", AutoSize = true };
    readonly NumericUpDown _failureThreshold = new() { Minimum = 1, Maximum = 10, Width = 70 };
    readonly NumericUpDown _quarantineMinutes = new() { Minimum = 1, Maximum = 1440, Width = 70 };
    readonly ComboBox _defaultProtocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly TextBox _importText = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly DataGridView _proxyGrid = Grid();
    readonly DataGridView _assignmentGrid = Grid();
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly Button _testButton = new() { Text = "Test tất cả", AutoSize = true };
    readonly Button _autoAssignButton = new() { Text = "Test + Gán tự động", AutoSize = true };
    readonly Button _replaceBadButton = new() { Text = "Gán lại Proxy lỗi", AutoSize = true };
    readonly Button _applyButton = new() { Text = "Áp dụng cho lần mở tiếp theo", AutoSize = true };
    readonly Button _clearAssignmentsButton = new() { Text = "Xóa gán đã chọn", AutoSize = true };
    CancellationTokenSource? _operationCts;
    bool _loading;

    public ProxyManagerForm(ProxyCoordinator coordinator, Func<IReadOnlyList<TikTokProfileEntry>> profilesProvider)
    {
        _coordinator = coordinator;
        _profilesProvider = profilesProvider;
        Text = "Quản lý Proxy";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1080, 780);
        MinimumSize = new Size(900, 650);
        Font = new Font("Segoe UI", 9F);

        _defaultProtocol.Items.AddRange(["HTTP", "HTTPS", "SOCKS5"]);
        BuildUi();
        ConfigureProxyGrid();
        ConfigureAssignmentGrid();
        LoadFromState();
        FormClosing += (_, _) =>
        {
            try { SaveSettingsFromUi(); } catch { }
            try { _operationCts?.Cancel(); } catch { }
        };
    }

    void BuildUi()
    {
        // Keep the logical layout tall enough for DPI/RDP scaling, then let a host
        // panel provide vertical scrolling when the window is shorter. This is UI-only:
        // no Proxy events, state, test, assignment or failover behavior is changed.
        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var root = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            Height = 760
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 202F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 430F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));

        void FitScrollableRootWidth()
        {
            var reserve = scrollHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            root.Width = Math.Max(860, scrollHost.ClientSize.Width - reserve);
        }
        scrollHost.Resize += (_, _) => FitScrollableRootWidth();

        var masterBox = new GroupBox { Text = "PROXY", Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };
        var masterFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        masterFlow.Controls.Add(_master);
        masterFlow.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(18, 5, 0, 0),
            ForeColor = Color.DimGray,
            Text = "Tắt = module Proxy ngừng hoàn toàn; PRF mở sau đó dùng mạng có sẵn. PRF đang chạy không bị restart."
        });
        masterBox.Controls.Add(masterFlow);
        root.Controls.Add(masterBox, 0, 0);

        var configBox = new GroupBox { Text = "PHÂN BỔ TỰ ĐỘNG", Dock = DockStyle.Fill, Padding = new Padding(12) };
        _configBox = configBox;
        var cfg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5 };
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22F));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22F));
        for (var i = 0; i < 5; i++) cfg.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

        var modeFlow = Flow(
            new Label { Text = "Chế độ:", AutoSize = true, Margin = new Padding(0, 5, 10, 0) },
            _modePerProfile,
            _modeManagerShared);
        cfg.Controls.Add(modeFlow, 0, 0);
        cfg.SetColumnSpan(modeFlow, 2);
        cfg.Controls.Add(_modeHint, 2, 0);
        cfg.SetColumnSpan(_modeHint, 2);

        var limitFlow = Flow(_limitCount, _profileLimit);
        cfg.Controls.Add(limitFlow, 0, 1);
        cfg.SetColumnSpan(limitFlow, 2);
        var perProxyFlow = Flow(new Label { Text = "Số PRF dùng chung 1 Proxy:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _profilesPerProxy);
        cfg.Controls.Add(perProxyFlow, 2, 1);
        cfg.SetColumnSpan(perProxyFlow, 2);

        cfg.Controls.Add(_autoAssign, 0, 2);
        cfg.SetColumnSpan(_autoAssign, 2);
        cfg.Controls.Add(_autoReplace, 2, 2);
        cfg.SetColumnSpan(_autoReplace, 2);

        var thresholdFlow = Flow(new Label { Text = "Lỗi liên tiếp trước khi loại:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _failureThreshold);
        cfg.Controls.Add(thresholdFlow, 0, 3);
        cfg.SetColumnSpan(thresholdFlow, 2);
        var quarantineFlow = Flow(new Label { Text = "Test lại Proxy lỗi sau (phút):", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _quarantineMinutes);
        cfg.Controls.Add(quarantineFlow, 2, 3);
        cfg.SetColumnSpan(quarantineFlow, 2);

        var protoFlow = Flow(new Label { Text = "Loại mặc định khi dòng Proxy không có scheme:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _defaultProtocol);
        cfg.Controls.Add(protoFlow, 0, 4);
        cfg.SetColumnSpan(protoFlow, 2);
        var note = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "PRF đang mở không bị restart; thay đổi Proxy có hiệu lực ở lần mở Chrome kế tiếp."
        };
        cfg.Controls.Add(note, 2, 4);
        cfg.SetColumnSpan(note, 2);
        configBox.Controls.Add(cfg);
        root.Controls.Add(configBox, 0, 1);

        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
        _body.Controls.Add(BuildPoolGroup(), 0, 0);
        _body.Controls.Add(BuildAssignmentGroup(), 0, 1);
        root.Controls.Add(_body, 0, 2);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        var save = new Button { Text = "Lưu", Width = 100, Height = 32 };
        var close = new Button { Text = "Đóng", Width = 100, Height = 32 };
        save.Click += async (_, _) => await SaveAndApplyAsync();
        close.Click += (_, _) => Close();
        bottom.Controls.Add(save, 1, 0);
        bottom.Controls.Add(close, 2, 0);
        root.Controls.Add(bottom, 0, 3);

        scrollHost.Controls.Add(root);
        Controls.Add(scrollHost);
        FitScrollableRootWidth();

        _master.CheckedChanged += async (_, _) => await MasterChangedAsync();
        _limitCount.CheckedChanged += (_, _) => UpdateModeUi();
        _modePerProfile.CheckedChanged += (_, _) => { if (!_loading && _modePerProfile.Checked) UpdateModeUi(); };
        _modeManagerShared.CheckedChanged += (_, _) =>
        {
            if (_loading || !_modeManagerShared.Checked) return;
            _autoReplace.Checked = true; // Manager chung luôn có failover theo thứ tự Pool.
            UpdateModeUi();
        };
    }

    Control BuildPoolGroup()
    {
        var group = new GroupBox { Text = "POOL PROXY + TEST", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        // UI-only fix: 100px was too short for 3 vertical 29px buttons once
        // WinForms control margins/DPI scaling are included, causing the last button
        // ("Xóa Proxy chọn") to be clipped behind the grid. Keep the same controls
        // and event flow; only reserve enough layout height for the existing toolbar.
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var import = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));
        import.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        import.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        import.Controls.Add(_importText, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var add = new Button { Text = "Thêm vào Pool", Width = 150, Height = 29 };
        var remove = new Button { Text = "Xóa Proxy chọn", Width = 150, Height = 29 };
        add.Click += (_, _) => ImportProxies();
        remove.Click += (_, _) => RemoveSelectedProxies();
        _testButton.Width = 150;
        _testButton.Height = 29;
        _testButton.Click += async (_, _) => await TestAllAsync();
        buttons.Controls.Add(add);
        buttons.Controls.Add(_testButton);
        buttons.Controls.Add(remove);
        import.Controls.Add(buttons, 1, 0);
        import.SetRowSpan(buttons, 2);
        import.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Text = "Mỗi dòng: ip:port | ip:port:user:pass | user:pass@ip:port | http(s)/socks5://..."
        }, 0, 1);

        layout.Controls.Add(import, 0, 0);
        layout.Controls.Add(_proxyGrid, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    Control BuildAssignmentGroup()
    {
        var group = new GroupBox { Text = "GÁN PRF", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _autoAssignButton.Click += async (_, _) => await TestAndAssignAsync();
        _replaceBadButton.Click += async (_, _) => await ReplaceBadAsync();
        _applyButton.Click += async (_, _) => await ApplyOnlyAsync();
        _clearAssignmentsButton.Click += (_, _) => ClearSelectedAssignments();
        buttons.Controls.Add(_autoAssignButton);
        buttons.Controls.Add(_replaceBadButton);
        buttons.Controls.Add(_clearAssignmentsButton);
        buttons.Controls.Add(_applyButton);
        var assignmentHint = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "PRF đang mở không bị restart; Proxy mới có hiệu lực ở lần mở Chrome kế tiếp."
        };
        layout.Controls.Add(buttons, 0, 0);
        layout.Controls.Add(assignmentHint, 0, 1);
        layout.Controls.Add(_assignmentGrid, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        foreach (var control in controls) flow.Controls.Add(control);
        return flow;
    }

    static DataGridView Grid()
        => new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Color.White
        };

    void ConfigureProxyGrid()
    {
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProxyId", Visible = false });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Proxy", HeaderText = "Proxy", FillWeight = 190 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Trạng thái", FillWeight = 85 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExitIp", HeaderText = "IP ra", FillWeight = 95 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Latency", HeaderText = "Độ trễ", FillWeight = 65 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Usage", HeaderText = "Đang dùng", FillWeight = 70 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastTest", HeaderText = "Lần test", FillWeight = 100 });
    }

    void ConfigureAssignmentGrid()
    {
        _assignmentGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Profile", HeaderText = "PRF", FillWeight = 75 });
        _assignmentGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Proxy", HeaderText = "Proxy", FillWeight = 190 });
        _assignmentGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExitIp", HeaderText = "IP ra", FillWeight = 90 });
        _assignmentGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Trạng thái", FillWeight = 85 });
    }

    void LoadFromState()
    {
        _loading = true;
        try
        {
            var state = _coordinator.GetSnapshot();
            _master.Checked = state.Settings.Enabled;
            _modeManagerShared.Checked = state.Settings.DistributionMode == ProxyDistributionMode.ManagerShared;
            _modePerProfile.Checked = !_modeManagerShared.Checked;
            _limitCount.Checked = state.Settings.LimitAssignedProfiles;
            _profileLimit.Value = ClampToNumeric(_profileLimit, state.Settings.AssignedProfileLimit);
            _profilesPerProxy.Value = ClampToNumeric(_profilesPerProxy, state.Settings.ProfilesPerProxy);
            _autoAssign.Checked = state.Settings.AutoAssignNewProfiles;
            _autoReplace.Checked = state.Settings.AutoReplaceBadProxy;
            _failureThreshold.Value = ClampToNumeric(_failureThreshold, state.Settings.FailureThreshold);
            _quarantineMinutes.Value = ClampToNumeric(_quarantineMinutes, state.Settings.QuarantineMinutes);
            _defaultProtocol.SelectedIndex = state.Settings.DefaultProtocol switch
            {
                ProxyProtocol.Https => 1,
                ProxyProtocol.Socks5 => 2,
                _ => 0
            };
            SetModuleControlsEnabled(_master.Checked);
            UpdateModeUi();
            RefreshGrids();
        }
        finally { _loading = false; }
    }

    async Task MasterChangedAsync()
    {
        if (_loading) return;
        SetModuleControlsEnabled(_master.Checked);
        try
        {
            SaveSettingsFromUi();
            SetBusy(true, _master.Checked ? "Đang bật Proxy..." : "Đang tắt Proxy; PRF đang chạy không bị restart...");
            await _coordinator.SetMasterEnabledAsync(_master.Checked, Profiles());
            RefreshGrids();
            _status.Text = _master.Checked
                ? (_modeManagerShared.Checked
                    ? "Proxy đã bật: toàn Manager dùng 1 Proxy chung; Pool là danh sách dự phòng theo thứ tự. PRF đang mở không restart."
                    : "Proxy đã bật. PRF chưa mở/new PRF sẽ dùng mapping tự động khi launch.")
                : "Proxy đã tắt. Lần mở Chrome tiếp theo dùng mạng có sẵn; PRF đang chạy giữ nguyên phiên hiện tại.";
        }
        catch (Exception ex)
        {
            _status.Text = "Không lưu được trạng thái Proxy: " + ex.Message;
        }
        finally { SetBusy(false); }
    }


    void SetModuleControlsEnabled(bool enabled)
    {
        if (_configBox is not null) _configBox.Enabled = enabled;
        _body.Enabled = enabled;
        UpdateModeUi();
    }

    void UpdateModeUi()
    {
        var enabled = _master.Checked;
        var shared = _modeManagerShared.Checked;

        _modeHint.Text = shared
            ? "Toàn bộ PRF dùng 1 Proxy chung. Pool là thứ tự dự phòng: Proxy 1 → 2 → 3 khi lỗi."
            : "Giữ logic cũ: gán Proxy theo từng PRF và giới hạn N PRF / 1 Proxy.";

        _limitCount.Enabled = enabled && !shared;
        _profileLimit.Enabled = enabled && !shared && _limitCount.Checked;
        _profilesPerProxy.Enabled = enabled && !shared;
        _autoAssign.Enabled = enabled && !shared;

        if (shared) _autoReplace.Checked = true;
        _autoReplace.Enabled = enabled && !shared;

        _autoAssignButton.Text = shared ? "Test + Chọn Proxy chung" : "Test + Gán tự động";
        _replaceBadButton.Text = shared ? "Chuyển Proxy chung lỗi" : "Gán lại Proxy lỗi";
        _clearAssignmentsButton.Enabled = enabled && !shared;
        _assignmentGrid.Enabled = enabled;
    }

    void ImportProxies()
    {
        SaveSettingsFromUi();
        var result = _coordinator.ImportLines(_importText.Text, SelectedProtocol());
        _importText.Clear();
        RefreshGrids();
        var detail = result.Errors.Count == 0 ? "" : " | " + string.Join("; ", result.Errors);
        _status.Text = $"Đã thêm {result.Added}; trùng {result.Duplicate}; lỗi {result.Invalid}.{detail}";
    }

    void RemoveSelectedProxies()
    {
        var ids = _proxyGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Cells["ProxyId"].Value?.ToString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();
        if (ids.Length == 0) return;
        var removed = _coordinator.RemoveProxies(ids);
        RefreshGrids();
        _status.Text = $"Đã xóa {removed} Proxy và bỏ các mapping liên quan. PRF đang mở không bị restart.";
    }

    async Task TestAllAsync()
    {
        SaveSettingsFromUi();
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<string>(text => _status.Text = text);
            await _coordinator.TestAllAsync(progress, token);
            if (!_modeManagerShared.Checked && _autoReplace.Checked)
                await _coordinator.ReassignBadAsync(Profiles());
            await _coordinator.ApplyAssignmentsToProfilesAsync(Profiles());
            _status.Text = _modeManagerShared.Checked
                ? "Đã test Pool. Proxy chung lỗi sẽ chuyển sang Proxy khả dụng kế tiếp theo thứ tự; PRF đang mở không restart."
                : "Đã test xong Proxy. Mapping lỗi đã được thay nếu có Proxy tốt còn chỗ.";
        });
    }

    async Task TestAndAssignAsync()
    {
        SaveSettingsFromUi();
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<string>(text => _status.Text = text);
            var assigned = await _coordinator.TestAndRebalanceAsync(Profiles(), progress, token);
            _status.Text = _modeManagerShared.Checked
                ? (assigned > 0
                    ? "Đã test Pool và chọn Proxy chung khả dụng. Toàn bộ PRF sẽ dùng Proxy này ở lần mở Chrome tiếp theo."
                    : "Đã test Pool nhưng chưa có Proxy chung khả dụng.")
                : $"Đã test + gán tự động {assigned} PRF. Mỗi Proxy tối đa {_profilesPerProxy.Value} PRF.";
        });
    }

    async Task ReplaceBadAsync()
    {
        SaveSettingsFromUi();
        await RunOperationAsync(async _ =>
        {
            var count = await _coordinator.ReassignBadAsync(Profiles());
            _status.Text = _modeManagerShared.Checked
                ? (count > 0
                    ? "Đã chuyển sang Proxy chung dự phòng kế tiếp. PRF đang mở không restart; áp dụng ở lần mở Chrome tiếp theo."
                    : "Proxy chung hiện tại vẫn dùng được hoặc chưa có Proxy dự phòng khả dụng.")
                : $"Đã gán lại {count} PRF có Proxy lỗi. PRF đang mở áp dụng ở lần mở Chrome tiếp theo.";
        });
    }

    async Task ApplyOnlyAsync()
    {
        SaveSettingsFromUi();
        await RunOperationAsync(async _ =>
        {
            await _coordinator.ApplyAssignmentsToProfilesAsync(Profiles());
            _status.Text = _modeManagerShared.Checked
                ? "Đã ghi Proxy chung cho các PRF. Không restart Chrome đang chạy; có hiệu lực ở lần mở Chrome tiếp theo."
                : "Đã ghi cấu hình Proxy cho các PRF. Không restart Chrome đang chạy.";
        });
    }

    async Task SaveAndApplyAsync()
    {
        SaveSettingsFromUi();
        await ApplyOnlyAsync();
    }

    void ClearSelectedAssignments()
    {
        var names = _assignmentGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Cells["Profile"].Value?.ToString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();
        if (names.Length == 0) return;
        _coordinator.ClearAssignments(names);
        _ = _coordinator.ApplyAssignmentsToProfilesAsync(Profiles());
        RefreshGrids();
        _status.Text = $"Đã xóa mapping của {names.Length} PRF.";
    }

    async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await action(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Đã hủy thao tác Proxy.";
        }
        catch (Exception ex)
        {
            _status.Text = "Lỗi Proxy: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            RefreshGrids();
        }
    }

    void SetBusy(bool busy, string? status = null)
    {
        _master.Enabled = !busy;
        _testButton.Enabled = !busy;
        _autoAssignButton.Enabled = !busy;
        _replaceBadButton.Enabled = !busy;
        _applyButton.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(status)) _status.Text = status;
        UseWaitCursor = busy;
    }

    void SaveSettingsFromUi()
    {
        var settings = new ProxySettings
        {
            Enabled = _master.Checked,
            DistributionMode = _modeManagerShared.Checked ? ProxyDistributionMode.ManagerShared : ProxyDistributionMode.PerProfile,
            LimitAssignedProfiles = _limitCount.Checked,
            AssignedProfileLimit = (int)_profileLimit.Value,
            ProfilesPerProxy = (int)_profilesPerProxy.Value,
            AutoAssignNewProfiles = _autoAssign.Checked,
            AutoReplaceBadProxy = _autoReplace.Checked,
            FailureThreshold = (int)_failureThreshold.Value,
            QuarantineMinutes = (int)_quarantineMinutes.Value,
            DefaultProtocol = SelectedProtocol()
        };
        _coordinator.UpdateSettings(settings);
    }

    void RefreshGrids()
    {
        if (IsDisposed) return;
        var state = _coordinator.GetSnapshot();
        var currentProfiles = Profiles();
        var currentNames = currentProfiles
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedMode = state.Settings.DistributionMode == ProxyDistributionMode.ManagerShared;
        var usage = state.Assignments
            .Where(x => currentNames.Contains(x.ProfileName))
            .GroupBy(x => x.ProxyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        _proxyGrid.Rows.Clear();
        foreach (var proxy in state.Proxies)
        {
            usage.TryGetValue(proxy.Id, out var used);
            var isSharedActive = sharedMode && proxy.Id.Equals(state.ActiveManagerProxyId, StringComparison.OrdinalIgnoreCase);
            var stateText = isSharedActive ? $"ĐANG DÙNG · {proxy.Health}" : proxy.Health.ToString();
            var usageText = sharedMode
                ? (isSharedActive ? $"CHUNG ({currentProfiles.Count(x => x.Enabled)} PRF)" : "Dự phòng")
                : $"{used}/{Math.Max(1, state.Settings.ProfilesPerProxy)}";
            var index = _proxyGrid.Rows.Add(
                proxy.Id,
                proxy.MaskedDisplay,
                stateText,
                proxy.ExitIp,
                proxy.LastLatencyMs > 0 ? proxy.LastLatencyMs + " ms" : "—",
                usageText,
                proxy.LastTestUtc?.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "—");
            var row = _proxyGrid.Rows[index];
            if (proxy.Health is ProxyHealthState.Dead or ProxyHealthState.Timeout or ProxyHealthState.AuthError or ProxyHealthState.Error)
                row.DefaultCellStyle.ForeColor = Color.Firebrick;
            else if (proxy.Health is ProxyHealthState.Good)
                row.DefaultCellStyle.ForeColor = Color.DarkGreen;
            else if (proxy.Health is ProxyHealthState.Slow)
                row.DefaultCellStyle.ForeColor = Color.DarkOrange;
        }

        _assignmentGrid.Rows.Clear();
        var proxies = state.Proxies.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var assignments = state.Assignments.ToDictionary(x => x.ProfileName, StringComparer.OrdinalIgnoreCase);
        proxies.TryGetValue(state.ActiveManagerProxyId, out var sharedProxy);
        foreach (var profile in currentProfiles.Where(x => x.Enabled).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (sharedMode)
            {
                if (sharedProxy is not null)
                    _assignmentGrid.Rows.Add(profile.Name, sharedProxy.MaskedDisplay, sharedProxy.ExitIp, $"Proxy chung · {sharedProxy.Health}");
                else
                    _assignmentGrid.Rows.Add(profile.Name, "—", "—", "Chưa có Proxy chung");
                continue;
            }

            if (assignments.TryGetValue(profile.Name, out var assignment)
                && proxies.TryGetValue(assignment.ProxyId, out var proxy))
            {
                _assignmentGrid.Rows.Add(profile.Name, proxy.MaskedDisplay, proxy.ExitIp, proxy.Health.ToString());
            }
            else
            {
                _assignmentGrid.Rows.Add(profile.Name, "—", "—", "Chưa gán");
            }
        }
    }

    IReadOnlyList<TikTokProfileEntry> Profiles() => _profilesProvider() ?? Array.Empty<TikTokProfileEntry>();

    ProxyProtocol SelectedProtocol()
        => _defaultProtocol.SelectedIndex switch
        {
            1 => ProxyProtocol.Https,
            2 => ProxyProtocol.Socks5,
            _ => ProxyProtocol.Http
        };

    static decimal ClampToNumeric(NumericUpDown control, int value)
        => Math.Clamp((decimal)value, control.Minimum, control.Maximum);
}
