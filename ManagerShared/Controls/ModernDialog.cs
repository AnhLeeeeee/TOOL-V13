namespace ToolTikTokV12.Controls;

/// <summary>
/// Shared visual language for custom WinForms dialogs in V12.5.
/// It deliberately leaves native MessageBox and file-picker behavior unchanged.
/// </summary>
public static class ModernDialog
{
    public static readonly Color Canvas = Color.FromArgb(250, 251, 253);
    static readonly Color BodyText = Color.FromArgb(42, 57, 76);
    static readonly Color PrimaryBack = Color.FromArgb(232, 242, 255);
    static readonly Color PrimaryText = Color.FromArgb(35, 91, 152);
    static readonly Color PrimaryBorder = Color.FromArgb(130, 173, 220);
    static readonly Color NeutralBack = Color.FromArgb(247, 249, 252);
    static readonly Color NeutralText = Color.FromArgb(55, 76, 103);
    static readonly Color NeutralBorder = Color.FromArgb(190, 201, 214);
    static readonly Color DangerBack = Color.FromArgb(255, 239, 239);
    static readonly Color DangerText = Color.FromArgb(171, 62, 62);
    static readonly Color DangerBorder = Color.FromArgb(219, 151, 151);

    public static void Apply(Form form, bool fixedDialog = true)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("Segoe UI", 10F);
        form.BackColor = Canvas;
        form.ForeColor = BodyText;
        form.StartPosition = FormStartPosition.CenterParent;
        form.ShowInTaskbar = false;

        if (!fixedDialog) return;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
    }

    public static void FitToWorkingArea(Form form, int margin = 24)
    {
        var workingArea = Screen.FromControl(form).WorkingArea;
        var maxWidth = Math.Max(420, workingArea.Width - margin * 2);
        var maxHeight = Math.Max(320, workingArea.Height - margin * 2);
        var requestedMinimum = form.MinimumSize;
        form.MinimumSize = new Size(
            Math.Min(requestedMinimum.Width, maxWidth),
            Math.Min(requestedMinimum.Height, maxHeight));
        form.Width = Math.Min(form.Width, maxWidth);
        form.Height = Math.Min(form.Height, maxHeight);
        form.Left = workingArea.Left + Math.Max(0, (workingArea.Width - form.Width) / 2);
        form.Top = workingArea.Top + Math.Max(0, (workingArea.Height - form.Height) / 2);
    }

    public static void StylePrimaryLabel(Label label)
    {
        label.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        label.ForeColor = BodyText;
    }

    public static void StyleTextInput(TextBox input)
    {
        input.Font = new Font("Segoe UI", 11F);
        input.BackColor = Color.White;
        input.ForeColor = BodyText;
        input.BorderStyle = BorderStyle.FixedSingle;
        if (!input.Multiline)
            input.MinimumSize = new Size(input.MinimumSize.Width, Math.Max(36, input.MinimumSize.Height));
    }

    public static void StyleSelectionInput(ComboBox input)
    {
        input.Font = new Font("Segoe UI", 11F);
        input.BackColor = Color.White;
        input.ForeColor = BodyText;
        input.FlatStyle = FlatStyle.Flat;
        input.MinimumSize = new Size(input.MinimumSize.Width, Math.Max(36, input.MinimumSize.Height));
    }

    public static void StyleSelectionList(ListBox list)
    {
        list.Font = new Font("Segoe UI", 11F);
        list.BackColor = Color.White;
        list.ForeColor = BodyText;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.ItemHeight = Math.Max(34, list.ItemHeight);
    }


    public static DialogResult ShowMessage(IWin32Window? owner, string text, string title, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        using var form = new Form
        {
            Text = title,
            Width = 560,
            Height = 280,
            MinimumSize = new Size(500, 240),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        Apply(form, fixedDialog: false);

        var accent = icon switch
        {
            MessageBoxIcon.Error => DangerText,
            MessageBoxIcon.Warning => Color.FromArgb(166, 106, 23),
            _ => PrimaryText
        };
        var symbol = icon switch
        {
            MessageBoxIcon.Error => "!",
            MessageBoxIcon.Warning => "!",
            _ => "i"
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            Padding = new Padding(16, 10, 16, 16),
            BackColor = Canvas
        };
        var ok = new Button { Text = "OK", Size = new Size(108, 42), DialogResult = DialogResult.OK };
        StylePrimaryButton(ok);
        ok.Dock = DockStyle.Right;
        footer.Controls.Add(ok);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18, 18, 18, 8),
            BackColor = Canvas
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var badge = new Label
        {
            Text = symbol,
            AutoSize = false,
            Size = new Size(34, 34),
            Margin = new Padding(0, 1, 10, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = accent,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        var message = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(450, 0),
            Margin = new Padding(0, 2, 0, 0),
            Font = new Font("Segoe UI", 10F),
            ForeColor = BodyText
        };
        layout.Controls.Add(badge, 0, 0);
        layout.Controls.Add(message, 1, 0);
        content.Controls.Add(layout);

        form.Controls.Add(content);
        form.Controls.Add(footer);
        form.AcceptButton = ok;
        form.CancelButton = ok;
        form.Shown += (_, _) =>
        {
            FitToWorkingArea(form);
            ok.Focus();
        };
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }


    public static DialogResult ShowConfirm(IWin32Window? owner, string text, string title)
    {
        using var form = new Form
        {
            Text = title,
            Width = 560,
            Height = 285,
            MinimumSize = new Size(500, 245),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        Apply(form, fixedDialog: false);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(16, 10, 16, 16), BackColor = Canvas };
        var yes = new Button { Text = "Xác nhận", Size = new Size(120, 42), DialogResult = DialogResult.Yes };
        var no = new Button { Text = "Hủy", Size = new Size(104, 42), DialogResult = DialogResult.No };
        StyleDestructiveButton(yes);
        StyleSecondaryButton(no);
        yes.Dock = DockStyle.Right;
        no.Dock = DockStyle.Right;
        footer.Controls.Add(yes);
        footer.Controls.Add(no);

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 18, 18, 8), BackColor = Canvas };
        var message = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Font = new Font("Segoe UI", 10F),
            ForeColor = BodyText
        };
        content.Controls.Add(message);
        form.Controls.Add(content);
        form.Controls.Add(footer);
        form.AcceptButton = yes;
        form.CancelButton = no;
        form.Shown += (_, _) => { FitToWorkingArea(form); no.Focus(); };
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }

    public static void StylePrimaryButton(Button button) => StyleButton(button, PrimaryBack, PrimaryText, PrimaryBorder, true);
    public static void StyleSecondaryButton(Button button) => StyleButton(button, NeutralBack, NeutralText, NeutralBorder, false);
    public static void StyleDestructiveButton(Button button) => StyleButton(button, DangerBack, DangerText, DangerBorder, true);

    static void StyleButton(Button button, Color background, Color foreground, Color border, bool bold)
    {
        button.AutoSize = false;
        button.Width = Math.Max(104, button.Width);
        button.Height = 42;
        button.Margin = new Padding(4, 0, 0, 0);
        button.Font = new Font("Segoe UI", 10F, bold ? FontStyle.Bold : FontStyle.Regular);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(background);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(background);
    }
}
