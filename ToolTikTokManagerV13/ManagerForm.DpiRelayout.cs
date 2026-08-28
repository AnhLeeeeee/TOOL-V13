namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _monitorRelayoutPending;
    bool _monitorRelayoutHooksInitialized;
    bool _toolbarLayoutPending;
    TableLayoutPanel? _mainToolbarHost;
    FlowLayoutPanel? _mainToolbarRow1;
    FlowLayoutPanel? _mainToolbarRow2;

    void InitializeMonitorRelayoutHooks()
    {
        if (_monitorRelayoutHooksInitialized) return;
        _monitorRelayoutHooksInitialized = true;

        // Dùng event thay vì override OnShown vì ManagerForm.WorkerAdoption.cs
        // đã override OnShown để nhận lại Worker cũ khi Manager khởi động.
        DpiChanged += (_, _) => ScheduleMonitorRelayout("dpi_changed");
        ResizeEnd += (_, _) => ScheduleMonitorRelayout("resize_end");
        Shown += (_, _) =>
        {
            FitInitialWindowToWorkingArea();
            ScheduleMonitorRelayout("shown");
        };
    }

    void RegisterResponsiveToolbar(
        TableLayoutPanel toolbarHost,
        FlowLayoutPanel toolbarRow1,
        FlowLayoutPanel toolbarRow2)
    {
        _mainToolbarHost = toolbarHost;
        _mainToolbarRow1 = toolbarRow1;
        _mainToolbarRow2 = toolbarRow2;

        // Khi chiều rộng cửa sổ đổi, xác định lại hàng nào cần scrollbar và
        // chừa đúng chiều cao cho scrollbar thay vì để nó đè lên nút.
        toolbarHost.SizeChanged += (_, _) => ScheduleToolbarLayout();
        toolbarRow1.ControlAdded += (_, _) => ScheduleToolbarLayout();
        toolbarRow1.ControlRemoved += (_, _) => ScheduleToolbarLayout();
        toolbarRow2.ControlAdded += (_, _) => ScheduleToolbarLayout();
        toolbarRow2.ControlRemoved += (_, _) => ScheduleToolbarLayout();
    }

    void ScheduleToolbarLayout()
    {
        if (_toolbarLayoutPending || IsDisposed || Disposing || !IsHandleCreated)
            return;

        _toolbarLayoutPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _toolbarLayoutPending = false;
                if (IsDisposed || Disposing) return;
                UpdateMainToolbarLayout();
            }));
        }
        catch (InvalidOperationException)
        {
            _toolbarLayoutPending = false;
        }
    }

    void UpdateMainToolbarLayout()
    {
        var host = _mainToolbarHost;
        var row1 = _mainToolbarRow1;
        var row2 = _mainToolbarRow2;
        if (host is null || row1 is null || row2 is null
            || host.IsDisposed || row1.IsDisposed || row2.IsDisposed)
        {
            return;
        }

        var availableWidth = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal);
        var scrollbarHeight = Math.Max(
            SystemInformation.HorizontalScrollBarHeight,
            (int)Math.Ceiling(17d * Math.Max(96, DeviceDpi) / 96d));

        static int ContentWidth(FlowLayoutPanel row)
        {
            var width = row.Padding.Horizontal;
            foreach (Control control in row.Controls)
            {
                if (!control.Visible) continue;
                width += control.Width + control.Margin.Horizontal;
            }
            return Math.Max(1, width);
        }

        static int ContentHeight(FlowLayoutPanel row)
        {
            var height = 0;
            foreach (Control control in row.Controls)
            {
                if (!control.Visible) continue;
                height = Math.Max(height, control.Height + control.Margin.Vertical);
            }
            return Math.Max(40, height + row.Padding.Vertical);
        }

        int RowHeight(FlowLayoutPanel row)
        {
            var contentWidth = ContentWidth(row);
            var needsHorizontalScroll = contentWidth > availableWidth;

            // AutoScrollMinSize buộc WinForms tạo scrollbar đúng lúc nhưng chiều
            // cao của TableLayoutPanel đã được chừa trước nên scrollbar không che nút.
            row.AutoScrollMinSize = new Size(contentWidth, 0);
            return ContentHeight(row) + (needsHorizontalScroll ? scrollbarHeight + 2 : 0);
        }

        var row1Height = RowHeight(row1);
        var row2Height = RowHeight(row2);

        host.SuspendLayout();
        try
        {
            host.RowStyles[0].SizeType = SizeType.Absolute;
            host.RowStyles[0].Height = row1Height;
            host.RowStyles[1].SizeType = SizeType.Absolute;
            host.RowStyles[1].Height = row2Height;
            host.Height = host.Padding.Vertical + row1Height + row2Height;
            row1.Height = row1Height;
            row2.Height = row2Height;
        }
        finally
        {
            host.ResumeLayout(true);
        }
    }

    void FitInitialWindowToWorkingArea()
    {
        if (WindowState != FormWindowState.Normal || IsDisposed || Disposing)
            return;

        var work = Screen.FromControl(this).WorkingArea;
        if (work.Width <= 0 || work.Height <= 0) return;

        const int edgeMargin = 12;
        var maxWidth = Math.Max(640, work.Width - edgeMargin * 2);
        var maxHeight = Math.Max(480, work.Height - edgeMargin * 2);

        // MinimumSize cũ 1120x720 có thể lớn hơn vùng làm việc logic của màn
        // 125%/150%, khiến Windows mở form trong trạng thái bị cắt. Cho phép
        // Manager thu nhỏ vừa màn; toolbar đã có cuộn ngang nên vẫn thao tác đủ.
        MinimumSize = new Size(
            Math.Min(980, maxWidth),
            Math.Min(620, maxHeight));

        var targetWidth = Math.Min(Width, maxWidth);
        var targetHeight = Math.Min(Height, maxHeight);
        Size = new Size(targetWidth, targetHeight);

        var left = Math.Clamp(
            Left,
            work.Left,
            Math.Max(work.Left, work.Right - Width));
        var top = Math.Clamp(
            Top,
            work.Top,
            Math.Max(work.Top, work.Bottom - Height));
        Location = new Point(left, top);
    }

    void ScheduleMonitorRelayout(string reason)
    {
        if (_monitorRelayoutPending || IsDisposed || Disposing || !IsHandleCreated)
            return;

        _monitorRelayoutPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _monitorRelayoutPending = false;
                if (IsDisposed || Disposing) return;

                RelayoutManagerForCurrentMonitor(reason);

                // Nhịp 2 chạy sau khi Dock/AutoScale của WinForms đã ổn định.
                // Đây là bước quan trọng với Worker là HWND của process khác.
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        UpdateMainToolbarLayout();
                        ResizeEmbeddedWorkersForCurrentHosts(reason + "_settled");
                        Invalidate(true);
                    }));
                }
                catch (InvalidOperationException) { }
            }));
        }
        catch (InvalidOperationException)
        {
            _monitorRelayoutPending = false;
        }
    }

    void RelayoutManagerForCurrentMonitor(string reason)
    {
        SuspendLayout();
        try
        {
            UpdateMainToolbarLayout();
            PerformLayout();
            PerformLayoutRecursive(this);

            if (!_tabs.IsDisposed)
                _tabs.PerformLayout();

            foreach (var ctx in _contexts.Values.ToList())
            {
                if (ctx.Tab is not null && !ctx.Tab.IsDisposed)
                    ctx.Tab.PerformLayout();

                if (ctx.Host is not null && !ctx.Host.IsDisposed)
                    ctx.Host.PerformLayout();
            }
        }
        finally
        {
            ResumeLayout(true);
        }

        ResizeEmbeddedWorkersForCurrentHosts(reason);
        Invalidate(true);

        try
        {
            _log.Info($"[MONITOR_RELAYOUT] reason={reason} dpi={DeviceDpi} size={ClientSize.Width}x{ClientSize.Height}");
        }
        catch { }
    }

    static void PerformLayoutRecursive(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child.IsDisposed) continue;
            child.PerformLayout();
            if (child.HasChildren)
                PerformLayoutRecursive(child);
        }
    }

    void ResizeEmbeddedWorkersForCurrentHosts(string reason)
    {
        foreach (var ctx in _contexts.Values.ToList())
        {
            var host = ctx.Host;
            if (ctx.Detached
                || host is null
                || host.IsDisposed
                || !host.IsHandleCreated
                || !WorkerWindowEmbedder.IsValid(ctx.WorkerWindow))
            {
                continue;
            }

            WorkerWindowEmbedder.Resize(ctx.WorkerWindow, host.ClientSize);
        }
    }
}
