using ToolTikTokV12.Controls;
using System.Runtime.CompilerServices;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _pinnedOverviewTabInstalled;
    Button? _pinnedOverviewTabButton;
    int _pinnedOverviewTabWidth = 132;
    int _pinnedOverviewTabHeight = 30;
    int _pinnedOverviewTabLeftInset = 2;
    int _pinnedOverviewTabTopInset;

    // Patch additive: ghim Tổng quan cố định bên trái mà không thay đổi
    // collection TabPage/profile tab hiện có. Khi native TabControl cuộn vì
    // mở nhiều PRF, chỉ các tab PRF phía dưới có thể bị khuất; nút Tổng quan
    // overlay vẫn luôn hiển thị và chọn đúng _dashboardTab cũ.
    [ModuleInitializer]
    internal static void BootstrapPinnedOverviewTabPatch()
    {
        EventHandler? idleHandler = null;
        idleHandler = (_, _) =>
        {
            foreach (Form openForm in Application.OpenForms)
            {
                if (openForm is not ManagerForm manager) continue;
                manager.InstallPinnedOverviewTabPatch();
                if (idleHandler is not null)
                    Application.Idle -= idleHandler;
                break;
            }
        };
        Application.Idle += idleHandler;
    }

    void InstallPinnedOverviewTabPatch()
    {
        if (_pinnedOverviewTabInstalled || IsDisposed || Disposing) return;
        _pinnedOverviewTabInstalled = true;

        // Dashboard đã được InitializeDashboardAndUpdater() tạo trong constructor.
        // Nếu có thay đổi thứ tự init ở bản sau thì vẫn bảo đảm page tồn tại.
        EnsureDashboardTab();
        CapturePinnedOverviewTabMetrics();

        var pinned = new Button
        {
            Text = "📊 Tổng quan",
            AutoSize = false,
            TabStop = false,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        pinned.FlatAppearance.BorderSize = 1;
        pinned.Click += (_, _) =>
        {
            try
            {
                EnsureDashboardTab();
                if (_dashboardTab is not null && !_dashboardTab.IsDisposed && _dashboardTab.Parent == _tabs)
                    SelectTabPageSafely(_dashboardTab);
            }
            catch { }
            UpdatePinnedOverviewTabStyle();
            LayoutPinnedOverviewTab();
        };

        _pinnedOverviewTabButton = pinned;
        Controls.Add(pinned);
        pinned.BringToFront();

        _tabs.SelectedIndexChanged += (_, _) => UpdatePinnedOverviewTabStyle();
        _tabs.SizeChanged += (_, _) => LayoutPinnedOverviewTab();
        _tabs.LocationChanged += (_, _) => LayoutPinnedOverviewTab();
        _tabs.HandleCreated += (_, _) => BeginInvoke(new Action(() =>
        {
            CapturePinnedOverviewTabMetrics();
            LayoutPinnedOverviewTab();
        }));
        Resize += (_, _) => LayoutPinnedOverviewTab();
        Layout += (_, _) => LayoutPinnedOverviewTab();
        DpiChanged += (_, _) => BeginInvoke(new Action(() =>
        {
            CapturePinnedOverviewTabMetrics();
            LayoutPinnedOverviewTab();
        }));
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            CapturePinnedOverviewTabMetrics();
            LayoutPinnedOverviewTab();
            UpdatePinnedOverviewTabStyle();
        }));

        LayoutPinnedOverviewTab();
        UpdatePinnedOverviewTabStyle();
    }

    void CapturePinnedOverviewTabMetrics()
    {
        if (_tabs.IsDisposed || _tabs.Disposing || !_tabs.IsHandleCreated) return;
        if (_dashboardTab is null || _dashboardTab.IsDisposed || _dashboardTab.Parent != _tabs) return;

        try
        {
            var index = _tabs.TabPages.IndexOf(_dashboardTab);
            if (index < 0) return;
            var rect = _tabs.GetTabRect(index);

            if (rect.Width >= 70 && rect.Width <= 320)
                _pinnedOverviewTabWidth = rect.Width;
            if (rect.Height >= 20 && rect.Height <= 80)
                _pinnedOverviewTabHeight = rect.Height;

            // Chỉ lấy inset khi tab dashboard đang ở vị trí đầu hợp lệ.
            // Khi native TabControl đã cuộn, rect.X có thể âm: không dùng giá trị đó.
            if (rect.X >= 0 && rect.X <= 20)
                _pinnedOverviewTabLeftInset = rect.X;
            if (rect.Y >= 0 && rect.Y <= 30)
                _pinnedOverviewTabTopInset = rect.Y;
        }
        catch { }
    }

    void LayoutPinnedOverviewTab()
    {
        var pinned = _pinnedOverviewTabButton;
        if (pinned is null || pinned.IsDisposed || IsDisposed || Disposing) return;
        if (_tabs.IsDisposed || _tabs.Disposing || !_tabs.IsHandleCreated) return;

        try
        {
            // Tọa độ luôn quy đổi từ chính TabControl sang Form hiện tại.
            // Không dùng Screen.PrimaryScreen nên đúng cả màn hình 1/2 và DPI khác nhau.
            var tabsOriginOnForm = PointToClient(_tabs.PointToScreen(Point.Empty));
            var left = tabsOriginOnForm.X + Math.Max(0, _pinnedOverviewTabLeftInset);
            var top = tabsOriginOnForm.Y + Math.Max(0, _pinnedOverviewTabTopInset);

            pinned.SetBounds(
                left,
                top,
                Math.Max(110, _pinnedOverviewTabWidth),
                Math.Max(24, _pinnedOverviewTabHeight));
            pinned.BringToFront();
        }
        catch { }
    }

    void UpdatePinnedOverviewTabStyle()
    {
        var pinned = _pinnedOverviewTabButton;
        if (pinned is null || pinned.IsDisposed) return;

        var active = false;
        try
        {
            active = _dashboardTab is not null
                && !_dashboardTab.IsDisposed
                && ReferenceEquals(_tabs.SelectedTab, _dashboardTab);
        }
        catch { }

        pinned.BackColor = active ? ActiveProfileColor : InactiveTabColor;
        pinned.ForeColor = active ? Color.White : SystemColors.ControlText;
        pinned.FlatAppearance.BorderColor = active ? ActiveProfileColor : UiTheme.Border;
        pinned.Text = active ? "● Tổng quan" : "📊 Tổng quan";
    }
}
