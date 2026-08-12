namespace ToolTikTokV12.Controls;

public enum UiButtonKind { Primary, Neutral, Danger }

/// <summary>Small native WinForms theme shared by Manager and profile tabs; no owner draw or animation.</summary>
public static class UiTheme
{
    public static readonly Color Canvas = Color.FromArgb(246, 248, 251);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(214, 220, 230);
    public static readonly Color Primary = Color.FromArgb(30, 97, 172);

    public static void Apply(Control root)
    {
        root.Font = new Font("Segoe UI", 9F);
        if (root is Form or UserControl) root.BackColor = Canvas;
        foreach (Control child in root.Controls)
        {
            if (child is GroupBox group)
            {
                group.BackColor = Card;
                group.ForeColor = Color.FromArgb(36, 49, 66);
                group.Padding = new Padding(Math.Max(8, group.Padding.Left), 10, Math.Max(8, group.Padding.Right), Math.Max(8, group.Padding.Bottom));
            }
            else if (child is TabPage page) page.BackColor = Canvas;
            else if (child is Button button) StyleButton(button);
            Apply(child);
        }
    }

    public static void StyleButton(Button button, UiButtonKind kind = UiButtonKind.Neutral)
    {
        button.AutoSize = true;
        button.Height = Math.Max(32, button.Height);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = new Padding(4);
        var (back, fore, border) = kind switch
        {
            UiButtonKind.Primary => (Primary, Color.White, Primary),
            UiButtonKind.Danger => (Color.FromArgb(180, 54, 54), Color.White, Color.FromArgb(180, 54, 54)),
            _ => (Card, Color.FromArgb(42, 57, 76), Border)
        };
        button.BackColor = back;
        button.ForeColor = fore;
        button.FlatAppearance.BorderColor = border;
    }
}
