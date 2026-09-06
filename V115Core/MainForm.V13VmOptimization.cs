using ToolTikTokV12.Utils;
using ToolTikTokV11.Models;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly ComboBox _vmMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150
    };
    readonly Label _vmModeSummary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(1000, 0)
    };
    readonly Label _vmApplyStatus = new()
    {
        AutoSize = true,
        ForeColor = Color.ForestGreen,
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        Margin = new Padding(10, 7, 0, 0)
    };

    TabPage BuildVmOptimizationTab()
    {
        var tab = new TabPage("Tối ưu VM");
        var p = VerticalPanel();

        p.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = $"{AppVersionInfo.Display} — chế độ tiết kiệm tài nguyên cho máy ảo"
        });

        _vmMode.Items.Clear();
        _vmMode.Items.AddRange(["Bình thường", "VM Safe", "VM Max"]);
        _vmMode.SelectedIndexChanged += (_, _) =>
        {
            _vmApplyStatus.Text = string.Empty;
            RefreshVmModeSummary();
        };

        var modeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        AddLabeled(modeRow, "Chế độ", _vmMode);
        modeRow.Controls.Add(Btn("Áp dụng ngay", async (_, _) =>
        {
            SaveVmOptimizationFromUi();
            ApplyVmOptimizationSettings();
            if (_chrome.Connected) await _chrome.ApplyVmRuntimePolicyAsync();
            RefreshVmModeSummary();

            var appliedMode = _settings.VmOptimization.Mode switch
            {
                VmOptimizationMode.VmSafe => "VM Safe",
                VmOptimizationMode.VmMax => "VM Max",
                _ => "Bình thường"
            };
            _vmApplyStatus.Text = $"✓ Đã áp dụng: {appliedMode}";
            _log.Info($"Đã áp dụng chế độ tối ưu VM: {appliedMode}.");
        }));
        modeRow.Controls.Add(_vmApplyStatus);
        p.Controls.Add(modeRow);
        p.Controls.Add(_vmModeSummary);

        p.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = "VM Safe: giảm refresh UI/log, pause video, block luồng video phổ biến và cho Chrome background throttling hoạt động để giảm CPU khi treo nhiều profile.\n" +
                   "VM Max: như VM Safe, đồng thời tắt animation CSS và giảm tần suất UI/log mạnh hơn.\n" +
                   "Không thay đổi XPath, delay nghiệp vụ, Viewer, InputGuard, Live cũ, F5 hoặc flow chuyển LIVE."
        });

        tab.Controls.Add(p);
        return tab;
    }

    void LoadVmOptimizationToUi()
    {
        _vmMode.SelectedIndex = _settings.VmOptimization.Mode switch
        {
            VmOptimizationMode.VmSafe => 1,
            VmOptimizationMode.VmMax => 2,
            _ => 0
        };
        RefreshVmModeSummary();
    }

    void SaveVmOptimizationFromUi()
    {
        _settings.VmOptimization.Mode = _vmMode.SelectedIndex switch
        {
            1 => VmOptimizationMode.VmSafe,
            2 => VmOptimizationMode.VmMax,
            _ => VmOptimizationMode.Normal
        };
    }

    void ApplyVmOptimizationSettings()
    {
        _log.VerboseDiagnosticsEnabled = !_settings.VmOptimization.SuppressDetailedPerfLogs;
        _periodicUiTimer.Interval = _settings.VmOptimization.WorkerUiRefreshMs;
        _logUiTimer.Interval = _settings.VmOptimization.WorkerLogUiRefreshMs;
        _chrome.ConfigureVmOptimization(_settings.VmOptimization);
    }

    void RefreshVmModeSummary()
    {
        var mode = _vmMode.SelectedIndex switch
        {
            1 => VmOptimizationMode.VmSafe,
            2 => VmOptimizationMode.VmMax,
            _ => VmOptimizationMode.Normal
        };
        _vmModeSummary.Text = mode switch
        {
            VmOptimizationMode.VmSafe => "VM Safe: UI nền 2s, log UI 250ms, pause video, block video/segment phổ biến và cho Chrome background throttling để giảm CPU khi chạy lâu.",
            VmOptimizationMode.VmMax => "VM Max: như VM Safe, thêm tắt animation và giảm tần suất UI/log mạnh hơn.",
            _ => "Bình thường: giữ hành vi Chrome/UI đầy đủ như chế độ thường và ghi đầy đủ log chẩn đoán."
        };
    }
}
