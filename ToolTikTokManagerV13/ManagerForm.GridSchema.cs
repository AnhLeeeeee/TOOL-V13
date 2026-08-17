namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly HashSet<string> _reportedGridSchemaErrors = new(StringComparer.Ordinal);

    void LogGridSchema(DataGridView grid, string gridName, params string[] requiredColumns)
    {
        if (string.IsNullOrWhiteSpace(grid.Name)) grid.Name = gridName;

        var columns = GridColumnNames(grid);
        _log.Info($"[GRID_SCHEMA] grid={GridLogName(grid, gridName)} columns={columns}");

        foreach (var requiredColumn in requiredColumns)
        {
            if (FindGridColumn(grid, requiredColumn) is null)
                LogGridSchemaError(grid, requiredColumn, "InitializeGrid", gridName);
        }
    }

    DataGridViewCell? TryGetGridCell(DataGridViewRow row, string columnName, string method)
    {
        var grid = row.DataGridView;
        if (grid is null)
        {
            LogGridSchemaError(null, columnName, method, "detached-row");
            return null;
        }

        var column = FindGridColumn(grid, columnName);
        if (column is null)
        {
            LogGridSchemaError(grid, columnName, method);
            return null;
        }

        if (column.Index < 0 || column.Index >= row.Cells.Count)
        {
            LogGridSchemaError(grid, columnName, method + ":row-cell-count");
            return null;
        }

        return row.Cells[column.Index];
    }

    object? GetGridCellValueOrNull(DataGridViewRow row, string columnName, string method)
        => TryGetGridCell(row, columnName, method)?.Value;

    bool TrySetGridCellValue(DataGridViewRow row, string columnName, object? value, string method)
    {
        var cell = TryGetGridCell(row, columnName, method);
        if (cell is null) return false;
        cell.Value = value;
        return true;
    }

    DataGridViewColumn? TryGetGridColumn(DataGridView grid, string columnName, string method)
    {
        var column = FindGridColumn(grid, columnName);
        if (column is not null) return column;
        LogGridSchemaError(grid, columnName, method);
        return null;
    }

    static DataGridViewColumn? FindGridColumn(DataGridView grid, string columnName)
        => grid.Columns.Cast<DataGridViewColumn>()
            .FirstOrDefault(column => string.Equals(column.Name, columnName, StringComparison.Ordinal));

    void LogGridSchemaError(DataGridView? grid, string columnName, string method, string? fallbackGridName = null)
    {
        var gridName = grid is null ? fallbackGridName ?? "<null>" : GridLogName(grid, fallbackGridName);
        var key = $"{gridName}\u001f{columnName}\u001f{method}";
        lock (_reportedGridSchemaErrors)
        {
            if (!_reportedGridSchemaErrors.Add(key)) return;
        }

        var columns = grid is null ? "<unavailable>" : GridColumnNames(grid);
        _log.Error($"[GRID_SCHEMA_ERROR] grid={gridName} missingColumn={columnName} method={method} columns={columns}");
    }

    static string GridLogName(DataGridView grid, string? fallback = null)
        => !string.IsNullOrWhiteSpace(grid.Name) ? grid.Name
            : !string.IsNullOrWhiteSpace(fallback) ? fallback
            : "<unnamed>";

    static string GridColumnNames(DataGridView grid)
        => string.Join(",", grid.Columns.Cast<DataGridViewColumn>().Select(column =>
            string.IsNullOrWhiteSpace(column.Name)
                ? $"<unnamed:{column.HeaderText}>"
                : column.Name));
}
