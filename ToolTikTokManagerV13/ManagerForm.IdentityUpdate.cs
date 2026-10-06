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
        public string VideoFolder { get; set; } = "";
        public string VideoCaptionText { get; set; } = "";
        public string VideoSelectionMode { get; set; } = "random";
        public bool VideoRandomCaption { get; set; } = true;
        public bool AvoidLastVideo { get; set; } = true;
        public bool VideoOncePerRun { get; set; } = true;
        public bool VideoDeleteEnabled { get; set; } = true;
        public bool VideoUploadEnabled { get; set; }
        public string VideoDeleteMode { get; set; } = "all";
        public Dictionary<string, string> LastVideoByAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    sealed class VideoDeleteReply
    {
        public bool Running { get; set; }
        public string Stage { get; set; } = "";
        public bool Completed { get; set; }
        public bool Ok { get; set; }
        public int InitialCount { get; set; }
        public int DeletedCount { get; set; }
        public int RemainingCount { get; set; } = -1;
        public bool VerifiedEmpty { get; set; }
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
    }

    sealed class VideoUploadReply
    {
        public bool Running { get; set; }
        public string Stage { get; set; } = "";
        public bool Completed { get; set; }
        public bool Ok { get; set; }
        public bool Posted { get; set; }
        public bool PrivacyUpdated { get; set; }
        public bool ProfileVerified { get; set; }
        public bool DeleteFallbackAttempted { get; set; }
        public bool DeleteFallbackSucceeded { get; set; }
        public int DeleteFallbackDeletedCount { get; set; }
        public int DeleteFallbackRemainingCount { get; set; } = -1;
        public string DeleteFallbackError { get; set; } = "";
        public string VideoPath { get; set; } = "";
        public string PostedHref { get; set; } = "";
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
    }

    sealed record IdentityPreview(ProfileContext Context, string DisplayName, string AvatarPath, string Bio);
    sealed record VideoUploadPreview(ProfileContext Context, string VideoPath, string Caption);

    string IdentityToolStatePath => Path.Combine(_baseDir, "tiktok_identity_tool.json");
    readonly HashSet<string> _autoIdentityHandledSession = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _autoVideoHandledReadyAccount = new(StringComparer.OrdinalIgnoreCase);
    // Khi AutoOnReady đang chạy từ RUNNING/PAUSED, VIDEO có thể chủ động STOP Worker.
    // Nếu Name Guard sau đó chỉ TRANSIENT thì lần retry kế tiếp sẽ quan sát STOPPED.
    // Giữ lại trạng thái chạy GỐC để chỉ phục hồi SAU KHI Tên/ảnh thật sự Allowed.
    // Cache này tuyệt đối không mở gate Tên/ảnh; nó chỉ nhớ trạng thái cần restore.
    readonly Dictionary<string, (string Username, string State)> _autoIdentityPendingResumeState = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoIdentityInFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoIdentityNextProbeUtc = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _autoIdentityQueueGate = new(1, 1);
    readonly object _identityToolStateCacheLock = new();
    IdentityToolState? _identityToolStateCache;
    DateTime _identityToolStateCacheWriteUtc = DateTime.MinValue;
    long _identityToolStateCacheLength = -1;

    static IdentityToolState CloneIdentityToolState(IdentityToolState state)
        => new()
        {
            NamesText = state.NamesText,
            ImageFolder = state.ImageFolder,
            BioText = state.BioText,
            UpdateName = state.UpdateName,
            UpdateAvatar = state.UpdateAvatar,
            UpdateBio = state.UpdateBio,
            AutoOnReady = state.AutoOnReady,
            RandomNames = state.RandomNames,
            AvoidLastAvatar = state.AvoidLastAvatar,
            LastAvatarByProfile = new Dictionary<string, string>(
                state.LastAvatarByProfile ?? new(),
                StringComparer.OrdinalIgnoreCase),
            VideoFolder = state.VideoFolder,
            VideoCaptionText = state.VideoCaptionText,
            VideoSelectionMode = state.VideoSelectionMode,
            VideoRandomCaption = state.VideoRandomCaption,
            AvoidLastVideo = state.AvoidLastVideo,
            VideoOncePerRun = state.VideoOncePerRun,
            VideoDeleteEnabled = state.VideoDeleteEnabled,
            VideoUploadEnabled = state.VideoUploadEnabled,
            VideoDeleteMode = state.VideoDeleteMode,
            LastVideoByAccount = new Dictionary<string, string>(
                state.LastVideoByAccount ?? new(),
                StringComparer.OrdinalIgnoreCase)
        };

    IdentityToolState LoadIdentityToolState()
    {
        try
        {
            if (!File.Exists(IdentityToolStatePath))
            {
                lock (_identityToolStateCacheLock)
                {
                    _identityToolStateCache = null;
                    _identityToolStateCacheWriteUtc = DateTime.MinValue;
                    _identityToolStateCacheLength = -1;
                }
                return new IdentityToolState();
            }

            var info = new FileInfo(IdentityToolStatePath);
            var writeUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            lock (_identityToolStateCacheLock)
            {
                if (_identityToolStateCache is not null
                    && _identityToolStateCacheWriteUtc == writeUtc
                    && _identityToolStateCacheLength == length)
                {
                    return CloneIdentityToolState(_identityToolStateCache);
                }
            }

            var state = JsonSerializer.Deserialize<IdentityToolState>(File.ReadAllText(IdentityToolStatePath));
            if (state is null) return new IdentityToolState();
            state.LastAvatarByProfile = new Dictionary<string, string>(state.LastAvatarByProfile ?? new(), StringComparer.OrdinalIgnoreCase);
            state.LastVideoByAccount = new Dictionary<string, string>(state.LastVideoByAccount ?? new(), StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(state.VideoSelectionMode)) state.VideoSelectionMode = "random";
            if (!string.Equals(state.VideoDeleteMode, "newest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(state.VideoDeleteMode, "all", StringComparison.OrdinalIgnoreCase))
                state.VideoDeleteMode = "all";

            lock (_identityToolStateCacheLock)
            {
                _identityToolStateCache = CloneIdentityToolState(state);
                _identityToolStateCacheWriteUtc = writeUtc;
                _identityToolStateCacheLength = length;
            }
            return CloneIdentityToolState(state);
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

            // Đồng bộ cache ngay sau Save để scheduler/tính năng khác không cần parse lại file.
            var info = new FileInfo(IdentityToolStatePath);
            lock (_identityToolStateCacheLock)
            {
                _identityToolStateCache = CloneIdentityToolState(state);
                _identityToolStateCacheWriteUtc = info.LastWriteTimeUtc;
                _identityToolStateCacheLength = info.Length;
            }
        }
        catch (Exception ex) { _log.Warn("[IDENTITY_TOOL_STATE_SAVE] " + ex.Message); }
    }

    async Task<(bool Ok, string Error)> MarkIdentityDoneVerifiedAsync(
        string username, string profileName, CancellationToken ct)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0)
            return (false, "Không xác định được tài khoản để ghi DONE.");

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await RunAccountPoolIoAsync(
                    () => _accountPoolService.MarkIdentityDone(username),
                    ct);

                var verified = await RunAccountPoolIoAsync(
                    () => _accountPoolService.IsIdentityDone(username),
                    ct);
                if (!verified)
                    throw new InvalidOperationException("Đã ghi nhưng đọc lại Excel chưa thấy DONE.");

                _log.Info($"[IDENTITY_EXCEL_DONE_VERIFIED] profile={profileName} account={username} attempt={attempt}/3 verified=true");
                return (true, "");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn($"[IDENTITY_EXCEL_DONE_RETRY] profile={profileName} account={username} attempt={attempt}/3 error={ex.Message}");
                if (attempt < 3) await Task.Delay(450 * attempt, ct);
            }
        }

        return (false, lastError?.Message ?? "Không ghi được DONE vào Excel.");
    }

    async Task ResumeAutomationAfterIdentityDoneAsync(ProfileContext ctx, string previousRunState)
    {
        if (previousRunState != "RUNNING" && previousRunState != "PAUSED") return;

        // Không tự bật lại profile nếu trong lúc Tên/ảnh -> VIDEO người dùng đã
        // Stop/đóng profile, Dừng khẩn cấp hoặc Manager đang thoát. VIDEO chỉ được
        // phục hồi trạng thái chạy đã có trước pipeline, không được thắng ý định tay.
        if (IsAutomationHalted || _closing || IsManualCloseSuppressed(ctx.Profile.Name))
        {
            _log.Info(
                $"[AUTO_IDENTITY_RESUME_SKIP_MANUAL_OR_HALT] profile={ctx.Profile.Name} " +
                $"previous={previousRunState} halted={IsAutomationHalted} closing={_closing} " +
                $"manualSuppressed={IsManualCloseSuppressed(ctx.Profile.Name)}");
            return;
        }

        try
        {
            // Nếu pipeline không thực sự phải dừng automation (ví dụ Tên/ảnh và VIDEO
            // đều đã DONE), không gửi Start lặp vào một Worker đang chạy.
            try { await RefreshStatusAsync(ctx); } catch { }
            var currentRunState = GetLastConfirmedRuntimeState(ctx);
            if (currentRunState == previousRunState)
            {
                _log.Info(
                    $"[AUTO_IDENTITY_RESUME_NOT_NEEDED] profile={ctx.Profile.Name} state={currentRunState}");
                return;
            }

            var started = await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(35));
            if (!string.Equals(started, "started", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"[AUTO_IDENTITY_RESUME_BLOCKED] profile={ctx.Profile.Name} state={previousRunState} reply={started}");
                return;
            }

            if (previousRunState == "PAUSED")
            {
                await Task.Delay(300);
                await SendCommandAsync(ctx, "pause", TimeSpan.FromSeconds(8));
            }
            _log.Info($"[AUTO_IDENTITY_RESUME_AFTER_DONE] profile={ctx.Profile.Name} restored={previousRunState}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_IDENTITY_RESUME_FAILED] profile={ctx.Profile.Name} {ex.Message}");
        }
    }

    async Task<(bool Ok, string Username, string Error)> MarkManualIdentityDoneAsync(ProfileContext ctx)
    {
        string authUsername = "";
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            authUsername = (_tiktokAuthService.Load(dataRoot).Username ?? "").Trim();
        }
        catch (Exception ex)
        {
            _log.Warn($"[MANUAL_IDENTITY_EXCEL_AUTH_READ_WARN] profile={ctx.Profile.Name} error={ex.Message}");
        }

        List<ToolTikTokV12.Services.TikTokAccountPoolItem> accounts;
        try
        {
            accounts = await RunAccountPoolIoAsync(
                () => _accountPoolService.Load(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return (false, authUsername, "Không đọc được Kho tài khoản: " + ex.Message);
        }

        // Ưu tiên username đang lưu trong chính profile vì đây là tài khoản mà
        // Worker vừa cập nhật trên TikTok. Chỉ fallback sang AssignedProfile khi
        // auth local chưa có username để tránh ghi DONE nhầm sang tài khoản cũ.
        var account = !string.IsNullOrWhiteSpace(authUsername)
            ? accounts.FirstOrDefault(x => x.Username.Equals(authUsername, StringComparison.OrdinalIgnoreCase))
            : null;
        account ??= accounts.FirstOrDefault(x =>
            (x.AssignedProfile ?? "").Trim().Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            var detail = string.IsNullOrWhiteSpace(authUsername)
                ? $"Không tìm thấy tài khoản đang gán cho profile {ctx.Profile.Name}."
                : $"Không tìm thấy tài khoản {authUsername} trong file Excel hiện tại.";
            return (false, authUsername, detail);
        }

        var done = await MarkIdentityDoneVerifiedAsync(account.Username, ctx.Profile.Name, CancellationToken.None);
        if (done.Ok)
            _log.Info($"[MANUAL_IDENTITY_EXCEL_DONE] profile={ctx.Profile.Name} account={account.Username} verified=true");
        return (done.Ok, account.Username, done.Error);
    }

    async Task<bool> AcquireManualIdentitySlotAsync(string profileName, TimeSpan timeout)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0) return false;

        var deadline = DateTime.UtcNow + timeout;
        while (_autoIdentityInFlight.Contains(profileName))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(250);
        }

        // ShowTikTokIdentityDialog và scheduler chạy trên UI context. Không có await
        // giữa lần kiểm tra cuối và Add nên Auto Identity nền không thể chen vào.
        _autoIdentityInFlight.Add(profileName);
        return true;
    }

    async Task<(bool Ok, bool Done, string Username, string Error)> ReadManualIdentityDoneStateAsync(ProfileContext ctx)
    {
        string authUsername = "";
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            authUsername = (_tiktokAuthService.Load(dataRoot).Username ?? "").Trim();
        }
        catch (Exception ex)
        {
            _log.Warn($"[MANUAL_IDENTITY_DONE_READ_AUTH_WARN] profile={ctx.Profile.Name} error={ex.Message}");
        }

        try
        {
            var accounts = await RunAccountPoolIoAsync(
                () => _accountPoolService.Load(),
                CancellationToken.None);

            var account = !string.IsNullOrWhiteSpace(authUsername)
                ? accounts.FirstOrDefault(x => x.Username.Equals(authUsername, StringComparison.OrdinalIgnoreCase))
                : null;
            account ??= accounts.FirstOrDefault(x =>
                (x.AssignedProfile ?? "").Trim().Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase));

            if (account is null)
            {
                var detail = string.IsNullOrWhiteSpace(authUsername)
                    ? $"Không tìm thấy tài khoản đang gán cho profile {ctx.Profile.Name}."
                    : $"Không tìm thấy tài khoản {authUsername} trong file Excel hiện tại.";
                return (false, false, authUsername, detail);
            }

            var done = await RunAccountPoolIoAsync(
                () => _accountPoolService.IsIdentityDone(account.Username),
                CancellationToken.None);
            return (true, done, account.Username, "");
        }
        catch (Exception ex)
        {
            return (false, false, authUsername, ex.Message);
        }
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
        var videoPreviews = new Dictionary<string, VideoUploadPreview>(StringComparer.OrdinalIgnoreCase);
        var updateResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contexts = _contexts.Values.OrderBy(x => x.Profile.Name, NaturalProfileNameOrder).ToList();

        using var form = new Form
        {
            Text = "Tên, ảnh & video TikTok",
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
            RowCount = 6,
            Padding = new Padding(16),
            BackColor = ModernDialog.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
        // Khu cấu hình có viewport cuộn riêng để Tiểu sử/Tự động luôn truy cập được
        // trên màn hình thấp hoặc Windows DPI > 100%, không cần maximize dialog.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 270F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = "Tên, ảnh và video chạy độc lập với automation LIVE. Chọn thẻ cần xử lý, xem trước rồi mới thực hiện. Profile đang chạy automation sẽ được dừng an toàn trước khi cập nhật.",
            ForeColor = Color.FromArgb(55, 76, 103),
            Margin = new Padding(0, 0, 0, 8)
        };

        var tabTextColor = Color.FromArgb(42, 57, 76);
        var tabSelectedBackColor = Color.FromArgb(232, 242, 255);
        var tabSelectedTextColor = Color.FromArgb(28, 67, 111);
        var tabBorderColor = Color.FromArgb(205, 214, 224);

        Button SectionTab(string text)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Card,
                ForeColor = tabTextColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = Padding.Empty
            };
            button.FlatAppearance.BorderColor = tabBorderColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 245, 252);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(225, 236, 248);
            return button;
        }

        var sectionTabs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 8),
            Padding = Padding.Empty,
            BackColor = ModernDialog.Canvas
        };
        sectionTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        sectionTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        sectionTabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var identityTab = SectionTab("TÊN & ẢNH");
        var videoTab = SectionTab("VIDEO");
        identityTab.Margin = new Padding(0, 0, 7, 0);
        videoTab.Margin = new Padding(7, 0, 0, 0);
        sectionTabs.Controls.Add(identityTab, 0, 0);
        sectionTabs.Controls.Add(videoTab, 1, 0);

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
            Text = "Tự xử lý sau đăng nhập: Tên/ảnh → Video → chạy tool",
            Checked = state.AutoOnReady,
            AutoSize = true,
            Margin = new Padding(8, 8, 0, 0)
        };
        var autoHint = new Label
        {
            Text = "Tên/ảnh giữ nguyên logic DONE hiện tại. Sau đó VIDEO đọc cột VIDEO (XÓA|ĐĂNG); vế DONE được bỏ qua, FAIL được thử lại ở lần đăng nhập sau.",
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

        // ------------------------------------------------------------
        // THẺ VIDEO — đã nối logic XÓA video/bài cũ; phần đăng video mới sẽ nối sau.
        // ------------------------------------------------------------
        var videoConfig = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 6,
            Margin = new Padding(0, 0, 0, 12)
        };
        videoConfig.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        videoConfig.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        videoConfig.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        videoConfig.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));

        var enableDelete = new CheckBox
        {
            Text = "XÓA VIDEO CŨ",
            Checked = state.VideoDeleteEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 8, 22, 6)
        };
        var enableUpload = new CheckBox
        {
            Text = "ĐĂNG VIDEO MỚI",
            Checked = state.VideoUploadEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 8, 8, 6)
        };
        var videoActionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        videoActionPanel.Controls.Add(enableDelete);
        videoActionPanel.Controls.Add(enableUpload);

        var deleteNewest = new RadioButton { Text = "Xóa video mới nhất", Checked = string.Equals(state.VideoDeleteMode, "newest", StringComparison.OrdinalIgnoreCase), AutoSize = true, Margin = new Padding(0, 6, 18, 4) };
        var deleteAll = new RadioButton { Text = "Xóa tất cả video", Checked = !string.Equals(state.VideoDeleteMode, "newest", StringComparison.OrdinalIgnoreCase), AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        var deleteModePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        deleteModePanel.Controls.Add(deleteNewest);
        deleteModePanel.Controls.Add(deleteAll);

        var videoFolder = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = state.VideoFolder ?? "" };
        ModernDialog.StyleTextInput(videoFolder);
        var browseVideo = new Button { Text = "Chọn thư mục", Width = 138, Height = 36 };
        ModernDialog.StyleSecondaryButton(browseVideo);

        var videoRandom = new RadioButton { Text = "Random", Checked = !string.Equals(state.VideoSelectionMode, "sequence", StringComparison.OrdinalIgnoreCase) && !string.Equals(state.VideoSelectionMode, "fixed", StringComparison.OrdinalIgnoreCase), AutoSize = true, Margin = new Padding(0, 6, 18, 4) };
        var videoSequence = new RadioButton { Text = "Lần lượt", Checked = string.Equals(state.VideoSelectionMode, "sequence", StringComparison.OrdinalIgnoreCase), AutoSize = true, Margin = new Padding(0, 6, 18, 4) };
        var videoFixed = new RadioButton { Text = "Một video cố định", Checked = string.Equals(state.VideoSelectionMode, "fixed", StringComparison.OrdinalIgnoreCase), AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        var chooseModePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        chooseModePanel.Controls.Add(videoRandom);
        chooseModePanel.Controls.Add(videoSequence);
        chooseModePanel.Controls.Add(videoFixed);

        var videoCaption = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 68,
            Dock = DockStyle.Fill,
            Text = state.VideoCaptionText ?? ""
        };
        ModernDialog.StyleTextInput(videoCaption);
        var randomCaption = new CheckBox { Text = "Random caption", Checked = state.VideoRandomCaption, AutoSize = true, Margin = new Padding(0, 6, 18, 4) };
        var avoidLastVideo = new CheckBox { Text = "Tránh video vừa dùng", Checked = state.AvoidLastVideo, AutoSize = true, Margin = new Padding(0, 6, 18, 4) };
        var videoOnce = new CheckBox { Text = "Chỉ thực hiện 1 lần trong mỗi lượt chạy", Checked = state.VideoOncePerRun, AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        var videoOptionsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        videoOptionsPanel.Controls.Add(randomCaption);
        videoOptionsPanel.Controls.Add(avoidLastVideo);
        videoOptionsPanel.Controls.Add(videoOnce);

        videoConfig.Controls.Add(videoActionPanel, 0, 0);
        videoConfig.SetColumnSpan(videoActionPanel, 4);
        videoConfig.Controls.Add(new Label { Text = "Chế độ xóa", AutoSize = true, Margin = new Padding(0, 9, 8, 4) }, 0, 1);
        videoConfig.Controls.Add(deleteModePanel, 1, 1);
        videoConfig.SetColumnSpan(deleteModePanel, 3);
        videoConfig.Controls.Add(new Label { Text = "Nguồn video", AutoSize = true, Margin = new Padding(0, 9, 8, 4) }, 0, 2);
        videoConfig.Controls.Add(videoFolder, 1, 2);
        videoConfig.Controls.Add(browseVideo, 2, 2);
        videoConfig.SetColumnSpan(videoFolder, 1);
        videoConfig.Controls.Add(new Label { Text = "", AutoSize = true }, 3, 2);
        videoConfig.Controls.Add(new Label { Text = "Cách chọn video", AutoSize = true, Margin = new Padding(0, 9, 8, 4) }, 0, 3);
        videoConfig.Controls.Add(chooseModePanel, 1, 3);
        videoConfig.SetColumnSpan(chooseModePanel, 3);
        videoConfig.Controls.Add(new Label { Text = "Caption", AutoSize = true, Margin = new Padding(0, 9, 8, 4) }, 0, 4);
        videoConfig.Controls.Add(videoCaption, 1, 4);
        videoConfig.SetColumnSpan(videoCaption, 3);
        videoConfig.Controls.Add(new Label { Text = "Tùy chọn", AutoSize = true, Margin = new Padding(0, 9, 8, 4) }, 0, 5);
        videoConfig.Controls.Add(videoOptionsPanel, 1, 5);
        videoConfig.SetColumnSpan(videoOptionsPanel, 3);

        var videoConfigViewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0, 0, 8, 0),
            BackColor = ModernDialog.Canvas,
            Visible = false
        };
        videoConfigViewport.Controls.Add(videoConfig);

        var videoBody = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Visible = false
        };
        var videoGrid = new DataGridView
        {
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
        videoGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 239, 249);
        videoGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        videoGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "VideoUse", HeaderText = "Chọn", Width = 55 });
        videoGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "VideoProfile", HeaderText = "Profile", Width = 110, ReadOnly = true });
        videoGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "VideoPreview", HeaderText = "Video dự kiến", Width = 260, ReadOnly = true });
        videoGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "VideoDeleteMode", HeaderText = "Thao tác", Width = 215, ReadOnly = true });
        videoGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "VideoResult", HeaderText = "Kết quả", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 190, ReadOnly = true });
        foreach (var ctx in contexts)
        {
            var rowIndex = videoGrid.Rows.Add(ReferenceEquals(ctx, selected), ctx.Profile.Name, "—", "Xóa: tất cả | Đăng: tắt", "Chưa chạy");
            videoGrid.Rows[rowIndex].Tag = ctx;
        }
        videoBody.Panel1.Controls.Add(videoGrid);

        var videoPreviewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Color.FromArgb(245, 248, 252) };
        var videoPreviewTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Text = "Xem trước video",
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(42, 57, 76),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var videoPreviewHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 150,
            Text = "XÓA và ĐĂNG là hai thao tác độc lập.\r\n\r\nNếu XÓA lỗi, tool ghi lỗi rồi bỏ qua để sang bước ĐĂNG. Nếu ĐĂNG cũng lỗi, tool bỏ qua và trả profile về luồng hiện tại; không giữ profile ở trạng thái bận. Logic ĐĂNG sẽ được nối ở bước sau.",
            ForeColor = Color.DimGray,
            AutoEllipsis = true
        };
        videoPreviewPanel.Controls.Add(videoPreviewHint);
        videoPreviewPanel.Controls.Add(videoPreviewTitle);
        videoBody.Panel2.Controls.Add(videoPreviewPanel);

        void FitVideoSplitter()
        {
            if (videoBody.IsDisposed) return;
            var width = videoBody.ClientSize.Width;
            var available = width - videoBody.SplitterWidth;
            if (available < 420) return;
            var panel2Min = Math.Min(210, Math.Max(140, available / 4));
            var panel1Min = Math.Min(620, Math.Max(300, available - panel2Min - 80));
            if (panel1Min + panel2Min > available) return;
            videoBody.Panel2MinSize = panel2Min;
            videoBody.Panel1MinSize = panel1Min;
            var max = width - videoBody.Panel2MinSize - videoBody.SplitterWidth;
            var preferred = (int)Math.Round(width * 0.76);
            videoBody.SplitterDistance = Math.Clamp(preferred, videoBody.Panel1MinSize, max);
        }

        var videoTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 6), Visible = false };
        var videoSelectAll = new Button { Text = "Chọn tất cả", Width = 112, Height = 36 };
        var videoClearAll = new Button { Text = "Bỏ chọn", Width = 100, Height = 36 };
        var videoRandomize = new Button { Text = "Random lại", Width = 112, Height = 36, Enabled = false };
        ModernDialog.StyleSecondaryButton(videoSelectAll);
        ModernDialog.StyleSecondaryButton(videoClearAll);
        ModernDialog.StyleSecondaryButton(videoRandomize);
        videoTools.Controls.Add(videoSelectAll);
        videoTools.Controls.Add(videoClearAll);
        videoTools.Controls.Add(videoRandomize);

        var activeSection = 0;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        var close = new Button { Text = "Đóng", Width = 104, Height = 42, DialogResult = DialogResult.Cancel };
        var stopBatch = new Button { Text = "Dừng", Width = 104, Height = 42, Enabled = false };
        var apply = new Button { Text = "Cập nhật đã chọn", Width = 164, Height = 42 };
        var updateInProgress = false;
        var batchStopRequested = false;
        CancellationTokenSource? manualVideoBatchStopCts = null;
        ModernDialog.StyleSecondaryButton(close);
        ModernDialog.StyleSecondaryButton(stopBatch);
        ModernDialog.StylePrimaryButton(apply);
        footer.Controls.Add(close);
        footer.Controls.Add(stopBatch);
        footer.Controls.Add(apply);

        var configHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ModernDialog.Canvas };
        configHost.Controls.Add(configViewport);
        configHost.Controls.Add(videoConfigViewport);
        var bodyHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ModernDialog.Canvas };
        bodyHost.Controls.Add(body);
        bodyHost.Controls.Add(videoBody);
        var toolsHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ModernDialog.Canvas };
        toolsHost.Controls.Add(tools);
        toolsHost.Controls.Add(videoTools);

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(sectionTabs, 0, 1);
        root.Controls.Add(configHost, 0, 2);
        root.Controls.Add(bodyHost, 0, 3);
        root.Controls.Add(toolsHost, 0, 4);
        root.Controls.Add(footer, 0, 5);
        form.Controls.Add(root);
        form.CancelButton = close;

        void UpdateSectionTabs()
        {
            var tabs = new[] { identityTab, videoTab };
            for (var i = 0; i < tabs.Length; i++)
            {
                var active = activeSection == i;
                tabs[i].BackColor = active ? tabSelectedBackColor : UiTheme.Card;
                tabs[i].ForeColor = active ? tabSelectedTextColor : tabTextColor;
                tabs[i].FlatAppearance.BorderColor = active ? ActiveProfileColor : tabBorderColor;
                tabs[i].FlatAppearance.BorderSize = active ? 2 : 1;
            }
        }

        void ShowSection(int section)
        {
            activeSection = Math.Clamp(section, 0, 1);
            var identityVisible = activeSection == 0;

            configViewport.Visible = identityVisible;
            body.Visible = identityVisible;
            tools.Visible = identityVisible;
            videoConfigViewport.Visible = !identityVisible;
            videoBody.Visible = !identityVisible;
            videoTools.Visible = !identityVisible;

            if (identityVisible)
            {
                configViewport.BringToFront();
                body.BringToFront();
                tools.BringToFront();
                intro.Text = "Mục này chạy độc lập với automation LIVE. Chọn profile, nhập danh sách tên và/hoặc chọn thư mục ảnh, xem trước rồi mới bấm Cập nhật. Profile đang chạy automation sẽ được dừng trước khi đổi hồ sơ.";
                apply.Text = "Cập nhật đã chọn";
                apply.Enabled = !updateInProgress;
            }
            else
            {
                videoConfigViewport.BringToFront();
                videoBody.BringToFront();
                videoTools.BringToFront();
                intro.Text = "Thẻ VIDEO tách riêng XÓA và ĐĂNG. Có thể bật một hoặc cả hai. Lỗi ở bước XÓA/ĐĂNG chỉ được ghi nhận rồi bỏ qua; profile phải được trả về luồng hiện tại thay vì bị treo.";
                apply.Text = "Thực hiện đã chọn";
                apply.Enabled = !updateInProgress;
                FitVideoSplitter();
            }

            UpdateSectionTabs();
        }

        identityTab.Click += (_, _) => ShowSection(0);
        videoTab.Click += (_, _) => ShowSection(1);
        stopBatch.Click += (_, _) =>
        {
            if (!updateInProgress || batchStopRequested) return;

            batchStopRequested = true;
            stopBatch.Enabled = false;
            stopBatch.Text = "Đang dừng...";
            try { manualVideoBatchStopCts?.Cancel(); } catch { }
            _log.Info($"[MANUAL_BATCH_STOP_REQUEST] section={(activeSection == 1 ? "VIDEO" : "IDENTITY")}");
        };
        videoSelectAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in videoGrid.Rows) row.Cells["VideoUse"].Value = true;
        };
        videoClearAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in videoGrid.Rows) row.Cells["VideoUse"].Value = false;
        };
        videoGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (videoGrid.IsCurrentCellDirty)
                videoGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        videoGrid.SelectionChanged += (_, _) => ShowVideoRowPreview();
        browseVideo.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Chọn thư mục chứa video TikTok" };
            if (Directory.Exists(videoFolder.Text)) picker.SelectedPath = videoFolder.Text;
            if (picker.ShowDialog(form) != DialogResult.OK) return;
            videoFolder.Text = picker.SelectedPath;
            SaveVideoUiState();
            RebuildVideoPreview();
        };
        videoRandomize.Click += (_, _) => RebuildVideoPreview();
        videoRandom.CheckedChanged += (_, _) => { if (videoRandom.Checked) { SaveVideoUiState(); RebuildVideoPreview(); } };
        videoSequence.CheckedChanged += (_, _) => { if (videoSequence.Checked) { SaveVideoUiState(); RebuildVideoPreview(); } };
        videoFixed.CheckedChanged += (_, _) => { if (videoFixed.Checked) { SaveVideoUiState(); RebuildVideoPreview(); } };
        randomCaption.CheckedChanged += (_, _) => { SaveVideoUiState(); RebuildVideoPreview(); };
        avoidLastVideo.CheckedChanged += (_, _) => { SaveVideoUiState(); RebuildVideoPreview(); };
        videoOnce.CheckedChanged += (_, _) => SaveVideoUiState();
        videoCaption.TextChanged += (_, _) => { SaveVideoUiState(); RebuildVideoPreview(); };
        void RefreshVideoActionState()
        {
            var deleteText = !enableDelete.Checked
                ? "Xóa: tắt"
                : deleteAll.Checked ? "Xóa: tất cả" : "Xóa: mới nhất";
            var uploadText = enableUpload.Checked ? "Đăng: bật" : "Đăng: tắt";
            foreach (DataGridViewRow row in videoGrid.Rows)
                row.Cells["VideoDeleteMode"].Value = $"{deleteText} | {uploadText}";

            deleteNewest.Enabled = enableDelete.Checked;
            deleteAll.Enabled = enableDelete.Checked;

            videoFolder.Enabled = enableUpload.Checked;
            browseVideo.Enabled = enableUpload.Checked;
            videoRandom.Enabled = enableUpload.Checked;
            videoSequence.Enabled = enableUpload.Checked;
            videoFixed.Enabled = enableUpload.Checked;
            videoCaption.Enabled = enableUpload.Checked;
            randomCaption.Enabled = enableUpload.Checked;
            avoidLastVideo.Enabled = enableUpload.Checked;
            videoOnce.Enabled = enableDelete.Checked || enableUpload.Checked;
            videoRandomize.Enabled = enableUpload.Checked;

            videoPreviewHint.Text = enableUpload.Checked
                ? "XÓA và ĐĂNG chạy độc lập theo kiểu best-effort. ĐĂNG đi thẳng URL TikTok Studio, gán file trực tiếp, không mở hộp chọn file Windows; 2 mục Kiểm tra được giữ TẮT. Nếu một bước lỗi, tool ghi lỗi rồi bỏ qua để trả profile về luồng hiện tại."
                : "Phần XÓA chạy độc lập và có timeout an toàn cho VPS chậm. Nếu xóa lỗi/timeout, tool ghi lỗi, dọn trạng thái và trả profile về luồng hiện tại; không giữ Worker ở trạng thái bận.";
            RebuildVideoPreview();
        }
        enableDelete.CheckedChanged += (_, _) => { SaveVideoUiState(); RefreshVideoActionState(); };
        enableUpload.CheckedChanged += (_, _) => { SaveVideoUiState(); RefreshVideoActionState(); RebuildVideoPreview(); };
        deleteNewest.CheckedChanged += (_, _) => { if (deleteNewest.Checked) SaveVideoUiState(); RefreshVideoActionState(); };
        deleteAll.CheckedChanged += (_, _) => { if (deleteAll.Checked) SaveVideoUiState(); RefreshVideoActionState(); };

        RefreshVideoActionState();

        form.Shown += (_, _) =>
        {
            FitIdentitySplitter();
            FitVideoSplitter();
            // Bảo đảm viewport biết toàn bộ chiều cao config sau scale DPI.
            configViewport.AutoScrollMinSize = new Size(0, Math.Max(config.PreferredSize.Height + 8, 300));
            videoConfigViewport.AutoScrollMinSize = new Size(0, Math.Max(videoConfig.PreferredSize.Height + 8, 300));
            ShowSection(0);
        };
        body.SizeChanged += (_, _) => FitIdentitySplitter();
        videoBody.SizeChanged += (_, _) => FitVideoSplitter();
        config.SizeChanged += (_, _) =>
            configViewport.AutoScrollMinSize = new Size(0, Math.Max(config.PreferredSize.Height + 8, 300));
        videoConfig.SizeChanged += (_, _) =>
            videoConfigViewport.AutoScrollMinSize = new Size(0, Math.Max(videoConfig.PreferredSize.Height + 8, 300));

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

        static List<string> ReadVideos(string videoSourceFolder)
        {
            if (string.IsNullOrWhiteSpace(videoSourceFolder) || !Directory.Exists(videoSourceFolder)) return new();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".mov", ".webm", ".m4v", ".avi", ".mkv", ".mpeg", ".mpg", ".mpe", ".ogm"
            };
            return Directory.EnumerateFiles(videoSourceFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(x => allowed.Contains(Path.GetExtension(x)))
                .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static List<string> ReadVideoCaptions(TextBox box) => box.Lines
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        string ResolveVideoAccountKey(ProfileContext ctx)
        {
            try
            {
                var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
                var username = (_tiktokAuthService.Load(dataRoot).Username ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(username)) return username.TrimStart('@').ToLowerInvariant();
            }
            catch { }
            return "profile:" + ctx.Profile.Name.ToLowerInvariant();
        }

        void SaveVideoUiState()
        {
            // Giữ đồng bộ cả phần TÊN & ẢNH để việc chỉnh VIDEO không ghi đè
            // các thay đổi người dùng vừa nhập nhưng chưa bấm Cập nhật.
            state.NamesText = names.Text;
            state.ImageFolder = folder.Text;
            state.UpdateName = updateName.Checked;
            state.UpdateAvatar = updateAvatar.Checked;
            state.BioText = bio.Text.Trim();
            state.UpdateBio = updateBio.Checked;
            state.AutoOnReady = autoOnReady.Checked;
            state.RandomNames = randomNames.Checked;
            state.AvoidLastAvatar = avoidLast.Checked;

            state.VideoFolder = videoFolder.Text.Trim();
            state.VideoCaptionText = videoCaption.Text;
            state.VideoSelectionMode = videoFixed.Checked ? "fixed" : videoSequence.Checked ? "sequence" : "random";
            state.VideoRandomCaption = randomCaption.Checked;
            state.AvoidLastVideo = avoidLastVideo.Checked;
            state.VideoOncePerRun = videoOnce.Checked;
            state.VideoDeleteEnabled = enableDelete.Checked;
            state.VideoUploadEnabled = enableUpload.Checked;
            state.VideoDeleteMode = deleteNewest.Checked ? "newest" : "all";
            SaveIdentityToolState(state);
        }

        void ShowVideoRowPreview()
        {
            if (videoGrid.CurrentRow?.Tag is not ProfileContext ctx || !videoPreviews.TryGetValue(ctx.Profile.Name, out var preview))
            {
                videoPreviewHint.Text = enableUpload.Checked
                    ? "Chọn một dòng để xem video dự kiến. XÓA và ĐĂNG chạy độc lập theo kiểu best-effort."
                    : "Phần XÓA chạy độc lập và có timeout an toàn; lỗi xóa không được giữ Worker ở trạng thái bận.";
                return;
            }

            var fileText = string.IsNullOrWhiteSpace(preview.VideoPath) ? "(chưa chọn)" : Path.GetFileName(preview.VideoPath);
            var captionText = string.IsNullOrWhiteSpace(preview.Caption) ? "(trống)" : preview.Caption;
            videoPreviewHint.Text = $"Profile: {ctx.Profile.Name}\r\nVideo: {fileText}\r\nCaption: {captionText}\r\n\r\nĐăng: đi thẳng TikTok Studio, gán file trực tiếp, giữ 2 mục Kiểm tra ở TẮT; thấy thông báo 'Đã đăng video' là DONE, không đổi/verify quyền riêng tư.";
        }

        void RebuildVideoPreview()
        {
            videoGrid.EndEdit();
            videoPreviews.Clear();
            var videos = ReadVideos(videoFolder.Text);
            var captions = ReadVideoCaptions(videoCaption);
            var shuffled = videos.OrderBy(_ => Random.Shared.Next()).ToList();
            var randomCursor = 0;
            var fixedVideo = videos.Count == 0 ? "" : videos[Random.Shared.Next(videos.Count)];
            var selectedIndex = 0;

            foreach (DataGridViewRow row in videoGrid.Rows)
            {
                if (row.Tag is not ProfileContext ctx) continue;
                string picked = "";
                if (enableUpload.Checked && videos.Count > 0)
                {
                    if (videoFixed.Checked)
                    {
                        picked = fixedVideo;
                    }
                    else if (videoSequence.Checked)
                    {
                        picked = videos[selectedIndex % videos.Count];
                    }
                    else
                    {
                        if (randomCursor >= shuffled.Count)
                        {
                            shuffled = videos.OrderBy(_ => Random.Shared.Next()).ToList();
                            randomCursor = 0;
                        }
                        picked = shuffled[randomCursor++];
                    }

                    if (avoidLastVideo.Checked && videos.Count > 1)
                    {
                        var accountKey = ResolveVideoAccountKey(ctx);
                        if (state.LastVideoByAccount.TryGetValue(accountKey, out var last)
                            && !string.IsNullOrWhiteSpace(last)
                            && string.Equals(Path.GetFullPath(last), Path.GetFullPath(picked), StringComparison.OrdinalIgnoreCase))
                        {
                            var replacement = videos.FirstOrDefault(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(last), StringComparison.OrdinalIgnoreCase));
                            if (!string.IsNullOrWhiteSpace(replacement)) picked = replacement;
                        }
                    }
                }

                string caption = "";
                if (captions.Count == 1) caption = captions[0];
                else if (captions.Count > 1) caption = randomCaption.Checked
                    ? captions[Random.Shared.Next(captions.Count)]
                    : captions[selectedIndex % captions.Count];

                videoPreviews[ctx.Profile.Name] = new VideoUploadPreview(ctx, picked, caption);
                row.Cells["VideoPreview"].Value = string.IsNullOrWhiteSpace(picked) ? "—" : Path.GetFileName(picked);
                selectedIndex++;
            }
            ShowVideoRowPreview();
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
            if (activeSection == 1)
            {
                try
                {
                    videoGrid.EndEdit();
                    var selectedVideoRows = videoGrid.Rows.Cast<DataGridViewRow>()
                        .Where(r => r.Tag is ProfileContext
                            && Convert.ToBoolean(r.Cells["VideoUse"].Value ?? false))
                        .ToList();

                    if (selectedVideoRows.Count == 0)
                    {
                        ModernDialog.ShowMessage(form, "Hãy chọn ít nhất một profile.", "Video TikTok", MessageBoxIcon.Information);
                        return;
                    }

                    if (!enableDelete.Checked && !enableUpload.Checked)
                    {
                        ModernDialog.ShowMessage(form, "Hãy bật ít nhất một thao tác: XÓA VIDEO CŨ hoặc ĐĂNG VIDEO MỚI.", "Video TikTok", MessageBoxIcon.Information);
                        return;
                    }

                    if (enableUpload.Checked)
                    {
                        SaveVideoUiState();
                        var availableVideos = ReadVideos(videoFolder.Text);
                        if (availableVideos.Count == 0)
                        {
                            ModernDialog.ShowMessage(form, "Thư mục nguồn chưa có video hợp lệ. Hãy chọn thư mục có MP4/MOV/WEBM...", "Video TikTok", MessageBoxIcon.Information);
                            return;
                        }
                        var missingPreview = selectedVideoRows.FirstOrDefault(r =>
                            r.Tag is ProfileContext c
                            && (!videoPreviews.TryGetValue(c.Profile.Name, out var p)
                                || string.IsNullOrWhiteSpace(p.VideoPath)
                                || !File.Exists(p.VideoPath)));
                        if (missingPreview is not null)
                        {
                            ModernDialog.ShowMessage(form, "Có profile chưa được gán video dự kiến. Bấm Random lại hoặc kiểm tra thư mục nguồn.", "Video TikTok", MessageBoxIcon.Warning);
                            return;
                        }
                    }

                    // Nếu vừa XÓA vừa ĐĂNG thì XÓA luôn chạy trước, vì vậy phải
                    // xóa sạch toàn bộ bài cũ. Khi chỉ bật XÓA mới giữ lựa chọn
                    // newest/all của giao diện.
                    var deleteMode = enableDelete.Checked
                        ? (enableUpload.Checked ? "all" : (deleteAll.Checked ? "all" : "newest"))
                        : "none";

                    if (enableDelete.Checked)
                    {
                        var modeText = deleteMode == "all" ? "XÓA TẤT CẢ video/bài cũ" : "XÓA video/bài mới nhất";
                        var confirm = $"Sẽ {modeText} trên {selectedVideoRows.Count} profile đã chọn.\n\n"
                            + "Nếu bước XÓA lỗi hoặc timeout, tool sẽ ghi lỗi rồi bỏ qua bước xóa để tiếp tục bước ĐĂNG (nếu được bật), sau đó trả profile về luồng hiện tại.\n\n"
                            + "XÓA VIDEO KHÔNG THỂ HOÀN TÁC. Tiếp tục?";
                        if (ModernDialog.ShowConfirm(form, confirm, "Xác nhận xóa video TikTok") != DialogResult.Yes)
                            return;
                    }

                    updateInProgress = true;
                    batchStopRequested = false;
                    manualVideoBatchStopCts?.Dispose();
                    manualVideoBatchStopCts = new CancellationTokenSource();
                    apply.Enabled = false;
                    stopBatch.Text = "Dừng";
                    stopBatch.Enabled = true;
                    close.Enabled = false;
                    identityTab.Enabled = false;
                    videoTab.Enabled = false;
                    videoSelectAll.Enabled = false;
                    videoClearAll.Enabled = false;
                    videoGrid.Enabled = false;

                    var success = 0;
                    var failed = 0;
                    var skipped = 0;
                    // Chỉ sống trong đúng lần bấm "Thực hiện đã chọn" này.
                    // Bấm chạy lại lần sau sẽ tạo HashSet mới, nên cùng tài khoản vẫn được phép xử lý lại.
                    var processedVideoAccountsThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var startedVideoProfiles = 0;
                    foreach (var row in selectedVideoRows)
                    {
                        if (batchStopRequested) break;
                        if (row.Tag is not ProfileContext ctx) continue;
                        startedVideoProfiles++;

                        var runAccountKey = ResolveVideoAccountKey(ctx);
                        if (videoOnce.Checked && !processedVideoAccountsThisRun.Add(runAccountKey))
                        {
                            skipped++;
                            row.Cells["VideoResult"].Value = "Bỏ qua: tài khoản đã xử lý trong lượt chạy này";
                            SetRowColorIfAttached(row, Color.DimGray);
                            _log.Info($"[VIDEO_ONCE_PER_RUN_SKIP] profile={ctx.Profile.Name} account={runAccountKey}");
                            continue;
                        }

                        row.Cells["VideoResult"].Value = "Đang chuẩn bị / dừng automation...";
                        SetRowColorIfAttached(row, Color.DarkOrange);
                        form.Refresh();

                        try
                        {
                            var parts = new List<string>();
                            var profileHadError = false;
                            var skipUploadBecauseLoginRequired = false;
                            // Xóa dùng trực tiếp TikTok Studio ngay từ đầu; không còn
                            // nhánh profile-delete rồi fallback sau upload.
                            // Trạng thái cuối dùng để ghi cột VIDEO trong Excel cho batch chạy tay.
                            // Dùng cùng chuẩn OFF/FAIL/DONE với Auto VIDEO để hai luồng không lệch nhau.
                            var manualDeleteStatus = enableDelete.Checked ? "FAIL" : "OFF";
                            var manualUploadStatus = enableUpload.Checked ? "FAIL" : "OFF";

                            if (enableDelete.Checked)
                            {
                                try
                                {
                                    var reply = await DeleteTikTokVideosAsync(ctx, deleteMode, progress =>
                                    {
                                        var text = string.IsNullOrWhiteSpace(progress.Message)
                                            ? progress.Stage
                                            : progress.Message;
                                        if (!string.IsNullOrWhiteSpace(text))
                                            row.Cells["VideoResult"].Value = "Xóa: " + text;
                                        SetRowColorIfAttached(row, Color.DarkOrange);
                                        videoGrid.Refresh();
                                    }, cancellationToken: manualVideoBatchStopCts!.Token);

                                    if (reply.Ok)
                                    {
                                        manualDeleteStatus = "DONE";
                                        parts.Add($"Xóa: OK ({reply.DeletedCount})");
                                    }
                                    else if (IsVideoLoginRequired(reply))
                                    {
                                        profileHadError = true;
                                        skipUploadBecauseLoginRequired = true;
                                        parts.Add("Xóa: bỏ qua (mất login)");
                                        _log.Warn(
                                            $"[VIDEO_DELETE_LOGIN_REQUIRED_SKIP_UPLOAD] profile={ctx.Profile.Name} " +
                                            $"stage={reply.Stage} error={reply.Error}");
                                    }
                                    else
                                    {
                                        var detail = string.IsNullOrWhiteSpace(reply.Error) ? "lỗi không xác định" : reply.Error;
                                        profileHadError = true;
                                        parts.Add($"Xóa: lỗi sau {reply.DeletedCount} bài");
                                        _log.Warn($"[VIDEO_DELETE_STUDIO_BEST_EFFORT_SKIP] profile={ctx.Profile.Name} deleted={reply.DeletedCount} error={detail} action=CONTINUE_UPLOAD");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    profileHadError = true;
                                    parts.Add("Xóa: lỗi");
                                    _log.Warn($"[VIDEO_DELETE_STUDIO_MANAGER_FAILED] profile={ctx.Profile.Name} error={ex.Message} action=CONTINUE_UPLOAD");
                                }
                            }
                            else
                            {
                                parts.Add("Xóa: bỏ qua");
                            }

                            if (batchStopRequested)
                            {
                                skipped++;
                                parts.Add("Đăng: bỏ qua (đã dừng)");
                                row.Cells["VideoResult"].Value = string.Join(" | ", parts) + " | Đã dừng theo yêu cầu";
                                SetRowColorIfAttached(row, Color.DarkOrange);
                                _log.Info($"[MANUAL_VIDEO_BATCH_STOPPED] profile={ctx.Profile.Name} stage=after_delete remaining={Math.Max(0, selectedVideoRows.Count - startedVideoProfiles)}");
                                break;
                            }

                            // Fail-open: lỗi XÓA thông thường không chặn bước ĐĂNG.
                            // Riêng LOGIN_REQUIRED thì bỏ luôn phần ĐĂNG để trả Chrome ngay cho
                            // logic đăng nhập hiện tại của tool; thử upload khi đã mất login chỉ
                            // làm tốn thời gian và có thể sinh thêm popup/timeout không cần thiết.
                            if (enableUpload.Checked && skipUploadBecauseLoginRequired)
                            {
                                profileHadError = true;
                                parts.Add("Đăng: bỏ qua (mất login)");
                                _log.Warn(
                                    $"[VIDEO_UPLOAD_SKIP_LOGIN_REQUIRED] profile={ctx.Profile.Name} account={runAccountKey}");
                            }
                            else if (enableUpload.Checked)
                            {
                                var accountKey = runAccountKey;
                                if (!videoPreviews.TryGetValue(ctx.Profile.Name, out var preview)
                                    || string.IsNullOrWhiteSpace(preview.VideoPath)
                                    || !File.Exists(preview.VideoPath))
                                {
                                    profileHadError = true;
                                    parts.Add("Đăng: thiếu file");
                                    _log.Warn($"[VIDEO_UPLOAD_PREVIEW_MISSING] profile={ctx.Profile.Name}");
                                }
                                else
                                {
                                    try
                                    {
                                        var uploadReply = await UploadTikTokVideoAsync(ctx, preview.VideoPath, preview.Caption, progress =>
                                        {
                                            var text = string.IsNullOrWhiteSpace(progress.Message) ? progress.Stage : progress.Message;
                                            if (!string.IsNullOrWhiteSpace(text))
                                                row.Cells["VideoResult"].Value = "Đăng: " + text;
                                            SetRowColorIfAttached(row, Color.DarkOrange);
                                            videoGrid.Refresh();
                                        },
                                        studioDeleteFallback: false,
                                        cancellationToken: manualVideoBatchStopCts!.Token);

                                        if (batchStopRequested || string.Equals(uploadReply.Stage, "USER_STOPPED", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (uploadReply.Posted)
                                            {
                                                state.LastVideoByAccount[accountKey] = preview.VideoPath;
                                                SaveIdentityToolState(state);
                                            }
                                            skipped++;
                                            parts.Add(uploadReply.Posted ? "Đăng: đã dừng sau khi bài đã lên" : "Đăng: đã dừng");
                                            row.Cells["VideoResult"].Value = string.Join(" | ", parts) + " | Đã dừng theo yêu cầu";
                                            SetRowColorIfAttached(row, Color.DarkOrange);
                                            _log.Info($"[MANUAL_VIDEO_BATCH_STOPPED] profile={ctx.Profile.Name} stage=upload remaining={Math.Max(0, selectedVideoRows.Count - startedVideoProfiles)} posted={uploadReply.Posted}");
                                            break;
                                        }

                                        if (uploadReply.Posted)
                                        {
                                            state.LastVideoByAccount[accountKey] = preview.VideoPath;
                                            SaveIdentityToolState(state);
                                        }

                                        var manualUploadDone = uploadReply.Ok && uploadReply.Posted;
                                        manualUploadStatus = manualUploadDone ? "DONE" : "FAIL";

                                        if (manualUploadDone)
                                        {
                                            parts.Add("Đăng: DONE (đã thấy 'Đã đăng video')");
                                        }
                                        else if (uploadReply.Posted)
                                        {
                                            profileHadError = true;
                                            parts.Add("Đăng: đã thấy bài lên nhưng Worker chưa xác nhận DONE");
                                            _log.Warn($"[VIDEO_UPLOAD_PARTIAL] profile={ctx.Profile.Name} posted=true error={uploadReply.Error}");
                                        }
                                        else
                                        {
                                            profileHadError = true;
                                            parts.Add("Đăng: lỗi");
                                            _log.Warn($"[VIDEO_UPLOAD_BEST_EFFORT_SKIP] profile={ctx.Profile.Name} error={uploadReply.Error}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        profileHadError = true;
                                        parts.Add("Đăng: lỗi");
                                        _log.Warn($"[VIDEO_UPLOAD_MANAGER_FAILED] profile={ctx.Profile.Name} error={ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                parts.Add("Đăng: bỏ qua");
                            }

                            // Batch VIDEO chạy tay trước đây chỉ cập nhật cột Kết quả trên giao diện,
                            // nên dù XÓA/ĐĂNG đã thành công thì cột VIDEO trong Excel vẫn trống.
                            // Ghi lại đúng kết quả cuối bằng CHÍNH helper của Auto VIDEO, có retry +
                            // đọc lại Excel xác minh để không báo DONE giả. Lỗi ghi Excel chỉ là cảnh báo,
                            // không được biến một lượt đăng video thành lỗi hay chặn trả PRF về luồng hiện tại.
                            try
                            {
                                var excelAccount = await ResolveNameGuardAccountAsync(ctx);
                                var excelUsername = (excelAccount.Username ?? "").Trim();
                                if (excelUsername.Length == 0)
                                {
                                    _log.Warn(
                                        $"[MANUAL_VIDEO_EXCEL_SKIP] profile={ctx.Profile.Name} " +
                                        $"status={ComposeVideoStatus(manualDeleteStatus, manualUploadStatus)} reason=account_not_resolved");
                                }
                                else
                                {
                                    var excelWrite = await WriteVideoStatusVerifiedAsync(
                                        excelUsername,
                                        ctx.Profile.Name,
                                        manualDeleteStatus,
                                        manualUploadStatus,
                                        CancellationToken.None);

                                    if (excelWrite.Ok)
                                    {
                                        _log.Info(
                                            $"[MANUAL_VIDEO_EXCEL_DONE] profile={ctx.Profile.Name} account={excelUsername} " +
                                            $"status={excelWrite.Status}");
                                    }
                                    else
                                    {
                                        _log.Warn(
                                            $"[MANUAL_VIDEO_EXCEL_WARN] profile={ctx.Profile.Name} account={excelUsername} " +
                                            $"status={excelWrite.Status} error={excelWrite.Error}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.Warn(
                                    $"[MANUAL_VIDEO_EXCEL_WARN] profile={ctx.Profile.Name} " +
                                    $"status={ComposeVideoStatus(manualDeleteStatus, manualUploadStatus)} error={ex.Message}");
                            }

                            if (profileHadError) failed++; else success++;
                            row.Cells["VideoResult"].Value = string.Join(" | ", parts) + " | Đã trả về luồng hiện tại";
                            SetRowColorIfAttached(row, profileHadError ? Color.DarkOrange : Color.DarkGreen);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            row.Cells["VideoResult"].Value = "Lỗi video đã được bỏ qua: " + ex.Message;
                            SetRowColorIfAttached(row, Color.DarkOrange);
                            _log.Warn($"[VIDEO_BEST_EFFORT_MANAGER_FAILED] profile={ctx.Profile.Name} error={ex.Message}");
                        }
                    }

                    var remainingVideoProfiles = Math.Max(0, selectedVideoRows.Count - startedVideoProfiles);
                    var videoSummary = batchStopRequested
                        ? $"Đã dừng theo yêu cầu. Đã bắt đầu: {startedVideoProfiles}/{selectedVideoRows.Count}; còn chưa chạy: {remainingVideoProfiles}. Thành công: {success}; bỏ qua/dừng: {skipped}; có lỗi đã bỏ qua: {failed}.\n\nProfile đang thao tác được dừng bằng lệnh VIDEO riêng và được trả về luồng hiện tại; tool không lấy thêm profile mới."
                        : $"Đã xử lý xong. Thành công: {success}; bỏ qua trong lượt: {skipped}; có lỗi đã bỏ qua: {failed}.\n\nXÓA/ĐĂNG đều chạy best-effort: lỗi ở bước nào được ghi nhận rồi bỏ qua, profile vẫn được trả về luồng hiện tại.";
                    ModernDialog.ShowMessage(
                        form,
                        videoSummary,
                        "Video TikTok",
                        failed == 0 && !batchStopRequested ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                catch (Exception ex)
                {
                    ModernDialog.ShowMessage(form, ex.Message, "Video TikTok", MessageBoxIcon.Error);
                }
                finally
                {
                    updateInProgress = false;
                    try { manualVideoBatchStopCts?.Dispose(); } catch { }
                    manualVideoBatchStopCts = null;
                    batchStopRequested = false;
                    stopBatch.Text = "Dừng";
                    stopBatch.Enabled = false;
                    close.Enabled = true;
                    identityTab.Enabled = true;
                    videoTab.Enabled = true;
                    videoSelectAll.Enabled = true;
                    videoClearAll.Enabled = true;
                    videoGrid.Enabled = true;
                    ShowSection(activeSection);
                }
                return;
            }

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
                batchStopRequested = false;
                apply.Enabled = false;
                stopBatch.Text = "Dừng";
                stopBatch.Enabled = true;
                randomize.Enabled = false;
                identityTab.Enabled = false;
                videoTab.Enabled = false;
                close.Enabled = false;
                var success = 0;
                var skipped = 0;
                var failed = 0;
                var excelWarnings = 0;
                var startedIdentityProfiles = 0;
                foreach (var row in selectedRows)
                {
                    if (batchStopRequested) break;
                    if (row.Tag is not ProfileContext ctx || !previews.TryGetValue(ctx.Profile.Name, out var preview)) continue;
                    startedIdentityProfiles++;
                    updateResults[ctx.Profile.Name] = "Đang xử lý...";
                    TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                    var profileCell = TryGetGridCell(row, profileColumn, "ShowTikTokIdentityDialog.Apply");
                    if (profileCell is not null) grid.CurrentCell = profileCell;
                    grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index);
                    Application.DoEvents();
                    var manualIdentitySlotAcquired = false;
                    var identityDoneBeforeKnown = false;
                    var identityDoneBefore = false;
                    try
                    {
                        // Manual và Auto Identity phải dùng chung một khóa theo profile.
                        // Nếu Auto nền đang làm dở thì chờ nó kết thúc; sau khi manual giữ
                        // khóa, scheduler sẽ bỏ qua profile này cho tới khi finally nhả khóa.
                        manualIdentitySlotAcquired = await AcquireManualIdentitySlotAsync(
                            ctx.Profile.Name, TimeSpan.FromSeconds(45));
                        if (!manualIdentitySlotAcquired)
                        {
                            updateResults[ctx.Profile.Name] = "Bỏ qua: profile đang được Auto Tên/ảnh xử lý";
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                            SetRowColorIfAttached(row, Color.DarkOrange);
                            skipped++;
                            _log.Warn($"[MANUAL_IDENTITY_SLOT_TIMEOUT] profile={ctx.Profile.Name} waited=45s");
                            continue;
                        }

                        if (batchStopRequested)
                        {
                            skipped++;
                            updateResults[ctx.Profile.Name] = "Đã dừng trước khi cập nhật";
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                            SetRowColorIfAttached(row, Color.DarkOrange);
                            _log.Info($"[MANUAL_IDENTITY_BATCH_STOPPED] profile={ctx.Profile.Name} stage=before_update");
                            continue;
                        }

                        // Chụp trạng thái DONE trước khi chạy để phần đối soát cuối không
                        // nhầm một DONE cũ thành thành công của lượt cập nhật hiện tại.
                        var before = await ReadManualIdentityDoneStateAsync(ctx);
                        identityDoneBeforeKnown = before.Ok;
                        identityDoneBefore = before.Done;

                        var reply = await UpdateTikTokIdentityAsync(ctx,
                            updateName.Checked ? preview.DisplayName : "",
                            updateAvatar.Checked ? preview.AvatarPath : "",
                            updateBio.Checked ? preview.Bio : "");
                        if (!reply.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(reply.Error) ? reply.Message : reply.Error);

                        var resultText = reply.Message.Length > 0 ? reply.Message : "Đã cập nhật";
                        var identityCompleted = reply.AlreadyConfigured || (!reply.Skipped && !reply.NameCooldown);

                        if (!identityCompleted)
                        {
                            updateResults[ctx.Profile.Name] = resultText + " • Không ghi Excel=DONE";
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                            SetRowColorIfAttached(row, Color.DarkOrange);
                            skipped++;
                        }
                        else
                        {
                            if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(preview.AvatarPath))
                                state.LastAvatarByProfile[ctx.Profile.Name] = preview.AvatarPath;

                            var excel = await MarkManualIdentityDoneAsync(ctx);
                            if (excel.Ok)
                            {
                                updateResults[ctx.Profile.Name] = resultText + " • Excel=DONE";
                                SetRowColorIfAttached(row, Color.DarkGreen);
                                success++;

                                _autoIdentityHandledSession.Add(ctx.Profile.Name);
                                if (!string.IsNullOrWhiteSpace(excel.Username))
                                    _autoIdentityHandledSession.Add("account:" + excel.Username.ToLowerInvariant());
                                _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);
                            }
                            else
                            {
                                // TikTok đã đổi thành công nhưng không được giả vờ rằng Excel
                                // cũng đã xong. Hiển thị cảnh báo rõ để người dùng biết cần xử lý.
                                updateResults[ctx.Profile.Name] = resultText + " • Chưa ghi Excel: " + excel.Error;
                                SetRowColorIfAttached(row, Color.DarkOrange);
                                success++;
                                excelWarnings++;
                                _log.Warn($"[MANUAL_IDENTITY_EXCEL_DONE_FAILED] profile={ctx.Profile.Name} account={excel.Username} error={excel.Error}");
                            }
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                        }
                        SaveIdentityToolState(state);
                    }
                    catch (Exception ex)
                    {
                        // Không tăng failed ngay. Trước hết đối soát trạng thái cuối cùng.
                        // Chỉ chuyển lỗi -> thành công khi DONE vừa xuất hiện trong chính lượt
                        // này (trước đó chưa DONE), tránh lấy DONE cũ để che một lỗi thật.
                        var recoveredByFinalState = false;
                        string recoveredUsername = "";
                        if (identityDoneBeforeKnown && !identityDoneBefore)
                        {
                            var after = await ReadManualIdentityDoneStateAsync(ctx);
                            recoveredByFinalState = after.Ok && after.Done;
                            recoveredUsername = after.Username;
                        }

                        if (recoveredByFinalState)
                        {
                            success++;
                            updateResults[ctx.Profile.Name] = "Đã hoàn tất • Excel=DONE (đối soát sau lỗi tạm thời)";
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                            SetRowColorIfAttached(row, Color.DarkGreen);
                            _autoIdentityHandledSession.Add(ctx.Profile.Name);
                            if (!string.IsNullOrWhiteSpace(recoveredUsername))
                                _autoIdentityHandledSession.Add("account:" + recoveredUsername.ToLowerInvariant());
                            _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);
                            _log.Info($"[MANUAL_IDENTITY_RECONCILED_DONE] profile={ctx.Profile.Name} account={recoveredUsername} originalError={ex.Message}");
                        }
                        else
                        {
                            failed++;
                            updateResults[ctx.Profile.Name] = "Lỗi: " + ex.Message;
                            TrySetGridCellValue(row, resultColumn, updateResults[ctx.Profile.Name], "ShowTikTokIdentityDialog.Apply");
                            SetRowColorIfAttached(row, Color.Firebrick);
                            _log.Warn($"[TIKTOK_IDENTITY_UPDATE] profile={ctx.Profile.Name} result=failed message={ex.Message}");
                        }
                    }
                    finally
                    {
                        if (manualIdentitySlotAcquired)
                            _autoIdentityInFlight.Remove(ctx.Profile.Name);
                    }

                    if (batchStopRequested)
                    {
                        _log.Info($"[MANUAL_IDENTITY_BATCH_STOPPED] profile={ctx.Profile.Name} remaining={Math.Max(0, selectedRows.Count - startedIdentityProfiles)} mode=after_current_profile");
                        break;
                    }
                }

                if (form.IsDisposed) return;
                var failedDetails = updateResults
                    .Where(item => item.Value.StartsWith("Lỗi:", StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .Select(item => $"{item.Key}: {item.Value}")
                    .ToList();
                var remainingIdentityProfiles = Math.Max(0, selectedRows.Count - startedIdentityProfiles);
                var summary = batchStopRequested
                    ? $"Đã dừng theo yêu cầu.\nĐã bắt đầu: {startedIdentityProfiles}/{selectedRows.Count}\nCòn chưa chạy: {remainingIdentityProfiles}\nThành công: {success}\nBỏ qua: {skipped}\nLỗi: {failed}\nCảnh báo ghi Excel: {excelWarnings}"
                    : $"Hoàn tất.\nThành công: {success}\nBỏ qua: {skipped}\nLỗi: {failed}\nCảnh báo ghi Excel: {excelWarnings}";
                if (failedDetails.Count > 0) summary += "\n\n" + string.Join("\n", failedDetails);
                summary += batchStopRequested
                    ? "\n\nTên/ảnh dừng sau khi profile đang xử lý hoàn tất; tool không lấy thêm profile mới."
                    : "\n\nProfile lỗi không làm dừng các profile còn lại.";
                ModernDialog.ShowMessage(form, summary, "Tên & ảnh TikTok", failed == 0 && excelWarnings == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                updateInProgress = false;
                batchStopRequested = false;
                if (!form.IsDisposed)
                {
                    stopBatch.Text = "Dừng";
                    stopBatch.Enabled = false;
                    apply.Enabled = activeSection == 0;
                    randomize.Enabled = true;
                    identityTab.Enabled = true;
                    videoTab.Enabled = true;
                    close.Enabled = true;
                }
            }
        };

        form.FormClosing += (_, e) =>
        {
            if (!updateInProgress || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            ModernDialog.ShowMessage(form, "Manager đang cập nhật profile. Hãy chờ thao tác hiện tại hoàn tất rồi đóng cửa sổ.", "Tên, ảnh & video TikTok", MessageBoxIcon.Information);
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
            state.VideoFolder = videoFolder.Text.Trim();
            state.VideoCaptionText = videoCaption.Text;
            state.VideoSelectionMode = videoFixed.Checked ? "fixed" : videoSequence.Checked ? "sequence" : "random";
            state.VideoRandomCaption = randomCaption.Checked;
            state.AvoidLastVideo = avoidLastVideo.Checked;
            state.VideoOncePerRun = videoOnce.Checked;
            state.VideoDeleteEnabled = enableDelete.Checked;
            state.VideoUploadEnabled = enableUpload.Checked;
            state.VideoDeleteMode = deleteNewest.Checked ? "newest" : "all";
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
        TimeSpan? workerTimeout = null, bool nameGuardFastMode = false,
        bool skipPostSaveNameVerification = false)
    {
        if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name))
            throw new InvalidOperationException("Profile đang được mục Tin nhắn TikTok xử lý. Hãy dừng/đợi Tin nhắn hoàn tất rồi cập nhật tên/ảnh.");

        // 3C.6.4G - NAME / IMAGE RuntimeGate.
        //
        // UpdateTikTokIdentityAsync() là điểm chung của toàn bộ thao tác Tên/ảnh thật:
        // - Tên & ảnh thủ công;
        // - Name Guard trước Start;
        // - Auto Profile;
        // - Auto Replace / THAY ALL khi pipeline cần xử lý identity.
        //
        // Gate được đặt TRƯỚC OpenProfile/Stop automation để khi enforcement bật sau này,
        // policy block sẽ không làm thay đổi runtime của PRF rồi mới từ chối.
        var nameImageAllowed = RemotePolicyRuntimeGate.IsAllowed(
            "name_image",
            out var nameImageDecision);

        _log.Info(
            $"[REMOTE_POLICY_NAME_IMAGE_RUNTIME_CHECK] profile={ctx.Profile.Name} " +
            $"revision={nameImageDecision.Revision} mode={nameImageDecision.Mode} " +
            $"wouldBlock={nameImageDecision.WouldBlock} enforcement={nameImageDecision.EnforcementEnabled} " +
            $"allowed={nameImageAllowed} adminBypass={RemotePolicyRuntimeGate.AdminBypass}");

        // 3C.6 hiện EnforcementEnabled=false nên nhánh này chưa thể xảy ra.
        if (!nameImageAllowed)
        {
            _log.Warn(
                $"[REMOTE_POLICY_NAME_IMAGE_RUNTIME_BLOCKED] profile={ctx.Profile.Name} " +
                $"revision={nameImageDecision.Revision} mode={nameImageDecision.Mode}");

            return new IdentityUpdateReply
            {
                Ok = false,
                Skipped = true,
                Message = "QITool policy đang chặn cập nhật Tên/ảnh TikTok trên thiết bị này.",
                Error = "policy_blocked_name_image"
            };
        }

        await OpenProfileAsync(ctx);
        try { await RefreshStatusAsync(ctx); } catch { }
        if (ctx.LastSnapshot?.VideoDeleteRunning == true)
            throw new InvalidOperationException("Profile đang xóa video TikTok. Hãy chờ xóa video hoàn tất rồi cập nhật tên/ảnh.");
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

        // Luồng Tên/ảnh bình thường vẫn dùng readiness đầy đủ. Riêng Name Guard nhanh
        // vừa đi vào trang Hồ sơ và đọc được tên bằng CDP nên không chạy lại gate/F5 ở đây.
        if (!nameGuardFastMode)
        {
            var identityReady = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(6));
            if (!string.Equals(identityReady, "ready", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TikTok chưa đăng nhập trên profile này. Chrome đã được giữ ở trang chủ; hãy đăng nhập tài khoản rồi cập nhật tên/ảnh lại.");
        }

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
                VerifyExistingState = verifyExistingState,
                FastNameGuardMode = nameGuardFastMode,
                SkipPostSaveNameVerification = skipPostSaveNameVerification
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            var raw = await SendCommandAsync(ctx, "update_tiktok_identity|" + payload, workerTimeout ?? TimeSpan.FromSeconds(150));
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

    async Task<VideoDeleteReply> DeleteTikTokVideosAsync(
        ProfileContext ctx,
        string deleteMode,
        Action<VideoDeleteReply>? onProgress = null,
        bool resumeAutomation = true,
        CancellationToken cancellationToken = default)
    {
        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[VIDEO_DELETE_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=before_open action=NO_CHROME");
            return new VideoDeleteReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_RETIRED",
                Error = "Profile đã BAN/retired hoặc đang Tự đóng/Tự xóa."
            };
        }

        if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name))
            return new VideoDeleteReply { Ok = false, Completed = true, Error = "Profile đang được mục Tin nhắn TikTok xử lý. Hãy dừng/đợi Tin nhắn hoàn tất rồi xóa video." };

        var opened = await OpenProfileAsync(ctx);
        if (!opened && IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[VIDEO_DELETE_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=open_profile action=NO_CHROME");
            return new VideoDeleteReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_RETIRED",
                Error = "Profile đã BAN/retired; không mở lại để xóa video."
            };
        }

        try { await RefreshStatusAsync(ctx); } catch { }
        if (ctx.LastSnapshot?.VideoDeleteRunning == true)
            return new VideoDeleteReply { Ok = false, Completed = true, Error = "Profile này đang có một lượt xóa video chạy." };

        var previousRunState = GetLastConfirmedRuntimeState(ctx);
        var shouldResume = resumeAutomation
            && (previousRunState is "RUNNING" or "PAUSED");

        try
        {
            if (shouldResume)
            {
                try
                {
                    _log.Info($"[VIDEO_DELETE_STOP_AUTOMATION] profile={ctx.Profile.Name} previous={previousRunState}");
                    await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO_DELETE_STOP_AUTOMATION_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _log.Info($"[VIDEO_DELETE_USER_STOP] profile={ctx.Profile.Name} stage=before_start");
                return new VideoDeleteReply { Ok = false, Completed = true, Stage = "USER_STOPPED", Error = "Đã dừng theo yêu cầu." };
            }

            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                await OpenChromeForProfileAsync(ctx);

                if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                {
                    _log.Warn(
                        $"[VIDEO_DELETE_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=open_chrome action=NO_DELETE");
                    return new VideoDeleteReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "HARD_RETIRED",
                        Error = "Profile đã BAN/retired trong lúc mở Chrome; dừng VIDEO."
                    };
                }

                try { await RefreshStatusAsync(ctx); } catch { }
            }
            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
                return new VideoDeleteReply { Ok = false, Completed = true, Error = "Chrome chưa kết nối. Tool bỏ qua bước xóa và sẽ trả profile về luồng hiện tại." };

            var username = "";
            try
            {
                var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
                username = _tiktokAuthService.Load(dataRoot).Username;
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_DELETE_USERNAME_READ] profile={ctx.Profile.Name} error={ex.Message}");
            }

            var request = JsonSerializer.Serialize(new
            {
                Username = username,
                DeleteMode = deleteMode
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            _log.Info($"[VIDEO_DELETE_MANAGER_START] profile={ctx.Profile.Name} mode={deleteMode} username={username}");

            // Worker nhận START và chạy nền. Timeout START rộng hơn để VPS chậm vẫn có thời gian dừng LIVE,
            // kiểm tra login và chuẩn bị CDP mà không bị Manager kết luận lỗi quá sớm.
            var startReply = await SendCommandAsync(
                ctx,
                "delete_tiktok_videos|" + payload,
                TimeSpan.FromSeconds(35));

            if (!string.Equals(startReply, "started", StringComparison.OrdinalIgnoreCase))
            {
                var reason = startReply switch
                {
                    "emergency_stopped" => "Tool đang ở trạng thái Dừng khẩn cấp.",
                    "already_running" => "Profile này đang có một lượt xóa video chạy.",
                    "message_reply_running" => "Profile đang xử lý Tin nhắn TikTok.",
                    "automation_running" => "Automation LIVE của profile vẫn đang chạy nên Worker chưa cho phép xóa video.",
                    "chrome_not_connected" => "Chrome chưa kết nối.",
                    "not_logged_in" => "TikTok chưa đăng nhập.",
                    "invalid_payload" => "Dữ liệu lệnh xóa video không hợp lệ.",
                    _ => "Worker không bắt đầu được xóa video: " + startReply
                };
                return new VideoDeleteReply { Ok = false, Completed = true, Error = reason };
            }

            // Hai lớp watchdog:
            // - hard deadline 10 phút để không có lượt nào chạy vô hạn;
            // - inactivity 6 phút: chỉ kích hoạt khi status vẫn sống nhưng không có tiến triển.
            // Đây là khoảng chờ cố ý rộng cho VPS chậm.
            var hardDeadline = DateTime.UtcNow.AddMinutes(10);
            var lastProgressUtc = DateTime.UtcNow;
            VideoDeleteReply? latest = null;
            var consecutiveStatusFailures = 0;
            var lastStage = "";
            var lastMessage = "";
            var lastDeleted = -1;
            var lastRemaining = int.MinValue;

            while (DateTime.UtcNow < hardDeadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { await SendCommandAsync(ctx, "video_delete_stop", TimeSpan.FromSeconds(8)); } catch { }
                    _log.Info($"[VIDEO_DELETE_USER_STOP] profile={ctx.Profile.Name} stage={latest?.Stage} deleted={latest?.DeletedCount ?? 0} wait_cleanup=true");

                    // Worker dừng theo CancellationToken rồi chạy cleanup riêng trước khi hạ cờ busy.
                    // Chờ cleanup hoàn tất để finally bên Manager chỉ resume automation sau khi
                    // thao tác VIDEO đã thực sự nhường profile, tránh race start <-> STOPPING.
                    var stopDeadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < stopDeadline)
                    {
                        await Task.Delay(400);
                        try
                        {
                            var stopRaw = await SendCommandAsync(ctx, "video_delete_status", TimeSpan.FromSeconds(5));
                            var stopped = JsonSerializer.Deserialize<VideoDeleteReply>(
                                stopRaw,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (stopped is not null)
                            {
                                latest = stopped;
                                try { onProgress?.Invoke(stopped); } catch { }
                                if (stopped.Completed)
                                {
                                    _log.Info($"[VIDEO_DELETE_USER_STOP_CLEAN] profile={ctx.Profile.Name} stage={stopped.Stage} deleted={stopped.DeletedCount}");
                                    return stopped;
                                }
                            }
                        }
                        catch { }
                    }

                    _log.Warn($"[VIDEO_DELETE_USER_STOP_CLEAN_TIMEOUT] profile={ctx.Profile.Name} waited=30s");
                    return new VideoDeleteReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "USER_STOPPED",
                        InitialCount = latest?.InitialCount ?? 0,
                        DeletedCount = latest?.DeletedCount ?? 0,
                        RemainingCount = latest?.RemainingCount ?? -1,
                        Error = "Đã dừng theo yêu cầu; Worker chưa xác nhận cleanup xong trong 30 giây."
                    };
                }

                await Task.Delay(900);
                string raw;
                try
                {
                    raw = await SendCommandAsync(ctx, "video_delete_status", TimeSpan.FromSeconds(8));
                }
                catch (Exception ex)
                {
                    consecutiveStatusFailures++;
                    _log.Warn($"[VIDEO_DELETE_STATUS_RETRY] profile={ctx.Profile.Name} failure={consecutiveStatusFailures}/8 error={ex.Message}");
                    if (consecutiveStatusFailures >= 8)
                    {
                        try { await SendCommandAsync(ctx, "video_delete_stop", TimeSpan.FromSeconds(8)); } catch { }
                        return new VideoDeleteReply
                        {
                            Ok = false,
                            Completed = true,
                            Stage = "STATUS_LOST",
                            InitialCount = latest?.InitialCount ?? 0,
                            DeletedCount = latest?.DeletedCount ?? 0,
                            RemainingCount = latest?.RemainingCount ?? -1,
                            Error = "Mất liên lạc với Worker 8 lần liên tiếp. Tool đã bỏ qua bước xóa để tránh treo profile."
                        };
                    }
                    continue;
                }

                consecutiveStatusFailures = 0;
                latest = JsonSerializer.Deserialize<VideoDeleteReply>(
                    raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (latest is null) continue;

                var changed = latest.Stage != lastStage
                    || latest.Message != lastMessage
                    || latest.DeletedCount != lastDeleted
                    || latest.RemainingCount != lastRemaining;
                if (changed)
                {
                    lastProgressUtc = DateTime.UtcNow;
                    lastStage = latest.Stage;
                    lastMessage = latest.Message;
                    lastDeleted = latest.DeletedCount;
                    lastRemaining = latest.RemainingCount;
                }

                try { onProgress?.Invoke(latest); } catch { }
                if (latest.Completed)
                {
                    _log.Info($"[VIDEO_DELETE_MANAGER_RESULT] profile={ctx.Profile.Name} ok={latest.Ok} initial={latest.InitialCount} deleted={latest.DeletedCount} remaining={latest.RemainingCount} verifiedEmpty={latest.VerifiedEmpty} stage={latest.Stage} error={latest.Error}");
                    return latest;
                }

                if (DateTime.UtcNow - lastProgressUtc > TimeSpan.FromMinutes(6))
                {
                    try { await SendCommandAsync(ctx, "video_delete_stop", TimeSpan.FromSeconds(8)); } catch { }
                    return new VideoDeleteReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "INACTIVITY_TIMEOUT",
                        InitialCount = latest.InitialCount,
                        DeletedCount = latest.DeletedCount,
                        RemainingCount = latest.RemainingCount,
                        Error = "Không có tiến triển mới trong 6 phút. Tool đã dừng/bỏ qua bước xóa để profile không bị treo."
                    };
                }
            }

            try { await SendCommandAsync(ctx, "video_delete_stop", TimeSpan.FromSeconds(8)); } catch { }
            return new VideoDeleteReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_TIMEOUT",
                InitialCount = latest?.InitialCount ?? 0,
                DeletedCount = latest?.DeletedCount ?? 0,
                RemainingCount = latest?.RemainingCount ?? -1,
                Error = "Lượt xóa vượt quá giới hạn an toàn 10 phút. Tool đã gửi yêu cầu dừng và bỏ qua bước xóa."
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_DELETE_MANAGER_COMMAND_FAILED] profile={ctx.Profile.Name} error={ex.Message}");
            return new VideoDeleteReply { Ok = false, Completed = true, Error = ex.Message };
        }
        finally
        {
            // Fail-open: dù xóa thành công, lỗi hay timeout thì không được để automation cũ bị dừng vĩnh viễn.
            // Chỉ khôi phục nếu profile trước đó vốn đang RUNNING/PAUSED và người dùng chưa Dừng khẩn cấp.
            if (shouldResume && !IsAutomationHalted && !_closing)
            {
                try
                {
                    _log.Info($"[VIDEO_DELETE_RESUME_AUTOMATION] profile={ctx.Profile.Name} previous={previousRunState}");
                    await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(45));
                    if (previousRunState == "PAUSED")
                    {
                        await Task.Delay(500);
                        await SendCommandAsync(ctx, "pause", TimeSpan.FromSeconds(12));
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO_DELETE_RESUME_FAILED] profile={ctx.Profile.Name} previous={previousRunState} error={ex.Message}");
                }
            }
            else
            {
                _log.Info($"[VIDEO_DELETE_RESUME_SKIP] profile={ctx.Profile.Name} previous={previousRunState} halted={IsAutomationHalted} closing={_closing}");
            }
        }
    }

    async Task<VideoUploadReply> UploadTikTokVideoAsync(
        ProfileContext ctx,
        string videoPath,
        string caption,
        Action<VideoUploadReply>? onProgress = null,
        bool resumeAutomation = true,
        bool studioDeleteFallback = false,
        bool studioCleanupOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[VIDEO_UPLOAD_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=before_open action=NO_CHROME");
            return new VideoUploadReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_RETIRED",
                VideoPath = videoPath,
                Error = "Profile đã BAN/retired hoặc đang Tự đóng/Tự xóa."
            };
        }

        if (_messageReplyProfilesInFlight.Contains(ctx.Profile.Name))
            return new VideoUploadReply { Ok = false, Completed = true, Error = "Profile đang được mục Tin nhắn TikTok xử lý. Hãy dừng/đợi Tin nhắn hoàn tất rồi đăng video." };

        if (!studioCleanupOnly
            && (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)))
            return new VideoUploadReply { Ok = false, Completed = true, Error = "Không tìm thấy file video cần đăng." };

        var opened = await OpenProfileAsync(ctx);
        if (!opened && IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[VIDEO_UPLOAD_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=open_profile action=NO_CHROME");
            return new VideoUploadReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_RETIRED",
                VideoPath = videoPath,
                Error = "Profile đã BAN/retired; không mở lại để đăng video."
            };
        }

        try { await RefreshStatusAsync(ctx); } catch { }
        if (ctx.LastSnapshot?.VideoDeleteRunning == true)
            return new VideoUploadReply { Ok = false, Completed = true, Error = "Profile này đang có một thao tác VIDEO khác chạy." };

        var previousRunState = GetLastConfirmedRuntimeState(ctx);
        var shouldResume = resumeAutomation
            && (previousRunState is "RUNNING" or "PAUSED");

        try
        {
            if (shouldResume)
            {
                try
                {
                    _log.Info($"[VIDEO_UPLOAD_STOP_AUTOMATION] profile={ctx.Profile.Name} previous={previousRunState}");
                    await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO_UPLOAD_STOP_AUTOMATION_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _log.Info($"[VIDEO_UPLOAD_USER_STOP] profile={ctx.Profile.Name} stage=before_start");
                return new VideoUploadReply { Ok = false, Completed = true, Stage = "USER_STOPPED", VideoPath = videoPath, Error = "Đã dừng theo yêu cầu." };
            }

            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                await OpenChromeForProfileAsync(ctx);

                if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                {
                    _log.Warn(
                        $"[VIDEO_UPLOAD_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} stage=open_chrome action=NO_UPLOAD");
                    return new VideoUploadReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "HARD_RETIRED",
                        VideoPath = videoPath,
                        Error = "Profile đã BAN/retired trong lúc mở Chrome; dừng VIDEO."
                    };
                }

                try { await RefreshStatusAsync(ctx); } catch { }
            }
            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
                return new VideoUploadReply { Ok = false, Completed = true, Error = "Chrome chưa kết nối. Tool bỏ qua bước đăng và trả profile về luồng hiện tại." };

            var username = "";
            try
            {
                var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
                username = _tiktokAuthService.Load(dataRoot).Username;
            }
            catch (Exception ex)
            {
                _log.Warn($"[VIDEO_UPLOAD_USERNAME_READ] profile={ctx.Profile.Name} error={ex.Message}");
            }

            var request = JsonSerializer.Serialize(new
            {
                Username = username,
                VideoPath = videoPath,
                Caption = caption ?? "",
                StudioDeleteFallback = studioDeleteFallback,
                StudioCleanupOnly = studioCleanupOnly
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            if (studioCleanupOnly)
            {
                _log.Info(
                    $"[VIDEO_DELETE_FALLBACK_MANAGER_START] profile={ctx.Profile.Name} username={username} " +
                    "mode=cleanup_only keep=row1 delete=row2_plus");
            }
            else
            {
                _log.Info($"[VIDEO_UPLOAD_MANAGER_START] profile={ctx.Profile.Name} file={Path.GetFileName(videoPath)} username={username} studioDeleteFallback={studioDeleteFallback}");
            }

            var startReply = await SendCommandAsync(
                ctx,
                "upload_tiktok_video|" + payload,
                TimeSpan.FromSeconds(35));

            if (!string.Equals(startReply, "started", StringComparison.OrdinalIgnoreCase))
            {
                var reason = startReply switch
                {
                    "emergency_stopped" => "Tool đang ở trạng thái Dừng khẩn cấp.",
                    "already_running" => "Profile này đang có một lượt đăng video chạy.",
                    "video_delete_running" => "Profile đang xử lý xóa video.",
                    "message_reply_running" => "Profile đang xử lý Tin nhắn TikTok.",
                    "automation_running" => "Automation LIVE của profile vẫn đang chạy nên Worker chưa cho phép đăng video.",
                    "chrome_not_connected" => "Chrome chưa kết nối.",
                    "not_logged_in" => "TikTok chưa đăng nhập.",
                    "video_not_found" => "File video không tồn tại trên máy/VPS đang chạy Worker.",
                    "invalid_payload" => "Dữ liệu lệnh đăng video không hợp lệ.",
                    _ => "Worker không bắt đầu được đăng video: " + startReply
                };
                return new VideoUploadReply { Ok = false, Completed = true, Error = reason };
            }

            // Upload có thể chậm hơn xóa trên VPS/mạng yếu: hard-limit 15 phút,
            // inactivity 7 phút. Khi hết hạn chỉ dừng phần ĐĂNG, không giữ profile bị treo.
            var hardDeadline = DateTime.UtcNow.AddMinutes(15);
            var lastProgressUtc = DateTime.UtcNow;
            VideoUploadReply? latest = null;
            var consecutiveStatusFailures = 0;
            var lastStage = "";
            var lastMessage = "";
            var lastPostedHref = "";
            var lastPosted = false;
            var lastPrivacy = false;
            var lastVerified = false;
            var lastDeleteFallbackAttempted = false;
            var lastDeleteFallbackSucceeded = false;
            var lastDeleteFallbackDeletedCount = 0;

            while (DateTime.UtcNow < hardDeadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { await SendCommandAsync(ctx, "video_upload_stop", TimeSpan.FromSeconds(8)); } catch { }
                    _log.Info($"[VIDEO_UPLOAD_USER_STOP] profile={ctx.Profile.Name} stage={latest?.Stage} posted={latest?.Posted ?? false} wait_cleanup=true");

                    var stopDeadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < stopDeadline)
                    {
                        await Task.Delay(400);
                        try
                        {
                            var stopRaw = await SendCommandAsync(ctx, "video_upload_status", TimeSpan.FromSeconds(5));
                            var stopped = JsonSerializer.Deserialize<VideoUploadReply>(
                                stopRaw,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (stopped is not null)
                            {
                                latest = stopped;
                                try { onProgress?.Invoke(stopped); } catch { }
                                if (stopped.Completed)
                                {
                                    _log.Info($"[VIDEO_UPLOAD_USER_STOP_CLEAN] profile={ctx.Profile.Name} stage={stopped.Stage} posted={stopped.Posted}");
                                    return stopped;
                                }
                            }
                        }
                        catch { }
                    }

                    _log.Warn($"[VIDEO_UPLOAD_USER_STOP_CLEAN_TIMEOUT] profile={ctx.Profile.Name} waited=30s");
                    return new VideoUploadReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "USER_STOPPED",
                        Posted = latest?.Posted ?? false,
                        PrivacyUpdated = latest?.PrivacyUpdated ?? false,
                        ProfileVerified = latest?.ProfileVerified ?? false,
                        DeleteFallbackAttempted = latest?.DeleteFallbackAttempted ?? false,
                        DeleteFallbackSucceeded = latest?.DeleteFallbackSucceeded ?? false,
                        DeleteFallbackDeletedCount = latest?.DeleteFallbackDeletedCount ?? 0,
                        DeleteFallbackRemainingCount = latest?.DeleteFallbackRemainingCount ?? -1,
                        DeleteFallbackError = latest?.DeleteFallbackError ?? "",
                        VideoPath = videoPath,
                        PostedHref = latest?.PostedHref ?? "",
                        Error = "Đã dừng theo yêu cầu; Worker chưa xác nhận cleanup xong trong 30 giây."
                    };
                }

                await Task.Delay(950);
                string raw;
                try
                {
                    raw = await SendCommandAsync(ctx, "video_upload_status", TimeSpan.FromSeconds(8));
                }
                catch (Exception ex)
                {
                    consecutiveStatusFailures++;
                    _log.Warn($"[VIDEO_UPLOAD_STATUS_RETRY] profile={ctx.Profile.Name} failure={consecutiveStatusFailures}/8 error={ex.Message}");
                    if (consecutiveStatusFailures >= 8)
                    {
                        try { await SendCommandAsync(ctx, "video_upload_stop", TimeSpan.FromSeconds(8)); } catch { }
                        return new VideoUploadReply
                        {
                            Ok = false,
                            Completed = true,
                            Stage = "STATUS_LOST",
                            Posted = latest?.Posted ?? false,
                            PrivacyUpdated = latest?.PrivacyUpdated ?? false,
                            ProfileVerified = latest?.ProfileVerified ?? false,
                            DeleteFallbackAttempted = latest?.DeleteFallbackAttempted ?? false,
                            DeleteFallbackSucceeded = latest?.DeleteFallbackSucceeded ?? false,
                            DeleteFallbackDeletedCount = latest?.DeleteFallbackDeletedCount ?? 0,
                            DeleteFallbackRemainingCount = latest?.DeleteFallbackRemainingCount ?? -1,
                            DeleteFallbackError = latest?.DeleteFallbackError ?? "",
                            VideoPath = videoPath,
                            PostedHref = latest?.PostedHref ?? "",
                            Error = "Mất liên lạc với Worker 8 lần liên tiếp. Tool đã bỏ qua bước đăng để tránh treo profile."
                        };
                    }
                    continue;
                }

                consecutiveStatusFailures = 0;
                latest = JsonSerializer.Deserialize<VideoUploadReply>(
                    raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (latest is null) continue;

                var changed = latest.Stage != lastStage
                    || latest.Message != lastMessage
                    || latest.PostedHref != lastPostedHref
                    || latest.Posted != lastPosted
                    || latest.PrivacyUpdated != lastPrivacy
                    || latest.ProfileVerified != lastVerified
                    || latest.DeleteFallbackAttempted != lastDeleteFallbackAttempted
                    || latest.DeleteFallbackSucceeded != lastDeleteFallbackSucceeded
                    || latest.DeleteFallbackDeletedCount != lastDeleteFallbackDeletedCount;
                if (changed)
                {
                    lastProgressUtc = DateTime.UtcNow;
                    lastStage = latest.Stage;
                    lastMessage = latest.Message;
                    lastPostedHref = latest.PostedHref;
                    lastPosted = latest.Posted;
                    lastPrivacy = latest.PrivacyUpdated;
                    lastVerified = latest.ProfileVerified;
                    lastDeleteFallbackAttempted = latest.DeleteFallbackAttempted;
                    lastDeleteFallbackSucceeded = latest.DeleteFallbackSucceeded;
                    lastDeleteFallbackDeletedCount = latest.DeleteFallbackDeletedCount;
                }

                try { onProgress?.Invoke(latest); } catch { }
                if (latest.Completed)
                {
                    if (studioCleanupOnly)
                    {
                        _log.Info(
                            $"[VIDEO_DELETE_FALLBACK_MANAGER_RESULT] profile={ctx.Profile.Name} ok={latest.Ok} " +
                            $"attempted={latest.DeleteFallbackAttempted} fallbackOk={latest.DeleteFallbackSucceeded} " +
                            $"deleted={latest.DeleteFallbackDeletedCount} remaining={latest.DeleteFallbackRemainingCount} " +
                            $"stage={latest.Stage} error={latest.DeleteFallbackError} commandError={latest.Error}");
                    }
                    else
                    {
                        _log.Info($"[VIDEO_UPLOAD_MANAGER_RESULT] profile={ctx.Profile.Name} ok={latest.Ok} posted={latest.Posted} stage={latest.Stage} completionSignal={(latest.Posted ? "DA_DANG_VIDEO" : "NOT_CONFIRMED")} privacy=NOT_CHECKED profile=NOT_CHECKED error={latest.Error}");
                    }
                    return latest;
                }

                if (DateTime.UtcNow - lastProgressUtc > TimeSpan.FromMinutes(7))
                {
                    try { await SendCommandAsync(ctx, "video_upload_stop", TimeSpan.FromSeconds(8)); } catch { }
                    return new VideoUploadReply
                    {
                        Ok = false,
                        Completed = true,
                        Stage = "INACTIVITY_TIMEOUT",
                        Posted = latest.Posted,
                        PrivacyUpdated = latest.PrivacyUpdated,
                        ProfileVerified = latest.ProfileVerified,
                        DeleteFallbackAttempted = latest.DeleteFallbackAttempted,
                        DeleteFallbackSucceeded = latest.DeleteFallbackSucceeded,
                        DeleteFallbackDeletedCount = latest.DeleteFallbackDeletedCount,
                        DeleteFallbackRemainingCount = latest.DeleteFallbackRemainingCount,
                        DeleteFallbackError = latest.DeleteFallbackError,
                        VideoPath = videoPath,
                        PostedHref = latest.PostedHref,
                        Error = "Không có tiến triển mới trong 7 phút. Tool đã dừng/bỏ qua bước đăng để profile không bị treo."
                    };
                }
            }

            try { await SendCommandAsync(ctx, "video_upload_stop", TimeSpan.FromSeconds(8)); } catch { }
            return new VideoUploadReply
            {
                Ok = false,
                Completed = true,
                Stage = "HARD_TIMEOUT",
                Posted = latest?.Posted ?? false,
                PrivacyUpdated = latest?.PrivacyUpdated ?? false,
                ProfileVerified = latest?.ProfileVerified ?? false,
                DeleteFallbackAttempted = latest?.DeleteFallbackAttempted ?? false,
                DeleteFallbackSucceeded = latest?.DeleteFallbackSucceeded ?? false,
                DeleteFallbackDeletedCount = latest?.DeleteFallbackDeletedCount ?? 0,
                DeleteFallbackRemainingCount = latest?.DeleteFallbackRemainingCount ?? -1,
                DeleteFallbackError = latest?.DeleteFallbackError ?? "",
                VideoPath = videoPath,
                PostedHref = latest?.PostedHref ?? "",
                Error = "Lượt đăng vượt quá giới hạn an toàn 15 phút. Tool đã gửi yêu cầu dừng và bỏ qua bước đăng."
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"[VIDEO_UPLOAD_MANAGER_COMMAND_FAILED] profile={ctx.Profile.Name} error={ex.Message}");
            return new VideoUploadReply { Ok = false, Completed = true, VideoPath = videoPath, Error = ex.Message };
        }
        finally
        {
            if (shouldResume && !IsAutomationHalted && !_closing)
            {
                try
                {
                    _log.Info($"[VIDEO_UPLOAD_RESUME_AUTOMATION] profile={ctx.Profile.Name} previous={previousRunState}");
                    await SendCommandAsync(ctx, "start", TimeSpan.FromSeconds(45));
                    if (previousRunState == "PAUSED")
                    {
                        await Task.Delay(500);
                        await SendCommandAsync(ctx, "pause", TimeSpan.FromSeconds(12));
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"[VIDEO_UPLOAD_RESUME_FAILED] profile={ctx.Profile.Name} previous={previousRunState} error={ex.Message}");
                }
            }
            else
            {
                _log.Info($"[VIDEO_UPLOAD_RESUME_SKIP] profile={ctx.Profile.Name} previous={previousRunState} halted={IsAutomationHalted} closing={_closing}");
            }
        }
    }

    static string NormalizeConfiguredVideoSide(string? value, bool enabled)
    {
        if (!enabled)
            return "OFF";

        value = (value ?? "").Trim();
        return value.Equals("DONE", StringComparison.OrdinalIgnoreCase)
            ? "DONE"
            : "FAIL";
    }

    static (string Delete, string Upload) ParseConfiguredVideoStatus(
        string? value,
        bool deleteEnabled,
        bool uploadEnabled)
    {
        var parts = (value ?? "").Split('|');
        var left = parts.Length > 0 ? parts[0] : "";
        var right = parts.Length > 1 ? parts[1] : "";

        return (
            NormalizeConfiguredVideoSide(left, deleteEnabled),
            NormalizeConfiguredVideoSide(right, uploadEnabled));
    }

    static string ComposeVideoStatus(string deleteStatus, string uploadStatus)
        => $"{deleteStatus}|{uploadStatus}";

    static bool IsVideoLoginRequired(VideoDeleteReply? reply)
    {
        if (reply is null) return false;
        if (string.Equals(reply.Stage, "SKIPPED_LOGIN", StringComparison.OrdinalIgnoreCase))
            return true;

        var text = ((reply.Error ?? "") + " " + (reply.Message ?? "")).Trim();
        if (text.Length == 0) return false;

        return text.Contains("chưa đăng nhập", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mất đăng nhập", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login_required", StringComparison.OrdinalIgnoreCase);
    }

    async Task<(bool Ok, string Status, string Error)> WriteVideoStatusVerifiedAsync(
        string username,
        string profileName,
        string deleteStatus,
        string uploadStatus,
        CancellationToken ct)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0)
            return (false, "", "Không xác định được tài khoản để ghi VIDEO.");

        var wanted = ComposeVideoStatus(deleteStatus, uploadStatus);
        Exception? lastError = null;

        // Dùng cùng pattern retry + đọc lại xác minh như Tên/ảnh để không báo DONE giả
        // khi Excel đang được lưu/khóa tạm thời.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await RunAccountPoolIoAsync(
                    () => _accountPoolService.MarkVideoResult(username, wanted),
                    ct);

                var actual = await RunAccountPoolIoAsync(
                    () => _accountPoolService.GetVideoResult(username),
                    ct);

                if (!string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Đã ghi nhưng đọc lại Excel thấy VIDEO='{actual}', cần '{wanted}'.");
                }

                _log.Info(
                    $"[AUTO_VIDEO_EXCEL_VERIFIED] profile={profileName} account={username} " +
                    $"status={wanted} attempt={attempt}/3");
                return (true, wanted, "");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn(
                    $"[AUTO_VIDEO_EXCEL_RETRY] profile={profileName} account={username} " +
                    $"status={wanted} attempt={attempt}/3 error={ex.Message}");

                if (attempt < 3)
                    await Task.Delay(450 * attempt, ct);
            }
        }

        return (false, wanted, lastError?.Message ?? "Không ghi được trạng thái VIDEO vào Excel.");
    }

    static List<string> ReadConfiguredVideoFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new List<string>();

        var allowed = new HashSet<string>(
            new[] { ".mp4", ".mov", ".webm", ".m4v", ".avi", ".mkv", ".mpeg", ".mpg", ".mpe", ".ogm" },
            StringComparer.OrdinalIgnoreCase);

        return Directory
            .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(x => allowed.Contains(Path.GetExtension(x)))
            .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static List<string> ReadConfiguredVideoCaptions(string? text)
        => (text ?? "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

    VideoUploadPreview ResolveAutoVideoPreview(
        ProfileContext ctx,
        string username,
        IdentityToolState state)
    {
        var videos = ReadConfiguredVideoFiles(state.VideoFolder);
        if (videos.Count == 0)
            return new VideoUploadPreview(ctx, "", "");

        var orderedProfiles = _contexts.Values
            .OrderBy(x => x.Profile.Name, NaturalProfileNameOrder)
            .Select(x => x.Profile.Name)
            .ToList();

        var profileIndex = orderedProfiles.FindIndex(x =>
            x.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase));
        if (profileIndex < 0)
            profileIndex = 0;

        string picked;
        if (string.Equals(state.VideoSelectionMode, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            // "Một video cố định": giữ ổn định giữa các lần chạy thay vì random lại
            // mỗi lần AutoOnReady được gọi.
            picked = videos[0];
        }
        else if (string.Equals(state.VideoSelectionMode, "sequence", StringComparison.OrdinalIgnoreCase))
        {
            picked = videos[profileIndex % videos.Count];
        }
        else
        {
            picked = videos[Random.Shared.Next(videos.Count)];
        }

        var accountKey = string.IsNullOrWhiteSpace(username)
            ? "profile:" + ctx.Profile.Name.ToLowerInvariant()
            : username.Trim().TrimStart('@').ToLowerInvariant();

        if (state.AvoidLastVideo
            && videos.Count > 1
            && state.LastVideoByAccount.TryGetValue(accountKey, out var last)
            && !string.IsNullOrWhiteSpace(last))
        {
            try
            {
                var lastFull = Path.GetFullPath(last);
                var alternatives = videos
                    .Where(x =>
                    {
                        try
                        {
                            return !string.Equals(
                                Path.GetFullPath(x),
                                lastFull,
                                StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return true;
                        }
                    })
                    .ToList();

                if (alternatives.Count > 0)
                {
                    picked = string.Equals(state.VideoSelectionMode, "random", StringComparison.OrdinalIgnoreCase)
                        ? alternatives[Random.Shared.Next(alternatives.Count)]
                        : alternatives[0];
                }
            }
            catch { }
        }

        var captions = ReadConfiguredVideoCaptions(state.VideoCaptionText);
        string caption = "";
        if (captions.Count == 1)
        {
            caption = captions[0];
        }
        else if (captions.Count > 1)
        {
            caption = state.VideoRandomCaption
                ? captions[Random.Shared.Next(captions.Count)]
                : captions[profileIndex % captions.Count];
        }

        return new VideoUploadPreview(ctx, picked, caption);
    }

    async Task<bool> RunAutoVideoPipelineAsync(
        ProfileContext ctx,
        string username,
        IdentityToolState state)
    {
        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                "stage=pipeline_begin action=KEEP_EXCEL_UNCHANGED_NO_VIDEO");
            return false;
        }

        username = (username ?? "").Trim();
        if (username.Length == 0)
        {
            _log.Warn($"[AUTO_VIDEO_SKIP_ACCOUNT] profile={ctx.Profile.Name} reason=username_empty");
            return false;
        }

        // Nếu một lượt VIDEO khác (thủ công hoặc một gate khác) đã bắt đầu trước,
        // tuyệt đối không tự ghi FAIL|FAIL vào Excel chỉ vì Worker đang bận.
        // Defer cả pipeline cho chu kỳ READY hiện tại; lượt đang chạy giữ quyền xử lý.
        try
        {
            await RefreshStatusAsync(ctx);
        }
        catch { }

        if (ctx.LastSnapshot?.VideoDeleteRunning == true)
        {
            _log.Info(
                $"[AUTO_VIDEO_BUSY_DEFER] profile={ctx.Profile.Name} account={username} " +
                "action=KEEP_EXCEL_UNCHANGED_ALLOW_EXISTING_VIDEO_OP");
            return false;
        }

        string rawStatus;
        try
        {
            rawStatus = await RunAccountPoolIoAsync(
                () => _accountPoolService.GetVideoResult(username),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Không đọc được Excel thì không tự đoán là FAIL, vì có thể tài khoản
            // thực tế đã DONE|DONE. Bỏ qua VIDEO để tránh xóa/đăng lặp ngoài ý muốn.
            _log.Warn(
                $"[AUTO_VIDEO_EXCEL_READ_WARN] profile={ctx.Profile.Name} account={username} " +
                $"action=SKIP_VIDEO_ALLOW_AUTOMATION error={ex.Message}");
            return false;
        }

        var status = ParseConfiguredVideoStatus(
            rawStatus,
            state.VideoDeleteEnabled,
            state.VideoUploadEnabled);

        var deleteStatus = status.Delete;
        var uploadStatus = status.Upload;

        _log.Info(
            $"[AUTO_VIDEO_STATUS] profile={ctx.Profile.Name} account={username} raw='{rawStatus}' " +
            $"effective={ComposeVideoStatus(deleteStatus, uploadStatus)} " +
            $"deleteEnabled={state.VideoDeleteEnabled} uploadEnabled={state.VideoUploadEnabled}");

        // Đồng bộ OFF theo cấu hình hiện tại. Việc ghi lỗi không được chặn automation.
        var initialWrite = await WriteVideoStatusVerifiedAsync(
            username,
            ctx.Profile.Name,
            deleteStatus,
            uploadStatus,
            CancellationToken.None);
        if (!initialWrite.Ok)
        {
            _log.Warn(
                $"[AUTO_VIDEO_EXCEL_INITIAL_WARN] profile={ctx.Profile.Name} account={username} " +
                $"status={initialWrite.Status} error={initialWrite.Error}");
        }

        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                "stage=after_initial_excel action=KEEP_STATUS_NO_VIDEO");
            return false;
        }

        if ((!state.VideoDeleteEnabled || deleteStatus == "DONE")
            && (!state.VideoUploadEnabled || uploadStatus == "DONE"))
        {
            _log.Info(
                $"[AUTO_VIDEO_SKIP_COMPLETE] profile={ctx.Profile.Name} account={username} " +
                $"status={ComposeVideoStatus(deleteStatus, uploadStatus)}");
            return true;
        }

        var runDelete = state.VideoDeleteEnabled && deleteStatus != "DONE";
        var runUpload = state.VideoUploadEnabled && uploadStatus != "DONE";
        var skipUploadBecauseLoginRequired = false;
        var cleanupExistingPostedVideoOnly =
            runDelete
            && state.VideoUploadEnabled
            && uploadStatus == "DONE";

        // FAIL|DONE: vế ĐĂNG đã xác nhận DONE nên tuyệt đối KHÔNG upload lại và
        // cũng KHÔNG chạy XÓA chính (nhánh đó có thể xóa luôn video mới). Gọi thẳng
        // chính fallback Studio hiện có: giữ hàng 1, xóa hàng 2 trở xuống.
        // Nếu cleanup lỗi thì giữ nguyên FAIL|DONE để chu kỳ READY sau có thể thử lại.
        if (cleanupExistingPostedVideoOnly)
        {
            runDelete = false;
            runUpload = false;
            _log.Info(
                $"[AUTO_VIDEO_DELETE_FALLBACK_DIRECT_BEGIN] profile={ctx.Profile.Name} account={username} " +
                $"status={ComposeVideoStatus(deleteStatus, uploadStatus)} action=KEEP_ROW1_DELETE_ROW2_PLUS_NO_UPLOAD");

            try
            {
                var cleanupReply = await UploadTikTokVideoAsync(
                    ctx,
                    "",
                    "",
                    resumeAutomation: false,
                    studioDeleteFallback: false,
                    studioCleanupOnly: true);

                if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                {
                    _log.Warn(
                        $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                        "stage=cleanup_after_call action=KEEP_EXCEL_UNCHANGED_NO_VIDEO");
                    return false;
                }

                if (cleanupReply.DeleteFallbackSucceeded
                    && cleanupReply.DeleteFallbackRemainingCount == 1)
                {
                    deleteStatus = "DONE";
                    _log.Info(
                        $"[AUTO_VIDEO_DELETE_FALLBACK_DONE] profile={ctx.Profile.Name} account={username} " +
                        $"source=direct_cleanup deleted={cleanupReply.DeleteFallbackDeletedCount} " +
                        $"remaining={cleanupReply.DeleteFallbackRemainingCount} status=DONE uploadStatus=KEEP_DONE");
                }
                else
                {
                    deleteStatus = "FAIL";
                    _log.Warn(
                        $"[AUTO_VIDEO_DELETE_FALLBACK_FAIL] profile={ctx.Profile.Name} account={username} " +
                        $"source=direct_cleanup deleted={cleanupReply.DeleteFallbackDeletedCount} " +
                        $"remaining={cleanupReply.DeleteFallbackRemainingCount} " +
                        $"error={(string.IsNullOrWhiteSpace(cleanupReply.DeleteFallbackError) ? cleanupReply.Error : cleanupReply.DeleteFallbackError)} " +
                        "status=FAIL|DONE action=KEEP_STATUS_NO_UPLOAD");
                }
            }
            catch (Exception ex)
            {
                deleteStatus = "FAIL";
                _log.Warn(
                    $"[AUTO_VIDEO_DELETE_FALLBACK_EXCEPTION] profile={ctx.Profile.Name} account={username} " +
                    $"source=direct_cleanup error={ex.Message} status=FAIL|DONE action=KEEP_STATUS_NO_UPLOAD");
            }

            var cleanupWrite = await WriteVideoStatusVerifiedAsync(
                username,
                ctx.Profile.Name,
                deleteStatus,
                uploadStatus,
                CancellationToken.None);
            if (!cleanupWrite.Ok)
            {
                _log.Warn(
                    $"[AUTO_VIDEO_EXCEL_AFTER_FALLBACK_WARN] profile={ctx.Profile.Name} account={username} " +
                    $"status={cleanupWrite.Status} error={cleanupWrite.Error}");
            }
        }

        if (runDelete)
        {
            try
            {
                // Khi DELETE + UPLOAD cùng bật, DELETE luôn chạy TRƯỚC upload nên
                // phải xóa sạch toàn bộ bài cũ. Nếu chỉ bật DELETE thì vẫn tôn trọng
                // tùy chọn newest/all của người dùng.
                var deleteMode = state.VideoUploadEnabled
                    ? "all"
                    : string.Equals(
                        state.VideoDeleteMode,
                        "newest",
                        StringComparison.OrdinalIgnoreCase)
                        ? "newest"
                        : "all";

                _log.Info(
                    $"[AUTO_VIDEO_DELETE_BEGIN] profile={ctx.Profile.Name} account={username} mode={deleteMode} engine=STUDIO_DIRECT");

                var reply = await DeleteTikTokVideosAsync(
                    ctx,
                    deleteMode,
                    resumeAutomation: false);

                if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                {
                    _log.Warn(
                        $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                        $"stage=delete_after_call replyStage={reply.Stage} action=KEEP_EXCEL_UNCHANGED_SKIP_UPLOAD");
                    return false;
                }

                deleteStatus = reply.Ok ? "DONE" : "FAIL";
                if (IsVideoLoginRequired(reply))
                {
                    skipUploadBecauseLoginRequired = true;
                    _log.Warn(
                        $"[AUTO_VIDEO_LOGIN_REQUIRED] profile={ctx.Profile.Name} account={username} " +
                        $"stage={reply.Stage} action=SKIP_UPLOAD_RETURN_TO_EXISTING_LOGIN_FLOW error={reply.Error}");
                }
                else if (!reply.Ok && runUpload)
                {
                    _log.Warn(
                        $"[AUTO_VIDEO_DELETE_STUDIO_FAIL_UPLOAD_CONTINUE] profile={ctx.Profile.Name} account={username} " +
                        $"deleted={reply.DeletedCount} error={reply.Error} action=KEEP_DELETE_FAIL_CONTINUE_UPLOAD");
                }

                _log.Info(
                    $"[AUTO_VIDEO_DELETE_RESULT] profile={ctx.Profile.Name} account={username} " +
                    $"ok={reply.Ok} deleted={reply.DeletedCount} remaining={reply.RemainingCount} " +
                    $"verifiedEmpty={reply.VerifiedEmpty} status={deleteStatus} stage={reply.Stage} error={reply.Error}");
            }
            catch (Exception ex)
            {
                deleteStatus = "FAIL";
                _log.Warn(
                    $"[AUTO_VIDEO_DELETE_EXCEPTION] profile={ctx.Profile.Name} account={username} error={ex.Message} action=KEEP_DELETE_FAIL_CONTINUE_UPLOAD");
            }

            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                _log.Warn(
                    $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                    "stage=before_delete_excel action=KEEP_EXCEL_UNCHANGED_SKIP_UPLOAD");
                return false;
            }

            var write = await WriteVideoStatusVerifiedAsync(
                username,
                ctx.Profile.Name,
                deleteStatus,
                uploadStatus,
                CancellationToken.None);
            if (!write.Ok)
            {
                _log.Warn(
                    $"[AUTO_VIDEO_EXCEL_AFTER_DELETE_WARN] profile={ctx.Profile.Name} account={username} " +
                    $"status={write.Status} error={write.Error}");
            }
        }

        // Xóa lỗi thông thường vẫn phải thử ĐĂNG theo nguyên tắc fail-open đã chốt.
        // Riêng khi XÓA đã xác định LOGIN_REQUIRED thì không thử upload nữa: giữ
        // vế ĐĂNG ở trạng thái hiện tại (thường là FAIL), ghi checkpoint Excel và
        // trả Chrome ngay cho logic login đang có của tool.
        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                "stage=before_upload_branch action=KEEP_EXCEL_UNCHANGED_SKIP_UPLOAD");
            return false;
        }

        if (runUpload && skipUploadBecauseLoginRequired)
        {
            _log.Warn(
                $"[AUTO_VIDEO_UPLOAD_SKIP_LOGIN_REQUIRED] profile={ctx.Profile.Name} account={username} " +
                $"status={ComposeVideoStatus(deleteStatus, uploadStatus)}");
        }
        else if (runUpload)
        {
            var preview = ResolveAutoVideoPreview(ctx, username, state);

            if (string.IsNullOrWhiteSpace(preview.VideoPath)
                || !File.Exists(preview.VideoPath))
            {
                uploadStatus = "FAIL";
                _log.Warn(
                    $"[AUTO_VIDEO_UPLOAD_MISSING_FILE] profile={ctx.Profile.Name} account={username} " +
                    $"folder='{state.VideoFolder}'");
            }
            else
            {
                try
                {
                    _log.Info(
                        $"[AUTO_VIDEO_UPLOAD_BEGIN] profile={ctx.Profile.Name} account={username} " +
                        $"file={Path.GetFileName(preview.VideoPath)}");

                    var reply = await UploadTikTokVideoAsync(
                        ctx,
                        preview.VideoPath,
                        preview.Caption,
                        resumeAutomation: false,
                        studioDeleteFallback: false);

                    if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
                    {
                        _log.Warn(
                            $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                            $"stage=upload_after_call replyStage={reply.Stage} action=KEEP_EXCEL_UNCHANGED");
                        return false;
                    }

                    // Mốc DONE mới: TikTok đã hiện toast "Đã đăng video".
                    // Không còn phụ thuộc privacy hay verify bài trên profile.
                    var uploadOk = reply.Ok && reply.Posted;

                    uploadStatus = uploadOk ? "DONE" : "FAIL";

                    if (reply.Posted)
                    {
                        var accountKey = username.Trim().TrimStart('@').ToLowerInvariant();
                        state.LastVideoByAccount[accountKey] = preview.VideoPath;
                        SaveIdentityToolState(state);
                    }

                    _log.Info(
                        $"[AUTO_VIDEO_UPLOAD_RESULT] profile={ctx.Profile.Name} account={username} " +
                        $"ok={reply.Ok} posted={reply.Posted} status={uploadStatus} " +
                        $"completionSignal=DA_DANG_VIDEO privacy=NOT_CHECKED profile=NOT_CHECKED error={reply.Error}");
                }
                catch (Exception ex)
                {
                    uploadStatus = "FAIL";
                    _log.Warn(
                        $"[AUTO_VIDEO_UPLOAD_EXCEPTION] profile={ctx.Profile.Name} account={username} error={ex.Message}");
                }
            }

            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                _log.Warn(
                    $"[AUTO_VIDEO_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                    "stage=before_upload_excel action=KEEP_EXCEL_UNCHANGED");
                return false;
            }

            var write = await WriteVideoStatusVerifiedAsync(
                username,
                ctx.Profile.Name,
                deleteStatus,
                uploadStatus,
                CancellationToken.None);
            if (!write.Ok)
            {
                _log.Warn(
                    $"[AUTO_VIDEO_EXCEL_AFTER_UPLOAD_WARN] profile={ctx.Profile.Name} account={username} " +
                    $"status={write.Status} error={write.Error}");
            }
        }

        var finalStatus = ComposeVideoStatus(deleteStatus, uploadStatus);
        var completed =
            (!state.VideoDeleteEnabled || deleteStatus == "DONE")
            && (!state.VideoUploadEnabled || uploadStatus == "DONE");

        _log.Info(
            $"[AUTO_VIDEO_PIPELINE_DONE] profile={ctx.Profile.Name} account={username} " +
            $"status={finalStatus} completed={completed}");

        return completed;
    }

    async Task<bool> WaitForExistingVideoOperationAsync(
        ProfileContext ctx,
        string username,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var loggedWait = false;

        while (DateTime.UtcNow < deadline)
        {
            try { await RefreshStatusAsync(ctx); } catch { }

            if (ctx.LastSnapshot?.VideoDeleteRunning != true)
            {
                if (loggedWait)
                {
                    _log.Info(
                        $"[AUTO_VIDEO_EXISTING_OP_FINISHED] profile={ctx.Profile.Name} account={username}");
                }
                return true;
            }

            if (!loggedWait)
            {
                loggedWait = true;
                _log.Info(
                    $"[AUTO_VIDEO_EXISTING_OP_WAIT] profile={ctx.Profile.Name} account={username} " +
                    $"timeoutSec={timeout.TotalSeconds:0}");
            }

            await Task.Delay(750);
        }

        _log.Warn(
            $"[AUTO_VIDEO_EXISTING_OP_TIMEOUT] profile={ctx.Profile.Name} account={username} " +
            $"timeoutSec={timeout.TotalSeconds:0} action=FAILOPEN_ALLOW_NAME_GUARD_DECISION");
        return false;
    }

    async Task EnsureAutoVideoBeforeStartAsync(
        ProfileContext ctx,
        bool force = false,
        string trigger = "prestart")
    {
        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[AUTO_VIDEO_PRESTART_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} trigger={trigger} action=NO_VIDEO");
            return;
        }

        var state = LoadIdentityToolState();

        if (!state.VideoDeleteEnabled && !state.VideoUploadEnabled)
        {
            _log.Info(
                $"[AUTO_VIDEO_PRESTART_SKIP_DISABLED] profile={ctx.Profile.Name} trigger={trigger} " +
                $"force={force} delete={state.VideoDeleteEnabled} upload={state.VideoUploadEnabled}");
            return;
        }

        string username = "";
        try
        {
            var account = await ResolveNameGuardAccountAsync(ctx);
            username = (account.Username ?? "").Trim();
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_VIDEO_PRESTART_ACCOUNT_WARN] profile={ctx.Profile.Name} error={ex.Message}");
        }

        if (username.Length == 0)
            return;

        // Nếu đã có một lượt VIDEO đang chạy trên chính Worker này, chờ nó kết thúc
        // trước khi cho Start/LIVE đi tiếp. Timeout chỉ fail-open; VIDEO không bao giờ
        // được phép khóa tool vô hạn.
        var existingVideoFinished = await WaitForExistingVideoOperationAsync(
            ctx,
            username,
            TimeSpan.FromMinutes(10));
        if (!existingVideoFinished)
        {
            _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
            return;
        }

        if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
        {
            _log.Warn(
                $"[AUTO_VIDEO_PRESTART_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} trigger={trigger} stage=after_wait action=NO_VIDEO");
            return;
        }

        await _autoIdentityQueueGate.WaitAsync();
        try
        {
            // Re-load sau khi lấy gate để nhận đúng cấu hình mới nhất nếu người dùng
            // vừa thay đổi VIDEO trong lúc chờ. PRE-START luôn độc lập AutoOnReady;
            // mỗi lệnh Start phải đọc lại trạng thái Excel, không được skip theo cache READY.
            state = LoadIdentityToolState();
            if (!state.VideoDeleteEnabled && !state.VideoUploadEnabled)
            {
                _log.Info(
                    $"[AUTO_VIDEO_PRESTART_SKIP_DISABLED_AFTER_WAIT] profile={ctx.Profile.Name} account={username} " +
                    $"trigger={trigger} force={force}");
                _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
                return;
            }

            _log.Info(
                $"[AUTO_VIDEO_PRESTART_BEGIN] profile={ctx.Profile.Name} account={username} trigger={trigger} force={force}");

            // RunAutoVideoPipelineAsync luôn đọc lại cột VIDEO từ Excel. Kết quả DONE,
            // FAIL, lỗi đọc/ghi Excel, lỗi XÓA/ĐĂNG hay exception đều chỉ là best-effort;
            // caller tuyệt đối không dùng bool kết quả để chặn Start/LIVE.
            await RunAutoVideoPipelineAsync(ctx, username, state);

            // Marker này CHỈ để scheduler AutoOnReady không chen thêm một pipeline thứ hai
            // trong cùng READY-session. PRE-START phía trên không đọc marker để skip,
            // nên lần Start tiếp theo vẫn đọc Excel lại đúng yêu cầu.
            _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
        }
        catch (Exception ex)
        {
            // VIDEO là best-effort: lỗi ngoài dự kiến không được chặn Start/LIVE.
            // Đánh dấu đã thử trong chu kỳ READY hiện tại để không lặp lỗi liên tục.
            _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
            _log.Warn(
                $"[AUTO_VIDEO_PRESTART_FAILOPEN] profile={ctx.Profile.Name} account={username} error={ex.Message}");
        }
        finally
        {
            _autoIdentityQueueGate.Release();
        }
    }

    void InitializeIdentityAutoFlow()
    {
        _refreshTimer.Tick += (_, _) => ScheduleAutoIdentityForReadyProfiles();
    }

    void ScheduleAutoIdentityForReadyProfiles()
    {
        if (IsAutomationHalted || _closing) return;
        var state = LoadIdentityToolState();
        if (!state.AutoOnReady) return;
        if (string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath)) return;

        // Một chu kỳ READY chỉ chạy VIDEO một lượt. Khi Chrome rời READY / mất kết nối,
        // bỏ dấu phiên để lần đăng nhập READY kế tiếp (kể cả cùng tài khoản) có thể retry FAIL.
        foreach (var candidate in _contexts.Values)
        {
            var snapshot = candidate.LastSnapshot;
            if (snapshot is null
                || !string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(snapshot.TikTokStartupState, "READY", StringComparison.OrdinalIgnoreCase))
            {
                _autoVideoHandledReadyAccount.Remove(candidate.Profile.Name);
                _autoIdentityPendingResumeState.Remove(candidate.Profile.Name);
            }
        }

        // Chỉ cho phép MỘT PRF Tên/ảnh chạy tại một thời điểm. Timer tick sau sẽ lấy
        // profile kế tiếp sau khi lượt hiện tại kết thúc.
        if (_autoIdentityInFlight.Count > 0) return;

        var ctx = _contexts.Values
            .OrderBy(x => x.Profile.Name, NaturalProfileNameOrder)
            .FirstOrDefault(candidate =>
            {
                var snapshot = candidate.LastSnapshot;
                if (snapshot is null) return false;
                if (!string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase)) return false;
                if (snapshot.MessageReplyRunning || _messageReplyProfilesInFlight.Contains(candidate.Profile.Name)) return false;
                if (snapshot.VideoDeleteRunning) return false;
                if (!string.Equals(snapshot.TikTokStartupState, "READY", StringComparison.OrdinalIgnoreCase)) return false;
                if (_autoIdentityHandledSession.Contains(candidate.Profile.Name)) return false;

                // Nếu PRESTART (StartWithNameGuard) vừa xử lý VIDEO xong trong chính
                // chu kỳ READY hiện tại thì scheduler AutoOnReady tuyệt đối không được
                // mở một pipeline Tên/ảnh -> VIDEO thứ hai. Marker này được reset khi
                // Chrome rời READY / mất kết nối ở đầu hàm, nên phiên đăng nhập sau vẫn retry.
                if (_autoVideoHandledReadyAccount.ContainsKey(candidate.Profile.Name)) return false;

                if (_autoIdentityNextProbeUtc.TryGetValue(candidate.Profile.Name, out var nextProbeUtc)
                    && DateTime.UtcNow < nextProbeUtc)
                    return false;
                return true;
            });

        if (ctx is null) return;
        _autoIdentityInFlight.Add(ctx.Profile.Name);
        _ = RunAutoIdentityForProfileAsync(ctx);
    }

    async Task RunAutoIdentityForProfileAsync(ProfileContext ctx)
    {
        if (IsAutomationHalted) return;
        await _autoIdentityQueueGate.WaitAsync();
        try
        {
            var state = LoadIdentityToolState();
            if (!state.AutoOnReady) return;

            // Re-check SAU khi lấy queue gate. Có thể scheduler đã chọn profile này
            // trước khi PRESTART kịp hoàn tất VIDEO; khi tới lượt chạy thật thì VIDEO
            // đã xong và marker READY-session đã được đặt. Nếu không re-check ở đây,
            // pipeline AutoOnReady sẽ arm HOLD lần 2 và chạy VIDEO lặp ngay sau release.
            var account = await ResolveNameGuardAccountAsync(ctx);
            var username = (account.Username ?? "").Trim();
            if (username.Length == 0)
            {
                _autoIdentityHandledSession.Add(ctx.Profile.Name);
                _log.Warn($"[AUTO_IDENTITY_SKIP_ACCOUNT] profile={ctx.Profile.Name} không xác định được tài khoản.");
                return;
            }

            if (_autoVideoHandledReadyAccount.TryGetValue(ctx.Profile.Name, out var prestartHandledUsername)
                && prestartHandledUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
            {
                _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);
                _log.Info(
                    $"[AUTO_IDENTITY_PIPELINE_SKIP_PRESTART_ALREADY_HANDLED] profile={ctx.Profile.Name} " +
                    $"account={username} action=NO_SECOND_HOLD_NO_SECOND_VIDEO");
                return;
            }

            // Chụp trạng thái chạy TRƯỚC khi bật HOLD / Tên-ảnh / VIDEO. VIDEO có thể
            // STOP Worker. Nếu lượt trước kết thúc ở Name Guard TRANSIENT thì Worker
            // vẫn phải giữ STOPPED để KHÔNG chạy logic; nhưng lần retry này phải nhớ
            // RUNNING/PAUSED gốc để chỉ restore sau khi Tên/ảnh thật sự Allowed.
            var observedPreviousRunState = GetLastConfirmedRuntimeState(ctx);
            var previousRunState = observedPreviousRunState;
            if (_autoIdentityPendingResumeState.TryGetValue(ctx.Profile.Name, out var rememberedResume))
            {
                if (rememberedResume.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                    && (rememberedResume.State == RuntimeStateRunning || rememberedResume.State == RuntimeStatePaused))
                {
                    previousRunState = rememberedResume.State;
                    _log.Info(
                        $"[AUTO_IDENTITY_RESUME_STATE_REUSED] profile={ctx.Profile.Name} account={username} " +
                        $"observed={observedPreviousRunState} remembered={rememberedResume.State} gate=NAME_NOT_BYPASSED");
                }
                else
                {
                    // Tài khoản đã đổi trong cùng profile: trạng thái nhớ của tài khoản
                    // cũ tuyệt đối không được dùng để tự Start tài khoản mới.
                    _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);
                }
            }

            // Giữ Worker ở Home/READY trong toàn bộ chuỗi Tên/ảnh -> VIDEO.
            // Đây chỉ là gate điều phối, không thay đổi kết quả/logic Tên/ảnh.
            ArmManagedAccountSetupHold(ctx, "auto_identity_video_pipeline");

            var accountSessionKey = "account:" + username.ToLowerInvariant();
            var names = SplitIdentityNames(state.NamesText);

            // Tên/ảnh vẫn là điều kiện quyết định CUỐI CÙNG cho phép automation chạy.
            // Tuy nhiên người dùng đã chốt thứ tự: TÊN/ẢNH -> VIDEO -> rồi mới áp
            // kết quả Tên/ảnh theo logic cũ. Vì vậy cleanup terminal của Name Guard
            // được trì hoãn cho tới sau VIDEO, nhưng bản thân logic kiểm tra/đổi tên
            // không bị viết lại.
            NameGuardResult identityResult = new(true, "Tên/ảnh không tham gia pipeline.");

            if (state.UpdateName && names.Count > 0)
            {
                var alreadyDone = await RunAccountPoolIoAsync(
                    () => _accountPoolService.IsIdentityDone(username),
                    CancellationToken.None);

                if (alreadyDone)
                {
                    identityResult = new NameGuardResult(true, "Tên/ảnh đã DONE trong Excel.");
                    _log.Info($"[AUTO_IDENTITY_SKIP_DONE] profile={ctx.Profile.Name} account={username}");
                }
                else
                {
                    _log.Info(
                        $"[AUTO_IDENTITY_ONE_SHOT_BEGIN] profile={ctx.Profile.Name} account={username} " +
                        $"previousRunState={previousRunState}");

                    identityResult = await ProcessNameGuardOnceAsync(
                        ctx,
                        username,
                        state,
                        names,
                        deferTerminalCleanup: true);

                    if (identityResult.Allowed)
                    {
                        _log.Info(
                            $"[AUTO_IDENTITY_ONE_SHOT_DONE] profile={ctx.Profile.Name} account={username} " +
                            $"changed={identityResult.ChangedName}");
                    }
                    else if (identityResult.Transient)
                    {
                        _log.Warn(
                            $"[AUTO_IDENTITY_ONE_SHOT_TRANSIENT_CONTINUE_VIDEO] profile={ctx.Profile.Name} " +
                            $"account={username} deferred={identityResult.Deferred} reason={identityResult.Message}");
                    }
                    else
                    {
                        _log.Warn(
                            $"[AUTO_IDENTITY_ONE_SHOT_FAIL_CONTINUE_VIDEO] profile={ctx.Profile.Name} " +
                            $"account={username} reason={identityResult.Message}");
                    }
                }
            }
            else
            {
                // Không thay đổi logic Tên/ảnh khi người dùng tắt Cập nhật tên /
                // chưa cấu hình danh sách tên; chỉ coi phần này là không tham gia pipeline.
                _log.Info(
                    $"[AUTO_IDENTITY_SKIP_NAME_CONFIG] profile={ctx.Profile.Name} account={username} " +
                    $"updateName={state.UpdateName} names={names.Count}");
            }

            // BAN/retired là terminal exception duy nhất của policy "VIDEO vẫn chạy sau
            // Tên/ảnh FAIL/TRANSIENT". Khi đã hard-retired thì không được mở Chrome
            // hay gửi thêm login chỉ để chạy VIDEO.
            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                await CancelManagedAccountSetupHoldAsync(ctx, "auto_identity_hard_retired_before_video");
                _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);
                _log.Warn(
                    $"[AUTO_IDENTITY_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                    "stage=before_video action=NO_VIDEO_NO_START");
                return;
            }

            // VIDEO luôn được thử SAU Tên/ảnh, kể cả khi Tên/ảnh vừa FAIL/TRANSIENT.
            // Nếu đã có một lượt VIDEO khác đang chạy thì chờ nó tối đa 10 phút;
            // timeout chỉ fail-open và KHÔNG thay đổi kết quả Name Guard.
            try
            {
                var existingVideoFinished = await WaitForExistingVideoOperationAsync(
                    ctx,
                    username,
                    TimeSpan.FromMinutes(10));

                if (existingVideoFinished)
                {
                    await RunAutoVideoPipelineAsync(ctx, username, state);
                }
                else
                {
                    _log.Warn(
                        $"[AUTO_VIDEO_PIPELINE_TIMEOUT_FAILOPEN] profile={ctx.Profile.Name} account={username} " +
                        "reason=existing_video_operation_timeout");
                }

                _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
            }
            catch (Exception ex)
            {
                // VIDEO là bước phụ: mọi lỗi ngoài dự kiến chỉ ghi log; không được
                // làm thay đổi quyết định cuối cùng của Tên/ảnh.
                _autoVideoHandledReadyAccount[ctx.Profile.Name] = username;
                _log.Warn(
                    $"[AUTO_VIDEO_PIPELINE_FAILOPEN] profile={ctx.Profile.Name} " +
                    $"account={username} error={ex.Message}");
            }

            // Nếu BAN được phát hiện trong VIDEO thì dừng tại đây. Không nhả HOLD theo
            // kiểu resume/start và không áp fail-open của VIDEO lên luồng chính.
            if (IsProfileRetireDeleteBlockedForOpen(ctx.Profile.Name))
            {
                await CancelManagedAccountSetupHoldAsync(ctx, "auto_identity_hard_retired_after_video");
                _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);
                _log.Warn(
                    $"[AUTO_IDENTITY_ABORT_RETIRE_DELETE] profile={ctx.Profile.Name} account={username} " +
                    "stage=after_video action=NO_START");
                return;
            }

            // Tới đây VIDEO đã kết thúc/skip/fail-open. Bây giờ mới áp kết quả
            // Tên/ảnh theo đúng policy hiện có. Nếu cần đóng/queue profile do lỗi cứng
            // hoặc NAME_SYNC_PENDING, thực hiện cleanup cũ ở đây.
            if (!identityResult.Allowed)
            {
                try
                {
                    await FinalizeDeferredNameGuardOutcomeAsync(ctx, username, identityResult);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_IDENTITY_FINALIZE_AFTER_VIDEO_WARN] profile={ctx.Profile.Name} " +
                        $"account={username} error={ex.Message}");
                }

                // TÊN/ẢNH là gate bắt buộc của Start/LIVE. VIDEO có thể DONE hoặc FAIL
                // nhưng tuyệt đối không được fail-open qua Name Guard. TRANSIENT cũng
                // vẫn là CHƯA DONE: giữ gate/hold để scheduler retry Tên/ảnh ở lượt sau.
                // Marker VIDEO READY chỉ dùng để chặn pipeline lặp khi PRESTART đã xử lý
                // thành công. Với Name Guard TRANSIENT phải bỏ marker này, nếu không
                // scheduler sẽ tự chặn luôn lượt retry Tên/ảnh. VIDEO đã DONE thì Excel
                // sẽ khiến lần retry sau skip nhanh; VIDEO FAIL có thể retry nhưng vẫn
                // không quyết định quyền Start/LIVE.
                if (identityResult.Transient && !identityResult.Deferred)
                {
                    _autoVideoHandledReadyAccount.Remove(ctx.Profile.Name);
                    if (previousRunState == RuntimeStateRunning || previousRunState == RuntimeStatePaused)
                        _autoIdentityPendingResumeState[ctx.Profile.Name] = (username, previousRunState);
                    else
                        _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);

                    _log.Warn(
                        $"[AUTO_IDENTITY_TRANSIENT_RETRY_ARMED_AFTER_VIDEO] profile={ctx.Profile.Name} " +
                        $"account={username} previous={previousRunState} action=KEEP_HOLD_NO_START_ALLOW_NAME_RETRY");
                }
                else
                {
                    // Deferred/hard-fail không được mang trạng thái RUNNING cũ sang một
                    // phiên sau; cleanup hiện có quyết định số phận profile ở đây.
                    _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);
                }

                // NAME_SYNC_PENDING/Deferred và hard-fail tiếp tục dùng cleanup hiện có.
                _log.Warn(
                    $"[AUTO_IDENTITY_BLOCK_AUTOMATION_AFTER_VIDEO] profile={ctx.Profile.Name} " +
                    $"account={username} transient={identityResult.Transient} deferred={identityResult.Deferred} " +
                    $"previous={previousRunState} action=NO_START_KEEP_NAME_GATE reason={identityResult.Message}");
                return;
            }

            // Chỉ Tên/ảnh Allowed mới mở đường cho logic tool hiện tại. Kết quả VIDEO
            // không tham gia điều kiện này: DONE hay FAIL đều được phép đi tiếp.
            // Nhả hold SAU khi VIDEO đã kết thúc, rồi mới resume/start luồng chính.
            await ReleaseManagedAccountSetupHoldAsync(ctx, "auto_identity_allowed_after_video");

            // Đến đây Name Guard đã Allowed nên mới được phép dùng trạng thái gốc đã
            // nhớ từ lượt TRANSIENT trước để phục hồi luồng. Xóa cache ngay trong
            // phiên READY này để không thể rò sang tài khoản/phiên sau.
            _autoIdentityPendingResumeState.Remove(ctx.Profile.Name);

            _autoIdentityHandledSession.Add(ctx.Profile.Name);
            _autoIdentityHandledSession.Add(accountSessionKey);
            _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);

            await ResumeAutomationAfterIdentityDoneAsync(ctx, previousRunState);
        }
        catch (Exception ex)
        {
            var username = "";
            try
            {
                var account = await ResolveNameGuardAccountAsync(ctx);
                username = account.Username;
            }
            catch { }

            // Exception ngoài dự kiến ở AutoOnReady là lỗi kỹ thuật. Không được
            // dùng FailNameGuardAndCloseAsync vì như vậy một lỗi IPC/JSON sẽ biến
            // thành Tên/ảnh=FAIL và tự đóng Chrome + Worker.
            RegisterNameGuardTransientFailure(
                ctx,
                username,
                ex.Message,
                "auto_on_ready_exception",
                ex);
        }
        finally
        {
            _autoIdentityQueueGate.Release();
            _autoIdentityInFlight.Remove(ctx.Profile.Name);
        }
    }

}
