namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _statisticsNewestFirstHookInitialized;

    sealed class StatisticsNewestFirstComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;

            var leftRow = x as DataGridViewRow;
            var rightRow = y as DataGridViewRow;

            if (leftRow is null) return 1;
            if (rightRow is null) return -1;

            var leftProfile =
                Convert.ToString(leftRow.Cells["Profile"].Value)?.Trim() ?? "";
            var rightProfile =
                Convert.ToString(rightRow.Cells["Profile"].Value)?.Trim() ?? "";

            // Đảo ngược NaturalProfileNameOrder:
            // 18 -> 17 -> 16 -> ... -> 2 -> 1.
            return NaturalProfileNameOrder.Compare(rightProfile, leftProfile);
        }
    }

    readonly StatisticsNewestFirstComparer _statisticsNewestFirstComparer = new();

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        EnsureStatisticsNewestFirstHook();
    }

    void EnsureStatisticsNewestFirstHook()
    {
        if (_statisticsNewestFirstHookInitialized)
            return;

        _statisticsNewestFirstHookInitialized = true;

        // RefreshStatistics() đã được gắn vào _refreshTimer trước đó.
        // Handler này được gắn sau khi form load nên chạy sau refresh và chỉ
        // đổi thứ tự hiển thị, không đọc thêm file/IPC.
        _refreshTimer.Tick += (_, _) => ApplyStatisticsNewestFirstSort();

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_statisticsTab is null
                || _statisticsTab.IsDisposed
                || !ReferenceEquals(_tabs.SelectedTab, _statisticsTab))
            {
                return;
            }

            try
            {
                BeginInvoke((Action)ApplyStatisticsNewestFirstSort);
            }
            catch (InvalidOperationException) { }
        };

        try
        {
            BeginInvoke((Action)ApplyStatisticsNewestFirstSort);
        }
        catch (InvalidOperationException) { }
    }

    void ApplyStatisticsNewestFirstSort()
    {
        var grid = _statisticsGrid;

        if (grid is null
            || grid.IsDisposed
            || grid.Rows.Count <= 1)
        {
            return;
        }

        if (_statisticsTab is not null
            && !_statisticsTab.IsDisposed
            && !ReferenceEquals(_tabs.SelectedTab, _statisticsTab))
        {
            return;
        }

        var selectedProfile = grid.SelectedRows.Count > 0
            ? Convert.ToString(grid.SelectedRows[0].Cells["Profile"].Value)
            : null;

        try
        {
            grid.Sort(_statisticsNewestFirstComparer);

            if (!string.IsNullOrWhiteSpace(selectedProfile))
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (!string.Equals(
                            Convert.ToString(row.Cells["Profile"].Value),
                            selectedProfile,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    row.Selected = true;
                    grid.CurrentCell = row.Cells["Profile"];
                    break;
                }
            }

            // Sau sort luôn giữ đầu danh sách ở trên cùng.
            if (grid.Rows.Count > 0)
                grid.FirstDisplayedScrollingRowIndex = 0;
        }
        catch (InvalidOperationException)
        {
            // Nếu đúng lúc RefreshStatistics đang Clear/Add row thì bỏ qua;
            // timer kế tiếp sẽ sort lại.
        }
    }
}
