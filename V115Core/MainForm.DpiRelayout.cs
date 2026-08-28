namespace ToolTikTokV11;

public sealed partial class MainForm
{
    bool _dpiRelayoutPending;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ScheduleDpiRelayout();
    }

    void ScheduleDpiRelayout()
    {
        if (_dpiRelayoutPending || IsDisposed || Disposing || !IsHandleCreated)
            return;

        _dpiRelayoutPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _dpiRelayoutPending = false;
                if (IsDisposed || Disposing) return;

                SuspendLayout();
                try
                {
                    PerformLayout();
                    PerformWorkerLayoutRecursive(this);
                }
                finally
                {
                    ResumeLayout(true);
                }

                Invalidate(true);
                try
                {
                    _log.Info($"[WORKER_DPI_RELAYOUT] dpi={DeviceDpi} size={ClientSize.Width}x{ClientSize.Height}");
                }
                catch { }
            }));
        }
        catch (InvalidOperationException)
        {
            _dpiRelayoutPending = false;
        }
    }

    static void PerformWorkerLayoutRecursive(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child.IsDisposed) continue;
            child.PerformLayout();
            if (child.HasChildren)
                PerformWorkerLayoutRecursive(child);
        }
    }
}
