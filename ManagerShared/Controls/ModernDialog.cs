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
