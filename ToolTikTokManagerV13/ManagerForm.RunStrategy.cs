using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    enum RunAllStrategyMode
    {
        Time = 0,
        PrimeFresh = 1
    }

    enum RunStrategyLane
    {
        Fresh,
        Medium,
        Old
    }

    enum PrePrimeRefreshSourceMode
    {
        CurrentFresh = 0,
        NeverRunOnly = 1
    }

    sealed class RunAllStrategySettings
    {
        public int Version { get; set; } = 7;
        public RunAllStrategyMode Mode { get; set; } = RunAllStrategyMode.Time;

        // V3: khi user đã chọn Giờ vàng + Bắt đầu, giữ "ý định vận hành" này
        // qua Stop All / Dừng khẩn cấp / restart Manager. Chỉ khi user chọn lại
        // chế độ Theo thời gian rồi Bắt đầu thì mới disarm Giờ vàng.
        public bool PrimeModeArmed { get; set; }

        // Mode vẫn được nhớ nhưng Stop All / Dừng khẩn cấp đặt scheduler vào trạng thái
        // chờ. Một START mới (target > 0) sẽ tự bỏ cờ này mà không cần chọn Giờ vàng lại.
        public bool PrimeModeSuspended { get; set; }

        // V7: công tắc "Duy trì số lượng PRF". Lần đầu sau khi cập nhật
        // mặc định TẮT; sau khi user đổi thì ON/OFF được persist ngay qua restart.
        // TẮT = target lấy theo số PRF đang mở; BẬT = dùng TargetSlots cố định.
        public bool AutoEnsureTarget { get; set; } = false;
        public int TargetSlots { get; set; } = 5;

        public int PrimeStartHour { get; set; } = 11;
        public int PrimeEndHour { get; set; } = 24;
        public int FreshTarget { get; set; } = 3;
        public int FreshHours { get; set; } = 3;
        public int OldHours { get; set; } = 6;
        public int RotationIntervalMinutes { get; set; } = 10;
        public int PrepareMinutes { get; set; } = 30;
        public bool PreserveFreshOffPeak { get; set; } = true;

        // V4: chế độ thay toàn bộ dàn đúng một lần trước giờ vàng. Khi bật,
        // Run Strategy không còn xoay theo tuổi ở các phase khác; sau khi thay
        // xong chỉ BAN/FAULT/manual mới làm thay slot cho tới kỳ PREPARE kế tiếp.
        public bool RefreshAllBeforePrime { get; set; }
        public PrePrimeRefreshSourceMode PrePrimeRefreshSource { get; set; } =
            PrePrimeRefreshSourceMode.CurrentFresh;

        // V5: cầu chì chống CREATE runaway. Mặc định BẬT nhưng user có thể
        // tắt hoặc chỉnh từng ngưỡng trong thẻ DÀN PRF. Chỉ đếm CREATE mới thật,
        // không đếm mở lại PRF chờ / NAME_SYNC_PENDING.
        public bool CreateLimitEnabled { get; set; } = true;
        public int CreateLimitPerSlot { get; set; } = 3;
        public int CreateLimitPerHour { get; set; } = 5;
        public int CreateLimitPerSession { get; set; } = 10;
        public int CreateLimitReuseRetryMinutes { get; set; } = 10;

        // V6: khung giờ cấm CREATE tự động. Hai setting này độc lập hoàn toàn
        // với “Chỉ dùng PRF chờ”: chỉ khi CẢ HAI cùng cho phép thì Tool mới
        // được lấy account chưa gán để tạo PRF mới. Dùng phút trong ngày để
        // hỗ trợ cả khung qua đêm, ví dụ 23:00 -> 07:00.
        public bool NoCreateScheduleEnabled { get; set; }
        public int NoCreateStartMinute { get; set; } = 23 * 60;
        public int NoCreateEndMinute { get; set; } = 7 * 60;
    }

    sealed record RunStrategyReusableCandidate(
        string ProfileName,
        double TotalRunSeconds,
        bool IsManual);

    sealed record RunStrategyRotationPlan(
        string Phase,
        ProfileContext Victim,
        IReadOnlyList<RunStrategyLane> PreferredLanes,
        bool AllowFreshEmergencyFallback,
        bool AllowCreateFallback)
    {
        public bool IsPrePrimeFullRefresh { get; init; }
        public PrePrimeRefreshSourceMode? ForcedPrePrimeSource { get; init; }
    }

    readonly object _runStrategyLock = new();
    bool _runStrategyFeatureInitialized;
    bool _runStrategySessionActive;
    bool _runStrategyTickBusy;
    bool _runStrategyRotationRunning;
    int _runStrategyTargetSlots;
    DateTime _runStrategyNextRotationUtc = DateTime.MinValue;
    string _runStrategyObservedPhase = "";
    CancellationTokenSource _runStrategyCts = new();
    CancellationTokenSource _runAllStartCts = new();
    RunAllStrategySettings _runStrategySettings = new();

    // State nhẹ của đợt làm mới trước giờ vàng. Chỉ giữ trong phiên Manager:
    // profile nào đã được chính đợt refresh mở vào thì được ACCEPTED; mọi profile
    // đã xuất hiện trước đó/được Tự bù chen vào khi refresh chưa xong đều phải thay.
    readonly HashSet<string> _runStrategyPrePrimePendingProfiles =
        new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _runStrategyPrePrimeAcceptedProfiles =
        new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _runStrategyPrePrimeDisallowedCandidates =
        new(StringComparer.OrdinalIgnoreCase);
    string _runStrategyPrePrimeCycleKey = "";
    bool _runStrategyPrePrimeCycleStarted;
    bool _runStrategyPrePrimeCycleCompleted;

    string RunStrategySettingsPath
        => Path.Combine(_baseDir, "manager_run_strategy.json");

    // Snapshot rất nhỏ chỉ để giữ đúng target của phiên Auto Run đang hoạt động
    // qua một lần Manager restart/update. Đây KHÔNG phải queue công việc và cũng
    // không tự arm Tự bù: Stop All/Dừng khẩn cấp sẽ xóa hiệu lực snapshot.
    sealed class RunStrategySessionTargetDocument
    {
        public int Version { get; set; } = 1;
        public bool Active { get; set; }
        public int TargetSlots { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    string RunStrategySessionTargetPath
        => Path.Combine(_baseDir, "manager_run_strategy_session_target.json");

    void PersistRunStrategySessionTarget(
        int targetSlots,
        bool active,
        string source)
    {
        try
        {
            targetSlots = Math.Clamp(targetSlots, 0, 50);
            active = active && targetSlots > 0;

            var document = new RunStrategySessionTargetDocument
            {
                Active = active,
                TargetSlots = active ? targetSlots : 0,
                UpdatedUtc = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true });

            var temp = RunStrategySessionTargetPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, RunStrategySessionTargetPath, overwrite: true);

            _log.Info(
                $"[RUN_SESSION_TARGET_SAVE] source={source} active={document.Active} target={document.TargetSlots}");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_SESSION_TARGET_SAVE_WARN] source={source} target={targetSlots} active={active} error={ex.Message}");
        }
    }

    void RestorePersistedRunStrategySessionTargetIfEligible(string source)
    {
        // Chỉ Giờ vàng đang ARMED và không bị Stop All/Dừng khẩn cấp mới có quyền
        // khôi phục target qua restart. Chế độ Theo thời gian vẫn bắt đầu phiên mới
        // bằng thao tác user như trước, tránh mang target cũ sang một phiên mới.
        if (_runStrategySettings.Mode != RunAllStrategyMode.PrimeFresh
            || !_runStrategySettings.PrimeModeArmed
            || _runStrategySettings.PrimeModeSuspended
            || !File.Exists(RunStrategySessionTargetPath))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<RunStrategySessionTargetDocument>(
                File.ReadAllText(RunStrategySessionTargetPath, Encoding.UTF8));

            if (document is null
                || document.Version != 1
                || document.TargetSlots < 0
                || document.TargetSlots > 50
                || (document.Active && document.TargetSlots <= 0))
            {
                return;
            }

            // Snapshot chỉ phục vụ restart/update gần đây; không hồi sinh quota rất cũ.
            if (document.UpdatedUtc == default
                || DateTime.UtcNow - document.UpdatedUtc > TimeSpan.FromHours(24))
            {
                _log.Info(
                    $"[RUN_SESSION_TARGET_RESTORE_SKIP] source={source} reason=stale target={document.TargetSlots} updated={document.UpdatedUtc:O}");
                return;
            }

            var restoredTarget = document.Active ? document.TargetSlots : 0;

            lock (_autoReplacementFixedSlotLock)
            {
                _autoReplacementTargetSlots = restoredTarget;
                _autoReplacementTargetInitialized = true;
                _autoReplacementNextCapacityReconcileUtc = DateTime.MinValue;
            }

            _log.Warn(
                $"[RUN_SESSION_TARGET_RESTORE] source={source} active={document.Active} target={restoredTarget} updated={document.UpdatedUtc:O} action=FIX_TARGET_BEFORE_RUNTIME_EVENTS");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_SESSION_TARGET_RESTORE_WARN] source={source} error={ex.Message}");
        }
    }

    void InitializeRunStrategyFeature()
    {
        if (_runStrategyFeatureInitialized)
            return;

        _runStrategyFeatureInitialized = true;

        // Prime Time dùng trực tiếp Tự bù + queue Chờ dùng lại hiện có. Bảo đảm
        // các subsystem này đã được load trước khi dialog đọc trạng thái.
        InitializeAutoCloseFeature();
        _runStrategySettings = LoadRunStrategySettings();
        RestorePersistedRunStrategySessionTargetIfEligible("run_strategy_init");

        // Theo dõi phase bằng một handler đồng bộ riêng. Nhờ vậy OFFPEAK/PREPARE/PRIME
        // đổi ngay theo đồng hồ, kể cả khi tick async trước đó còn đang chờ pipeline.
        // Handler này chỉ cập nhật state + reset cooldown, không đóng/mở profile.
        _refreshTimer.Tick += (_, _) => ObserveRunStrategyPhaseClock();

        // Tick 1 giây chỉ làm nhiệm vụ đánh giá nhẹ. Mỗi lần xoay thật đều có
        // busy gate + khoảng nghỉ riêng nên tuyệt đối không đóng hàng loạt.
        _refreshTimer.Tick += async (_, _) => await CheckRunStrategyAsync();
    }

    RunAllStrategySettings LoadRunStrategySettings()
    {
        try
        {
            if (!File.Exists(RunStrategySettingsPath))
                return NormalizeRunStrategySettings(new RunAllStrategySettings());

            var loaded = JsonSerializer.Deserialize<RunAllStrategySettings>(
                File.ReadAllText(RunStrategySettingsPath));

            // Migration V2 -> V3: Mode=PrimeFresh chỉ từng được ghi khi user đã
            // bấm Bắt đầu trong Auto Run, nên có thể an toàn coi đó là ý định đã arm.
            // Nhờ vậy cập nhật patch không bắt user phải chọn Giờ vàng lại một lần.
            if (loaded is not null
                && loaded.Version < 3
                && loaded.Mode == RunAllStrategyMode.PrimeFresh)
            {
                loaded.PrimeModeArmed = true;
                loaded.PrimeModeSuspended = false;
            }

            // V6 -> V7: công tắc mới phải TẮT ở lần đầu sau cập nhật, không
            // kế thừa giá trị true mặc định của giao diện cũ. Ngay khi user đổi
            // checkbox, file sẽ là Version=7 và giữ đúng lựa chọn qua restart.
            if (loaded is not null && loaded.Version < 7)
                loaded.AutoEnsureTarget = false;

            return NormalizeRunStrategySettings(
                loaded ?? new RunAllStrategySettings());
        }
        catch (Exception ex)
        {
            _log.Warn($"[RUN_STRATEGY_SETTINGS_READ] error={ex.Message}");
            return NormalizeRunStrategySettings(new RunAllStrategySettings());
        }
    }

    static RunAllStrategySettings NormalizeRunStrategySettings(
        RunAllStrategySettings settings)
    {
        settings.Version = 7;
        if (settings.Mode != RunAllStrategyMode.PrimeFresh)
        {
            settings.PrimeModeArmed = false;
            settings.PrimeModeSuspended = false;
        }

        settings.TargetSlots = Math.Clamp(settings.TargetSlots, 1, 50);
        settings.PrimeStartHour = Math.Clamp(settings.PrimeStartHour, 0, 23);
        settings.PrimeEndHour = Math.Clamp(settings.PrimeEndHour, 1, 24);
        if (settings.PrimeEndHour <= settings.PrimeStartHour)
            settings.PrimeEndHour = 24;

        settings.FreshTarget = Math.Clamp(settings.FreshTarget, 1, 50);
        settings.FreshHours = Math.Clamp(settings.FreshHours, 1, 12);
        settings.OldHours = Math.Clamp(settings.OldHours, settings.FreshHours + 1, 24);
        settings.RotationIntervalMinutes = Math.Clamp(settings.RotationIntervalMinutes, 5, 60);
        settings.PrepareMinutes = Math.Clamp(settings.PrepareMinutes, 0, 180);

        settings.CreateLimitPerSlot = Math.Clamp(settings.CreateLimitPerSlot, 1, 50);
        settings.CreateLimitPerHour = Math.Clamp(settings.CreateLimitPerHour, 1, 200);
        settings.CreateLimitPerSession = Math.Clamp(settings.CreateLimitPerSession, 1, 500);
        settings.CreateLimitReuseRetryMinutes = Math.Clamp(settings.CreateLimitReuseRetryMinutes, 1, 120);
        settings.NoCreateStartMinute = Math.Clamp(settings.NoCreateStartMinute, 0, (24 * 60) - 1);
        settings.NoCreateEndMinute = Math.Clamp(settings.NoCreateEndMinute, 0, (24 * 60) - 1);

        if (!Enum.IsDefined(typeof(PrePrimeRefreshSourceMode), settings.PrePrimeRefreshSource))
            settings.PrePrimeRefreshSource = PrePrimeRefreshSourceMode.CurrentFresh;

        // Full refresh cần một cửa sổ PREPARE thực sự. File config cũ không có
        // field này nên RefreshAllBeforePrime mặc định false, không đổi hành vi cũ.
        if (settings.RefreshAllBeforePrime && settings.PrepareMinutes <= 0)
            settings.PrepareMinutes = 30;

        return settings;
    }

    void SaveRunStrategySettings(RunAllStrategySettings settings)
    {
        settings = NormalizeRunStrategySettings(settings);
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });

        var temp = RunStrategySettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, RunStrategySettingsPath, overwrite: true);
        _runStrategySettings = settings;
    }

    async Task ShowRunAllStrategyDialogAndStartAsync()
    {
        if (!EnsureAutomationAllowedFromUi("chạy Auto Run"))
            return;

        InitializeRunStrategyFeature();

        var openCount = _contexts.Values.Count(ctx =>
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs);

        var current = NormalizeRunStrategySettings(new RunAllStrategySettings
        {
            Version = _runStrategySettings.Version,
            Mode = _runStrategySettings.Mode,
            PrimeModeArmed = _runStrategySettings.PrimeModeArmed,
            PrimeModeSuspended = _runStrategySettings.PrimeModeSuspended,
            AutoEnsureTarget = _runStrategySettings.AutoEnsureTarget,
            TargetSlots = _runStrategySettings.TargetSlots,
            PrimeStartHour = _runStrategySettings.PrimeStartHour,
            PrimeEndHour = _runStrategySettings.PrimeEndHour,
            FreshTarget = _runStrategySettings.FreshTarget,
            FreshHours = _runStrategySettings.FreshHours,
            OldHours = _runStrategySettings.OldHours,
            RotationIntervalMinutes = _runStrategySettings.RotationIntervalMinutes,
            PrepareMinutes = _runStrategySettings.PrepareMinutes,
            PreserveFreshOffPeak = _runStrategySettings.PreserveFreshOffPeak,
            RefreshAllBeforePrime = _runStrategySettings.RefreshAllBeforePrime,
            PrePrimeRefreshSource = _runStrategySettings.PrePrimeRefreshSource,
            CreateLimitEnabled = _runStrategySettings.CreateLimitEnabled,
            CreateLimitPerSlot = _runStrategySettings.CreateLimitPerSlot,
            CreateLimitPerHour = _runStrategySettings.CreateLimitPerHour,
            CreateLimitPerSession = _runStrategySettings.CreateLimitPerSession,
            CreateLimitReuseRetryMinutes = _runStrategySettings.CreateLimitReuseRetryMinutes,
            NoCreateScheduleEnabled = _runStrategySettings.NoCreateScheduleEnabled,
            NoCreateStartMinute = _runStrategySettings.NoCreateStartMinute,
            NoCreateEndMinute = _runStrategySettings.NoCreateEndMinute
        });

        using var form = new Form
        {
            Text = $"Auto Run — {AppVersionInfo.Display}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(980, 820),
            BackColor = UiTheme.Canvas,
            Font = new Font("Segoe UI", 9.5F),
            AutoScaleMode = AutoScaleMode.Dpi
        };
        ModernDialog.Apply(form);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18, 14, 18, 12),
            BackColor = ModernDialog.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var title = new Label
        {
            Text = "AUTO RUN",
            AutoSize = true,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Margin = new Padding(0, 0, 0, 4)
        };

        var summary = new Label
        {
            Text = current.AutoEnsureTarget
                ? $"PRF đang mở: {openCount}  ·  Cài đặt: duy trì {current.TargetSlots} PRF"
                : $"PRF đang mở: {openCount}  ·  Cài đặt: chạy theo số PRF đang mở",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 10)
        };

        // Màu chọn dùng nền xanh rất nhạt + chữ tối để dễ đọc hơn nền xanh đặc.
        var cardTextColor = Color.FromArgb(42, 57, 76);
        var selectedCardBackColor = Color.FromArgb(232, 242, 255);
        var selectedCardTextColor = Color.FromArgb(28, 67, 111);
        var cardBorderColor = Color.FromArgb(205, 214, 224);

        Button MainCard()
        {
            var card = new Button
            {
                AutoSize = false,
                Size = new Size(430, 64),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Card,
                ForeColor = cardTextColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(10, 6, 10, 6),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, 12, 0)
            };
            card.FlatAppearance.BorderColor = cardBorderColor;
            card.FlatAppearance.BorderSize = 1;
            card.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 245, 252);
            card.FlatAppearance.MouseDownBackColor = Color.FromArgb(225, 236, 248);
            return card;
        }

        var cardsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 8),
            Margin = Padding.Empty,
            BackColor = ModernDialog.Canvas
        };

        // DÀN PRF đã được chuyển sang nút “⚙ Cài đặt” trên màn hình chính.
        // Giữ launchCard/launchBox nội bộ để tái sử dụng validation/settings hiện có,
        // nhưng Auto Run chỉ hiển thị đúng hai chiến lược vận hành.
        var launchCard = MainCard();
        var timeCard = MainCard();
        var primeCard = MainCard();
        cardsRow.Controls.Add(timeCard);
        cardsRow.Controls.Add(primeCard);

        // Hai RadioButton này chỉ giữ trạng thái mode. Giao diện chọn mode chính là
        // các thẻ phía trên, giống cách thẻ PRF đổi trạng thái khi được chọn.
        var timeMode = new RadioButton
        {
            Checked = current.Mode == RunAllStrategyMode.Time,
            Visible = false
        };
        var primeMode = new RadioButton
        {
            Checked = current.Mode == RunAllStrategyMode.PrimeFresh,
            Visible = false
        };

        var detailsHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = ModernDialog.Canvas
        };

        // ------------------------------------------------------------
        // THẺ CHA 1: DÀN CHẠY
        // ------------------------------------------------------------
        var launchBox = new GroupBox
        {
            Text = "SỐ LƯỢNG & NGUỒN PRF",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            Margin = Padding.Empty,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var launchRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(4),
            Margin = Padding.Empty
        };
        launchRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var launchHint = new Label
        {
            Text = "Bật để duy trì một số lượng PRF cố định; tắt để chạy theo số PRF đang mở.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(0, 0, 0, 10)
        };

        var launchChoiceRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty
        };

        var autoEnsureTarget = new CheckBox
        {
            Text = "Duy trì số lượng PRF",
            Checked = current.AutoEnsureTarget,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.3F, FontStyle.Bold),
            ForeColor = cardTextColor,
            Margin = new Padding(4, 4, 16, 4),
            Cursor = Cursors.Hand
        };

        var autoEnsureTargetNote = new Label
        {
            Text = "Tắt: chạy theo số PRF đang mở lúc Bắt đầu. Bật: duy trì đúng số lượng bên dưới.",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(0, 6, 0, 4)
        };

        launchChoiceRow.Controls.Add(autoEnsureTarget);
        launchChoiceRow.Controls.Add(autoEnsureTargetNote);

        // Dùng chung đúng setting với hộp “Tự động” bên ngoài. Lưu ở Auto Run
        // phải phản ánh ngay sang Tự động và engine Tự bù của phiên đang chạy.
        var reuseOnlyNoCreate = new CheckBox
        {
            Text = "Chỉ dùng PRF chờ — không tạo PRF mới",
            Checked = _autoCloseSettings.ReuseOnlyNoCreateProfile,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.3F),
            ForeColor = cardTextColor,
            Margin = new Padding(4, 0, 0, 10),
            Cursor = Cursors.Hand
        };

        var targetGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        targetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        targetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        targetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var targetLabel = new Label
        {
            Text = "Số PRF muốn duy trì =",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F),
            Margin = new Padding(0, 8, 6, 4)
        };

        var targetSlots = new NumericUpDown
        {
            Value = Math.Clamp(current.TargetSlots, 1, 50),
            Minimum = 1,
            Maximum = 50,
            Width = 92,
            Height = 30,
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(2, 3, 2, 3)
        };

        var targetStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(5, 8, 0, 4)
        };
        targetGrid.Controls.Add(targetLabel, 0, 0);
        targetGrid.Controls.Add(targetSlots, 1, 0);
        targetGrid.Controls.Add(targetStatus, 2, 0);

        var createLimitBox = new GroupBox
        {
            Text = "GIỚI HẠN TẠO PRF",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 12, 0, 0),
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold)
        };

        var createLimitRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        createLimitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        var createLimitEnabled = new CheckBox
        {
            Text = "Bật giới hạn tạo PRF mới",
            Checked = current.CreateLimitEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
            Margin = new Padding(0, 4, 16, 6)
        };
        createLimitRoot.SetColumnSpan(createLimitEnabled, 8);
        createLimitRoot.Controls.Add(createLimitEnabled, 0, 0);

        NumericUpDown CreateLimitNum(int value, int min, int max)
            => new()
            {
                Value = Math.Clamp(value, min, max),
                Minimum = min,
                Maximum = max,
                Width = 62,
                Height = 28,
                Font = new Font("Segoe UI", 9.5F),
                Margin = new Padding(4, 2, 10, 2)
            };

        Label CreateLimitLabel(string text)
            => new()
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(0, 6, 0, 2)
            };

        var createLimitPerSlot = CreateLimitNum(current.CreateLimitPerSlot, 1, 50);
        var createLimitPerHour = CreateLimitNum(current.CreateLimitPerHour, 1, 200);
        var createLimitPerSession = CreateLimitNum(current.CreateLimitPerSession, 1, 500);
        var createLimitRetryMinutes = CreateLimitNum(current.CreateLimitReuseRetryMinutes, 1, 120);

        createLimitRoot.Controls.Add(CreateLimitLabel("Mỗi slot"), 0, 1);
        createLimitRoot.Controls.Add(createLimitPerSlot, 1, 1);
        createLimitRoot.Controls.Add(CreateLimitLabel("Trong 1 giờ"), 2, 1);
        createLimitRoot.Controls.Add(createLimitPerHour, 3, 1);
        createLimitRoot.Controls.Add(CreateLimitLabel("Trong 1 phiên"), 4, 1);
        createLimitRoot.Controls.Add(createLimitPerSession, 5, 1);
        createLimitRoot.Controls.Add(CreateLimitLabel("Retry PRF chờ (phút)"), 6, 1);
        createLimitRoot.Controls.Add(createLimitRetryMinutes, 7, 1);
        createLimitBox.Controls.Add(createLimitRoot);

        var noCreateScheduleBox = new GroupBox
        {
            Text = "KHUNG GIỜ KHÔNG TẠO PRF MỚI",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 12, 0, 0),
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold)
        };

        var noCreateScheduleRoot = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var noCreateScheduleEnabled = new CheckBox
        {
            Text = "Không tạo PRF mới trong khung giờ",
            Checked = current.NoCreateScheduleEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
            Margin = new Padding(0, 5, 16, 4)
        };

        DateTime MinuteOfDayToPickerValue(int minute)
        {
            minute = Math.Clamp(minute, 0, (24 * 60) - 1);
            return DateTime.Today.AddMinutes(minute);
        }

        DateTimePicker CreateNoCreateTimePicker(int minute)
            => new()
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm",
                ShowUpDown = true,
                Width = 78,
                Value = MinuteOfDayToPickerValue(minute),
                Font = new Font("Segoe UI", 9.5F),
                Margin = new Padding(4, 2, 8, 2)
            };

        var noCreateStart = CreateNoCreateTimePicker(current.NoCreateStartMinute);
        var noCreateEnd = CreateNoCreateTimePicker(current.NoCreateEndMinute);

        Label ScheduleLabel(string text)
            => new()
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(0, 6, 0, 2)
            };

        var noCreateScheduleNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.8F),
            Text = "Trong giờ cấm vẫn dùng PRF chờ; chỉ CREATE tự động bị chặn. Khung qua đêm như 23:00 → 07:00 được hỗ trợ.",
            Margin = new Padding(12, 6, 0, 2)
        };

        noCreateScheduleRoot.Controls.Add(noCreateScheduleEnabled);
        noCreateScheduleRoot.Controls.Add(ScheduleLabel("Từ"));
        noCreateScheduleRoot.Controls.Add(noCreateStart);
        noCreateScheduleRoot.Controls.Add(ScheduleLabel("đến"));
        noCreateScheduleRoot.Controls.Add(noCreateEnd);
        noCreateScheduleRoot.Controls.Add(noCreateScheduleNote);
        noCreateScheduleBox.Controls.Add(noCreateScheduleRoot);

        void UpdateNoCreateScheduleEnabled()
        {
            var enabled = noCreateScheduleEnabled.Checked;
            noCreateStart.Enabled = enabled;
            noCreateEnd.Enabled = enabled;
        }
        noCreateScheduleEnabled.CheckedChanged += (_, _) => UpdateNoCreateScheduleEnabled();
        UpdateNoCreateScheduleEnabled();

        void UpdateCreateLimitEnabled()
        {
            var enabled = createLimitEnabled.Checked;
            createLimitPerSlot.Enabled = enabled;
            createLimitPerHour.Enabled = enabled;
            createLimitPerSession.Enabled = enabled;
            createLimitRetryMinutes.Enabled = enabled;
        }
        createLimitEnabled.CheckedChanged += (_, _) => UpdateCreateLimitEnabled();
        UpdateCreateLimitEnabled();

        var launchFlowNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(860, 0),
            ForeColor = Color.FromArgb(37, 77, 122),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = "Tự bù luôn ưu tiên PRF có sẵn phù hợp trước; chỉ khi hết nguồn mới dùng Auto Profile để tạo mới (trừ khi bật CHỈ PRF CHỜ hoặc đang trong khung giờ cấm CREATE).",
            Margin = new Padding(0, 12, 0, 0)
        };

        launchRoot.Controls.Add(launchHint, 0, 0);
        launchRoot.Controls.Add(launchChoiceRow, 0, 1);
        launchRoot.Controls.Add(reuseOnlyNoCreate, 0, 2);
        launchRoot.Controls.Add(targetGrid, 0, 3);
        launchRoot.Controls.Add(createLimitBox, 0, 4);
        launchRoot.Controls.Add(noCreateScheduleBox, 0, 5);
        launchRoot.Controls.Add(launchFlowNote, 0, 6);
        launchBox.Controls.Add(launchRoot);

        // ------------------------------------------------------------
        // THẺ CHA 2: CHẠY THEO THỜI GIAN
        // ------------------------------------------------------------
        var timeBox = new GroupBox
        {
            Text = "Xoay theo runtime",
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 16),
            Margin = Padding.Empty,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var timeInfo = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(860, 0),
            ForeColor = Color.FromArgb(42, 57, 76),
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 8, 0, 12)
        };
        var timeNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(860, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9F),
            Text = "Thẻ này chỉ chọn chiến lược Auto Run. Số lượng PRF, nguồn PRF, TIME/BAN/treo/Tự bù và giới hạn CREATE lấy từ nút ‘⚙ Cài đặt’ trên màn hình chính.",
            Margin = new Padding(0, 8, 0, 0)
        };
        var timeLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6),
            Margin = Padding.Empty
        };
        timeLayout.Controls.Add(timeInfo);
        timeLayout.Controls.Add(timeNote);
        timeBox.Controls.Add(timeLayout);

        // ------------------------------------------------------------
        // THẺ CHA 3: GIỜ VÀNG
        // ------------------------------------------------------------
        var primeBox = new GroupBox
        {
            Text = "Chiến lược giờ vàng",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            Margin = Padding.Empty,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 13,
            Padding = new Padding(4),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

        NumericUpDown Num(decimal value, decimal min, decimal max)
            => new()
            {
                Value = Math.Clamp(value, min, max),
                Minimum = min,
                Maximum = max,
                Width = 92,
                Height = 30,
                Font = new Font("Segoe UI", 10F),
                Margin = new Padding(2, 3, 2, 3)
            };

        Label Field(string text)
            => new()
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                Margin = new Padding(0, 8, 6, 4)
            };

        Label Unit(string text)
            => new()
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(5, 8, 0, 4)
            };

        var startHour = Num(current.PrimeStartHour, 0, 23);
        var endHour = Num(current.PrimeEndHour, 1, 24);
        var freshTarget = Num(current.FreshTarget, 1, 50);
        var freshHours = Num(current.FreshHours, 1, 12);
        var oldHours = Num(current.OldHours, 2, 24);
        var rotationMinutes = Num(current.RotationIntervalMinutes, 5, 60);
        var prepareMinutes = Num(current.PrepareMinutes, 0, 180);
        // Helper thẻ lựa chọn dùng riêng cho 2 chiến lược con của Giờ vàng.
        // Phần DÀN PRF hiện dùng CheckBox "Duy trì số lượng PRF", nhưng Giờ vàng
        // vẫn cần RadioButton dạng thẻ để hai lựa chọn loại trừ nhau.
        RadioButton ChildChoice(string text, bool isChecked, int width)
        {
            var choice = new RadioButton
            {
                Appearance = Appearance.Button,
                AutoSize = false,
                Size = new Size(width, 72),
                Text = text,
                Checked = isChecked,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 6, 10, 6),
                Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Card,
                ForeColor = cardTextColor,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 12, 0),
                UseVisualStyleBackColor = false
            };
            choice.FlatAppearance.BorderColor = cardBorderColor;
            choice.FlatAppearance.BorderSize = 1;
            choice.FlatAppearance.CheckedBackColor = selectedCardBackColor;
            choice.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 245, 252);
            return choice;
        }

        // Hai chiến lược con của Giờ vàng được trình bày như 2 thẻ chọn loại trừ nhau,
        // giống phần DÀN PRF. Chọn THAY ACC thì dùng full-refresh trước giờ vàng;
        // chọn CHẠY MỚI > TRUNG BÌNH thì dùng chiến lược PrimeFresh hiện tại.
        var refreshAllBeforePrime = ChildChoice(
            "THAY ACC TRƯỚC GIỜ VÀNG",
            current.RefreshAllBeforePrime,
            430);
        var preserveFresh = ChildChoice(
            "CHẠY MỚI > TRUNG BÌNH\r\nTRONG GIỜ VÀNG",
            !current.RefreshAllBeforePrime,
            430);

        RadioButton SourceChoice(string text, bool isChecked)
            => new()
            {
                Text = text,
                Checked = isChecked,
                AutoSize = true,
                Appearance = Appearance.Normal,
                Font = new Font("Segoe UI", 9.3F, FontStyle.Bold),
                ForeColor = cardTextColor,
                Margin = new Padding(0, 2, 24, 2),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = true
            };

        var prePrimeCurrentFresh = SourceChoice(
            "NEW HIỆN TẠI",
            current.PrePrimeRefreshSource == PrePrimeRefreshSourceMode.CurrentFresh);
        var prePrimeNeverRun = SourceChoice(
            "MỚI HOÀN TOÀN",
            current.PrePrimeRefreshSource == PrePrimeRefreshSourceMode.NeverRunOnly);

        var prePrimeSourcePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(18, 4, 0, 0),
            Padding = Padding.Empty,
            BackColor = UiTheme.Card
        };
        prePrimeSourcePanel.Controls.Add(prePrimeCurrentFresh);
        prePrimeSourcePanel.Controls.Add(prePrimeNeverRun);

        // Hai thẻ chiến lược cùng một parent => RadioButton tự loại trừ nhau.
        // Hàng dưới chỉ chứa lựa chọn nguồn cho thẻ THAY ACC.
        var primeFeatureCards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 8),
            Padding = Padding.Empty,
            BackColor = UiTheme.Card
        };
        primeFeatureCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        primeFeatureCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        primeFeatureCards.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
        primeFeatureCards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        refreshAllBeforePrime.Margin = new Padding(0, 0, 7, 0);
        preserveFresh.Margin = new Padding(7, 0, 0, 0);
        primeFeatureCards.Controls.Add(refreshAllBeforePrime, 0, 0);
        primeFeatureCards.Controls.Add(preserveFresh, 1, 0);
        primeFeatureCards.Controls.Add(prePrimeSourcePanel, 0, 1);

        var ageLegend = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.FromArgb(37, 77, 122),
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 7)
        };

        void UpdateAgeLegend()
        {
            ageLegend.Text =
                $"Phân loại:  MỚI < {freshHours.Value:0}h   |   TRUNG BÌNH: {freshHours.Value:0}h ≤ runtime < {oldHours.Value:0}h   |   CŨ ≥ {oldHours.Value:0}h";
        }

        var row = 0;
        grid.Controls.Add(Field("Bắt đầu giờ vàng"), 0, row); grid.Controls.Add(startHour, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.Controls.Add(Field("Kết thúc giờ vàng"), 0, row); grid.Controls.Add(endHour, 1, row); grid.Controls.Add(Unit("giờ  (24 = 00:00)"), 2, row++);
        var freshTargetUnit = Unit("");
        grid.Controls.Add(Field("PRF MỚI ="), 0, row); grid.Controls.Add(freshTarget, 1, row); grid.Controls.Add(freshTargetUnit, 2, row++);
        grid.Controls.Add(Field("PRF MỚI: runtime <"), 0, row); grid.Controls.Add(freshHours, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.Controls.Add(Field("PRF CŨ: runtime ≥"), 0, row); grid.Controls.Add(oldHours, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.SetColumnSpan(ageLegend, 3);
        grid.Controls.Add(ageLegend, 0, row++);
        grid.Controls.Add(Field("Thay PRF tiếp theo sau ≥"), 0, row); grid.Controls.Add(rotationMinutes, 1, row); grid.Controls.Add(Unit("phút  ·  logic xoay giờ vàng cũ"), 2, row++);
        grid.Controls.Add(Field("Chuẩn bị trước giờ vàng"), 0, row); grid.Controls.Add(prepareMinutes, 1, row); grid.Controls.Add(Unit("phút  ·  mốc bắt đầu làm mới toàn bộ"), 2, row++);
        grid.SetColumnSpan(primeFeatureCards, 3);
        grid.Controls.Add(primeFeatureCards, 0, row++);

        var reuseNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.8F),
            Text = "",
            Margin = new Padding(0, 6, 0, 0)
        };
        grid.SetColumnSpan(reuseNote, 3);
        grid.Controls.Add(reuseNote, 0, row);
        primeBox.Controls.Add(grid);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            Margin = Padding.Empty
        };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Size = new Size(104, 40) };
        var start = new Button { Text = "Bắt đầu", DialogResult = DialogResult.OK, Size = new Size(122, 40) };
        var save = new Button { Text = "Lưu", Size = new Size(104, 40) };
        var launchWarning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(480, 42),
            ForeColor = Color.Firebrick,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(16, 9, 10, 0),
            Visible = false
        };
        ModernDialog.StyleSecondaryButton(cancel);
        ModernDialog.StylePrimaryButton(start);
        ModernDialog.StyleSecondaryButton(save);
        footer.Controls.Add(cancel);
        footer.Controls.Add(start);
        footer.Controls.Add(save);
        footer.Controls.Add(launchWarning);

        detailsHost.Controls.Add(launchBox);
        detailsHost.Controls.Add(timeBox);
        detailsHost.Controls.Add(primeBox);

        var activeSection = current.Mode == RunAllStrategyMode.PrimeFresh ? 2 : 1;

        void StyleChildChoice(RadioButton choice)
        {
            choice.BackColor = choice.Checked ? selectedCardBackColor : UiTheme.Card;
            choice.ForeColor = choice.Checked ? selectedCardTextColor : cardTextColor;
            choice.FlatAppearance.CheckedBackColor = selectedCardBackColor;
            choice.FlatAppearance.BorderColor = choice.Checked
                ? ActiveProfileColor
                : cardBorderColor;
            choice.FlatAppearance.BorderSize = choice.Checked ? 2 : 1;
        }

        void UpdateReuseNote()
        {
            if (refreshAllBeforePrime.Checked)
            {
                var source = prePrimeNeverRun.Checked
                    ? "chỉ PRF chưa hề chạy"
                    : $"PRF MỚI < {freshHours.Value:0}h theo logic hiện tại";

                reuseNote.Text =
                    $"THAY ACC: đến PREPARE Tool thay tuần tự 1 PRF/lần bằng {source}; đóng sạch Worker/Chrome cũ rồi mới mở PRF tiếp theo. " +
                    "Sau khi đủ dàn, không xoay theo tuổi/TIME nữa; BAN và watchdog lỗi kỹ thuật vẫn dùng Tự bù hiện tại." +
                    (_autoCloseSettings.ReuseOnlyNoCreateProfile
                        ? " Đang bật ‘CHỈ PRF CHỜ’ nên nếu hết nguồn phù hợp Tool sẽ chờ, không tạo PRF mới."
                        : " Nếu hết PRF chờ phù hợp, Tool dùng Auto Profile/Tự bù hiện có để tạo mới theo cooldown.");
            }
            else
            {
                reuseNote.Text = _autoCloseSettings.ReuseOnlyNoCreateProfile
                    ? "Giờ vàng: giữ đúng mục tiêu MỚI khi có đủ TB/CŨ; slot còn lại lấy runtime thấp trước. Ngoài giờ: lấy runtime cao trước. Đang bật ‘CHỈ PRF CHỜ’ nên Tool không tạo PRF mới. Luôn chỉ 1 PRF/lần."
                    : "Giờ vàng: đạt mục tiêu MỚI trước; slot còn lại ưu tiên TB/CŨ runtime thấp → chỉ dùng thêm MỚI khi thiếu TB/CŨ. Ngoài giờ vàng: ưu tiên runtime cao để tận dụng PRF cũ. Luôn chỉ 1 PRF/lần.";
            }
        }

        void UpdatePrimeEnabled()
        {
            foreach (Control control in grid.Controls)
                control.Enabled = primeMode.Checked;

            prePrimeSourcePanel.Enabled =
                primeMode.Checked && refreshAllBeforePrime.Checked;

            // Hai thẻ chiến lược luôn có thể chuyển qua lại khi đang ở mode Giờ vàng.
            refreshAllBeforePrime.Enabled = primeMode.Checked;
            preserveFresh.Enabled = primeMode.Checked;
            StyleChildChoice(refreshAllBeforePrime);
            StyleChildChoice(preserveFresh);

            // Khi chọn THAY ACC, các setting xoay theo tuổi chỉ còn ý nghĩa cho
            // chiến lược còn lại. Giữ giá trị để khi chuyển lại dùng ngay.
            freshTarget.Enabled = primeMode.Checked && !refreshAllBeforePrime.Checked;
            rotationMinutes.Enabled = primeMode.Checked && !refreshAllBeforePrime.Checked;

            UpdateReuseNote();
        }

        void UpdateMainCards()
        {
            if (save.Text != "Lưu")
                save.Text = "Lưu";

            launchCard.Text = "CÀI ĐẶT";
            timeCard.Text = "XOAY THEO RUNTIME";
            primeCard.Text = "CHIẾN LƯỢC GIỜ VÀNG";

            var cards = new[] { launchCard, timeCard, primeCard };
            for (var i = 0; i < cards.Length; i++)
            {
                var active = activeSection == i;
                var card = cards[i];
                card.BackColor = active ? selectedCardBackColor : UiTheme.Card;
                card.ForeColor = active ? selectedCardTextColor : cardTextColor;
                card.FlatAppearance.BorderColor = active
                    ? ActiveProfileColor
                    : cardBorderColor;
                card.FlatAppearance.BorderSize = active ? 2 : 1;
            }

            // Mode đang chọn vẫn được đánh dấu bằng viền xanh khi user đang xem thẻ khác.
            if (activeSection != 1 && timeMode.Checked)
            {
                timeCard.FlatAppearance.BorderColor = ActiveProfileColor;
                timeCard.FlatAppearance.BorderSize = 2;
            }
            if (activeSection != 2 && primeMode.Checked)
            {
                primeCard.FlatAppearance.BorderColor = ActiveProfileColor;
                primeCard.FlatAppearance.BorderSize = 2;
            }
        }

        void ShowSection(int section)
        {
            activeSection = Math.Clamp(section, 0, 2);
            launchBox.Visible = activeSection == 0;
            timeBox.Visible = activeSection == 1;
            primeBox.Visible = activeSection == 2;
            if (launchBox.Visible) launchBox.BringToFront();
            if (timeBox.Visible) timeBox.BringToFront();
            if (primeBox.Visible) primeBox.BringToFront();
            UpdateMainCards();
        }

        RunAllStrategySettings BuildSettingsFromDialog(bool startRequested)
        {
            // Nút Lưu chỉ lưu cấu hình, tuyệt đối không tự arm/bắt đầu một phiên
            // Giờ vàng mới. Nếu phiên Giờ vàng hiện tại đã được arm thì giữ nguyên
            // armed/suspended để chỉnh setting giữa lúc chạy có hiệu lực ngay.
            var preserveExistingPrimeArm =
                !startRequested
                && primeMode.Checked
                && _runStrategySettings.Mode == RunAllStrategyMode.PrimeFresh
                && _runStrategySettings.PrimeModeArmed;

            return NormalizeRunStrategySettings(new RunAllStrategySettings
            {
                Version = 7,
                Mode = primeMode.Checked ? RunAllStrategyMode.PrimeFresh : RunAllStrategyMode.Time,
                PrimeModeArmed = startRequested
                    ? primeMode.Checked
                    : preserveExistingPrimeArm,
                PrimeModeSuspended = startRequested
                    ? false
                    : preserveExistingPrimeArm && _runStrategySettings.PrimeModeSuspended,
                AutoEnsureTarget = autoEnsureTarget.Checked,
                TargetSlots = (int)targetSlots.Value,
                PrimeStartHour = (int)startHour.Value,
                PrimeEndHour = (int)endHour.Value,
                FreshTarget = (int)freshTarget.Value,
                FreshHours = (int)freshHours.Value,
                OldHours = (int)oldHours.Value,
                RotationIntervalMinutes = (int)rotationMinutes.Value,
                PrepareMinutes = (int)prepareMinutes.Value,
                PreserveFreshOffPeak = preserveFresh.Checked,
                RefreshAllBeforePrime = refreshAllBeforePrime.Checked,
                PrePrimeRefreshSource = prePrimeNeverRun.Checked
                    ? PrePrimeRefreshSourceMode.NeverRunOnly
                    : PrePrimeRefreshSourceMode.CurrentFresh,
                CreateLimitEnabled = createLimitEnabled.Checked,
                CreateLimitPerSlot = (int)createLimitPerSlot.Value,
                CreateLimitPerHour = (int)createLimitPerHour.Value,
                CreateLimitPerSession = (int)createLimitPerSession.Value,
                CreateLimitReuseRetryMinutes = (int)createLimitRetryMinutes.Value,
                NoCreateScheduleEnabled = noCreateScheduleEnabled.Checked,
                NoCreateStartMinute = (noCreateStart.Value.Hour * 60) + noCreateStart.Value.Minute,
                NoCreateEndMinute = (noCreateEnd.Value.Hour * 60) + noCreateEnd.Value.Minute
            });
        }

        bool ValidateSettingsForSave(RunAllStrategySettings settings, out string message)
        {
            if (settings.OldHours <= settings.FreshHours)
            {
                message = "Mốc PRF CŨ phải lớn hơn mốc PRF MỚI.";
                return false;
            }

            if (settings.Mode == RunAllStrategyMode.PrimeFresh
                && !settings.RefreshAllBeforePrime
                && settings.AutoEnsureTarget
                && settings.FreshTarget > settings.TargetSlots)
            {
                message = $"PRF MỚI mục tiêu ({settings.FreshTarget}) không được lớn hơn target ({settings.TargetSlots}).";
                return false;
            }

            if (settings.Mode == RunAllStrategyMode.PrimeFresh
                && settings.RefreshAllBeforePrime
                && settings.PrepareMinutes <= 0)
            {
                message = "THAY ACC TRƯỚC GIỜ VÀNG cần ‘Chuẩn bị trước giờ vàng’ lớn hơn 0 phút.";
                return false;
            }

            if (settings.NoCreateScheduleEnabled
                && settings.NoCreateStartMinute == settings.NoCreateEndMinute)
            {
                message = "Khung giờ không tạo PRF phải có giờ bắt đầu khác giờ kết thúc.";
                return false;
            }

            // Không chặn Lưu chỉ vì số PRF đang mở hiện tại chưa phù hợp target.
            // Những điều kiện phụ thuộc runtime (0 PRF mở, open > target, FreshTarget
            // > target động khi CHỈ PRF ĐANG MỞ...) chỉ được chặn khi Bắt đầu.
            message = "";
            return true;
        }

        void SyncSavedRunStrategyToOutsideAutomation(RunAllStrategySettings settings)
        {
            // Các mode tự duy trì/giờ vàng đều cần engine Tự bù bên ngoài. Đây chính
            // là cùng rule trước đây chỉ được áp dụng khi bấm Bắt đầu; nay Lưu cũng
            // đồng bộ ngay để nút “Tự động” bên ngoài phản ánh cấu hình đã chọn.
            var requiresReplacement =
                settings.AutoEnsureTarget
                || settings.Mode == RunAllStrategyMode.PrimeFresh;

            var changed = false;
            bool previousReuseOnly;
            bool currentReuseOnly;

            // Serialize toggle với đúng điểm BuildAutoProfileQueue ASSIGN account.
            // Không giữ lock qua Save/await nên UI không bị treo bởi một vòng login/create.
            lock (_autoReplacementCreateModeGate)
            {
                previousReuseOnly = _autoCloseSettings.ReuseOnlyNoCreateProfile;
                currentReuseOnly = reuseOnlyNoCreate.Checked;

                if (previousReuseOnly != currentReuseOnly)
                {
                    _autoCloseSettings.ReuseOnlyNoCreateProfile = currentReuseOnly;
                    changed = true;
                }
            }

            if (requiresReplacement && !_autoCloseSettings.OpenReplacementAfterAutoClose)
            {
                _autoCloseSettings.OpenReplacementAfterAutoClose = true;
                changed = true;
            }

            if (changed)
            {
                SaveAutoCloseSettings();
                _log.Info(
                    $"[RUN_STRATEGY_SAVE_SYNC_AUTO] replacement={_autoCloseSettings.OpenReplacementAfterAutoClose} " +
                    $"reuseOnly={_autoCloseSettings.ReuseOnlyNoCreateProfile} mode={settings.Mode} autoEnsure={settings.AutoEnsureTarget}");
            }
            else
            {
                UpdateAutoCloseToolbarButtonText();
            }

            // Bật/tắt CHỈ PRF CHỜ phải có hiệu lực hai chiều ngay lập tức:
            // bật => các hard-gate live chặn CREATE chưa commit;
            // tắt => wake request đang ngủ 5 phút vì reuse-only để CREATE fallback lại được phép.
            NotifyAutoReplacementReuseOnlySettingChanged(
                previousReuseOnly,
                currentReuseOnly,
                "run_strategy_save");

            // Giới hạn CREATE đọc trực tiếp Run Strategy settings. Nếu user vừa tắt
            // giới hạn hoặc nâng ngưỡng khiến request đang chờ không còn bị block,
            // đánh thức request ngay thay vì bắt chờ hết timer cũ.
            NotifyAutoReplacementCreateLimitSettingsChanged("run_strategy_save");

            // Khung giờ cấm CREATE cũng phải áp dụng live. Nếu user vừa tắt hoặc
            // đổi sang một khung không còn bao phủ hiện tại, đánh thức request
            // đang chờ ngay; nếu vẫn đang trong giờ cấm thì cập nhật deadline mới.
            NotifyAutoReplacementNoCreateScheduleSettingsChanged("run_strategy_save");
        }

        void UpdateLaunchValidation()
        {
            if (save.Text != "Lưu")
                save.Text = "Lưu";

            var ensure = autoEnsureTarget.Checked;
            targetLabel.Enabled = ensure;
            targetSlots.Enabled = ensure;

            var effectiveTarget = ensure
                ? (int)targetSlots.Value
                : openCount;

            freshTargetUnit.Text = refreshAllBeforePrime.Checked
                ? "không dùng khi chọn THAY ACC TRƯỚC GIỜ VÀNG"
                : $"PRF mục tiêu  (≤ target: {Math.Max(0, effectiveTarget)} · chỉ vượt khi thiếu TB/CŨ)";

            var valid = true;
            if (ensure)
            {
                if (openCount > effectiveTarget)
                {
                    targetStatus.Text =
                        $"Đang mở {openCount} > target {effectiveTarget} — hãy đóng bớt hoặc tăng target.";
                    targetStatus.ForeColor = Color.Firebrick;
                    valid = false;
                }
                else
                {
                    var missing = Math.Max(0, effectiveTarget - openCount);
                    targetStatus.Text = missing == 0
                        ? $"Đang mở {openCount}/{effectiveTarget} — đã đủ target."
                        : $"Đang mở {openCount}/{effectiveTarget} → sẽ bổ sung tuần tự {missing} PRF.";
                    targetStatus.ForeColor = missing == 0
                        ? Color.DarkGreen
                        : Color.FromArgb(174, 94, 24);
                }
            }
            else
            {
                targetStatus.Text = openCount > 0
                    ? $"Target = số PRF đang mở hiện tại: {openCount}. Không tự mở thêm."
                    : "Chưa có PRF đang mở — chế độ này không thể bắt đầu.";
                targetStatus.ForeColor = openCount > 0
                    ? Color.DimGray
                    : Color.Firebrick;
                valid = openCount > 0;
            }

            if (primeMode.Checked
                && !refreshAllBeforePrime.Checked
                && effectiveTarget > 0
                && freshTarget.Value > effectiveTarget)
            {
                targetStatus.Text =
                    $"PRF MỚI mục tiêu ({freshTarget.Value:0}) không được > target ({effectiveTarget}).";
                targetStatus.ForeColor = Color.Firebrick;
                valid = false;
            }

            if (primeMode.Checked && oldHours.Value <= freshHours.Value)
            {
                targetStatus.Text =
                    "Mốc PRF CŨ phải lớn hơn mốc PRF MỚI.";
                targetStatus.ForeColor = Color.Firebrick;
                valid = false;
            }

            if (primeMode.Checked
                && refreshAllBeforePrime.Checked
                && prepareMinutes.Value <= 0)
            {
                targetStatus.Text =
                    "THAY ACC TRƯỚC GIỜ VÀNG cần ‘Chuẩn bị trước giờ vàng’ lớn hơn 0 phút.";
                targetStatus.ForeColor = Color.Firebrick;
                valid = false;
            }

            timeInfo.Text = _autoCloseSettings.CloseOnRunTime
                ? $"Đang dùng TIME hiện tại: đủ tổng runtime {_autoCloseSettings.RunHours} giờ thì đóng sạch PRF cũ và Tự bù theo engine hiện có."
                : "TIME hiện đang TẮT trong cấu hình ‘Tự động’. Chọn chế độ Theo thời gian vẫn giữ nguyên cấu hình này; Tool không tự bật TIME.";

            var overTargetBlocked = ensure && openCount > effectiveTarget;
            launchWarning.Visible = overTargetBlocked;
            launchWarning.Text = overTargetBlocked
                ? $"⚠ Không thể Bắt đầu: đang mở {openCount} PRF > target {effectiveTarget}. Hãy tăng target hoặc đóng bớt PRF."
                : string.Empty;
            start.Text = overTargetBlocked ? "Bị chặn" : "Bắt đầu";

            start.Enabled = valid;
            UpdateMainCards();
        }

        launchCard.Click += (_, _) => ShowSection(0);
        timeCard.Click += (_, _) =>
        {
            timeMode.Checked = true;
            primeMode.Checked = false;
            ShowSection(1);
        };
        primeCard.Click += (_, _) =>
        {
            primeMode.Checked = true;
            timeMode.Checked = false;
            ShowSection(2);
        };

        autoEnsureTarget.CheckedChanged += (_, _) =>
        {
            UpdateLaunchValidation();

            // Công tắc này là preference riêng: lưu ngay khi user đổi để kể cả
            // đóng dialog/thoát Tool mà chưa bấm Lưu, lần mở sau vẫn giữ ON/OFF.
            try
            {
                _runStrategySettings.AutoEnsureTarget = autoEnsureTarget.Checked;
                SaveRunStrategySettings(_runStrategySettings);
                _log.Info(
                    $"[RUN_STRATEGY_MAINTAIN_TARGET_PREF_SAVE] enabled={autoEnsureTarget.Checked}");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[RUN_STRATEGY_MAINTAIN_TARGET_PREF_SAVE_WARN] enabled={autoEnsureTarget.Checked} error={ex.Message}");
            }
        };
        targetSlots.ValueChanged += (_, _) => UpdateLaunchValidation();
        freshTarget.ValueChanged += (_, _) => UpdateLaunchValidation();
        freshHours.ValueChanged += (_, _) =>
        {
            UpdateAgeLegend();
            UpdateReuseNote();
            UpdateLaunchValidation();
        };
        oldHours.ValueChanged += (_, _) =>
        {
            UpdateAgeLegend();
            UpdateLaunchValidation();
        };
        startHour.ValueChanged += (_, _) => UpdateMainCards();
        endHour.ValueChanged += (_, _) => UpdateMainCards();
        rotationMinutes.ValueChanged += (_, _) => UpdateLaunchValidation();
        prepareMinutes.ValueChanged += (_, _) => UpdateLaunchValidation();
        refreshAllBeforePrime.CheckedChanged += (_, _) =>
        {
            UpdatePrimeEnabled();
            UpdateLaunchValidation();
        };
        preserveFresh.CheckedChanged += (_, _) =>
        {
            UpdatePrimeEnabled();
            UpdateLaunchValidation();
        };
        prePrimeCurrentFresh.CheckedChanged += (_, _) =>
        {
            UpdateReuseNote();
            UpdateMainCards();
        };
        prePrimeNeverRun.CheckedChanged += (_, _) =>
        {
            UpdateReuseNote();
            UpdateMainCards();
        };
        primeMode.CheckedChanged += (_, _) =>
        {
            UpdatePrimeEnabled();
            UpdateLaunchValidation();
        };
        timeMode.CheckedChanged += (_, _) => UpdateLaunchValidation();

        save.Click += (_, _) =>
        {
            try
            {
                var saved = BuildSettingsFromDialog(startRequested: false);
                if (!ValidateSettingsForSave(saved, out var saveError))
                {
                    ModernDialog.ShowMessage(
                        form,
                        saveError,
                        "Auto Run",
                        MessageBoxIcon.Warning);
                    return;
                }

                SaveRunStrategySettings(saved);
                SyncSavedRunStrategyToOutsideAutomation(saved);

                // Cập nhật ngay note trong dialog theo trạng thái CHỈ PRF CHỜ vừa lưu.
                // Engine Tự bù cũng đọc trực tiếp _autoCloseSettings nên phiên đang
                // chạy sẽ áp dụng lựa chọn mới từ lần bù kế tiếp, không cần Start lại.
                UpdateReuseNote();

                save.Text = "Đã lưu ✓";
                _log.Info(
                    $"[RUN_STRATEGY_SETTINGS_SAVE_UI] mode={saved.Mode} autoEnsure={saved.AutoEnsureTarget} target={saved.TargetSlots} " +
                    $"primeArmed={saved.PrimeModeArmed} fullRefresh={saved.RefreshAllBeforePrime} source={saved.PrePrimeRefreshSource} " +
                    $"createLimit={saved.CreateLimitEnabled} perSlot={saved.CreateLimitPerSlot} perHour={saved.CreateLimitPerHour} " +
                    $"perSession={saved.CreateLimitPerSession} retryMinutes={saved.CreateLimitReuseRetryMinutes} " +
                    $"noCreateSchedule={saved.NoCreateScheduleEnabled} window={saved.NoCreateStartMinute / 60:00}:{saved.NoCreateStartMinute % 60:00}-" +
                    $"{saved.NoCreateEndMinute / 60:00}:{saved.NoCreateEndMinute % 60:00}");
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Không lưu được cấu hình Auto Run.\r\n\r\n" + ex.Message,
                    "Auto Run",
                    MessageBoxIcon.Error);
            }
        };

        UpdateAgeLegend();
        UpdatePrimeEnabled();
        UpdateLaunchValidation();
        ShowSection(activeSection);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(summary, 0, 1);
        root.Controls.Add(cardsRow, 0, 2);
        root.Controls.Add(detailsHost, 0, 3);
        root.Controls.Add(footer, 0, 4);
        form.Controls.Add(root);
        form.AcceptButton = start;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            ModernDialog.FitToWorkingArea(form);

            // Cảnh báo một lần ngay khi mở Auto Run nếu nút Bắt đầu đang bị khóa
            // chỉ vì số PRF đang mở lớn hơn target. Không đổi validation/engine hiện có.
            if (autoEnsureTarget.Checked && openCount > (int)targetSlots.Value)
            {
                ModernDialog.ShowMessage(
                    form,
                    $"Đang mở {openCount} PRF nhưng target chỉ là {(int)targetSlots.Value}.\r\n\r\n" +
                    "Tool không tự đóng PRF dư khi Bắt đầu. Hãy tăng target hoặc đóng bớt PRF.",
                    "Auto Run — chưa thể bắt đầu",
                    MessageBoxIcon.Warning);
            }
        };

        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        var selected = BuildSettingsFromDialog(startRequested: true);

        if (!ValidateSettingsForSave(selected, out var startSettingsError))
        {
            ModernDialog.ShowMessage(
                this,
                startSettingsError,
                "Auto Run",
                MessageBoxIcon.Warning);
            return;
        }

        var target = selected.AutoEnsureTarget
            ? selected.TargetSlots
            : openCount;

        if (target <= 0)
        {
            ModernDialog.ShowMessage(
                this,
                "Chưa có PRF đang mở. Hãy bật “Duy trì số lượng PRF” hoặc mở PRF trước.",
                "Auto Run",
                MessageBoxIcon.Information);
            return;
        }

        if (openCount > target)
        {
            ModernDialog.ShowMessage(
                this,
                $"Đang mở {openCount} PRF nhưng target chỉ là {target}. Tool không tự đóng PRF dư khi Bắt đầu. Hãy đóng bớt hoặc tăng target.",
                "Auto Run",
                MessageBoxIcon.Warning);
            return;
        }

        if (selected.Mode == RunAllStrategyMode.PrimeFresh
            && !selected.RefreshAllBeforePrime
            && selected.FreshTarget > target)
        {
            ModernDialog.ShowMessage(
                this,
                $"PRF MỚI mục tiêu ({selected.FreshTarget}) không được lớn hơn target ({target}).",
                "Auto Run",
                MessageBoxIcon.Warning);
            return;
        }

        if (selected.OldHours <= selected.FreshHours)
        {
            ModernDialog.ShowMessage(
                this,
                "Mốc PRF cũ phải lớn hơn mốc PRF mới.",
                "Auto Run",
                MessageBoxIcon.Warning);
            return;
        }

        SaveRunStrategySettings(selected);
        StopRunStrategySession("run_all_new_selection");
        ResetRunStrategyPrePrimeRefreshState("run_all_new_selection");
        ResetAutoReplacementCreateLimitSession("run_all_new_selection");

        // Đồng bộ cùng engine Tự động bên ngoài. Helper không tự tắt Tự bù nếu
        // user đã bật riêng cho BAN/TIME.
        SyncSavedRunStrategyToOutsideAutomation(selected);

        // Chốt target PHIÊN trước khi START profile đầu tiên. Tận dụng đúng fixed-slot
        // engine cũ; mục đích là BAN/FAULT xảy ra sớm trong startup không còn quyền
        // INITIAL_CAPTURE từ occupied tạm thời (ví dụ 2/4).
        SetRunAllDesiredTarget(target, "run_all_session_baseline_before_start");
        PersistRunStrategySessionTarget(
            target,
            selected.Mode == RunAllStrategyMode.PrimeFresh,
            "run_all_session_baseline_before_start");
        _log.Info(
            $"[RUN_ALL_SESSION_BASELINE] target={target} autoEnsure={selected.AutoEnsureTarget} open={openCount} mode={selected.Mode}");

        // Bước 1: tận dụng nguyên StartAll hiện có cho mọi PRF đang mở.
        // Không có PRF mở thì bỏ qua; target sẽ được fill ở bước 2.
        if (openCount > 0)
            await StartAllAsync();

        if (!selected.AutoEnsureTarget)
        {
            // Chế độ cũ: target chính là số tab đang mở.
            if (selected.Mode == RunAllStrategyMode.Time)
            {
                _log.Info(
                    $"[RUN_STRATEGY_SELECT] mode=TIME openedOnly=true target={target}");
                return;
            }

            StartRunStrategySession(selected, target);
            return;
        }

        // Bước 2: override target StartAll (vốn chỉ biết số tab mở) bằng target user chọn.
        // Sau đó bổ sung đúng 1 slot/lần: Chờ dùng lại trước, cuối cùng mới tạo mới.
        SetRunAllDesiredTarget(target, "run_all_auto_ensure_target");

        var startToken = BeginRunAllSequentialStart();
        int filled;
        try
        {
            filled = await EnsureRunAllTargetSequentialAsync(
                target,
                selected,
                startToken);
        }
        catch (OperationCanceledException)
        {
            _log.Info("[RUN_ALL_SEQUENTIAL_FILL_CANCELLED] source=user_stop_or_new_start");
            return;
        }

        if (selected.Mode == RunAllStrategyMode.PrimeFresh)
            StartRunStrategySession(selected, target);
        else
            _log.Info(
                $"[RUN_STRATEGY_SELECT] mode=TIME autoEnsure=true target={target} active={filled}");

        if (filled < target)
        {
            UpdateAutoCloseToolbarButtonText();
            var replacementStatus = GetAutoReplacementUiStatusText();

            ModernDialog.ShowMessage(
                this,
                $"Đã khởi động {filled}/{target} PRF.\n\n" +
                $"Trạng thái hiện tại: {replacementStatus}.\n\n" +
                "Tool vẫn đang hoạt động và giữ target đã chọn. Nếu đang CHỜ, Tool sẽ tự thử lại khi đến lượt; " +
                "nếu CHỜ NGUỒN, Tool đang đợi PRF phù hợp/capacity reconcile. Trạng thái bù được cập nhật trực tiếp trên thanh trên cùng.",
                "Auto Run",
                MessageBoxIcon.Information);
        }
    }

    CancellationToken BeginRunAllSequentialStart()
    {
        CancellationTokenSource old;
        CancellationToken token;

        lock (_runStrategyLock)
        {
            old = _runAllStartCts;
            _runAllStartCts = new CancellationTokenSource();
            token = _runAllStartCts.Token;
        }

        try { old.Cancel(); } catch { }
        try { old.Dispose(); } catch { }
        return token;
    }

    void SetRunAllDesiredTarget(
        int targetSlots,
        string source)
    {
        targetSlots = Math.Clamp(targetSlots, 1, 50);

        lock (_autoReplacementFixedSlotLock)
        {
            var old = _autoReplacementTargetSlots;
            _autoReplacementTargetSlots = targetSlots;
            _autoReplacementTargetInitialized = true;

            _log.Info(
                $"[RUN_ALL_TARGET_SET] source={source} old={old} target={targetSlots}");
        }

        ArmAutoReplacementSession(
            $"run_all_target:{targetSlots}:{source}");
        MarkNightReservePrimaryRunIntent(targetSlots, source);
    }

    async Task<int> EnsureRunAllTargetSequentialAsync(
        int targetSlots,
        RunAllStrategySettings settings,
        CancellationToken token)
    {
        targetSlots = Math.Clamp(targetSlots, 1, 50);

        // Nếu StartAll vừa phát sinh request bù thật (ví dụ một PRF mở sẵn lỗi),
        // không chen bootstrap trực tiếp vào queue đang xử lý. Queue cũ vốn tuần tự;
        // target đã được set ở trên nên nó sẽ tiếp tục lấp đúng số slot.
        if (_autoReplacementQueueRunning || GetAutoReplacementPendingCount() > 0)
        {
            _log.Info(
                $"[RUN_ALL_SEQUENTIAL_FILL_DEFER] active={GetRunStrategyActiveContexts().Count} target={targetSlots} pending={GetAutoReplacementPendingCount()} queueRunning={_autoReplacementQueueRunning}");
            return GetRunStrategyActiveContexts().Count;
        }

        var previousStartAllGate = _autoReplacementStartAllInProgress;
        var previousQueueGate = _autoReplacementQueueRunning;

        // Giữ hai gate hiện có trong suốt bootstrap để Capacity Reconcile/queue
        // không chen vào và tạo nhiều slot cùng lúc. Chính hàm này mở 1 PRF, chờ
        // helper cũ xác nhận RUNNING khỏe, rồi mới xét slot kế tiếp.
        _autoReplacementStartAllInProgress = true;
        _autoReplacementQueueRunning = true;

        try
        {
            while (!_closing && !IsDisposed && !Disposing)
            {
                token.ThrowIfCancellationRequested();

                var activeCount = GetRunStrategyActiveContexts().Count;
                if (activeCount >= targetSlots)
                    return activeCount;

                var slotNumber = activeCount + 1;
                _log.Info(
                    $"[RUN_ALL_SEQUENTIAL_FILL_BEGIN] slot={slotNumber}/{targetSlots} active={activeCount} oneAtATime=true");

                var filled = await TryFillRunAllStartupSlotAsync(
                    settings,
                    slotNumber,
                    targetSlots,
                    token);

                if (!filled)
                {
                    _log.Warn(
                        $"[RUN_ALL_SEQUENTIAL_FILL_WAIT] slot={slotNumber}/{targetSlots} active={GetRunStrategyActiveContexts().Count} reason=no_supply_or_not_healthy");
                    return GetRunStrategyActiveContexts().Count;
                }

                var nowActive = GetRunStrategyActiveContexts().Count;
                _log.Info(
                    $"[RUN_ALL_SEQUENTIAL_FILL_OK] slot={slotNumber}/{targetSlots} active={nowActive} oneAtATime=true");

                // Chỉ một khoảng settle rất ngắn. Login cooldown / stabilization
                // riêng của engine cũ vẫn được tôn trọng đầy đủ.
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            return GetRunStrategyActiveContexts().Count;
        }
        finally
        {
            _autoReplacementQueueRunning = previousQueueGate;
            _autoReplacementStartAllInProgress = previousStartAllGate;

            // Nếu có request tự động phát sinh trong lúc bootstrap, trả gate xong
            // mới cho queue cũ chạy. Queue runner vốn tuần tự.
            if (!IsAutomationHalted
                && !_autoReplacementQueueRunning
                && GetAutoReplacementPendingCount() > 0
                && !_closing
                && _autoReplacementSessionArmed
                && _autoCloseSettings.OpenReplacementAfterAutoClose)
            {
                _ = RunAutoReplacementQueueAsync();
            }
        }
    }

    async Task<bool> TryFillRunAllStartupSlotAsync(
        RunAllStrategySettings settings,
        int slotNumber,
        int targetSlots,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (settings.Mode == RunAllStrategyMode.PrimeFresh)
        {
            var active = GetRunStrategyActiveContexts();
            var phase = GetRunStrategyPhase(GetToolNow(), settings);
            var freshLimit = TimeSpan.FromHours(settings.FreshHours).TotalSeconds;
            var desiredFresh = Math.Min(settings.FreshTarget, targetSlots);
            var freshActive = active.Count(ctx =>
                GetRunStrategyTotalSeconds(ctx) < freshLimit);

            await RefreshReusableProfileQueueAsync(
                "run_all_initial_supply_probe:" + phase,
                token);

            if (phase is "PRIME" or "PREPARE")
            {
                // Giờ vàng có HAI mục tiêu tách biệt:
                // 1) chỉ lấp quota MỚI đến đúng FreshTarget;
                // 2) các slot còn lại ưu tiên TB/CŨ có runtime THẤP nhất.
                // Nhờ vậy target=5 / MỚI=3 không biến thành 5/5 fresh khi kho đang
                // có profile 3-6h hoặc >=6h phù hợp.
                if (freshActive < desiredFresh)
                {
                    foreach (var candidate in GetRunStrategyReusableCandidates(
                                 settings,
                                 RunStrategyLane.Fresh,
                                 preferLowerRuntime: true,
                                 allowProtectedNightReserve: true))
                    {
                        if (await TryUseSpecificRunStrategyReusableProfileAsync(
                                candidate.ProfileName,
                                "RUN_ALL_START",
                                $"INITIAL_{phase}_FRESH_{slotNumber}",
                                token,
                                allowProtectedNightReserveBorrow: true))
                        {
                            return true;
                        }
                    }

                    // Thiếu quota MỚI thật sự: chỉ lúc này mới được tạo đúng 1 PRF mới.
                    if (IsAutomaticNewProfileCreationAllowedNow()
                        && await TryCreateRunStrategyReplacementAsync(
                            "RUN_ALL_START",
                            $"INITIAL_{phase}_FRESH_{slotNumber}",
                            token))
                    {
                        return true;
                    }

                    // Nếu chưa thể có fresh thì vẫn cố giữ đủ tổng target bằng PRF
                    // đã có, nhưng không tạo thêm profile chỉ để lấp slot phụ.
                    foreach (var lane in new[] { RunStrategyLane.Medium, RunStrategyLane.Old })
                    {
                        foreach (var candidate in GetRunStrategyReusableCandidates(
                                     settings,
                                     lane,
                                     preferLowerRuntime: true))
                        {
                            if (await TryUseSpecificRunStrategyReusableProfileAsync(
                                    candidate.ProfileName,
                                    "RUN_ALL_START",
                                    $"INITIAL_{phase}_FALLBACK_{lane}_{slotNumber}",
                                    token))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                // Đã đủ quota MỚI: TUYỆT ĐỐI không tạo thêm fresh nếu vẫn còn
                // TB/CŨ trong Chờ dùng lại. Giờ vàng ưu tiên runtime thấp trước:
                // MEDIUM luôn trẻ hơn OLD, và trong mỗi lane cũng sort tăng dần.
                foreach (var lane in new[] { RunStrategyLane.Medium, RunStrategyLane.Old })
                {
                    foreach (var candidate in GetRunStrategyReusableCandidates(
                                 settings,
                                 lane,
                                 preferLowerRuntime: true))
                    {
                        if (await TryUseSpecificRunStrategyReusableProfileAsync(
                                candidate.ProfileName,
                                "RUN_ALL_START",
                                $"INITIAL_{phase}_NONFRESH_{lane}_{slotNumber}",
                                token))
                        {
                            return true;
                        }
                    }
                }

                // Chỉ khi kho không còn TB/CŨ phù hợp mới dùng thêm fresh có sẵn.
                foreach (var candidate in GetRunStrategyReusableCandidates(
                             settings,
                             RunStrategyLane.Fresh,
                             preferLowerRuntime: true))
                {
                    if (await TryUseSpecificRunStrategyReusableProfileAsync(
                            candidate.ProfileName,
                            "RUN_ALL_START",
                            $"INITIAL_{phase}_EXTRA_FRESH_{slotNumber}",
                            token))
                    {
                        return true;
                    }
                }

                // Không còn reusable phù hợp: lúc này mới tạo mới để giữ đủ target.
                if (!IsAutomaticNewProfileCreationAllowedNow())
                    return false;

                return await TryCreateRunStrategyReplacementAsync(
                    "RUN_ALL_START",
                    $"INITIAL_{phase}_DEFICIT_{slotNumber}",
                    token);
            }

            // Ngoài giờ vàng: tận dụng profile chạy NHIỀU giờ trước để tiêu nốt
            // vòng đời, bảo tồn profile ít giờ cho hôm sau. OLD -> MEDIUM -> FRESH;
            // trong từng lane runtime cao nhất đứng trước.
            foreach (var lane in new[]
                     {
                         RunStrategyLane.Old,
                         RunStrategyLane.Medium,
                         RunStrategyLane.Fresh
                     })
            {
                foreach (var candidate in GetRunStrategyReusableCandidates(
                             settings,
                             lane,
                             preferLowerRuntime: false))
                {
                    if (await TryUseSpecificRunStrategyReusableProfileAsync(
                            candidate.ProfileName,
                            "RUN_ALL_START",
                            $"INITIAL_OFFPEAK_{lane}_{slotNumber}",
                            token))
                    {
                        return true;
                    }
                }
            }

            if (!IsAutomaticNewProfileCreationAllowedNow())
                return false;

            return await TryCreateRunStrategyReplacementAsync(
                "RUN_ALL_START",
                $"INITIAL_OFFPEAK_DEFICIT_{slotNumber}",
                token);
        }

        // Chế độ Theo thời gian giữ nguyên queue reuse hiện có; chỉ Prime Time
        // áp dụng chính sách tuổi runtime ở trên.
        if (await TryUseAnyRunStrategyReusableProfileAsync(
                $"INITIAL_SLOT_{slotNumber}",
                token))
        {
            return true;
        }

        if (!IsAutomaticNewProfileCreationAllowedNow())
            return false;

        return await TryCreateRunStrategyReplacementAsync(
            "RUN_ALL_START",
            $"INITIAL_SLOT_{slotNumber}",
            token);
    }

    TimeSpan GetRunStrategyNameSyncRetryDelay(string? onlyProfileName = null)
    {
        List<DateTime> lastChecked;
        lock (_reusableProfileQueueLock)
        {
            lastChecked = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x =>
                    x.NameSyncPending
                    && (string.IsNullOrWhiteSpace(onlyProfileName)
                        || x.ProfileName.Equals(
                            onlyProfileName,
                            StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.LastCheckedUtc)
                .ToList();
        }

        if (lastChecked.Count == 0)
            return TimeSpan.Zero;

        var nowUtc = DateTime.UtcNow;
        var wait = TimeSpan.Zero;
        foreach (var checkedUtc in lastChecked)
        {
            var age = nowUtc - checkedUtc;
            var remaining = AutoReplacementNameSyncMinRetryAge - age;
            if (remaining > wait)
                wait = remaining;
        }

        if (wait <= TimeSpan.Zero)
            return TimeSpan.Zero;

        // Không bao giờ chờ quá cửa sổ retry chuẩn, kể cả clock hệ thống vừa nhảy.
        return wait > AutoReplacementNameSyncMinRetryAge
            ? AutoReplacementNameSyncMinRetryAge
            : wait;
    }

    async Task<bool> TryUseAnyRunStrategyReusableProfileAsync(
        string reason,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var execution = CaptureAutoReplacementExecution();
        if (!IsAutoReplacementExecutionAllowed(execution.Generation))
            return false;

        var request = new AutoReplacementRequest
        {
            Id = "run-all-start-" + Guid.NewGuid().ToString("N"),
            ClosedProfileName = "RUN_ALL_START_SLOT_" + Guid.NewGuid().ToString("N")[..8],
            Reason = "RUN_ALL_" + reason,
            QueuedUtc = DateTime.UtcNow,
            NextAttemptUtc = DateTime.UtcNow,
            RequiresSourceCleanup = false
        };

        try
        {
            var opened = await TryUseReusableProfileQueueAsync(
                request,
                execution.Generation,
                execution.Token);

            if (opened)
                return true;

            // Run All/Run Strategy trước đây chỉ vét lane reuse thường rồi có thể
            // rơi thẳng sang CREATE, trong khi NAME_SYNC_PENDING vẫn còn PRF đã tạo
            // và đã login. Vét lane chờ đồng bộ tên bằng CHÍNH request này để giữ
            // one-attempt/profile và tuyệt đối ưu tiên PRF có sẵn trước account mới.
            _log.Info(
                $"[RUN_ALL_NAME_SYNC_BEFORE_CREATE] id={request.Id} reason={reason} pending={GetNameSyncPendingReusableProfileCount()} attempted={GetAutoReplacementAttemptedProfileCount(request)}");

            // Nếu PRF chờ tên vừa mới được kiểm tra, đợi tối đa cửa sổ 60s để TẤT CẢ
            // entry pending đủ tuổi rồi mới sweep. Mục tiêu là không tiêu account mới
            // chỉ vì PRF có sẵn còn thiếu vài giây để được probe lại.
            var nameSyncWait = GetRunStrategyNameSyncRetryDelay();
            if (nameSyncWait > TimeSpan.Zero)
            {
                _log.Info(
                    $"[RUN_ALL_NAME_SYNC_WAIT_BEFORE_CREATE] id={request.Id} wait={nameSyncWait:c} pending={GetNameSyncPendingReusableProfileCount()}");
                await Task.Delay(nameSyncWait, token);
            }

            return await TryRecoverNameSyncPendingReusableProfilesOnceAsync(
                request,
                execution.Generation,
                execution.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_ALL_REUSE_FILL_FAIL] reason={reason} error={ex.Message}");
            return false;
        }
    }

    void StartRunStrategySession(
        RunAllStrategySettings settings,
        int targetSlots)
    {
        if (IsAutomationHalted)
            return;

        settings = NormalizeRunStrategySettings(settings);
        targetSlots = Math.Max(0, targetSlots);

        CancellationTokenSource oldCts;
        lock (_runStrategyLock)
        {
            oldCts = _runStrategyCts;
            _runStrategyCts = new CancellationTokenSource();
            _runStrategySettings = settings;
            _runStrategyTargetSlots = targetSlots;
            _runStrategyNextRotationUtc = DateTime.MinValue;
            _runStrategyObservedPhase = "";
            _runStrategySessionActive =
                settings.Mode == RunAllStrategyMode.PrimeFresh
                && targetSlots > 0;
        }

        try { oldCts.Cancel(); } catch { }
        try { oldCts.Dispose(); } catch { }

        if (!_runStrategySessionActive)
            return;

        // Giữ target phiên qua Manager restart/update. Snapshot không tự tạo PRF;
        // nó chỉ giúp fixed-slot target được phục hồi trước các event BAN/FAULT.
        PersistRunStrategySessionTarget(
            targetSlots,
            active: true,
            source: "run_strategy_prime_start");

        ArmAutoReplacementSession("run_strategy_prime_start");

        _log.Info(
            $"[RUN_STRATEGY_START] mode=PRIME armed={settings.PrimeModeArmed} target={targetSlots} prime={settings.PrimeStartHour:00}:00-{settings.PrimeEndHour:00}:00 " +
            $"freshTarget={Math.Min(settings.FreshTarget, targetSlots)} freshUnder={settings.FreshHours}h oldFrom={settings.OldHours}h " +
            $"interval={settings.RotationIntervalMinutes}m prepare={settings.PrepareMinutes}m preserveOffPeak={settings.PreserveFreshOffPeak} " +
            $"prePrimeFullRefresh={settings.RefreshAllBeforePrime} prePrimeSource={settings.PrePrimeRefreshSource}");
    }

    void StopRunStrategySession(string source)
    {
        // Mỗi lần kết thúc/chuyển phiên Auto Run đều kết thúc quyền tạo reserve của
        // phiên cũ. Auto Run mới sẽ arm lại sau khi capture target mới.
        ResetNightReservePrimaryRunIntent(source);

        CancellationTokenSource? oldCts = null;
        CancellationTokenSource? startCts = null;
        var wasActive = false;

        lock (_runStrategyLock)
        {
            wasActive = _runStrategySessionActive;
            _runStrategySessionActive = false;
            _runStrategyTargetSlots = 0;
            _runStrategyNextRotationUtc = DateTime.MinValue;
            _runStrategyObservedPhase = "";

            if (!_runStrategyCts.IsCancellationRequested)
                oldCts = _runStrategyCts;
            if (!_runAllStartCts.IsCancellationRequested)
                startCts = _runAllStartCts;
        }

        if (oldCts is not null)
        {
            try { oldCts.Cancel(); } catch { }
        }
        if (startCts is not null)
        {
            try { startCts.Cancel(); } catch { }
        }

        if (source.Equals("stop_all", StringComparison.OrdinalIgnoreCase))
        {
            lock (_autoReplacementFixedSlotLock)
            {
                _autoReplacementTargetSlots = 0;
                _autoReplacementTargetInitialized = true;
            }

            _log.Info(
                "[RUN_ALL_TARGET_STOP_ALL] target=0 initialized=true");
        }

        if (source.Equals("stop_all", StringComparison.OrdinalIgnoreCase)
            || source.Equals("emergency_stop", StringComparison.OrdinalIgnoreCase)
            || source.Equals("emergency_resume_resync", StringComparison.OrdinalIgnoreCase))
        {
            PersistRunStrategySessionTarget(0, active: false, source: source);
        }

        // Không disarm Giờ vàng; chỉ persist trạng thái "đang chờ START mới".
        // Điều này phân biệt restart bình thường (được tự restore nếu Worker còn RUNNING)
        // với restart sau Stop All / Dừng khẩn cấp (không được tự bật lại do Worker sót).
        if (source.Equals("stop_all", StringComparison.OrdinalIgnoreCase)
            || source.Equals("emergency_stop", StringComparison.OrdinalIgnoreCase)
            || source.Equals("emergency_resume_resync", StringComparison.OrdinalIgnoreCase))
        {
            var persistSuspended = false;
            RunAllStrategySettings? settingsToSave = null;
            lock (_runStrategyLock)
            {
                if (_runStrategySettings.Mode == RunAllStrategyMode.PrimeFresh
                    && _runStrategySettings.PrimeModeArmed
                    && !_runStrategySettings.PrimeModeSuspended)
                {
                    _runStrategySettings.PrimeModeSuspended = true;
                    settingsToSave = _runStrategySettings;
                    persistSuspended = true;
                }
            }

            if (persistSuspended && settingsToSave is not null)
            {
                try { SaveRunStrategySettings(settingsToSave); }
                catch (Exception ex)
                {
                    _log.Warn($"[RUN_STRATEGY_SUSPEND_SAVE_WARN] source={source} error={ex.Message}");
                }
            }
        }

        if (wasActive || startCts is not null)
            _log.Info($"[RUN_STRATEGY_STOP] source={source}");
    }

    void RestoreArmedRunStrategySessionIfNeeded(string source)
    {
        if (IsAutomationHalted
            || !_runStrategyFeatureInitialized
            || _closing
            || IsDisposed
            || Disposing)
        {
            return;
        }

        RunAllStrategySettings settings;
        bool sessionActive;
        int currentTarget;

        lock (_runStrategyLock)
        {
            settings = _runStrategySettings;
            sessionActive = _runStrategySessionActive;
            currentTarget = _runStrategyTargetSlots;
        }

        if (settings.Mode != RunAllStrategyMode.PrimeFresh
            || !settings.PrimeModeArmed)
        {
            return;
        }

        // Target của Tự bù là nguồn sự thật khi đã được khởi tạo. Đặc biệt target=0
        // sau Stop All / Dừng khẩn cấp là CHỦ Ý, nên không được suy ngược từ các tab
        // RUNNING còn đang unwind trong vài giây.
        int desiredTarget;
        bool targetInitialized;
        lock (_autoReplacementFixedSlotLock)
        {
            desiredTarget = _autoReplacementTargetSlots;
            targetInitialized = _autoReplacementTargetInitialized;
        }

        if (settings.PrimeModeSuspended)
        {
            // Chỉ một target MỚI được thiết lập bởi Start/Auto Run trong phiên hiện tại
            // mới bỏ trạng thái chờ. target chưa initialized sau restart không đủ bằng chứng.
            if (!targetInitialized || desiredTarget <= 0)
                return;

            settings.PrimeModeSuspended = false;
            try
            {
                SaveRunStrategySettings(settings);
                _log.Info(
                    $"[RUN_STRATEGY_RESUME_ARMED] source={source} target={desiredTarget}");
            }
            catch (Exception ex)
            {
                // Không mở scheduler nếu chưa persist được trạng thái resume; nếu Manager
                // crash ngay sau đó thì lần mở sau vẫn phải hiểu đúng là đang chờ START.
                settings.PrimeModeSuspended = true;
                _log.Warn(
                    $"[RUN_STRATEGY_RESUME_SAVE_WARN] source={source} error={ex.Message}");
                return;
            }
        }

        if (!targetInitialized)
        {
            // Manager vừa restart không mang target session cũ sang. Nếu Worker cũ
            // thực sự đang RUNNING/RECOVERING thì khôi phục Giờ vàng theo đúng số
            // runtime đang hoạt động; tab STOPPED không được tính.
            desiredTarget = _contexts.Values.Count(ctx =>
            {
                if (ctx.Tab is null
                    || ctx.Tab.IsDisposed
                    || ctx.Tab.Parent != _tabs)
                {
                    return false;
                }

                var state = GetEffectiveRuntimeState(ctx);
                return state is RuntimeStateRunning or RuntimeStateRecovering;
            });
        }

        if (desiredTarget <= 0)
            return;

        if (sessionActive && currentTarget == desiredTarget)
            return;

        _log.Info(
            $"[RUN_STRATEGY_RESTORE_ARMED] source={source} target={desiredTarget} previousActive={sessionActive} previousTarget={currentTarget}");

        StartRunStrategySession(settings, desiredTarget);
    }

    void ObserveRunStrategyPhaseClock()
    {
        if (IsAutomationHalted
            || !_runStrategyFeatureInitialized
            || _closing
            || IsDisposed
            || Disposing)
        {
            return;
        }

        RunAllStrategySettings settings;
        lock (_runStrategyLock)
        {
            if (!_runStrategySessionActive)
                return;

            settings = _runStrategySettings;
        }

        var currentPhase = GetRunStrategyPhase(GetToolNow(), settings);
        string previousPhase;
        var changed = false;
        var initialized = false;

        lock (_runStrategyLock)
        {
            if (!_runStrategySessionActive)
                return;

            previousPhase = _runStrategyObservedPhase;
            if (string.IsNullOrWhiteSpace(previousPhase))
            {
                _runStrategyObservedPhase = currentPhase;
                initialized = true;
            }
            else if (!string.Equals(previousPhase, currentPhase, StringComparison.Ordinal))
            {
                _runStrategyObservedPhase = currentPhase;

                // Cooldown chỉ giới hạn hai lần xoay TRONG CÙNG phase. Khi đồng hồ
                // đổi phase, hành vi mới được phép đánh giá ngay ở tick kế tiếp.
                _runStrategyNextRotationUtc = DateTime.MinValue;
                changed = true;
            }
        }

        if (initialized)
        {
            _log.Info($"[RUN_STRATEGY_PHASE_INIT] phase={currentPhase}");
        }
        else if (changed)
        {
            _log.Info(
                $"[RUN_STRATEGY_PHASE_CHANGE] from={previousPhase} to={currentPhase} action=RESET_ROTATION_COOLDOWN");
        }
    }

    async Task CheckRunStrategyAsync()
    {
        if (IsAutomationHalted
            || !_runStrategyFeatureInitialized
            || _runStrategyTickBusy
            || _runStrategyRotationRunning
            || _closing
            || IsDisposed
            || Disposing)
        {
            return;
        }

        // Giờ vàng là mode đã "arm", không còn phụ thuộc lifetime của một phiên
        // Auto Run. Stop All/Emergency chỉ pause runtime; khi target hợp lệ quay lại
        // (Start thủ công hoặc Worker cũ sau restart) scheduler tự khôi phục.
        RestoreArmedRunStrategySessionIfNeeded("tick");

        if (!_runStrategySessionActive)
            return;

        // Restore có thể vừa arm session trong chính tick này, nên quan sát phase ngay
        // thay vì đợi tick Timer kế tiếp.
        ObserveRunStrategyPhaseClock();

        _runStrategyTickBusy = true;
        try
        {
            RunAllStrategySettings settings;
            int targetSlots;
            DateTime nextRotationUtc;
            CancellationToken token;

            lock (_runStrategyLock)
            {
                if (!_runStrategySessionActive)
                    return;

                settings = _runStrategySettings;
                targetSlots = _runStrategyTargetSlots;
                nextRotationUtc = _runStrategyNextRotationUtc;
                token = _runStrategyCts.Token;
            }

            if (targetSlots <= 0 || DateTime.UtcNow < nextRotationUtc)
                return;

            if (!CanRunStrategyRotateNow())
                return;

            var active = GetRunStrategyActiveContexts();
            if (active.Count != targetSlots)
                return;

            // Chỉ xoay khi toàn bộ target hiện tại thực sự RUNNING. Nếu một slot đang
            // RECOVERING/STOPPED thì để watchdog/Tự bù hiện có xử lý trước.
            if (active.Any(ctx =>
                    !string.Equals(
                        GetEffectiveRuntimeState(ctx),
                        RuntimeStateRunning,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            var now = GetToolNow();
            var phase = GetRunStrategyPhase(now, settings);
            var plan = await BuildRunStrategyRotationPlanAsync(
                phase,
                active,
                settings,
                token);

            if (plan is null)
                return;

            // Build plan có thể phải refresh queue / kiểm tra account. Nếu đúng lúc đó
            // đồng hồ qua phase khác, tuyệt đối không khởi động một rotation theo phase cũ.
            var latestPhase = GetRunStrategyPhase(GetToolNow(), settings);
            if (!string.Equals(plan.Phase, latestPhase, StringComparison.Ordinal))
            {
                ObserveRunStrategyPhaseClock();
                _log.Info(
                    $"[RUN_STRATEGY_STALE_PLAN_SKIP] planned={plan.Phase} current={latestPhase} victim={plan.Victim.Profile.Name}");
                return;
            }

            var rotated = await ExecuteRunStrategyRotationAsync(
                plan,
                settings,
                token);

            // Rotation có thể kéo dài qua đúng mốc chuyển phase. Cooldown của phase cũ
            // không được đè lên reset mà phase watcher vừa tạo.
            var phaseAfterExecute = GetRunStrategyPhase(GetToolNow(), settings);
            if (!string.Equals(phase, phaseAfterExecute, StringComparison.Ordinal))
            {
                ObserveRunStrategyPhaseClock();
                _log.Info(
                    $"[RUN_STRATEGY_PHASE_CHANGED_DURING_ROTATION] from={phase} to={phaseAfterExecute} action=NO_OLD_COOLDOWN");
            }
            else
            {
                lock (_runStrategyLock)
                {
                    if (_runStrategySessionActive)
                    {
                        _runStrategyNextRotationUtc = rotated
                            ? (plan.IsPrePrimeFullRefresh
                                // Full refresh đã đóng sạch + mở khỏe từng slot; chuyển
                                // slot kế tiếp ngay sau một nhịp ngắn, không áp interval
                                // 5-60 phút của chiến lược xoay tuổi legacy.
                                ? DateTime.UtcNow.AddSeconds(2)
                                : DateTime.UtcNow.AddMinutes(settings.RotationIntervalMinutes))
                            : (plan.IsPrePrimeFullRefresh
                                ? DateTime.UtcNow.AddSeconds(15)
                                : DateTime.UtcNow.AddMinutes(2));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop/Dừng tất cả: kết thúc yên lặng.
        }
        catch (Exception ex)
        {
            _log.Error("[RUN_STRATEGY_TICK] " + ex);
            lock (_runStrategyLock)
            {
                if (_runStrategySessionActive)
                    _runStrategyNextRotationUtc = DateTime.UtcNow.AddMinutes(2);
            }
        }
        finally
        {
            _runStrategyTickBusy = false;
        }
    }

    bool CanRunStrategyRotateNow()
    {
        if (!_runStrategySessionActive
            || _autoReplacementStartAllInProgress
            || _autoReplacementCapacityReconcileRunning
            || _autoReplacementQueueRunning
            || GetAutoReplacementPendingCount() > 0
            || _autoCloseInProgressProfiles.Count > 0
            || _autoReplacementClaimedProfiles.Count > 0
            || _autoReplacementCleanupProfiles.Count > 0)
        {
            return false;
        }

        // + Auto Profile / Tạo trước PRF / Tự bù dùng chung gate này. Prime Time
        // chỉ được xoay khi pipeline tạo profile đang rảnh.
        if (_autoProfileQueueGate.CurrentCount <= 0)
            return false;

        return !_contexts.Values.Any(ctx => ctx.Opening);
    }

    List<ProfileContext> GetRunStrategyActiveContexts()
        => _contexts.Values
            .Where(ctx =>
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs)
            .OrderBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
            .ToList();

    void ResetRunStrategyPrePrimeRefreshState(string source)
    {
        lock (_runStrategyLock)
        {
            _runStrategyPrePrimePendingProfiles.Clear();
            _runStrategyPrePrimeAcceptedProfiles.Clear();
            _runStrategyPrePrimeDisallowedCandidates.Clear();
            _runStrategyPrePrimeCycleKey = "";
            _runStrategyPrePrimeCycleStarted = false;
            _runStrategyPrePrimeCycleCompleted = false;
        }

        _log.Info($"[PRE_GOLD_REFRESH_RESET] source={source}");
    }

    static string GetRunStrategyPrePrimeCycleKey(
        DateTimeOffset now,
        RunAllStrategySettings settings)
        => $"{now:yyyy-MM-dd}|{settings.PrimeStartHour:00}:00";

    bool IsRunStrategyPrePrimeCycleStartedFor(
        DateTimeOffset now,
        RunAllStrategySettings settings)
    {
        var key = GetRunStrategyPrePrimeCycleKey(now, settings);
        lock (_runStrategyLock)
        {
            return _runStrategyPrePrimeCycleStarted
                   && string.Equals(
                       _runStrategyPrePrimeCycleKey,
                       key,
                       StringComparison.Ordinal);
        }
    }

    bool IsRunStrategyPrePrimeCandidateDisallowed(string profileName)
    {
        lock (_runStrategyLock)
        {
            return _runStrategyPrePrimeDisallowedCandidates.Contains(
                (profileName ?? "").Trim());
        }
    }

    void EnsureRunStrategyPrePrimeCycleStarted(
        DateTimeOffset now,
        IReadOnlyList<ProfileContext> active,
        RunAllStrategySettings settings,
        int targetSlots)
    {
        var key = GetRunStrategyPrePrimeCycleKey(now, settings);
        var activeNames = active
            .Select(ctx => (ctx.Profile.Name ?? "").Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var startedNow = false;
        var completedNow = false;
        var pendingCount = 0;
        var acceptedCount = 0;

        lock (_runStrategyLock)
        {
            if (!string.Equals(
                    _runStrategyPrePrimeCycleKey,
                    key,
                    StringComparison.Ordinal))
            {
                _runStrategyPrePrimePendingProfiles.Clear();
                _runStrategyPrePrimeAcceptedProfiles.Clear();
                _runStrategyPrePrimeDisallowedCandidates.Clear();
                _runStrategyPrePrimeCycleKey = key;
                _runStrategyPrePrimeCycleStarted = false;
                _runStrategyPrePrimeCycleCompleted = false;
            }

            if (!_runStrategyPrePrimeCycleStarted)
            {
                _runStrategyPrePrimeCycleStarted = true;
                _runStrategyPrePrimeCycleCompleted = false;
                _runStrategyPrePrimeAcceptedProfiles.Clear();
                _runStrategyPrePrimePendingProfiles.Clear();
                _runStrategyPrePrimeDisallowedCandidates.Clear();

                foreach (var name in activeNames)
                {
                    _runStrategyPrePrimePendingProfiles.Add(name);
                    _runStrategyPrePrimeDisallowedCandidates.Add(name);
                }

                startedNow = true;
            }
            else if (!_runStrategyPrePrimeCycleCompleted)
            {
                // Accepted chỉ có hiệu lực khi profile đó vẫn là thành viên target.
                // Nếu nó BAN trong lúc refresh, Tự bù có thể đưa profile khác vào;
                // profile mới đó chưa được refresh chủ động nên phải vào pending.
                _runStrategyPrePrimeAcceptedProfiles.RemoveWhere(
                    name => !activeNames.Contains(name));
                _runStrategyPrePrimePendingProfiles.RemoveWhere(
                    name => !activeNames.Contains(name));

                foreach (var name in activeNames)
                {
                    if (_runStrategyPrePrimeAcceptedProfiles.Contains(name))
                        continue;

                    _runStrategyPrePrimePendingProfiles.Add(name);
                    _runStrategyPrePrimeDisallowedCandidates.Add(name);
                }
            }

            if (!_runStrategyPrePrimeCycleCompleted
                && targetSlots > 0
                && activeNames.Count == targetSlots
                && activeNames.All(name =>
                    _runStrategyPrePrimeAcceptedProfiles.Contains(name)))
            {
                _runStrategyPrePrimeCycleCompleted = true;
                _runStrategyPrePrimePendingProfiles.Clear();
                completedNow = true;
            }

            pendingCount = _runStrategyPrePrimePendingProfiles.Count;
            acceptedCount = _runStrategyPrePrimeAcceptedProfiles.Count;
        }

        if (startedNow)
        {
            _log.Info(
                $"[PRE_GOLD_REFRESH_BEGIN] cycle={key} target={targetSlots} active={activeNames.Count} " +
                $"source={settings.PrePrimeRefreshSource} prepare={settings.PrepareMinutes}m pending={pendingCount}");
        }
        else if (completedNow)
        {
            _log.Info(
                $"[PRE_GOLD_REFRESH_DONE] cycle={key} target={targetSlots} accepted={acceptedCount} " +
                "action=HOLD_UNTIL_BAN_OR_NEXT_PREPARE");
        }
    }

    void RecordRunStrategyPrePrimeRotationResult(
        string victimName,
        IReadOnlyCollection<string> activeBefore,
        bool filled,
        RunAllStrategySettings settings)
    {
        var activeAfter = GetRunStrategyActiveContexts()
            .Select(ctx => (ctx.Profile.Name ?? "").Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = activeAfter
            .Where(name => !activeBefore.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        string acceptedProfile = "";
        var completedNow = false;
        int pendingCount;
        int acceptedCount;
        int targetSlots;
        string cycleKey;

        lock (_runStrategyLock)
        {
            targetSlots = _runStrategyTargetSlots;
            cycleKey = _runStrategyPrePrimeCycleKey;

            if (!_runStrategyPrePrimeCycleStarted
                || _runStrategyPrePrimeCycleCompleted)
            {
                return;
            }

            if (!activeAfter.Contains(victimName))
                _runStrategyPrePrimePendingProfiles.Remove(victimName);
            else
                _runStrategyPrePrimePendingProfiles.Add(victimName);

            // Planned rotation giữ gate Tự bù nên một lần thành công bình thường chỉ
            // có đúng một profile mới xuất hiện. Nếu có >1 thì không đoán: tick sau
            // sẽ coi chúng là pending và xử lý lại an toàn.
            if (filled && added.Count == 1)
            {
                var candidate = added[0];
                if (!_runStrategyPrePrimeDisallowedCandidates.Contains(candidate))
                {
                    _runStrategyPrePrimeAcceptedProfiles.Add(candidate);
                    _runStrategyPrePrimePendingProfiles.Remove(candidate);
                    acceptedProfile = candidate;
                }
            }

            if (targetSlots > 0
                && activeAfter.Count == targetSlots
                && activeAfter.All(name =>
                    _runStrategyPrePrimeAcceptedProfiles.Contains(name)))
            {
                _runStrategyPrePrimeCycleCompleted = true;
                _runStrategyPrePrimePendingProfiles.Clear();
                completedNow = true;
            }

            pendingCount = _runStrategyPrePrimePendingProfiles.Count;
            acceptedCount = _runStrategyPrePrimeAcceptedProfiles.Count;
        }

        _log.Info(
            $"[PRE_GOLD_REFRESH_RESULT] cycle={cycleKey} victim={victimName} filled={filled} " +
            $"accepted={(acceptedProfile.Length > 0 ? acceptedProfile : "-")} added={string.Join(",", added)} " +
            $"acceptedCount={acceptedCount}/{targetSlots} pending={pendingCount} source={settings.PrePrimeRefreshSource}");

        if (completedNow)
        {
            _log.Info(
                $"[PRE_GOLD_REFRESH_DONE] cycle={cycleKey} target={targetSlots} accepted={acceptedCount} " +
                "action=HOLD_UNTIL_BAN_OR_NEXT_PREPARE");
        }
    }

    async Task<RunStrategyRotationPlan?> BuildRunStrategyPrePrimeFullRefreshPlanAsync(
        string phase,
        IReadOnlyList<ProfileContext> active,
        RunAllStrategySettings settings,
        CancellationToken token)
    {
        var now = GetToolNow();
        int targetSlots;
        lock (_runStrategyLock)
            targetSlots = _runStrategyTargetSlots;

        if (phase == "PREPARE")
        {
            EnsureRunStrategyPrePrimeCycleStarted(
                now,
                active,
                settings,
                targetSlots);
        }
        else if (phase == "PRIME")
        {
            // Nếu PREPARE đã bắt đầu nhưng chưa kịp thay hết thì tiếp tục tuần tự
            // qua mốc giờ vàng. Nếu Manager/Auto Run chỉ được bật sau khi đã vào
            // PRIME thì không khởi động một đợt refresh muộn ngoài yêu cầu.
            if (!IsRunStrategyPrePrimeCycleStartedFor(now, settings))
                return null;

            EnsureRunStrategyPrePrimeCycleStarted(
                now,
                active,
                settings,
                targetSlots);
        }
        else
        {
            return null;
        }

        HashSet<string> pendingSnapshot;
        lock (_runStrategyLock)
        {
            if (_runStrategyPrePrimeCycleCompleted)
                return null;

            pendingSnapshot = new HashSet<string>(
                _runStrategyPrePrimePendingProfiles,
                StringComparer.OrdinalIgnoreCase);
        }

        // Không giữ _runStrategyLock trong lúc đọc runtime_stats.json.
        var victim = active
            .Where(ctx => pendingSnapshot.Contains(ctx.Profile.Name))
            .OrderByDescending(GetRunStrategyTotalSeconds)
            .ThenBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
            .FirstOrDefault();

        if (victim is null)
            return null;

        await RefreshReusableProfileQueueAsync(
            $"pre_gold_refresh_supply:{settings.PrePrimeRefreshSource}",
            token);

        var neverRunOnly =
            settings.PrePrimeRefreshSource == PrePrimeRefreshSourceMode.NeverRunOnly;

        var available = GetRunStrategyReusableCandidates(
                settings,
                RunStrategyLane.Fresh,
                excludeProfileName: victim.Profile.Name,
                preferLowerRuntime: true,
                allowProtectedNightReserve: true,
                neverRunOnly: neverRunOnly)
            .Any(candidate =>
                !IsRunStrategyPrePrimeCandidateDisallowed(candidate.ProfileName));

        var canCreate = IsAutomaticNewProfileCreationAllowedNow();
        if (!available && canCreate)
            canCreate = await HasRunStrategyNewAccountSupplyAsync(token);

        if (!available && !canCreate)
        {
            lock (_runStrategyLock)
            {
                if (_runStrategySessionActive)
                    _runStrategyNextRotationUtc = DateTime.UtcNow.AddSeconds(15);
            }

            _log.Info(
                $"[PRE_GOLD_REFRESH_WAIT_SUPPLY] victim={victim.Profile.Name} source={settings.PrePrimeRefreshSource} " +
                $"reuseOnly={_autoCloseSettings.ReuseOnlyNoCreateProfile} retry=15s action=KEEP_CURRENT");
            return null;
        }

        return new RunStrategyRotationPlan(
            phase,
            victim,
            new[] { RunStrategyLane.Fresh },
            AllowFreshEmergencyFallback: false,
            AllowCreateFallback: canCreate)
        {
            IsPrePrimeFullRefresh = true,
            ForcedPrePrimeSource = settings.PrePrimeRefreshSource
        };
    }

    // AutoClose TIME phải được tắt logic trong mode này: sau khi user chọn full
    // refresh, profile được giữ tới BAN/FAULT/manual hoặc kỳ PREPARE ngày kế tiếp.
    // BAN và watchdog lỗi kỹ thuật không bị ảnh hưởng.
    bool ShouldSuppressAutoCloseTimeForPrePrimeRefresh()
    {
        if (!_runStrategyFeatureInitialized)
            return false;

        lock (_runStrategyLock)
        {
            return _runStrategySettings.Mode == RunAllStrategyMode.PrimeFresh
                   && _runStrategySettings.PrimeModeArmed
                   && !_runStrategySettings.PrimeModeSuspended
                   && _runStrategySettings.RefreshAllBeforePrime;
        }
    }

    string GetRunStrategyPhase(
        DateTimeOffset now,
        RunAllStrategySettings settings)
    {
        var minute = now.Hour * 60 + now.Minute;
        var start = settings.PrimeStartHour * 60;
        var end = settings.PrimeEndHour * 60;
        var prepareStart = Math.Max(0, start - settings.PrepareMinutes);

        if (minute >= start && minute < end)
            return "PRIME";

        if (settings.PrepareMinutes > 0
            && minute >= prepareStart
            && minute < start)
        {
            return "PREPARE";
        }

        return "OFFPEAK";
    }

    async Task<RunStrategyRotationPlan?> BuildRunStrategyRotationPlanAsync(
        string phase,
        IReadOnlyList<ProfileContext> active,
        RunAllStrategySettings settings,
        CancellationToken token)
    {
        var runtimes = active.ToDictionary(
            ctx => ctx.Profile.Name,
            GetRunStrategyTotalSeconds,
            StringComparer.OrdinalIgnoreCase);

        // Chế độ mới là một chiến lược tách biệt: chỉ thay toàn bộ một lần ở PREPARE
        // (có thể hoàn tất nốt trong PRIME), còn OFFPEAK/PRIME sau đó không xoay theo
        // tuổi. BAN/FAULT vẫn do AutoClose/Tự bù hiện tại xử lý.
        if (settings.RefreshAllBeforePrime)
        {
            if (phase is "PREPARE" or "PRIME")
            {
                return await BuildRunStrategyPrePrimeFullRefreshPlanAsync(
                    phase,
                    active,
                    settings,
                    token);
            }

            return null;
        }

        var freshLimit = TimeSpan.FromHours(settings.FreshHours).TotalSeconds;
        var oldLimit = TimeSpan.FromHours(settings.OldHours).TotalSeconds;
        var desiredFresh = Math.Min(settings.FreshTarget, active.Count);
        var freshActive = active.Count(ctx =>
            runtimes.TryGetValue(ctx.Profile.Name, out var seconds)
            && seconds < freshLimit);

        if (phase is "PRIME" or "PREPARE")
        {
            if (freshActive < desiredFresh)
            {
                // Thiếu fresh: OLD trước, hết OLD mới đến MEDIUM già nhất.
                var victim = active
                    .Where(ctx => runtimes[ctx.Profile.Name] >= oldLimit)
                    .OrderByDescending(ctx => runtimes[ctx.Profile.Name])
                    .FirstOrDefault()
                    ?? active
                        .Where(ctx =>
                            runtimes[ctx.Profile.Name] >= freshLimit
                            && runtimes[ctx.Profile.Name] < oldLimit)
                        .OrderByDescending(ctx => runtimes[ctx.Profile.Name])
                        .FirstOrDefault();

                if (victim is null)
                    return null;

                // Không đóng profile tốt trước rồi mới phát hiện hết nguồn fresh.
                // Phải có PRF fresh chờ sẵn hoặc có account mới phù hợp để tạo 1 PRF.
                await RefreshReusableProfileQueueAsync(
                    "run_strategy_supply_probe:" + phase,
                    token);

                var hasFreshReusable = GetRunStrategyReusableCandidates(
                        settings,
                        RunStrategyLane.Fresh,
                        excludeProfileName: victim.Profile.Name,
                        preferLowerRuntime: true,
                        allowProtectedNightReserve: true)
                    .Count > 0;

                var canCreate = IsAutomaticNewProfileCreationAllowedNow();
                if (!hasFreshReusable && canCreate)
                    canCreate = await HasRunStrategyNewAccountSupplyAsync(token);

                if (!hasFreshReusable && !canCreate)
                {
                    _log.Info(
                        $"[RUN_STRATEGY_WAIT_SUPPLY] phase={phase} fresh={freshActive}/{desiredFresh} victim={victim.Profile.Name} action=KEEP_CURRENT");
                    return null;
                }

                return new RunStrategyRotationPlan(
                    phase,
                    victim,
                    new[] { RunStrategyLane.Fresh },
                    AllowFreshEmergencyFallback: false,
                    AllowCreateFallback: canCreate);
            }

            if (freshActive > desiredFresh)
            {
                // Có nhiều MỚI hơn mục tiêu (ví dụ 5/5 fresh nhưng mục tiêu=3).
                // Chỉ giảm dư khi kho đã có TB/CŨ sẵn; không tạo profile mới để
                // "sửa tỷ lệ". Mỗi tick vẫn thay đúng 1 PRF.
                await RefreshReusableProfileQueueAsync(
                    "run_strategy_trim_excess_fresh:" + phase,
                    token);

                var hasMedium = GetRunStrategyReusableCandidates(
                        settings,
                        RunStrategyLane.Medium,
                        preferLowerRuntime: true)
                    .Count > 0;
                var hasOld = GetRunStrategyReusableCandidates(
                        settings,
                        RunStrategyLane.Old,
                        preferLowerRuntime: true)
                    .Count > 0;

                if (!hasMedium && !hasOld)
                    return null;

                // Cất PRF fresh ít runtime nhất để bảo tồn profile trẻ cho lượt sau.
                var excessFreshVictim = active
                    .Where(ctx => runtimes[ctx.Profile.Name] < freshLimit)
                    .OrderBy(ctx => runtimes[ctx.Profile.Name])
                    .FirstOrDefault();

                if (excessFreshVictim is null)
                    return null;

                return new RunStrategyRotationPlan(
                    phase,
                    excessFreshVictim,
                    new[] { RunStrategyLane.Medium, RunStrategyLane.Old },
                    AllowFreshEmergencyFallback: false,
                    AllowCreateFallback: false);
            }

            // freshActive == desiredFresh: đã đúng mục tiêu, không xoay thêm.
            return null;
        }

        if (!settings.PreserveFreshOffPeak)
            return null;

        // 00:00 -> trước PREPARE: cất dần PRF fresh để dành ngày mai. Chỉ cất khi
        // đã có OLD/MEDIUM trong kho để thay. Nếu kho thiếu thì GIỮ fresh đang chạy;
        // deficit thật do BAN/TIME vẫn do Tự bù hiện có xử lý.
        var freshVictim = active
            .Where(ctx => runtimes[ctx.Profile.Name] < freshLimit)
            .OrderBy(ctx => runtimes[ctx.Profile.Name])
            .FirstOrDefault();

        if (freshVictim is null)
            return null;

        await RefreshReusableProfileQueueAsync(
            "run_strategy_supply_probe:OFFPEAK",
            token);

        var oldAvailable = GetRunStrategyReusableCandidates(
                settings,
                RunStrategyLane.Old,
                excludeProfileName: freshVictim.Profile.Name)
            .Count > 0;
        var mediumAvailable = GetRunStrategyReusableCandidates(
                settings,
                RunStrategyLane.Medium,
                excludeProfileName: freshVictim.Profile.Name)
            .Count > 0;

        if (!oldAvailable && !mediumAvailable)
            return null;

        return new RunStrategyRotationPlan(
            "OFFPEAK",
            freshVictim,
            new[] { RunStrategyLane.Old, RunStrategyLane.Medium },
            AllowFreshEmergencyFallback: true,
            AllowCreateFallback: IsAutomaticNewProfileCreationAllowedNow());
    }

    double GetRunStrategyTotalSeconds(ProfileContext ctx)
    {
        double persisted = 0;
        try { persisted = ReadReusableProfileTotalSeconds(ctx.Profile); }
        catch { }

        var snapshot = ctx.LastSnapshot?.TotalRunSeconds ?? -1;
        return Math.Max(
            0,
            Math.Max(
                persisted,
                snapshot >= 0 ? snapshot : 0));
    }

    List<RunStrategyReusableCandidate> GetRunStrategyReusableCandidates(
        RunAllStrategySettings settings,
        RunStrategyLane lane,
        string? excludeProfileName = null,
        bool? preferLowerRuntime = null,
        bool allowProtectedNightReserve = false,
        bool neverRunOnly = false)
    {
        var freshLimit = TimeSpan.FromHours(settings.FreshHours).TotalSeconds;
        var oldLimit = TimeSpan.FromHours(settings.OldHours).TotalSeconds;
        List<RunStrategyReusableCandidate> snapshot;

        lock (_reusableProfileQueueLock)
        {
            snapshot = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.ProfileName)
                    && (allowProtectedNightReserve
                        || !IsNightReserveProfileProtected(entry.ProfileName))
                    && !entry.ProfileName.Equals(
                        excludeProfileName ?? "",
                        StringComparison.OrdinalIgnoreCase))
                .Select(entry => new RunStrategyReusableCandidate(
                    entry.ProfileName.Trim(),
                    Math.Max(0, entry.TotalRunSeconds),
                    entry.IsManual))
                .ToList();
        }

        IEnumerable<RunStrategyReusableCandidate> filtered = lane switch
        {
            RunStrategyLane.Fresh => snapshot.Where(x => x.TotalRunSeconds < freshLimit),
            RunStrategyLane.Medium => snapshot.Where(x =>
                x.TotalRunSeconds >= freshLimit
                && x.TotalRunSeconds < oldLimit),
            RunStrategyLane.Old => snapshot.Where(x => x.TotalRunSeconds >= oldLimit),
            _ => Array.Empty<RunStrategyReusableCandidate>()
        };

        if (neverRunOnly)
        {
            // Dùng đúng nguồn runtime_stats hiện có. Profile chưa từng Automation
            // RUNNING có totalRunSeconds = 0; profile đã chạy dù rất ngắn bị loại.
            filtered = filtered.Where(x => x.TotalRunSeconds <= 0.001);
        }

        // Prime/PREPARE truyền preferLowerRuntime=true: runtime thấp trước.
        // OFFPEAK truyền false: runtime cao trước để tận dụng nốt vòng đời.
        // Call-site cũ không truyền tham số vẫn giữ hành vi legacy theo lane.
        var lowerFirst = preferLowerRuntime ?? (lane == RunStrategyLane.Fresh);
        filtered = lowerFirst
            ? filtered
                .OrderBy(x => x.TotalRunSeconds)
                .ThenByDescending(x => x.IsManual)
                .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
            : filtered
                .OrderByDescending(x => x.TotalRunSeconds)
                .ThenByDescending(x => x.IsManual)
                .ThenBy(x => x.ProfileName, NaturalProfileNameOrder);

        return filtered.ToList();
    }

    async Task<bool> HasRunStrategyNewAccountSupplyAsync(CancellationToken token)
    {
        if (!IsAutomaticNewProfileCreationAllowedNow())
            return false;

        try
        {
            var startName = DetectNextAutoProfileName();
            var queue = await RunAccountPoolIoAsync(
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                        _accountPoolService.ReloadCurrentExcel();
                    _accountPoolService.EnsureAutoColumns();

                    // Probe này gọi BuildAutoProfileQueue (có điểm COMMIT account),
                    // nên phải re-check chính sách ngay sát Build, không chỉ ở đầu hàm.
                    lock (_autoReplacementCreateModeGate)
                    {
                        if (_autoCloseSettings.ReuseOnlyNoCreateProfile
                            || TryGetAutoReplacementNoCreateScheduleBlock(
                                out _,
                                out _))
                        {
                            return new List<AutoProfileQueueItem>();
                        }

                        return BuildAutoProfileQueue(
                            requestedNew: 1,
                            requestedStartName: startName,
                            resumeIncomplete: false,
                            retryPaused: false);
                    }
                },
                token);

            return queue.Any(item => !item.ResumeExisting);
        }
        catch (Exception ex)
        {
            _log.Warn($"[RUN_STRATEGY_NEW_SUPPLY_PROBE] error={ex.Message}");
            return false;
        }
    }

    async Task<bool> ExecuteRunStrategyRotationAsync(
        RunStrategyRotationPlan plan,
        RunAllStrategySettings settings,
        CancellationToken token)
    {
        if (_runStrategyRotationRunning || !CanRunStrategyRotateNow())
            return false;

        // Chốt lại phase ngay trước khi bắt đầu thao tác vật lý. Timer phase watcher
        // có thể đã đổi PRIME/OFFPEAK trong khoảng giữa build-plan và execute.
        var executePhase = GetRunStrategyPhase(GetToolNow(), settings);
        if (!string.Equals(plan.Phase, executePhase, StringComparison.Ordinal))
        {
            ObserveRunStrategyPhaseClock();
            _log.Info(
                $"[RUN_STRATEGY_STALE_EXECUTE_SKIP] planned={plan.Phase} current={executePhase} victim={plan.Victim.Profile.Name}");
            return false;
        }

        _runStrategyRotationRunning = true;

        // Mượn chính 2 gate sẵn có để trong lúc planned rotation đang giữ 1 slot,
        // capacity reconcile/queue runner không nhìn khoảng trống tạm thời rồi mở bù
        // thêm một profile khác. Sau khi slot mới healthy thì mới trả gate.
        _autoReplacementStartAllInProgress = true;
        _autoReplacementQueueRunning = true;

        var victimName = plan.Victim.Profile.Name;
        var activeBefore = plan.IsPrePrimeFullRefresh
            ? GetRunStrategyActiveContexts()
                .Select(ctx => (ctx.Profile.Name ?? "").Trim())
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filled = false;
        try
        {
            token.ThrowIfCancellationRequested();

            _log.Info(
                $"[RUN_STRATEGY_ROTATE_BEGIN] phase={plan.Phase} victim={victimName} runtime={TimeSpan.FromSeconds(GetRunStrategyTotalSeconds(plan.Victim)):c} " +
                $"lanes={string.Join(",", plan.PreferredLanes)} oneAtATime=true prePrimeFull={plan.IsPrePrimeFullRefresh} " +
                $"prePrimeSource={(plan.ForcedPrePrimeSource?.ToString() ?? "-")}");

            // Planned close KHÔNG gọi RegisterManagerManualCloseIntent và KHÔNG mark
            // retired: target vẫn giữ nguyên, profile tốt được quét lại vào Chờ dùng lại.
            await CloseRunStrategyProfileAsync(
                plan.Victim,
                plan.Phase,
                token);

            token.ThrowIfCancellationRequested();
            await RefreshReusableProfileQueueAsync(
                "run_strategy_after_close:" + plan.Phase,
                token);

            filled = await TryFillRunStrategySlotAsync(
                victimName,
                plan,
                settings,
                token);

            if (plan.IsPrePrimeFullRefresh)
            {
                RecordRunStrategyPrePrimeRotationResult(
                    victimName,
                    activeBefore,
                    filled,
                    settings);
            }

            _log.Info(
                $"[RUN_STRATEGY_ROTATE_END] phase={plan.Phase} victim={victimName} filled={filled} oneAtATime=true");

            return filled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"[RUN_STRATEGY_ROTATE_FAIL] phase={plan.Phase} victim={victimName} error={ex}");
            return false;
        }
        finally
        {
            _autoReplacementQueueRunning = false;
            _autoReplacementStartAllInProgress = false;
            _runStrategyRotationRunning = false;

            // Nếu trong lúc planned rotation một profile khác tự BAN/TIME/FAULT thì
            // request đã được xếp hàng nhưng bị gate chặn. Trả gate xong cho queue cũ
            // tiếp tục đúng tuần tự của nó.
            if (!IsAutomationHalted
                && GetAutoReplacementPendingCount() > 0
                && !_closing
                && _autoReplacementSessionArmed
                && _autoCloseSettings.OpenReplacementAfterAutoClose)
            {
                _ = RunAutoReplacementQueueAsync();
            }

            // Nếu planned slot chưa fill được, dùng capacity reconcile hiện có làm
            // safety-net. Reconcile vẫn 2-pass, không mở hàng loạt do scheduler.
            if (!IsAutomationHalted
                && !filled
                && !_closing
                && _autoCloseSettings.OpenReplacementAfterAutoClose
                && _autoReplacementSessionArmed)
            {
                _ = MaybeReconcileAutoReplacementCapacityAsync(
                    "run_strategy_unfilled",
                    force: true);
            }
        }
    }

    async Task CloseRunStrategyProfileAsync(
        ProfileContext ctx,
        string phase,
        CancellationToken token)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();
        if (profileName.Length == 0)
            throw new InvalidOperationException("Profile planned rotation không có tên.");

        _autoReplacementCleanupProfiles.Add(profileName);
        try
        {
            token.ThrowIfCancellationRequested();

            var workerAlive = false;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
            catch { workerAlive = ctx.Worker is not null; }

            if (workerAlive)
            {
                try
                {
                    await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[RUN_STRATEGY_STOP_WARN] profile={profileName} phase={phase} error={ex.Message}");
                }

                try
                {
                    var reply = await SendCloseChromeCommandAsync(ctx);
                    _log.Info(
                        $"[RUN_STRATEGY_CLOSE_CHROME] profile={profileName} phase={phase} reply={reply}");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[RUN_STRATEGY_CLOSE_CHROME_WARN] profile={profileName} phase={phase} error={ex.Message}");
                }
            }

            token.ThrowIfCancellationRequested();

            // Tận dụng cleanup strict đang dùng cho AutoClose/Tự bù: Worker chết thật,
            // Chrome đúng ProfilePath sạch rồi mới gỡ tab và mở profile thay thế.
            await EnsureAutoCloseWorkerStoppedAsync(ctx);
            await EnsureAutoCloseChromeStoppedAsync(ctx);

            if (ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs)
            {
                RemoveTab(ctx);
            }

            ClearAutoCloseExpectedRunning(
                profileName,
                "run_strategy_planned_close:" + phase);

            _log.Info(
                $"[RUN_STRATEGY_PLANNED_CLOSE_DONE] profile={profileName} phase={phase} retired=false reusable=true");
        }
        finally
        {
            _autoReplacementCleanupProfiles.Remove(profileName);
        }
    }

    async Task<bool> TryFillRunStrategySlotAsync(
        string outgoingProfileName,
        RunStrategyRotationPlan plan,
        RunAllStrategySettings settings,
        CancellationToken token)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var preferLowerRuntime =
            plan.Phase is "PRIME" or "PREPARE";

        foreach (var lane in plan.PreferredLanes)
        {
            await RefreshReusableProfileQueueAsync(
                $"run_strategy_fill:{plan.Phase}:{lane}",
                token);

            var allowProtectedNightReserveBorrow =
                lane == RunStrategyLane.Fresh
                && plan.Phase is "PRIME" or "PREPARE";

            var neverRunOnly =
                plan.IsPrePrimeFullRefresh
                && plan.ForcedPrePrimeSource == PrePrimeRefreshSourceMode.NeverRunOnly;

            foreach (var candidate in GetRunStrategyReusableCandidates(
                         settings,
                         lane,
                         excludeProfileName: outgoingProfileName,
                         preferLowerRuntime: preferLowerRuntime,
                         allowProtectedNightReserve: allowProtectedNightReserveBorrow,
                         neverRunOnly: neverRunOnly))
            {
                if (plan.IsPrePrimeFullRefresh
                    && IsRunStrategyPrePrimeCandidateDisallowed(candidate.ProfileName))
                {
                    continue;
                }

                if (!tried.Add(candidate.ProfileName))
                    continue;

                if (await TryUseSpecificRunStrategyReusableProfileAsync(
                        candidate.ProfileName,
                        outgoingProfileName,
                        plan.Phase,
                        token,
                        allowProtectedNightReserveBorrow))
                {
                    return true;
                }
            }
        }

        if (plan.AllowFreshEmergencyFallback)
        {
            await RefreshReusableProfileQueueAsync(
                "run_strategy_fill_emergency_fresh:" + plan.Phase,
                token);

            foreach (var candidate in GetRunStrategyReusableCandidates(
                         settings,
                         RunStrategyLane.Fresh,
                         excludeProfileName: outgoingProfileName,
                         preferLowerRuntime: false,
                         neverRunOnly: plan.IsPrePrimeFullRefresh
                             && plan.ForcedPrePrimeSource == PrePrimeRefreshSourceMode.NeverRunOnly))
            {
                if (plan.IsPrePrimeFullRefresh
                    && IsRunStrategyPrePrimeCandidateDisallowed(candidate.ProfileName))
                {
                    continue;
                }

                if (!tried.Add(candidate.ProfileName))
                    continue;

                if (await TryUseSpecificRunStrategyReusableProfileAsync(
                        candidate.ProfileName,
                        outgoingProfileName,
                        plan.Phase + "_EMERGENCY_FRESH",
                        token))
                {
                    return true;
                }
            }
        }

        // Trong PRIME/PREPARE, mục tiêu là fresh nên tạo mới trước khi phục hồi victim.
        // OFFPEAK thì ngược lại: ưu tiên mở lại chính PRF vừa cất trước; chỉ khi
        // slot thật sự không thể phục hồi mới tiêu account mới ở nhánh cuối.
        if (plan.Phase != "OFFPEAK"
            && plan.AllowCreateFallback
            && IsAutomaticNewProfileCreationAllowedNow()
            && await TryCreateRunStrategyReplacementAsync(
                outgoingProfileName,
                plan.Phase,
                token))
        {
            return true;
        }

        // Nếu nguồn thay thất bại, ưu tiên mở lại chính profile vừa cất để không làm
        // target tụt. Đây vẫn là MỘT slot; không đụng tới profile thứ hai.
        await RefreshReusableProfileQueueAsync(
            "run_strategy_restore_outgoing:" + plan.Phase,
            token);

        if (await TryUseSpecificRunStrategyReusableProfileAsync(
                outgoingProfileName,
                outgoingProfileName,
                plan.Phase + "_RESTORE",
                token))
        {
            _log.Warn(
                $"[RUN_STRATEGY_RESTORE_OUTGOING] profile={outgoingProfileName} phase={plan.Phase}");
            return true;
        }

        // OFFPEAK: chỉ khi đã thật sự tạo ra deficit và không thể mở lại profile cũ
        // mới dùng account mới, đúng yêu cầu “thiếu thì mới tạo PRF mới”.
        if (plan.Phase == "OFFPEAK"
            && plan.AllowCreateFallback
            && IsAutomaticNewProfileCreationAllowedNow())
        {
            return await TryCreateRunStrategyReplacementAsync(
                outgoingProfileName,
                plan.Phase + "_DEFICIT",
                token);
        }

        return false;
    }

    async Task<bool> TryUseSpecificRunStrategyReusableProfileAsync(
        string candidateProfileName,
        string outgoingProfileName,
        string reason,
        CancellationToken token,
        bool allowProtectedNightReserveBorrow = false)
    {
        candidateProfileName = (candidateProfileName ?? "").Trim();
        if (candidateProfileName.Length == 0)
            return false;

        token.ThrowIfCancellationRequested();
        var execution = CaptureAutoReplacementExecution();
        if (!IsAutoReplacementExecutionAllowed(execution.Generation))
            return false;

        // Tận dụng toàn bộ one-attempt / Name Guard / stabilization / cleanup của
        // queue reuse cũ. Để buộc helper chỉ lấy đúng lane đã chọn, đánh dấu mọi
        // profile khác là attempted trong request tạm thời này. Lấy cả tên đang có
        // trong queue (không chỉ _contexts) để không lọt candidate do catalog refresh race.
        List<ReusableProfileQueueEntry> reusableSnapshot;
        lock (_reusableProfileQueueLock)
        {
            reusableSnapshot = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        var attempted = _contexts.Keys
            .Concat(reusableSnapshot.Select(x => x.ProfileName))
            .Where(name =>
                !string.IsNullOrWhiteSpace(name)
                && !name.Equals(candidateProfileName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var request = new AutoReplacementRequest
        {
            Id = "prime-" + Guid.NewGuid().ToString("N"),
            // TryUseReusableProfileQueueAsync luôn bỏ qua ClosedProfileName. Planned
            // rotation cần có khả năng mở lại chính outgoing như phương án cứu slot,
            // nên dùng marker slot ảo; candidate thật vẫn bị khóa chính xác bằng
            // AttemptedProfiles bên dưới.
            ClosedProfileName = "RUN_STRATEGY_SLOT_" + Guid.NewGuid().ToString("N")[..8],
            Reason = "RUN_STRATEGY_" + reason
                + "; outgoing=" + outgoingProfileName
                + (allowProtectedNightReserveBorrow
                    ? "; allow_night_reserve_borrow=fresh_quota"
                    : ""),
            QueuedUtc = DateTime.UtcNow,
            NextAttemptUtc = DateTime.UtcNow,
            RequiresSourceCleanup = false,
            AttemptedProfiles = attempted
        };

        try
        {
            var nameSyncPending = reusableSnapshot.Any(x =>
                x.NameSyncPending
                && x.ProfileName.Equals(
                    candidateProfileName,
                    StringComparison.OrdinalIgnoreCase));

            if (nameSyncPending)
            {
                _log.Info(
                    $"[RUN_STRATEGY_NAME_SYNC_CANDIDATE] candidate={candidateProfileName} outgoing={outgoingProfileName} reason={reason} action=PROBE_BEFORE_CREATE");

                // Không bỏ qua candidate chỉ vì vừa check <60s rồi rơi sang CREATE.
                // Chờ đúng phần còn lại của cửa sổ retry rồi probe PRF có sẵn trước.
                var nameSyncWait = GetRunStrategyNameSyncRetryDelay(candidateProfileName);
                if (nameSyncWait > TimeSpan.Zero)
                {
                    _log.Info(
                        $"[RUN_STRATEGY_NAME_SYNC_WAIT] candidate={candidateProfileName} wait={nameSyncWait:c} reason={reason}");
                    await Task.Delay(nameSyncWait, token);
                }

                // Request tạm đã đánh dấu mọi profile khác là attempted, vì vậy helper
                // NAME_SYNC_PENDING chỉ được phép thử đúng candidate Run Strategy đã
                // chọn theo lane/runtime. Nếu chưa sync, vòng ngoài sẽ chuyển sang PRF
                // kế tiếp thay vì tạo mới ngay.
                return await TryRecoverNameSyncPendingReusableProfilesOnceAsync(
                    request,
                    execution.Generation,
                    execution.Token);
            }

            return await TryUseReusableProfileQueueAsync(
                request,
                execution.Generation,
                execution.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_STRATEGY_REUSE_FAIL] candidate={candidateProfileName} outgoing={outgoingProfileName} reason={reason} error={ex.Message}");
            return false;
        }
    }

    async Task<bool> TryCreateRunStrategyReplacementAsync(
        string outgoingProfileName,
        string phase,
        CancellationToken token)
    {
        if (!IsAutomaticNewProfileCreationAllowedNow())
            return false;

        token.ThrowIfCancellationRequested();
        var execution = CaptureAutoReplacementExecution();
        if (!IsAutoReplacementExecutionAllowed(execution.Generation))
            return false;

        var request = new AutoReplacementRequest
        {
            Id = "prime-create-" + Guid.NewGuid().ToString("N"),
            ClosedProfileName = outgoingProfileName,
            Reason = "RUN_STRATEGY_" + phase,
            QueuedUtc = DateTime.UtcNow,
            NextAttemptUtc = DateTime.UtcNow,
            RequiresSourceCleanup = false
        };

        try
        {
            // Dùng nguyên engine tạo PRF bù hiện có: account gate, login cooldown,
            // đổi tên, user* BAN, Name Guard, one-attempt và healthy confirmation.
            return await TryCreateReplacementAsync(
                request,
                execution.Generation,
                execution.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[RUN_STRATEGY_CREATE_FAIL] outgoing={outgoingProfileName} phase={phase} error={ex.Message}");
            return false;
        }
    }
}
