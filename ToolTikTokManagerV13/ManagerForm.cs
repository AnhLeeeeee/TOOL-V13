using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Models;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm : Form
{
    sealed class ProfileContext
    {
        public required TikTokProfileEntry Profile { get; set; }
        public TabPage? Tab { get; set; }
        public Panel? Host { get; set; }
        public Label? ProfileHeader { get; set; }
        public Label? Status { get; set; }
        public Button? DetachButton { get; set; }
        public Process? Worker { get; set; }
        public IntPtr WorkerWindow { get; set; }
        public bool Detached { get; set; }
        public bool Opening { get; set; }
        public bool EmbedRecoveryInProgress { get; set; }
        public DateTime LastEmbedRecoveryUtc { get; set; } = DateTime.MinValue;
        public int ConsecutiveEmbedRecoveryFailures { get; set; }
        public DateTime LastStatusRefreshUtc { get; set; } = DateTime.MinValue;
        public DateTime LastStatusPollAttemptUtc { get; set; } = DateTime.MinValue;
        public WorkerSnapshot? LastSnapshot { get; set; }
        public string LastConfirmedRuntimeState { get; set; } = RuntimeStateUnknown;
        public DateTime LastConfirmedRuntimeStateUtc { get; set; } = DateTime.MinValue;
        public bool RuntimeRecoveryInProgress { get; set; }
        public int ConsecutiveStatusPollFailures { get; set; }
        public string LastStatusPollFailure { get; set; } = "";
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
    }

    sealed class DeleteProfileListItem
    {
        public required ProfileContext Context { get; init; }
        public override string ToString() => $"{Context.Profile.Name}    |    {Context.Profile.ProfilePath}";
    }

    sealed class OpenProfileListItem
    {
        public required ProfileContext Context { get; init; }
        public override string ToString() => Context.Profile.Name;
    }

    sealed record ProfileDeletionPlan(ProfileContext Context, TikTokProfileEntry Profile, string ChromeProfilePath, string DataRoot);
    sealed record ChromeNameSyncRuntimeState(bool ChromeWasOpen, bool AutomationWasRunning);
    sealed record ProfileOpenSelection(bool IsMultiple, IReadOnlyList<ProfileContext> Contexts);
    sealed record BatchOpenResult(string ProfileName, bool Opened, bool Skipped, string? Error = null);
    sealed record ProfileCreateRequest(string Name, string Username, string Password, string TotpSecret, bool AutoLogin, string? AccountPoolId);

    sealed class NaturalProfileNameComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftChar = left[leftIndex];
                var rightChar = right[rightIndex];
                if (char.IsDigit(leftChar) && char.IsDigit(rightChar))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                    var leftDigits = left[leftStart..leftIndex].TrimStart('0');
                    var rightDigits = right[rightStart..rightIndex].TrimStart('0');
                    leftDigits = leftDigits.Length == 0 ? "0" : leftDigits;
                    rightDigits = rightDigits.Length == 0 ? "0" : rightDigits;
                    var digitLengthCompare = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (digitLengthCompare != 0) return digitLengthCompare;
                    var digitCompare = string.Compare(leftDigits, rightDigits, StringComparison.Ordinal);
                    if (digitCompare != 0) return digitCompare;

                    var originalLengthCompare = (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
                    if (originalLengthCompare != 0) return originalLengthCompare;
                    continue;
                }

                var charCompare = char.ToUpperInvariant(leftChar).CompareTo(char.ToUpperInvariant(rightChar));
                if (charCompare != 0) return charCompare;
                leftIndex++;
                rightIndex++;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    sealed class AddTabMarker { }
    readonly string _baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    readonly TikTokProfileService _profileService;
    readonly ChromeProfileNameSyncService _chromeProfileNameSync = new();
    readonly TikTokAuthService _tiktokAuthService = new();
    readonly TikTokAccountPoolService _accountPoolService;
    readonly Logger _log;
    readonly Dictionary<string, ProfileContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, Padding = new Point(18, 6) };
    readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000, Enabled = true };
    readonly Label _availability = new() { AutoSize = true, Margin = new Padding(12, 9, 4, 0), ForeColor = Color.DimGray };
    readonly AddTabMarker _addMarker = new();
    static readonly Color ActiveProfileColor = Color.FromArgb(57, 111, 178);
    static readonly Color InactiveTabColor = Color.FromArgb(238, 242, 247);
    static readonly TimeSpan ChromeCloseTimeout = TimeSpan.FromSeconds(30);
    static readonly NaturalProfileNameComparer NaturalProfileNameOrder = new();
    static readonly JsonSerializerOptions WorkerSnapshotJson = new() { PropertyNameCaseInsensitive = true };
    bool _changingTabs;
    bool _refreshing;
    bool _closing;
    bool _profileRenameInProgress;
    ChromeMonitorForm? _chromeMonitor;
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_CHROME_MONITOR_TOGGLE = 0x1348;
    bool _chromeMonitorHotkeyRegistered;

    public ManagerForm()
    {
        LegacyDataMigration.TryImportLegacyCatalog(_baseDir);
        _profileService = new TikTokProfileService(_baseDir);
        _accountPoolService = new TikTokAccountPoolService(_baseDir);
        _log = new Logger(_baseDir, "manager", "manager-v13.log");
        Text = $"Tool TikTok Manager {AppVersionInfo.Display} — VM Optimized Multi Worker";
        // Phải bật DPI scaling trước khi tạo layout/handle để kéo qua màn hình
        // có Scale khác (100%/125%/150%) không giữ kích thước cache cũ.
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        InitializeMonitorRelayoutHooks();
        ReloadCatalog();
        EnsureAddTab();
        InitializeDashboardAndUpdater();
        _refreshTimer.Tick += async (_, _) => await RefreshOpenProfilesAsync();
        InitializeIdentityAutoFlow();
        InitializeMessageReplyAutoFlow();
        Shown += (_, _) => RegisterChromeMonitorHotkey();
        FormClosing += OnClosing;
    }

    void BuildLayout()
    {
        BackColor = UiTheme.Canvas;
        Font = new Font("Segoe UI", 9F);

        // Manager V13.5: cố định toolbar thành đúng 2 hàng để các nút không bị dồn/lệch
        // theo độ rộng cửa sổ hoặc DPI của máy ảo.
        // Không dùng AutoSize cho hàng toolbar có AutoScroll. WinForms có thể
        // tính chiều cao trước khi horizontal scrollbar xuất hiện, làm cắt nửa
        // hàng nút khi cửa sổ mở ở kích thước hẹp. Chiều cao 2 hàng sẽ được
        // UpdateMainToolbarLayout() tính lại theo DPI + nhu cầu scrollbar.
        var toolbarHost = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 92,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0),
            BackColor = UiTheme.Card
        };
        toolbarHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        toolbarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        toolbarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

        FlowLayoutPanel ToolbarRow() => new()
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 40,
            WrapContents = false,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.Card
        };

        var toolbarRow1 = ToolbarRow();
        toolbarRow1.Controls.Add(Button("Mở profile", (_, _) => OpenProfileChooser(), UiButtonKind.Primary));
        toolbarRow1.Controls.Add(Button("+ Profile", (_, _) => AddProfile(), UiButtonKind.Primary));
        toolbarRow1.Controls.Add(Button("+ Auto Profile", (_, _) => ShowAutoProfileDialog(), UiButtonKind.Primary));
        toolbarRow1.Controls.Add(Button("Kho tài khoản", (_, _) => ShowAccountPoolDialog(), UiButtonKind.Neutral));
        toolbarRow1.Controls.Add(Button("Cấu hình mặc định", (_, _) => ShowDefaultConfigDialog(), UiButtonKind.Neutral));
        toolbarRow1.Controls.Add(Button("Tên & ảnh TikTok", (_, _) => ShowTikTokIdentityDialog(), UiButtonKind.Neutral));
        toolbarRow1.Controls.Add(Button("Tin nhắn TikTok", (_, _) => ShowTikTokMessageReplyDialog(), UiButtonKind.Neutral));
        toolbarRow1.Controls.Add(Button("Profile có sẵn", async (_, _) => { try { await AddExistingProfileAsync(); } catch (Exception ex) { ShowError(ex); } }));

        var toolbarRow2 = ToolbarRow();
        toolbarRow2.Controls.Add(Button("Đổi tên", async (_, _) => { try { await RenameSelectedProfileAsync(); } catch (Exception ex) { ShowError(ex); } }));
        toolbarRow2.Controls.Add(Button("Đồng bộ tên Chrome", (_, _) => ShowChromeNameSyncDialog()));
        toolbarRow2.Controls.Add(Button("Giám sát Chrome", (_, _) => ShowChromeMonitor(), UiButtonKind.Primary));
        toolbarRow2.Controls.Add(Button("Xóa profile", (_, _) => ShowDeleteProfilesDialog(), UiButtonKind.Danger));
        toolbarRow2.Controls.Add(Button("Chạy tất cả", async (_, _) => await StartAllAsync(), UiButtonKind.Primary));
        toolbarRow2.Controls.Add(Button("Dừng tất cả", async (_, _) => await StopAllAsync(), UiButtonKind.Danger));
        toolbarRow2.Controls.Add(_availability);

        toolbarHost.Controls.Add(toolbarRow1, 0, 0);
        toolbarHost.Controls.Add(toolbarRow2, 0, 1);
        RegisterResponsiveToolbar(toolbarHost, toolbarRow1, toolbarRow2);

        _tabs.DrawItem += DrawTabs;
        _tabs.MouseDown += OnTabsMouseDown;
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_changingTabs) return;
            if (TryGetSelectedTabPage(out var selectedPage) && IsAddTab(selectedPage)) await HandleAddTabAsync();
            UpdateTitle();
        };
        Controls.Add(_tabs);
        Controls.Add(toolbarHost);
        UiTheme.Apply(this);
        StyleToolbarButtons(toolbarRow1);
        StyleToolbarButtons(toolbarRow2);
    }

    Button Button(string text, EventHandler action, UiButtonKind kind = UiButtonKind.Neutral)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(4) };
        UiTheme.StyleButton(b, kind);
        b.Click += (_, e) => { try { action(b, e); } catch (Exception ex) { ShowError(ex); } };
        return b;
    }

    static void StyleToolbarButtons(FlowLayoutPanel toolbar)
    {
        foreach (var button in toolbar.Controls.OfType<Button>())
        {
            var (background, foreground) = button.Text switch
            {
                "Mở profile" => (Color.FromArgb(232, 242, 255), Color.FromArgb(35, 91, 152)),
                "+ Profile" or "+ Auto Profile" => (Color.FromArgb(238, 246, 255), Color.FromArgb(35, 91, 152)),
                "Profile có sẵn" or "Đổi tên" or "Đồng bộ tên Chrome" or "Kho tài khoản" or "Cấu hình mặc định" or "Tên & ảnh TikTok" or "Tin nhắn TikTok" => (Color.FromArgb(242, 246, 251), Color.FromArgb(55, 76, 103)),
                "Giám sát Chrome" => (Color.FromArgb(234, 244, 255), Color.FromArgb(31, 91, 158)),
                "Xóa profile" or "Dừng tất cả" => (Color.FromArgb(255, 239, 239), Color.FromArgb(171, 62, 62)),
                "Chạy tất cả" => (Color.FromArgb(234, 248, 238), Color.FromArgb(36, 119, 66)),
                _ => (UiTheme.Card, Color.FromArgb(42, 57, 76))
            };
            button.BackColor = background;
            button.ForeColor = foreground;
            button.FlatAppearance.BorderColor = ControlPaint.Dark(background);
            button.FlatAppearance.MouseOverBackColor = ControlPaint.LightLight(background);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(background);
        }
    }

    void ReloadCatalog()
    {
        var catalog = _profileService.Load();
        foreach (var warning in _profileService.LastLoadWarnings)
            _log.Warn(warning);
        // Load() already normalizes/allocates ports and SaveWithBackup() also
        // validates them before persistence.  Avoid a third identical pass.
        _profileService.SaveWithBackup(catalog);
        RefreshContextsFromCatalog(catalog);
    }

    // Only refreshes the in-memory index.  In particular, a completed rename
    // must not call Load()/discovery again between writing profiles.json and
    // updating its existing ProfileContext.
    void RefreshContextsFromCatalog(TikTokProfileCatalog catalog)
    {
        var alive = new HashSet<string>(catalog.Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in catalog.Profiles)
        {
            if (_contexts.TryGetValue(p.Name, out var ctx)) ctx.Profile = p;
            else _contexts[p.Name] = new ProfileContext { Profile = p };
        }
        foreach (var stale in _contexts.Keys.Where(k => !alive.Contains(k)).ToList())
            _contexts.Remove(stale);
        RefreshAvailability();
    }

    void RefreshAvailability()
    {
        var count = _contexts.Values.Count(c => c.Tab is null);
        _availability.Text = count == 0 ? "Không còn profile chưa mở" : $"Profile chưa mở: {count}";
    }

    void EnsureAddTab()
    {
        if (_tabs.TabPages.Cast<TabPage>().Any(p => ReferenceEquals(p.Tag, _addMarker))) return;
        _tabs.TabPages.Add(new TabPage("+") { Tag = _addMarker });
    }
    bool IsAddTab(TabPage? page) => page is not null && ReferenceEquals(page.Tag, _addMarker);
    TabPage AddTab() => _tabs.TabPages.Cast<TabPage>().First(p => IsAddTab(p));

    async Task HandleAddTabAsync()
    {
        try { _changingTabs = true; OpenProfileChooser(); }
        finally
        {
            _changingTabs = false;
            var first = _contexts.Values.FirstOrDefault(c => c.Tab is not null)?.Tab;
            if (first is not null) SelectTabPageSafely(first);
        }
        await Task.CompletedTask;
    }

    void DrawTabs(object? sender, DrawItemEventArgs e)
    {
        if (_tabs.IsDisposed || _tabs.Disposing || !_tabs.IsHandleCreated) return;
        var tabCount = _tabs.TabPages.Count;
        if (e.Index < 0 || e.Index >= tabCount) return;

        TabPage page;
        try { page = _tabs.TabPages[e.Index]; }
        catch (ArgumentOutOfRangeException) { return; }
        catch (ObjectDisposedException) { return; }
        if (page.IsDisposed) return;

        var selectedIndex = GetSafeSelectedIndex(tabCount);
        // Chỉ tab profile mới có nút đóng. Tab Tổng quan vẫn được tô trạng thái active, tab + thì không.
        var closeable = page.Tag is ProfileContext;
        var selectable = !IsAddTab(page);
        var active = selectable && selectedIndex >= 0 && e.Index == selectedIndex;
        var background = active ? ActiveProfileColor : InactiveTabColor;
        var foreground = active ? Color.White : SystemColors.ControlText;
        using (var backgroundBrush = new SolidBrush(background))
            e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        using (var border = new Pen(active ? ActiveProfileColor : UiTheme.Border))
            e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - (closeable ? 24 : 10), e.Bounds.Height - 8);
        var profileName = page.Tag is ProfileContext context ? context.Profile.Name : page.Text;
        var text = active ? "● " + profileName : profileName;
        using var tabFont = new Font(Font, active ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, text, tabFont, textRect, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (closeable)
        {
            var close = GetCloseRect(e.Bounds);
            TextRenderer.DrawText(e.Graphics, "×", tabFont, close, active ? Color.White : Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    int GetSafeSelectedIndex(int? knownTabCount = null)
    {
        if (_tabs.IsDisposed || _tabs.Disposing) return -1;
        var tabCount = knownTabCount ?? _tabs.TabPages.Count;
        if (tabCount <= 0) return -1;
        try
        {
            var selectedIndex = _tabs.SelectedIndex;
            return selectedIndex >= 0 && selectedIndex < tabCount ? selectedIndex : -1;
        }
        catch (ArgumentOutOfRangeException) { return -1; }
        catch (ObjectDisposedException) { return -1; }
    }

    bool TryGetSelectedTabPage(out TabPage? page)
    {
        page = null;
        if (_tabs.IsDisposed || _tabs.Disposing) return false;
        var tabCount = _tabs.TabPages.Count;
        var selectedIndex = GetSafeSelectedIndex(tabCount);
        if (selectedIndex < 0) return false;
        try
        {
            page = _tabs.TabPages[selectedIndex];
            return !page.IsDisposed;
        }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    void SelectTabPageSafely(TabPage page)
    {
        if (_tabs.IsDisposed || _tabs.Disposing || page.IsDisposed) return;
        var index = _tabs.TabPages.IndexOf(page);
        if (index < 0) return;
        try { _tabs.SelectedIndex = index; }
        catch (ArgumentOutOfRangeException) { }
        catch (ObjectDisposedException) { }
    }

    void NormalizeSelectionAfterTabRemoval(int removedIndex, int previousSelectedIndex)
    {
        if (_tabs.IsDisposed || _tabs.Disposing) return;
        var tabCount = _tabs.TabPages.Count;
        var targetIndex = -1;
        if (tabCount > 0)
        {
            if (previousSelectedIndex >= 0)
                targetIndex = previousSelectedIndex > removedIndex ? previousSelectedIndex - 1 : previousSelectedIndex;
            else targetIndex = removedIndex;
            targetIndex = Math.Clamp(targetIndex, 0, tabCount - 1);
        }
        try { _tabs.SelectedIndex = targetIndex; }
        catch (ArgumentOutOfRangeException) { }
        catch (ObjectDisposedException) { }
    }

    void OnTabsMouseDown(object? sender, MouseEventArgs e)
    {
        for (var i = 0; i < _tabs.TabPages.Count; i++)
        {
            var rect = _tabs.GetTabRect(i);
            if (!rect.Contains(e.Location)) continue;
            var page = _tabs.TabPages[i];
            if (IsAddTab(page) || !GetCloseRect(rect).Contains(e.Location)) return;
            if (page.Tag is ProfileContext ctx) _ = CloseProfileAsync(ctx);
            return;
        }
    }
    static Rectangle GetCloseRect(Rectangle tab) => new(tab.Right - 18, tab.Top + 6, 12, Math.Max(12, tab.Height - 12));

    void EnsureTab(ProfileContext ctx)
    {
        if (ctx.Tab is not null && ctx.Tab.Parent == _tabs) return;
        var page = new TabPage(ctx.Profile.Name) { Tag = ctx, BackColor = UiTheme.Canvas };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(6, 3, 6, 3), BackColor = UiTheme.Card };
        var profileHeader = new Label
        {
            AutoSize = false,
            Size = new Size(320, 32),
            MinimumSize = new Size(320, 32),
            MaximumSize = new Size(320, 32),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(4, 4, 10, 0),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        // Status không được phép đẩy nút Tách Worker ra ngoài khi cửa sổ vừa
        // chuyển từ màn nhỏ sang màn lớn và vẫn đang ở gần MinimumSize.
        var status = new Label
        {
            AutoSize = false,
            Size = new Size(210, 32),
            Text = "Worker: đang khởi động",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(8, 4, 12, 0)
        };
        var openChrome = Button("Mở Chrome", async (_, _) => { try { await OpenChromeForProfileAsync(ctx); } catch (Exception ex) { ShowError(ex); } }, UiButtonKind.Primary);
        var closeChrome = Button("Đóng Chrome", async (_, _) => { try { await CloseChromeForProfileAsync(ctx); } catch (Exception ex) { ShowError(ex); } }, UiButtonKind.Danger);
        var viewChrome = Button("👁 View", async (_, _) => { try { await ViewChromeForProfileAsync(ctx); } catch (Exception ex) { ShowError(ex); } }, UiButtonKind.Neutral);
        var account = Button("🔐 Tài khoản", (_, _) => ConfigureTikTokAccount(ctx));
        var detach = Button("Tách Worker", (_, _) => ToggleDetach(ctx));
        top.Controls.Add(profileHeader);
        top.Controls.Add(openChrome);
        top.Controls.Add(closeChrome);
        top.Controls.Add(viewChrome);
        top.Controls.Add(account);
        top.Controls.Add(detach);
        top.Controls.Add(status);
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        host.Resize += (_, _) => { if (!ctx.Detached) WorkerWindowEmbedder.Resize(ctx.WorkerWindow, host.ClientSize); };
        page.Controls.Add(host);
        page.Controls.Add(top);
        ctx.Tab = page; ctx.Host = host; ctx.ProfileHeader = profileHeader; ctx.Status = status; ctx.DetachButton = detach;
        var add = AddTab();
        _tabs.TabPages.Insert(Math.Max(0, _tabs.TabPages.IndexOf(add)), page);
        SelectTabPageSafely(page);
        RefreshSelectedProfilePresentation();
        RefreshAvailability();
    }

    void OpenProfileChooser()
    {
        var candidates = _contexts.Values
            .Where(context => context.Tab is null)
            .OrderBy(context => context.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) { RefreshAvailability(); return; }

        var selection = ChooseProfiles(candidates, "Mở profile");
        if (selection is null || selection.Contexts.Count == 0) return;
        if (selection.IsMultiple)
            _ = OpenProfilesSequentiallyAsync(selection.Contexts);
        else
            _ = OpenProfileAsync(selection.Contexts[0]);
    }

    async Task<bool> OpenProfileAsync(ProfileContext ctx, string? openingStatus = null)
    {
        if (_profileRenameInProgress)
            throw new InvalidOperationException("Đang đổi tên profile. Hãy chờ thao tác hoàn tất trước khi mở Worker/profile khác.");
        if (ctx.Opening)
        {
            _log.Info($"[PROFILE_OPEN_SKIP] profile={ctx.Profile.Name} reason=already_opening");
            return false;
        }

        ctx.Opening = true;
        try
        {
            EnsureTab(ctx);
            if (ctx.Tab is not null) SelectTabPageSafely(ctx.Tab);
            if (!string.IsNullOrWhiteSpace(openingStatus)) SetStatus(ctx, openingStatus, Color.DarkOrange);
            LegacyDataMigration.TryImportLegacyProfileData(_baseDir, ctx.Profile.Name, _profileService.ResolveDataRoot(ctx.Profile));
            await EnsureWorkerAsync(ctx);
            await RefreshStatusAsync(ctx);
            await EmbedWorkerAsync(ctx);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(ctx, "Worker lỗi: " + ex.Message, Color.Firebrick);
            _log.Error($"[{ctx.Profile.Name}] {ex}");
            return false;
        }
        finally { ctx.Opening = false; }
    }

    async Task OpenProfilesSequentiallyAsync(IReadOnlyList<ProfileContext> selectedContexts)
    {
        if (_profileRenameInProgress)
        {
            MessageBox.Show(this, "Đang đổi tên profile. Hãy chờ thao tác hoàn tất trước khi mở nhiều profile.", "Mở profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ordered = selectedContexts
            .Distinct()
            .OrderBy(context => context.Profile.Name, NaturalProfileNameOrder)
            .ToList();
        if (ordered.Count == 0)
        {
            MessageBox.Show(this, "Vui lòng chọn ít nhất một profile.", "Mở profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        const int staggerMs = 300;
        var started = new List<Task<BatchOpenResult>>();
        var immediateResults = new List<BatchOpenResult>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var context = ordered[index];
            if (context.Tab is not null || (context.Worker is not null && !context.Worker.HasExited))
            {
                var skip = new BatchOpenResult(context.Profile.Name, Opened: false, Skipped: true, "đã có tab hoặc Worker đang mở");
                immediateResults.Add(skip);
                _log.Info($"[BATCH_OPEN_SKIP] profile={context.Profile.Name} reason=already_open_or_worker_running");
                continue;
            }
            if (context.Opening)
            {
                var skip = new BatchOpenResult(context.Profile.Name, Opened: false, Skipped: true, "đang được mở bởi thao tác khác");
                immediateResults.Add(skip);
                _log.Info($"[BATCH_OPEN_SKIP] profile={context.Profile.Name} reason=already_opening");
                continue;
            }

            started.Add(OpenBatchProfileAsync(context, index + 1, ordered.Count));
            await Task.Delay(staggerMs);
        }

        var results = new List<BatchOpenResult>(immediateResults);
        // The profiles were already started with a stagger above.  Awaiting the
        // individual tasks here collects the final result without launching a
        // second Worker/Chrome or using Task.WhenAll to start them together.
        foreach (var task in started)
            results.Add(await task);

        var openedCount = results.Count(result => result.Opened);
        var skipped = results.Where(result => result.Skipped).ToList();
        var failed = results.Where(result => !result.Opened && !result.Skipped).ToList();
        var summary = $"Đã mở {openedCount}/{ordered.Count} profile.\nBỏ qua: {skipped.Count}.\nLỗi: {failed.Count}.";
        _log.Info($"[BATCH_OPEN_COMPLETE] requested={ordered.Count} opened={openedCount} skipped={skipped.Count} failed={failed.Count} order={string.Join(" -> ", ordered.Select(context => context.Profile.Name))}");
        if (failed.Count > 0)
            _log.Warn("[BATCH_OPEN_FAILED] " + string.Join("; ", failed.Select(result => $"{result.ProfileName}: {result.Error}")));
        if (!IsDisposed)
            MessageBox.Show(this, summary, "Mở profile", MessageBoxButtons.OK, failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    async Task<BatchOpenResult> OpenBatchProfileAsync(ProfileContext context, int index, int total)
    {
        var progress = $"Đang mở profile {index}/{total}: {context.Profile.Name}";
        _log.Info("[BATCH_OPEN_START] " + progress);
        try
        {
            var opened = await OpenProfileAsync(context, progress);
            if (opened)
            {
                _log.Info($"[BATCH_OPEN_OPENED] profile={context.Profile.Name} position={index}/{total}");
                return new BatchOpenResult(context.Profile.Name, Opened: true, Skipped: false);
            }
            return new BatchOpenResult(context.Profile.Name, Opened: false, Skipped: false, "Worker/Chrome không khởi động được; xem trạng thái tab hoặc nhật ký Manager.");
        }
        catch (Exception ex)
        {
            _log.Error($"[BATCH_OPEN_ERROR] profile={context.Profile.Name} position={index}/{total} {ex}");
            return new BatchOpenResult(context.Profile.Name, Opened: false, Skipped: false, ex.Message);
        }
    }

    async Task OpenChromeForProfileAsync(ProfileContext ctx)
    {
        SetStatus(ctx, "Đang mở Chrome của profile này...", Color.DarkOrange);
        var result = await SendCommandAsync(ctx, "launch", TimeSpan.FromSeconds(75));
        if (string.Equals(result, "captcha_required", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(ctx, "Chrome đã mở — cần xử lý CAPTCHA trên TikTok.", Color.DarkOrange);
            ModernDialog.ShowMessage(this, $"Profile {ctx.Profile.Name} đang gặp CAPTCHA. Hãy xử lý CAPTCHA trực tiếp trên Chrome; tool sẽ chờ và tự tiếp tục khi CAPTCHA biến mất.", "TikTok CAPTCHA", MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(result, "totp_required", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(ctx, "Chrome đã mở — thiếu secret 2FA/TOTP.", Color.DarkOrange);
            ModernDialog.ShowMessage(this, $"Profile {ctx.Profile.Name} cần 2FA nhưng chưa có secret TOTP. Hãy bấm ‘🔐 Tài khoản’ ngay trong profile này để cấu hình.", "TikTok 2FA", MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(result, "login_required", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(ctx, "Chrome đã mở — chưa cấu hình tự đăng nhập.", Color.DarkOrange);
            return;
        }
        if (!string.Equals(result, "opened", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Chrome chưa mở/kết nối thành công cho profile “{ctx.Profile.Name}” (worker: {result}).");

        SetStatus(ctx, "Chrome đã kết nối — TikTok trang chủ, chưa vào LIVE.", Color.DarkGreen);
        _log.Info($"[CHROME_OPEN] profile={ctx.Profile.Name} profilePath={ctx.Profile.ProfilePath} port={ctx.Profile.CdpPort}");
        try { await RefreshStatusAsync(ctx); } catch (Exception ex) { _log.Warn($"[{ctx.Profile.Name}] refresh status sau mở Chrome: {ex.Message}"); }
    }

    async Task ViewChromeForProfileAsync(ProfileContext ctx)
    {
        try
        {
            try { await RefreshStatusAsync(ctx); } catch { }

            var chromeState = ctx.LastSnapshot?.Chrome ?? "DISCONNECTED";
            if (chromeState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"[VIEW_FAIL] profile={ctx.Profile.Name} cdpPort={ctx.Profile.CdpPort} reason=cdp_disconnected");
                ModernDialog.ShowMessage(this,
                    $"Chrome của profile “{ctx.Profile.Name}” chưa kết nối. Hãy mở/chạy profile trước rồi dùng ‘👁 View’.",
                    "View Chrome", MessageBoxIcon.Information);
                return;
            }

            var cachedHwndValue = ctx.LastSnapshot?.ChromeWindowHandle ?? 0;
            var cachedHwnd = cachedHwndValue > 0 ? new IntPtr(cachedHwndValue) : IntPtr.Zero;
            var cachedPid = ChromeMonitorWindowActions.GetProcessId(cachedHwnd);

            if (ChromeMonitorWindowActions.IsValid(cachedHwnd)
                && ChromeMonitorWindowActions.RestoreMaximizeAndActivate(cachedHwnd))
            {
                _log.Info($"[VIEW] profile={ctx.Profile.Name} cdpPort={ctx.Profile.CdpPort} cachedPid={cachedPid} resolvedPid={cachedPid} hwnd={cachedHwndValue} source=status_cache");
                return;
            }

            async Task<ChromeViewResolutionReply> ResolveChromeWindowOnDemandAsync()
            {
                await EnsureWorkerAsync(ctx);
                var result = await SendCommandAsync(ctx, "view_chrome", TimeSpan.FromSeconds(15));
                if (result.StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<ChromeViewResolutionReply>(
                            result,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? new ChromeViewResolutionReply { Reason = "invalid_worker_response" };
                    }
                    catch (JsonException ex)
                    {
                        return new ChromeViewResolutionReply { Reason = "invalid_worker_json:" + ex.Message };
                    }
                }

                // Tương thích Worker cũ đang chạy trong lúc Manager vừa được cập nhật.
                if (result.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(result.AsSpan(5), out var legacyHwnd)
                    && legacyHwnd > 0)
                {
                    var hwnd = new IntPtr(legacyHwnd);
                    return new ChromeViewResolutionReply
                    {
                        CachedPid = cachedPid,
                        ResolvedPid = ChromeMonitorWindowActions.GetProcessId(hwnd),
                        WindowHandle = legacyHwnd,
                        Reason = "legacy_worker"
                    };
                }

                return new ChromeViewResolutionReply { CachedPid = cachedPid, Reason = result };
            }

            var resolution = await ResolveChromeWindowOnDemandAsync();
            var resolvedHwnd = resolution.WindowHandle > 0 ? new IntPtr(resolution.WindowHandle) : IntPtr.Zero;
            var activated = ChromeMonitorWindowActions.RestoreMaximizeAndActivate(resolvedHwnd);

            // Handle có thể biến mất đúng lúc Worker vừa resolve xong. Resolve lại
            // một lần nữa; mỗi lệnh Worker đã tự EnumWindows 8 lần × 250 ms.
            if (!activated && resolution.WindowHandle > 0)
            {
                await Task.Delay(250);
                resolution = await ResolveChromeWindowOnDemandAsync();
                resolvedHwnd = resolution.WindowHandle > 0 ? new IntPtr(resolution.WindowHandle) : IntPtr.Zero;
                activated = ChromeMonitorWindowActions.RestoreMaximizeAndActivate(resolvedHwnd);
            }

            var effectiveCachedPid = resolution.CachedPid > 0 ? resolution.CachedPid : cachedPid;
            var resolvedPid = resolution.ResolvedPid > 0
                ? resolution.ResolvedPid
                : ChromeMonitorWindowActions.GetProcessId(resolvedHwnd);
            if (!activated)
            {
                var reason = string.IsNullOrWhiteSpace(resolution.Reason) ? "window_not_found" : resolution.Reason;
                _log.Warn($"[VIEW_FAIL] profile={ctx.Profile.Name} cdpPort={ctx.Profile.CdpPort} cachedPid={effectiveCachedPid} resolvedPid={resolvedPid} hwnd={resolution.WindowHandle} reason={reason}");
                ModernDialog.ShowMessage(this,
                    $"Chrome của profile “{ctx.Profile.Name}” vẫn kết nối nhưng Manager không tìm thấy cửa sổ Chrome đúng profile sau khi tự dò lại.",
                    "View Chrome", MessageBoxIcon.Information);
                return;
            }

            try { await RefreshStatusAsync(ctx); } catch { }
            _log.Info($"[VIEW] profile={ctx.Profile.Name} cdpPort={ctx.Profile.CdpPort} cachedPid={effectiveCachedPid} resolvedPid={resolvedPid} hwnd={resolution.WindowHandle} source={resolution.Reason}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIEW_FAIL] profile={ctx.Profile.Name} cdpPort={ctx.Profile.CdpPort} reason={ex.Message}");
            ShowError(ex);
        }
    }

    async Task EnsureWorkerAsync(ProfileContext ctx)
    {
        if (ctx.Worker is not null && !ctx.Worker.HasExited && await PingAsync(ctx)) return;
        if (ctx.Worker is not null)
        {
            try { ctx.Worker.Dispose(); } catch { }
            ctx.Worker = null;
        }
        var exe = ResolveWorkerExe();
        var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
        Directory.CreateDirectory(dataRoot);
        var pipe = PipeName(ctx.Profile.Name);
        var args = $"--worker --embedded --profile {Quote(ctx.Profile.Name)} --profile-path {Quote(ctx.Profile.ProfilePath)} --cdp-port {ctx.Profile.CdpPort} --data-root {Quote(dataRoot)} --pipe-name {Quote(pipe)}";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo { FileName = exe, Arguments = args, WorkingDirectory = _baseDir, UseShellExecute = false },
            EnableRaisingEvents = true
        };
        if (!process.Start()) throw new InvalidOperationException("Không khởi động được V13 worker.");
        ctx.Worker = process;
        ctx.WorkerWindow = IntPtr.Zero;
        ctx.Detached = false;
        process.Exited += (_, _) => OnWorkerProcessExited(ctx, process);
        SetStatus(ctx, $"Worker PID {process.Id} — chờ pipe", Color.DarkOrange);
        var end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
        {
            if (process.HasExited) throw new InvalidOperationException("Worker thoát khi đang khởi động.");
            if (await PingAsync(ctx)) { SetStatus(ctx, $"Worker PID {process.Id} — sẵn sàng", Color.DarkGreen); return; }
            await Task.Delay(200);
        }
        throw new TimeoutException("Worker V13 chưa mở pipe sau 10 giây.");
    }

    async Task<bool> PingAsync(ProfileContext ctx)
    {
        try { return string.Equals(await SendPipeAsync(ctx.Profile.Name, "ping", TimeSpan.FromSeconds(1)), "pong", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    async Task EmbedWorkerAsync(ProfileContext ctx)
    {
        if (ctx.Host is null) return;
        if (!await AttachWorkerWithRetryAsync(ctx, maxAttempts: 20, delayMs: 120, reason: "open"))
            throw new InvalidOperationException("Không gắn được giao diện V13 vào tab sau nhiều lần thử.");
    }

    async Task<bool> AttachWorkerWithRetryAsync(ProfileContext ctx, int maxAttempts, int delayMs, string reason)
    {
        if (ctx.Host is null || ctx.Host.IsDisposed || ctx.Detached) return false;
        if (ctx.EmbedRecoveryInProgress) return WorkerWindowEmbedder.IsAttachedTo(ctx.WorkerWindow, ctx.Host);

        ctx.EmbedRecoveryInProgress = true;
        try
        {
            ctx.Host.CreateControl();
            for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
            {
                if (_closing || ctx.Host is null || ctx.Host.IsDisposed || ctx.Detached) return false;

                try
                {
                    var snapshot = await ReadStatusAsync(ctx);
                    if (snapshot.WindowHandle != 0) ctx.WorkerWindow = new IntPtr(snapshot.WindowHandle);
                }
                catch (Exception ex)
                {
                    if (attempt == 1 || attempt == maxAttempts)
                        _log.Warn($"[WORKER_EMBED_STATUS_RETRY] profile={ctx.Profile.Name} reason={reason} attempt={attempt}/{maxAttempts} error={ex.Message}");
                }

                if (WorkerWindowEmbedder.IsValid(ctx.WorkerWindow))
                {
                    if (WorkerWindowEmbedder.IsAttachedTo(ctx.WorkerWindow, ctx.Host) || WorkerWindowEmbedder.Attach(ctx.WorkerWindow, ctx.Host))
                    {
                        ctx.Detached = false;
                        ctx.ConsecutiveEmbedRecoveryFailures = 0;
                        ctx.LastEmbedRecoveryUtc = DateTime.UtcNow;
                        if (ctx.DetachButton is not null) ctx.DetachButton.Text = "Tách Worker";
                        WorkerWindowEmbedder.Resize(ctx.WorkerWindow, ctx.Host.ClientSize);
                        _log.Info($"[WORKER_EMBED_OK] profile={ctx.Profile.Name} reason={reason} attempt={attempt}/{maxAttempts} hwnd={ctx.WorkerWindow.ToInt64()}");
                        return true;
                    }
                }

                if (attempt < maxAttempts) await Task.Delay(Math.Max(50, delayMs));
            }

            ctx.ConsecutiveEmbedRecoveryFailures++;
            ctx.LastEmbedRecoveryUtc = DateTime.UtcNow;
            _log.Warn($"[WORKER_EMBED_FAILED] profile={ctx.Profile.Name} reason={reason} hwnd={ctx.WorkerWindow.ToInt64()} failures={ctx.ConsecutiveEmbedRecoveryFailures}");
            return false;
        }
        finally
        {
            ctx.EmbedRecoveryInProgress = false;
        }
    }

    async Task RecoverWorkerEmbedIfNeededAsync(ProfileContext ctx)
    {
        if (ctx.Opening || ctx.Detached || ctx.Host is null || ctx.Host.IsDisposed) return;
        if (ctx.Worker is null || ctx.Worker.HasExited) return;
        if (WorkerWindowEmbedder.IsAttachedTo(ctx.WorkerWindow, ctx.Host)) return;
        if (ctx.EmbedRecoveryInProgress) return;
        if (DateTime.UtcNow - ctx.LastEmbedRecoveryUtc < TimeSpan.FromMilliseconds(750)) return;

        _log.Warn($"[WORKER_EMBED_RECOVERY_START] profile={ctx.Profile.Name} hwnd={ctx.WorkerWindow.ToInt64()}");
        var recovered = await AttachWorkerWithRetryAsync(ctx, maxAttempts: 6, delayMs: 150, reason: "auto_recovery");
        if (recovered)
        {
            SetStatus(ctx, "Worker đã tự khôi phục giao diện trong tab.", Color.DarkGreen);
            _log.Info($"[WORKER_EMBED_RECOVERY_OK] profile={ctx.Profile.Name}");
        }
        else
        {
            _log.Warn($"[WORKER_EMBED_RECOVERY_PENDING] profile={ctx.Profile.Name} sẽ thử lại ở vòng refresh sau");
        }
    }

    void ToggleDetach(ProfileContext ctx)
    {
        try
        {
            if (!WorkerWindowEmbedder.IsValid(ctx.WorkerWindow)) return;
            if (!ctx.Detached)
            {
                if (WorkerWindowEmbedder.Detach(ctx.WorkerWindow))
                {
                    ctx.Detached = true;
                    if (ctx.DetachButton is not null) ctx.DetachButton.Text = "Gắn Worker";
                }
            }
            else if (ctx.Host is not null && WorkerWindowEmbedder.Attach(ctx.WorkerWindow, ctx.Host))
            {
                ctx.Detached = false;
                if (ctx.DetachButton is not null) ctx.DetachButton.Text = "Tách Worker";
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    async Task RefreshOpenProfilesAsync()
    {
        if (_refreshing || _closing) return;
        _refreshing = true;
        try
        {
            var now = DateTime.UtcNow;
            var selectedTab = _tabs.SelectedTab;
            foreach (var ctx in _contexts.Values.Where(c => c.Tab is not null).ToList())
            {
                if (ctx.Opening) continue;
                if (ctx.Worker is null) continue;
                if (IsWorkerProcessExited(ctx))
                {
                    ConfirmRuntimeState(ctx, RuntimeStateStopped, "worker_process_exited");
                    continue;
                }

                // V13.5: profile đang xem vẫn refresh 1 giây như cũ. Các tab nền
                // chỉ refresh 5 giây/lần để giảm pipe/JSON/UI work khi chạy nhiều VM profile.
                var monitorVisible = _chromeMonitor is not null && !_chromeMonitor.IsDisposed && _chromeMonitor.Visible;
                var interval = ReferenceEquals(ctx.Tab, selectedTab) || monitorVisible
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(5);
                if (now - ctx.LastStatusPollAttemptUtc < interval) continue;

                try
                {
                    ctx.LastStatusPollAttemptUtc = DateTime.UtcNow;
                    await RefreshStatusAsync(ctx);
                    await RecoverWorkerEmbedIfNeededAsync(ctx);
                }
                catch (Exception ex) { HandleStatusPollFailure(ctx, ex, "RefreshOpenProfilesAsync"); }
            }
        }
        finally { _refreshing = false; }
    }

    async Task RefreshStatusAsync(ProfileContext ctx)
    {
        var s = await ReadStatusAsync(ctx);
        ctx.LastStatusRefreshUtc = DateTime.UtcNow;
        ctx.LastSnapshot = s;
        ctx.ConsecutiveStatusPollFailures = 0;
        ctx.LastStatusPollFailure = "";
        ApplyWorkerSnapshotRuntimeState(ctx, s);
        var effectiveRunState = GetEffectiveRuntimeState(ctx);
        var color = GetRuntimeStateColor(effectiveRunState);
        SetStatus(ctx, $"Worker {s.State} | {effectiveRunState} | Chrome {s.Chrome}", color);
        if (s.WindowHandle != 0)
        {
            var reported = new IntPtr(s.WindowHandle);
            if (ctx.WorkerWindow != reported)
            {
                _log.Info($"[WORKER_WINDOW_HANDLE_CHANGED] profile={ctx.Profile.Name} old={ctx.WorkerWindow.ToInt64()} new={reported.ToInt64()}");
                ctx.WorkerWindow = reported;
            }
        }
    }

    async Task<WorkerSnapshot> ReadStatusAsync(ProfileContext ctx)
    {
        var raw = await SendCommandAsync(ctx, "status", TimeSpan.FromSeconds(2));
        var snapshot = JsonSerializer.Deserialize<WorkerSnapshot>(raw, WorkerSnapshotJson) ?? new WorkerSnapshot();
        if (!string.Equals(snapshot.Profile, ctx.Profile.Name, StringComparison.OrdinalIgnoreCase) || snapshot.CdpPort != ctx.Profile.CdpPort)
            throw new InvalidOperationException($"[WORKER_PROFILE_MISMATCH] Expected profile={ctx.Profile.Name}, CDP={ctx.Profile.CdpPort}; worker reported profile={snapshot.Profile}, CDP={snapshot.CdpPort}.");
        if (!IsWorkerReportedRuntimeState(snapshot.RunState))
            throw new InvalidDataException($"[WORKER_STATUS_INVALID] Profile={ctx.Profile.Name}; missing/invalid RunState='{snapshot.RunState}'.");
        return snapshot;
    }

    async Task<string> SendCommandAsync(ProfileContext ctx, string command, TimeSpan? timeout = null)
    {
        await ctx.CommandGate.WaitAsync();
        try
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);

            // Status polling is observational only. A timeout/missing snapshot
            // must never replace/restart a live Worker and turn a transient IPC
            // failure into a fresh Worker's legitimate STOPPED snapshot.
            if (command.Equals("status", StringComparison.OrdinalIgnoreCase)
                || command.Equals("message_reply_status", StringComparison.OrdinalIgnoreCase)
                || command.Equals("message_reply_log", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Worker is null || ctx.Worker.HasExited)
                    throw new InvalidOperationException($"Worker process is not running for profile '{ctx.Profile.Name}'.");
                return await SendPipeAsync(ctx.Profile.Name, command, effectiveTimeout);
            }
            if (command.Equals("message_reply_stop", StringComparison.OrdinalIgnoreCase)
                && (ctx.Worker is null || ctx.Worker.HasExited))
                return "not_running";

            await EnsureWorkerAsyncIfCommandNeedsIt(ctx, command);
            var response = await SendPipeAsync(ctx.Profile.Name, command, effectiveTimeout);
            ApplyCommandRuntimeConfirmation(ctx, command, response);
            return response;
        }
        finally { ctx.CommandGate.Release(); }
    }

    async Task<string> SendCloseChromeCommandAsync(ProfileContext ctx)
    {
        await ctx.CommandGate.WaitAsync();
        try
        {
            // Closing is deliberately not an open/connect path.  Do not create a
            // Worker here because that would only be needed to launch Chrome.
            if (ctx.Worker is null || ctx.Worker.HasExited || !await PingAsync(ctx))
                throw new InvalidOperationException($"Worker của profile “{ctx.Profile.Name}” chưa chạy. Hãy mở đúng profile này trong Manager trước khi đóng Chrome.");
            return await SendPipeAsync(ctx.Profile.Name, "close_chrome", ChromeCloseTimeout);
        }
        finally { ctx.CommandGate.Release(); }
    }

    async Task EnsureWorkerAsyncIfCommandNeedsIt(ProfileContext ctx, string command)
    {
        if (command == "shutdown" && (ctx.Worker is null || ctx.Worker.HasExited)) return;
        if (ctx.Worker is null || ctx.Worker.HasExited || !await PingAsync(ctx)) await EnsureWorkerAsync(ctx);
    }

    static async Task<string> SendPipeAsync(string profileName, string command, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(profileName), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await pipe.ConnectAsync(cts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            return await reader.ReadLineAsync(cts.Token) ?? "";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("IPC timeout: " + command);
        }
    }

    async Task StartAllAsync()
    {
        // "Chạy tất cả" chỉ áp dụng cho các profile đang có tab mở trong Manager.
        // Profile chưa mở sẽ được bỏ qua hoàn toàn, không tự tạo tab/Worker.
        var openContexts = _contexts.Values
            .Where(ctx => ctx.Tab is not null
                          && !ctx.Tab.IsDisposed
                          && ctx.Tab.Parent == _tabs)
            .OrderBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
            .ToList();

        foreach (var ctx in openContexts)
        {
            try
            {
                // Tab đã mở nhưng Worker có thể vừa thoát; OpenProfileAsync sẽ bảo đảm
                // Worker của đúng profile sẵn sàng rồi mới gửi lệnh start.
                await OpenProfileAsync(ctx);
                await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                SetStatus(ctx, "Không chạy được: " + ex.Message, Color.Firebrick);
                _log.Error($"[{ctx.Profile.Name}] START_ALL_OPEN_ONLY: {ex}");
            }
        }

        _log.Info($"[START_ALL_OPEN_ONLY] requested={openContexts.Count}");
    }

    async Task StopAllAsync()
    {
        foreach (var ctx in _contexts.Values.Where(c => c.Worker is not null && !c.Worker.HasExited).ToList())
        {
            try { await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    async Task CloseProfileAsync(ProfileContext ctx)
    {
        var worker = ctx.Worker;
        if (worker is not null && !worker.HasExited)
        {
            if (MessageBox.Show($"Đóng worker V13 của '{ctx.Profile.Name}'?\nChrome/profile đăng nhập không bị xóa.", "Đóng profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { await SendPipeAsync(ctx.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch { }
            try { if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(7))) worker.Kill(true); } catch { }
        }
        RemoveTab(ctx);
    }

    void RemoveTab(ProfileContext ctx)
    {
        var page = ctx.Tab;
        if (page is not null && !page.IsDisposed && page.Parent == _tabs)
        {
            var removedIndex = _tabs.TabPages.IndexOf(page);
            if (removedIndex >= 0)
            {
                var previousSelectedIndex = GetSafeSelectedIndex();
                var wasChangingTabs = _changingTabs;
                _changingTabs = true;
                try
                {
                    _tabs.TabPages.Remove(page);
                    NormalizeSelectionAfterTabRemoval(removedIndex, previousSelectedIndex);
                }
                finally { _changingTabs = wasChangingTabs; }
            }
        }
        page?.Dispose();
        ctx.Tab = null; ctx.Host = null; ctx.ProfileHeader = null; ctx.Status = null; ctx.DetachButton = null; ctx.WorkerWindow = IntPtr.Zero; ctx.Detached = false;
        EnsureAddTab(); RefreshAvailability(); UpdateTitle();
    }

    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        if (_chromeMonitorHotkeyRegistered)
        {
            try { UnregisterHotKey(Handle, HOTKEY_CHROME_MONITOR_TOGGLE); } catch { }
            _chromeMonitorHotkeyRegistered = false;
        }
        _refreshTimer.Stop();

        // Chrome Monitor is intentionally an independent top-level window (not owned by Manager)
        // so the Manager can be brought in front of it. Close it explicitly when Manager exits.
        if (_chromeMonitor is not null && !_chromeMonitor.IsDisposed)
        {
            try { _chromeMonitor.Close(); } catch { }
            _chromeMonitor = null;
        }

        Enabled = false;
        foreach (var ctx in _contexts.Values.Where(c => c.Worker is not null && !c.Worker.HasExited).ToList())
        {
            var worker = ctx.Worker;
            try { await SendPipeAsync(ctx.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch { }
            try { if (worker is not null && !await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(7))) worker.Kill(true); } catch { }
        }
        FormClosing -= OnClosing;
        Close();
    }

    void AddProfile()
    {
        var catalog = _profileService.Load();
        var request = ShowAddProfileDialog(catalog);
        if (request is null) return;
        var name = request.Name;

        TikTokProfileEntry? entry = null;
        try
        {
            entry = _profileService.CreateManagedProfile(name);
            _chromeProfileNameSync.SyncBeforeLaunch(entry.ProfilePath, entry.Name);
            var dataRoot = _profileService.ResolveDataRoot(entry);
            Directory.CreateDirectory(dataRoot);
            ApplyManagerDefaultConfigToNewProfile(dataRoot);
            if (!string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrEmpty(request.Password) || !string.IsNullOrWhiteSpace(request.TotpSecret))
                _tiktokAuthService.Save(dataRoot, request.Username, request.Password, request.TotpSecret, request.AutoLogin);
            catalog.Profiles.Add(entry);
            catalog.SelectedProfile = entry.Name;
            _profileService.EnsurePorts(catalog.Profiles);
            _profileService.SaveWithBackup(catalog);
            if (!string.IsNullOrWhiteSpace(request.AccountPoolId))
            {
                try { _accountPoolService.Assign(request.AccountPoolId, entry.Name); }
                catch (Exception assignEx) { _log.Warn($"[ACCOUNT_POOL_ASSIGN_FAILED] profile={entry.Name} {assignEx.Message}"); }
            }
        }
        catch (Exception ex)
        {
            var rollbackError = entry is null ? null : TryRollbackCreatedProfile(entry);
            try { ReloadCatalog(); }
            catch (Exception reloadEx) { _log.Error("[PROFILE_CREATE] cannot reload catalog after failed creation: " + reloadEx); }

            var detail = $"Không thể tạo profile {name}: {ex.Message}";
            if (!string.IsNullOrWhiteSpace(rollbackError)) detail += "\n\nKhông thể dọn dữ liệu tạo dở: " + rollbackError;
            _log.Error("[PROFILE_CREATE] name=" + name + " " + ex);
            ModernDialog.ShowMessage(this, detail, "Thêm profile TikTok", MessageBoxIcon.Error);
            return;
        }

        try
        {
            ReloadCatalog();
            _log.Info($"[PROFILE_CREATED] name={entry.Name} profilePath={entry.ProfilePath}");
            ModernDialog.ShowMessage(this, $"Đã tạo profile {entry.Name} thành công.", "Thêm profile TikTok", MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Error("[PROFILE_CREATE] profile created but UI refresh failed: " + ex);
            ModernDialog.ShowMessage(this, $"Đã tạo profile {entry.Name}, nhưng không thể cập nhật giao diện: {ex.Message}", "Thêm profile TikTok", MessageBoxIcon.Warning);
        }
    }

    string ManagerDefaultConfigRoot => Path.Combine(_baseDir, "manager_default_config");
    string ManagerDefaultIniPath => Path.Combine(ManagerDefaultConfigRoot, "auto_chrome.ini");
    string ManagerDefaultContentPath => Path.Combine(ManagerDefaultConfigRoot, "auto_chrome_noidung.txt");

    void ApplyManagerDefaultConfigToNewProfile(string dataRoot)
    {
        if (!File.Exists(ManagerDefaultIniPath)) return;
        Directory.CreateDirectory(dataRoot);
        File.Copy(ManagerDefaultIniPath, Path.Combine(dataRoot, "auto_chrome.ini"), overwrite: true);
        var targetContent = Path.Combine(dataRoot, "auto_chrome_noidung.txt");
        if (File.Exists(ManagerDefaultContentPath))
            File.Copy(ManagerDefaultContentPath, targetContent, overwrite: true);
        else if (File.Exists(targetContent))
            File.Delete(targetContent);
        _log.Info($"[DEFAULT_CONFIG_APPLIED] dataRoot={dataRoot}");
    }

    void ShowDefaultConfigDialog()
    {
        var catalog = _profileService.Load();
        using var form = new Form
        {
            Text = $"Cấu hình mặc định — {AppVersionInfo.Display}",
            Width = 680,
            Height = 540,
            MinimumSize = new Size(640, 500),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form);

        // Footer luôn cố định để không bị khuất khi Windows dùng DPI/Scale lớn.
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = UiTheme.Canvas
        };
        var footerFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var close = new Button { Text = "Đóng", DialogResult = DialogResult.Cancel, Size = new Size(110, 40) };
        ModernDialog.StyleSecondaryButton(close);
        footerFlow.Controls.Add(close);
        footer.Controls.Add(footerFlow);

        var viewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(18, 16, 18, 12)
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 12,
            BackColor = UiTheme.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < content.RowCount; i++)
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Cấu hình dùng cho profile tạo mới",
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        ModernDialog.StylePrimaryLabel(title);

        var note = new Label
        {
            Text = "Chỉ sao chép cấu hình Tool (auto_chrome.ini + nội dung dán), không sao chép tài khoản, mật khẩu, 2FA hay dữ liệu Chrome. Profile đã tồn tại không bị thay đổi.",
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            Margin = new Padding(0, 0, 0, 12)
        };
        var status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            Margin = new Padding(0, 0, 0, 16)
        };

        // ZIP là cách nhập chính, luôn hiển thị ngay phía trên.
        var zipTitle = new Label
        {
            Text = "Nhập cấu hình từ file ZIP",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        var zipNote = new Label
        {
            Text = "Chọn ZIP đã xuất từ Tool/profile. Tool tự tìm auto_chrome.ini và auto_chrome_noidung.txt dù chúng nằm trong thư mục con của ZIP.",
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        var zipActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 18)
        };
        var importZip = new Button { Text = "Nhập từ ZIP...", Size = new Size(180, 42), Margin = new Padding(0, 0, 8, 4) };
        ModernDialog.StylePrimaryButton(importZip);
        zipActions.Controls.Add(importZip);

        var separator = new Label
        {
            AutoSize = false,
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(210, 218, 228),
            Margin = new Padding(0, 0, 0, 16)
        };

        var sourceLabel = new Label
        {
            Text = "Hoặc lấy cấu hình từ profile đã có",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        };
        var profileBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 0, 0, 9)
        };
        var profiles = catalog.Profiles.OrderBy(p => p.Name, NaturalProfileNameOrder).ToList();
        foreach (var profile in profiles) profileBox.Items.Add(profile.Name);
        var preferredName = SelectedContext()?.Profile.Name;
        if (string.IsNullOrWhiteSpace(preferredName)) preferredName = catalog.SelectedProfile;
        ModernDialog.StyleSelectionInput(profileBox);
        if (profileBox.Items.Count > 0)
        {
            var preferredIndex = profiles.FindIndex(p => p.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            profileBox.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        }

        var profileActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 16)
        };
        var useProfile = new Button { Text = "Dùng profile này làm mặc định", Size = new Size(230, 42), Margin = new Padding(0, 0, 8, 4) };
        ModernDialog.StyleSecondaryButton(useProfile);
        profileActions.Controls.Add(useProfile);

        var clear = new Button { Text = "Bỏ cấu hình mặc định riêng", Size = new Size(220, 42), Margin = new Padding(0, 0, 8, 4) };
        ModernDialog.StyleSecondaryButton(clear);
        profileActions.Controls.Add(clear);

        void RefreshStatus()
        {
            if (File.Exists(ManagerDefaultIniPath))
            {
                var contentState = File.Exists(ManagerDefaultContentPath) ? "có nội dung dán" : "không có nội dung dán";
                status.Text = $"Đang dùng cấu hình mặc định riêng ({contentState}). Profile tạo mới sẽ tự nhận cấu hình này.";
                status.ForeColor = Color.DarkGreen;
                clear.Enabled = true;
            }
            else
            {
                status.Text = "Chưa đặt cấu hình mặc định riêng. Profile mới sẽ dùng defaults gốc đi kèm Tool.";
                status.ForeColor = Color.DimGray;
                clear.Enabled = false;
            }
        }

        importZip.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Chọn ZIP cấu hình V13",
                Filter = "Gói cấu hình (*.zip)|*.zip|Tất cả file (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(form) != DialogResult.OK) return;
            try
            {
                ImportManagerDefaultConfigZip(picker.FileName);
                RefreshStatus();
                ModernDialog.ShowMessage(form,
                    $"Đã nhập cấu hình mặc định từ:\n{Path.GetFileName(picker.FileName)}\n\nCác profile tạo mới sẽ tự nhận cấu hình này.",
                    "Cấu hình mặc định", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _log.Error("[DEFAULT_CONFIG_IMPORT] " + ex);
                ModernDialog.ShowMessage(form, ex.Message, "Không nhập được cấu hình", MessageBoxIcon.Warning);
            }
        };

        useProfile.Click += (_, _) =>
        {
            if (profileBox.SelectedItem is not string profileName) return;
            var profile = profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return;
            try
            {
                var sourceRoot = _profileService.ResolveDataRoot(profile);
                var sourceIni = Path.Combine(sourceRoot, "auto_chrome.ini");
                var sourceContent = Path.Combine(sourceRoot, "auto_chrome_noidung.txt");
                if (!File.Exists(sourceIni))
                    throw new FileNotFoundException($"Profile {profile.Name} chưa có auto_chrome.ini. Hãy mở profile, chỉnh cấu hình và bấm Lưu trước.", sourceIni);
                BackupManagerDefaultConfig();
                Directory.CreateDirectory(ManagerDefaultConfigRoot);
                File.Copy(sourceIni, ManagerDefaultIniPath, overwrite: true);
                if (File.Exists(sourceContent)) File.Copy(sourceContent, ManagerDefaultContentPath, overwrite: true);
                else if (File.Exists(ManagerDefaultContentPath)) File.Delete(ManagerDefaultContentPath);
                _log.Info($"[DEFAULT_CONFIG_SET_FROM_PROFILE] profile={profile.Name} source={sourceRoot}");
                RefreshStatus();
                ModernDialog.ShowMessage(form, $"Đã lấy cấu hình của {profile.Name} làm mặc định. Các profile tạo từ bây giờ sẽ tự nhận cấu hình này.", "Cấu hình mặc định", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _log.Error("[DEFAULT_CONFIG_SET_FROM_PROFILE] " + ex);
                ModernDialog.ShowMessage(form, ex.Message, "Không đặt được cấu hình mặc định", MessageBoxIcon.Warning);
            }
        };

        clear.Click += (_, _) =>
        {
            var confirm = ModernDialog.ShowConfirm(form,
                "Bỏ cấu hình mặc định riêng? Profile mới sau đó sẽ quay về dùng defaults gốc đi kèm Tool. Các profile đã tạo không thay đổi.",
                "Cấu hình mặc định");
            if (confirm != DialogResult.Yes) return;
            try
            {
                BackupManagerDefaultConfig();
                if (Directory.Exists(ManagerDefaultConfigRoot)) Directory.Delete(ManagerDefaultConfigRoot, recursive: true);
                _log.Info("[DEFAULT_CONFIG_CLEARED]");
                RefreshStatus();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không bỏ được cấu hình mặc định", MessageBoxIcon.Warning);
            }
        };

        content.Controls.Add(title, 0, 0);
        content.Controls.Add(note, 0, 1);
        content.Controls.Add(status, 0, 2);
        content.Controls.Add(zipTitle, 0, 3);
        content.Controls.Add(zipNote, 0, 4);
        content.Controls.Add(zipActions, 0, 5);
        content.Controls.Add(separator, 0, 6);
        content.Controls.Add(sourceLabel, 0, 7);
        content.Controls.Add(profileBox, 0, 8);
        content.Controls.Add(profileActions, 0, 9);

        viewport.Controls.Add(content);
        form.Controls.Add(viewport);
        form.Controls.Add(footer);
        form.CancelButton = close;
        RefreshStatus();
        form.ShowDialog(this);
    }

    void BackupManagerDefaultConfig()
    {
        if (!File.Exists(ManagerDefaultIniPath) && !File.Exists(ManagerDefaultContentPath)) return;
        var backupRoot = Path.Combine(_baseDir, "default_config_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(backupRoot);
        if (File.Exists(ManagerDefaultIniPath)) File.Copy(ManagerDefaultIniPath, Path.Combine(backupRoot, "auto_chrome.ini"), true);
        if (File.Exists(ManagerDefaultContentPath)) File.Copy(ManagerDefaultContentPath, Path.Combine(backupRoot, "auto_chrome_noidung.txt"), true);
        try
        {
            var root = new DirectoryInfo(Path.Combine(_baseDir, "default_config_backups"));
            foreach (var dir in root.GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(5))
                try { dir.Delete(true); } catch { }
        }
        catch { }
    }

    void ImportManagerDefaultConfigZip(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Không tìm thấy file ZIP cấu hình.", zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        var iniEntry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("auto_chrome.ini", StringComparison.OrdinalIgnoreCase));
        if (iniEntry is null) throw new InvalidDataException("ZIP không có auto_chrome.ini nên không phải gói cấu hình Tool hợp lệ.");
        var contentEntry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("auto_chrome_noidung.txt", StringComparison.OrdinalIgnoreCase));

        BackupManagerDefaultConfig();
        Directory.CreateDirectory(ManagerDefaultConfigRoot);
        iniEntry.ExtractToFile(ManagerDefaultIniPath, overwrite: true);
        if (contentEntry is not null) contentEntry.ExtractToFile(ManagerDefaultContentPath, overwrite: true);
        else if (File.Exists(ManagerDefaultContentPath)) File.Delete(ManagerDefaultContentPath);
        _log.Info($"[DEFAULT_CONFIG_IMPORTED] zip={zipPath}");
    }

    ProfileCreateRequest? ShowAddProfileDialog(TikTokProfileCatalog catalog)
    {
        using var form = new Form
        {
            Text = $"Thêm profile TikTok — {AppVersionInfo.Display}",
            Width = 620,
            Height = 690,
            MinimumSize = new Size(580, 590),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);

        var poolItems = _accountPoolService.Load();

        Label L(string text, bool bold = false)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(535, 0),
                Margin = new Padding(0, 8, 0, 5),
                Font = new Font("Segoe UI", 10F, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            ModernDialog.StylePrimaryLabel(label);
            if (!bold) label.Font = new Font("Segoe UI", 10F, FontStyle.Regular);
            return label;
        }

        TextBox T(bool password = false)
        {
            var box = new TextBox
            {
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 11F),
                MinimumSize = new Size(0, 36),
                Margin = new Padding(0)
            };
            if (password) box.UseSystemPasswordChar = true;
            ModernDialog.StyleTextInput(box);
            return box;
        }

        var nameBox = T();
        var usernameBox = T();
        var passwordBox = T(password: true);
        var totpBox = T(password: true);
        TikTokAccountPoolItem? selectedPoolAccount = null;
        var selectedAccountSummary = new Label
        {
            Text = "Chưa chọn tài khoản từ kho — sẽ nhập thủ công.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(10, 9, 10, 7),
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.DimGray,
            BackColor = Color.White,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var choosePoolAccount = new Button
        {
            Text = "Chọn tài khoản từ kho...",
            AutoSize = true,
            Height = 38,
            Margin = new Padding(0, 0, 8, 0)
        };
        var manualAccount = new Button
        {
            Text = "Nhập thủ công",
            AutoSize = true,
            Height = 38,
            Margin = new Padding(0)
        };
        ModernDialog.StylePrimaryButton(choosePoolAccount);
        ModernDialog.StyleSecondaryButton(manualAccount);
        var accountButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 5),
            Padding = new Padding(0)
        };
        accountButtons.Controls.Add(choosePoolAccount);
        accountButtons.Controls.Add(manualAccount);

        void ApplyPoolAccount(TikTokAccountPoolItem item)
        {
            selectedPoolAccount = item;
            usernameBox.Text = item.Username;
            passwordBox.Text = item.Password;
            totpBox.Text = item.TotpSecret;
            var assigned = string.IsNullOrWhiteSpace(item.AssignedProfile) ? "Chưa gán" : item.AssignedProfile;
            selectedAccountSummary.Text = $"Dòng {item.SourceRow}: {item.Username}    |    Profile đã gán: {assigned}";
            selectedAccountSummary.ForeColor = string.IsNullOrWhiteSpace(item.AssignedProfile)
                ? Color.FromArgb(35, 91, 152)
                : Color.FromArgb(174, 94, 24);
        }

        choosePoolAccount.Click += (_, _) =>
        {
            var item = ShowAccountPoolPicker(form, includeAssigned: true);
            if (item is not null) ApplyPoolAccount(item);
        };
        manualAccount.Click += (_, _) =>
        {
            selectedPoolAccount = null;
            selectedAccountSummary.Text = "Nhập thủ công — tài khoản này sẽ không gắn với một dòng trong Kho tài khoản.";
            selectedAccountSummary.ForeColor = Color.DimGray;
            usernameBox.Focus();
        };
        var showSecrets = new CheckBox
        {
            Text = "Hiện mật khẩu và secret 2FA",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        showSecrets.CheckedChanged += (_, _) =>
        {
            passwordBox.UseSystemPasswordChar = !showSecrets.Checked;
            totpBox.UseSystemPasswordChar = !showSecrets.Checked;
        };
        var autoLogin = new CheckBox
        {
            Text = "Tự đăng nhập khi Chrome profile chưa có phiên TikTok",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 4)
        };
        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(535, 0),
            Margin = new Padding(0, 6, 0, 6),
            ForeColor = Color.DimGray,
            Text = "Có thể để trống phần đăng nhập và cấu hình sau trong từng profile. Mật khẩu + secret 2FA được mã hóa bằng Windows DPAPI. Nếu TikTok yêu cầu CAPTCHA, tool sẽ chờ bạn xử lý xong rồi tự tiếp tục đăng nhập."
        };

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18, 12, 18, 8),
            BackColor = ModernDialog.Canvas
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        root.Controls.Add(L("Tên profile mới", true), 0, row++); root.Controls.Add(nameBox, 0, row++);
        root.Controls.Add(L("Chọn tài khoản từ kho", true), 0, row++);
        root.Controls.Add(accountButtons, 0, row++);
        root.Controls.Add(selectedAccountSummary, 0, row++);
        root.Controls.Add(L("Tài khoản TikTok (username / email / số điện thoại)"), 0, row++); root.Controls.Add(usernameBox, 0, row++);
        root.Controls.Add(L("Mật khẩu TikTok"), 0, row++); root.Controls.Add(passwordBox, 0, row++);
        root.Controls.Add(L("Secret 2FA/TOTP (không phải mã 6 số; có thể để trống)"), 0, row++); root.Controls.Add(totpBox, 0, row++);
        root.Controls.Add(showSecrets, 0, row++);
        root.Controls.Add(autoLogin, 0, row++);
        root.Controls.Add(note, 0, row++);
        contentHost.Controls.Add(root);

        var create = new Button { Text = "Tạo profile", Size = new Size(132, 42) };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(104, 42) };
        ModernDialog.StylePrimaryButton(create);
        ModernDialog.StyleSecondaryButton(cancel);
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
            Padding = new Padding(18, 14, 18, 16),
            BackColor = ModernDialog.Canvas
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(create);
        footer.Controls.Add(buttons);

        form.Controls.Add(contentHost);
        form.Controls.Add(footer);
        form.AcceptButton = create;
        form.CancelButton = cancel;

        ProfileCreateRequest? request = null;
        create.Click += (_, _) =>
        {
            try
            {
                var name = ValidateNewProfileName(nameBox.Text, catalog);
                var username = usernameBox.Text.Trim();
                var password = passwordBox.Text;
                var totp = TikTokAuthService.NormalizeTotpSecret(totpBox.Text);
                if ((username.Length == 0) != (password.Length == 0))
                    throw new InvalidOperationException("Nếu dùng tự đăng nhập, hãy nhập đủ tài khoản và mật khẩu.");
                var selectedPoolId = selectedPoolAccount?.Id;
                request = new ProfileCreateRequest(name, username, password, totp, autoLogin.Checked, selectedPoolId);
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Thông tin profile chưa hợp lệ", MessageBoxIcon.Warning);
            }
        };
        form.Shown += (_, _) =>
        {
            ModernDialog.FitToWorkingArea(form);
            nameBox.Focus();
        };
        return form.ShowDialog(this) == DialogResult.OK ? request : null;
    }


    void ShowAccountPoolDialog()
    {
        using var form = new Form
        {
            Text = $"Kho tài khoản TikTok — {AppVersionInfo.Display}",
            Width = 1240,
            Height = 720,
            MinimumSize = new Size(900, 560),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);

        var sourceInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(18, 9, 18, 6),
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            BackColor = ModernDialog.Canvas
        };

        var grid = new DataGridView
        {
            Name = "AccountPoolGrid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = ModernDialog.Canvas,
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ScrollBars = ScrollBars.Both
        };

        // Chỉ thay đổi HIỂN THỊ Kho tài khoản.
        // Mật khẩu/2FA vẫn được đọc và lưu trong Excel như cũ, nhưng không đưa lên bảng.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "row",
            HeaderText = "Dòng",
            Width = 46,
            Frozen = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(125, 133, 143),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "assigned",
            HeaderText = "Profile",
            Width = 92,
            Frozen = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(231, 241, 255),
                ForeColor = Color.FromArgb(24, 82, 155),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            },
            HeaderCell =
            {
                Style =
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    BackColor = Color.FromArgb(214, 231, 252),
                    ForeColor = Color.FromArgb(24, 82, 155),
                    Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
                }
            }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "user",
            HeaderText = "Tài khoản",
            Width = 245
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "note",
            HeaderText = "Ghi chú",
            Width = 165
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "identity",
            HeaderText = "Tên/ảnh",
            Width = 100
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "autoProfile",
            HeaderText = "Auto Profile",
            Width = 115
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "reuseQueue",
            HeaderText = "Chờ dùng lại",
            Width = 155
        });
        LogGridSchema(
            grid,
            "AccountPoolGrid",
            "row", "assigned", "user", "note", "identity",
            "autoProfile", "reuseQueue");

        var detailInfo = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            Padding = new Padding(16, 8, 16, 8),
            AutoEllipsis = false,
            ForeColor = Color.FromArgb(46, 65, 88),
            BackColor = Color.FromArgb(247, 250, 253),
            BorderStyle = BorderStyle.FixedSingle,
            Text = "Chọn một tài khoản để xem Ghi chú, kết quả và trạng thái Chờ dùng lại."
        };

        List<TikTokAccountPoolItem> items = new();
        Dictionary<string, TikTokAccountPoolService.TikTokAccountAutoState> autoStates =
            new(StringComparer.OrdinalIgnoreCase);
        DateTime lastSourceWriteUtc = DateTime.MinValue;

        void CaptureSourceWriteTime()
        {
            try
            {
                var path = _accountPoolService.CurrentSourcePath;
                lastSourceWriteUtc =
                    !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                        ? File.GetLastWriteTimeUtc(path)
                        : DateTime.MinValue;
            }
            catch
            {
                lastSourceWriteUtc = DateTime.MinValue;
            }
        }

        void UpdateDetailInfo()
        {
            if (grid.SelectedRows.Count == 0)
            {
                detailInfo.Text = "Chọn một tài khoản để xem Ghi chú, kết quả và trạng thái Chờ dùng lại.";
                return;
            }

            var id = grid.SelectedRows[0].Tag as string;
            var item = items.FirstOrDefault(x => x.Id == id);
            if (item is null)
            {
                detailInfo.Text = "Không còn tìm thấy tài khoản đã chọn.";
                return;
            }

            autoStates.TryGetValue(item.Id, out var autoState);
            var identityResults = _accountPoolService.GetIdentityResults();

            var noteText =
                string.IsNullOrWhiteSpace(item.Note)
                    ? "—"
                    : item.Note.Trim();

            var identityText =
                identityResults.TryGetValue(item.Username, out var identityResult)
                    ? identityResult
                    : "—";

            var autoProfileText =
                string.IsNullOrWhiteSpace(autoState?.Status)
                    ? "—"
                    : autoState!.Status.Trim();

            var reuseQueue =
                GetReusableProfileQueueSnapshot();

            var reuseText =
                !string.IsNullOrWhiteSpace(item.AssignedProfile)
                && reuseQueue.TryGetValue(
                    item.AssignedProfile.Trim(),
                    out var reuseItem)
                    ? reuseItem.IsManual
                        ? $"#{reuseItem.Position} (THỦ CÔNG · {FormatReusableRuntime(reuseItem.TotalRuntime)})"
                        : $"#{reuseItem.Position} ({FormatReusableRuntime(reuseItem.TotalRuntime)})"
                    : "—";

            detailInfo.Text =
                $"Profile {(string.IsNullOrWhiteSpace(item.AssignedProfile) ? "—" : item.AssignedProfile)}  •  {item.Username}  •  Dòng {item.SourceRow}\n"
                + $"Ghi chú: {noteText}    |    Tên/ảnh: {identityText}    |    Auto Profile: {autoProfileText}    |    Chờ dùng lại: {reuseText}";
        }

        void RefreshGrid()
        {
            var selectedId = grid.SelectedRows.Count > 0
                ? grid.SelectedRows[0].Tag as string
                : null;

            items = _accountPoolService.Load()
                .OrderBy(x => x.SourceRow)
                .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                autoStates = _accountPoolService.LoadAutoStates();
            }
            catch
            {
                autoStates = new Dictionary<string, TikTokAccountPoolService.TikTokAccountAutoState>(
                    StringComparer.OrdinalIgnoreCase);
            }

            grid.Rows.Clear();
            var identityResults = _accountPoolService.GetIdentityResults();
            var reuseQueue = GetReusableProfileQueueSnapshot();
            DataGridViewRow? rowToSelect = null;

            foreach (var item in items)
            {
                autoStates.TryGetValue(item.Id, out var autoState);

                var index = grid.Rows.Add(
                    item.SourceRow,
                    item.AssignedProfile,
                    item.Username,
                    item.Note,
                    identityResults.TryGetValue(item.Username, out var identityResult)
                        ? identityResult
                        : "",
                    autoState?.Status ?? "",
                    !string.IsNullOrWhiteSpace(item.AssignedProfile)
                    && reuseQueue.TryGetValue(
                        item.AssignedProfile.Trim(),
                        out var reuseItem)
                        ? reuseItem.IsManual
                            ? $"#{reuseItem.Position} · THỦ CÔNG"
                            : $"#{reuseItem.Position} · {FormatReusableRuntime(reuseItem.TotalRuntime)}"
                        : "");

                var row = grid.Rows[index];
                row.Tag = item.Id;

                // Profile là thông tin quan sát thường xuyên: luôn giữ nổi bật.
                row.Cells["assigned"].Style.BackColor = Color.FromArgb(231, 241, 255);
                row.Cells["assigned"].Style.ForeColor = Color.FromArgb(24, 82, 155);
                row.Cells["assigned"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                row.Cells["assigned"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

                if (string.Equals((item.Note ?? "").Trim(), "ban", StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["note"].Style.BackColor = Color.MistyRose;
                    row.Cells["note"].Style.ForeColor = Color.Firebrick;
                }

                var identityText =
                    identityResults.TryGetValue(item.Username, out var identityState)
                        ? identityState
                        : "";

                if (identityText.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["identity"].Style.BackColor = Color.Honeydew;
                    row.Cells["identity"].Style.ForeColor = Color.DarkGreen;
                }
                else if (identityText.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["identity"].Style.BackColor = Color.MistyRose;
                    row.Cells["identity"].Style.ForeColor = Color.Firebrick;
                }

                var statusText = (autoState?.Status ?? "").Trim();

                if (statusText.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["autoProfile"].Style.BackColor = Color.Honeydew;
                    row.Cells["autoProfile"].Style.ForeColor = Color.DarkGreen;
                }
                else if (statusText.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["autoProfile"].Style.BackColor = Color.MistyRose;
                    row.Cells["autoProfile"].Style.ForeColor = Color.Firebrick;
                }

                if (!string.IsNullOrWhiteSpace(item.AssignedProfile)
                    && reuseQueue.ContainsKey(item.AssignedProfile.Trim()))
                {
                    row.Cells["reuseQueue"].Style.BackColor =
                        Color.FromArgb(231, 241, 255);
                    row.Cells["reuseQueue"].Style.ForeColor =
                        Color.FromArgb(32, 83, 145);
                    row.Cells["reuseQueue"].Style.Font =
                        new Font(grid.Font, FontStyle.Bold);
                }

                if (!string.IsNullOrWhiteSpace(selectedId)
                    && item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    rowToSelect = row;
                }
            }

            if (rowToSelect is not null)
            {
                grid.ClearSelection();
                rowToSelect.Selected = true;
                grid.CurrentCell = rowToSelect.Cells["assigned"];
            }
            else if (grid.Rows.Count > 0)
            {
                grid.Rows[0].Selected = true;
                grid.CurrentCell = grid.Rows[0].Cells["assigned"];
            }

            var currentFile = _accountPoolService.CurrentSourcePath;
            sourceInfo.Text = string.IsNullOrWhiteSpace(currentFile)
                ? "Chưa chọn Excel  •  Bấm Mở Excel để chọn nguồn tài khoản."
                : $"{Path.GetFileName(currentFile)}  •  {items.Count} tài khoản  •  Chờ dùng lại: {reuseQueue.Count}  •  "
                  + $"Tự quét: Tổng < {Math.Clamp(_autoCloseSettings.RunHours, 3, 8)}h";
            sourceInfo.Tag = currentFile;

            CaptureSourceWriteTime();
            UpdateDetailInfo();
        }

        TikTokAccountPoolItem? SelectedItem()
        {
            if (grid.SelectedRows.Count == 0) return null;
            var id = grid.SelectedRows[0].Tag as string;
            return items.FirstOrDefault(x => x.Id == id);
        }

        var openExcel = new Button { Text = "Mở Excel", AutoSize = true, Height = 36 };
        var reload = new Button { Text = "Tải lại", AutoSize = true, Height = 36 };
        var scanReuse = new Button { Text = "Quét chờ", AutoSize = true, Height = 36 };
        var addReuseManual = new Button { Text = "+ Vào chờ", AutoSize = true, Height = 36 };
        var removeReuseManual = new Button { Text = "- Bỏ chờ", AutoSize = true, Height = 36 };
        var add = new Button { Text = "+ Thêm dòng", AutoSize = true, Height = 36 };
        var edit = new Button { Text = "Sửa", AutoSize = true, Height = 36 };
        var release = new Button { Text = "Bỏ gán profile", AutoSize = true, Height = 36 };
        var delete = new Button { Text = "Xóa dòng", AutoSize = true, Height = 36 };
        var close = new Button { Text = "Đóng", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 36 };
        ModernDialog.StylePrimaryButton(openExcel);
        ModernDialog.StyleSecondaryButton(reload);
        ModernDialog.StyleSecondaryButton(scanReuse);
        ModernDialog.StylePrimaryButton(addReuseManual);
        ModernDialog.StyleSecondaryButton(removeReuseManual);
        ModernDialog.StylePrimaryButton(add);
        ModernDialog.StyleSecondaryButton(edit);
        ModernDialog.StyleSecondaryButton(release);
        ModernDialog.StyleSecondaryButton(delete);
        ModernDialog.StyleSecondaryButton(close);

        openExcel.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Mở file tài khoản",
                Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|Text (*.txt)|*.txt|Tất cả file|*.*",
                Multiselect = false,
                CheckFileExists = true
            };
            var currentPath = _accountPoolService.CurrentSourcePath;
            if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
            {
                picker.InitialDirectory = Path.GetDirectoryName(currentPath);
                picker.FileName = Path.GetFileName(currentPath);
            }
            if (picker.ShowDialog(form) != DialogResult.OK) return;
            try
            {
                var result = _accountPoolService.ImportExcel(picker.FileName);
                RefreshGrid();
                ModernDialog.ShowMessage(form,
                    $"Đã mở file Excel mới và nạp lại toàn bộ Kho tài khoản.\n\nSố tài khoản hiện tại: {result.Added}\nDữ liệu của file cũ đã được loại khỏi giao diện.",
                    "Mở Excel", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không mở được Excel", MessageBoxIcon.Warning);
            }
        };

        reload.Click += (_, _) =>
        {
            try
            {
                var currentPath = _accountPoolService.CurrentSourcePath;
                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    ModernDialog.ShowMessage(form, "Chưa có file Excel đang dùng. Hãy bấm Mở Excel trước.", "Tải lại Excel", MessageBoxIcon.Information);
                    return;
                }
                _accountPoolService.ReloadCurrentExcel();
                RefreshGrid();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không tải lại được Excel", MessageBoxIcon.Warning);
            }
        };

        scanReuse.Click += async (_, _) =>
        {
            scanReuse.Enabled = false;

            try
            {
                await RefreshReusableProfileQueueAsync(
                    "account_pool_manual");

                RefreshGrid();

                var autoReuseHours =
                    Math.Clamp(
                        _autoCloseSettings.RunHours,
                        3,
                        8);

                ModernDialog.ShowMessage(
                    form,
                    $"Đã quét xong. Chờ dùng lại: {GetReusableProfileQueueCount()} profile.\n\n"
                    + $"Tự quét nhận profile có Tổng chạy < {autoReuseHours} giờ và Ghi chú trống. "
                    + "Profile thủ công vẫn được ưu tiên trước.",
                    "Chờ dùng lại",
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(
                    form,
                    ex.Message,
                    "Không quét được Chờ dùng lại",
                    MessageBoxIcon.Warning);
            }
            finally
            {
                scanReuse.Enabled = true;
            }
        };

        addReuseManual.Click += (_, _) =>
        {
            var current = SelectedItem();

            if (current is null)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Hãy chọn một tài khoản/profile trước.",
                    "Thêm vào Chờ dùng lại",
                    MessageBoxIcon.Information);
                return;
            }

            if (TryAddReusableProfileManual(
                    current.Id,
                    current.Username,
                    current.AssignedProfile,
                    current.Note,
                    out var message))
            {
                RefreshGrid();

                ModernDialog.ShowMessage(
                    form,
                    message + "\n\nProfile thủ công được ưu tiên trước các profile tự quét.",
                    "Đã thêm vào Chờ dùng lại",
                    MessageBoxIcon.Information);
            }
            else
            {
                ModernDialog.ShowMessage(
                    form,
                    message,
                    "Không thể thêm vào Chờ dùng lại",
                    MessageBoxIcon.Warning);
            }
        };

        removeReuseManual.Click += (_, _) =>
        {
            var current = SelectedItem();

            if (current is null)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Hãy chọn một tài khoản/profile trước.",
                    "Bỏ khỏi Chờ dùng lại",
                    MessageBoxIcon.Information);
                return;
            }

            if (TryRemoveReusableProfileManual(
                    current.AssignedProfile,
                    out var message))
            {
                RefreshGrid();

                ModernDialog.ShowMessage(
                    form,
                    message,
                    "Đã bỏ khỏi Chờ dùng lại",
                    MessageBoxIcon.Information);
            }
            else
            {
                ModernDialog.ShowMessage(
                    form,
                    message,
                    "Chờ dùng lại",
                    MessageBoxIcon.Information);
            }
        };

        add.Click += (_, _) =>
        {
            var sourceRow = items.Count == 0 ? 2 : Math.Max(2, items.Max(x => x.SourceRow) + 1);
            var created = ShowAccountPoolItemEditor(form, null, sourceRow);
            if (created is null) return;
            try
            {
                _accountPoolService.Upsert(created);
                RefreshGrid();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không lưu được vào Excel", MessageBoxIcon.Warning);
            }
        };

        edit.Click += (_, _) =>
        {
            var current = SelectedItem();
            if (current is null) return;
            var updated = ShowAccountPoolItemEditor(form, current, current.SourceRow);
            if (updated is null) return;
            try
            {
                _accountPoolService.Upsert(updated);
                RefreshGrid();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không lưu được vào Excel", MessageBoxIcon.Warning);
            }
        };

        release.Click += (_, _) =>
        {
            var current = SelectedItem();
            if (current is null || string.IsNullOrWhiteSpace(current.AssignedProfile)) return;
            _accountPoolService.ReleaseAccount(current.Id);
            RefreshGrid();
        };

        delete.Click += (_, _) =>
        {
            var current = SelectedItem();
            if (current is null) return;
            if (ModernDialog.ShowConfirm(form,
                    $"Xóa tài khoản {current.Username} khỏi Kho và xóa dữ liệu A-D ở dòng {current.SourceRow} trong file Excel đang dùng?\nThông tin đăng nhập đã lưu trong profile hiện tại sẽ không bị xóa.",
                    "Xóa tài khoản") != DialogResult.Yes) return;
            try
            {
                _accountPoolService.Delete(current.Id);
                RefreshGrid();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không xóa được trong Excel", MessageBoxIcon.Warning);
            }
        };
        grid.SelectionChanged += (_, _) => UpdateDetailInfo();
        grid.CellDoubleClick += (_, _) => edit.PerformClick();

        var autoRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000,
            Enabled = false
        };
        autoRefreshTimer.Tick += (_, _) =>
        {
            try
            {
                var currentPath = _accountPoolService.CurrentSourcePath;
                if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
                    return;

                var currentWriteUtc = File.GetLastWriteTimeUtc(currentPath);
                if (currentWriteUtc == lastSourceWriteUtc)
                    return;

                _accountPoolService.ReloadCurrentExcel();
                RefreshGrid();
            }
            catch
            {
                // Nếu Excel đang khóa thì giữ dữ liệu hiện tại và tự thử lại ở vòng sau.
            }
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 106,
            Padding = new Padding(14, 10, 14, 10),
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ModernDialog.Canvas
        };
        footer.Controls.Add(openExcel);
        footer.Controls.Add(reload);
        footer.Controls.Add(scanReuse);
        footer.Controls.Add(addReuseManual);
        footer.Controls.Add(removeReuseManual);
        footer.Controls.Add(add);
        footer.Controls.Add(edit);
        footer.Controls.Add(release);
        footer.Controls.Add(delete);
        footer.Controls.Add(close);

        form.Controls.Add(grid);
        form.Controls.Add(detailInfo);
        form.Controls.Add(sourceInfo);
        form.Controls.Add(footer);
        form.CancelButton = close;
        form.FormClosed += (_, _) =>
        {
            try { autoRefreshTimer.Stop(); } catch { }
            try { autoRefreshTimer.Dispose(); } catch { }
        };
        form.Shown += async (_, _) =>
        {
            ModernDialog.FitToWorkingArea(form);

            try
            {
                var currentPath = _accountPoolService.CurrentSourcePath;

                if (!string.IsNullOrWhiteSpace(currentPath)
                    && File.Exists(currentPath))
                {
                    _accountPoolService.ReloadCurrentExcel();
                }
            }
            catch
            {
                // Nếu file nguồn tạm thời không đọc được, vẫn giữ cache hiện tại.
            }

            try
            {
                await RefreshReusableProfileQueueAsync(
                    "account_pool_open");
            }
            catch { }

            RefreshGrid();
            autoRefreshTimer.Start();
        };
        form.ShowDialog(this);
    }

    TikTokAccountPoolItem? ShowAccountPoolItemEditor(IWin32Window owner, TikTokAccountPoolItem? current, int sourceRow)
    {
        using var form = new Form
        {
            Text = current is null ? "Thêm tài khoản TikTok" : "Sửa tài khoản TikTok",
            Width = 560,
            Height = 520,
            MinimumSize = new Size(520, 450),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);

        TextBox Box(string value, bool secret = false)
        {
            var box = new TextBox { Text = value, Dock = DockStyle.Top, Font = new Font("Segoe UI", 11F), MinimumSize = new Size(0, 36), UseSystemPasswordChar = secret };
            ModernDialog.StyleTextInput(box);
            return box;
        }
        Label LabelFor(string text)
        {
            var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 5) };
            ModernDialog.StylePrimaryLabel(label);
            return label;
        }

        var user = Box(current?.Username ?? "");
        var pass = Box(current?.Password ?? "", true);
        var totp = Box(current?.TotpSecret ?? "", true);
        var noteBox = new TextBox
        {
            Text = current?.Note ?? "",
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 10.5F),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            MinimumSize = new Size(0, 72)
        };
        ModernDialog.StyleTextInput(noteBox);
        var show = new CheckBox { Text = "Hiện mật khẩu và secret 2FA", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        show.CheckedChanged += (_, _) => { pass.UseSystemPasswordChar = !show.Checked; totp.UseSystemPasswordChar = !show.Checked; };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 12, 18, 8), AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        root.Controls.Add(LabelFor("Tài khoản / Email / Số điện thoại"), 0, row++); root.Controls.Add(user, 0, row++);
        root.Controls.Add(LabelFor("Mật khẩu"), 0, row++); root.Controls.Add(pass, 0, row++);
        root.Controls.Add(LabelFor("Secret 2FA/TOTP"), 0, row++); root.Controls.Add(totp, 0, row++);
        root.Controls.Add(LabelFor("Ghi chú"), 0, row++); root.Controls.Add(noteBox, 0, row++);
        root.Controls.Add(show, 0, row++);

        var save = new Button { Text = "Lưu", Size = new Size(110, 40) };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(100, 40) };
        ModernDialog.StylePrimaryButton(save);
        ModernDialog.StyleSecondaryButton(cancel);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        footer.Controls.Add(cancel); footer.Controls.Add(save);
        form.Controls.Add(root); form.Controls.Add(footer);
        form.AcceptButton = save; form.CancelButton = cancel;

        TikTokAccountPoolItem? result = null;
        save.Click += (_, _) =>
        {
            try
            {
                var username = user.Text.Trim();
                if (username.Length == 0) throw new InvalidOperationException("Tài khoản không được để trống.");
                result = new TikTokAccountPoolItem(
                    current?.Id ?? Guid.NewGuid().ToString("N"),
                    username,
                    pass.Text,
                    TikTokAuthService.NormalizeTotpSecret(totp.Text),
                    noteBox.Text.Trim(),
                    current?.AssignedProfile ?? "",
                    sourceRow);
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Thông tin chưa hợp lệ", MessageBoxIcon.Warning);
            }
        };
        form.Shown += (_, _) => { ModernDialog.FitToWorkingArea(form); user.Focus(); };
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    TikTokAccountPoolItem? ShowAccountPoolPicker(IWin32Window owner, string? currentProfile = null, bool includeAssigned = false)
    {
        var items = _accountPoolService.Load()
            .Where(x => includeAssigned || !x.IsAssigned || x.AssignedProfile.Equals(currentProfile ?? "", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.SourceRow).ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count == 0)
        {
            ModernDialog.ShowMessage(owner, "Kho tài khoản chưa có dòng phù hợp. Hãy nhập Excel trong mục Kho tài khoản.", "Chọn tài khoản", MessageBoxIcon.Information);
            return null;
        }

        using var form = new Form
        {
            Text = "Chọn tài khoản từ kho",
            Width = 760,
            Height = 520,
            MinimumSize = new Size(620, 420),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(14, 9, 14, 6),
            Text = includeAssigned
                ? "Cột ‘Profile đã gán’ cho biết tài khoản đang thuộc profile nào. Bạn vẫn có thể chọn lại một dòng đã gán nếu thực sự muốn dùng lại."
                : "Chọn một tài khoản chưa dùng (hoặc tài khoản đang gán cho profile hiện tại).",
            ForeColor = Color.DimGray,
            BackColor = ModernDialog.Canvas,
            AutoEllipsis = true
        };

        var grid = new DataGridView
        {
            Name = "AvailableAccountGrid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = ModernDialog.Canvas,
            BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "row", HeaderText = "Dòng Excel", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "user", HeaderText = "Tài khoản", FillWeight = 58 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "assigned", HeaderText = "Profile đã gán", FillWeight = 38 });
        LogGridSchema(grid, "AvailableAccountGrid", "row", "user", "assigned");
        foreach (var item in items)
        {
            var index = grid.Rows.Add(item.SourceRow, item.Username, string.IsNullOrWhiteSpace(item.AssignedProfile) ? "—" : item.AssignedProfile);
            grid.Rows[index].Tag = item.Id;
            if (item.IsAssigned && !item.AssignedProfile.Equals(currentProfile ?? "", StringComparison.OrdinalIgnoreCase))
                grid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(174, 94, 24);
        }
        if (grid.Rows.Count > 0)
        {
            grid.ClearSelection();
            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[1];
        }

        TikTokAccountPoolItem? SelectedItem()
        {
            if (grid.SelectedRows.Count == 0) return null;
            var id = grid.SelectedRows[0].Tag as string;
            return items.FirstOrDefault(x => x.Id == id);
        }

        var choose = new Button { Text = "Chọn", Size = new Size(110, 40) };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(100, 40) };
        ModernDialog.StylePrimaryButton(choose);
        ModernDialog.StyleSecondaryButton(cancel);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(14, 12, 14, 14), BackColor = ModernDialog.Canvas };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(choose);
        footer.Controls.Add(buttons);

        form.Controls.Add(grid);
        form.Controls.Add(footer);
        form.Controls.Add(hint);
        form.AcceptButton = choose;
        form.CancelButton = cancel;
        choose.Click += (_, _) =>
        {
            if (SelectedItem() is null) return;
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) choose.PerformClick(); };
        form.Shown += (_, _) => { ModernDialog.FitToWorkingArea(form); grid.Focus(); };
        return form.ShowDialog(owner) == DialogResult.OK ? SelectedItem() : null;
    }

    void ConfigureTikTokAccount(ProfileContext profileContext)
    {
        var dataRoot = _profileService.ResolveDataRoot(profileContext.Profile);
        Directory.CreateDirectory(dataRoot);
        TikTokAuthMaterial current;
        try { current = _tiktokAuthService.Load(dataRoot); }
        catch { current = new TikTokAuthMaterial("", "", "", true); }
        string? pendingPoolId = null;

        using var form = new Form
        {
            Text = $"Tài khoản TikTok — {profileContext.Profile.Name}",
            Width = 600,
            Height = 600,
            MinimumSize = new Size(560, 520),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);

        TextBox Box(string value, bool secret = false)
        {
            var box = new TextBox
            {
                Text = value,
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 11F),
                MinimumSize = new Size(0, 36),
                UseSystemPasswordChar = secret,
                Margin = new Padding(0)
            };
            ModernDialog.StyleTextInput(box);
            return box;
        }

        Label FieldLabel(string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(510, 0),
                Margin = new Padding(0, 10, 0, 5)
            };
            ModernDialog.StylePrimaryLabel(label);
            return label;
        }

        var user = Box(current.Username);
        var pass = Box(current.Password, true);
        var totp = Box(current.TotpSecret, true);
        var showSecrets = new CheckBox
        {
            Text = "Hiện mật khẩu và secret 2FA",
            AutoSize = true,
            Margin = new Padding(0, 9, 0, 0)
        };
        showSecrets.CheckedChanged += (_, _) =>
        {
            pass.UseSystemPasswordChar = !showSecrets.Checked;
            totp.UseSystemPasswordChar = !showSecrets.Checked;
        };
        var auto = new CheckBox
        {
            Text = "Tự đăng nhập khi mất phiên TikTok",
            Checked = current.AutoLogin,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 4)
        };
        var note = new Label
        {
            Text = "Thay đổi ở đây chỉ cập nhật thông tin đăng nhập của profile này; không đổi thư mục Chrome, XPath, nội dung dán hoặc các thiết lập khác. Secret 2FA là khóa Base32/otpauth, không phải mã 6 số. Khi CAPTCHA xuất hiện, tool chờ bạn xử lý xong rồi tự tiếp tục.",
            AutoSize = true,
            MaximumSize = new Size(510, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 8)
        };

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18, 10, 18, 8),
            BackColor = ModernDialog.Canvas
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        root.Controls.Add(FieldLabel("Tài khoản / Email / Số điện thoại"), 0, row++); root.Controls.Add(user, 0, row++);
        root.Controls.Add(FieldLabel("Mật khẩu TikTok"), 0, row++); root.Controls.Add(pass, 0, row++);
        root.Controls.Add(FieldLabel("Secret 2FA/TOTP"), 0, row++); root.Controls.Add(totp, 0, row++);
        root.Controls.Add(showSecrets, 0, row++);
        root.Controls.Add(auto, 0, row++);
        root.Controls.Add(note, 0, row++);
        contentHost.Controls.Add(root);

        var choosePool = new Button { Text = "Chọn từ kho", Size = new Size(124, 42) };
        var save = new Button { Text = "Lưu thay đổi", Size = new Size(132, 42) };
        var clear = new Button { Text = "Xóa đăng nhập", Size = new Size(130, 42) };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(100, 42) };
        ModernDialog.StylePrimaryButton(save);
        ModernDialog.StyleSecondaryButton(choosePool);
        ModernDialog.StyleSecondaryButton(clear);
        ModernDialog.StyleSecondaryButton(cancel);
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
            Padding = new Padding(18, 14, 18, 16),
            BackColor = ModernDialog.Canvas
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(choosePool);
        footer.Controls.Add(buttons);

        form.Controls.Add(contentHost);
        form.Controls.Add(footer);
        form.AcceptButton = save;
        form.CancelButton = cancel;

        choosePool.Click += (_, _) =>
        {
            var selected = ShowAccountPoolPicker(form, profileContext.Profile.Name);
            if (selected is null) return;
            pendingPoolId = selected.Id;
            user.Text = selected.Username;
            pass.Text = selected.Password;
            totp.Text = selected.TotpSecret;
        };

        save.Click += (_, _) =>
        {
            try
            {
                if ((user.Text.Trim().Length == 0) != (pass.Text.Length == 0))
                    throw new InvalidOperationException("Hãy nhập đủ tài khoản và mật khẩu.");
                _tiktokAuthService.Save(dataRoot, user.Text.Trim(), pass.Text, totp.Text, auto.Checked);
                if (!string.IsNullOrWhiteSpace(pendingPoolId))
                    _accountPoolService.Assign(pendingPoolId, profileContext.Profile.Name);
                _log.Info($"[TIKTOK_AUTH_SAVED] profile={profileContext.Profile.Name} usernameConfigured={user.Text.Trim().Length > 0} totpConfigured={totp.Text.Trim().Length > 0}");
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(form, ex.Message, "Không lưu được", MessageBoxIcon.Warning);
            }
        };
        clear.Click += (_, _) =>
        {
            _tiktokAuthService.Delete(dataRoot);
            _accountPoolService.ReleaseByProfile(profileContext.Profile.Name);
            pendingPoolId = null;
            user.Clear();
            pass.Clear();
            totp.Clear();
            auto.Checked = true;
            ModernDialog.ShowMessage(form, $"Đã xóa thông tin đăng nhập đã lưu của profile {profileContext.Profile.Name}.", "Tài khoản TikTok", MessageBoxIcon.Information);
        };
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);
        form.ShowDialog(this);
    }

    string ValidateNewProfileName(string rawName, TikTokProfileCatalog catalog)
    {
        var name = (rawName ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("Tên profile mới không được để trống.");
        if (name is "." or "..") throw new InvalidOperationException("Tên profile không hợp lệ.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Tên profile chứa ký tự không hợp lệ cho tên thư mục/file.");

        var normalized = _profileService.NormalizeName(name);
        if (catalog.Profiles.Any(p => p.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Profile đã tồn tại: " + normalized);
        if (Directory.Exists(_profileService.GetProfilePath(normalized)))
            throw new InvalidOperationException("Profile đã tồn tại: " + normalized);
        return normalized;
    }

    string? TryRollbackCreatedProfile(TikTokProfileEntry entry)
    {
        try
        {
            if (!entry.Managed) return "Profile vừa tạo không phải profile được Manager quản lý; không tự xóa dữ liệu.";
            var expectedProfilePath = Path.GetFullPath(_profileService.GetProfilePath(entry.Name));
            var actualProfilePath = Path.GetFullPath(entry.ProfilePath);
            if (!actualProfilePath.Equals(expectedProfilePath, StringComparison.OrdinalIgnoreCase))
                return "Đường dẫn profile vừa tạo không khớp đường dẫn quản lý dự kiến; không tự xóa để tránh xóa nhầm.";

            if (Directory.Exists(actualProfilePath)) Directory.Delete(actualProfilePath, true);
            var containerPath = Path.GetDirectoryName(actualProfilePath);
            if (!string.IsNullOrWhiteSpace(containerPath) && Directory.Exists(containerPath) && !Directory.EnumerateFileSystemEntries(containerPath).Any())
                Directory.Delete(containerPath, false);
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"[PROFILE_CREATE_ROLLBACK] name={entry.Name} path={entry.ProfilePath} {ex}");
            return ex.Message;
        }
    }

    async Task AddExistingProfileAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục Chrome user-data-dir/profile có sẵn", UseDescriptionForTitle = true, SelectedPath = TikTokProfileService.ProfilesRoot };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var defaultName = new DirectoryInfo(dialog.SelectedPath).Parent?.Name ?? new DirectoryInfo(dialog.SelectedPath).Name;
        var name = PromptText("Profile có sẵn", "Tên profile", defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;
        var catalog = _profileService.Load();
        var entry = _profileService.ImportExistingProfile(name, dialog.SelectedPath);
        catalog.Profiles.RemoveAll(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        catalog.Profiles.Add(entry); catalog.SelectedProfile = entry.Name; _profileService.EnsurePorts(catalog.Profiles); _profileService.SaveWithBackup(catalog); ReloadCatalog();
        await SynchronizeChromeProfileNameAsync(_contexts[entry.Name]);
    }

    async Task RenameSelectedProfileAsync()
    {
        var selected = SelectedContext();
        if (selected is null)
        {
            ShowRenameFailure("Chưa chọn profile.");
            return;
        }
        var oldName = selected.Profile.Name;
        var newName = PromptText("Đổi tên profile", "Tên mới", oldName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        try { ResolveAndPersistRenamePathState(selected); }
        catch (Exception ex) { ShowRenameFailure(ex.Message); return; }

        var validationCatalog = _profileService.Load();
        string normalizedName;
        try { normalizedName = ValidateRenameProfileName(newName, selected.Profile, validationCatalog); }
        catch (Exception ex) { ShowRenameFailure(ex.Message); return; }
        if (normalizedName.Equals(oldName, StringComparison.Ordinal)) return;
        if (_contexts.TryGetValue(normalizedName, out var nameOwner) && !ReferenceEquals(nameOwner, selected))
        {
            ShowRenameFailure("Profile đã tồn tại: " + normalizedName);
            return;
        }

        _profileRenameInProgress = true;
        var previousEnabled = Enabled;
        Enabled = false;

        ChromeNameSyncRuntimeState? runtime = null;
        var workerWasRunning = selected.Worker is not null && !selected.Worker.HasExited;
        var workerStoppedForRename = false;
        var saveAttempted = false;
        TikTokProfileCatalog? previousPersistedCatalog = null;
        TikTokProfileCatalog? verifiedCatalog = null;
        TikTokProfileEntry? verifiedRename = null;
        try
        {
            // A display rename never moves ProfilePath or DataRoot.
            runtime = await CloseChromeForNameSyncAsync(selected, selected.Profile.ProfilePath);
            await ShutdownWorkerForProfileRenameAsync(selected);
            workerStoppedForRename = workerWasRunning;

            // Use the raw persisted file as the transaction source. Discovery
            // must not run between a successful save and UI/context update.
            var catalog = _profileService.LoadPersistedCatalog();
            previousPersistedCatalog = _profileService.LoadPersistedCatalog();

            // Name is the catalog identity the user selected.  The old code
            // tried to rediscover the entry by ProfilePath + DataRoot after
            // stopping the Worker.  A stale/in-memory DataRoot was enough to
            // make that comparison fail even though profiles.json still had
            // exactly one valid entry named oldName.  Resolve the transaction
            // source by its unique persisted old name, then preserve storage
            // fields from that persisted entry verbatim.
            var currentIndexes = catalog.Profiles
                .Select((profile, index) => (profile, index))
                .Where(item => item.profile.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (currentIndexes.Count != 1)
                throw new InvalidOperationException($"Không tìm thấy đúng một entry catalog tên “{oldName}” để đổi tên (count={currentIndexes.Count}).");

            var currentIndex = currentIndexes[0].index;
            var current = currentIndexes[0].profile;
            _log.Info($"[PROFILE_RENAME_SOURCE] oldName={oldName} profilePath={current.ProfilePath} dataRoot={current.DataRoot} cdpPort={current.CdpPort}");

            normalizedName = ValidateRenameProfileName(normalizedName, current, catalog);
            var renamed = _profileService.RenameProfile(current, normalizedName);

            // Replace exactly the selected persisted entry in-place.  Do not
            // remove by storage identity: aliases/legacy entries must never be
            // deleted as a side effect of a display-name rename.
            catalog.Profiles[currentIndex] = renamed;
            catalog.SelectedProfile = renamed.Name;
            saveAttempted = true;
            _profileService.SaveWithBackupPreservingPorts(catalog);
            var persisted = VerifyPersistedRename(oldName, renamed, current);
            verifiedCatalog = persisted.Catalog;
            verifiedRename = persisted.Entry;
        }
        catch (Exception ex)
        {
            var rollbackError = saveAttempted && previousPersistedCatalog is not null
                ? TryRestoreCatalogAfterFailedRename(previousPersistedCatalog)
                : null;
            var runtimeError = await TryRestoreProfileRuntimeAfterRenameAsync(selected, workerStoppedForRename, runtime);
            var detail = ex.Message;
            if (!string.IsNullOrWhiteSpace(rollbackError)) detail += "\nKhôi phục profiles.json không thành công: " + rollbackError;
            if (!string.IsNullOrWhiteSpace(runtimeError)) detail += "\nKhông thể khôi phục Worker/Chrome cũ: " + runtimeError;
            _log.Error($"[PROFILE_RENAME_FAILED] oldName={oldName} newName={normalizedName} {detail}");
            ShowRenameFailure(detail);
            return;
        }
        finally
        {
            if (verifiedRename is null)
            {
                _profileRenameInProgress = false;
                if (!IsDisposed) Enabled = previousEnabled;
            }
        }

        try
        {
            // The existing ProfileContext, tab, worker host and settings UI
            // move together only after persistence is proven.  This is not a
            // new profile/open operation; the same context is re-keyed.
            ApplyCommittedProfileRename(selected, oldName, verifiedRename!);
            RefreshContextsFromCatalog(verifiedCatalog!);

            string? syncError = null;
            try { _chromeProfileNameSync.SyncBeforeLaunch(verifiedRename!.ProfilePath, verifiedRename.Name); }
            catch (Exception ex)
            {
                syncError = ex.Message;
                _log.Error($"[PROFILE_RENAME_CHROME_NAME_SYNC_FAILED] profile={verifiedRename!.Name} {ex}");
            }
            var reopenError = await TryRestoreProfileRuntimeAfterRenameAsync(selected, workerStoppedForRename, runtime);
            if (!string.IsNullOrWhiteSpace(reopenError))
                _log.Error($"[PROFILE_RENAME_RESTORE_FAILED] profile={verifiedRename!.Name} {reopenError}");
            _log.Info($"[PROFILE_RENAMED] oldName={oldName} newName={verifiedRename!.Name} profilePath={verifiedRename.ProfilePath} dataRoot={verifiedRename.DataRoot} cdpPort={verifiedRename.CdpPort}");
            try { _accountPoolService.RenameAssignedProfile(oldName, verifiedRename.Name); }
            catch (Exception poolEx) { _log.Warn($"[ACCOUNT_POOL_RENAME_ASSIGNMENT_FAILED] {poolEx.Message}"); }

            var success = $"Đã đổi tên {oldName} → {verifiedRename.Name}";
            if (!string.IsNullOrWhiteSpace(syncError)) success += "\n\nTên Chrome chưa đồng bộ: " + syncError;
            if (!string.IsNullOrWhiteSpace(reopenError)) success += "\n\nWorker/Chrome chưa khôi phục: " + reopenError;
            MessageBox.Show(this, success, "Đổi tên profile", MessageBoxButtons.OK,
                string.IsNullOrWhiteSpace(syncError) && string.IsNullOrWhiteSpace(reopenError) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _profileRenameInProgress = false;
            if (!IsDisposed) Enabled = previousEnabled;
        }
    }

    void ResolveAndPersistRenamePathState(ProfileContext context)
    {
        var contextProfile = context.Profile;
        var profileName = _profileService.NormalizeName(contextProfile.Name);
        var catalog = _profileService.LoadPersistedCatalog();
        var matches = catalog.Profiles
            .Where(profile => profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException("Không tìm thấy đúng một entry catalog cho profile đang đổi tên.");

        var stored = matches[0];
        _log.Info($"[RENAME_PATH_STATE] name={profileName} profilePath={LogRenamePath(stored.ProfilePath)} dataRoot={LogRenamePath(stored.DataRoot)} contextProfilePath={LogRenamePath(contextProfile.ProfilePath)} contextDataRoot={LogRenamePath(contextProfile.DataRoot)}");

        var (profilePath, profilePathSource) = ResolveRenameProfilePath(profileName, stored.ProfilePath, contextProfile.ProfilePath);
        var (dataRoot, dataRootSource) = ResolveRenameDataRoot(profileName, stored.DataRoot, contextProfile.DataRoot);
        var resolved = new TikTokProfileEntry
        {
            Name = stored.Name,
            ProfilePath = profilePath,
            DataRoot = dataRoot,
            CdpPort = stored.CdpPort,
            Enabled = stored.Enabled,
            Managed = stored.Managed
        };

        // Persist even when only normalization was needed.  This creates the
        // durable legacy-path checkpoint before the rename transaction starts.
        catalog.Profiles[catalog.Profiles.IndexOf(stored)] = resolved;
        _profileService.SaveWithBackupPreservingPorts(catalog);

        var persisted = _profileService.LoadPersistedCatalog();
        var verifiedMatches = persisted.Profiles
            .Where(profile => profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (verifiedMatches.Count != 1)
            throw new InvalidOperationException("Không thể xác minh lại entry catalog sau khi bổ sung đường dẫn.");
        var verified = verifiedMatches[0];
        var verifiedProfilePath = TikTokProfileService.RequireCanonicalPath(verified.ProfilePath, "ProfilePath");
        var verifiedDataRoot = _profileService.ResolveDataRoot(verified);
        if (!SameCanonicalPath(verifiedProfilePath, profilePath) || !SameCanonicalPath(verifiedDataRoot, dataRoot))
            throw new InvalidOperationException("Không thể xác minh ProfilePath/DataRoot sau khi bổ sung đường dẫn.");

        context.Profile = new TikTokProfileEntry
        {
            Name = verified.Name,
            ProfilePath = verifiedProfilePath,
            DataRoot = verifiedDataRoot,
            CdpPort = verified.CdpPort,
            Enabled = verified.Enabled,
            Managed = verified.Managed
        };
        _log.Info($"[RENAME_PATH_BACKFILL] name={profileName} profilePath={verifiedProfilePath} dataRoot={verifiedDataRoot} profilePathSource={profilePathSource} dataRootSource={dataRootSource} persisted=true");
    }

    (string Path, string Source) ResolveRenameProfilePath(string profileName, string? storedPath, string? contextPath)
    {
        foreach (var candidate in new[] { (Value: storedPath, Source: "catalog"), (Value: contextPath, Source: "context") })
        {
            if (string.IsNullOrWhiteSpace(candidate.Value)) continue;
            var fullPath = TikTokProfileService.RequireCanonicalPath(candidate.Value, "ProfilePath");
            if (Directory.Exists(fullPath)) return (fullPath, candidate.Source);
        }

        // Managed legacy profiles have historically used this exact existing
        // directory layout.  It is a lookup only: no directory is created.
        var managedPath = _profileService.GetProfilePath(profileName);
        if (Directory.Exists(managedPath))
            return (TikTokProfileService.RequireCanonicalPath(managedPath, "ProfilePath"), "managed-legacy");

        if (profileName.Equals(TikTokProfileService.LegacyImportedProfileName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(TikTokProfileService.LegacyImportedProfilePath))
            return (TikTokProfileService.RequireCanonicalPath(TikTokProfileService.LegacyImportedProfilePath, "ProfilePath"), "v11-legacy");

        var suppliedButMissing = FirstNonEmpty(storedPath, contextPath);
        if (!string.IsNullOrWhiteSpace(suppliedButMissing))
            throw new InvalidOperationException("ProfilePath không tồn tại: " + suppliedButMissing.Trim());
        throw new InvalidOperationException("ProfilePath đang thiếu. Không tìm thấy thư mục Chrome profile thực tế để bổ sung.");
    }

    (string Path, string Source) ResolveRenameDataRoot(string profileName, string? storedPath, string? contextPath)
    {
        var existing = FirstNonEmpty(storedPath, contextPath);
        if (!string.IsNullOrWhiteSpace(existing))
            return (TikTokProfileService.RequireCanonicalPath(existing, "DataRoot"), string.IsNullOrWhiteSpace(storedPath) ? "context" : "catalog");

        // ResolveDataRoot's legacy compatibility rule is profiles/<old name>.
        // It only records the location and deliberately does not create it.
        var compatibilityEntry = new TikTokProfileEntry { Name = profileName, DataRoot = "" };
        return (_profileService.ResolveDataRoot(compatibilityEntry), "compatibility-default");
    }

    static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    static string LogRenamePath(string? path) => string.IsNullOrWhiteSpace(path) ? "<empty>" : path.Trim();

    string ValidateRenameProfileName(string rawName, TikTokProfileEntry currentProfile, TikTokProfileCatalog catalog)
    {
        var normalized = _profileService.NormalizeName(rawName);
        if (catalog.Profiles.Any(profile => !profile.Name.Equals(currentProfile.Name, StringComparison.OrdinalIgnoreCase)
            && profile.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Profile đã tồn tại: " + normalized);
        return normalized;
    }

    (TikTokProfileCatalog Catalog, TikTokProfileEntry Entry) VerifyPersistedRename(string oldName, TikTokProfileEntry expectedRename, TikTokProfileEntry original)
    {
        var persisted = _profileService.LoadPersistedCatalog();
        var renamedEntries = persisted.Profiles
            .Where(profile => profile.Name.Equals(expectedRename.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (renamedEntries.Count != 1)
            throw new InvalidOperationException($"profiles.json không chứa đúng một profile mới “{expectedRename.Name}”.");
        if (persisted.Profiles.Any(profile => profile.Name.Equals(oldName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"profiles.json vẫn còn tên cũ “{oldName}”.");

        var renamed = renamedEntries[0];
        if (!HasExactStorageIdentity(renamed, original)
            || renamed.CdpPort != original.CdpPort
            || renamed.Managed != original.Managed
            || renamed.Enabled != original.Enabled)
            throw new InvalidOperationException("profiles.json sau khi lưu đã thay đổi ProfilePath, DataRoot hoặc cấu hình của profile. Đã hủy đổi tên.");
        return (persisted, renamed);
    }

    string? TryRestoreCatalogAfterFailedRename(TikTokProfileCatalog previousCatalog)
    {
        try
        {
            _profileService.Save(previousCatalog);
            _profileService.BackupCatalogIfExists();
            return null;
        }
        catch (Exception ex)
        {
            _log.Error("[PROFILE_RENAME_ROLLBACK_CATALOG] " + ex);
            return ex.Message;
        }
    }

    bool HasExactStorageIdentity(TikTokProfileEntry left, TikTokProfileEntry right)
        => SameCanonicalPath(left.ProfilePath, right.ProfilePath)
            && SameCanonicalPath(_profileService.ResolveDataRoot(left), _profileService.ResolveDataRoot(right));

    static bool SameCanonicalPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var canonicalLeft = Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var canonicalRight = Path.GetFullPath(right.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return canonicalLeft.Equals(canonicalRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    void ShowRenameFailure(string reason)
        => MessageBox.Show(this, "Không thể đổi tên: " + reason, "Đổi tên profile", MessageBoxButtons.OK, MessageBoxIcon.Error);

    void ApplyCommittedProfileRename(ProfileContext context, string oldName, TikTokProfileEntry renamed)
    {
        // Contexts used display names as dictionary keys.  Remove each key
        // that points to this exact context, then reinsert it under the
        // verified new name without replacing its tab/host/Worker objects.
        foreach (var key in _contexts.Where(pair => ReferenceEquals(pair.Value, context)).Select(pair => pair.Key).ToList())
            _contexts.Remove(key);
        _contexts.Remove(oldName);
        context.Profile = renamed;
        _contexts[renamed.Name] = context;
        if (context.Tab is not null && !context.Tab.IsDisposed)
            context.Tab.Text = renamed.Name;
        context.WorkerWindow = IntPtr.Zero;
        context.Detached = false;
        EnsureAddTab();
        RefreshAvailability();
        UpdateTitle();
    }

    void ShowChromeNameSyncDialog()
    {
        var candidates = _contexts.Values
            .OrderBy(c => c.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DeleteProfileListItem { Context = c })
            .ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show("Chưa có profile nào để đồng bộ.", "Đồng bộ tên Chrome", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "Đồng bộ tên Chrome Profile",
            Width = 760,
            Height = 640,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(dialog);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
            Margin = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "Chọn profile cần đồng bộ",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        };
        ModernDialog.StylePrimaryLabel(title);
        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Margin = new Padding(0, 0, 0, 8),
            Text = "Tên Chrome sẽ được đặt theo tên profile Tool. Nếu Chrome của profile đang mở, chỉ Chrome dùng đúng ProfilePath đó sẽ được đóng, cập nhật và mở lại.",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var search = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Tìm profile...",
            Font = new Font("Segoe UI", 11F),
            Margin = new Padding(0, 0, 0, 10)
        };
        ModernDialog.StyleTextInput(search);
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            HorizontalScrollbar = true,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 11F),
            ItemHeight = 34,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0)
        };
        ModernDialog.StyleSelectionList(list);
        var selectAll = new Button
        {
            Text = "Chọn tất cả",
            Size = new Size(112, 42),
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.FromArgb(247, 249, 252),
            ForeColor = Color.FromArgb(55, 76, 103),
            FlatStyle = FlatStyle.Flat
        };
        selectAll.FlatAppearance.BorderColor = Color.FromArgb(190, 201, 214);
        var clear = new Button
        {
            Text = "Bỏ chọn",
            Size = new Size(94, 42),
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.FromArgb(247, 249, 252),
            ForeColor = Color.FromArgb(55, 76, 103),
            FlatStyle = FlatStyle.Flat
        };
        clear.FlatAppearance.BorderColor = Color.FromArgb(190, 201, 214);
        var sync = new Button
        {
            Text = "Đồng bộ",
            Size = new Size(108, 42),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.FromArgb(232, 242, 255),
            ForeColor = Color.FromArgb(35, 91, 152),
            FlatStyle = FlatStyle.Flat
        };
        sync.FlatAppearance.BorderColor = Color.FromArgb(130, 173, 220);
        var cancel = new Button
        {
            Text = "Hủy",
            DialogResult = DialogResult.Cancel,
            Size = new Size(94, 42),
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.FromArgb(247, 249, 252),
            ForeColor = Color.FromArgb(55, 76, 103),
            FlatStyle = FlatStyle.Flat
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(190, 201, 214);
        ModernDialog.StyleSecondaryButton(selectAll);
        ModernDialog.StyleSecondaryButton(clear);
        ModernDialog.StylePrimaryButton(sync);
        ModernDialog.StyleSecondaryButton(cancel);
        var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rebuildingList = false;

        void CaptureVisibleChecks()
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.Items[i] is not DeleteProfileListItem item) continue;
                if (list.GetItemChecked(i)) checkedNames.Add(item.Context.Profile.Name);
                else checkedNames.Remove(item.Context.Profile.Name);
            }
        }

        void ApplyFilter(bool captureVisibleChecks = true)
        {
            if (captureVisibleChecks) CaptureVisibleChecks();
            var keyword = search.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? candidates
                : candidates.Where(item => item.Context.Profile.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            rebuildingList = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in filtered)
                {
                    var index = list.Items.Add(item);
                    list.SetItemChecked(index, checkedNames.Contains(item.Context.Profile.Name));
                }
            }
            finally
            {
                list.EndUpdate();
                rebuildingList = false;
            }
        }

        void SetSyncControlsEnabled(bool enabled)
        {
            search.Enabled = list.Enabled = selectAll.Enabled = clear.Enabled = sync.Enabled = cancel.Enabled = enabled;
            dialog.ControlBox = enabled;
        }

        search.TextChanged += (_, _) => ApplyFilter();
        list.ItemCheck += (_, e) =>
        {
            if (rebuildingList || e.Index < 0 || e.Index >= list.Items.Count || list.Items[e.Index] is not DeleteProfileListItem item) return;
            if (e.NewValue == CheckState.Checked) checkedNames.Add(item.Context.Profile.Name);
            else checkedNames.Remove(item.Context.Profile.Name);
        };
        selectAll.Click += (_, _) =>
        {
            checkedNames.Clear();
            foreach (var item in candidates) checkedNames.Add(item.Context.Profile.Name);
            ApplyFilter(captureVisibleChecks: false);
        };
        clear.Click += (_, _) =>
        {
            checkedNames.Clear();
            ApplyFilter(captureVisibleChecks: false);
        };
        sync.Click += async (_, _) =>
        {
            CaptureVisibleChecks();
            var selected = candidates
                .Where(item => checkedNames.Contains(item.Context.Profile.Name))
                .Select(item => item.Context)
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(dialog, "Hãy tích ít nhất một profile.", "Đồng bộ tên Chrome", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetSyncControlsEnabled(false);
            var succeeded = new List<string>();
            var failed = new List<string>();
            foreach (var context in selected)
            {
                try
                {
                    var result = await SynchronizeChromeProfileNameAsync(context);
                    succeeded.Add($"{context.Profile.Name} ({(result.Updated ? "đã cập nhật" : "đã đúng")})");
                }
                catch (Exception ex)
                {
                    _log.Error($"[CHROME_PROFILE_NAME_SYNC] profile={context.Profile.Name} {ex}");
                    failed.Add($"{context.Profile.Name}: {ex.Message}");
                }
            }

            var message = succeeded.Count > 0 ? "Đã đồng bộ:\n" + string.Join(Environment.NewLine, succeeded) : "";
            if (failed.Count > 0)
                message += (message.Length > 0 ? "\n\n" : "") + "Không đồng bộ được:\n" + string.Join(Environment.NewLine, failed);
            MessageBox.Show(dialog, message, "Đồng bộ tên Chrome", MessageBoxButtons.OK, failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (failed.Count == 0)
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            else if (!dialog.IsDisposed) SetSyncControlsEnabled(true);
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0),
            Margin = new Padding(0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(sync);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(selectAll);
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(note, 0, 1);
        root.Controls.Add(search, 0, 2);
        root.Controls.Add(list, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(root);
        dialog.AcceptButton = sync;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => search.Focus();
        ApplyFilter();
        dialog.ShowDialog(this);
    }

    async Task<ChromeProfileNameSyncService.SyncResult> SynchronizeChromeProfileNameAsync(ProfileContext context)
    {
        var profile = context.Profile;
        var runtime = await CloseChromeForNameSyncAsync(context, profile.ProfilePath);
        try
        {
            var result = _chromeProfileNameSync.SyncBeforeLaunch(profile.ProfilePath, profile.Name);
            _log.Info($"[CHROME_PROFILE_NAME_SYNC] profile={profile.Name} updated={result.Updated} preferences={result.PreferencesPath}");
            return result;
        }
        finally
        {
            if (runtime.ChromeWasOpen)
                await ReopenChromeAfterNameSyncAsync(context, runtime.AutomationWasRunning);
        }
    }

    async Task<ChromeNameSyncRuntimeState> CloseChromeForNameSyncAsync(ProfileContext context, string profilePath)
    {
        var chromeWasOpen = ChromeProfileNameSyncService.IsProfileInUse(profilePath);
        var automationWasRunning = false;
        if (chromeWasOpen && context.Worker is not null && !context.Worker.HasExited)
        {
            await context.CommandGate.WaitAsync();
            try
            {
                try
                {
                    var raw = await SendPipeAsync(context.Profile.Name, "status", TimeSpan.FromSeconds(2));
                    var snapshot = JsonSerializer.Deserialize<WorkerSnapshot>(raw, WorkerSnapshotJson);
                    automationWasRunning = snapshot?.RunState == "RUNNING";
                }
                catch { }
                if (automationWasRunning)
                    await SendPipeAsync(context.Profile.Name, "stop", TimeSpan.FromSeconds(5));

                for (var attempt = 0; attempt < 4 && ChromeProfileNameSyncService.IsProfileInUse(profilePath); attempt++)
                {
                    try { await SendPipeAsync(context.Profile.Name, "close_chrome", ChromeCloseTimeout); }
                    catch (Exception ex) { _log.Warn($"[{context.Profile.Name}] close Chrome để đồng bộ tên: {ex.Message}"); }
                    await Task.Delay(350);
                }
            }
            finally { context.CommandGate.Release(); }
        }

        var closeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (ChromeProfileNameSyncService.IsProfileInUse(profilePath) && DateTime.UtcNow < closeDeadline)
        {
            ChromeProfileNameSyncService.StopChromeUsingProfile(profilePath);
            await Task.Delay(250);
        }
        if (ChromeProfileNameSyncService.IsProfileInUse(profilePath))
            throw new InvalidOperationException($"Không thể đóng Chrome của profile “{context.Profile.Name}” tại ProfilePath={profilePath}. Không ghi Preferences khi Chrome còn mở.");
        return new ChromeNameSyncRuntimeState(chromeWasOpen, automationWasRunning);
    }

    async Task ReopenChromeAfterNameSyncAsync(ProfileContext context, bool restartAutomation)
    {
        await SendCommandAsync(context, "launch", TimeSpan.FromSeconds(25));
        if (restartAutomation)
            await SendCommandAsync(context, "start", TimeSpan.FromSeconds(30));
    }

    async Task<string?> TryRestoreProfileRuntimeAfterRenameAsync(ProfileContext context, bool restoreWorker, ChromeNameSyncRuntimeState? runtime)
    {
        if (!restoreWorker && runtime?.ChromeWasOpen != true) return null;
        try
        {
            // Do not use the generic OpenProfileAsync here: rename deliberately
            // blocks normal opens.  Recreate and embed the same ProfileContext
            // so an already-open tab never turns into a blank/new profile.
            await EnsureWorkerAsync(context);
            await RefreshStatusAsync(context);
            if (context.Host is not null)
                await EmbedWorkerAsync(context);

            if (runtime?.ChromeWasOpen == true)
            {
                var result = await SendCommandAsync(context, "launch", TimeSpan.FromSeconds(25));
                if (!result.Equals("opened", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Worker không mở lại Chrome: " + result);
                if (runtime.AutomationWasRunning)
                    await SendCommandAsync(context, "start", TimeSpan.FromSeconds(30));
            }
            return null;
        }
        catch (Exception ex)
        {
            SetStatus(context, "Không thể khôi phục Worker/Chrome: " + ex.Message, Color.Firebrick);
            return ex.Message;
        }
    }

    async Task ShutdownWorkerForProfileRenameAsync(ProfileContext context)
    {
        if (context.Worker is null || context.Worker.HasExited) return;
        await context.CommandGate.WaitAsync();
        try
        {
            try { await SendPipeAsync(context.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch (Exception ex) { _log.Warn($"[{context.Profile.Name}] shutdown worker để đổi tên: {ex.Message}"); }
            if (!await WaitForProcessExitAsync(context.Worker, TimeSpan.FromSeconds(7)))
            {
                try { context.Worker.Kill(entireProcessTree: true); } catch { }
                if (!await WaitForProcessExitAsync(context.Worker, TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException($"Worker của profile “{context.Profile.Name}” không dừng được; không đổi tên profile.");
            }
            try { context.Worker.Dispose(); } catch { }
            context.Worker = null;
        }
        finally { context.CommandGate.Release(); }
    }

    void ShowDeleteProfilesDialog()
    {
        var candidates = _contexts.Values
            .OrderBy(c => c.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DeleteProfileListItem { Context = c })
            .ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show("Chưa có profile nào để xóa.", "Xóa profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "Xóa profile đã lưu",
            Width = 940,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.SizableToolWindow
        };
        ModernDialog.Apply(dialog, fixedDialog: false);
        var instruction = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 10, 12, 0),
            Text = "Chọn một hoặc nhiều profile. Chỉ các profile được tích mới bị dừng Worker/Chrome, xóa dữ liệu và xóa khỏi Manager.",
            AutoEllipsis = true
        };
        ModernDialog.StylePrimaryLabel(instruction);
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            HorizontalScrollbar = true,
            IntegralHeight = false
        };
        ModernDialog.StyleSelectionList(list);
        list.Items.AddRange(candidates.Cast<object>().ToArray());

        var selectAll = new Button { Text = "Chọn tất cả", AutoSize = true };
        var clear = new Button { Text = "Bỏ chọn", AutoSize = true };
        var delete = new Button { Text = "Xóa", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Hủy", AutoSize = true, DialogResult = DialogResult.Cancel };
        ModernDialog.StyleSecondaryButton(selectAll);
        ModernDialog.StyleSecondaryButton(clear);
        ModernDialog.StyleDestructiveButton(delete);
        ModernDialog.StyleSecondaryButton(cancel);
        selectAll.Click += (_, _) => { for (var i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        clear.Click += (_, _) => { for (var i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
        delete.Click += async (_, _) =>
        {
            var selected = list.CheckedItems.Cast<DeleteProfileListItem>().Select(x => x.Context).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(dialog, "Hãy tích ít nhất một profile cần xóa.", "Xóa profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            delete.Enabled = selectAll.Enabled = clear.Enabled = cancel.Enabled = false;
            try
            {
                if (await DeleteProfilesAsync(selected))
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                _log.Error("[PROFILE_DELETE] " + ex);
                MessageBox.Show(dialog, ex.Message, "Không thể xóa profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!dialog.IsDisposed) delete.Enabled = selectAll.Enabled = clear.Enabled = cancel.Enabled = true;
            }
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(10, 8, 10, 8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel); buttons.Controls.Add(delete); buttons.Controls.Add(clear); buttons.Controls.Add(selectAll);
        dialog.Controls.Add(list); dialog.Controls.Add(instruction); dialog.Controls.Add(buttons);
        dialog.AcceptButton = delete;
        dialog.CancelButton = cancel;
        dialog.ShowDialog(this);
    }

    async Task<bool> DeleteProfilesAsync(IReadOnlyList<ProfileContext> selectedContexts)
    {
        var plans = BuildDeletionPlans(selectedContexts);
        var confirmation = string.Join(Environment.NewLine, plans.Select(p => $"• {p.Profile.Name}\n  Chrome: {p.ChromeProfilePath}\n  Dữ liệu Tool: {p.DataRoot}"));
        if (MessageBox.Show(this,
            "Các profile sau sẽ bị xóa hoàn toàn (Worker/Chrome sẽ được dừng trước):\n\n" + confirmation + "\n\nKhông thể hoàn tác.",
            "Xác nhận xóa profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return false;

        var catalog = _profileService.Load();
        var deleted = new List<ProfileDeletionPlan>();
        foreach (var plan in plans)
        {
            try
            {
                await StopProfileRuntimeForDeletionAsync(plan);
                DeleteDirectoryStrict(plan.Profile.Name, "dữ liệu Tool", plan.DataRoot);
                await DeleteChromeProfileDirectoryWithRetryAsync(plan.Profile.Name, plan.ChromeProfilePath);
                RemoveManagedProfileContainerIfEmpty(plan.ChromeProfilePath);

                _profileService.RemoveFromCatalog(catalog, plan.Profile.Name);
                _profileService.SaveWithBackup(catalog);
                // Giữ nguyên lịch sử "Profile đã gán" trong Kho tài khoản và file Excel.
                // Xóa profile chỉ xóa profile/Chrome/data của Tool, KHÔNG bỏ gán account.
                // Nhờ vậy cột "Profile đã gán" vẫn giữ giá trị cũ để kiểm soát lịch sử acc về sau.
                _log.Info($"[PROFILE_DELETE_KEEP_ACCOUNT_ASSIGNMENT] profile={plan.Profile.Name}");
                deleted.Add(plan);
                _log.Warn($"[PROFILE_DELETED] name={plan.Profile.Name} profilePath={plan.ChromeProfilePath} dataRoot={plan.DataRoot}");
            }
            catch (Exception ex)
            {
                try { PersistCatalogWithoutDeletedReferences(catalog); }
                catch (Exception persistEx) { _log.Error("[PROFILE_DELETE] cannot refresh catalog backup: " + persistEx); }
                FinalizeDeletedProfiles(deleted);
                throw new InvalidOperationException(
                    $"Không xóa được profile “{plan.Profile.Name}”. Các profile đã xóa trước đó: {(deleted.Count == 0 ? "(không có)" : string.Join(", ", deleted.Select(x => x.Profile.Name)))}.\n\n{ex.Message}", ex);
            }
        }

        PersistCatalogWithoutDeletedReferences(catalog);
        FinalizeDeletedProfiles(deleted);
        MessageBox.Show(this, "Đã xóa: " + string.Join(", ", deleted.Select(x => x.Profile.Name)), "Xóa profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    void PersistCatalogWithoutDeletedReferences(TikTokProfileCatalog catalog)
    {
        // SaveWithBackup preserves the pre-delete catalog by design.  For an
        // explicit destructive delete, keep the backup in sync too so deleted
        // profiles cannot be restored accidentally through a stale reference.
        _profileService.Save(catalog);
        _profileService.BackupCatalogIfExists();
    }

    List<ProfileDeletionPlan> BuildDeletionPlans(IReadOnlyList<ProfileContext> selectedContexts)
    {
        var selectedNames = selectedContexts.Select(c => c.Profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selectedNames.Count != selectedContexts.Count)
            throw new InvalidOperationException("Danh sách profile chọn để xóa có mục trùng lặp; không thực hiện thay đổi nào.");

        var catalog = _profileService.Load();
        var plans = new List<ProfileDeletionPlan>();
        foreach (var name in selectedNames)
        {
            var profile = catalog.Profiles.SingleOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Profile “{name}” không còn tồn tại trong cấu hình. Không thực hiện xóa.");
            var context = _contexts.TryGetValue(profile.Name, out var value)
                ? value
                : throw new InvalidOperationException($"Không tìm được Worker context cho profile “{profile.Name}”. Không thực hiện xóa.");
            var chromePath = Path.GetFullPath(profile.ProfilePath);
            var dataRoot = _profileService.ResolveDataRoot(profile);
            EnsureSafeDeletionTargets(profile, chromePath, dataRoot);
            plans.Add(new ProfileDeletionPlan(context, profile, chromePath, dataRoot));
        }
        EnsureNoSharedDeletionTargets(catalog.Profiles, plans);
        return plans;
    }

    void EnsureNoSharedDeletionTargets(IEnumerable<TikTokProfileEntry> allProfiles, IReadOnlyList<ProfileDeletionPlan> plans)
    {
        var selectedNames = new HashSet<string>(plans.Select(x => x.Profile.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            foreach (var other in allProfiles.Where(p => !selectedNames.Contains(p.Name)))
            {
                var otherChrome = Path.GetFullPath(other.ProfilePath);
                var otherData = _profileService.ResolveDataRoot(other);
                if (PathsOverlap(plan.ChromeProfilePath, otherChrome)
                    || PathsOverlap(plan.ChromeProfilePath, otherData)
                    || PathsOverlap(plan.DataRoot, otherChrome)
                    || PathsOverlap(plan.DataRoot, otherData))
                    throw new InvalidOperationException($"[DELETE_PATH_CONFLICT] Profile “{plan.Profile.Name}” dùng thư mục trùng/chồng lấn với profile chưa chọn “{other.Name}”. Không xóa profile nào.");
            }
        }

        for (var i = 0; i < plans.Count; i++)
        for (var j = i + 1; j < plans.Count; j++)
            if (PathsOverlap(plans[i].ChromeProfilePath, plans[j].ChromeProfilePath)
                || PathsOverlap(plans[i].ChromeProfilePath, plans[j].DataRoot)
                || PathsOverlap(plans[i].DataRoot, plans[j].ChromeProfilePath)
                || PathsOverlap(plans[i].DataRoot, plans[j].DataRoot))
                throw new InvalidOperationException($"[DELETE_PATH_CONFLICT] Hai profile được chọn “{plans[i].Profile.Name}” và “{plans[j].Profile.Name}” dùng thư mục trùng/chồng lấn. Không xóa profile nào.");
    }

    void EnsureSafeDeletionTargets(TikTokProfileEntry profile, string chromePath, string dataRoot)
    {
        var managedChromeRoot = Path.GetFullPath(TikTokProfileService.ProfilesRoot);
        var legacyChromePath = Path.GetFullPath(TikTokProfileService.LegacyImportedProfilePath);
        if (!IsChildPathOf(chromePath, managedChromeRoot) && !chromePath.Equals(legacyChromePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[DELETE_PATH_BLOCKED] Profile “{profile.Name}” có ProfilePath ngoài vùng dữ liệu Tool: {chromePath}. Không xóa cấu hình hay thư mục nào.");

        var dataRootBase = Path.Combine(_baseDir, "profiles");
        if (!IsChildPathOf(dataRoot, dataRootBase))
            throw new InvalidOperationException($"[DELETE_PATH_BLOCKED] Profile “{profile.Name}” có DataRoot ngoài vùng dữ liệu V13: {dataRoot}. Không xóa cấu hình hay thư mục nào.");

        if (chromePath.Equals(dataRoot, StringComparison.OrdinalIgnoreCase)
            || IsChildPathOf(chromePath, dataRoot)
            || IsChildPathOf(dataRoot, chromePath))
            throw new InvalidOperationException($"[DELETE_PATH_BLOCKED] Profile “{profile.Name}” có ProfilePath/DataRoot chồng lấn bất thường. Không xóa để tránh nhầm thư mục.");
    }

    static bool IsChildPathOf(string targetPath, string rootPath)
    {
        var target = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static bool PathsOverlap(string left, string right)
        => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || IsChildPathOf(left, right)
        || IsChildPathOf(right, left);

    async Task StopProfileRuntimeForDeletionAsync(ProfileDeletionPlan plan)
    {
        var context = plan.Context;
        await context.CommandGate.WaitAsync();
        try
        {
            if (context.Worker is not null && !context.Worker.HasExited)
            {
                try { await SendPipeAsync(plan.Profile.Name, "stop", TimeSpan.FromSeconds(5)); } catch (Exception ex) { _log.Warn($"[{plan.Profile.Name}] stop worker: {ex.Message}"); }
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        var result = await SendPipeAsync(plan.Profile.Name, "close_chrome", ChromeCloseTimeout);
                        if (result is "closed" or "not_running") break;
                    }
                    catch (Exception ex) { _log.Warn($"[{plan.Profile.Name}] close Chrome: {ex.Message}"); }
                    await Task.Delay(400);
                }
                try { await SendPipeAsync(plan.Profile.Name, "shutdown", TimeSpan.FromSeconds(5)); } catch (Exception ex) { _log.Warn($"[{plan.Profile.Name}] shutdown worker: {ex.Message}"); }
                if (!await WaitForProcessExitAsync(context.Worker, TimeSpan.FromSeconds(7)))
                {
                    try { context.Worker.Kill(entireProcessTree: true); } catch { }
                    if (!await WaitForProcessExitAsync(context.Worker, TimeSpan.FromSeconds(3)))
                        throw new InvalidOperationException($"Worker của profile “{plan.Profile.Name}” không dừng được; không xóa dữ liệu.");
                }
                try { context.Worker.Dispose(); } catch { }
                context.Worker = null;
            }
        }
        finally { context.CommandGate.Release(); }

        // Covers a manually opened Chrome as well as a worker that did not answer.
        var stoppedPids = ChromeProfileNameSyncService.StopChromeUsingProfile(plan.ChromeProfilePath);
        if (stoppedPids.Count > 0)
            _log.Warn($"[{plan.Profile.Name}] đã dừng Chrome PID={string.Join(',', stoppedPids)} theo ProfilePath đã lưu.");
        var end = DateTime.UtcNow.AddSeconds(5);
        while (ChromeProfileNameSyncService.IsProfileInUse(plan.ChromeProfilePath) && DateTime.UtcNow < end)
        {
            ChromeProfileNameSyncService.StopChromeUsingProfile(plan.ChromeProfilePath);
            await Task.Delay(250);
        }
        if (ChromeProfileNameSyncService.IsProfileInUse(plan.ChromeProfilePath))
            throw new InvalidOperationException($"Chrome của profile “{plan.Profile.Name}” vẫn đang khóa ProfilePath: {plan.ChromeProfilePath}. Không xóa dữ liệu.");
    }

    static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited) return true;
        using var cts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cts.Token); return true; }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return process.HasExited; }
    }

    async Task DeleteChromeProfileDirectoryWithRetryAsync(string profileName, string path)
    {
        if (!Directory.Exists(path)) return;

        // Chrome/Crashpad can outlive the visible Chrome window for a short time and keep
        // files such as CrashpadMetrics-active.pma open.  Re-scan processes that reference
        // this exact profile path and retry deletion instead of failing on the first locked file.
        var retryDelaysMs = new[] { 250, 350, 500, 650, 800, 1000, 1200, 1500 };
        Exception? lastError = null;

        for (var attempt = 1; attempt <= retryDelaysMs.Length; attempt++)
        {
            var stoppedPids = ChromeProfileNameSyncService.StopChromeUsingProfile(path);
            if (stoppedPids.Count > 0)
                _log.Warn($"[{profileName}] [PROFILE_DELETE_UNLOCK] attempt={attempt}/{retryDelaysMs.Length} stoppedPid={string.Join(',', stoppedPids)}");

            // Give Windows a moment to release late Crashpad/Chrome file handles.
            await Task.Delay(retryDelaysMs[attempt - 1]);

            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                {
                    if (attempt > 1)
                        _log.Info($"[{profileName}] [PROFILE_DELETE_RETRY_OK] attempt={attempt}/{retryDelaysMs.Length}");
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                _log.Warn($"[{profileName}] [PROFILE_DELETE_LOCKED] attempt={attempt}/{retryDelaysMs.Length} detail={ex.Message}");
            }
        }

        throw new IOException(
            $"Không xóa được Chrome profile của profile “{profileName}” sau {retryDelaysMs.Length} lần thử. " +
            $"Tool đã dừng lại Chrome/Crashpad theo đúng ProfilePath nhưng Windows vẫn đang khóa tệp: {path}\n" +
            $"Chi tiết: {lastError?.Message ?? "Thư mục vẫn còn tồn tại."}",
            lastError);
    }

    static void DeleteDirectoryStrict(string profileName, string kind, string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Không xóa được {kind} của profile “{profileName}”. Đường dẫn đang bị khóa hoặc không có quyền truy cập: {path}\nChi tiết: {ex.Message}", ex);
        }
        if (Directory.Exists(path))
            throw new IOException($"Không xóa được {kind} của profile “{profileName}”. Thư mục vẫn còn tồn tại: {path}");
    }

    static void RemoveManagedProfileContainerIfEmpty(string chromeProfilePath)
    {
        var parent = Directory.GetParent(chromeProfilePath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsChildPathOf(parent, TikTokProfileService.ProfilesRoot) || !Directory.Exists(parent)) return;
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent, recursive: false);
    }

    void FinalizeDeletedProfiles(IEnumerable<ProfileDeletionPlan> deleted)
    {
        foreach (var plan in deleted)
        {
            RemoveTab(plan.Context);
            _contexts.Remove(plan.Profile.Name);
        }
        ReloadCatalog();
        EnsureAddTab();
        RefreshAvailability();
        UpdateTitle();
    }

    ProfileContext? SelectedContext()
    {
        if (!TryGetSelectedTabPage(out var page) || page is null) return null;
        return page.Tag as ProfileContext;
    }

    ProfileOpenSelection? ChooseProfiles(IReadOnlyList<ProfileContext> contexts, string title)
    {
        var allItems = contexts.Select(context => new OpenProfileListItem { Context = context }).ToList();
        var chooserColumns = Math.Max(1, (allItems.Count + 9) / 10);
        using var form = new Form
        {
            Text = title,
            Width = Math.Clamp(chooserColumns * 178 + 70, 500, 1120),
            Height = 565,
            MinimumSize = new Size(500, 440),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F),
            KeyPreview = true
        };
        ModernDialog.Apply(form, fixedDialog: false);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 12, 14, 10),
            Margin = new Padding(0),
            BackColor = ModernDialog.Canvas
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 8)
        };
        var label = new Label { Text = "Chọn profile cần mở", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
        var search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Tìm profile...", Font = new Font("Segoe UI", 11F), Margin = new Padding(0, 0, 0, 8) };
        var multiSelect = new CheckBox { Text = "Chọn nhiều profile", AutoSize = true, Font = new Font("Segoe UI", 10F), Margin = new Padding(0, 0, 0, 2) };
        ModernDialog.StylePrimaryLabel(label);
        ModernDialog.StyleTextInput(search);
        header.Controls.Add(label, 0, 0);
        header.Controls.Add(search, 0, 1);
        header.Controls.Add(multiSelect, 0, 2);

        var viewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Margin = new Padding(0)
        };
        var profileGrid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(6),
            Margin = new Padding(0),
            BackColor = Color.White
        };
        viewport.Controls.Add(profileGrid);

        var open = new Button { Text = "Mở profile", Size = new Size(132, 42) };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(104, 42) };
        ModernDialog.StylePrimaryButton(open);
        ModernDialog.StyleSecondaryButton(cancel);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            Margin = new Padding(0)
        };
        footer.Controls.Add(cancel);
        footer.Controls.Add(open);

        outer.Controls.Add(header, 0, 0);
        outer.Controls.Add(viewport, 0, 1);
        outer.Controls.Add(footer, 0, 2);
        form.Controls.Add(outer);
        form.AcceptButton = open;
        form.CancelButton = cancel;

        var checkedContexts = new HashSet<ProfileContext>();
        ProfileContext? selectedSingleContext = allItems.FirstOrDefault()?.Context;
        ProfileOpenSelection? selection = null;

        void RebuildProfiles()
        {
            var keyword = search.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? allItems
                : allItems.Where(item => item.Context.Profile.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!multiSelect.Checked && selectedSingleContext is not null && !filtered.Any(x => ReferenceEquals(x.Context, selectedSingleContext)))
                selectedSingleContext = filtered.FirstOrDefault()?.Context;

            profileGrid.SuspendLayout();
            profileGrid.Controls.Clear();
            profileGrid.ColumnStyles.Clear();
            profileGrid.RowStyles.Clear();
            profileGrid.RowCount = 10;
            for (var r = 0; r < 10; r++) profileGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            var columns = Math.Max(1, (filtered.Count + 9) / 10);
            profileGrid.ColumnCount = columns;
            for (var c = 0; c < columns; c++) profileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));

            for (var i = 0; i < filtered.Count; i++)
            {
                var item = filtered[i];
                var col = i / 10;
                var row = i % 10;
                if (multiSelect.Checked)
                {
                    var check = new CheckBox
                    {
                        Text = item.Context.Profile.Name,
                        Tag = item.Context,
                        Checked = checkedContexts.Contains(item.Context),
                        AutoSize = false,
                        Width = 164,
                        Height = 30,
                        Margin = new Padding(2, 1, 2, 1),
                        Padding = new Padding(2, 0, 0, 0),
                        Font = new Font("Segoe UI", 10.5F),
                        BackColor = Color.White
                    };
                    check.CheckedChanged += (_, _) =>
                    {
                        if (check.Checked) checkedContexts.Add(item.Context);
                        else checkedContexts.Remove(item.Context);
                        open.Enabled = checkedContexts.Count > 0;
                    };
                    profileGrid.Controls.Add(check, col, row);
                }
                else
                {
                    var radio = new RadioButton
                    {
                        Text = item.Context.Profile.Name,
                        Tag = item.Context,
                        Checked = ReferenceEquals(item.Context, selectedSingleContext),
                        AutoSize = false,
                        Width = 164,
                        Height = 30,
                        Margin = new Padding(2, 1, 2, 1),
                        Padding = new Padding(2, 0, 0, 0),
                        Font = new Font("Segoe UI", 10.5F),
                        BackColor = Color.White
                    };
                    radio.CheckedChanged += (_, _) =>
                    {
                        if (!radio.Checked) return;
                        selectedSingleContext = item.Context;
                        open.Enabled = true;
                    };
                    radio.MouseDoubleClick += (_, _) => { selectedSingleContext = item.Context; open.PerformClick(); };
                    profileGrid.Controls.Add(radio, col, row);
                }
            }
            profileGrid.ResumeLayout(true);
            profileGrid.Location = new Point(0, 0);
            open.Enabled = multiSelect.Checked ? checkedContexts.Count > 0 : selectedSingleContext is not null && filtered.Any(x => ReferenceEquals(x.Context, selectedSingleContext));
        }

        void OpenSelectedProfiles()
        {
            if (multiSelect.Checked)
            {
                if (checkedContexts.Count == 0)
                {
                    ModernDialog.ShowMessage(form, "Vui lòng chọn ít nhất một profile.", "Mở profile", MessageBoxIcon.Information);
                    return;
                }
                selection = new ProfileOpenSelection(
                    IsMultiple: true,
                    Contexts: allItems.Where(item => checkedContexts.Contains(item.Context)).Select(item => item.Context).ToList());
            }
            else
            {
                if (selectedSingleContext is null) return;
                selection = new ProfileOpenSelection(IsMultiple: false, Contexts: [selectedSingleContext]);
            }
            form.DialogResult = DialogResult.OK;
            form.Close();
        }

        search.TextChanged += (_, _) => RebuildProfiles();
        multiSelect.CheckedChanged += (_, _) =>
        {
            // Chế độ chọn nhiều luôn bắt đầu trống; không lấy profile đang chọn ở chế độ đơn.
            if (multiSelect.Checked) checkedContexts.Clear();
            RebuildProfiles();
            viewport.Focus();
        };
        open.Click += (_, _) => OpenSelectedProfiles();
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            }
        };
        form.Shown += (_, _) =>
        {
            ModernDialog.FitToWorkingArea(form);
            RebuildProfiles();
            search.Focus();
        };

        RebuildProfiles();
        return form.ShowDialog(this) == DialogResult.OK ? selection : null;
    }

    string? PromptText(string title, string label, string initial = "")
    {
        using var form = new Form { Text = title, Width = 440, Height = 180, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        ModernDialog.Apply(form);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        var text = new TextBox { Text = initial, Dock = DockStyle.Top };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, AutoSize = true };
        flow.Controls.Add(cancel); flow.Controls.Add(ok); root.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0); root.Controls.Add(text, 0, 1); root.Controls.Add(flow, 0, 2); form.Controls.Add(root); form.AcceptButton = ok; form.CancelButton = cancel;
        ModernDialog.StylePrimaryLabel(root.Controls.OfType<Label>().First());
        ModernDialog.StyleTextInput(text);
        ModernDialog.StylePrimaryButton(ok);
        ModernDialog.StyleSecondaryButton(cancel);
        form.Shown += (_, _) => { text.Focus(); text.SelectAll(); };
        return form.ShowDialog(this) == DialogResult.OK ? text.Text.Trim() : null;
    }

    void UpdateTitle()
    {
        var selected = SelectedContext();
        Text = selected is null ? $"Tool TikTok Manager {AppVersionInfo.Display} — VM Optimized Multi Worker" : $"Tool TikTok Manager {AppVersionInfo.Display} — {selected.Profile.Name}";
        RefreshSelectedProfilePresentation();
    }

    void RefreshSelectedProfilePresentation()
    {
        TryGetSelectedTabPage(out var selectedPage);

        var notesByProfile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var account in _accountPoolService.Load())
            {
                var profileName = (account.AssignedProfile ?? "").Trim();
                var note = (account.Note ?? "").Trim();
                if (profileName.Length == 0 || note.Length == 0)
                    continue;

                notesByProfile[profileName] = note;
            }
        }
        catch
        {
            // Ghi chú trên header chỉ là thông tin phụ.
        }

        foreach (var context in _contexts.Values)
        {
            var header = context.ProfileHeader;
            if (header is null || header.IsDisposed) continue;

            var active = ReferenceEquals(context.Tab, selectedPage);
            var headerText = active
                ? $"ĐANG CHỌN: {context.Profile.Name} | CDP {context.Profile.CdpPort}"
                : $"{context.Profile.Name} | CDP {context.Profile.CdpPort}";

            if (notesByProfile.TryGetValue(context.Profile.Name, out var noteText))
            {
                var compactNote = noteText.Length > 28
                    ? noteText[..28] + "…"
                    : noteText;
                headerText += $" | Ghi chú: {compactNote}";
            }

            header.Text = headerText;
            header.BackColor = active ? ActiveProfileColor : UiTheme.Card;
            header.ForeColor = active ? Color.White : Color.FromArgb(42, 57, 76);
        }
        _tabs.Invalidate();
    }

    async Task CloseChromeForProfileAsync(ProfileContext selected)
    {
        SetStatus(selected, "Đang đóng Chrome theo đúng ProfilePath...", Color.DarkOrange);

        var workerReply = "worker_unavailable";
        try
        {
            var workerAlive = false;
            try { workerAlive = selected.Worker is not null && !selected.Worker.HasExited; }
            catch { workerAlive = selected.Worker is not null; }

            if (workerAlive)
            {
                try
                {
                    workerReply = await SendCloseChromeCommandAsync(selected);
                    _log.Info($"[CHROME_CLOSE_WORKER] profile={selected.Profile.Name} reply={workerReply}");
                }
                catch (Exception ex)
                {
                    // Worker/IPC chết không được làm nút Đóng Chrome mất tác dụng.
                    // Fallback phía dưới sẽ probe + kill đúng ProfilePath.
                    workerReply = "worker_close_failed";
                    _log.Warn($"[CHROME_CLOSE_WORKER_WARN] profile={selected.Profile.Name} error={ex.Message}");
                }
            }

            if (workerReply == "automation_running")
            {
                SetStatus(selected, "Hãy dừng automation trước khi đóng Chrome.", Color.DarkOrange);
                _log.Warn($"[CHROME_CLOSE] profile={selected.Profile.Name} result=automation_running");
                return;
            }

            // Dù Worker báo closed/not_running vẫn xác minh lại process thật theo ProfilePath.
            // Nếu Worker đã chết/CDP hỏng, hàm này vẫn đóng được Chrome mồ côi đúng profile.
            await EnsureAutoCloseChromeStoppedAsync(selected);

            SetStatus(selected, "Đã xác minh Chrome của đúng profile đã đóng.", Color.DarkGreen);
            _log.Info($"[CHROME_CLOSE] profile={selected.Profile.Name} result=verified_closed workerReply={workerReply} profilePath={selected.Profile.ProfilePath} port={selected.Profile.CdpPort}");
        }
        catch (Exception ex)
        {
            SetStatus(selected, "Không thể xác nhận Chrome đã đóng hoàn toàn.", Color.Firebrick);
            _log.Warn($"[CHROME_CLOSE] profile={selected.Profile.Name} result=verify_failed error={ex.Message} profilePath={selected.Profile.ProfilePath} port={selected.Profile.CdpPort}");
            throw;
        }
        finally
        {
            try { await RefreshStatusAsync(selected); }
            catch (Exception ex) { _log.Warn($"[{selected.Profile.Name}] refresh status sau close Chrome: {ex.Message}"); }
        }
    }

    void SetStatus(ProfileContext ctx, string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(ctx, text, color))); return; }
        if (ctx.Status is null) return;
        ctx.Status.Text = text; ctx.Status.ForeColor = color;
    }

    void RegisterChromeMonitorHotkey()
    {
        if (_chromeMonitorHotkeyRegistered || !IsHandleCreated) return;
        _chromeMonitorHotkeyRegistered = RegisterHotKey(Handle, HOTKEY_CHROME_MONITOR_TOGGLE, 0, (uint)Keys.F8);
        if (_chromeMonitorHotkeyRegistered)
            _log.Info("[CHROME_MONITOR_HOTKEY] F8 registered");
        else
            _log.Warn("[CHROME_MONITOR_HOTKEY] Không thể đăng ký F8; có thể phím đang được ứng dụng khác sử dụng.");
    }

    void ToggleChromeMonitorFromHotkey()
    {
        if (_closing || IsDisposed || Disposing) return;

        if (_chromeMonitor is null || _chromeMonitor.IsDisposed)
        {
            ShowChromeMonitor();
            return;
        }

        // Nếu monitor đang là cửa sổ người dùng đang nhìn, F8 = ẩn.
        // Nếu đang thao tác ở Chrome (monitor vẫn còn Visible nhưng nằm phía sau),
        // F8 = đưa monitor trở lại ngay, tránh phải bấm hai lần.
        var monitorIsForeground = GetForegroundWindow() == _chromeMonitor.Handle;
        if (_chromeMonitor.Visible && monitorIsForeground)
        {
            _chromeMonitor.Hide();
            return;
        }

        ShowChromeMonitor();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_CHROME_MONITOR_TOGGLE)
        {
            ToggleChromeMonitorFromHotkey();
            return;
        }
        base.WndProc(ref m);
    }

    void ShowChromeMonitor()
    {
        if (_chromeMonitor is not null && !_chromeMonitor.IsDisposed)
        {
            // Deliberately show without an owner. An owned WinForms window is always kept
            // above its owner, which prevented the Manager from covering the monitor.
            if (!_chromeMonitor.Visible) _chromeMonitor.Show();
            if (_chromeMonitor.WindowState == FormWindowState.Minimized) _chromeMonitor.WindowState = FormWindowState.Normal;
            _chromeMonitor.BringToFront();
            _chromeMonitor.Activate();
            return;
        }

        _chromeMonitor = new ChromeMonitorForm(GetChromeMonitorProfiles, ActivateChromeFromMonitorAsync);
        _chromeMonitor.FormClosed += (_, _) => _chromeMonitor = null;
        _chromeMonitor.Show();
    }

    IReadOnlyList<ChromeMonitorProfileInfo> GetChromeMonitorProfiles()
    {
        return _contexts.Values
            .Where(c => c.Tab is not null || c.LastSnapshot?.ChromeWindowHandle > 0)
            .OrderBy(c => c.Profile.Name, NaturalProfileNameOrder)
            .Select(c =>
            {
                var snapshot = c.LastSnapshot;
                return new ChromeMonitorProfileInfo(
                    c.Profile.Name,
                    GetEffectiveRuntimeState(c),
                    snapshot?.Chrome ?? "DISCONNECTED",
                    snapshot?.ChromeWindowHandle ?? 0,
                    snapshot?.Viewer ?? -1,
                    snapshot?.Step ?? 0,
                    snapshot?.Rounds ?? 0,
                    snapshot is { F5Enabled: true } ? snapshot.F5RemainingSec : -1,
                    snapshot?.Detail ?? "",
                    c.LastStatusRefreshUtc);
            })
            .ToList();
    }

    async Task ActivateChromeFromMonitorAsync(string profileName)
    {
        if (!_contexts.TryGetValue(profileName, out var ctx)) return;
        // Dùng chung resolver của nút View. Không launch/restart Chrome chỉ vì
        // snapshot của monitor chưa có HWND hoặc đang giữ handle cũ.
        await ViewChromeForProfileAsync(ctx);
    }

    void ShowError(Exception ex)
    {
        _log.Error(ex.ToString());
        MessageBox.Show(ex.Message, "V13", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    string ResolveWorkerExe()
    {
        var candidates = new[]
        {
            Path.Combine(_baseDir, "ToolTikTokWorkerV13.exe"),
            Path.Combine(_baseDir, "ToolTikTokWorkerV13")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Không tìm thấy ToolTikTokWorkerV13.exe trong dist_v13.");
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static string PipeName(string profileName) => "ToolTikTokV13_" + profileName;
    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    sealed class WorkerSnapshot
    {
        public string Profile { get; set; } = "";
        public string State { get; set; } = "";
        public string RunState { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Chrome { get; set; } = "";
        public int CdpPort { get; set; }
        public int Pid { get; set; }
        public long WindowHandle { get; set; }
        public long ChromeWindowHandle { get; set; }
        public int Viewer { get; set; } = -1;
        public int Step { get; set; }
        public long Rounds { get; set; }
        public long TotalRunSeconds { get; set; } = -1;
        public bool F5Enabled { get; set; }
        public int F5RemainingSec { get; set; } = -1;
        public string TikTokStartupState { get; set; } = "";
        public bool MessageReplyRunning { get; set; }
    }

    sealed class ChromeViewResolutionReply
    {
        public int CachedPid { get; set; }
        public int ResolvedPid { get; set; }
        public long WindowHandle { get; set; }
        public string Reason { get; set; } = "";
    }
}
