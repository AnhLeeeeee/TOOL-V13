using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Models;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed class ManagerForm : Form
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
        public DateTime LastStatusRefreshUtc { get; set; } = DateTime.MinValue;
        public WorkerSnapshot? LastSnapshot { get; set; }
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
        _log = new Logger(_baseDir, "manager", "manager-v13.log");
        Text = "Tool TikTok Manager V13.4.1 — VM Optimized Multi Worker";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        ReloadCatalog();
        EnsureAddTab();
        _refreshTimer.Tick += async (_, _) => await RefreshOpenProfilesAsync();
        Shown += (_, _) => RegisterChromeMonitorHotkey();
        FormClosing += OnClosing;
    }

    void BuildLayout()
    {
        BackColor = UiTheme.Canvas;
        Font = new Font("Segoe UI", 9F);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 8), WrapContents = true, BackColor = UiTheme.Card };
        toolbar.Controls.Add(Button("Mở profile", (_, _) => OpenProfileChooser(), UiButtonKind.Primary));
        toolbar.Controls.Add(Button("+ Profile", (_, _) => AddProfile(), UiButtonKind.Primary));
        toolbar.Controls.Add(Button("Profile có sẵn", async (_, _) => { try { await AddExistingProfileAsync(); } catch (Exception ex) { ShowError(ex); } }));
        toolbar.Controls.Add(Button("Đổi tên", async (_, _) => { try { await RenameSelectedProfileAsync(); } catch (Exception ex) { ShowError(ex); } }));
        toolbar.Controls.Add(Button("Đồng bộ tên Chrome", (_, _) => ShowChromeNameSyncDialog()));
        toolbar.Controls.Add(Button("Giám sát Chrome", (_, _) => ShowChromeMonitor(), UiButtonKind.Primary));
        toolbar.Controls.Add(Button("Xóa profile", (_, _) => ShowDeleteProfilesDialog(), UiButtonKind.Danger));
        toolbar.Controls.Add(Button("Chạy tất cả", async (_, _) => await StartAllAsync(), UiButtonKind.Primary));
        toolbar.Controls.Add(Button("Dừng tất cả", async (_, _) => await StopAllAsync(), UiButtonKind.Danger));
        toolbar.Controls.Add(_availability);

        _tabs.DrawItem += DrawTabs;
        _tabs.MouseDown += OnTabsMouseDown;
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_changingTabs) return;
            if (TryGetSelectedTabPage(out var selectedPage) && IsAddTab(selectedPage)) await HandleAddTabAsync();
            UpdateTitle();
        };
        Controls.Add(_tabs);
        Controls.Add(toolbar);
        UiTheme.Apply(this);
        StyleToolbarButtons(toolbar);
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
                "+ Profile" => (Color.FromArgb(238, 246, 255), Color.FromArgb(35, 91, 152)),
                "Profile có sẵn" or "Đổi tên" or "Đồng bộ tên Chrome" => (Color.FromArgb(242, 246, 251), Color.FromArgb(55, 76, 103)),
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
        var closeable = !IsAddTab(page);
        var active = closeable && selectedIndex >= 0 && e.Index == selectedIndex;
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
        var status = new Label { AutoSize = true, Text = "Worker: đang khởi động", Margin = new Padding(8, 8, 12, 0) };
        var openChrome = Button("Mở Chrome", async (_, _) => { try { await OpenChromeForProfileAsync(ctx); } catch (Exception ex) { ShowError(ex); } }, UiButtonKind.Primary);
        var closeChrome = Button("Đóng Chrome", async (_, _) => { try { await CloseChromeForProfileAsync(ctx); } catch (Exception ex) { ShowError(ex); } }, UiButtonKind.Danger);
        var detach = Button("Tách Worker", (_, _) => ToggleDetach(ctx));
        top.Controls.Add(profileHeader);
        top.Controls.Add(openChrome);
        top.Controls.Add(closeChrome);
        top.Controls.Add(status);
        top.Controls.Add(detach);
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
        var result = await SendCommandAsync(ctx, "launch", TimeSpan.FromSeconds(30));
        if (!string.Equals(result, "opened", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Không thể mở Chrome của profile “{ctx.Profile.Name}” (worker: {result}).");

        SetStatus(ctx, "Chrome của đúng profile đã kết nối.", Color.DarkGreen);
        _log.Info($"[CHROME_OPEN] profile={ctx.Profile.Name} profilePath={ctx.Profile.ProfilePath} port={ctx.Profile.CdpPort}");
        try { await RefreshStatusAsync(ctx); } catch (Exception ex) { _log.Warn($"[{ctx.Profile.Name}] refresh status sau mở Chrome: {ex.Message}"); }
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
        process.Exited += (_, _) => BeginInvoke(new Action(() => SetStatus(ctx, $"Worker đã thoát ({process.ExitCode})", Color.Firebrick)));
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
        for (var i = 0; i < 20; i++)
        {
            var snapshot = await ReadStatusAsync(ctx);
            if (snapshot.WindowHandle != 0)
            {
                ctx.WorkerWindow = new IntPtr(snapshot.WindowHandle);
                break;
            }
            await Task.Delay(100);
        }
        if (!WorkerWindowEmbedder.IsValid(ctx.WorkerWindow)) throw new InvalidOperationException("Không lấy được cửa sổ V13 worker.");
        ctx.Host.CreateControl();
        if (!WorkerWindowEmbedder.Attach(ctx.WorkerWindow, ctx.Host)) throw new InvalidOperationException("Không gắn được giao diện V13 vào tab.");
        ctx.Detached = false;
        if (ctx.DetachButton is not null) ctx.DetachButton.Text = "Tách Worker";
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
                if (ctx.Worker is null || ctx.Worker.HasExited) continue;

                // V13.4.1: profile đang xem vẫn refresh 1 giây như cũ. Các tab nền
                // chỉ refresh 5 giây/lần để giảm pipe/JSON/UI work khi chạy nhiều VM profile.
                var monitorVisible = _chromeMonitor is not null && !_chromeMonitor.IsDisposed && _chromeMonitor.Visible;
                var interval = ReferenceEquals(ctx.Tab, selectedTab) || monitorVisible
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(5);
                if (now - ctx.LastStatusRefreshUtc < interval) continue;

                try { await RefreshStatusAsync(ctx); } catch { }
            }
        }
        finally { _refreshing = false; }
    }

    async Task RefreshStatusAsync(ProfileContext ctx)
    {
        var s = await ReadStatusAsync(ctx);
        ctx.LastStatusRefreshUtc = DateTime.UtcNow;
        ctx.LastSnapshot = s;
        var color = s.RunState == "RUNNING" ? Color.DarkGreen : s.RunState == "PAUSED" ? Color.DarkOrange : Color.DimGray;
        SetStatus(ctx, $"Worker {s.State} | {s.RunState} | Chrome {s.Chrome}", color);
        if (s.WindowHandle != 0 && ctx.WorkerWindow == IntPtr.Zero) ctx.WorkerWindow = new IntPtr(s.WindowHandle);
    }

    async Task<WorkerSnapshot> ReadStatusAsync(ProfileContext ctx)
    {
        var raw = await SendCommandAsync(ctx, "status", TimeSpan.FromSeconds(2));
        var snapshot = JsonSerializer.Deserialize<WorkerSnapshot>(raw, WorkerSnapshotJson) ?? new WorkerSnapshot();
        if (!snapshot.Profile.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase) || snapshot.CdpPort != ctx.Profile.CdpPort)
            throw new InvalidOperationException($"[WORKER_PROFILE_MISMATCH] Expected profile={ctx.Profile.Name}, CDP={ctx.Profile.CdpPort}; worker reported profile={snapshot.Profile}, CDP={snapshot.CdpPort}.");
        return snapshot;
    }

    async Task<string> SendCommandAsync(ProfileContext ctx, string command, TimeSpan? timeout = null)
    {
        await ctx.CommandGate.WaitAsync();
        try
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);

            // Status is polled every second for every open profile.  A healthy
            // worker does not need a separate ping pipe before the status pipe.
            // Send status directly; only on failure do the old health/restart
            // path and one idempotent retry.  Non-idempotent commands retain
            // the original ping-before-send behavior.
            if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Worker is null || ctx.Worker.HasExited)
                    await EnsureWorkerAsync(ctx);

                try
                {
                    return await SendPipeAsync(ctx.Profile.Name, command, effectiveTimeout);
                }
                catch when (!_closing)
                {
                    await EnsureWorkerAsync(ctx);
                    return await SendPipeAsync(ctx.Profile.Name, command, effectiveTimeout);
                }
            }

            await EnsureWorkerAsyncIfCommandNeedsIt(ctx, command);
            return await SendPipeAsync(ctx.Profile.Name, command, effectiveTimeout);
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
        foreach (var ctx in _contexts.Values.OrderBy(c => c.Profile.Name))
        {
            try
            {
                await OpenProfileAsync(ctx);
                await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                SetStatus(ctx, "Không chạy được: " + ex.Message, Color.Firebrick);
                _log.Error($"[{ctx.Profile.Name}] START_ALL: {ex}");
            }
        }
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
        var name = ShowAddProfileDialog(catalog);
        if (name is null) return;

        TikTokProfileEntry? entry = null;
        try
        {
            entry = _profileService.CreateManagedProfile(name);
            _chromeProfileNameSync.SyncBeforeLaunch(entry.ProfilePath, entry.Name);
            catalog.Profiles.Add(entry);
            catalog.SelectedProfile = entry.Name;
            _profileService.EnsurePorts(catalog.Profiles);
            _profileService.SaveWithBackup(catalog);
        }
        catch (Exception ex)
        {
            var rollbackError = entry is null ? null : TryRollbackCreatedProfile(entry);
            try { ReloadCatalog(); }
            catch (Exception reloadEx) { _log.Error("[PROFILE_CREATE] cannot reload catalog after failed creation: " + reloadEx); }

            var detail = $"Không thể tạo profile {name}: {ex.Message}";
            if (!string.IsNullOrWhiteSpace(rollbackError)) detail += "\n\nKhông thể dọn dữ liệu tạo dở: " + rollbackError;
            _log.Error("[PROFILE_CREATE] name=" + name + " " + ex);
            MessageBox.Show(this, detail, "Thêm profile TikTok", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            ReloadCatalog();
            _log.Info($"[PROFILE_CREATED] name={entry.Name} profilePath={entry.ProfilePath}");
            MessageBox.Show(this, $"Đã tạo profile {entry.Name} thành công", "Thêm profile TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Error("[PROFILE_CREATE] profile created but UI refresh failed: " + ex);
            MessageBox.Show(this, $"Đã tạo profile {entry.Name}, nhưng không thể cập nhật giao diện: {ex.Message}", "Thêm profile TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    string? ShowAddProfileDialog(TikTokProfileCatalog catalog)
    {
        using var form = new Form
        {
            Text = "Thêm profile TikTok",
            Width = 470,
            Height = 270,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
            Margin = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "Tên profile mới",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        ModernDialog.StylePrimaryLabel(label);
        var nameBox = new TextBox
        {
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 11F),
            MinimumSize = new Size(0, 36),
            Margin = new Padding(0)
        };
        ModernDialog.StyleTextInput(nameBox);
        var create = new Button
        {
            Text = "Tạo profile",
            Size = new Size(132, 42),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.FromArgb(232, 242, 255),
            ForeColor = Color.FromArgb(35, 91, 152),
            FlatStyle = FlatStyle.Flat
        };
        create.FlatAppearance.BorderColor = Color.FromArgb(130, 173, 220);
        var cancel = new Button
        {
            Text = "Hủy",
            DialogResult = DialogResult.Cancel,
            Size = new Size(104, 42),
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.FromArgb(247, 249, 252),
            ForeColor = Color.FromArgb(55, 76, 103),
            FlatStyle = FlatStyle.Flat
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(190, 201, 214);
        ModernDialog.StylePrimaryButton(create);
        ModernDialog.StyleSecondaryButton(cancel);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0),
            Margin = new Padding(0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(create);
        root.Controls.Add(label, 0, 0);
        root.Controls.Add(nameBox, 0, 1);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        form.Controls.Add(root);
        form.AcceptButton = create;
        form.CancelButton = cancel;

        string? validatedName = null;
        create.Click += (_, _) =>
        {
            try
            {
                validatedName = ValidateNewProfileName(nameBox.Text, catalog);
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, ex.Message, "Tên profile không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                nameBox.Focus();
                nameBox.SelectAll();
            }
        };
        form.Shown += (_, _) => nameBox.Focus();
        return form.ShowDialog(this) == DialogResult.OK ? validatedName : null;
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
                DeleteDirectoryStrict(plan.Profile.Name, "Chrome profile", plan.ChromeProfilePath);
                RemoveManagedProfileContainerIfEmpty(plan.ChromeProfilePath);

                _profileService.RemoveFromCatalog(catalog, plan.Profile.Name);
                _profileService.SaveWithBackup(catalog);
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
        using var form = new Form
        {
            Text = title,
            Width = 460,
            Height = 600,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = "Chọn profile cần mở", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
        var search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Tìm profile...", Font = new Font("Segoe UI", 11F), Margin = new Padding(0, 0, 0, 10) };
        ModernDialog.StylePrimaryLabel(label);
        ModernDialog.StyleTextInput(search);
        var multiSelect = new CheckBox
        {
            Text = "Chọn nhiều profile",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 0, 0, 8)
        };
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var singleList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 11F),
            ItemHeight = 34,
            BorderStyle = BorderStyle.FixedSingle,
            HorizontalScrollbar = true,
            SelectionMode = SelectionMode.One,
            Margin = new Padding(0)
        };
        var multiList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            CheckOnClick = true,
            Font = new Font("Segoe UI", 11F),
            ItemHeight = 34,
            BorderStyle = BorderStyle.FixedSingle,
            HorizontalScrollbar = true,
            Margin = new Padding(0),
            Visible = false
        };
        ModernDialog.StyleSelectionList(singleList);
        ModernDialog.StyleSelectionList(multiList);
        listHost.Controls.Add(multiList);
        listHost.Controls.Add(singleList);

        var open = new Button { Text = "Mở profile", Size = new Size(132, 42), Font = new Font("Segoe UI", 10F, FontStyle.Bold), BackColor = Color.FromArgb(232, 242, 255), ForeColor = Color.FromArgb(35, 91, 152), FlatStyle = FlatStyle.Flat };
        open.FlatAppearance.BorderColor = Color.FromArgb(130, 173, 220);
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(104, 42), Font = new Font("Segoe UI", 10F), FlatStyle = FlatStyle.Flat };
        ModernDialog.StylePrimaryButton(open);
        ModernDialog.StyleSecondaryButton(cancel);
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        flow.Controls.Add(cancel);
        flow.Controls.Add(open);
        root.Controls.Add(label, 0, 0);
        root.Controls.Add(search, 0, 1);
        root.Controls.Add(multiSelect, 0, 2);
        root.Controls.Add(listHost, 0, 3);
        root.Controls.Add(flow, 0, 4);
        form.Controls.Add(root);
        form.AcceptButton = open;
        form.CancelButton = cancel;

        var checkedContexts = new HashSet<ProfileContext>();
        ProfileContext? preferredSingleContext = null;
        ProfileOpenSelection? selection = null;
        var rebuildingChecks = false;

        void CaptureVisibleChecks()
        {
            for (var index = 0; index < multiList.Items.Count; index++)
            {
                if (multiList.Items[index] is not OpenProfileListItem item) continue;
                if (multiList.GetItemChecked(index)) checkedContexts.Add(item.Context);
                else checkedContexts.Remove(item.Context);
            }
        }

        void ApplyFilter(bool captureVisibleChecks = true)
        {
            if (captureVisibleChecks) CaptureVisibleChecks();
            if (singleList.SelectedItem is OpenProfileListItem selectedItem)
                preferredSingleContext = selectedItem.Context;
            var keyword = search.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? allItems
                : allItems.Where(item => item.Context.Profile.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            singleList.BeginUpdate();
            multiList.BeginUpdate();
            try
            {
                singleList.Items.Clear();
                singleList.Items.AddRange(filtered.Cast<object>().ToArray());
                var selectedIndex = preferredSingleContext is null ? -1 : filtered.FindIndex(item => ReferenceEquals(item.Context, preferredSingleContext));
                singleList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : singleList.Items.Count > 0 ? 0 : -1;

                rebuildingChecks = true;
                multiList.Items.Clear();
                foreach (var item in filtered)
                {
                    var index = multiList.Items.Add(item);
                    multiList.SetItemChecked(index, checkedContexts.Contains(item.Context));
                }
            }
            finally
            {
                rebuildingChecks = false;
                multiList.EndUpdate();
                singleList.EndUpdate();
            }

            singleList.Visible = !multiSelect.Checked;
            multiList.Visible = multiSelect.Checked;
            if (multiSelect.Checked) multiList.BringToFront();
            else singleList.BringToFront();
            open.Enabled = multiSelect.Checked ? allItems.Count > 0 : singleList.SelectedIndex >= 0;
        }

        void OpenSelectedProfiles()
        {
            if (multiSelect.Checked)
            {
                CaptureVisibleChecks();
                if (checkedContexts.Count == 0)
                {
                    MessageBox.Show(form, "Vui lòng chọn ít nhất một profile.", "Mở profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                selection = new ProfileOpenSelection(
                    IsMultiple: true,
                    Contexts: allItems.Where(item => checkedContexts.Contains(item.Context)).Select(item => item.Context).ToList());
            }
            else
            {
                if (singleList.SelectedItem is not OpenProfileListItem item) return;
                selection = new ProfileOpenSelection(IsMultiple: false, Contexts: [item.Context]);
            }
            form.DialogResult = DialogResult.OK;
            form.Close();
        }

        search.TextChanged += (_, _) => ApplyFilter();
        singleList.SelectedIndexChanged += (_, _) =>
        {
            if (singleList.SelectedItem is OpenProfileListItem item) preferredSingleContext = item.Context;
            if (!multiSelect.Checked) open.Enabled = singleList.SelectedIndex >= 0;
        };
        multiList.ItemCheck += (_, e) =>
        {
            if (rebuildingChecks || e.Index < 0 || e.Index >= multiList.Items.Count || multiList.Items[e.Index] is not OpenProfileListItem item) return;
            if (e.NewValue == CheckState.Checked) checkedContexts.Add(item.Context);
            else checkedContexts.Remove(item.Context);
        };
        multiSelect.CheckedChanged += (_, _) =>
        {
            if (multiSelect.Checked && singleList.SelectedItem is OpenProfileListItem item)
                checkedContexts.Add(item.Context);
            if (!multiSelect.Checked)
            {
                CaptureVisibleChecks();
                preferredSingleContext = allItems.FirstOrDefault(item => checkedContexts.Contains(item.Context))?.Context ?? preferredSingleContext;
            }
            ApplyFilter(captureVisibleChecks: false);
            if (multiSelect.Checked) multiList.Focus();
            else singleList.Focus();
        };
        form.Shown += (_, _) => singleList.Focus();

        void HandleListKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenSelectedProfiles();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            }
        }

        singleList.KeyDown += (_, e) => HandleListKeyDown(e);
        multiList.KeyDown += (_, e) => HandleListKeyDown(e);
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                var activeList = multiSelect.Checked ? (ListBox)multiList : singleList;
                if (activeList.Items.Count > 0)
                {
                    activeList.Focus();
                    var next = Math.Clamp(activeList.SelectedIndex + (e.KeyCode == Keys.Up ? -1 : 1), 0, activeList.Items.Count - 1);
                    activeList.SelectedIndex = next;
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenSelectedProfiles();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            }
        };
        singleList.MouseDoubleClick += (_, e) =>
        {
            if (singleList.IndexFromPoint(e.Location) == ListBox.NoMatches) return;
            OpenSelectedProfiles();
        };
        open.Click += (_, _) => OpenSelectedProfiles();
        ApplyFilter(captureVisibleChecks: false);
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
        Text = selected is null ? "Tool TikTok Manager V13.4.1 — VM Optimized Multi Worker" : $"Tool TikTok Manager V13.4.1 — {selected.Profile.Name}";
        RefreshSelectedProfilePresentation();
    }

    void RefreshSelectedProfilePresentation()
    {
        TryGetSelectedTabPage(out var selectedPage);
        foreach (var context in _contexts.Values)
        {
            var header = context.ProfileHeader;
            if (header is null || header.IsDisposed) continue;

            var active = ReferenceEquals(context.Tab, selectedPage);
            header.Text = active
                ? $"ĐANG CHỌN: {context.Profile.Name} | CDP {context.Profile.CdpPort}"
                : $"{context.Profile.Name} | CDP {context.Profile.CdpPort}";
            header.BackColor = active ? ActiveProfileColor : UiTheme.Card;
            header.ForeColor = active ? Color.White : Color.FromArgb(42, 57, 76);
        }
        _tabs.Invalidate();
    }

    async Task CloseChromeForProfileAsync(ProfileContext selected)
    {
        SetStatus(selected, "Đang đóng Chrome theo profilePath/CDP/PID đã xác minh...", Color.DarkOrange);
        var result = await SendCloseChromeCommandAsync(selected);

        switch (result)
        {
            case "closed":
                SetStatus(selected, "Đã đóng Chrome của đúng profile.", Color.DarkGreen);
                _log.Info($"[CHROME_CLOSE] profile={selected.Profile.Name} result=closed profilePath={selected.Profile.ProfilePath} port={selected.Profile.CdpPort}");
                break;
            case "not_running":
                SetStatus(selected, "Chrome của profile này chưa chạy.", Color.DimGray);
                _log.Info($"[CHROME_CLOSE] profile={selected.Profile.Name} result=not_running profilePath={selected.Profile.ProfilePath} port={selected.Profile.CdpPort}");
                break;
            case "automation_running":
                SetStatus(selected, "Hãy dừng automation trước khi đóng Chrome.", Color.DarkOrange);
                _log.Warn($"[CHROME_CLOSE] profile={selected.Profile.Name} result=automation_running");
                break;
            default:
                SetStatus(selected, "Không thể xác nhận Chrome đã đóng hoàn toàn.", Color.Firebrick);
                _log.Warn($"[CHROME_CLOSE] profile={selected.Profile.Name} result={result} profilePath={selected.Profile.ProfilePath} port={selected.Profile.CdpPort}");
                break;
        }

        try { await RefreshStatusAsync(selected); } catch (Exception ex) { _log.Warn($"[{selected.Profile.Name}] refresh status sau close Chrome: {ex.Message}"); }
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
            if (!_chromeMonitor.Visible) _chromeMonitor.Show(this);
            if (_chromeMonitor.WindowState == FormWindowState.Minimized) _chromeMonitor.WindowState = FormWindowState.Normal;
            _chromeMonitor.BringToFront();
            _chromeMonitor.Activate();
            return;
        }

        _chromeMonitor = new ChromeMonitorForm(GetChromeMonitorProfiles, ActivateChromeFromMonitorAsync);
        _chromeMonitor.FormClosed += (_, _) => _chromeMonitor = null;
        _chromeMonitor.Show(this);
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
                    snapshot?.RunState ?? "STOPPED",
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
        try
        {
            if (ctx.Worker is null || ctx.Worker.HasExited)
                await EnsureWorkerAsync(ctx);

            try { await RefreshStatusAsync(ctx); } catch { }
            var hwndValue = ctx.LastSnapshot?.ChromeWindowHandle ?? 0;
            if (hwndValue <= 0)
            {
                await OpenChromeForProfileAsync(ctx);
                await Task.Delay(300);
                await RefreshStatusAsync(ctx);
                hwndValue = ctx.LastSnapshot?.ChromeWindowHandle ?? 0;
            }

            if (hwndValue <= 0 || !ChromeMonitorWindowActions.RestoreMaximizeAndActivate(new IntPtr(hwndValue)))
                throw new InvalidOperationException($"Không tìm thấy cửa sổ Chrome của profile “{profileName}” để phóng to.");
        }
        catch (Exception ex)
        {
            _log.Warn($"[CHROME_MONITOR_ACTIVATE] profile={profileName} failed={ex.Message}");
            ShowError(ex);
        }
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
        public bool F5Enabled { get; set; }
        public int F5RemainingSec { get; set; } = -1;
    }
}
