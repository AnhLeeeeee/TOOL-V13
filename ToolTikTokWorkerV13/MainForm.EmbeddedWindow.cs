namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const int WsExToolWindow = 0x00000080;
    const int WsExAppWindow = 0x00040000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Worker embedded chỉ là giao diện con của Manager.
            // Không để Windows đưa nó vào Alt+Tab / Task View trước lúc SetParent.
            if (_startupOptions?.Embedded == true)
            {
                cp.ExStyle |= WsExToolWindow;
                cp.ExStyle &= ~WsExAppWindow;
            }

            return cp;
        }
    }
}
