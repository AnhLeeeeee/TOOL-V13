using ToolTikTokV12.Utils;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    static string ManagerDisplayVersion => AppVersionInfo.Current;
    const string UpdateSettingsFileName = "manager_update.json";

    sealed class DashboardMarker { }
    sealed class UpdateSettings
    {
        // version.json: manifest ngắn cho bản mới nhất, giữ tương thích với cấu hình cũ.
        public string ManifestUrl { get; set; } = "";
        // versions.json: lịch sử nhiều phiên bản. Để trống sẽ tự suy ra từ ManifestUrl.
        public string VersionsManifestUrl { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public bool AutoCheck { get; set; } = true;
        // Khi người dùng chủ động downgrade, giữ ở đúng bản này để không nhắc nâng lại ngay.
        public string PinnedVersion { get; set; } = "";
    }

    sealed class UpdateManifest
    {
        public string Version { get; set; } = "";
        public string SetupUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public string Status { get; set; } = ""; // stable | beta | withdrawn
        public string ReleaseDate { get; set; } = "";
        public bool? AllowInstall { get; set; }

        public string EffectiveStatus
        {
            get
            {
                var raw = string.IsNullOrWhiteSpace(Status) ? Channel : Status;
                raw = (raw ?? "stable").Trim().ToLowerInvariant();
                return raw switch
                {
                    "test" => "beta",
                    "stable" or "beta" or "withdrawn" => raw,
                    _ => "stable"
                };
            }
        }

        public bool IsInstallAllowed => AllowInstall ?? !EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase);
    }

    sealed class VersionChoice
    {
        public required UpdateManifest Manifest { get; init; }
        public bool IsLatest { get; init; }
        public bool IsCurrent { get; init; }

        public override string ToString()
        {
            var version = (Manifest.Version ?? "").Trim().TrimStart('v', 'V');
            var status = Manifest.EffectiveStatus switch
            {
                "withdrawn" => "Đã thu hồi",
                "beta" => "Beta",
                _ => "Stable"
            };
            var badges = new List<string>();
            if (IsLatest) badges.Add("Mới nhất");
            if (IsCurrent) badges.Add("Đang dùng");
            badges.Add(status);
            if (!string.IsNullOrWhiteSpace(Manifest.ReleaseDate)) badges.Add(Manifest.ReleaseDate.Trim());
            var notes = CompactVersionNotes(Manifest.Notes, 70);
            return $"{version} — {string.Join(" · ", badges)}{(notes.Length > 0 ? " — " + notes : "")}";
        }
    }

    readonly DashboardMarker _dashboardMarker = new();
    readonly Dictionary<string, string> _dashboardAccountCache = new(StringComparer.OrdinalIgnoreCase);
    DateTime _dashboardAccountCacheUtc = DateTime.MinValue;
    TabPage? _dashboardTab;
    DataGridView? _dashboardGrid;
    Label? _dashboardSummary;
    Label? _dashboardUpdateStatus;
    Button? _dashboardUpdateButton;
    ComboBox? _dashboardVersionSelector;
    CheckBox? _dashboardHoldVersionToggle;
    CheckBox? _dashboardShowAllProfilesToggle;
    UpdateManifest? _latestUpdate;
    readonly List<UpdateManifest> _availableVersions = new();
    bool _updateCheckInProgress;
    bool _updateDownloadInProgress;
    bool _updatingHoldToggle;

    string UpdateSettingsPath => Path.Combine(_baseDir, UpdateSettingsFileName);

    void InitializeDashboardAndUpdater()
    {
        EnsureDashboardTab();
        RefreshDashboard();

        // Dashboard chỉ đọc LastSnapshot vốn đã được Manager refresh định kỳ,
        // không tạo thêm một vòng status IPC riêng cho từng Worker.
        _refreshTimer.Tick += (_, _) => RefreshDashboard();

        Shown += async (_, _) =>
        {
            RefreshDashboard();
            var settings = LoadUpdateSettings();
            RefreshUpdatePanel(settings);
            if (settings.AutoCheck && !string.IsNullOrWhiteSpace(settings.ManifestUrl))
            {
                await Task.Delay(1200);
                await CheckForUpdatesAsync(showWhenCurrent: false);
            }
        };
    }

    void EnsureDashboardTab()
    {
        if (_dashboardTab is not null && !_dashboardTab.IsDisposed && _dashboardTab.Parent == _tabs)
            return;

        var page = new TabPage("📊 Tổng quan")
        {
            Tag = _dashboardMarker,
            BackColor = UiTheme.Canvas
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = UiTheme.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = UiTheme.Card
        };
        var title = new Label
        {
            AutoSize = true,
            Text = $"TỔNG QUAN HỆ THỐNG — V{ManagerDisplayVersion}",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Location = new Point(12, 8)
        };
        _dashboardSummary = new Label
        {
            AutoSize = true,
            Text = "Đang tải trạng thái...",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(42, 57, 76),
            Location = new Point(14, 39)
        };
        _dashboardShowAllProfilesToggle = new CheckBox
        {
            Appearance = Appearance.Button,
            AutoSize = false,
            Width = 170,
            Height = 32,
            Text = "Hồ sơ đang mở",
            TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat,
            Checked = false,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _dashboardShowAllProfilesToggle.FlatAppearance.BorderSize = 1;
        _dashboardShowAllProfilesToggle.CheckedChanged += (_, _) =>
        {
            UpdateDashboardProfileFilterToggleStyle();
            RefreshDashboard();
        };

        void LayoutDashboardFilterToggle()
        {
            if (_dashboardShowAllProfilesToggle is null || _dashboardShowAllProfilesToggle.IsDisposed) return;
            _dashboardShowAllProfilesToggle.Left = Math.Max(420, header.ClientSize.Width - _dashboardShowAllProfilesToggle.Width - 12);
            _dashboardShowAllProfilesToggle.Top = 19;
        }

        header.Controls.Add(title);
        header.Controls.Add(_dashboardSummary);
        header.Controls.Add(_dashboardShowAllProfilesToggle);
        header.Resize += (_, _) => LayoutDashboardFilterToggle();
        LayoutDashboardFilterToggle();
        UpdateDashboardProfileFilterToggleStyle();

        var updatePanel = BuildUpdatePanel();
        _dashboardGrid = BuildDashboardGrid();
        var actions = BuildDashboardActions();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(updatePanel, 0, 1);
        root.Controls.Add(_dashboardGrid, 0, 2);
        root.Controls.Add(actions, 0, 3);
        page.Controls.Add(root);
        page.Enter += (_, _) => RefreshDashboard();

        _dashboardTab = page;
        _tabs.TabPages.Insert(0, page);
        SelectTabPageSafely(page);
    }

    Panel BuildUpdatePanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(0, 8, 0, 8),
            BackColor = Color.FromArgb(245, 248, 252)
        };

        _dashboardUpdateStatus = new Label
        {
            AutoSize = false,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} — chưa tải danh sách phiên bản.",
            ForeColor = Color.FromArgb(55, 76, 103),
            Location = new Point(10, 5),
            AutoEllipsis = true
        };

        _dashboardVersionSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 470,
            Height = 32,
            Location = new Point(10, 43),
            IntegralHeight = false,
            DropDownHeight = 260
        };
        ModernDialog.StyleSelectionInput(_dashboardVersionSelector);
        _dashboardVersionSelector.SelectedIndexChanged += (_, _) => RefreshSelectedVersionAction();

        _dashboardUpdateButton = new Button
        {
            Text = "Cài / Hạ phiên bản",
            AutoSize = true,
            Height = 32,
            Enabled = false,
            Location = new Point(490, 42)
        };
        UiTheme.StyleButton(_dashboardUpdateButton, UiButtonKind.Primary);
        _dashboardUpdateButton.Click += async (_, _) => await DownloadAndInstallSelectedVersionAsync();

        var check = new Button
        {
            Text = "Kiểm tra cập nhật",
            AutoSize = true,
            Height = 32,
            Location = new Point(650, 42)
        };
        UiTheme.StyleButton(check, UiButtonKind.Primary);
        check.Click += async (_, _) => await CheckForUpdatesAsync(showWhenCurrent: true);

        var configure = new Button
        {
            Text = "Cấu hình cập nhật",
            AutoSize = true,
            Height = 32,
            Location = new Point(800, 42)
        };
        UiTheme.StyleButton(configure, UiButtonKind.Neutral);
        configure.Click += (_, _) => ShowUpdateSettingsDialog();

        _dashboardHoldVersionToggle = new CheckBox
        {
            AutoSize = true,
            Text = "Giữ ở phiên bản này",
            Margin = new Padding(0),
            Location = new Point(950, 49),
            Cursor = Cursors.Hand
        };
        _dashboardHoldVersionToggle.CheckedChanged += (_, _) => OnHoldVersionToggleChanged();

        panel.Controls.Add(_dashboardUpdateStatus);
        panel.Controls.Add(_dashboardVersionSelector);
        panel.Controls.Add(_dashboardUpdateButton);
        panel.Controls.Add(check);
        panel.Controls.Add(configure);
        panel.Controls.Add(_dashboardHoldVersionToggle);

        void LayoutUpdateControls()
        {
            if (panel.IsDisposed || _dashboardUpdateStatus is null || _dashboardUpdateStatus.IsDisposed
                || _dashboardVersionSelector is null || _dashboardVersionSelector.IsDisposed
                || _dashboardUpdateButton is null || _dashboardUpdateButton.IsDisposed
                || _dashboardHoldVersionToggle is null || _dashboardHoldVersionToggle.IsDisposed)
                return;

            const int gap = 8;
            const int rightPadding = 10;
            _dashboardUpdateStatus.Width = Math.Max(250, panel.ClientSize.Width - 20);

            var right = Math.Max(0, panel.ClientSize.Width - rightPadding);
            _dashboardHoldVersionToggle.Left = Math.Max(10, right - _dashboardHoldVersionToggle.Width);
            configure.Left = Math.Max(10, _dashboardHoldVersionToggle.Left - gap - configure.Width);
            check.Left = Math.Max(10, configure.Left - gap - check.Width);
            _dashboardUpdateButton.Left = Math.Max(10, check.Left - gap - _dashboardUpdateButton.Width);

            var selectorRight = Math.Max(260, _dashboardUpdateButton.Left - gap);
            _dashboardVersionSelector.Width = Math.Max(250, selectorRight - _dashboardVersionSelector.Left);
        }

        panel.Resize += (_, _) => LayoutUpdateControls();
        _dashboardUpdateButton.TextChanged += (_, _) => LayoutUpdateControls();
        _dashboardHoldVersionToggle.TextChanged += (_, _) => LayoutUpdateControls();
        LayoutUpdateControls();
        return panel;
    }

    DataGridView BuildDashboardGrid()
    {
        var grid = new DataGridView
        {
            Name = "DashboardGrid",
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
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 31 }
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 239, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(35, 63, 98);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(215, 232, 252);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 50, 75);

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Profile", HeaderText = "Profile", Width = 105,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(25, 67, 112),
                SelectionForeColor = Color.FromArgb(18, 55, 95)
            }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Account", HeaderText = "Tài khoản", Width = 155 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RunState", HeaderText = "Trạng thái", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Chrome", HeaderText = "Chrome", Width = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RunTime", HeaderText = "T/g chạy", Width = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Step", HeaderText = "Bước", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rounds", HeaderText = "Vòng", Width = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ram", HeaderText = "RAM chính", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detail", HeaderText = "Chi tiết", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
        LogGridSchema(grid, "DashboardGrid", "Profile", "Account", "RunState", "Chrome", "RunTime", "Step", "Rounds", "Ram", "Detail");

        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].Tag is not ProfileContext ctx) return;
            try
            {
                await OpenProfileAsync(ctx);
                if (ctx.Tab is not null) SelectTabPageSafely(ctx.Tab);
            }
            catch (Exception ex) { ShowError(ex); }
        };
        return grid;
    }

    FlowLayoutPanel BuildDashboardActions()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            FlowDirection = FlowDirection.LeftToRight
        };

        Button ActionButton(string text, UiButtonKind kind, Func<ProfileContext, Task> action)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 34, Margin = new Padding(4) };
            UiTheme.StyleButton(b, kind);
            b.Click += async (_, _) =>
            {
                var ctx = DashboardSelectedContext();
                if (ctx is null)
                {
                    ModernDialog.ShowMessage(this, "Hãy chọn một profile trong bảng Tổng quan trước.", "Tổng quan", MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    await action(ctx);
                    try { await RefreshStatusAsync(ctx); } catch { }
                    RefreshDashboard();
                }
                catch (Exception ex) { ShowError(ex); }
            };
            return b;
        }

        flow.Controls.Add(ActionButton("Mở tab", UiButtonKind.Neutral, async ctx =>
        {
            await OpenProfileAsync(ctx);
            if (ctx.Tab is not null) SelectTabPageSafely(ctx.Tab);
        }));
        flow.Controls.Add(ActionButton("👁 View", UiButtonKind.Primary, ViewChromeForProfileAsync));
        flow.Controls.Add(ActionButton("▶ Start", UiButtonKind.Primary, async ctx =>
        {
            await OpenProfileAsync(ctx);
            await StartWithNameGuardAsync(ctx, "start", TimeSpan.FromSeconds(30));
        }));
        flow.Controls.Add(ActionButton("⏯ Pause/Resume", UiButtonKind.Neutral, async ctx =>
        {
            if (ctx.Worker is null || ctx.Worker.HasExited)
                await OpenProfileAsync(ctx);
            try { await RefreshStatusAsync(ctx); } catch { }
            var paused = string.Equals(GetLastConfirmedRuntimeState(ctx), RuntimeStatePaused, StringComparison.Ordinal);
            await SendCommandAsync(ctx, paused ? "resume" : "pause", TimeSpan.FromSeconds(8));
        }));
        flow.Controls.Add(ActionButton("■ Stop", UiButtonKind.Danger, async ctx =>
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
                await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(8));
        }));
        flow.Controls.Add(ActionButton("↻ Restart Chrome", UiButtonKind.Neutral, async ctx =>
        {
            if (ctx.Worker is null || ctx.Worker.HasExited)
                await OpenProfileAsync(ctx);
            try { await CloseChromeForProfileAsync(ctx); } catch { }
            await Task.Delay(500);
            await OpenChromeForProfileAsync(ctx);
        }));

        var refresh = new Button { Text = "Làm mới", AutoSize = true, Height = 34, Margin = new Padding(12, 4, 4, 4) };
        UiTheme.StyleButton(refresh, UiButtonKind.Neutral);
        refresh.Click += (_, _) => RefreshDashboard(forceAccountRefresh: true);
        flow.Controls.Add(refresh);
        return flow;
    }

    void UpdateDashboardProfileFilterToggleStyle()
    {
        var toggle = _dashboardShowAllProfilesToggle;
        if (toggle is null || toggle.IsDisposed) return;

        var showAll = toggle.Checked;
        toggle.Text = showAll ? "Tất cả hồ sơ" : "Hồ sơ đang mở";
        toggle.BackColor = showAll ? Color.FromArgb(37, 99, 176) : Color.White;
        toggle.ForeColor = showAll ? Color.White : Color.FromArgb(37, 77, 122);
        toggle.FlatAppearance.BorderColor = showAll ? Color.FromArgb(37, 99, 176) : Color.FromArgb(93, 128, 170);
    }

    static bool IsDashboardProfileOpen(ProfileContext ctx)
    {
        var tab = ctx.Tab;
        return tab is not null && !tab.IsDisposed && tab.Parent is not null;
    }

    ProfileContext? DashboardSelectedContext()
    {
        var grid = _dashboardGrid;
        if (grid is null || grid.SelectedRows.Count == 0) return null;
        return grid.SelectedRows[0].Tag as ProfileContext;
    }

    static void SetDashboardCellIfChanged(DataGridViewRow row, string columnName, string value)
    {
        var cell = row.Cells[columnName];
        var current = Convert.ToString(cell.Value) ?? "";
        if (!string.Equals(current, value, StringComparison.Ordinal))
            cell.Value = value;
    }

    static void ApplyDashboardRowStyle(DataGridViewRow row, string runState)
    {
        row.DefaultCellStyle.BackColor = Color.White;
        row.DefaultCellStyle.ForeColor = SystemColors.ControlText;
        if (runState == RuntimeStateRecovering)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 218);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(112, 82, 14);
        }
        else if (runState == RuntimeStateRunning)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(237, 249, 240);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(28, 98, 54);
        }
        else if (runState == RuntimeStatePaused)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 246, 229);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(140, 83, 12);
        }
        else if (runState == RuntimeStateStopped)
        {
            row.DefaultCellStyle.ForeColor = Color.FromArgb(146, 54, 54);
        }

        // Chỉ nhấn thị giác, không thay đổi dữ liệu/logic trạng thái.
        var profileCell = row.Cells["Profile"];
        profileCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        profileCell.Style.ForeColor = Color.FromArgb(25, 67, 112);
        profileCell.Style.SelectionForeColor = Color.FromArgb(18, 55, 95);

        var stateCell = row.Cells["RunState"];
        stateCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        stateCell.Style.ForeColor = GetRuntimeStateColor(runState);
    }

    void RefreshDashboard(bool forceAccountRefresh = false)
    {
        if (IsDisposed || Disposing || _dashboardGrid is null || _dashboardGrid.IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => RefreshDashboard(forceAccountRefresh))); } catch { }
            return;
        }

        // Khi tab Tổng quan không hiển thị, LastSnapshot vẫn được Worker refresh như cũ;
        // chỉ bỏ phần dựng/repaint DataGridView để giảm tải UI. Khi quay lại tab, Enter sẽ refresh ngay.
        if (!forceAccountRefresh && _dashboardTab is not null && !ReferenceEquals(_tabs.SelectedTab, _dashboardTab))
            return;

        if (forceAccountRefresh || DateTime.UtcNow - _dashboardAccountCacheUtc > TimeSpan.FromSeconds(15))
        {
            _dashboardAccountCache.Clear();
            _dashboardAccountCacheUtc = DateTime.UtcNow;
        }

        var selectedName = DashboardSelectedContext()?.Profile.Name;
        var allContexts = _contexts.Values.OrderByDescending(c => c.Profile.Name, NaturalProfileNameOrder).ToList();
        var showAllProfiles = _dashboardShowAllProfilesToggle?.Checked == true;
        var contexts = showAllProfiles
            ? allContexts
            : allContexts.Where(IsDashboardProfileOpen).ToList();
        var running = 0;
        var paused = 0;
        var recovering = 0;
        var stopped = 0;
        var unknown = 0;
        long viewerTotal = 0;
        var viewerCount = 0;

        _dashboardGrid.SuspendLayout();
        try
        {
            var needsRowRebuild = _dashboardGrid.Rows.Count != contexts.Count;
            if (!needsRowRebuild)
            {
                for (var i = 0; i < contexts.Count; i++)
                {
                    if (_dashboardGrid.Rows[i].Tag is not ProfileContext existing
                        || !existing.Profile.Name.Equals(contexts[i].Profile.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        needsRowRebuild = true;
                        break;
                    }
                }
            }

            if (needsRowRebuild)
            {
                _dashboardGrid.Rows.Clear();
                foreach (var ctx in contexts)
                {
                    var rowIndex = _dashboardGrid.Rows.Add(ctx.Profile.Name, "—", "—", "—", "—", "—", "—", "—", "");
                    _dashboardGrid.Rows[rowIndex].Tag = ctx;
                }
            }

            for (var i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                var row = _dashboardGrid.Rows[i];
                row.Tag = ctx;
                var snapshot = ctx.LastSnapshot;
                var runState = GetEffectiveRuntimeState(ctx);
                var detail = snapshot?.Detail ?? "";
                if (ctx.ConsecutiveStatusPollFailures > 0)
                {
                    var transient = $"Status poll tạm thời lỗi ({ctx.ConsecutiveStatusPollFailures}); giữ {GetLastConfirmedRuntimeState(ctx)}";
                    detail = string.IsNullOrWhiteSpace(detail) ? transient : $"{transient} | {detail}";
                }

                if (runState == RuntimeStateRecovering) recovering++;
                else if (runState == RuntimeStateRunning) running++;
                else if (runState == RuntimeStatePaused) paused++;
                else if (runState == RuntimeStateStopped) stopped++;
                else unknown++;

                var viewer = snapshot?.Viewer ?? -1;
                if (viewer >= 0)
                {
                    viewerTotal += viewer;
                    viewerCount++;
                }

                var account = GetDashboardAccount(ctx);
                var stepText = snapshot is null || snapshot.Step <= 0 ? "—" : snapshot.Step.ToString();
                var roundsText = snapshot is null ? "—" : snapshot.Rounds.ToString();
                var chrome = snapshot?.Chrome ?? "—";
                var runTime = FormatDashboardRuntime(snapshot?.TotalRunSeconds ?? -1);
                var ram = GetDashboardPrimaryRamMb(ctx, snapshot);
                var previousRunState = Convert.ToString(row.Cells["RunState"].Value) ?? "";

                SetDashboardCellIfChanged(row, "Profile", ctx.Profile.Name);
                SetDashboardCellIfChanged(row, "Account", string.IsNullOrWhiteSpace(account) ? "—" : account);
                SetDashboardCellIfChanged(row, "RunState", runState);
                SetDashboardCellIfChanged(row, "Chrome", chrome);
                SetDashboardCellIfChanged(row, "RunTime", runTime);
                SetDashboardCellIfChanged(row, "Step", stepText);
                SetDashboardCellIfChanged(row, "Rounds", roundsText);
                SetDashboardCellIfChanged(row, "Ram", ram < 0 ? "—" : $"{ram:N0} MB");
                SetDashboardCellIfChanged(row, "Detail", detail);

                if (needsRowRebuild || !string.Equals(previousRunState, runState, StringComparison.Ordinal))
                    ApplyDashboardRowStyle(row, runState);

                if (needsRowRebuild && !string.IsNullOrWhiteSpace(selectedName)
                    && ctx.Profile.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                    row.Selected = true;
            }
        }
        finally { _dashboardGrid.ResumeLayout(); }

        if (_dashboardSummary is not null && !_dashboardSummary.IsDisposed)
        {
            var avg = viewerCount == 0 ? "—" : FormatDashboardViewer((int)Math.Round((double)viewerTotal / viewerCount));
            var profileCountText = showAllProfiles
                ? $"Profiles: {allContexts.Count}"
                : $"Profiles đang mở: {contexts.Count}/{allContexts.Count}";
            _dashboardSummary.Text = $"{profileCountText}   |   🟢 Running: {running}   |   🟠 Paused: {paused}   |   🟡 Recovering: {recovering}   |   ⚪ Stopped: {stopped}   |   ❔ Unknown: {unknown}   |   Viewer TB: {avg}";
        }
    }

    string GetDashboardAccount(ProfileContext ctx)
    {
        if (_dashboardAccountCache.TryGetValue(ctx.Profile.Name, out var cached)) return cached;
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            var username = _tiktokAuthService.Load(dataRoot).Username;
            _dashboardAccountCache[ctx.Profile.Name] = username;
            return username;
        }
        catch
        {
            _dashboardAccountCache[ctx.Profile.Name] = "";
            return "";
        }
    }

    static string FormatDashboardRuntime(long totalSeconds)
    {
        if (totalSeconds < 0) return "—";
        var value = TimeSpan.FromSeconds(totalSeconds);
        return $"{(long)value.TotalHours}h {value.Minutes:00}m";
    }

    static string FormatDashboardViewer(int value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#}M";
        if (value >= 1_000) return $"{value / 1_000d:0.#}K";
        return value.ToString("N0");
    }

    static long GetDashboardPrimaryRamMb(ProfileContext ctx, WorkerSnapshot? snapshot)
    {
        long bytes = 0;
        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
                bytes += ctx.Worker.WorkingSet64;
        }
        catch { }

        // Chỉ cộng process Chrome top-level gắn với cửa sổ của profile.
        // Đây là chỉ số nhanh để phát hiện bất thường, không phải tổng mọi renderer Chrome.
        try
        {
            if (snapshot is { ChromeWindowHandle: > 0 })
            {
                GetWindowThreadProcessId(new IntPtr(snapshot.ChromeWindowHandle), out var pid);
                if (pid > 0)
                {
                    using var chrome = Process.GetProcessById((int)pid);
                    bytes += chrome.WorkingSet64;
                }
            }
        }
        catch { }
        return bytes <= 0 ? -1 : (long)Math.Round(bytes / 1024d / 1024d);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    UpdateSettings LoadUpdateSettings()
    {
        try
        {
            if (!File.Exists(UpdateSettingsPath)) return new UpdateSettings();
            return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(UpdateSettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UpdateSettings();
        }
        catch (Exception ex)
        {
            _log.Warn("[UPDATE_SETTINGS_READ] " + ex.Message);
            return new UpdateSettings();
        }
    }

    void SaveUpdateSettings(UpdateSettings settings)
    {
        var temp = UpdateSettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, UpdateSettingsPath, true);
    }

    void ShowUpdateSettingsDialog()
    {
        var settings = LoadUpdateSettings();
        using var form = new Form
        {
            Text = $"Trình quản lý phiên bản — V{ManagerDisplayVersion}",
            Width = 820,
            Height = 500,
            MinimumSize = new Size(720, 450),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi
        };
        ModernDialog.Apply(form);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(18)
        };
        for (var i = 0; i < 8; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = "version.json dùng để kiểm tra bản mới nhất. versions.json chứa lịch sử các bản để nâng/hạ phiên bản. Nếu URL versions.json để trống, Manager sẽ tự suy ra cùng thư mục với version.json.",
            Margin = new Padding(0, 0, 0, 10)
        };
        ModernDialog.StylePrimaryLabel(intro);

        var latestLabel = new Label { Text = "URL version.json (bản mới nhất)", AutoSize = true };
        var latestUrl = new TextBox { Dock = DockStyle.Top, Text = settings.ManifestUrl };
        ModernDialog.StyleTextInput(latestUrl);

        var historyLabel = new Label { Text = "URL versions.json (lịch sử phiên bản)", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        var historyUrl = new TextBox { Dock = DockStyle.Top, Text = settings.VersionsManifestUrl };
        ModernDialog.StyleTextInput(historyUrl);

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 0) };
        var channelLabel = new Label { Text = "Kênh:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) };
        var channel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        channel.Items.AddRange(new object[] { "stable", "beta" });
        var normalizedChannel = settings.Channel.Equals("test", StringComparison.OrdinalIgnoreCase) ? "beta" : settings.Channel;
        channel.SelectedItem = normalizedChannel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
        ModernDialog.StyleSelectionInput(channel);
        var autoCheck = new CheckBox { Text = "Tự kiểm tra khi mở Manager", Checked = settings.AutoCheck, AutoSize = true, Margin = new Padding(18, 7, 0, 0) };
        options.Controls.Add(channelLabel);
        options.Controls.Add(channel);
        options.Controls.Add(autoCheck);

        var hold = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.DimGray,
            Text = string.IsNullOrWhiteSpace(settings.PinnedVersion)
                ? "Giữ phiên bản: đang tắt. Khi hạ phiên bản, Manager sẽ tự giữ ở bản đã chọn để không nhắc nâng lại ngay."
                : $"Giữ phiên bản hiện tại: V{settings.PinnedVersion}. Có thể bỏ giữ ngay trên trang Tổng quan.",
            Margin = new Padding(0, 12, 0, 0)
        };

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Mỗi mục trong versions.json: version + setupUrl + sha256 + notes + status (stable/beta/withdrawn) + releaseDate + allowInstall. Với bản lịch sử, setupUrl phải trỏ thẳng tag releases/download/vX.Y.Z/..., không dùng releases/latest/download/...",
            MaximumSize = new Size(760, 0),
            Margin = new Padding(0, 10, 0, 8)
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Lưu", DialogResult = DialogResult.OK, AutoSize = true };
        ModernDialog.StyleSecondaryButton(cancel);
        ModernDialog.StylePrimaryButton(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(latestLabel, 0, 1);
        root.Controls.Add(latestUrl, 0, 2);
        root.Controls.Add(historyLabel, 0, 3);
        root.Controls.Add(historyUrl, 0, 4);
        root.Controls.Add(options, 0, 5);
        root.Controls.Add(hold, 0, 6);
        root.Controls.Add(hint, 0, 7);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 8);
        root.Controls.Add(buttons, 0, 9);
        form.Controls.Add(root);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);

        if (form.ShowDialog(this) != DialogResult.OK) return;
        settings.ManifestUrl = latestUrl.Text.Trim();
        settings.VersionsManifestUrl = historyUrl.Text.Trim();
        settings.Channel = channel.SelectedItem?.ToString() ?? "stable";
        settings.AutoCheck = autoCheck.Checked;
        try
        {
            SaveUpdateSettings(settings);
            _latestUpdate = null;
            _availableVersions.Clear();
            RefreshVersionSelector();
            RefreshUpdatePanel(settings);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    string GetVersionsManifestUrl(UpdateSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VersionsManifestUrl))
            return settings.VersionsManifestUrl.Trim();
        if (!Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var latestUri))
            return "";
        try
        {
            var builder = new UriBuilder(latestUri);
            var path = builder.Path;
            var slash = path.LastIndexOf('/');
            builder.Path = slash >= 0 ? path[..(slash + 1)] + "versions.json" : "/versions.json";
            return builder.Uri.ToString();
        }
        catch { return ""; }
    }

    void RefreshUpdatePanel(UpdateSettings? settings = null)
    {
        if (_dashboardUpdateStatus is null || _dashboardUpdateStatus.IsDisposed) return;
        settings ??= LoadUpdateSettings();

        _updatingHoldToggle = true;
        try
        {
            if (_dashboardHoldVersionToggle is not null && !_dashboardHoldVersionToggle.IsDisposed)
            {
                _dashboardHoldVersionToggle.Text = $"Giữ ở V{ManagerDisplayVersion}";
                _dashboardHoldVersionToggle.Checked = VersionEquals(settings.PinnedVersion, ManagerDisplayVersion);
            }
        }
        finally { _updatingHoldToggle = false; }

        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            _dashboardUpdateStatus.Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} — chưa cấu hình nguồn cập nhật.";
            _dashboardUpdateStatus.ForeColor = Color.DimGray;
            RefreshSelectedVersionAction();
            return;
        }

        var current = FindAvailableVersion(ManagerDisplayVersion);
        if (current is not null && current.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase))
        {
            var rollback = FindRecommendedStableRollback();
            _dashboardUpdateStatus.Text = rollback is null
                ? $"Phiên bản hiện tại: V{ManagerDisplayVersion} — ĐÃ THU HỒI. Chưa tìm thấy bản stable để quay về."
                : $"Phiên bản hiện tại: V{ManagerDisplayVersion} — ĐÃ THU HỒI. Đề xuất quay về V{rollback.Version}.";
            _dashboardUpdateStatus.ForeColor = Color.Firebrick;
        }
        else if (VersionEquals(settings.PinnedVersion, ManagerDisplayVersion))
        {
            _dashboardUpdateStatus.Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} — đang GIỮ phiên bản, không tự nhắc nâng lên bản mới hơn.";
            _dashboardUpdateStatus.ForeColor = Color.FromArgb(111, 78, 15);
        }
        else if (FindBestUpdateCandidate(settings) is UpdateManifest candidate)
        {
            _dashboardUpdateStatus.Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} → có bản V{candidate.Version} ({DisplayVersionStatus(candidate)}).";
            _dashboardUpdateStatus.ForeColor = Color.DarkGreen;
        }
        else if (_availableVersions.Count > 0)
        {
            _dashboardUpdateStatus.Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} — đã tải {_availableVersions.Count} phiên bản có trong lịch sử.";
            _dashboardUpdateStatus.ForeColor = Color.FromArgb(55, 76, 103);
        }
        else
        {
            _dashboardUpdateStatus.Text = $"Phiên bản hiện tại: V{ManagerDisplayVersion} — nguồn cập nhật đã cấu hình ({settings.Channel}).";
            _dashboardUpdateStatus.ForeColor = Color.FromArgb(55, 76, 103);
        }
        RefreshSelectedVersionAction();
    }

    void OnHoldVersionToggleChanged()
    {
        if (_updatingHoldToggle || _dashboardHoldVersionToggle is null) return;
        try
        {
            var settings = LoadUpdateSettings();
            if (_dashboardHoldVersionToggle.Checked)
            {
                settings.PinnedVersion = ManagerDisplayVersion;
                SaveUpdateSettings(settings);
                _log.Info($"[VERSION_PIN] version={ManagerDisplayVersion}");
            }
            else if (VersionEquals(settings.PinnedVersion, ManagerDisplayVersion))
            {
                settings.PinnedVersion = "";
                SaveUpdateSettings(settings);
                _log.Info($"[VERSION_UNPIN] version={ManagerDisplayVersion}");
            }
            RefreshUpdatePanel(settings);
        }
        catch (Exception ex)
        {
            _log.Warn("[VERSION_PIN_FAILED] " + ex.Message);
            ModernDialog.ShowMessage(this, "Không lưu được chế độ giữ phiên bản.\n\n" + ex.Message, "Trình quản lý phiên bản", MessageBoxIcon.Warning);
        }
    }

    async Task CheckForUpdatesAsync(bool showWhenCurrent)
    {
        if (_updateCheckInProgress) return;
        var settings = LoadUpdateSettings();
        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            ModernDialog.ShowMessage(this, "Chưa cấu hình URL version.json. Hãy bấm ‘Cấu hình cập nhật’ trước.", "Trình quản lý phiên bản", MessageBoxIcon.Information);
            return;
        }

        if (!TryHttpUri(settings.ManifestUrl, out var latestUri))
        {
            ModernDialog.ShowMessage(this, "URL version.json không hợp lệ. URL phải bắt đầu bằng https:// hoặc http://.", "Trình quản lý phiên bản", MessageBoxIcon.Warning);
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            if (_dashboardUpdateStatus is not null)
            {
                _dashboardUpdateStatus.Text = "Đang tải thông tin phiên bản...";
                _dashboardUpdateStatus.ForeColor = Color.DarkOrange;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ToolTikTokManager/{AppVersionInfo.Current}");

            var latestJson = await client.GetStringAsync(latestUri);
            var latest = JsonSerializer.Deserialize<UpdateManifest>(latestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (latest is null || string.IsNullOrWhiteSpace(latest.Version) || string.IsNullOrWhiteSpace(latest.SetupUrl))
                throw new InvalidDataException("version.json thiếu version hoặc setupUrl.");

            var history = new List<UpdateManifest>();
            var historyUrl = GetVersionsManifestUrl(settings);
            string? historyWarning = null;
            if (TryHttpUri(historyUrl, out var historyUri))
            {
                try
                {
                    var historyJson = await client.GetStringAsync(historyUri);
                    history.AddRange(ParseVersionCatalog(historyJson));
                }
                catch (Exception ex)
                {
                    historyWarning = ex.Message;
                    _log.Warn($"[VERSION_HISTORY_FAILED] url={historyUrl} message={ex.Message}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(historyUrl))
            {
                historyWarning = "URL versions.json không hợp lệ.";
            }

            MergeAvailableVersions(latest, history);
            _latestUpdate = FindAvailableVersion(latest.Version) ?? latest;

            var current = FindAvailableVersion(ManagerDisplayVersion);
            var rollback = current is not null && current.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase)
                ? FindRecommendedStableRollback()
                : null;
            var updateCandidate = FindBestUpdateCandidate(settings);
            var preferredVersion = rollback?.Version
                ?? (VersionEquals(settings.PinnedVersion, ManagerDisplayVersion)
                    ? ManagerDisplayVersion
                    : updateCandidate?.Version ?? ManagerDisplayVersion);
            RefreshVersionSelector(preferredVersion);
            RefreshUpdatePanel(settings);

            if (current is not null && current.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase))
            {
                var detail = rollback is null
                    ? "Chưa có bản stable phù hợp trong versions.json."
                    : $"Nên chọn V{rollback.Version} trong danh sách và bấm ‘Hạ xuống’.";
                ModernDialog.ShowMessage(this,
                    $"V{ManagerDisplayVersion} đã được đánh dấu ĐÃ THU HỒI.\n\n{detail}",
                    "Cảnh báo phiên bản", MessageBoxIcon.Warning);
                return;
            }

            if (VersionEquals(settings.PinnedVersion, ManagerDisplayVersion))
            {
                if (showWhenCurrent)
                {
                    var availableCandidate = FindBestUpdateCandidate(settings);
                    var latestText = availableCandidate is not null
                        ? $" Bản có thể cập nhật hiện là V{availableCandidate.Version}, nhưng Manager đang giữ ở V{ManagerDisplayVersion}."
                        : "";
                    ModernDialog.ShowMessage(this,
                        $"Đang giữ ở V{ManagerDisplayVersion}, nên Manager sẽ không tự nhắc nâng phiên bản.{latestText}\n\nBỏ chọn ‘Giữ ở V{ManagerDisplayVersion}’ nếu muốn nhận nhắc cập nhật bình thường.",
                        "Trình quản lý phiên bản", MessageBoxIcon.Information);
                }
                return;
            }

            var bestCandidate = FindBestUpdateCandidate(settings);
            if (bestCandidate is not null)
            {
                if (showWhenCurrent)
                {
                    var notes = string.IsNullOrWhiteSpace(bestCandidate.Notes) ? "Không có ghi chú phiên bản." : bestCandidate.Notes.Trim();
                    var suffix = string.IsNullOrWhiteSpace(historyWarning) ? "" : $"\n\nLưu ý: không tải được đầy đủ versions.json: {historyWarning}";
                    ModernDialog.ShowMessage(this,
                        $"Có bản mới V{bestCandidate.Version} ({DisplayVersionStatus(bestCandidate)}).\n\n{notes}\n\nBạn có thể chọn phiên bản cần cài trực tiếp trên Tổng quan.{suffix}",
                        "Có bản cập nhật", MessageBoxIcon.Information);
                }
            }
            else if (showWhenCurrent)
            {
                var suffix = string.IsNullOrWhiteSpace(historyWarning) ? "" : $"\n\nKhông tải được đầy đủ versions.json: {historyWarning}";
                ModernDialog.ShowMessage(this,
                    $"Bạn đang dùng V{ManagerDisplayVersion}. Không có bản mới hơn phù hợp và được phép cài.{suffix}",
                    "Trình quản lý phiên bản", MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _latestUpdate = null;
            if (_dashboardUpdateStatus is not null)
            {
                _dashboardUpdateStatus.Text = "Kiểm tra phiên bản thất bại: " + ex.Message;
                _dashboardUpdateStatus.ForeColor = Color.Firebrick;
            }
            _log.Warn("[UPDATE_CHECK_FAILED] " + ex);
            if (showWhenCurrent)
                ModernDialog.ShowMessage(this, "Không kiểm tra được phiên bản.\n\n" + ex.Message, "Trình quản lý phiên bản", MessageBoxIcon.Warning);
        }
        finally { _updateCheckInProgress = false; }
    }

    static IReadOnlyList<UpdateManifest> ParseVersionCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement versionsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            versionsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
                 && (document.RootElement.TryGetProperty("versions", out versionsElement)
                     || document.RootElement.TryGetProperty("releases", out versionsElement)))
        {
        }
        else
        {
            throw new InvalidDataException("versions.json phải là một mảng hoặc object có thuộc tính versions hoặc releases.");
        }

        if (versionsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("versions trong versions.json phải là mảng.");

        var result = new List<UpdateManifest>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var item in versionsElement.EnumerateArray())
        {
            try
            {
                var manifest = item.Deserialize<UpdateManifest>(options);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version)) continue;
                result.Add(manifest);
            }
            catch { }
        }
        if (result.Count == 0)
            throw new InvalidDataException("versions.json không có phiên bản hợp lệ.");
        return result;
    }

    void MergeAvailableVersions(UpdateManifest latest, IEnumerable<UpdateManifest> history)
    {
        var map = new Dictionary<string, UpdateManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in history)
        {
            var key = NormalizeVersion(item.Version);
            if (key.Length == 0) continue;
            map[key] = item;
        }

        var latestKey = NormalizeVersion(latest.Version);
        if (map.TryGetValue(latestKey, out var fromHistory))
        {
            // versions.json quyết định status/releaseDate/allowInstall; version.json có thể bổ sung URL/hash/notes mới nhất.
            if (string.IsNullOrWhiteSpace(fromHistory.SetupUrl)) fromHistory.SetupUrl = latest.SetupUrl;
            if (string.IsNullOrWhiteSpace(fromHistory.Sha256)) fromHistory.Sha256 = latest.Sha256;
            if (string.IsNullOrWhiteSpace(fromHistory.Notes)) fromHistory.Notes = latest.Notes;
            if (string.IsNullOrWhiteSpace(fromHistory.Channel)) fromHistory.Channel = latest.Channel;
        }
        else
        {
            map[latestKey] = latest;
        }

        _availableVersions.Clear();
        _availableVersions.AddRange(map.Values);
        _availableVersions.Sort((a, b) => CompareVersions(b.Version, a.Version));
    }

    void RefreshVersionSelector(string? preferredVersion = null)
    {
        var selector = _dashboardVersionSelector;
        if (selector is null || selector.IsDisposed) return;

        var oldSelection = preferredVersion;
        if (string.IsNullOrWhiteSpace(oldSelection) && selector.SelectedItem is VersionChoice oldChoice)
            oldSelection = oldChoice.Manifest.Version;

        var versions = _availableVersions.ToList();
        if (!versions.Any(v => VersionEquals(v.Version, ManagerDisplayVersion)))
        {
            versions.Add(new UpdateManifest
            {
                Version = ManagerDisplayVersion,
                Notes = "Phiên bản đang chạy (chưa có trong versions.json).",
                Status = "stable",
                AllowInstall = false
            });
        }
        versions.Sort((a, b) => CompareVersions(b.Version, a.Version));

        selector.BeginUpdate();
        try
        {
            selector.Items.Clear();
            foreach (var item in versions)
            {
                selector.Items.Add(new VersionChoice
                {
                    Manifest = item,
                    IsLatest = _latestUpdate is not null && VersionEquals(item.Version, _latestUpdate.Version),
                    IsCurrent = VersionEquals(item.Version, ManagerDisplayVersion)
                });
            }

            var target = string.IsNullOrWhiteSpace(oldSelection) ? ManagerDisplayVersion : oldSelection;
            for (var i = 0; i < selector.Items.Count; i++)
            {
                if (selector.Items[i] is VersionChoice choice && VersionEquals(choice.Manifest.Version, target))
                {
                    selector.SelectedIndex = i;
                    break;
                }
            }
            if (selector.SelectedIndex < 0 && selector.Items.Count > 0)
                selector.SelectedIndex = 0;
        }
        finally { selector.EndUpdate(); }
        RefreshSelectedVersionAction();
    }

    UpdateManifest? SelectedVersionManifest()
        => _dashboardVersionSelector?.SelectedItem is VersionChoice choice ? choice.Manifest : null;

    void RefreshSelectedVersionAction()
    {
        var button = _dashboardUpdateButton;
        if (button is null || button.IsDisposed) return;
        var target = SelectedVersionManifest();
        if (target is null)
        {
            button.Text = "Cài / Hạ phiên bản";
            button.Enabled = false;
            return;
        }

        var compare = CompareVersions(target.Version, ManagerDisplayVersion);
        if (compare == 0)
        {
            button.Text = "Đang sử dụng";
            button.Enabled = false;
            return;
        }
        if (!target.IsInstallAllowed || target.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase))
        {
            button.Text = "Bản đã thu hồi";
            button.Enabled = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(target.SetupUrl))
        {
            button.Text = "Thiếu link Setup";
            button.Enabled = false;
            return;
        }
        if (NormalizeSha256(target.Sha256).Length != 64)
        {
            button.Text = "Thiếu SHA-256";
            button.Enabled = false;
            return;
        }

        button.Text = compare < 0
            ? $"Hạ xuống V{NormalizeVersion(target.Version)}"
            : $"Cài V{NormalizeVersion(target.Version)}";
        button.Enabled = true;
    }

    async Task DownloadAndInstallSelectedVersionAsync()
    {
        if (_updateDownloadInProgress) return;
        var manifest = SelectedVersionManifest();
        if (manifest is null)
        {
            ModernDialog.ShowMessage(this, "Hãy chọn một phiên bản trước.", "Trình quản lý phiên bản", MessageBoxIcon.Information);
            return;
        }

        var compare = CompareVersions(manifest.Version, ManagerDisplayVersion);
        if (compare == 0) return;
        var isDowngrade = compare < 0;

        if (!manifest.IsInstallAllowed || manifest.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase))
        {
            ModernDialog.ShowMessage(this, $"V{manifest.Version} đã bị thu hồi hoặc không cho phép cài.", "Trình quản lý phiên bản", MessageBoxIcon.Warning);
            return;
        }
        if (!TryHttpUri(manifest.SetupUrl, out var setupUri))
        {
            ModernDialog.ShowMessage(this, "setupUrl của phiên bản đã chọn không hợp lệ.", "Trình quản lý phiên bản", MessageBoxIcon.Warning);
            return;
        }
        if (isDowngrade && manifest.SetupUrl.Contains("/releases/latest/download/", StringComparison.OrdinalIgnoreCase))
        {
            ModernDialog.ShowMessage(this,
                "Không thể hạ phiên bản bằng link releases/latest/download/.\n\nHãy sửa setupUrl trong versions.json thành link tag cố định, ví dụ releases/download/v13.6.2/ToolTikTok_V13.6.2_Setup.exe.",
                "Link phiên bản cũ không an toàn", MessageBoxIcon.Warning);
            return;
        }

        var expectedHash = NormalizeSha256(manifest.Sha256);
        if (expectedHash.Length != 64)
        {
            ModernDialog.ShowMessage(this, "Phiên bản này thiếu SHA-256 hợp lệ (phải đủ 64 ký tự hex). Tool sẽ không cài để tránh tải nhầm file.", "Thiếu SHA-256", MessageBoxIcon.Warning);
            return;
        }

        var action = isDowngrade ? "HẠ" : "NÂNG";
        var notes = CompactVersionNotes(manifest.Notes, 240);
        var confirmText = $"{action} Tool TikTok từ V{ManagerDisplayVersion} xuống/lên V{NormalizeVersion(manifest.Version)}?\n\n"
            + (notes.Length > 0 ? notes + "\n\n" : "")
            + "Manager sẽ tải Setup, bắt buộc kiểm tra SHA-256, dừng các Worker rồi mở bộ cài."
            + (isDowngrade
                ? "\n\nTrước khi hạ phiên bản, Tool sẽ backup profiles.json + cấu hình + dữ liệu profile cần thiết. Sau khi hạ, Tool tự bật ‘Giữ phiên bản’ để không nhắc nâng lại ngay."
                : "");
        if (ModernDialog.ShowConfirm(this, confirmText, isDowngrade ? "Xác nhận hạ phiên bản" : "Xác nhận cập nhật") != DialogResult.Yes) return;

        _updateDownloadInProgress = true;
        var settings = LoadUpdateSettings();
        var oldPinned = settings.PinnedVersion;
        var pinChanged = false;
        var setupLaunched = false;
        try
        {
            var updateDir = Path.Combine(Path.GetTempPath(), "ToolTikTokUpdates");
            Directory.CreateDirectory(updateDir);
            var destination = Path.Combine(updateDir, $"ToolTikTok_V{SanitizeVersionForFile(manifest.Version)}_Setup.exe");
            var temp = destination + ".download";
            if (File.Exists(temp)) File.Delete(temp);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ToolTikTokManager/{AppVersionInfo.Current}");
            using var response = await client.GetAsync(setupUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    written += read;
                    if (_dashboardUpdateStatus is not null)
                    {
                        _dashboardUpdateStatus.Text = total is > 0
                            ? $"Đang tải V{NormalizeVersion(manifest.Version)}: {written * 100 / total.Value}%"
                            : $"Đang tải V{NormalizeVersion(manifest.Version)}: {written / 1024 / 1024} MB";
                    }
                }
            }

            var actual = ComputeSha256(temp);
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 không khớp. Expected={expectedHash}; Actual={actual}");

            File.Move(temp, destination, true);
            await StopWorkersForVersionInstallAsync();

            string backupPath = "";
            if (isDowngrade)
            {
                if (_dashboardUpdateStatus is not null) _dashboardUpdateStatus.Text = "Đang backup dữ liệu trước khi hạ phiên bản...";
                backupPath = CreateDowngradeBackup(manifest.Version);
                _log.Info($"[VERSION_BACKUP_OK] from={ManagerDisplayVersion} to={manifest.Version} path={backupPath}");
                settings.PinnedVersion = NormalizeVersion(manifest.Version);
                SaveUpdateSettings(settings);
                pinChanged = true;
            }
            else if (!string.IsNullOrWhiteSpace(settings.PinnedVersion))
            {
                // Người dùng đã chủ động chọn một bản khác, vì vậy bỏ pin cũ.
                settings.PinnedVersion = "";
                SaveUpdateSettings(settings);
                pinChanged = true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(destination) ?? updateDir
            });
            setupLaunched = true;
            _log.Info($"[VERSION_SETUP_LAUNCHED] from={ManagerDisplayVersion} to={manifest.Version} downgrade={isDowngrade} path={destination}");
            BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            if (pinChanged && !setupLaunched)
            {
                try
                {
                    settings.PinnedVersion = oldPinned;
                    SaveUpdateSettings(settings);
                }
                catch { }
            }
            _log.Error("[VERSION_INSTALL_FAILED] " + ex);
            ModernDialog.ShowMessage(this, "Không tải/cài được phiên bản đã chọn.\n\n" + ex.Message, "Trình quản lý phiên bản", MessageBoxIcon.Warning);
            RefreshUpdatePanel();
        }
        finally { _updateDownloadInProgress = false; }
    }

    async Task StopWorkersForVersionInstallAsync()
    {
        if (_dashboardUpdateStatus is not null) _dashboardUpdateStatus.Text = "Đã xác thực Setup. Đang dừng Worker...";
        foreach (var ctx in _contexts.Values.Where(c => c.Worker is not null && !c.Worker.HasExited).ToList())
        {
            try { await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(5)); } catch { }
            try { await SendPipeAsync(ctx.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch { }
            try
            {
                if (ctx.Worker is not null)
                    await WaitForProcessExitAsync(ctx.Worker, TimeSpan.FromSeconds(4));
            }
            catch { }
        }
    }

    string CreateDowngradeBackup(string targetVersion)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupRoot = Path.Combine(_baseDir, "version_backups", $"{stamp}_V{SanitizeVersionForFile(ManagerDisplayVersion)}_to_V{SanitizeVersionForFile(targetVersion)}");
        Directory.CreateDirectory(backupRoot);

        var files = new[]
        {
            "profiles.json",
            UpdateSettingsFileName,
            "tiktok_identity_tool.json",
            "tiktok_message_reply_tool.json"
        };
        foreach (var name in files)
        {
            var source = Path.Combine(_baseDir, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(backupRoot, name), true);
        }

        var directories = new[] { "profiles", "manager_default_config" };
        foreach (var name in directories)
        {
            var source = Path.Combine(_baseDir, name);
            if (Directory.Exists(source)) CopyDirectoryForVersionBackup(source, Path.Combine(backupRoot, name));
        }

        var info = new
        {
            createdAt = DateTimeOffset.Now,
            fromVersion = ManagerDisplayVersion,
            toVersion = NormalizeVersion(targetVersion),
            includes = new[] { "profiles.json", "manager_update.json", "tiktok_identity_tool.json", "tiktok_message_reply_tool.json", "profiles/", "manager_default_config/" },
            excludes = new[] { "TikTokProfiles/ (Chrome user-data lớn, không bị bộ cài ghi đè)" }
        };
        File.WriteAllText(Path.Combine(backupRoot, "backup_info.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return backupRoot;
    }

    static void CopyDirectoryForVersionBackup(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryForVersionBackup(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
    }

    UpdateManifest? FindAvailableVersion(string version)
        => _availableVersions.FirstOrDefault(v => VersionEquals(v.Version, version));

    UpdateManifest? FindBestUpdateCandidate(UpdateSettings settings)
    {
        var allowBeta = settings.Channel.Equals("beta", StringComparison.OrdinalIgnoreCase)
                        || settings.Channel.Equals("test", StringComparison.OrdinalIgnoreCase);
        return _availableVersions
            .Where(v => v.IsInstallAllowed
                        && !v.EffectiveStatus.Equals("withdrawn", StringComparison.OrdinalIgnoreCase)
                        && CompareVersions(v.Version, ManagerDisplayVersion) > 0
                        && (allowBeta || v.EffectiveStatus.Equals("stable", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrWhiteSpace(v.SetupUrl)
                        && NormalizeSha256(v.Sha256).Length == 64)
            .OrderByDescending(v => ParseVersionForCompare(v.Version))
            .FirstOrDefault();
    }

    UpdateManifest? FindRecommendedStableRollback()
    {
        return _availableVersions
            .Where(v => v.IsInstallAllowed
                        && v.EffectiveStatus.Equals("stable", StringComparison.OrdinalIgnoreCase)
                        && CompareVersions(v.Version, ManagerDisplayVersion) < 0
                        && !string.IsNullOrWhiteSpace(v.SetupUrl)
                        && NormalizeSha256(v.Sha256).Length == 64)
            .OrderByDescending(v => ParseVersionForCompare(v.Version))
            .FirstOrDefault();
    }

    static string DisplayVersionStatus(UpdateManifest manifest)
        => manifest.EffectiveStatus switch
        {
            "withdrawn" => "Đã thu hồi",
            "beta" => "Beta",
            _ => "Stable"
        };

    static bool TryHttpUri(string? raw, out Uri uri)
    {
        if (Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    static Version ParseVersionForCompare(string? raw)
    {
        raw = NormalizeVersion(raw);
        var core = raw.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0, 0, 0);
    }

    static int CompareVersions(string? left, string? right)
    {
        var l = ParseVersionForCompare(left);
        var r = ParseVersionForCompare(right);
        var cmp = l.CompareTo(r);
        if (cmp != 0) return cmp;
        return string.Compare(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
    }

    static bool VersionEquals(string? left, string? right)
        => CompareVersions(left, right) == 0;

    static bool IsVersionNewer(string candidate, string current)
        => CompareVersions(candidate, current) > 0;

    static string NormalizeVersion(string? version)
        => (version ?? "").Trim().TrimStart('v', 'V');

    static string CompactVersionNotes(string? notes, int maxLength)
    {
        var clean = string.Join(" ", (notes ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= maxLength) return clean;
        return maxLength <= 1 ? clean[..maxLength] : clean[..(maxLength - 1)].TrimEnd() + "…";
    }

    static string NormalizeSha256(string? value)
        => new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    static string SanitizeVersionForFile(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((version ?? "update").Where(ch => !invalid.Contains(ch)).ToArray());
    }

}
