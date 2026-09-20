namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyLogForm : Form
{
    readonly ProxyDiagnostics _diagnostics;
    readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9F),
        BackColor = Color.White
    };
    readonly Label _path = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };

    public ProxyLogForm(ProxyDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        Text = "Nhật ký Proxy";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1080, 680);
        MinimumSize = new Size(760, 480);
        Font = new Font("Segoe UI", 9F);
        BuildUi();
        Reload();
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

        _path.Text = "Thư mục: " + _diagnostics.LogDirectory;
        root.Controls.Add(_path, 0, 0);
        root.Controls.Add(_text, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = "Đóng", Width = 100, Height = 30 };
        var refresh = new Button { Text = "Làm mới", Width = 100, Height = 30 };
        var openFolder = new Button { Text = "Mở thư mục", Width = 120, Height = 30 };
        close.Click += (_, _) => Close();
        refresh.Click += (_, _) => Reload();
        openFolder.Click += (_, _) => _diagnostics.OpenFolder();
        buttons.Controls.Add(close);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(openFolder);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    void Reload()
    {
        _text.Text = _diagnostics.ReadRecentText(500);
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }
}
