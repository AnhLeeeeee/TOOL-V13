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
        public string ManifestUrl { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public bool AutoCheck { get; set; } = true;
    }

    sealed class UpdateManifest
    {
        public string Version { get; set; } = "";
        public string SetupUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Channel { get; set; } = "stable";
    }

    readonly DashboardMarker _dashboardMarker = new();
    readonly Dictionary<string, string> _dashboardAccountCache = new(StringComparer.OrdinalIgnoreCase);
    DateTime _dashboardAccountCacheUtc = DateTime.MinValue;
    TabPage? _dashboardTab;
    DataGridView? _dashboardGrid;
    Label? _dashboardSummary;
    Label? _dashboardUpdateStatus;
    Button? _dashboardUpdateButton;
    CheckBox? _dashboardShowAllProfilesToggle;
    UpdateManifest? _latestUpdate;
    bool _updateCheckInProgress;
    bool _updateDownloadInProgress;

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

        _dashboardTab = page;
        _tabs.TabPages.Insert(0, page);
        SelectTabPageSafely(page);
    }

    Panel BuildUpdatePanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(0, 8, 0, 8),
            BackColor = Color.FromArgb(245, 248, 252)
        };

        _dashboardUpdateStatus = new Label
        {
            AutoSize = false,
            Width = 760,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Cập nhật: chưa cấu hình nguồn cập nhật.",
            ForeColor = Color.DimGray,
            Location = new Point(10, 9),
            AutoEllipsis = true
        };

        var configure = new Button
        {
            Text = "Cấu hình cập nhật",
            AutoSize = true,
            Height = 32,
            Location = new Point(785, 7)
        };
        UiTheme.StyleButton(configure, UiButtonKind.Neutral);
        configure.Click += (_, _) => ShowUpdateSettingsDialog();

        var check = new Button
        {
            Text = "Kiểm tra cập nhật",
            AutoSize = true,
            Height = 32,
            Location = new Point(930, 7)
        };
        UiTheme.StyleButton(check, UiButtonKind.Primary);
        check.Click += async (_, _) => await CheckForUpdatesAsync(showWhenCurrent: true);

        _dashboardUpdateButton = new Button
        {
            Text = "Tải & cập nhật",
            AutoSize = true,
            Height = 32,
            Visible = false,
            Location = new Point(1075, 7)
        };
        UiTheme.StyleButton(_dashboardUpdateButton, UiButtonKind.Primary);
        _dashboardUpdateButton.Click += async (_, _) => await DownloadAndInstallLatestUpdateAsync();

        panel.Controls.Add(_dashboardUpdateStatus);
        panel.Controls.Add(configure);
        panel.Controls.Add(check);
        panel.Controls.Add(_dashboardUpdateButton);
        panel.Resize += (_, _) =>
        {
            // Giữ phần trạng thái co giãn theo cửa sổ, nhưng không đẩy nút ra ngoài.
            var buttonsWidth = configure.Width + check.Width + (_dashboardUpdateButton.Visible ? _dashboardUpdateButton.Width : 0) + 50;
            _dashboardUpdateStatus.Width = Math.Max(280, panel.ClientSize.Width - buttonsWidth - 28);
            var x = _dashboardUpdateStatus.Right + 8;
            configure.Left = x;
            check.Left = configure.Right + 8;
            _dashboardUpdateButton.Left = check.Right + 8;
        };

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

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Profile", HeaderText = "Profile", Width = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Account", HeaderText = "Tài khoản", Width = 155 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RunState", HeaderText = "Trạng thái", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Chrome", HeaderText = "Chrome", Width = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Viewer", HeaderText = "Viewer", Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Step", HeaderText = "Bước", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rounds", HeaderText = "Vòng", Width = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ram", HeaderText = "RAM chính", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detail", HeaderText = "Chi tiết", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
        LogGridSchema(grid, "DashboardGrid", "Profile", "Account", "RunState", "Chrome", "Viewer", "Step", "Rounds", "Ram", "Detail");

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
            await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(30));
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

    void RefreshDashboard(bool forceAccountRefresh = false)
    {
        if (IsDisposed || Disposing || _dashboardGrid is null || _dashboardGrid.IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => RefreshDashboard(forceAccountRefresh))); } catch { }
            return;
        }

        if (forceAccountRefresh || DateTime.UtcNow - _dashboardAccountCacheUtc > TimeSpan.FromSeconds(15))
        {
            _dashboardAccountCache.Clear();
            _dashboardAccountCacheUtc = DateTime.UtcNow;
        }

        var selectedName = DashboardSelectedContext()?.Profile.Name;
        var allContexts = _contexts.Values.OrderBy(c => c.Profile.Name, NaturalProfileNameOrder).ToList();
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
            _dashboardGrid.Rows.Clear();
            foreach (var ctx in contexts)
            {
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
                var viewerText = viewer < 0 ? "—" : FormatDashboardViewer(viewer);
                var stepText = snapshot is null || snapshot.Step <= 0 ? "—" : snapshot.Step.ToString();
                var roundsText = snapshot is null ? "—" : snapshot.Rounds.ToString();
                var chrome = snapshot?.Chrome ?? "—";
                var ram = GetDashboardPrimaryRamMb(ctx, snapshot);
                var rowIndex = _dashboardGrid.Rows.Add(
                    ctx.Profile.Name,
                    string.IsNullOrWhiteSpace(account) ? "—" : account,
                    runState,
                    chrome,
                    viewerText,
                    stepText,
                    roundsText,
                    ram < 0 ? "—" : $"{ram:N0} MB",
                    detail);
                var row = _dashboardGrid.Rows[rowIndex];
                row.Tag = ctx;

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

                if (!string.IsNullOrWhiteSpace(selectedName) && ctx.Profile.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
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
            Text = $"Cấu hình cập nhật — V{ManagerDisplayVersion}",
            Width = 760,
            Height = 390,
            MinimumSize = new Size(680, 350),
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
            RowCount = 7,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "Nhập URL manifest cập nhật. Nếu để trống, chức năng kiểm tra cập nhật sẽ tắt. Phần cách tạo manifest/đưa Setup lên mạng sẽ cấu hình sau.",
            Margin = new Padding(0, 0, 0, 10)
        };
        ModernDialog.StylePrimaryLabel(intro);

        var urlLabel = new Label { Text = "Manifest URL", AutoSize = true };
        var url = new TextBox { Dock = DockStyle.Top, Text = settings.ManifestUrl };
        ModernDialog.StyleTextInput(url);

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var channelLabel = new Label { Text = "Kênh:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) };
        var channel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        channel.Items.AddRange(new object[] { "stable", "test" });
        channel.SelectedItem = settings.Channel.Equals("test", StringComparison.OrdinalIgnoreCase) ? "test" : "stable";
        ModernDialog.StyleSelectionInput(channel);
        var autoCheck = new CheckBox { Text = "Tự kiểm tra khi mở Manager", Checked = settings.AutoCheck, AutoSize = true, Margin = new Padding(18, 7, 0, 0) };
        options.Controls.Add(channelLabel);
        options.Controls.Add(channel);
        options.Controls.Add(autoCheck);

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Manifest dự kiến: version + setupUrl + sha256 + notes + channel.",
            MaximumSize = new Size(700, 0),
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
        root.Controls.Add(urlLabel, 0, 1);
        root.Controls.Add(url, 0, 2);
        root.Controls.Add(options, 0, 3);
        root.Controls.Add(hint, 0, 4);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 5);
        root.Controls.Add(buttons, 0, 6);
        form.Controls.Add(root);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);

        if (form.ShowDialog(this) != DialogResult.OK) return;
        settings.ManifestUrl = url.Text.Trim();
        settings.Channel = channel.SelectedItem?.ToString() ?? "stable";
        settings.AutoCheck = autoCheck.Checked;
        try
        {
            SaveUpdateSettings(settings);
            _latestUpdate = null;
            RefreshUpdatePanel(settings);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    void RefreshUpdatePanel(UpdateSettings? settings = null)
    {
        if (_dashboardUpdateStatus is null || _dashboardUpdateStatus.IsDisposed) return;
        settings ??= LoadUpdateSettings();
        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            _dashboardUpdateStatus.Text = $"V{ManagerDisplayVersion} — Cập nhật: chưa cấu hình nguồn cập nhật.";
            _dashboardUpdateStatus.ForeColor = Color.DimGray;
            if (_dashboardUpdateButton is not null) _dashboardUpdateButton.Visible = false;
            return;
        }

        if (_latestUpdate is not null && IsVersionNewer(_latestUpdate.Version, ManagerDisplayVersion))
        {
            _dashboardUpdateStatus.Text = $"V{ManagerDisplayVersion} → có bản V{_latestUpdate.Version} ({_latestUpdate.Channel}).";
            _dashboardUpdateStatus.ForeColor = Color.DarkGreen;
            if (_dashboardUpdateButton is not null) _dashboardUpdateButton.Visible = true;
        }
        else
        {
            _dashboardUpdateStatus.Text = $"V{ManagerDisplayVersion} — nguồn cập nhật đã cấu hình ({settings.Channel}).";
            _dashboardUpdateStatus.ForeColor = Color.FromArgb(55, 76, 103);
            if (_dashboardUpdateButton is not null) _dashboardUpdateButton.Visible = false;
        }
    }

    async Task CheckForUpdatesAsync(bool showWhenCurrent)
    {
        if (_updateCheckInProgress) return;
        var settings = LoadUpdateSettings();
        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            ModernDialog.ShowMessage(this, "Chưa cấu hình Manifest URL. Hãy bấm ‘Cấu hình cập nhật’ trước.", "Cập nhật", MessageBoxIcon.Information);
            return;
        }

        if (!Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var manifestUri)
            || (manifestUri.Scheme != Uri.UriSchemeHttps && manifestUri.Scheme != Uri.UriSchemeHttp))
        {
            ModernDialog.ShowMessage(this, "Manifest URL không hợp lệ. URL phải bắt đầu bằng https:// hoặc http://.", "Cập nhật", MessageBoxIcon.Warning);
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            if (_dashboardUpdateStatus is not null)
            {
                _dashboardUpdateStatus.Text = "Đang kiểm tra cập nhật...";
                _dashboardUpdateStatus.ForeColor = Color.DarkOrange;
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ToolTikTokManager/{AppVersionInfo.Current}");
            var json = await client.GetStringAsync(manifestUri);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.SetupUrl))
                throw new InvalidDataException("Manifest thiếu version hoặc setupUrl.");

            if (!string.IsNullOrWhiteSpace(manifest.Channel)
                && !manifest.Channel.Equals(settings.Channel, StringComparison.OrdinalIgnoreCase))
            {
                _latestUpdate = null;
                RefreshUpdatePanel(settings);
                if (showWhenCurrent)
                    ModernDialog.ShowMessage(this, $"Manifest hiện thuộc kênh {manifest.Channel}, trong khi Manager đang chọn kênh {settings.Channel}.", "Cập nhật", MessageBoxIcon.Information);
                return;
            }

            _latestUpdate = manifest;
            RefreshUpdatePanel(settings);
            if (IsVersionNewer(manifest.Version, ManagerDisplayVersion))
            {
                if (showWhenCurrent)
                {
                    var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "Không có ghi chú phiên bản." : manifest.Notes.Trim();
                    ModernDialog.ShowMessage(this, $"Có bản mới V{manifest.Version}.\n\n{notes}\n\nBấm ‘Tải & cập nhật’ trên Tổng quan để cài.", "Có bản cập nhật", MessageBoxIcon.Information);
                }
            }
            else if (showWhenCurrent)
            {
                ModernDialog.ShowMessage(this, $"Bạn đang dùng V{ManagerDisplayVersion}. Chưa có bản mới hơn trên kênh {settings.Channel}.", "Cập nhật", MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _latestUpdate = null;
            if (_dashboardUpdateStatus is not null)
            {
                _dashboardUpdateStatus.Text = "Kiểm tra cập nhật thất bại: " + ex.Message;
                _dashboardUpdateStatus.ForeColor = Color.Firebrick;
            }
            _log.Warn("[UPDATE_CHECK_FAILED] " + ex);
            if (showWhenCurrent)
                ModernDialog.ShowMessage(this, "Không kiểm tra được cập nhật.\n\n" + ex.Message, "Cập nhật", MessageBoxIcon.Warning);
        }
        finally { _updateCheckInProgress = false; }
    }

    async Task DownloadAndInstallLatestUpdateAsync()
    {
        if (_updateDownloadInProgress) return;
        var manifest = _latestUpdate;
        if (manifest is null || !IsVersionNewer(manifest.Version, ManagerDisplayVersion))
        {
            await CheckForUpdatesAsync(showWhenCurrent: true);
            manifest = _latestUpdate;
            if (manifest is null || !IsVersionNewer(manifest.Version, ManagerDisplayVersion)) return;
        }

        if (!Uri.TryCreate(manifest.SetupUrl, UriKind.Absolute, out var setupUri)
            || (setupUri.Scheme != Uri.UriSchemeHttps && setupUri.Scheme != Uri.UriSchemeHttp))
        {
            ModernDialog.ShowMessage(this, "setupUrl trong manifest không hợp lệ.", "Cập nhật", MessageBoxIcon.Warning);
            return;
        }

        var confirmText = $"Tải và cài Tool TikTok V{manifest.Version}?\n\nManager sẽ tải Setup, kiểm tra SHA-256 nếu manifest có cung cấp, rồi mở bộ cài. Các Worker sẽ được dừng trước khi cài.";
        if (ModernDialog.ShowConfirm(this, confirmText, "Cập nhật Tool") != DialogResult.Yes) return;

        _updateDownloadInProgress = true;
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
                            ? $"Đang tải V{manifest.Version}: {written * 100 / total.Value}%"
                            : $"Đang tải V{manifest.Version}: {written / 1024 / 1024} MB";
                    }
                }
            }

            var expectedHash = NormalizeSha256(manifest.Sha256);
            if (expectedHash.Length > 0)
            {
                var actual = ComputeSha256(temp);
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 không khớp. Expected={expectedHash}; Actual={actual}");
            }
            else
            {
                _log.Warn("[UPDATE_NO_SHA256] Manifest không có sha256; Setup chỉ được xác thực bằng HTTPS/HTTP response.");
            }

            File.Move(temp, destination, true);
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

            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(destination) ?? updateDir
            });
            _log.Info($"[UPDATE_SETUP_LAUNCHED] version={manifest.Version} path={destination}");

            BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            _log.Error("[UPDATE_INSTALL_FAILED] " + ex);
            ModernDialog.ShowMessage(this, "Không tải/cài được bản cập nhật.\n\n" + ex.Message, "Cập nhật", MessageBoxIcon.Warning);
            RefreshUpdatePanel();
        }
        finally { _updateDownloadInProgress = false; }
    }

    static bool IsVersionNewer(string candidate, string current)
    {
        static Version? Parse(string raw)
        {
            raw = (raw ?? "").Trim().TrimStart('v', 'V');
            var core = raw.Split('-', '+')[0];
            return Version.TryParse(core, out var version) ? version : null;
        }
        var left = Parse(candidate);
        var right = Parse(current);
        return left is not null && right is not null && left > right;
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
