using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

internal sealed record ChromeMonitorProfileInfo(
    string Name,
    string RunState,
    string ChromeState,
    long ChromeWindowHandle,
    int Viewer,
    int Step,
    long Rounds,
    int F5RemainingSec,
    string Detail,
    DateTime LastRefreshUtc);

internal static class ChromeMonitorWindowActions
{
    const int SW_RESTORE = 9;
    const int SW_MAXIMIZE = 3;

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    public static bool RestoreMaximizeAndActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        ShowWindowAsync(hwnd, SW_RESTORE);
        ShowWindowAsync(hwnd, SW_MAXIMIZE);
        SetForegroundWindow(hwnd);
        return true;
    }
}

/// <summary>
/// Cửa sổ chỉ-để-xem. DWM thumbnail được composited trực tiếp lên top-level HWND
/// của form, không SetParent/resize Chrome thật nên không làm thay đổi viewport/XPath.
/// </summary>
internal sealed class ChromeMonitorForm : Form
{
    sealed class TileState
    {
        public required ChromeMonitorProfileInfo Info { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle PreviewBounds { get; set; }
        public IntPtr SourceHwnd { get; set; }
        public IntPtr Thumbnail { get; set; }
    }

    readonly Func<IReadOnlyList<ChromeMonitorProfileInfo>> _getProfiles;
    readonly Func<string, Task> _activateChrome;
    readonly ComboBox _pageSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72 };
    readonly Label _pageLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(8, 9, 8, 0) };
    readonly Button _prev = new() { Text = "◀", Width = 38, Height = 30 };
    readonly Button _next = new() { Text = "▶", Width = 38, Height = 30 };
    readonly Label _hint = new() { AutoSize = true, Text = "Double-click: phóng to Chrome  |  F8: ẩn/hiện giám sát", Margin = new Padding(12, 9, 4, 0) };
    readonly FlowLayoutPanel _toolbar;
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 700 };
    readonly List<TileState> _tiles = [];
    readonly Font _headerFont = new("Segoe UI Semibold", 10F);
    readonly Font _footerFont = new("Segoe UI", 9F);
    readonly Font _metricValueFont = new("Segoe UI Semibold", 9F);
    static readonly Color TextPrimary = Color.FromArgb(36, 49, 66);
    static readonly Color TextMuted = Color.FromArgb(88, 105, 126);
    static readonly Color SoftBlue = Color.FromArgb(232, 242, 255);
    static readonly Color SoftBlue2 = Color.FromArgb(247, 250, 254);
    static readonly Color BlueBorder = Color.FromArgb(174, 201, 231);
    int _pageIndex;
    bool _suspended;

    public ChromeMonitorForm(Func<IReadOnlyList<ChromeMonitorProfileInfo>> getProfiles, Func<string, Task> activateChrome)
    {
        _getProfiles = getProfiles;
        _activateChrome = activateChrome;

        Text = "Giám sát Chrome — V13.4.1";
        Width = 1240;
        Height = 820;
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Canvas;
        Font = new Font("Segoe UI", 9F);
        DoubleBuffered = true;

        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(12, 8, 12, 5),
            WrapContents = false,
            BackColor = UiTheme.Card
        };
        var tileCountLabel = new Label
        {
            Text = "Số ô:",
            AutoSize = true,
            Margin = new Padding(0, 9, 6, 0),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI Semibold", 9F)
        };
        _toolbar.Controls.Add(tileCountLabel);

        _pageSize.Items.AddRange(["4", "6", "9"]);
        _pageSize.SelectedIndex = 0;
        _pageSize.BackColor = Color.White;
        _pageSize.ForeColor = TextPrimary;
        _pageSize.FlatStyle = FlatStyle.Flat;
        _toolbar.Controls.Add(_pageSize);

        StyleToolbarButton(_prev);
        StyleToolbarButton(_next);
        _pageLabel.ForeColor = UiTheme.Primary;
        _pageLabel.Font = new Font("Segoe UI Semibold", 9F);
        _hint.ForeColor = Color.FromArgb(75, 100, 132);

        _toolbar.Controls.Add(_prev);
        _toolbar.Controls.Add(_pageLabel);
        _toolbar.Controls.Add(_next);
        _toolbar.Controls.Add(_hint);
        _toolbar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, _toolbar.ClientSize.Height - 1, _toolbar.ClientSize.Width, _toolbar.ClientSize.Height - 1);
        };
        Controls.Add(_toolbar);

        _pageSize.SelectedIndexChanged += (_, _) => { _pageIndex = 0; RefreshModel(rebuildThumbnails: true); };
        _prev.Click += (_, _) => { if (_pageIndex > 0) { _pageIndex--; RefreshModel(rebuildThumbnails: true); } };
        _next.Click += (_, _) =>
        {
            var pages = GetPageCount();
            if (_pageIndex + 1 < pages) { _pageIndex++; RefreshModel(rebuildThumbnails: true); }
        };
        _statusTimer.Tick += (_, _) => RefreshModel(rebuildThumbnails: false);
        Shown += (_, _) => { RefreshModel(rebuildThumbnails: true); _statusTimer.Start(); };
        VisibleChanged += (_, _) =>
        {
            if (!Visible)
            {
                _statusTimer.Stop();
                ClearThumbnails();
            }
            else if (!_suspended && IsHandleCreated)
            {
                RefreshModel(rebuildThumbnails: true);
                _statusTimer.Start();
            }
        };
        Resize += (_, _) => HandleResize();
        MouseDoubleClick += async (_, e) => await HandleDoubleClickAsync(e.Location);
        FormClosed += (_, _) =>
        {
            _statusTimer.Stop();
            ClearThumbnails();
            _headerFont.Dispose();
            _footerFont.Dispose();
            _metricValueFont.Dispose();
        };
    }

    int PageSize => int.TryParse(_pageSize.SelectedItem?.ToString(), out var value) ? value : 4;

    int GetPageCount()
    {
        var count = _getProfiles().Count;
        return Math.Max(1, (int)Math.Ceiling(count / (double)PageSize));
    }

    void HandleResize()
    {
        var nowSuspended = WindowState == FormWindowState.Minimized;
        if (nowSuspended != _suspended)
        {
            _suspended = nowSuspended;
            if (_suspended) ClearThumbnails();
            else RefreshModel(rebuildThumbnails: true);
            _statusTimer.Interval = _suspended ? 2500 : 700;
        }
        if (!_suspended)
        {
            LayoutTiles();
            UpdateAllThumbnails();
            Invalidate();
        }
    }

    void RefreshModel(bool rebuildThumbnails)
    {
        if (_suspended || IsDisposed || Disposing || !IsHandleCreated) return;
        var all = _getProfiles();
        var pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)PageSize));
        if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
        var page = all.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
        var namesChanged = page.Count != _tiles.Count || page.Where((x, i) => i >= _tiles.Count || !_tiles[i].Info.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase)).Any();

        if (namesChanged || rebuildThumbnails)
        {
            ClearThumbnails();
            _tiles.Clear();
            foreach (var info in page) _tiles.Add(new TileState { Info = info });
        }
        else
        {
            for (var i = 0; i < page.Count; i++) _tiles[i].Info = page[i];
        }

        _pageLabel.Text = all.Count == 0 ? "Chưa có profile đang mở" : $"Nhóm {_pageIndex + 1}/{pageCount}";
        _prev.Enabled = _pageIndex > 0;
        _next.Enabled = _pageIndex + 1 < pageCount;

        LayoutTiles();
        SyncThumbnails();
        Invalidate();
    }

    void LayoutTiles()
    {
        var pageSize = PageSize;
        var columns = pageSize switch { 4 => 2, 6 => 3, _ => 3 };
        var rows = pageSize switch { 4 => 2, 6 => 2, _ => 3 };
        var left = 12;
        var top = _toolbar.Bottom + 10;
        var width = Math.Max(100, ClientSize.Width - 24);
        var height = Math.Max(100, ClientSize.Height - top - 12);
        const int gap = 12;
        var tileWidth = Math.Max(100, (width - gap * (columns - 1)) / columns);
        var tileHeight = Math.Max(100, (height - gap * (rows - 1)) / rows);

        for (var i = 0; i < _tiles.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var bounds = new Rectangle(left + col * (tileWidth + gap), top + row * (tileHeight + gap), tileWidth, tileHeight);
            _tiles[i].Bounds = bounds;
            _tiles[i].PreviewBounds = new Rectangle(bounds.Left + 2, bounds.Top + 39, Math.Max(10, bounds.Width - 4), Math.Max(10, bounds.Height - 88));
        }
    }

    void SyncThumbnails()
    {
        foreach (var tile in _tiles)
        {
            var source = tile.Info.ChromeWindowHandle > 0 ? new IntPtr(tile.Info.ChromeWindowHandle) : IntPtr.Zero;
            if (tile.SourceHwnd != source || (tile.Thumbnail == IntPtr.Zero && source != IntPtr.Zero))
            {
                UnregisterThumbnail(tile);
                tile.SourceHwnd = source;
                if (source != IntPtr.Zero && DwmNative.IsWindow(source))
                {
                    var hr = DwmNative.DwmRegisterThumbnail(Handle, source, out var thumbnail);
                    if (hr == 0) tile.Thumbnail = thumbnail;
                }
            }
        }
        UpdateAllThumbnails();
    }

    void UpdateAllThumbnails()
    {
        if (_suspended || !IsHandleCreated) return;
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            var r = tile.PreviewBounds;
            var props = new DwmNative.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DwmNative.DWM_TNP_RECTDESTINATION | DwmNative.DWM_TNP_VISIBLE | DwmNative.DWM_TNP_OPACITY | DwmNative.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new DwmNative.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false
            };
            DwmNative.DwmUpdateThumbnailProperties(tile.Thumbnail, ref props);
        }
    }

    async Task HandleDoubleClickAsync(Point location)
    {
        var tile = _tiles.FirstOrDefault(t => t.Bounds.Contains(location));
        if (tile is null) return;
        try { await _activateChrome(tile.Info.Name); }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        foreach (var tile in _tiles) DrawTile(e.Graphics, tile);
    }

    void DrawTile(Graphics g, TileState tile)
    {
        var info = tile.Info;
        var stateColor = info.RunState switch
        {
            "RUNNING" => Color.FromArgb(41, 145, 82),
            "PAUSED" => Color.FromArgb(210, 137, 38),
            _ when info.ChromeState == "DISCONNECTED" => Color.FromArgb(188, 68, 68),
            _ => Color.FromArgb(122, 135, 151)
        };
        var stateBack = info.RunState switch
        {
            "RUNNING" => Color.FromArgb(232, 247, 237),
            "PAUSED" => Color.FromArgb(255, 246, 229),
            _ when info.ChromeState == "DISCONNECTED" => Color.FromArgb(255, 238, 238),
            _ => Color.FromArgb(241, 244, 248)
        };

        var bounds = tile.Bounds;
        var headerRect = new Rectangle(bounds.Left + 1, bounds.Top + 4, Math.Max(1, bounds.Width - 2), 34);
        var footerRect = new Rectangle(bounds.Left + 1, tile.PreviewBounds.Bottom, Math.Max(1, bounds.Width - 2), Math.Max(1, bounds.Bottom - tile.PreviewBounds.Bottom - 1));

        using var cardBrush = new SolidBrush(UiTheme.Card);
        using var headerBrush = new SolidBrush(SoftBlue);
        using var footerBrush = new SolidBrush(SoftBlue2);
        using var borderPen = new Pen(BlueBorder, 1F);
        using var statePen = new Pen(stateColor, 4F);
        using var primary = new SolidBrush(UiTheme.Primary);
        using var previewBackground = new SolidBrush(Color.FromArgb(28, 32, 38));

        g.FillRectangle(cardBrush, bounds);
        g.FillRectangle(headerBrush, headerRect);
        g.FillRectangle(footerBrush, footerRect);
        g.DrawRectangle(borderPen, bounds.X, bounds.Y, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        g.DrawLine(statePen, bounds.Left + 2, bounds.Top + 2, bounds.Right - 3, bounds.Top + 2);

        var dotColor = info.ChromeState == "CONNECTED" ? stateColor : Color.FromArgb(158, 168, 180);
        using (var dotBrush = new SolidBrush(dotColor))
            g.FillEllipse(dotBrush, bounds.Left + 10, bounds.Top + 15, 7, 7);

        g.DrawString(info.Name, _headerFont, primary, bounds.Left + 23, bounds.Top + 9);

        var badgeText = info.RunState;
        var badgeTextSize = g.MeasureString(badgeText, _footerFont);
        var badgeRect = new RectangleF(
            bounds.Right - badgeTextSize.Width - 24,
            bounds.Top + 8,
            badgeTextSize.Width + 14,
            22);
        using (var badgeBack = new SolidBrush(stateBack))
            FillRoundedRectangle(g, badgeBack, badgeRect, 8F);
        using (var badgeFore = new SolidBrush(stateColor))
            g.DrawString(badgeText, _footerFont, badgeFore, badgeRect.Left + 7, badgeRect.Top + 3);

        g.FillRectangle(previewBackground, tile.PreviewBounds);
        using (var previewPen = new Pen(Color.FromArgb(207, 217, 229)))
            g.DrawRectangle(previewPen, tile.PreviewBounds.X, tile.PreviewBounds.Y, Math.Max(1, tile.PreviewBounds.Width - 1), Math.Max(1, tile.PreviewBounds.Height - 1));

        if (tile.Thumbnail == IntPtr.Zero)
        {
            var message = info.ChromeState == "CONNECTED" ? "Đang lấy DWM preview..." : "Chrome chưa mở / chưa kết nối";
            var size = g.MeasureString(message, _footerFont);
            g.DrawString(message, _footerFont, Brushes.Gainsboro,
                tile.PreviewBounds.Left + Math.Max(4, (tile.PreviewBounds.Width - size.Width) / 2),
                tile.PreviewBounds.Top + Math.Max(4, (tile.PreviewBounds.Height - size.Height) / 2));
        }

        var viewer = info.Viewer >= 0 ? info.Viewer.ToString("N0") : "—";
        var step = info.Step is >= 1 and <= 8 ? $"{info.Step}/8" : "—";
        var f5 = info.F5RemainingSec >= 0 ? FormatSeconds(info.F5RemainingSec) : "—";
        var footerTop = tile.PreviewBounds.Bottom + 8;
        var columnWidth = Math.Max(80, (bounds.Width - 24) / 2);

        DrawMetric(g, "Viewer", viewer, bounds.Left + 10, footerTop, columnWidth);
        DrawMetric(g, "Step", step, bounds.Left + 12 + columnWidth, footerTop, columnWidth - 2);
        DrawMetric(g, "F5", f5, bounds.Left + 10, footerTop + 20, columnWidth);
        DrawMetric(g, "Vòng", info.Rounds.ToString("N0"), bounds.Left + 12 + columnWidth, footerTop + 20, columnWidth - 2);
    }

    void DrawMetric(Graphics g, string label, string value, int x, int y, int width)
    {
        using var muted = new SolidBrush(TextMuted);
        using var primary = new SolidBrush(UiTheme.Primary);
        g.DrawString($"{label}:", _footerFont, muted, x, y);
        var labelWidth = g.MeasureString($"{label}:", _footerFont).Width;
        var valueX = x + (int)Math.Ceiling(labelWidth) + 4;
        var state = g.Save();
        g.SetClip(new Rectangle(x, y, Math.Max(1, width), 18), CombineMode.Intersect);
        g.DrawString(value, _metricValueFont, primary, valueX, y);
        g.Restore(state);
    }

    static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        var diameter = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    static void StyleToolbarButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = BlueBorder;
        button.BackColor = SoftBlue;
        button.ForeColor = UiTheme.Primary;
        button.Font = new Font("Segoe UI Semibold", 9F);
        button.Margin = new Padding(5, 0, 5, 0);
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(219, 235, 253);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(205, 226, 249);
    }

    static string FormatSeconds(int seconds)
    {
        seconds = Math.Max(0, seconds);
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    void ClearThumbnails()
    {
        foreach (var tile in _tiles) UnregisterThumbnail(tile);
    }

    static void UnregisterThumbnail(TileState tile)
    {
        if (tile.Thumbnail == IntPtr.Zero) return;
        try { DwmNative.DwmUnregisterThumbnail(tile.Thumbnail); } catch { }
        tile.Thumbnail = IntPtr.Zero;
    }

    static class DwmNative
    {
        public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
        public const uint DWM_TNP_OPACITY = 0x00000004;
        public const uint DWM_TNP_VISIBLE = 0x00000008;
        public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr source, out IntPtr thumbnail);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
    }
}
