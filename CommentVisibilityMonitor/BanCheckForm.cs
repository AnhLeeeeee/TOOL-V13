namespace CommentVisibilityMonitor;

internal sealed class BanCheckForm : Form
{
    readonly ObserverChromeSession _observer;
    readonly string _dataDir;
    readonly Action<string> _log;
    readonly Func<bool> _canStart;
    readonly Action<bool> _runningChanged;
    readonly BanCheckExcelStore _store = new();

    readonly Label _sourceInfo = new()
    {
        Dock = DockStyle.Top,
        Height = 58,
        Padding = new Padding(16, 9, 16, 6),
        ForeColor = Color.DimGray,
        AutoEllipsis = true,
        Text = "Chưa chọn Excel  •  Bấm Mở Excel để chọn kho tài khoản."
    };
    readonly Label _status = new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        Padding = new Padding(16, 5, 16, 5),
        ForeColor = Color.FromArgb(46, 65, 88),
        Text = "Đã kiểm tra: — | Sống: — | BAN: — | Không rõ: —"
    };
    readonly DataGridView _grid = new()
    {
        Name = "BanCheckAccountGrid",
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        BackgroundColor = Color.FromArgb(246, 248, 251),
        BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 34
    };
    readonly Button _openExcel = new() { Text = "Mở Excel", AutoSize = true, Height = 34 };
    readonly Button _reload = new() { Text = "Tải lại", AutoSize = true, Height = 34 };
    readonly Button _start = new() { Text = "Bắt đầu Check BAN", AutoSize = true, Height = 34 };
    readonly Button _stop = new() { Text = "Dừng", AutoSize = true, Height = 34, Enabled = false };
    readonly Button _close = new() { Text = "Đóng", AutoSize = true, Height = 34 };

    string _sourcePath = "";
    List<BanCheckAccountRow> _items = new();
    CancellationTokenSource? _runCts;
    bool _running;

    string SourceSettingsPath => Path.Combine(_dataDir, "ban_check_source.txt");

    public BanCheckForm(
        ObserverChromeSession observer,
        string dataDir,
        Action<string> log,
        Func<bool> canStart,
        Action<bool> runningChanged)
    {
        _observer = observer;
        _dataDir = dataDir;
        _log = log;
        _canStart = canStart;
        _runningChanged = runningChanged;

        Text = "Kho tài khoản — CHECK BAN";
        Width = 920;
        Height = 650;
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);
        BackColor = Color.FromArgb(246, 248, 251);

        _grid.RowTemplate.Height = 28;
        _grid.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "row",
            HeaderText = "Dòng",
            Width = 58,
            Frozen = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(125, 133, 143)
            }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "user",
            HeaderText = "Tài khoản",
            Width = 285,
            Frozen = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "note",
            HeaderText = "Ghi chú",
            Width = 150
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "result",
            HeaderText = "Kết quả",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220
        });

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(12, 9, 12, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.White
        };
        bottom.Controls.AddRange(new Control[] { _openExcel, _reload, _start, _stop, _close });

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(_sourceInfo);
        Controls.Add(bottom);

        _openExcel.Click += (_, _) => OpenExcel();
        _reload.Click += (_, _) => ReloadExcel(showError: true);
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop("Người dùng dừng");
        _close.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            Stop("Đóng cửa sổ");
        };
        Shown += (_, _) => LoadRememberedSource();
    }

    void LoadRememberedSource()
    {
        try
        {
            if (!File.Exists(SourceSettingsPath)) return;
            var path = File.ReadAllText(SourceSettingsPath).Trim();
            if (path.Length == 0 || !File.Exists(path)) return;
            _sourcePath = path;
            ReloadExcel(showError: false);
        }
        catch { }
    }

    void RememberSource()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(SourceSettingsPath, _sourcePath);
        }
        catch { }
    }

    void OpenExcel()
    {
        if (_running) return;
        using var picker = new OpenFileDialog
        {
            Title = "Mở kho tài khoản để CHECK BAN",
            Filter = "Excel (*.xlsx)|*.xlsx",
            Multiselect = false,
            CheckFileExists = true
        };
        if (_sourcePath.Length > 0 && File.Exists(_sourcePath))
        {
            picker.InitialDirectory = Path.GetDirectoryName(_sourcePath);
            picker.FileName = Path.GetFileName(_sourcePath);
        }
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _sourcePath = Path.GetFullPath(picker.FileName);
            _items = _store.Open(_sourcePath).ToList();
            RememberSource();
            RefreshGrid();
            _log($"[BAN_CHECK_EXCEL_OPEN] file={Path.GetFileName(_sourcePath)} accounts={_items.Count}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Không mở được Excel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void ReloadExcel(bool showError)
    {
        if (_running) return;
        try
        {
            if (_sourcePath.Length == 0 || !File.Exists(_sourcePath))
            {
                if (showError)
                    MessageBox.Show(this, "Chưa có file Excel đang dùng. Hãy bấm Mở Excel trước.", "Tải lại", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _items = _store.Reload(_sourcePath).ToList();
            RefreshGrid();
        }
        catch (Exception ex)
        {
            if (showError)
                MessageBox.Show(this, ex.Message, "Không tải lại được Excel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var item in _items.OrderBy(x => x.SourceRow))
        {
            var existingBan = IsBan(item.Note);
            var existingDone = IsDone(item.Note);
            var index = _grid.Rows.Add(
                item.SourceRow,
                item.Username,
                item.Note,
                existingBan ? "BAN (đã ghi)" : existingDone ? "SỐNG (đã ghi)" : "Chưa kiểm tra");
            var row = _grid.Rows[index];
            row.Tag = item.SourceRow;
            if (existingBan)
                ApplyResultStyle(row, BanCheckUiResult.Ban);
            else if (existingDone)
                ApplyResultStyle(row, BanCheckUiResult.Alive);
        }

        _sourceInfo.Text = _sourcePath.Length == 0
            ? "Chưa chọn Excel  •  Bấm Mở Excel để chọn kho tài khoản."
            : $"{Path.GetFileName(_sourcePath)}  •  {_items.Count} tài khoản  •  Chỉ dùng cột Tài khoản để tìm; Mật khẩu/2FA được giữ nguyên.";

        var savedAlive = _items.Count(x => IsDone(x.Note));
        var savedBan = _items.Count(x => IsBan(x.Note));
        var savedChecked = savedAlive + savedBan;
        _status.Text = $"Đã kiểm tra: {savedChecked}/{_items.Count} | Sống: {savedAlive} | BAN: {savedBan} | Không rõ: 0";
    }

    async Task StartAsync()
    {
        if (_running) return;
        if (!_canStart())
        {
            MessageBox.Show(this, "Check CMT đang chạy. Hãy dừng Check CMT trước khi Check BAN.", "CHECK BAN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_sourcePath.Length == 0 || !File.Exists(_sourcePath))
        {
            MessageBox.Show(this, "Hãy bấm Mở Excel để chọn kho tài khoản trước.", "CHECK BAN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_observer.Connected)
        {
            MessageBox.Show(this, "Hãy mở Chrome Observer ở cửa sổ Check CMT trước.", "CHECK BAN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            _items = _store.Reload(_sourcePath).ToList();
            RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Không đọc được Excel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var work = _items.Where(x => !IsFinalNote(x.Note)).OrderBy(x => x.SourceRow).ToList();
        if (work.Count == 0)
        {
            MessageBox.Show(this, "Không còn tài khoản cần kiểm tra. Các dòng Ghi chú=done/ban đã được bỏ qua.", "CHECK BAN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        _running = true;
        SetUiRunning(true);
        _runningChanged(true);
        _log($"[BAN_CHECK_BEGIN] total={work.Count} file={Path.GetFileName(_sourcePath)}");

        var processed = 0;
        var alive = _items.Count(x => IsDone(x.Note));
        var banned = _items.Count(x => IsBan(x.Note));
        var unknown = 0;
        var persistedChecked = alive + banned;

        try
        {
            foreach (var item in work)
            {
                ct.ThrowIfCancellationRequested();
                MarkChecking(item.SourceRow);
                _status.Text = $"Đang kiểm tra: {item.Username}  •  {persistedChecked + processed}/{_items.Count} | Sống: {alive} | BAN: {banned} | Không rõ: {unknown}";

                var first = await _observer.SearchUserExactAsync(item.Username, ct);
                BanCheckUiResult uiResult;
                string detail;

                if (first.State == UserSearchState.Found)
                {
                    _store.WriteDone(_sourcePath, item.SourceRow);
                    alive++;
                    uiResult = BanCheckUiResult.Alive;
                    detail = "SỐNG · đã ghi done · tìm thấy chính xác @" + NormalizeUser(item.Username);
                    UpdateItemNote(item.SourceRow, "done");
                    _log($"[BAN_CHECK_ALIVE_CONFIRMED] row={item.SourceRow} account={item.Username} result=ALIVE note=done first={first.Detail}");
                }
                else if (first.State == UserSearchState.NotFound)
                {
                    // Một lần Search hợp lệ là đủ: SearchUserExactAsync chỉ trả NotFound
                    // sau khi trang Search Người dùng đã load và danh sách đã ổn định.
                    _store.WriteBan(_sourcePath, item.SourceRow);
                    banned++;
                    uiResult = BanCheckUiResult.Ban;
                    detail = "BAN · không có exact username trong lượt tìm hợp lệ";
                    UpdateItemNote(item.SourceRow, "ban");
                    _log($"[BAN_CHECK_CONFIRMED] row={item.SourceRow} account={item.Username} result=BAN note=ban probe={first.Detail}");
                }
                else
                {
                    unknown++;
                    uiResult = BanCheckUiResult.Unknown;
                    detail = "KHÔNG RÕ · " + first.Detail;
                }

                processed++;
                UpdateRowResult(item.SourceRow, uiResult, detail);
                _status.Text = $"Đã kiểm tra: {persistedChecked + processed}/{_items.Count} | Sống: {alive} | BAN: {banned} | Không rõ: {unknown}";
                _log($"[BAN_CHECK_RESULT] row={item.SourceRow} account={item.Username} result={uiResult} detail={detail}");
                await Task.Delay(450, ct);
            }

            _log($"[BAN_CHECK_DONE] checked={processed} alive={alive} ban={banned} unknown={unknown}");
        }
        catch (OperationCanceledException)
        {
            _log($"[BAN_CHECK_STOPPED] checked={processed} alive={alive} ban={banned} unknown={unknown}");
        }
        catch (Exception ex)
        {
            _log("[BAN_CHECK_ERROR] " + ex);
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, "CHECK BAN lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _running = false;
            if (!IsDisposed)
                SetUiRunning(false);
            _runningChanged(false);
        }
    }

    void Stop(string reason)
    {
        if (!_running) return;
        _log("[BAN_CHECK_CANCEL] " + reason);
        try { _runCts?.Cancel(); } catch { }
    }

    void SetUiRunning(bool running)
    {
        _openExcel.Enabled = !running;
        _reload.Enabled = !running;
        _start.Enabled = !running;
        _stop.Enabled = running;
        _close.Enabled = !running;
    }

    void MarkChecking(int sourceRow)
    {
        var row = FindGridRow(sourceRow);
        if (row is null) return;
        row.Cells["result"].Value = "Đang tìm...";
        row.Cells["result"].Style.BackColor = Color.FromArgb(255, 248, 225);
        row.Cells["result"].Style.ForeColor = Color.FromArgb(145, 94, 0);
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells["user"];
        try { _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 2); } catch { }
    }

    void UpdateRowResult(int sourceRow, BanCheckUiResult result, string detail)
    {
        var row = FindGridRow(sourceRow);
        if (row is null) return;
        row.Cells["result"].Value = detail;
        if (result == BanCheckUiResult.Ban)
            row.Cells["note"].Value = "ban";
        else if (result == BanCheckUiResult.Alive)
            row.Cells["note"].Value = "done";
        ApplyResultStyle(row, result);
    }

    void ApplyResultStyle(DataGridViewRow row, BanCheckUiResult result)
    {
        var cell = row.Cells["result"];
        switch (result)
        {
            case BanCheckUiResult.Alive:
                cell.Style.BackColor = Color.Honeydew;
                cell.Style.ForeColor = Color.DarkGreen;
                break;
            case BanCheckUiResult.Ban:
                cell.Style.BackColor = Color.MistyRose;
                cell.Style.ForeColor = Color.Firebrick;
                cell.Style.Font = new Font(_grid.Font, FontStyle.Bold);
                row.Cells["note"].Style.ForeColor = Color.Firebrick;
                row.Cells["note"].Style.Font = new Font(_grid.Font, FontStyle.Bold);
                break;
            default:
                cell.Style.BackColor = Color.FromArgb(245, 245, 245);
                cell.Style.ForeColor = Color.DimGray;
                break;
        }
    }

    DataGridViewRow? FindGridRow(int sourceRow)
        => _grid.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(r => r.Tag is int row && row == sourceRow);

    void UpdateItemNote(int sourceRow, string note)
    {
        var index = _items.FindIndex(x => x.SourceRow == sourceRow);
        if (index >= 0)
            _items[index] = _items[index] with { Note = note };
    }

    static bool IsBan(string? note)
        => (note ?? "").Trim().Equals("ban", StringComparison.OrdinalIgnoreCase);

    static bool IsDone(string? note)
        => (note ?? "").Trim().Equals("done", StringComparison.OrdinalIgnoreCase);

    static bool IsFinalNote(string? note)
        => IsBan(note) || IsDone(note);

    static string NormalizeUser(string? value)
        => (value ?? "").Trim().TrimStart('@').ToLowerInvariant();

    enum BanCheckUiResult
    {
        Alive,
        Ban,
        Unknown
    }
}
