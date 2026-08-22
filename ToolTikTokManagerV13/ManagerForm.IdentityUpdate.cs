using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class IdentityToolState
    {
        public string NamesText { get; set; } = "";
        public string ImageFolder { get; set; } = "";
        public string BioText { get; set; } = "";
        public bool UpdateName { get; set; } = true;
        public bool UpdateAvatar { get; set; } = true;
        public bool UpdateBio { get; set; }
        public bool AutoOnReady { get; set; }
        public bool RandomNames { get; set; }
        public bool AvoidLastAvatar { get; set; } = true;
        public Dictionary<string, string> LastAvatarByProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class IdentityUpdateReply
    {
        public bool Ok { get; set; }
        public bool NameChanged { get; set; }
        public bool AvatarChanged { get; set; }
        public bool BioChanged { get; set; }
        public bool NameCooldown { get; set; }
        public bool AlreadyConfigured { get; set; }
        public bool Skipped { get; set; }
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
    }

    sealed record IdentityPreview(ProfileContext Context, string DisplayName, string AvatarPath, string Bio);

    string IdentityToolStatePath => Path.Combine(_baseDir, "tiktok_identity_tool.json");
    readonly HashSet<string> _autoIdentityHandledSession = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoIdentityInFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoIdentityNextProbeUtc = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _autoIdentityQueueGate = new(1, 1);

    IdentityToolState LoadIdentityToolState()
    {
        try
        {
            if (!File.Exists(IdentityToolStatePath)) return new IdentityToolState();
            var state = JsonSerializer.Deserialize<IdentityToolState>(File.ReadAllText(IdentityToolStatePath));
            if (state is null) return new IdentityToolState();
            state.LastAvatarByProfile = new Dictionary<string, string>(state.LastAvatarByProfile ?? new(), StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _log.Warn($"[IDENTITY_TOOL_STATE_LOAD] fallback=defaults error={ex.Message}");
            return new IdentityToolState();
        }
    }

    void SaveIdentityToolState(IdentityToolState state)
    {
        try
        {
            File.WriteAllText(IdentityToolStatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.Warn("[IDENTITY_TOOL_STATE_SAVE] " + ex.Message); }
    }

    void ShowTikTokIdentityDialog()
    {
        const string gridName = "TikTokIdentityGrid";
        const string useColumn = "Use";
        const string profileColumn = "Profile";
        const string namePreviewColumn = "NamePreview";
        const string avatarPreviewColumn = "AvatarPreview";
        const string bioPreviewColumn = "BioPreview";
        const string resultColumn = "Result";

        var state = LoadIdentityToolState();
        var previews = new Dictionary<string, IdentityPreview>(StringComparer.OrdinalIgnoreCase);
        var updateResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contexts = _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder).ToList();

        using var form = new Form
        {
            Text = "Đổi tên & ảnh đại diện TikTok",
            Width = 1120,
            Height = 820,
            MinimumSize = new Size(920, 680),
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
        // Khu cấu hình có viewport cuộn riêng để Tiểu sử/Tự động luôn truy cập được
        // trên màn hình thấp hoặc Windows DPI > 100%, không cần maximize dialog.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 270F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = "Mục này chạy độc lập với automation LIVE. Chọn profile, nhập danh sách tên và/hoặc chọn thư mục ảnh, xem trước rồi mới bấm Cập nhật. Profile đang chạy automation sẽ được dừng trước khi đổi hồ sơ.",
            ForeColor = Color.FromArgb(55, 76, 103),
            Margin = new Padding(0, 0, 0, 12)
        };

        var config = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 5,
            Margin = new Padding(0, 0, 0, 12)
        };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));

        var updateName = new CheckBox { Text = "Cập nhật tên", Checked = state.UpdateName && !string.IsNullOrWhiteSpace(state.NamesText), AutoSize = true, Margin = new Padding(0, 8, 8, 4) };
        var names = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 92,
            Dock = DockStyle.Fill,
            Text = state.NamesText,
            AcceptsReturn = true
        };
        ModernDialog.StyleTextInput(names);
        var randomNames = new CheckBox { Text = "Random tên", Checked = state.RandomNames, AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        var nameHint = new Label { Text = "Mỗi dòng một tên. Nếu chỉ có 1 dòng, tên đó dùng chung cho tất cả profile đã chọn.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 2, 0, 8) };

        var updateAvatar = new CheckBox { Text = "Cập nhật ảnh", Checked = state.UpdateAvatar, AutoSize = true, Margin = new Padding(0, 8, 8, 4) };
        var folder = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = state.ImageFolder };
        ModernDialog.StyleTextInput(folder);
        var browse = new Button { Text = "Chọn thư mục ảnh", Width = 138, Height = 36 };
        ModernDialog.StyleSecondaryButton(browse);
        var avoidLast = new CheckBox { Text = "Tránh ảnh vừa dùng", Checked = state.AvoidLastAvatar, AutoSize = true, Margin = new Padding(8, 8, 0, 0) };

        var updateBio = new CheckBox { Text = "Cập nhật tiểu sử", Checked = state.UpdateBio, AutoSize = true, Margin = new Padding(0, 8, 8, 4) };
        var bio = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 64,
            Dock = DockStyle.Fill,
            Text = state.BioText,
            MaxLength = 80
        };
        ModernDialog.StyleTextInput(bio);
        var autoOnReady = new CheckBox
        {
            Text = "Tự xử lý khi tài khoản đăng nhập xong và chưa DONE trong Excel",
            Checked = state.AutoOnReady,
            AutoSize = true,
            Margin = new Padding(8, 8, 0, 0)
        };
        var autoHint = new Label
        {
            Text = "Nếu TikTok báo tên đang chờ 7 ngày thì bỏ qua tài khoản đó trong phiên hiện tại, không ghi DONE và automation vẫn tiếp tục.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 8, 0, 0)
        };

        config.Controls.Add(updateName, 0, 0);
        config.Controls.Add(names, 1, 0);
        config.SetColumnSpan(names, 3);
        config.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 1);
        var nameOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        nameOptions.Controls.Add(randomNames);
        nameOptions.Controls.Add(nameHint);
        config.Controls.Add(nameOptions, 1, 1);
        config.SetColumnSpan(nameOptions, 3);
        config.Controls.Add(updateAvatar, 0, 2);
        config.Controls.Add(folder, 1, 2);
        config.Controls.Add(browse, 2, 2);
        config.Controls.Add(avoidLast, 3, 2);
        config.Controls.Add(updateBio, 0, 3);
        config.Controls.Add(bio, 1, 3);
        config.SetColumnSpan(bio, 3);
        config.Controls.Add(new Label { Text = "Tự động", AutoSize = true, Margin = new Padding(0, 10, 8, 4) }, 0, 4);
        var autoPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        autoPanel.Controls.Add(autoOnReady);
        autoPanel.Controls.Add(autoHint);
        config.Controls.Add(autoPanel, 1, 4);
        config.SetColumnSpan(autoPanel, 3);

        var configViewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0, 0, 8, 0),
            BackColor = ModernDialog.Canvas
        };
        // AutoSize + Dock Top làm nội dung giữ đủ chiều cao; Panel bên ngoài sẽ hiện
        // thanh cuộn dọc khi cửa sổ/DPI không đủ chỗ.
        config.Dock = DockStyle.Top;
        config.AutoSize = true;
        configViewport.Controls.Add(config);

        var body = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };

        // Không đặt PanelMinSize/SplitterDistance trong object initializer. Khi đó
        // SplitContainer vẫn đang có Width mặc định rất nhỏ, nên WinForms có thể
        // ném ArgumentOutOfRangeException trước cả khi dialog hiện ra. Chỉ áp dụng
        // các giới hạn sau khi layout đã có kích thước thật và luôn clamp an toàn.
        void FitIdentitySplitter()
        {
            if (body.IsDisposed) return;
            var width = body.ClientSize.Width;
            var available = width - body.SplitterWidth;
            if (available < 420) return;

            var panel2Min = Math.Min(180, Math.Max(120, available / 4));
            var panel1Min = Math.Min(620, Math.Max(300, available - panel2Min - 80));
            if (panel1Min + panel2Min > available) return;

            // Đặt Panel2 trước để khoảng bên phải luôn còn hợp lệ khi tăng Panel1.
            body.Panel2MinSize = panel2Min;
            body.Panel1MinSize = panel1Min;

            var max = width - body.Panel2MinSize - body.SplitterWidth;
            var preferred = (int)Math.Round(width * 0.76);
            var distance = Math.Clamp(preferred, body.Panel1MinSize, max);
            if (body.SplitterDistance != distance)
                body.SplitterDistance = distance;
        }

        var grid = new DataGridView
        {
            Name = gridName,
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 31 }
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 239, 249);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = useColumn, HeaderText = "Chọn", Width = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = profileColumn, HeaderText = "Profile", Width = 110, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = namePreviewColumn, HeaderText = "Tên dự kiến", Width = 190, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = avatarPreviewColumn, HeaderText = "Avatar", Width = 165, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = bioPreviewColumn, HeaderText = "Tiểu sử", Width = 175, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = resultColumn, HeaderText = "Kết quả", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180, ReadOnly = true });
        LogGridSchema(grid, gridName, useColumn, profileColumn, namePreviewColumn, avatarPreviewColumn, bioPreviewColumn, resultColumn);

        var selected = SelectedContext();
        foreach (var ctx in contexts)
        {
            var rowIndex = grid.Rows.Add(ReferenceEquals(ctx, selected), ctx.Profile.Name, "—", "—", "—", "Chưa chạy");
            grid.Rows[rowIndex].Tag = ctx;
            updateResults[ctx.Profile.Name] = "Chưa chạy";
        }

        void SetRowColorIfAttached(DataGridViewRow row, Color color)
        {
            if (!grid.IsDisposed && ReferenceEquals(row.DataGridView, grid))
                row.DefaultCellStyle.ForeColor = color;
        }

        body.Panel1.Controls.Add(grid);

        var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Color.FromArgb(245, 248, 252) };
        var previewImage = new PictureBox { Dock = DockStyle.Top, Height = 210, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var previewText = new Label { Dock = DockStyle.Top, Height = 125, Padding = new Padding(0, 10, 0, 0), AutoEllipsis = true, Text = "Chọn một dòng để xem trước." };
        previewPanel.Controls.Add(previewText);
        previewPanel.Controls.Add(previewImage);
        body.Panel2.Controls.Add(previewPanel);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 6) };
        var selectAll = new Button { Text = "Chọn tất cả", Width = 112, Height = 36 };
        var clearAll = new Button { Text = "Bỏ chọn", Width = 100, Height = 36 };
        var randomize = new Button { Text = "Random lại", Width = 112, Height = 36 };
        ModernDialog.StyleSecondaryButton(selectAll);
        ModernDialog.StyleSecondaryButton(clearAll);
        ModernDialog.StyleSecondaryButton(randomize);
        tools.Controls.Add(selectAll);
        tools.Controls.Add(clearAll);
        tools.Controls.Add(randomize);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        var close = new Button { Text = "Đóng", Width = 104, Height = 42, DialogResult = DialogResult.Cancel };
        var apply = new Button { Text = "Cập nhật đã chọn", Width = 164, Height = 42 };
        var updateInProgress = false;
        ModernDialog.StyleSecondaryButton(close);
        ModernDialog.StylePrimaryButton(apply);
        footer.Controls.Add(close);
        footer.Controls.Add(apply);

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(configViewport, 0, 1);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(tools, 0, 3);
        root.Controls.Add(footer, 0, 4);
        form.Controls.Add(root);
        form.CancelButton = close;

        form.Shown += (_, _) =>
        {
            FitIdentitySplitter();
            // Bảo đảm viewport biết toàn bộ chiều cao config sau scale DPI.
            configViewport.AutoScrollMinSize = new Size(0, Math.Max(config.PreferredSize.Height + 8, 300));
        };
        body.SizeChanged += (_, _) => FitIdentitySplitter();
        config.SizeChanged += (_, _) =>
            configViewport.AutoScrollMinSize = new Size(0, Math.Max(config.PreferredSize.Height + 8, 300));

        static List<string> ReadNames(TextBox box) => box.Lines
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        static List<string> ReadImages(string imageFolder)
        {
            if (string.IsNullOrWhiteSpace(imageFolder) || !Directory.Exists(imageFolder)) return new();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
            return Directory.EnumerateFiles(imageFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(x => allowed.Contains(Path.GetExtension(x)))
                .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        void DisposePreviewImage()
        {
            var old = previewImage.Image;
            previewImage.Image = null;
            try { old?.Dispose(); } catch { }
        }

        void ShowRowPreview()
        {
            DisposePreviewImage();
            if (grid.CurrentRow?.Tag is not ProfileContext ctx || !previews.TryGetValue(ctx.Profile.Name, out var preview))
            {
                previewText.Text = "Chọn một dòng để xem trước.";
                return;
            }
            previewText.Text = $"Profile: {ctx.Profile.Name}\nTên: {(string.IsNullOrWhiteSpace(preview.DisplayName) ? "(không đổi)" : preview.DisplayName)}\nẢnh: {(string.IsNullOrWhiteSpace(preview.AvatarPath) ? "(không đổi)" : Path.GetFileName(preview.AvatarPath))}\nTiểu sử: {(string.IsNullOrWhiteSpace(preview.Bio) ? "(không đổi)" : preview.Bio)}";
            if (!string.IsNullOrWhiteSpace(preview.AvatarPath) && File.Exists(preview.AvatarPath))
            {
                try
                {
                    using var fs = new FileStream(preview.AvatarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var img = Image.FromStream(fs);
                    previewImage.Image = new Bitmap(img);
                }
                catch { }
            }
        }

        void RebuildPreview()
        {
            grid.EndEdit();
            previews.Clear();
            var nameList = ReadNames(names);
            var images = ReadImages(folder.Text);
            var shuffledImages = images.OrderBy(_ => Random.Shared.Next()).ToList();
            var shuffledNames = nameList.OrderBy(_ => Random.Shared.Next()).ToList();
            var avatarCursor = 0;
            var nameCursor = 0;

            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not ProfileContext ctx) continue;
                string displayName = "";
                string avatarPath = "";
                string bioText = updateBio.Checked ? bio.Text.Trim() : "";

                if (updateName.Checked && nameList.Count > 0)
                {
                    if (nameList.Count == 1) displayName = nameList[0];
                    else if (randomNames.Checked)
                    {
                        if (nameCursor >= shuffledNames.Count) { shuffledNames = nameList.OrderBy(_ => Random.Shared.Next()).ToList(); nameCursor = 0; }
                        displayName = shuffledNames[nameCursor++];
                    }
                    else
                    {
                        displayName = nameList[row.Index % nameList.Count];
                    }
                }

                if (updateAvatar.Checked && images.Count > 0)
                {
                    if (avatarCursor >= shuffledImages.Count)
                    {
                        shuffledImages = images.OrderBy(_ => Random.Shared.Next()).ToList();
                        avatarCursor = 0;
                    }
                    avatarPath = shuffledImages[avatarCursor++];
                    if (avoidLast.Checked && images.Count > 1
                        && state.LastAvatarByProfile.TryGetValue(ctx.Profile.Name, out var previous)
                        && string.Equals(Path.GetFullPath(previous), Path.GetFullPath(avatarPath), StringComparison.OrdinalIgnoreCase))
                    {
                        var replacement = images.FirstOrDefault(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(replacement)) avatarPath = replacement;
                    }
                }

                previews[ctx.Profile.Name] = new IdentityPreview(ctx, displayName, avatarPath, bioText);
                TrySetGridCellValue(row, namePreviewColumn, string.IsNullOrWhiteSpace(displayName) ? "—" : displayName, "ShowTikTokIdentityDialog.RebuildPreview");
                TrySetGridCellValue(row, avatarPreviewColumn, string.IsNullOrWhiteSpace(avatarPath) ? "—" : Path.GetFileName(avatarPath), "ShowTikTokIdentityDialog.RebuildPreview");
                TrySetGridCellValue(row, bioPreviewColumn, string.IsNullOrWhiteSpace(bioText) ? "—" : bioText, "ShowTikTokIdentityDialog.RebuildPreview");
            }
            ShowRowPreview();
        }

        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Chọn thư mục chứa avatar TikTok" };
            if (Directory.Exists(folder.Text)) picker.SelectedPath = folder.Text;
            if (picker.ShowDialog(form) != DialogResult.OK) return;
            folder.Text = picker.SelectedPath;
            RebuildPreview();
        };
        randomize.Click += (_, _) => RebuildPreview();
        names.TextChanged += (_, _) => RebuildPreview();
        updateName.CheckedChanged += (_, _) => { names.Enabled = updateName.Checked; randomNames.Enabled = updateName.Checked; RebuildPreview(); };
        updateAvatar.CheckedChanged += (_, _) => { folder.Enabled = updateAvatar.Checked; browse.Enabled = updateAvatar.Checked; avoidLast.Enabled = updateAvatar.Checked; RebuildPreview(); };
        updateBio.CheckedChanged += (_, _) => { bio.Enabled = updateBio.Checked; RebuildPreview(); };
        bio.TextChanged += (_, _) => RebuildPreview();
        autoOnReady.CheckedChanged += (_, _) => { state.AutoOnReady = autoOnReady.Checked; SaveIdentityToolState(state); };
        randomNames.CheckedChanged += (_, _) => RebuildPreview();
        avoidLast.CheckedChanged += (_, _) => RebuildPreview();
        grid.SelectionChanged += (_, _) => ShowRowPreview();
        grid.CellValueChanged += (_, e) =>
        {
            var useGridColumn = TryGetGridColumn(grid, useColumn, "ShowTikTokIdentityDialog.CellValueChanged");
            if (useGridColumn is not null && e.ColumnIndex == useGridColumn.Index) ShowRowPreview();
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        selectAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                TrySetGridCellValue(row, useColumn, true, "ShowTikTokIdentityDialog.SelectAll");
        };
        clearAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                TrySetGridCellValue(row, useColumn, false, "ShowTikTokIdentityDialog.ClearAll");
        };

        apply.Click += async (_, _) =>
        {
            try
            {
                grid.EndEdit();
                var selectedRows = grid.Rows.Cast<DataGridViewRow>()
                    .Where(r => r.Tag is ProfileContext
                        && Convert.ToBoolean(GetGridCellValueOrNull(r, useColumn, "ShowTikTokIdentityDialog.Apply") ?? false))
                    .ToList();
                if (selectedRows.Count == 0)
                {
                    ModernDialog.ShowMessage(form, "Hãy chọn ít nhất một profile.", "Tên & ảnh TikTok", MessageBoxIcon.Information);
                    return;
                }
                if (!updateName.Checked && !updateAvatar.Checked && !updateBio.Checked)
                {
                    ModernDialog.ShowMessage(form, "Hãy bật ít nhất một mục: Cập nhật tên, Cập nhật ảnh hoặc Cập nhật tiểu sử.", "Tên & ảnh TikTok", MessageBoxIcon.Information);
                    return;
                }
                if (updateName.Checked && ReadNames(names).Count == 0)
                {
                    ModernDialog.ShowMessage(form, "Bạn đã bật Cập nhật tên nhưng danh sách tên đang trống.", "Tên & ảnh TikTok", MessageBoxIcon.Warning);
                    return;
                }
                if (updateAvatar.Checked && ReadImages(folder.Text).Count == 0)
                {
                    ModernDialog.ShowMessage(form, "Không tìm thấy ảnh JPG/JPEG/PNG/WEBP/BMP trong thư mục đã chọn.", "Tên & ảnh TikTok", MessageBoxIcon.Warning);
                    return;
                }
                if (updateBio.Checked && bio.Text.Trim().Length == 0)
                {
                    ModernDialog.ShowMessage(form, "Bạn đã bật Cập nhật tiểu sử nhưng ô Tiểu sử đang trống.", "Tên & ảnh TikTok", MessageBoxIcon.Warning);
                    return;
                }

                state.NamesText = names.Text;
                state.ImageFolder = folder.Text;
                state.UpdateName = updateName.Checked;
                state.UpdateAvatar = updateAvatar.Checked;
                state.BioText = bio.Text.Trim();
                state.UpdateBio = updateBio.Checked;
                state.AutoOnReady = autoOnReady.Checked;
                state.RandomNames = randomNames.Checked;
                state.AvoidLastAvatar = avoidLast.Checked;
                SaveIdentityToolState(state);

                var confirm = $"Sẽ cập nhật {selectedRows.Count} profile theo phần Xem trước.\n\nProfile đang chạy automation sẽ được DỪNG trước khi đổi hồ sơ. Sau khi đổi xong tool không tự chạy lại automation.\n\nTiếp tục?";
                if (ModernDialog.ShowConfirm(form, confirm, "Xác nhận cập nhật TikTok") != DialogResult.Yes) return;

                updateInProgress = true;
                apply.Enabled = false;
                randomize.Enabled = false;
                close.Enabled = false;
                var success = 0;
                var skipped = 0;
                var failed = 0;
                foreach (var row in selectedRows)
                {
                    if (row.Tag is not ProfileContext ctx || !previews.TryGetValue(ctx.Profile.Name, out var preview)) continue;
                    updateResults[ctx.Profile.Name] = "Đang xử lý...";
                    TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                    var profileCell = TryGetGridCell(row, profileColumn, "ShowTikTokIdentityDialog.Apply");
                    if (profileCell is not null) grid.CurrentCell = profileCell;
                    grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index);
                    Application.DoEvents();
                    try
                    {
                        var reply = await UpdateTikTokIdentityAsync(ctx,
                            updateName.Checked ? preview.DisplayName : "",
                            updateAvatar.Checked ? preview.AvatarPath : "",
                            updateBio.Checked ? preview.Bio : "");
                        if (!reply.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(reply.Error) ? reply.Message : reply.Error);
                        updateResults[ctx.Profile.Name] = reply.Message.Length > 0 ? reply.Message : "Đã cập nhật";
                        TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                        if (reply.Skipped || reply.NameCooldown)
                        {
                            SetRowColorIfAttached(row, Color.DarkOrange);
                            skipped++;
                        }
                        else
                        {
                            SetRowColorIfAttached(row, Color.DarkGreen);
                            success++;
                            if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(preview.AvatarPath))
                                state.LastAvatarByProfile[ctx.Profile.Name] = preview.AvatarPath;
                        }
                        SaveIdentityToolState(state);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        updateResults[ctx.Profile.Name] = "Lỗi: " + ex.Message;
                        TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                        SetRowColorIfAttached(row, Color.Firebrick);
                        _log.Warn($"[TIKTOK_IDENTITY_UPDATE] profile={ctx.Profile.Name} result=failed message={ex.Message}");
                    }
                }

                if (form.IsDisposed) return;
                var failedDetails = updateResults
                    .Where(item => item.Value.StartsWith("Lỗi:", StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .Select(item => $"{item.Key}: {item.Value}")
                    .ToList();
                var summary = $"Hoàn tất.\nThành công: {success}\nBỏ qua: {skipped}\nLỗi: {failed}";
                if (failedDetails.Count > 0) summary += "\n\n" + string.Join("\n", failedDetails);
                summary += "\n\nProfile lỗi không làm dừng các profile còn lại.";
                ModernDialog.ShowMessage(form, summary, "Tên & ảnh TikTok", failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                updateInProgress = false;
                if (!form.IsDisposed)
                {
                    apply.Enabled = true;
                    randomize.Enabled = true;
                    close.Enabled = true;
                }
            }
        };

        form.FormClosing += (_, e) =>
        {
            if (!updateInProgress || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            ModernDialog.ShowMessage(form, "Manager đang cập nhật profile. Hãy chờ thao tác hiện tại hoàn tất rồi đóng cửa sổ.", "Tên & ảnh TikTok", MessageBoxIcon.Information);
        };

        form.FormClosed += (_, _) =>
        {
            state.NamesText = names.Text;
            state.ImageFolder = folder.Text;
            state.UpdateName = updateName.Checked;
            state.UpdateAvatar = updateAvatar.Checked;
            state.BioText = bio.Text.Trim();
            state.UpdateBio = updateBio.Checked;
            state.AutoOnReady = autoOnReady.Checked;
            state.RandomNames = randomNames.Checked;
            state.AvoidLastAvatar = avoidLast.Checked;
            SaveIdentityToolState(state);
            DisposePreviewImage();
        };
        form.Shown += (_, _) => { ModernDialog.FitToWorkingArea(form); RebuildPreview(); };
        form.ShowDialog(this);
    }

    async Task<IdentityUpdateReply> UpdateTikTokIdentityAsync(
        ProfileContext ctx, string displayName, string avatarPath, string bio = "",
        bool skipIfNameCooldown = false, bool resumeAutomation = false,
        IReadOnlyList<string>? knownDisplayNames = null, bool verifyExistingState = false,
        TimeSpan? workerTimeout = null)
    {
        if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name))
            throw new InvalidOperationException("Profile đang được mục Tin nhắn TikTok xử lý. Hãy dừng/đợi Tin nhắn hoàn tất rồi cập nhật tên/ảnh.");
        await OpenProfileAsync(ctx);
        try { await RefreshStatusAsync(ctx); } catch { }
        var previousRunState = GetLastConfirmedRuntimeState(ctx);
        var shouldResume = resumeAutomation && (previousRunState is "RUNNING" or "PAUSED");
        if (previousRunState is "RUNNING" or "PAUSED")
        {
            try { await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(8)); } catch { }
        }

        if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            await OpenChromeForProfileAsync(ctx);
            try { await RefreshStatusAsync(ctx); } catch { }
        }
        if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chrome chưa kết nối. Hãy mở Chrome của profile rồi thử lại.");

        // Đổi tên/ảnh là luồng setup tài khoản độc lập với LIVE. Chỉ cần TikTok
        // đã đăng nhập; không gọi startup gate và không điều hướng sang /live.
        var identityReady = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(6));
        if (!string.Equals(identityReady, "ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TikTok chưa đăng nhập trên profile này. Chrome đã được giữ ở trang chủ; hãy đăng nhập tài khoản rồi cập nhật tên/ảnh lại.");

        var username = "";
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            username = _tiktokAuthService.Load(dataRoot).Username;
        }
        catch (Exception ex)
        {
            _log.Warn($"[TIKTOK_IDENTITY_USERNAME_READ] profile={ctx.Profile.Name} message={ex.Message}");
        }

        try
        {
            var request = JsonSerializer.Serialize(new
            {
                Username = username,
                DisplayName = displayName,
                AvatarPath = avatarPath,
                Bio = bio,
                SkipIfNameCooldown = skipIfNameCooldown,
                KnownDisplayNames = knownDisplayNames ?? Array.Empty<string>(),
                VerifyExistingState = verifyExistingState
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            var raw = await SendCommandAsync(ctx, "update_tiktok_identity|" + payload, workerTimeout ?? TimeSpan.FromSeconds(100));
            var reply = JsonSerializer.Deserialize<IdentityUpdateReply>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return reply ?? new IdentityUpdateReply { Ok = false, Error = "Worker trả về kết quả không hợp lệ." };
        }
        finally
        {
            if (shouldResume)
            {
                try
                {
                    await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(35));
                    if (previousRunState == "PAUSED")
                    {
                        await Task.Delay(300);
                        await SendCommandAsync(ctx, "pause", TimeSpan.FromSeconds(8));
                    }
                }
                catch (Exception ex) { _log.Warn($"[AUTO_IDENTITY_RESUME_FAILED] profile={ctx.Profile.Name} {ex.Message}"); }
            }
        }
    }

    void InitializeIdentityAutoFlow()
    {
        _refreshTimer.Tick += (_, _) => ScheduleAutoIdentityForReadyProfiles();
    }

    void ScheduleAutoIdentityForReadyProfiles()
    {
        if (_closing) return;
        var state = LoadIdentityToolState();
        if (!state.AutoOnReady) return;
        if (string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath)) return;

        foreach (var ctx in _contexts.Values.ToList())
        {
            var snapshot = ctx.LastSnapshot;
            if (snapshot is null) continue;
            if (!string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshot.MessageReplyRunning || _messageReplyProfilesInFlight.Contains(ctx.Profile.Name)) continue; // Không tranh điều hướng với module Tin nhắn TikTok.

            // READY ở V13.5 mới chỉ có nghĩa là gate mở Chrome/đăng nhập đã hoàn tất.
            // Khi mở profile để setup, READY vẫn dừng ở trang chủ và KHÔNG vào LIVE.
            // Chờ READY trước khi Auto Identity chạy để tránh tranh điều hướng với
            // luồng tự đăng nhập/đưa về trang chủ vừa được thực hiện trong Worker.
            if (!string.Equals(snapshot.TikTokStartupState, "READY", StringComparison.OrdinalIgnoreCase)) continue;

            if (_autoIdentityNextProbeUtc.TryGetValue(ctx.Profile.Name, out var nextProbeUtc)
                && DateTime.UtcNow < nextProbeUtc) continue;
            if (_autoIdentityHandledSession.Contains(ctx.Profile.Name) || _autoIdentityInFlight.Contains(ctx.Profile.Name)) continue;
            _autoIdentityInFlight.Add(ctx.Profile.Name);
            _ = RunAutoIdentityForProfileAsync(ctx);
        }
    }

    async Task RunAutoIdentityForProfileAsync(ProfileContext ctx)
    {
        try
        {
            // Gate riêng cho setup hồ sơ: có Chrome + có session TikTok là đủ.
            // Nếu người dùng đang đăng nhập thủ công, thử lại sau vài giây mà không
            // đánh dấu profile là đã xử lý và không ép Chrome vào LIVE.
            var readiness = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(6));
            if (!string.Equals(readiness, "ready", StringComparison.OrdinalIgnoreCase))
            {
                _autoIdentityNextProbeUtc[ctx.Profile.Name] = DateTime.UtcNow.AddSeconds(4);
                return;
            }
            _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);

            await _autoIdentityQueueGate.WaitAsync();
            try
            {
                var state = LoadIdentityToolState();
                if (!state.AutoOnReady) return;

                string username = "";
                try
                {
                    var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
                    username = _tiktokAuthService.Load(dataRoot).Username.Trim();
                }
                catch { }
                if (string.IsNullOrWhiteSpace(username)) return;
                var accountSessionKey = "account:" + username.ToLowerInvariant();
                if (_autoIdentityHandledSession.Contains(accountSessionKey))
                {
                    _autoIdentityHandledSession.Add(ctx.Profile.Name);
                    return;
                }

                var account = _accountPoolService.Load().FirstOrDefault(x =>
                    x.AssignedProfile.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase))
                    ?? _accountPoolService.Load().FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (account is null) return;

                if (_accountPoolService.IsIdentityDone(account.Username))
                {
                    _autoIdentityHandledSession.Add(ctx.Profile.Name);
                    _autoIdentityHandledSession.Add(accountSessionKey);
                    _log.Info($"[AUTO_IDENTITY_SKIP_DONE] profile={ctx.Profile.Name} account={account.Username}");
                    return;
                }

                var names = (state.NamesText ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var images = Directory.Exists(state.ImageFolder)
                    ? Directory.EnumerateFiles(state.ImageFolder, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(x => new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase)).ToList()
                    : new List<string>();

                var displayName = !state.UpdateName || names.Count == 0 ? "" : names.Count == 1 ? names[0] : names[Random.Shared.Next(names.Count)];
                var avatarPath = "";
                if (state.UpdateAvatar && images.Count > 0)
                {
                    var candidates = images;
                    if (state.AvoidLastAvatar && images.Count > 1 && state.LastAvatarByProfile.TryGetValue(ctx.Profile.Name, out var previous))
                        candidates = images.Where(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase)).ToList();
                    avatarPath = candidates[Random.Shared.Next(candidates.Count)];
                }
                var bio = state.UpdateBio ? (state.BioText ?? "").Trim() : "";

                if (displayName.Length == 0 && avatarPath.Length == 0 && bio.Length == 0)
                {
                    _log.Warn($"[AUTO_IDENTITY_SKIP_CONFIG] profile={ctx.Profile.Name} không có tên/ảnh/tiểu sử để áp dụng.");
                    _autoIdentityHandledSession.Add(ctx.Profile.Name);
                    return;
                }

                _log.Info($"[AUTO_IDENTITY_START] profile={ctx.Profile.Name} account={account.Username}");
                var reply = await UpdateTikTokIdentityAsync(
                    ctx, displayName, avatarPath, bio,
                    skipIfNameCooldown: true,
                    resumeAutomation: true,
                    knownDisplayNames: names,
                    verifyExistingState: true);

                if (!reply.Ok)
                {
                    // Lỗi tạm thời không được khóa profile cho cả session. Cho phép
                    // Auto Identity thử lại sau, thay vì coi là đã xử lý xong.
                    _autoIdentityNextProbeUtc[ctx.Profile.Name] = DateTime.UtcNow.AddSeconds(20);
                    _log.Warn($"[AUTO_IDENTITY_FAILED] profile={ctx.Profile.Name} account={account.Username} {reply.Error}");
                    return;
                }

                _autoIdentityHandledSession.Add(ctx.Profile.Name);
                _autoIdentityHandledSession.Add(accountSessionKey);
                if (reply.AlreadyConfigured)
                {
                    _accountPoolService.MarkIdentityDone(account.Username);
                    _log.Info($"[AUTO_IDENTITY_ALREADY_CONFIGURED] profile={ctx.Profile.Name} account={account.Username}; trạng thái TikTok đã phù hợp, ghi Excel=F:DONE và bỏ qua cập nhật.");
                    return;
                }
                if (reply.NameCooldown || reply.Skipped)
                {
                    _log.Info($"[AUTO_IDENTITY_SKIP_COOLDOWN] profile={ctx.Profile.Name} account={account.Username}; không ghi DONE.");
                    return;
                }

                _accountPoolService.MarkIdentityDone(account.Username);
                if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(avatarPath))
                {
                    state.LastAvatarByProfile[ctx.Profile.Name] = avatarPath;
                    SaveIdentityToolState(state);
                }
                _log.Info($"[AUTO_IDENTITY_DONE] profile={ctx.Profile.Name} account={account.Username} ghi Excel=F:DONE");
            }
            finally { _autoIdentityQueueGate.Release(); }
        }
        catch (Exception ex)
        {
            _autoIdentityNextProbeUtc[ctx.Profile.Name] = DateTime.UtcNow.AddSeconds(20);
            _log.Warn($"[AUTO_IDENTITY_ERROR] profile={ctx.Profile.Name} {ex.Message}");
        }
        finally { _autoIdentityInFlight.Remove(ctx.Profile.Name); }
    }

}
