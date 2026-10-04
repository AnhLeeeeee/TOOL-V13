using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const int CommentCheckSecretClickCount = 5;
    static readonly TimeSpan CommentCheckSecretClickWindow = TimeSpan.FromSeconds(3);
    const string CommentCheckSecretSha256 = "A4F488D8472393E1821F8D536E77F64307D70D124DC3B7513E7A99C153186FDB";

    void InstallSecretCommentCheckTrigger(Form autoRunForm, Label autoRunTitle)
    {
        var clickCount = 0;
        var firstClickUtc = DateTime.MinValue;

        autoRunTitle.Click += (_, _) =>
        {
            var now = DateTime.UtcNow;

            if (clickCount == 0 || now - firstClickUtc > CommentCheckSecretClickWindow)
            {
                clickCount = 1;
                firstClickUtc = now;
                return;
            }

            clickCount++;
            if (clickCount < CommentCheckSecretClickCount)
                return;

            var elapsed = now - firstClickUtc;
            clickCount = 0;
            firstClickUtc = DateTime.MinValue;

            if (elapsed > CommentCheckSecretClickWindow)
                return;

            var entered = ShowCommentCheckSecretPrompt(autoRunForm);
            if (!IsCommentCheckSecretValid(entered))
                return;

            OpenCommentVisibilityMonitor();
        };
    }

    static bool IsCommentCheckSecretValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var bytes = System.Text.Encoding.UTF8.GetBytes(value.Trim());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return string.Equals(
            Convert.ToHexString(hash),
            CommentCheckSecretSha256,
            StringComparison.Ordinal);
    }

    string? ShowCommentCheckSecretPrompt(IWin32Window owner)
    {
        using var dialog = new Form
        {
            Text = string.Empty,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 118),
            BackColor = ModernDialog.Canvas,
            Font = new Font("Segoe UI", 9.5F),
            AutoScaleMode = AutoScaleMode.Dpi
        };
        ModernDialog.Apply(dialog);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14, 14, 14, 10),
            BackColor = ModernDialog.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var input = new TextBox
        {
            Dock = DockStyle.Top,
            UseSystemPasswordChar = true,
            Margin = new Padding(0, 4, 0, 8)
        };
        ModernDialog.StyleTextInput(input);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(78, 32)
        };
        var cancel = new Button
        {
            Text = "Hủy",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(78, 32)
        };
        ModernDialog.StylePrimaryButton(ok);
        ModernDialog.StyleSecondaryButton(cancel);

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(input, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(root);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            ModernDialog.FitToWorkingArea(dialog);
            input.Focus();
        };

        return dialog.ShowDialog(owner) == DialogResult.OK
            ? input.Text.Trim()
            : null;
    }

    void OpenCommentVisibilityMonitor()
    {
        try
        {
            var existing = System.Diagnostics.Process
                .GetProcessesByName("CommentVisibilityMonitor")
                .FirstOrDefault(p => !p.HasExited);

            if (existing is not null)
            {
                TryBringCommentCheckToFront(existing);
                return;
            }

            var exe = FindCommentVisibilityMonitorExecutable();
            if (string.IsNullOrWhiteSpace(exe))
            {
                _log.Warn("[COMMENT_CHECK_SECRET_OPEN_MISSING] executable_not_found");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });

            _log.Info("[COMMENT_CHECK_SECRET_OPEN] launched=true");
        }
        catch (Exception ex)
        {
            // Đây là tính năng phụ: lỗi mở Comment Check tuyệt đối không được lan
            // sang Auto Run hoặc thay đổi trạng thái vận hành của Manager.
            _log.Warn($"[COMMENT_CHECK_SECRET_OPEN_WARN] {ex.GetType().Name}: {ex.Message}");
        }
    }

    static string? FindCommentVisibilityMonitorExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "CommentCheck", "CommentVisibilityMonitor.exe"),
            Path.Combine(baseDir, "dist_comment_check", "CommentVisibilityMonitor.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "dist_comment_check", "CommentVisibilityMonitor.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    static void TryBringCommentCheckToFront(System.Diagnostics.Process process)
    {
        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
                return;

            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(handle);
        }
        catch
        {
            // Không ảnh hưởng gì nếu Windows từ chối focus cửa sổ hiện có.
        }
    }

    static class NativeMethods
    {
        internal const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
