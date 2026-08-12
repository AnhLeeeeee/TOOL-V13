using System.Drawing;
using System.Windows.Forms;
using ToolTikTokV11.Models;
using ToolTikTokV11.Services;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly CheckBox _inputGuardEnabled = new() { Text = "Bật kiểm tra trạng thái ô nhập trước mỗi Click", AutoSize = true, Checked = true };
    readonly TextBox _inputGuardPlaceholder = new() { Width = 160, Text = "Nhập" };
    readonly NumericUpDown _inputGuardConfirmReads = Num(1, 5);
    readonly NumericUpDown _inputGuardConfirmDelay = Num(0, 1000);
    readonly NumericUpDown _inputGuardConsecutive = Num(1, 4);
    readonly Label _inputGuardTest = new() { AutoSize = true, Text = "Chưa kiểm tra" };

    TabPage BuildInputGuardTab()
    {
        var tab = new TabPage("Kiểm tra ô nhập");
        var root = VerticalPanel();

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "V13.4.1 — phát hiện trạng thái trực tiếp từ DOM/XPath, không quét ảnh vùng lỗi"
        };
        root.Controls.Add(title);
        root.Controls.Add(_inputGuardEnabled);

        // Dùng CHUNG đúng XPathPoint1/XPathPoint2 với tab “Điều khiển / XPath”.
        // Không tạo thêm một bộ cấu hình XPath riêng để tránh lệch dữ liệu giữa hai tab.
        var xpathTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 6, 0, 8) };
        xpathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        xpathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        xpathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        xpathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        xpathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        AddXPathRow(xpathTable, 0, "XPath ô nhập 1", _xp1, () => PickIntoAsync(_xp1), () => TestInputGuardAsync(_xp1.Text, "ô 1"), "Thử guard");
        AddXPathRow(xpathTable, 1, "XPath ô nhập 2", _xp2, () => PickIntoAsync(_xp2), () => TestInputGuardAsync(_xp2.Text, "ô 2"), "Thử guard");
        root.Controls.Add(xpathTable);

        var config = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 6, 0, 6) };
        AddLabeled(config, "Chữ bình thường", _inputGuardPlaceholder);
        AddLabeled(config, "Xác nhận lần", _inputGuardConfirmReads);
        AddLabeled(config, "Cách nhau ms", _inputGuardConfirmDelay);
        AddLabeled(config, "Lỗi LT tối đa", _inputGuardConsecutive);
        root.Controls.Add(config);

        var tests = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        tests.Controls.Add(Btn("Kiểm tra ô 1", async (_, _) => await TestInputGuardAsync(_xp1.Text, "ô 1")));
        tests.Controls.Add(Btn("Kiểm tra ô 2", async (_, _) => await TestInputGuardAsync(_xp2.Text, "ô 2")));
        tests.Controls.Add(_inputGuardTest);
        root.Controls.Add(tests);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            Text = "Logic runtime V13: ngay trước Click 1/2, tool đọc ô nhập bằng CDP. " +
                   "Hai dòng XPath phía trên dùng chung trực tiếp với Điểm/ô nhập 1 và 2 ở tab Điều khiển / XPath. " +
                   "Bình thường = ô tồn tại, hiển thị, editable, rỗng và còn chữ/placeholder cấu hình (mặc định ‘Nhập’). " +
                   "Nếu bất thường liên tiếp theo số lần xác nhận, tool gọi nguyên flow chuyển LIVE cũ (↓/nút LIVE → chờ → F5 → xác nhận LIVE mới). " +
                   "Các vùng ảnh lỗi/STOP/ban acc V12.5 không còn chạy trong runtime. Viewer và F5 định kỳ giữ nguyên; Live cũ V13.4.1 dùng định danh tài khoản DOM/XPath."
        });

        tab.Controls.Add(root);
        return tab;
    }

    void LoadInputGuardToUi()
    {
        var g = _settings.InputGuard ?? new InputGuardSettings();
        _inputGuardEnabled.Checked = g.Enabled;
        _inputGuardPlaceholder.Text = string.IsNullOrWhiteSpace(g.NormalPlaceholderText) ? "Nhập" : g.NormalPlaceholderText;
        _inputGuardConfirmReads.Value = Clamp(Math.Clamp(g.ConfirmReads, 1, 5), _inputGuardConfirmReads);
        _inputGuardConfirmDelay.Value = Clamp(Math.Clamp(g.ConfirmDelayMs, 0, 1000), _inputGuardConfirmDelay);
        _inputGuardConsecutive.Value = Clamp(Math.Clamp(g.ConsecutiveMax, 1, 4), _inputGuardConsecutive);
    }

    void SaveInputGuardFromUi()
    {
        _settings.InputGuard ??= new InputGuardSettings();
        _settings.InputGuard.Enabled = _inputGuardEnabled.Checked;
        _settings.InputGuard.NormalPlaceholderText = _inputGuardPlaceholder.Text.Trim();
        _settings.InputGuard.ConfirmReads = (int)_inputGuardConfirmReads.Value;
        _settings.InputGuard.ConfirmDelayMs = (int)_inputGuardConfirmDelay.Value;
        _settings.InputGuard.ConsecutiveMax = (int)_inputGuardConsecutive.Value;
    }

    async Task TestInputGuardAsync(string xpath, string label)
    {
        try
        {
            SaveInputGuardFromUi();
            if (string.IsNullOrWhiteSpace(_settings.InputGuard.NormalPlaceholderText))
                throw new InvalidOperationException("Chữ bình thường đang trống. Mặc định nên để ‘Nhập’. ");
            await EnsureChromeAsync();
            var guard = new ChatInputGuard(_chrome, _log);
            var snapshot = await guard.ProbeAsync(xpath.Trim(), _settings.InputGuard.NormalPlaceholderText);
            _inputGuardTest.Text = snapshot.IsNormal
                ? $"✓ {label}: BÌNH THƯỜNG — placeholder={snapshot.Placeholder}"
                : $"! {label}: BẤT THƯỜNG — {snapshot.Reason}";
            _inputGuardTest.ForeColor = snapshot.IsNormal ? Color.DarkGreen : Color.Firebrick;
            _toolTip.SetToolTip(_inputGuardTest,
                $"exists={snapshot.Exists}; visible={snapshot.Visible}; editable={snapshot.Editable}; disabled={snapshot.Disabled}; empty={snapshot.Empty}; placeholder={snapshot.Placeholder}; text={snapshot.Text}");
        }
        catch (Exception ex)
        {
            _inputGuardTest.Text = "! Không kiểm tra được: " + ex.Message;
            _inputGuardTest.ForeColor = Color.Firebrick;
            ShowUiProblem("INPUT_GUARD_TEST", "Kiểm tra ô nhập", ex, showDialog: false);
        }
    }
}
