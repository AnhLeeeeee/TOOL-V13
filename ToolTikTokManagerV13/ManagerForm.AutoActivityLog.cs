using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoActivityLogEntry
    {
        public DateTime TimeLocal { get; set; } = DateTime.Now;
        public string Action { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Account { get; set; } = "";
        public string Reason { get; set; } = "";
        public string ReplacementProfile { get; set; } = "";
        public string Result { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    readonly SemaphoreSlim _autoActivityLogGate = new(1, 1);

    string AutoActivityLogPath
        => Path.Combine(_baseDir, "logs", "auto_close_replace.jsonl");

    string ResolveAutoActivityAccount(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return "";

        try
        {
            if (_dashboardAccountCache.TryGetValue(profileName, out var cached)
                && !string.IsNullOrWhiteSpace(cached)
                && cached != "—")
            {
                return cached.Trim();
            }
        }
        catch { }

        return "";
    }

    void WriteAutoActivityLog(
        string action,
        string profile = "",
        string account = "",
        string reason = "",
        string replacementProfile = "",
        string result = "",
        string detail = "")
    {
        var entry = new AutoActivityLogEntry
        {
            TimeLocal = DateTime.Now,
            Action = (action ?? "").Trim(),
            Profile = (profile ?? "").Trim(),
            Account = (account ?? "").Trim(),
            Reason = (reason ?? "").Trim(),
            ReplacementProfile = (replacementProfile ?? "").Trim(),
            Result = (result ?? "").Trim(),
            Detail = CompactAutoActivityText(detail, 600)
        };

        // Ghi file ở background để không giữ UI Manager.
        _ = Task.Run(async () =>
        {
            await _autoActivityLogGate.WaitAsync();
            try
            {
                var dir = Path.GetDirectoryName(AutoActivityLogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(entry);
                await File.AppendAllTextAsync(
                    AutoActivityLogPath,
                    json + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try { _log.Warn("[AUTO_ACTIVITY_LOG_WRITE] " + ex.Message); } catch { }
            }
            finally
            {
                _autoActivityLogGate.Release();
            }
        });
    }

    static string CompactAutoActivityText(string? value, int maxLength)
    {
        value = (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }

    async Task<List<AutoActivityLogEntry>> ReadAutoActivityLogAsync()
    {
        return await Task.Run(async () =>
        {
            await _autoActivityLogGate.WaitAsync();
            try
            {
                if (!File.Exists(AutoActivityLogPath))
                    return new List<AutoActivityLogEntry>();

                // Giữ phần cuối file để cửa sổ vẫn nhẹ dù log đã lớn.
                var tail = new Queue<string>(capacity: 2000);

                foreach (var line in File.ReadLines(AutoActivityLogPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (tail.Count >= 2000)
                        tail.Dequeue();

                    tail.Enqueue(line);
                }

                var result = new List<AutoActivityLogEntry>(tail.Count);

                foreach (var line in tail)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<AutoActivityLogEntry>(line);
                        if (entry is not null)
                            result.Add(entry);
                    }
                    catch { }
                }

                return result
                    .OrderByDescending(x => x.TimeLocal)
                    .ToList();
            }
            finally
            {
                _autoActivityLogGate.Release();
            }
        });
    }

    async Task ClearAutoActivityLogAsync()
    {
        await Task.Run(async () =>
        {
            await _autoActivityLogGate.WaitAsync();
            try
            {
                var dir = Path.GetDirectoryName(AutoActivityLogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(
                    AutoActivityLogPath,
                    "",
                    new UTF8Encoding(false));
            }
            finally
            {
                _autoActivityLogGate.Release();
            }
        });
    }

    sealed class AutoActivityCycle
    {
        public DateTime StartedAt { get; set; }
        public string OldProfile { get; set; } = "";
        public string OldAccount { get; set; } = "";
        public string Reason { get; set; } = "";
        public string NewProfile { get; set; } = "";
        public string NewAccount { get; set; } = "";
        public string Status { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<AutoActivityLogEntry> Events { get; } = new();
    }

    static List<AutoActivityCycle> BuildAutoActivityCycles(
        IEnumerable<AutoActivityLogEntry> source)
    {
        var ordered = source
            .OrderBy(x => x.TimeLocal)
            .ToList();

        var cycles = new List<AutoActivityCycle>();
        var currentByProfile = new Dictionary<string, AutoActivityCycle>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ordered)
        {
            var action = (entry.Action ?? "").Trim();
            var result = (entry.Result ?? "").Trim();
            var profile = (entry.Profile ?? "").Trim();
            var reason = (entry.Reason ?? "").Trim();

            AutoActivityCycle? cycle = null;

            // Mỗi "TỰ ĐÓNG | BẮT ĐẦU" mở một chu trình mới.
            if (profile.Length > 0
                && action.Equals("TỰ ĐÓNG", StringComparison.OrdinalIgnoreCase)
                && result.Contains("BẮT ĐẦU", StringComparison.OrdinalIgnoreCase))
            {
                cycle = new AutoActivityCycle
                {
                    StartedAt = entry.TimeLocal,
                    OldProfile = profile,
                    OldAccount = (entry.Account ?? "").Trim(),
                    Reason = reason
                };

                cycles.Add(cycle);
                currentByProfile[profile] = cycle;
            }
            else if (profile.Length > 0
                     && currentByProfile.TryGetValue(profile, out var existing))
            {
                cycle = existing;
            }

            // Tương thích log cũ/entry đơn lẻ.
            if (cycle is null)
            {
                cycle = new AutoActivityCycle
                {
                    StartedAt = entry.TimeLocal,
                    OldProfile = profile,
                    Reason = reason
                };

                cycles.Add(cycle);

                if (profile.Length > 0)
                    currentByProfile[profile] = cycle;
            }

            cycle.Events.Add(entry);

            if (cycle.OldProfile.Length == 0 && profile.Length > 0)
                cycle.OldProfile = profile;

            if (cycle.Reason.Length == 0 && reason.Length > 0)
                cycle.Reason = reason;

            if ((action.Equals("TỰ ĐÓNG", StringComparison.OrdinalIgnoreCase)
                 || action.Equals("SUẤT BÙ", StringComparison.OrdinalIgnoreCase)
                 || action.Equals("GHI EXCEL BAN", StringComparison.OrdinalIgnoreCase))
                && cycle.OldAccount.Length == 0
                && !string.IsNullOrWhiteSpace(entry.Account))
            {
                cycle.OldAccount = entry.Account.Trim();
            }

            if (action.Equals("MỞ PROFILE BÙ", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(entry.ReplacementProfile))
                    cycle.NewProfile = entry.ReplacementProfile.Trim();

                if (!string.IsNullOrWhiteSpace(entry.Account))
                    cycle.NewAccount = entry.Account.Trim();
            }

            UpdateAutoActivityCyclePresentation(cycle);
        }

        return cycles
            .OrderByDescending(x => x.StartedAt)
            .ToList();
    }

    static void UpdateAutoActivityCyclePresentation(AutoActivityCycle cycle)
    {
        var replacementSuccess = cycle.Events
            .Where(x =>
                x.Action.Equals("MỞ PROFILE BÙ", StringComparison.OrdinalIgnoreCase)
                && x.Result.Contains("THÀNH CÔNG", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.TimeLocal)
            .LastOrDefault();

        var latestRetry = cycle.Events
            .Where(x => x.Result.Contains("RETRY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.TimeLocal)
            .LastOrDefault();

        var latestError = cycle.Events
            .Where(x =>
                x.Result.Contains("LỖI", StringComparison.OrdinalIgnoreCase)
                || x.Result.Contains("THẤT BẠI", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.TimeLocal)
            .LastOrDefault();

        var closeSuccess = cycle.Events
            .Where(x =>
                x.Action.Equals("TỰ ĐÓNG", StringComparison.OrdinalIgnoreCase)
                && x.Result.Contains("THÀNH CÔNG", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.TimeLocal)
            .LastOrDefault();

        if (replacementSuccess is not null)
        {
            cycle.Status = "HOÀN TẤT";
            cycle.Summary =
                $"Đóng {cycle.OldProfile}"
                + (cycle.NewProfile.Length > 0 ? $" → tạo {cycle.NewProfile}" : "")
                + " → RUNNING khỏe";
            return;
        }

        if (latestRetry is not null)
        {
            cycle.Status = "RETRY";
            cycle.Summary = latestRetry.Detail;
            return;
        }

        if (latestError is not null)
        {
            cycle.Status = "LỖI";
            cycle.Summary = latestError.Detail;
            return;
        }

        if (cycle.Events.Any(x =>
                x.Action.Equals("MỞ PROFILE BÙ", StringComparison.OrdinalIgnoreCase)))
        {
            cycle.Status = "ĐANG MỞ BÙ";
            cycle.Summary = cycle.NewProfile.Length > 0
                ? $"Đang xử lý profile mới {cycle.NewProfile}"
                : "Đang tạo profile mới";
            return;
        }

        if (cycle.Events.Any(x =>
                x.Action.Equals("SUẤT BÙ", StringComparison.OrdinalIgnoreCase)))
        {
            cycle.Status = "ĐANG BÙ";
            cycle.Summary = "Đã xếp suất bù, đang chờ tạo profile mới";
            return;
        }

        if (closeSuccess is not null)
        {
            cycle.Status = "ĐÃ ĐÓNG";
            cycle.Summary = "Đã đóng profile, đang chờ bước tiếp theo";
            return;
        }

        cycle.Status = "ĐANG ĐÓNG";
        cycle.Summary = cycle.Events
            .OrderBy(x => x.TimeLocal)
            .LastOrDefault()?.Detail ?? "Đang xử lý";
    }

    static string FormatAutoActivityTimeline(AutoActivityCycle cycle)
    {
        var sb = new StringBuilder();

        sb.Append("Profile ");
        sb.Append(cycle.OldProfile);

        if (cycle.NewProfile.Length > 0)
        {
            sb.Append("  →  Profile ");
            sb.Append(cycle.NewProfile);
        }

        sb.AppendLine();
        sb.Append("Lý do: ");
        sb.Append(cycle.Reason);
        sb.Append("    |    Trạng thái: ");
        sb.AppendLine(cycle.Status);

        if (cycle.OldAccount.Length > 0 || cycle.NewAccount.Length > 0)
        {
            sb.Append("TK cũ: ");
            sb.Append(cycle.OldAccount);

            if (cycle.NewAccount.Length > 0)
            {
                sb.Append("    |    TK mới: ");
                sb.Append(cycle.NewAccount);
            }

            sb.AppendLine();
        }

        sb.AppendLine(new string('-', 90));

        foreach (var ev in cycle.Events.OrderBy(x => x.TimeLocal))
        {
            sb.Append(ev.TimeLocal.ToString("HH:mm:ss"));
            sb.Append("  ");
            sb.Append((ev.Action ?? "").PadRight(18));
            sb.Append("  ");
            sb.Append(ev.Result ?? "");

            var extras = new List<string>();

            if (!string.IsNullOrWhiteSpace(ev.ReplacementProfile))
                extras.Add("profile bù=" + ev.ReplacementProfile.Trim());

            if (!string.IsNullOrWhiteSpace(ev.Account)
                && !ev.Account.Equals(
                    cycle.OldAccount,
                    StringComparison.OrdinalIgnoreCase))
            {
                extras.Add("account=" + ev.Account.Trim());
            }

            if (!string.IsNullOrWhiteSpace(ev.Detail))
                extras.Add(ev.Detail.Trim());

            if (extras.Count > 0)
            {
                sb.Append("  |  ");
                sb.Append(string.Join(" | ", extras));
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    void ShowAutoActivityLogDialog(IWin32Window? owner = null)
    {
        var form = new Form
        {
            Text = $"Nhật ký Tự động & Tự bù — {AppVersionInfo.Display}",
            StartPosition = FormStartPosition.CenterParent,
            Width = 1220,
            Height = 720,
            MinimumSize = new Size(950, 560),
            BackColor = UiTheme.Canvas,
            Font = new Font("Segoe UI", 9F)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190F));

        var top = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 8),
            BackColor = UiTheme.Card
        };

        var title = new Label
        {
            Text = "NHẬT KÝ TỰ ĐỘNG & TỰ BÙ",
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Location = new Point(12, 8)
        };

        var summary = new Label
        {
            Text = "Đang tải...",
            AutoSize = true,
            ForeColor = Color.FromArgb(70, 82, 96),
            Location = new Point(14, 35)
        };

        var refresh = new Button
        {
            Text = "Làm mới",
            Width = 95,
            Height = 32
        };

        var clear = new Button
        {
            Text = "Xóa nhật ký",
            Width = 110,
            Height = 32
        };

        var close = new Button
        {
            Text = "Đóng",
            Width = 90,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        void LayoutTopButtons()
        {
            var right = Math.Max(360, top.ClientSize.Width - 12);

            close.Left = right - close.Width;
            clear.Left = close.Left - 8 - clear.Width;
            refresh.Left = clear.Left - 8 - refresh.Width;

            close.Top = clear.Top = refresh.Top = 15;
        }

        top.Controls.Add(title);
        top.Controls.Add(summary);
        top.Controls.Add(refresh);
        top.Controls.Add(clear);
        top.Controls.Add(close);
        top.Resize += (_, _) => LayoutTopButtons();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 31 }
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 239, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(35, 63, 98);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(215, 232, 252);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 50, 75);

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Time",
            HeaderText = "Thời gian",
            Width = 145
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "OldProfile",
            HeaderText = "Profile cũ",
            Width = 90
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "OldAccount",
            HeaderText = "TK cũ",
            Width = 155
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Reason",
            HeaderText = "Lý do",
            Width = 100
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "NewProfile",
            HeaderText = "Profile mới",
            Width = 95
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "NewAccount",
            HeaderText = "TK mới",
            Width = 155
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "Trạng thái",
            Width = 125
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Summary",
            HeaderText = "Tóm tắt",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 250
        });

        var detailGroup = new GroupBox
        {
            Text = "Chi tiết chu trình",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Margin = new Padding(8, 4, 8, 8)
        };

        var detail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        detailGroup.Controls.Add(detail);

        void ShowSelectedCycleDetail()
        {
            if (grid.SelectedRows.Count == 0
                || grid.SelectedRows[0].Tag is not AutoActivityCycle cycle)
            {
                detail.Text = "";
                return;
            }

            detail.Text = FormatAutoActivityTimeline(cycle);
        }

        grid.SelectionChanged += (_, _) => ShowSelectedCycleDetail();

        async Task ReloadAsync()
        {
            refresh.Enabled = false;

            try
            {
                var items = await ReadAutoActivityLogAsync();
                var cycles = BuildAutoActivityCycles(items);

                grid.SuspendLayout();
                try
                {
                    grid.Rows.Clear();

                    foreach (var cycle in cycles)
                    {
                        var rowIndex = grid.Rows.Add(
                            cycle.StartedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                            cycle.OldProfile,
                            cycle.OldAccount,
                            cycle.Reason,
                            cycle.NewProfile,
                            cycle.NewAccount,
                            cycle.Status,
                            cycle.Summary);

                        var row = grid.Rows[rowIndex];
                        row.Tag = cycle;

                        if (cycle.Status.Contains("LỖI", StringComparison.OrdinalIgnoreCase))
                        {
                            row.DefaultCellStyle.BackColor = Color.MistyRose;
                            row.DefaultCellStyle.ForeColor = Color.Firebrick;
                        }
                        else if (cycle.Status.Contains("HOÀN TẤT", StringComparison.OrdinalIgnoreCase)
                                 || cycle.Status.Contains("ĐÃ ĐÓNG", StringComparison.OrdinalIgnoreCase))
                        {
                            row.DefaultCellStyle.BackColor = Color.Honeydew;
                        }
                        else
                        {
                            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 250, 232);
                        }
                    }
                }
                finally
                {
                    grid.ResumeLayout();
                }

                summary.Text = cycles.Count == 0
                    ? "Chưa có chu trình Tự đóng/Tự bù."
                    : $"Hiển thị {cycles.Count} chu trình — 1 chu trình = 1 dòng, mới nhất ở trên.";

                if (grid.Rows.Count > 0)
                {
                    grid.Rows[0].Selected = true;
                    grid.CurrentCell = grid.Rows[0].Cells["Time"];
                }
                else
                {
                    detail.Text = "";
                }

                ShowSelectedCycleDetail();
            }
            catch (Exception ex)
            {
                summary.Text = "Không đọc được nhật ký: " + ex.Message;
                detail.Text = ex.ToString();
            }
            finally
            {
                refresh.Enabled = true;
            }
        }

        refresh.Click += async (_, _) => await ReloadAsync();

        clear.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(
                form,
                "Xóa toàn bộ Nhật ký Tự đóng & Tự bù hiện có?",
                "Xóa nhật ký",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
                return;

            clear.Enabled = false;

            try
            {
                await ClearAutoActivityLogAsync();
                await ReloadAsync();
            }
            finally
            {
                clear.Enabled = true;
            }
        };

        root.Controls.Add(top, 0, 0);
        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(detailGroup, 0, 2);

        form.Controls.Add(root);
        form.CancelButton = close;

        UiTheme.Apply(form);
        UiTheme.StyleButton(refresh, UiButtonKind.Neutral);
        UiTheme.StyleButton(clear, UiButtonKind.Danger);
        UiTheme.StyleButton(close, UiButtonKind.Neutral);

        form.Shown += async (_, _) =>
        {
            LayoutTopButtons();
            await ReloadAsync();
        };

        if (owner is null)
            form.ShowDialog(this);
        else
            form.ShowDialog(owner);
    }
}
