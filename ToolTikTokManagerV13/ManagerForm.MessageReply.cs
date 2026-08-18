using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class MessageReplyToolState
    {
        public string MessagesText { get; set; } = "";
        public bool AcceptRequests { get; set; } = true;
        public bool ReplyAfterAccept { get; set; } = true;
        public bool SkipAlreadyReplied { get; set; } = true;
        public bool OnlyInitialRequests { get; set; } = true;
        public decimal DelayMinSeconds { get; set; } = 1.5M;
        public decimal DelayMaxSeconds { get; set; } = 3.5M;
        public int RetryCount { get; set; } = 2;

        // Chế độ tự động xen giữa LIVE. Khoảng hỗ trợ cố định: 30p / 1h / 2h / 4h.
        public bool AutoEnabled { get; set; }
        public int AutoIntervalMinutes { get; set; } = 60;
        public Dictionary<string, MessageReplyAutoStats> AutoStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class MessageReplyAutoStats
    {
        public int CheckRuns { get; set; }
        public int ReplyRuns { get; set; }
        public int TotalReplied { get; set; }
        public int LastReplied { get; set; }
        public int LastFailed { get; set; }
        public DateTime LastRunUtc { get; set; }
        public string LastResult { get; set; } = "";
    }

    sealed class MessageReplyStatusReply
    {
        public bool Running { get; set; }
        public string Stage { get; set; } = "";
        public int RequestsFound { get; set; }
        public int Processed { get; set; }
        public int Accepted { get; set; }
        public int Replied { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string CurrentUser { get; set; } = "";
        public string Message { get; set; } = "";
        public bool Completed { get; set; }
        public bool Cancelled { get; set; }
    }

    readonly HashSet<string> _messageReplyProfilesInFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoMessageReplyNextRunUtc = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _autoMessageReplyQueueGate = new(1, 1);
    string MessageReplyToolStatePath => Path.Combine(_baseDir, "tiktok_message_reply_tool.json");

    MessageReplyToolState LoadMessageReplyToolState()
    {
        try
        {
            if (!File.Exists(MessageReplyToolStatePath)) return new MessageReplyToolState();
            var state = JsonSerializer.Deserialize<MessageReplyToolState>(File.ReadAllText(MessageReplyToolStatePath)) ?? new MessageReplyToolState();
            state.AutoIntervalMinutes = NormalizeMessageReplyInterval(state.AutoIntervalMinutes);
            state.AutoStats = new Dictionary<string, MessageReplyAutoStats>(state.AutoStats ?? new(), StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _log.Warn("[MESSAGE_REPLY_STATE_LOAD] " + ex.Message);
            return new MessageReplyToolState();
        }
    }

    void SaveMessageReplyToolState(MessageReplyToolState state)
    {
        try
        {
            File.WriteAllText(MessageReplyToolStatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.Warn("[MESSAGE_REPLY_STATE_SAVE] " + ex.Message); }
    }

    static int NormalizeMessageReplyInterval(int minutes)
        => minutes switch { 30 => 30, 60 => 60, 120 => 120, 240 => 240, _ => 60 };

    static string BuildAutoReplyNote(string profileName, MessageReplyToolState state)
    {
        if (!state.AutoStats.TryGetValue(profileName, out var stats) || stats.CheckRuns <= 0)
            return "Chưa có lần tự động";
        var local = stats.LastRunUtc == default ? "—" : stats.LastRunUtc.ToLocalTime().ToString("dd/MM HH:mm");
        return $"Đã rep {stats.ReplyRuns} lần | Lần #{stats.CheckRuns}: {stats.LastReplied} tin | Tổng {stats.TotalReplied} tin | {local}";
    }

    static string[] ParseSharedReplyMessages(string text)
    {
        // Một dòng chỉ chứa --- là ranh giới giữa hai tin nhắn riêng biệt.
        // Những dòng nằm trong cùng một khối được giữ nguyên xuống dòng.
        var result = new List<string>();
        var current = new List<string>();
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                var message = string.Join(Environment.NewLine, current).Trim();
                if (message.Length > 0) result.Add(message);
                current.Clear();
                continue;
            }
            current.Add(line);
        }
        var last = string.Join(Environment.NewLine, current).Trim();
        if (last.Length > 0) result.Add(last);
        return result.ToArray();
    }

    void ShowTikTokMessageReplyDialog()
    {
        const string useColumn = "Use";
        const string profileColumn = "Profile";
        const string resultColumn = "Result";
        const string noteColumn = "AutoNote";

        var state = LoadMessageReplyToolState();
        var contexts = _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder).ToList();
        using var form = new Form
        {
            Text = "Tin nhắn TikTok",
            Width = 1040,
            Height = 835,
            MinimumSize = new Size(940, 700),
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true
        };
        ModernDialog.Apply(form, fixedDialog: false);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = ModernDialog.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // Giảm chiều cao cấu hình để ưu tiên danh sách tài khoản trên màn hình thấp.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            Text = "Có 2 chế độ: Chạy xử lý thủ công, hoặc Tự động xen giữa LIVE. Ở chế độ tự động, đến mốc 30 phút / 1 giờ / 2 giờ / 4 giờ, profile đang chạy LIVE sẽ tạm dừng, xử lý HẾT yêu cầu tin nhắn đang chờ, quay lại đúng trang trước đó rồi tiếp tục LIVE. Nếu phần Tin nhắn gặp lỗi, tool bỏ qua lỗi và ưu tiên quay lại luồng LIVE.",
            ForeColor = Color.FromArgb(55, 76, 103),
            Margin = new Padding(0, 0, 0, 10)
        };

        var config = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            Margin = new Padding(0, 0, 0, 10)
        };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        config.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        config.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        config.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        config.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        config.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var contentLabel = new Label { Text = "Nội dung dùng chung", AutoSize = true, Margin = new Padding(0, 8, 8, 4) };
        var messages = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Dock = DockStyle.Fill,
            Text = state.MessagesText
        };
        ModernDialog.StyleTextInput(messages);
        var contentHint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Một dòng chỉ chứa  ---  = sang tin nhắn mới cho CÙNG một người. Ví dụ:  Chào bạn  /  ---  /  Mình đây ạ  → gửi thành 2 tin riêng.",
            Margin = new Padding(0, 4, 0, 8)
        };

        var acceptRequests = new CheckBox { Text = "Chấp nhận yêu cầu", Checked = state.AcceptRequests, AutoSize = true };
        var replyAfterAccept = new CheckBox { Text = "Trả lời ngay sau khi chấp nhận", Checked = state.ReplyAfterAccept, AutoSize = true };
        var skipReplied = new CheckBox { Text = "Bỏ qua người đã trả lời", Checked = state.SkipAlreadyReplied, AutoSize = true };
        var onlyInitial = new CheckBox { Text = "Chỉ xử lý yêu cầu có lúc bấm Chạy", Checked = state.OnlyInitialRequests, AutoSize = true };

        var optionsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        optionsPanel.Controls.Add(acceptRequests);
        optionsPanel.Controls.Add(replyAfterAccept);
        optionsPanel.Controls.Add(skipReplied);
        optionsPanel.Controls.Add(onlyInitial);

        NumericUpDown DelayBox(decimal value) => new()
        {
            Minimum = 0,
            Maximum = 60,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Value = Math.Clamp(value, 0M, 60M),
            Width = 74
        };
        var delayMin = DelayBox(state.DelayMinSeconds);
        var delayMax = DelayBox(state.DelayMaxSeconds);
        var retry = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 5,
            Value = Math.Clamp(state.RetryCount, 1, 5),
            Width = 60
        };
        var timingPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        timingPanel.Controls.Add(new Label { Text = "Delay từ", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        timingPanel.Controls.Add(delayMin);
        timingPanel.Controls.Add(new Label { Text = "đến", AutoSize = true, Margin = new Padding(8, 7, 4, 0) });
        timingPanel.Controls.Add(delayMax);
        timingPanel.Controls.Add(new Label { Text = "giây", AutoSize = true, Margin = new Padding(4, 7, 14, 0) });
        timingPanel.Controls.Add(new Label { Text = "Retry", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        timingPanel.Controls.Add(retry);

        var autoEnabled = new CheckBox
        {
            Text = "Tự động xen giữa luồng LIVE",
            Checked = state.AutoEnabled,
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0)
        };
        var autoInterval = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 105,
            Margin = new Padding(0, 2, 12, 0)
        };
        autoInterval.Items.AddRange(new object[] { "30 phút", "1 giờ", "2 giờ", "4 giờ" });
        autoInterval.SelectedIndex = NormalizeMessageReplyInterval(state.AutoIntervalMinutes) switch
        {
            30 => 0,
            60 => 1,
            120 => 2,
            240 => 3,
            _ => 1
        };
        var autoHint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Đến giờ: Pause LIVE → xử lý đến khi hết yêu cầu → quay lại trang LIVE → Resume. Mọi lỗi Tin nhắn đều fail-open về LIVE.",
            Margin = new Padding(0, 7, 0, 0)
        };
        var autoPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        autoPanel.Controls.Add(autoEnabled);
        autoPanel.Controls.Add(autoInterval);
        autoPanel.Controls.Add(autoHint);

        config.Controls.Add(contentLabel, 0, 0);
        config.Controls.Add(messages, 1, 0);
        config.SetColumnSpan(messages, 3);
        config.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 1);
        config.Controls.Add(contentHint, 1, 1);
        config.SetColumnSpan(contentHint, 3);
        config.Controls.Add(new Label { Text = "Tùy chọn", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, 2);
        config.Controls.Add(optionsPanel, 1, 2);
        config.SetColumnSpan(optionsPanel, 3);
        config.Controls.Add(new Label { Text = "Nhịp xử lý", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, 3);
        config.Controls.Add(timingPanel, 1, 3);
        config.SetColumnSpan(timingPanel, 3);
        config.Controls.Add(new Label { Text = "Tự động LIVE", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, 4);
        config.Controls.Add(autoPanel, 1, 4);
        config.SetColumnSpan(autoPanel, 3);

        var status = new Label
        {
            AutoSize = true,
            Text = "Trạng thái: Chưa chạy",
            ForeColor = Color.FromArgb(55, 76, 103),
            Margin = new Padding(0, 0, 0, 8)
        };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnHeadersHeight = 36
        };
        grid.RowTemplate.Height = 38;
        grid.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = useColumn,
            HeaderText = "Chọn",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 72,
            MinimumWidth = 72
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = profileColumn,
            HeaderText = "Profile",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 125,
            MinimumWidth = 100
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = resultColumn, HeaderText = "Trạng thái / kết quả", ReadOnly = true, FillWeight = 45, MinimumWidth = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = noteColumn, HeaderText = "Ghi chú tự động", ReadOnly = true, FillWeight = 55, MinimumWidth = 220 });
        var selectedContext = SelectedContext();
        foreach (var ctx in contexts)
        {
            var index = grid.Rows.Add(ReferenceEquals(ctx, selectedContext), ctx.Profile.Name, "Chưa chạy", BuildAutoReplyNote(ctx.Profile.Name, state));
            grid.Rows[index].Tag = ctx;
        }
        LogGridSchema(grid, "TikTokMessageReplyGrid", useColumn, profileColumn, resultColumn, noteColumn);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Point(14, 5)
        };
        var accountsTab = new TabPage("Tài khoản") { BackColor = ModernDialog.Canvas, Padding = new Padding(6) };
        var journalTab = new TabPage("Nhật ký") { BackColor = ModernDialog.Canvas, Padding = new Padding(6) };

        // Thanh chọn tài khoản đặt ngay trên danh sách để màn hình nhỏ vẫn dễ thao tác.
        var selectAll = new Button { Text = "Chọn tất", Width = 92, Height = 32 };
        var clearAll = new Button { Text = "Bỏ chọn", Width = 92, Height = 32 };
        var invertSelection = new Button { Text = "Đảo chọn", Width = 92, Height = 32 };
        ModernDialog.StyleSecondaryButton(selectAll);
        ModernDialog.StyleSecondaryButton(clearAll);
        ModernDialog.StyleSecondaryButton(invertSelection);
        var selectionSummary = new Label
        {
            AutoSize = true,
            Text = "Đã chọn: 0/0",
            ForeColor = Color.FromArgb(55, 76, 103),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(12, 8, 0, 0)
        };
        var accountHint = new Label
        {
            AutoSize = true,
            Text = "Bấm vào bất kỳ chỗ nào trên một dòng để chọn / bỏ chọn.",
            ForeColor = Color.DimGray,
            Margin = new Padding(12, 8, 0, 0)
        };
        var accountToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        accountToolbar.Controls.Add(selectAll);
        accountToolbar.Controls.Add(clearAll);
        accountToolbar.Controls.Add(invertSelection);
        accountToolbar.Controls.Add(selectionSummary);
        accountToolbar.Controls.Add(accountHint);

        var accountRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        accountRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        accountRoot.Controls.Add(accountToolbar, 0, 0);
        accountRoot.Controls.Add(grid, 0, 1);
        accountsTab.Controls.Add(accountRoot);

        var journalRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        journalRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        journalRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var journalBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F),
            ScrollBars = RichTextBoxScrollBars.Both
        };
        var journalButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        var copyJournal = new Button { Text = "Sao chép nhật ký", Width = 145, Height = 32 };
        var clearJournal = new Button { Text = "Xóa hiển thị", Width = 110, Height = 32 };
        ModernDialog.StyleSecondaryButton(copyJournal);
        ModernDialog.StyleSecondaryButton(clearJournal);
        journalButtons.Controls.Add(copyJournal);
        journalButtons.Controls.Add(clearJournal);
        journalRoot.Controls.Add(journalBox, 0, 0);
        journalRoot.Controls.Add(journalButtons, 0, 1);
        journalTab.Controls.Add(journalRoot);
        tabs.TabPages.Add(accountsTab);
        tabs.TabPages.Add(journalTab);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        var close = new Button { Text = "Đóng", Width = 100, Height = 40 };
        var stop = new Button { Text = "■ Dừng", Width = 110, Height = 40, Enabled = false };
        var start = new Button { Text = "▶ Chạy xử lý", Width = 145, Height = 40 };
        ModernDialog.StyleSecondaryButton(close);
        UiTheme.StyleButton(stop, UiButtonKind.Danger);
        ModernDialog.StylePrimaryButton(start);
        buttons.Controls.Add(close);
        buttons.Controls.Add(stop);
        buttons.Controls.Add(start);

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(config, 0, 1);
        root.Controls.Add(status, 0, 2);
        root.Controls.Add(tabs, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        form.Controls.Add(root);

        var running = false;
        var stopRequested = false;
        ProfileContext? currentContext = null;
        const int maxUiJournalLines = 500;
        var sessionJournal = new List<string>();
        var journalSeenByProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void RenderJournal()
        {
            if (journalBox.IsDisposed) return;
            journalBox.Lines = sessionJournal.ToArray();
            journalBox.SelectionStart = journalBox.TextLength;
            journalBox.ScrollToCaret();
            journalTab.Text = sessionJournal.Count > 0 ? $"Nhật ký ({sessionJournal.Count})" : "Nhật ký";
        }

        void AddJournalLine(string profile, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            sessionJournal.Add($"[{profile}] {line}");
            if (sessionJournal.Count > maxUiJournalLines)
                sessionJournal.RemoveRange(0, sessionJournal.Count - maxUiJournalLines);
            RenderJournal();
        }

        async Task RefreshJournalAsync(ProfileContext ctx)
        {
            try
            {
                var rawLog = await SendCommandAsync(ctx, "message_reply_log", TimeSpan.FromSeconds(5));
                var remoteLines = JsonSerializer.Deserialize<string[]>(rawLog) ?? Array.Empty<string>();
                journalSeenByProfile.TryGetValue(ctx.Profile.Name, out var seen);
                if (remoteLines.Length < seen) seen = 0; // Worker vừa bắt đầu phiên mới / journal được reset.
                for (var i = seen; i < remoteLines.Length; i++)
                    AddJournalLine(ctx.Profile.Name, remoteLines[i]);
                journalSeenByProfile[ctx.Profile.Name] = remoteLines.Length;
            }
            catch (Exception ex)
            {
                AddJournalLine(ctx.Profile.Name, $"{DateTime.Now:HH:mm:ss.fff} [JOURNAL_READ_ERROR] {ex.Message}");
            }
        }

        void SaveUiState()
        {
            // AutoStats có thể được scheduler cập nhật trong lúc dialog đang mở.
            // Luôn merge bản mới nhất trước khi lưu UI để không ghi đè thống kê nền.
            var latest = LoadMessageReplyToolState();
            state.AutoStats = latest.AutoStats;
            state.MessagesText = messages.Text;
            state.AcceptRequests = acceptRequests.Checked;
            state.ReplyAfterAccept = replyAfterAccept.Checked;
            state.SkipAlreadyReplied = skipReplied.Checked;
            state.OnlyInitialRequests = onlyInitial.Checked;
            state.DelayMinSeconds = delayMin.Value;
            state.DelayMaxSeconds = delayMax.Value;
            state.RetryCount = (int)retry.Value;
            state.AutoEnabled = autoEnabled.Checked;
            state.AutoIntervalMinutes = autoInterval.SelectedIndex switch
            {
                0 => 30,
                1 => 60,
                2 => 120,
                3 => 240,
                _ => 60
            };
            SaveMessageReplyToolState(state);
        }

        void SetRowResult(DataGridViewRow row, string text, Color? color = null)
        {
            TrySetGridCellValue(row, resultColumn, text, "ShowTikTokMessageReplyDialog.SetRowResult");
            if (color.HasValue && row.DataGridView is not null && !row.DataGridView.IsDisposed)
                row.DefaultCellStyle.ForeColor = color.Value;
        }

        string Summary(MessageReplyStatusReply x)
        {
            var who = string.IsNullOrWhiteSpace(x.CurrentUser) ? "—" : x.CurrentUser;
            return $"{x.Stage} | Yêu cầu {x.RequestsFound} | Đã xử lý {x.Processed} | Accept {x.Accepted} | Gửi {x.Replied} | Bỏ qua {x.Skipped} | Lỗi {x.Failed} | {who}";
        }

        copyJournal.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(journalBox.Text)) Clipboard.SetText(journalBox.Text);
        };
        clearJournal.Click += (_, _) =>
        {
            sessionJournal.Clear();
            RenderJournal();
        };

        autoEnabled.CheckedChanged += (_, _) =>
        {
            SaveUiState();
            _autoMessageReplyNextRunUtc.Clear();
            status.Text = autoEnabled.Checked
                ? $"Trạng thái: Đã bật tự động kiểm tra tin nhắn mỗi {autoInterval.Text}."
                : "Trạng thái: Đã tắt tự động Tin nhắn.";
        };
        autoInterval.SelectedIndexChanged += (_, _) =>
        {
            if (!form.Visible) return;
            SaveUiState();
            _autoMessageReplyNextRunUtc.Clear();
            if (autoEnabled.Checked)
                status.Text = $"Trạng thái: Đã đổi chu kỳ tự động thành {autoInterval.Text}.";
        };

        void UpdateSelectionSummary()
        {
            var total = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is ProfileContext);
            var selectedCount = grid.Rows.Cast<DataGridViewRow>().Count(r =>
                r.Tag is ProfileContext
                && Convert.ToBoolean(GetGridCellValueOrNull(r, useColumn, "ShowTikTokMessageReplyDialog.SelectionSummary") ?? false));
            selectionSummary.Text = $"Đã chọn: {selectedCount}/{total}";
        }

        selectAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                TrySetGridCellValue(row, useColumn, true, "ShowTikTokMessageReplyDialog.SelectAll");
            UpdateSelectionSummary();
        };
        clearAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                TrySetGridCellValue(row, useColumn, false, "ShowTikTokMessageReplyDialog.ClearAll");
            UpdateSelectionSummary();
        };
        invertSelection.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var current = Convert.ToBoolean(GetGridCellValueOrNull(row, useColumn, "ShowTikTokMessageReplyDialog.Invert") ?? false);
                TrySetGridCellValue(row, useColumn, !current, "ShowTikTokMessageReplyDialog.Invert");
            }
            UpdateSelectionSummary();
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns[useColumn].Index)
                UpdateSelectionSummary();
        };
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name == useColumn) return; // checkbox tự xử lý click của nó
            var row = grid.Rows[e.RowIndex];
            if (row.Tag is not ProfileContext) return;
            var current = Convert.ToBoolean(GetGridCellValueOrNull(row, useColumn, "ShowTikTokMessageReplyDialog.RowClick") ?? false);
            TrySetGridCellValue(row, useColumn, !current, "ShowTikTokMessageReplyDialog.RowClick");
            UpdateSelectionSummary();
        };
        UpdateSelectionSummary();

        close.Click += (_, _) => form.Close();
        stop.Click += async (_, _) =>
        {
            stopRequested = true;
            stop.Enabled = false;
            status.Text = "Trạng thái: Đang yêu cầu dừng...";
            var ctx = currentContext;
            if (ctx is not null)
            {
                try { await SendCommandAsync(ctx, "message_reply_stop", TimeSpan.FromSeconds(5)); } catch { }
            }
        };

        start.Click += async (_, _) =>
        {
            if (running) return;
            grid.EndEdit();
            var selected = grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is ProfileContext && Convert.ToBoolean(GetGridCellValueOrNull(r, useColumn, "ShowTikTokMessageReplyDialog.Start") ?? false))
                .ToList();
            if (selected.Count == 0)
            {
                ModernDialog.ShowMessage(form, "Hãy chọn ít nhất một profile.", "Tin nhắn TikTok", MessageBoxIcon.Information);
                return;
            }
            if (!acceptRequests.Checked)
            {
                ModernDialog.ShowMessage(form, "Bản này xử lý theo luồng Chấp nhận → trả lời ngay. Hãy bật 'Chấp nhận yêu cầu'. Bạn vẫn có thể tắt 'Trả lời ngay' nếu chỉ muốn Accept.", "Tin nhắn TikTok", MessageBoxIcon.Information);
                return;
            }
            var parsedMessages = ParseSharedReplyMessages(messages.Text);
            if (replyAfterAccept.Checked && parsedMessages.Length == 0)
            {
                ModernDialog.ShowMessage(form, "Danh sách nội dung đang trống. Nhập ít nhất một tin nhắn, hoặc tắt 'Trả lời ngay sau khi chấp nhận' để chỉ Accept.", "Tin nhắn TikTok", MessageBoxIcon.Warning);
                return;
            }
            if (delayMax.Value < delayMin.Value)
            {
                ModernDialog.ShowMessage(form, "Delay tối đa phải lớn hơn hoặc bằng delay tối thiểu.", "Tin nhắn TikTok", MessageBoxIcon.Warning);
                return;
            }

            SaveUiState();
            var preview = replyAfterAccept.Checked
                ? $"Mỗi người vừa Accept sẽ nhận {parsedMessages.Length} tin nhắn dùng chung. Dòng --- tách thành các tin riêng."
                : "Chế độ hiện tại chỉ Chấp nhận yêu cầu, không gửi trả lời.";
            if (ModernDialog.ShowConfirm(form,
                $"Sẽ xử lý {selected.Count} profile theo thứ tự.\n\n{preview}\n\nAutomation LIVE của profile sẽ được DỪNG trước khi xử lý Tin nhắn. Tiếp tục?",
                "Xác nhận xử lý Tin nhắn TikTok") != DialogResult.Yes) return;

            running = true;
            stopRequested = false;
            sessionJournal.Clear();
            journalSeenByProfile.Clear();
            RenderJournal();
            start.Enabled = false;
            close.Enabled = false;
            stop.Enabled = true;
            messages.Enabled = false;
            acceptRequests.Enabled = false;
            replyAfterAccept.Enabled = false;
            skipReplied.Enabled = false;
            onlyInitial.Enabled = false;
            delayMin.Enabled = false;
            delayMax.Enabled = false;
            retry.Enabled = false;
            autoEnabled.Enabled = false;
            autoInterval.Enabled = false;
            var successProfiles = 0;
            var failedProfiles = 0;

            try
            {
                foreach (var row in selected)
                {
                    if (stopRequested) break;
                    if (row.Tag is not ProfileContext ctx) continue;
                    currentContext = ctx;
                    if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name))
                        throw new InvalidOperationException("Profile đang có một phiên Tin nhắn tự động/thủ công khác. Hãy chờ phiên đó hoàn tất.");
                    _messageReplyProfilesInFlight.Add(ctx.Profile.Name);
                    AddJournalLine(ctx.Profile.Name, $"{DateTime.Now:HH:mm:ss.fff} [MANAGER] Bắt đầu chuẩn bị profile.");
                    SetRowResult(row, "Đang chuẩn bị...", Color.RoyalBlue);
                    status.Text = $"Trạng thái: Đang chuẩn bị {ctx.Profile.Name}...";
                    Application.DoEvents();

                    try
                    {
                        if (_autoIdentityInFlight.Contains(ctx.Profile.Name))
                            throw new InvalidOperationException("Profile đang được mục Tên & ảnh TikTok xử lý. Hãy chờ phần đó hoàn tất rồi chạy Tin nhắn.");
                        await OpenProfileAsync(ctx);
                        if (stopRequested) break;
                        try { await RefreshStatusAsync(ctx); } catch { }
                        var runState = GetLastConfirmedRuntimeState(ctx);
                        if (runState is "RUNNING" or "PAUSED")
                        {
                            try { await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(8)); } catch { }
                        }

                        if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
                        {
                            await OpenChromeForProfileAsync(ctx);
                            if (stopRequested) break;
                            try { await RefreshStatusAsync(ctx); } catch { }
                        }
                        if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Chrome chưa kết nối.");

                        if (stopRequested) break;
                        var ready = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(7));
                        if (!string.Equals(ready, "ready", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("TikTok chưa đăng nhập trên profile này.");

                        var requestJson = JsonSerializer.Serialize(new
                        {
                            Messages = parsedMessages,
                            AcceptRequests = acceptRequests.Checked,
                            ReplyAfterAccept = replyAfterAccept.Checked,
                            SkipAlreadyReplied = skipReplied.Checked,
                            OnlyInitialRequests = onlyInitial.Checked,
                            DelayMinMs = (int)Math.Round(delayMin.Value * 1000M),
                            DelayMaxMs = (int)Math.Round(delayMax.Value * 1000M),
                            RetryCount = (int)retry.Value
                        });
                        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson));
                        var startReply = await SendCommandAsync(ctx, "message_reply_start|" + payload, TimeSpan.FromSeconds(10));
                        if (!string.Equals(startReply, "started", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(startReply switch
                            {
                                "automation_running" => "Automation LIVE vẫn đang chạy.",
                                "chrome_not_connected" => "Chrome chưa kết nối.",
                                "not_logged_in" => "TikTok chưa đăng nhập.",
                                "already_running" => "Module Tin nhắn của profile này đang chạy.",
                                _ => "Worker không khởi động được module Tin nhắn: " + startReply
                            });

                        AddJournalLine(ctx.Profile.Name, $"{DateTime.Now:HH:mm:ss.fff} [MANAGER] Worker trả về started.");
                        await RefreshJournalAsync(ctx);

                        MessageReplyStatusReply? last = null;
                        var consecutiveStatusErrors = 0;
                        while (!stopRequested)
                        {
                            await Task.Delay(600);
                            string raw;
                            try
                            {
                                raw = await SendCommandAsync(ctx, "message_reply_status", TimeSpan.FromSeconds(5));
                                consecutiveStatusErrors = 0;
                            }
                            catch (Exception ex)
                            {
                                if (++consecutiveStatusErrors >= 4) throw new InvalidOperationException("Không đọc được trạng thái module Tin nhắn: " + ex.Message);
                                continue;
                            }
                            last = JsonSerializer.Deserialize<MessageReplyStatusReply>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (last is null) continue;
                            await RefreshJournalAsync(ctx);
                            var line = Summary(last);
                            SetRowResult(row, line, last.Failed > 0 ? Color.DarkOrange : Color.RoyalBlue);
                            status.Text = $"Trạng thái: {ctx.Profile.Name} | {line}";
                            if (last.Completed || !last.Running) break;
                        }

                        await RefreshJournalAsync(ctx);

                        if (stopRequested)
                        {
                            try { await SendCommandAsync(ctx, "message_reply_stop", TimeSpan.FromSeconds(5)); } catch { }
                            SetRowResult(row, "Đã yêu cầu dừng.", Color.DarkOrange);
                            break;
                        }

                        if (last is null) throw new InvalidOperationException("Worker không trả trạng thái kết thúc.");
                        if (string.Equals(last.Stage, "ERROR", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(last.Message);

                        // Stage ERROR / exception mới là lỗi cấp profile.
                        // Failed chỉ là số request con lỗi và không được biến một phiên đã hoàn tất thành "Profile có lỗi".
                        SetRowResult(row, Summary(last), last.Failed == 0 ? Color.DarkGreen : Color.DarkOrange);
                        successProfiles++;
                    }
                    catch (Exception ex)
                    {
                        failedProfiles++;
                        AddJournalLine(ctx.Profile.Name, $"{DateTime.Now:HH:mm:ss.fff} [MANAGER_ERROR] {ex.Message}");
                        SetRowResult(row, "Lỗi: " + ex.Message, Color.Firebrick);
                        _log.Warn($"[MESSAGE_REPLY_MANAGER] profile={ctx.Profile.Name} error={ex.Message}");
                    }
                    finally
                    {
                        _messageReplyProfilesInFlight.Remove(ctx.Profile.Name);
                    }
                }

                if (!form.IsDisposed && !stopRequested)
                {
                    // Không hiện hộp thoại modal khi kết thúc: kết quả đã có ngay trên bảng + Nhật ký.
                    // Điều này tránh việc chạy xong nhiều profile lại bị popup chặn thao tác.
                    status.Text = failedProfiles == 0
                        ? $"Trạng thái: Hoàn tất | Profile hoàn tất: {successProfiles} | Lỗi hệ thống: 0"
                        : $"Trạng thái: Hoàn tất | Profile hoàn tất: {successProfiles} | Lỗi hệ thống: {failedProfiles}";
                    AddJournalLine("MANAGER", $"{DateTime.Now:HH:mm:ss.fff} [SUMMARY] Hoàn tất. profileCompleted={successProfiles} systemErrors={failedProfiles}");
                }
            }
            finally
            {
                if (currentContext is not null) _messageReplyProfilesInFlight.Remove(currentContext.Profile.Name);
                currentContext = null;
                running = false;
                if (!form.IsDisposed)
                {
                    start.Enabled = true;
                    close.Enabled = true;
                    stop.Enabled = false;
                    messages.Enabled = true;
                    acceptRequests.Enabled = true;
                    replyAfterAccept.Enabled = true;
                    skipReplied.Enabled = true;
                    onlyInitial.Enabled = true;
                    delayMin.Enabled = true;
                    delayMax.Enabled = true;
                    retry.Enabled = true;
                    autoEnabled.Enabled = true;
                    autoInterval.Enabled = true;
                    if (stopRequested)
                        status.Text = "Trạng thái: Đã dừng.";
                    else if (string.IsNullOrWhiteSpace(status.Text) || !status.Text.StartsWith("Trạng thái: Hoàn tất", StringComparison.Ordinal))
                        status.Text = $"Trạng thái: Hoàn tất | Profile hoàn tất: {successProfiles} | Lỗi hệ thống: {failedProfiles}";
                }
            }
        };

        var noteRefreshTimer = new System.Windows.Forms.Timer { Interval = 1500, Enabled = true };
        noteRefreshTimer.Tick += (_, _) =>
        {
            if (form.IsDisposed || grid.IsDisposed) return;
            var latest = LoadMessageReplyToolState();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not ProfileContext ctx) continue;
                TrySetGridCellValue(row, noteColumn, BuildAutoReplyNote(ctx.Profile.Name, latest), "ShowTikTokMessageReplyDialog.AutoNoteRefresh");
            }
        };

        form.FormClosing += (_, e) =>
        {
            if (!running || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            ModernDialog.ShowMessage(form, "Module Tin nhắn đang chạy. Hãy bấm Dừng rồi chờ kết thúc trước khi đóng cửa sổ.", "Tin nhắn TikTok", MessageBoxIcon.Information);
        };
        form.FormClosed += (_, _) =>
        {
            noteRefreshTimer.Stop();
            noteRefreshTimer.Dispose();
            SaveUiState();
        };
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);
        form.ShowDialog(this);
    }

    void InitializeMessageReplyAutoFlow()
    {
        _refreshTimer.Tick += (_, _) => ScheduleAutoMessageReplyForLiveProfiles();
    }

    void ScheduleAutoMessageReplyForLiveProfiles()
    {
        if (_closing) return;

        var state = LoadMessageReplyToolState();
        if (!state.AutoEnabled)
        {
            _autoMessageReplyNextRunUtc.Clear();
            return;
        }

        // Auto reply cần nội dung. Không làm bất cứ điều gì với LIVE nếu cấu hình chưa đủ.
        if (ParseSharedReplyMessages(state.MessagesText).Length == 0) return;

        var intervalMinutes = NormalizeMessageReplyInterval(state.AutoIntervalMinutes);
        var now = DateTime.UtcNow;
        foreach (var ctx in _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder).ToList())
        {
            var snapshot = ctx.LastSnapshot;
            if (snapshot is null) continue;
            if (!string.Equals(GetLastConfirmedRuntimeState(ctx), "RUNNING", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshot.MessageReplyRunning) continue;
            if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name)) continue;
            if (_autoIdentityInFlight.Contains(ctx.Profile.Name)) continue;

            if (!_autoMessageReplyNextRunUtc.TryGetValue(ctx.Profile.Name, out var dueUtc))
            {
                if (state.AutoStats.TryGetValue(ctx.Profile.Name, out var stats) && stats.LastRunUtc != default)
                {
                    dueUtc = stats.LastRunUtc.AddMinutes(intervalMinutes);
                    if (dueUtc <= now) dueUtc = now.AddSeconds(5);
                }
                else
                {
                    // Lần đầu bật: cho LIVE chạy đủ chu kỳ rồi mới chen vào.
                    dueUtc = now.AddMinutes(intervalMinutes);
                }
                _autoMessageReplyNextRunUtc[ctx.Profile.Name] = dueUtc;
                _log.Info($"[AUTO_MESSAGE_REPLY_SCHEDULE] profile={ctx.Profile.Name} dueLocal={dueUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} intervalMin={intervalMinutes}");
                continue;
            }

            if (now < dueUtc) continue;

            // Khóa ngay trước khi fire-and-forget để Tick 1 giây kế tiếp không xếp trùng profile.
            _messageReplyProfilesInFlight.Add(ctx.Profile.Name);
            _autoMessageReplyNextRunUtc[ctx.Profile.Name] = DateTime.MaxValue;
            _ = RunAutoMessageReplyForProfileAsync(ctx);
        }
    }

    async Task RunAutoMessageReplyForProfileAsync(ProfileContext ctx)
    {
        await _autoMessageReplyQueueGate.WaitAsync();
        var intervalMinutes = 60;
        var pauseAttempted = false;
        var livePausedByAuto = false;
        var messageStartAttempted = false;
        var messageStarted = false;
        var countThisRun = false;
        MessageReplyStatusReply? last = null;
        var finalResult = "";

        try
        {
            var state = LoadMessageReplyToolState();
            intervalMinutes = NormalizeMessageReplyInterval(state.AutoIntervalMinutes);
            if (!state.AutoEnabled) return;

            var messages = ParseSharedReplyMessages(state.MessagesText);
            if (messages.Length == 0)
            {
                finalResult = "Bỏ qua: chưa có nội dung trả lời.";
                return;
            }

            try { await RefreshStatusAsync(ctx); } catch { }
            if (!string.Equals(GetLastConfirmedRuntimeState(ctx), "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                // Người dùng đã dừng/pause LIVE trong lúc profile chờ tới lượt. Không can thiệp.
                finalResult = "Bỏ qua: LIVE không còn ở trạng thái RUNNING.";
                return;
            }
            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                finalResult = "Bỏ qua: Chrome mất kết nối.";
                return;
            }

            _log.Info($"[AUTO_MESSAGE_REPLY_START] profile={ctx.Profile.Name} intervalMin={intervalMinutes}");

            // LIVE là luồng ưu tiên: chỉ dùng Pause, không Stop, để giữ nguyên engine/session/step.
            pauseAttempted = true;
            var pauseReply = await SendCommandAsync(ctx, "pause", TimeSpan.FromSeconds(8));
            if (!string.Equals(pauseReply, "paused", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Không Pause được automation LIVE trước khi kiểm tra Tin nhắn: " + pauseReply);
            livePausedByAuto = true;
            await Task.Delay(350);

            var requestJson = JsonSerializer.Serialize(new
            {
                Messages = messages,
                AcceptRequests = true,
                ReplyAfterAccept = true,
                SkipAlreadyReplied = state.SkipAlreadyReplied,
                // Auto phải xử lý cho tới khi danh sách request thực sự hết.
                OnlyInitialRequests = false,
                DelayMinMs = (int)Math.Round(state.DelayMinSeconds * 1000M),
                DelayMaxMs = (int)Math.Round(state.DelayMaxSeconds * 1000M),
                RetryCount = state.RetryCount,
                ReturnToPreviousPage = true,
                ReturnPageSettleMs = 1400,
                AbortOnAnyError = true
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson));
            messageStartAttempted = true;
            var startReply = await SendCommandAsync(ctx, "message_reply_start|" + payload, TimeSpan.FromSeconds(10));
            if (!string.Equals(startReply, "started", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Không khởi động được Tin nhắn tự động: " + startReply);

            messageStarted = true;
            countThisRun = true;
            var deadline = DateTime.UtcNow.AddMinutes(12);
            var consecutiveStatusErrors = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(700);
                try
                {
                    var raw = await SendCommandAsync(ctx, "message_reply_status", TimeSpan.FromSeconds(5));
                    last = JsonSerializer.Deserialize<MessageReplyStatusReply>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    consecutiveStatusErrors = 0;
                }
                catch (Exception ex)
                {
                    if (++consecutiveStatusErrors >= 4)
                        throw new InvalidOperationException("Mất trạng thái Tin nhắn tự động: " + ex.Message);
                    continue;
                }

                if (last is null) continue;
                if (last.Completed || !last.Running) break;
            }

            if (last is null || (last.Running && !last.Completed))
                throw new TimeoutException("Tin nhắn tự động vượt quá 12 phút; bỏ qua để trả quyền cho LIVE.");
            if (string.Equals(last.Stage, "ERROR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(last.Message);

            finalResult = last.Replied > 0
                ? $"OK: rep {last.Replied}, accept {last.Accepted}, bỏ qua {last.Skipped}, lỗi con {last.Failed}."
                : $"OK: không có tin chờ; lỗi con {last.Failed}.";
            _log.Info($"[AUTO_MESSAGE_REPLY_DONE] profile={ctx.Profile.Name} replied={last.Replied} accepted={last.Accepted} skipped={last.Skipped} failed={last.Failed}");
        }
        catch (Exception ex)
        {
            // Fail-open: lỗi Tin nhắn không được phép kéo LIVE xuống theo.
            countThisRun = countThisRun || messageStarted;
            finalResult = "Bỏ qua lỗi Tin nhắn: " + ex.Message;
            _log.Warn($"[AUTO_MESSAGE_REPLY_FAIL_OPEN] profile={ctx.Profile.Name} error={ex.Message}");
            if (messageStartAttempted)
            {
                try { await SendCommandAsync(ctx, "message_reply_stop", TimeSpan.FromSeconds(5)); }
                catch (Exception stopEx) { _log.Warn($"[AUTO_MESSAGE_REPLY_STOP_FAILED] profile={ctx.Profile.Name} error={stopEx.Message}"); }
            }
        }
        finally
        {
            // Nếu vừa cancel/timeout, cho Worker vài giây chạy finally để quay về URL LIVE đã lưu.
            if (messageStartAttempted)
            {
                for (var i = 0; i < 10; i++)
                {
                    try
                    {
                        var raw = await SendCommandAsync(ctx, "message_reply_status", TimeSpan.FromSeconds(3));
                        var probe = JsonSerializer.Deserialize<MessageReplyStatusReply>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (probe is null || probe.Completed || !probe.Running) break;
                    }
                    catch { break; }
                    await Task.Delay(500);
                }
            }

            if (livePausedByAuto || pauseAttempted)
            {
                try
                {
                    var resumeReply = await SendCommandAsync(ctx, "resume", TimeSpan.FromSeconds(8));
                    if (!string.Equals(resumeReply, "running", StringComparison.OrdinalIgnoreCase))
                        _log.Warn($"[AUTO_MESSAGE_REPLY_RESUME_UNCERTAIN] profile={ctx.Profile.Name} response={resumeReply}");
                    else
                        _log.Info($"[AUTO_MESSAGE_REPLY_LIVE_RESUMED] profile={ctx.Profile.Name}");
                }
                catch (Exception resumeEx)
                {
                    _log.Error($"[AUTO_MESSAGE_REPLY_RESUME_FAILED] profile={ctx.Profile.Name} error={resumeEx}");
                    // Không tự Start mới: tránh reset workflow. Engine recovery/status hiện có sẽ phản ánh rõ lỗi resume.
                }
            }

            if (countThisRun)
            {
                try
                {
                    var state = LoadMessageReplyToolState();
                    if (!state.AutoStats.TryGetValue(ctx.Profile.Name, out var stats))
                    {
                        stats = new MessageReplyAutoStats();
                        state.AutoStats[ctx.Profile.Name] = stats;
                    }
                    stats.CheckRuns++;
                    stats.LastRunUtc = DateTime.UtcNow;
                    stats.LastReplied = Math.Max(0, last?.Replied ?? 0);
                    stats.LastFailed = Math.Max(0, last?.Failed ?? (string.IsNullOrWhiteSpace(finalResult) ? 0 : 1));
                    if (stats.LastReplied > 0)
                    {
                        stats.ReplyRuns++;
                        stats.TotalReplied += stats.LastReplied;
                    }
                    stats.LastResult = finalResult;
                    SaveMessageReplyToolState(state);
                    intervalMinutes = NormalizeMessageReplyInterval(state.AutoIntervalMinutes);
                }
                catch (Exception statsEx)
                {
                    _log.Warn($"[AUTO_MESSAGE_REPLY_STATS] profile={ctx.Profile.Name} error={statsEx.Message}");
                }
            }

            _autoMessageReplyNextRunUtc[ctx.Profile.Name] = DateTime.UtcNow.AddMinutes(intervalMinutes);
            _messageReplyProfilesInFlight.Remove(ctx.Profile.Name);
            _autoMessageReplyQueueGate.Release();
        }
    }
}
