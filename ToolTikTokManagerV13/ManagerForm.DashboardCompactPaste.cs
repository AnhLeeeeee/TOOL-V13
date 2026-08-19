using System.Runtime.CompilerServices;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _dashboardCompactPasteInstalled;

    // Bản dán: không cần BAT/PS1. File này tự gắn vào Dashboard hiện có khi Manager mở.
    [ModuleInitializer]
    internal static void BootstrapDashboardCompactPaste()
    {
        EventHandler? idleHandler = null;
        idleHandler = (_, _) =>
        {
            foreach (Form openForm in Application.OpenForms)
            {
                if (openForm is not ManagerForm manager) continue;
                manager.InstallDashboardCompactPaste();
                if (idleHandler is not null)
                    Application.Idle -= idleHandler;
                break;
            }
        };
        Application.Idle += idleHandler;
    }

    void InstallDashboardCompactPaste()
    {
        if (_dashboardCompactPasteInstalled) return;
        _dashboardCompactPasteInstalled = true;

        ApplyDashboardCompactPaste();

        // Tận dụng timer refresh có sẵn của Manager, không tạo timer/luồng mới.
        _refreshTimer.Tick += (_, _) => ApplyDashboardCompactPaste();
        if (_dashboardTab is not null && !_dashboardTab.IsDisposed)
            _dashboardTab.Enter += (_, _) => ApplyDashboardCompactPaste();
    }

    void ApplyDashboardCompactPaste()
    {
        var grid = _dashboardGrid;
        if (grid is null || grid.IsDisposed) return;
        if (_dashboardTab is not null && !ReferenceEquals(_tabs.SelectedTab, _dashboardTab)) return;

        // Nếu source đã áp dụng bản Dashboard gọn trước đó thì chỉ giữ layout/format,
        // không ghi đè logic màu cảnh báo vốn đã nằm trong file chính.
        var legacyLayout = grid.Columns.Contains("Chrome")
            || grid.Columns.Contains("Step")
            || grid.Columns.Contains("Ram")
            || grid.Columns.Contains("Detail");

        HideDashboardColumn(grid, "Chrome");
        HideDashboardColumn(grid, "Step");
        HideDashboardColumn(grid, "Ram");
        HideDashboardColumn(grid, "Detail");

        if (!grid.Columns.Contains("Progress"))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Progress",
                HeaderText = "Tiến trình",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 360
            });
        }

        if (grid.Columns.Contains("Account")) grid.Columns["Account"].Width = 185;
        if (grid.Columns.Contains("RunState")) grid.Columns["RunState"].Width = 115;
        if (grid.Columns.Contains("RunTime"))
        {
            grid.Columns["RunTime"].Width = 105;
            grid.Columns["RunTime"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        if (grid.Columns.Contains("Rounds"))
        {
            grid.Columns["Rounds"].Width = 80;
            grid.Columns["Rounds"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        if (grid.Columns.Contains("Progress"))
        {
            grid.Columns["Progress"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["Progress"].MinimumWidth = 360;
        }

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;

            if (legacyLayout)
            {
                var progress = BuildDashboardCompactProgress(row);
                SetCompactCellIfChanged(row, "Progress", progress);
                ApplyDashboardCompactRowStyle(row, progress);
            }
            else
            {
                // Source đã gọn sẵn: chỉ bảo đảm Profile nổi bật và 2 cột số căn giữa.
                ApplyDashboardCompactProfileStyle(row);
            }
        }
    }

    static void HideDashboardColumn(DataGridView grid, string name)
    {
        if (grid.Columns.Contains(name))
            grid.Columns[name].Visible = false;
    }

    static string BuildDashboardCompactProgress(DataGridViewRow row)
    {
        var detail = GetCompactCellText(row, "Detail").Trim();
        if (detail.StartsWith("Bước:", StringComparison.OrdinalIgnoreCase))
            detail = detail[5..].Trim();

        if (!string.IsNullOrWhiteSpace(detail) && detail != "—")
            return detail;

        var step = GetCompactCellText(row, "Step").Trim();
        return string.IsNullOrWhiteSpace(step) || step == "—" ? "—" : $"Bước {step}";
    }

    static string GetCompactCellText(DataGridViewRow row, string columnName)
    {
        try
        {
            return row.DataGridView?.Columns.Contains(columnName) == true
                ? Convert.ToString(row.Cells[columnName].Value) ?? ""
                : "";
        }
        catch { return ""; }
    }

    static void SetCompactCellIfChanged(DataGridViewRow row, string columnName, string value)
    {
        if (row.DataGridView?.Columns.Contains(columnName) != true) return;
        var current = Convert.ToString(row.Cells[columnName].Value) ?? "";
        if (!string.Equals(current, value, StringComparison.Ordinal))
            row.Cells[columnName].Value = value;
    }

    static void ApplyDashboardCompactProfileStyle(DataGridViewRow row)
    {
        if (row.DataGridView?.Columns.Contains("Profile") != true) return;
        var profileCell = row.Cells["Profile"];
        profileCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        profileCell.Style.ForeColor = Color.FromArgb(25, 67, 112);
        profileCell.Style.SelectionForeColor = Color.FromArgb(18, 55, 95);
    }

    static void ApplyDashboardCompactRowStyle(DataGridViewRow row, string progress)
    {
        var runState = GetCompactCellText(row, "RunState").Trim();
        var chrome = GetCompactCellText(row, "Chrome").Trim();

        var chromeKnown = !string.IsNullOrWhiteSpace(chrome) && chrome != "—";
        var chromeDisconnected = chromeKnown
            && !string.Equals(chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        var hasError = !string.IsNullOrWhiteSpace(progress)
            && (progress.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || progress.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || progress.Contains("Lỗi", StringComparison.OrdinalIgnoreCase));

        // RUNNING bình thường giữ nền trắng để bảng dễ quét.
        row.DefaultCellStyle.BackColor = Color.White;
        row.DefaultCellStyle.ForeColor = SystemColors.ControlText;

        if (chromeDisconnected || hasError)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 238, 238);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(135, 45, 45);
        }
        else if (string.Equals(runState, RuntimeStateRecovering, StringComparison.Ordinal))
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 218);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(112, 82, 14);
        }
        else if (string.Equals(runState, RuntimeStatePaused, StringComparison.Ordinal))
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 246, 229);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(140, 83, 12);
        }

        ApplyDashboardCompactProfileStyle(row);

        if (row.DataGridView?.Columns.Contains("RunState") == true)
        {
            var stateCell = row.Cells["RunState"];
            stateCell.Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            stateCell.Style.ForeColor = chromeDisconnected || hasError
                ? Color.FromArgb(176, 45, 45)
                : GetRuntimeStateColor(runState);
        }
    }
}
