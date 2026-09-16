using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
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

    sealed class RunAllStrategySettings
    {
        public int Version { get; set; } = 3;
        public RunAllStrategyMode Mode { get; set; } = RunAllStrategyMode.Time;

        // V3: khi user đã chọn Giờ vàng + Bắt đầu, giữ "ý định vận hành" này
        // qua Stop All / Dừng khẩn cấp / restart Manager. Chỉ khi user chọn lại
        // chế độ Theo thời gian rồi Bắt đầu thì mới disarm Giờ vàng.
        public bool PrimeModeArmed { get; set; }

        // Mode vẫn được nhớ nhưng Stop All / Dừng khẩn cấp đặt scheduler vào trạng thái
        // chờ. Một START mới (target > 0) sẽ tự bỏ cờ này mà không cần chọn Giờ vàng lại.
        public bool PrimeModeSuspended { get; set; }

        // V2: "Chạy tất cả" có target độc lập với số tab đang mở.
        // Mặc định Tool tự bảo đảm đủ target; user vẫn có thể chọn chỉ chạy tab đang mở.
        public bool AutoEnsureTarget { get; set; } = true;
        public int TargetSlots { get; set; } = 5;

        public int PrimeStartHour { get; set; } = 11;
        public int PrimeEndHour { get; set; } = 24;
        public int FreshTarget { get; set; } = 3;
        public int FreshHours { get; set; } = 3;
        public int OldHours { get; set; } = 6;
        public int RotationIntervalMinutes { get; set; } = 10;
        public int PrepareMinutes { get; set; } = 30;
        public bool PreserveFreshOffPeak { get; set; } = true;
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
        bool AllowCreateFallback);

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

    string RunStrategySettingsPath
        => Path.Combine(_baseDir, "manager_run_strategy.json");

    void InitializeRunStrategyFeature()
    {
        if (_runStrategyFeatureInitialized)
            return;

        _runStrategyFeatureInitialized = true;

        // Prime Time dùng trực tiếp Tự bù + queue Chờ dùng lại hiện có. Bảo đảm
        // các subsystem này đã được load trước khi dialog đọc trạng thái.
        InitializeAutoCloseFeature();
        _runStrategySettings = LoadRunStrategySettings();

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
        settings.Version = 3;
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
            PreserveFreshOffPeak = _runStrategySettings.PreserveFreshOffPeak
        });

        using var form = new Form
        {
            Text = $"Auto Run — {AppVersionInfo.Display}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(800, 790),
            BackColor = UiTheme.Canvas,
            Font = new Font("Segoe UI", 9.5F),
            AutoScaleMode = AutoScaleMode.Dpi
        };
        ModernDialog.Apply(form);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(18, 14, 18, 12),
            BackColor = ModernDialog.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var title = new Label
        {
            Text = "CHỌN CHIẾN LƯỢC CHẠY",
            AutoSize = true,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Margin = new Padding(0, 0, 0, 4)
        };

        var summary = new Label
        {
            Text = $"PRF đang mở hiện tại: {openCount}. Có thể chạy các PRF đang mở hoặc để Tool tự bổ sung tuần tự đến target.",
            AutoSize = true,
            MaximumSize = new Size(665, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 12)
        };

        var launchBox = new GroupBox
        {
            Text = "Cách khởi động / số PRF cần duy trì",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var launchGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        launchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        launchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        launchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));

        var autoEnsureTarget = new RadioButton
        {
            Text = "Tự đảm bảo đủ số PRF — dùng PRF đang mở trước → Chờ dùng lại → cuối cùng mới tạo mới",
            Checked = current.AutoEnsureTarget,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 4)
        };
        launchGrid.SetColumnSpan(autoEnsureTarget, 3);
        launchGrid.Controls.Add(autoEnsureTarget, 0, 0);

        var openedOnly = new RadioButton
        {
            Text = "Chỉ chạy PRF đang mở — không tự mở thêm PRF",
            Checked = !current.AutoEnsureTarget,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F),
            Margin = new Padding(0, 2, 0, 6)
        };
        launchGrid.SetColumnSpan(openedOnly, 3);
        launchGrid.Controls.Add(openedOnly, 0, 1);

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
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(5, 8, 0, 4)
        };

        launchGrid.Controls.Add(targetLabel, 0, 2);
        launchGrid.Controls.Add(targetSlots, 1, 2);
        launchGrid.Controls.Add(targetStatus, 2, 2);
        launchBox.Controls.Add(launchGrid);

        var modePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10)
        };

        var timeMode = new RadioButton
        {
            Text = _autoCloseSettings.CloseOnRunTime
                ? $"Theo thời gian — runtime ≥ {_autoCloseSettings.RunHours}h → thay (logic hiện tại)"
                : "Theo thời gian — giữ logic hiện tại (TIME hiện đang tắt trong Tự động)",
            AutoSize = true,
            Checked = current.Mode == RunAllStrategyMode.Time,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 3, 0, 8)
        };

        var primeMode = new RadioButton
        {
            Text = "Giờ vàng / Giữ mục tiêu PRF MỚI",
            AutoSize = true,
            Checked = current.Mode == RunAllStrategyMode.PrimeFresh,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 3, 0, 0)
        };
        modePanel.Controls.Add(timeMode);
        modePanel.Controls.Add(primeMode);

        var primeBox = new GroupBox
        {
            Text = "Cấu hình giờ vàng",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 10,
            Padding = new Padding(4),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));

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
        var preserveFresh = new CheckBox
        {
            Text = "Ngoài giờ vàng: bảo lưu PRF MỚI; ưu tiên chạy CŨ → TRUNG BÌNH",
            Checked = current.PreserveFreshOffPeak,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F),
            Margin = new Padding(0, 8, 0, 4)
        };

        var ageLegend = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = Color.FromArgb(37, 77, 122),
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 7)
        };

        void UpdateAgeLegend()
        {
            ageLegend.Text =
                $"Phân loại:  MỚI < {freshHours.Value:0}h   |   TRUNG BÌNH: {freshHours.Value:0}h ≤ runtime < {oldHours.Value:0}h   |   CŨ ≥ {oldHours.Value:0}h";
        }

        freshHours.ValueChanged += (_, _) => UpdateAgeLegend();
        oldHours.ValueChanged += (_, _) => UpdateAgeLegend();
        UpdateAgeLegend();

        var row = 0;
        grid.Controls.Add(Field("Bắt đầu giờ vàng"), 0, row); grid.Controls.Add(startHour, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.Controls.Add(Field("Kết thúc giờ vàng"), 0, row); grid.Controls.Add(endHour, 1, row); grid.Controls.Add(Unit("giờ  (24 = 00:00)"), 2, row++);
        var freshTargetUnit = Unit("");
        grid.Controls.Add(Field("PRF MỚI ="), 0, row); grid.Controls.Add(freshTarget, 1, row); grid.Controls.Add(freshTargetUnit, 2, row++);
        grid.Controls.Add(Field("PRF MỚI: runtime <"), 0, row); grid.Controls.Add(freshHours, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.Controls.Add(Field("PRF CŨ: runtime ≥"), 0, row); grid.Controls.Add(oldHours, 1, row); grid.Controls.Add(Unit("giờ"), 2, row++);
        grid.SetColumnSpan(ageLegend, 3);
        grid.Controls.Add(ageLegend, 0, row++);
        grid.Controls.Add(Field("Thay PRF tiếp theo sau ≥"), 0, row); grid.Controls.Add(rotationMinutes, 1, row); grid.Controls.Add(Unit("phút  ·  luôn chỉ 1 PRF/lần"), 2, row++);
        grid.Controls.Add(Field("Chuẩn bị trước giờ vàng"), 0, row); grid.Controls.Add(prepareMinutes, 1, row); grid.Controls.Add(Unit("phút"), 2, row++);
        grid.SetColumnSpan(preserveFresh, 3);
        grid.Controls.Add(preserveFresh, 0, row++);

        var reuseNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.8F),
            Text = _autoCloseSettings.ReuseOnlyNoCreateProfile
                ? "Giờ vàng: giữ đúng mục tiêu MỚI khi có đủ TB/CŨ; slot còn lại lấy runtime thấp trước. Ngoài giờ: lấy runtime cao trước. Đang bật ‘CHỈ PRF CHỜ’ nên Tool không tạo PRF mới. Luôn chỉ 1 PRF/lần."
                : "Giờ vàng: đạt mục tiêu MỚI trước; slot còn lại ưu tiên TB/CŨ runtime thấp → chỉ dùng thêm MỚI khi thiếu TB/CŨ. Ngoài giờ vàng: ưu tiên runtime cao để tận dụng PRF cũ. Luôn chỉ 1 PRF/lần.",
            Margin = new Padding(0, 6, 0, 0)
        };
        grid.SetColumnSpan(reuseNote, 3);
        grid.Controls.Add(reuseNote, 0, row);

        primeBox.Controls.Add(grid);

        void UpdatePrimeEnabled()
        {
            foreach (Control control in grid.Controls)
                control.Enabled = primeMode.Checked;
        }
        timeMode.CheckedChanged += (_, _) => UpdatePrimeEnabled();
        primeMode.CheckedChanged += (_, _) => UpdatePrimeEnabled();
        UpdatePrimeEnabled();

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
        ModernDialog.StyleSecondaryButton(cancel);
        ModernDialog.StylePrimaryButton(start);
        footer.Controls.Add(cancel);
        footer.Controls.Add(start);

        void UpdateLaunchValidation()
        {
            var ensure = autoEnsureTarget.Checked;
            targetLabel.Enabled = ensure;
            targetSlots.Enabled = ensure;

            var effectiveTarget = ensure
                ? (int)targetSlots.Value
                : openCount;

            freshTargetUnit.Text =
                $"PRF mục tiêu  (≤ target: {Math.Max(0, effectiveTarget)} · chỉ vượt khi thiếu TB/CŨ)";

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

            if (primeMode.Checked && effectiveTarget > 0
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

            start.Enabled = valid;
        }

        autoEnsureTarget.CheckedChanged += (_, _) => UpdateLaunchValidation();
        openedOnly.CheckedChanged += (_, _) => UpdateLaunchValidation();
        targetSlots.ValueChanged += (_, _) => UpdateLaunchValidation();
        freshTarget.ValueChanged += (_, _) => UpdateLaunchValidation();
        freshHours.ValueChanged += (_, _) => UpdateLaunchValidation();
        oldHours.ValueChanged += (_, _) => UpdateLaunchValidation();
        primeMode.CheckedChanged += (_, _) => UpdateLaunchValidation();
        timeMode.CheckedChanged += (_, _) => UpdateLaunchValidation();
        UpdateLaunchValidation();

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(summary, 0, 1);
        root.Controls.Add(launchBox, 0, 2);
        root.Controls.Add(modePanel, 0, 3);
        root.Controls.Add(primeBox, 0, 4);
        root.Controls.Add(footer, 0, 5);
        form.Controls.Add(root);
        form.AcceptButton = start;
        form.CancelButton = cancel;
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);

        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        var selected = NormalizeRunStrategySettings(new RunAllStrategySettings
        {
            Version = 3,
            Mode = primeMode.Checked ? RunAllStrategyMode.PrimeFresh : RunAllStrategyMode.Time,
            PrimeModeArmed = primeMode.Checked,
            PrimeModeSuspended = false,
            AutoEnsureTarget = autoEnsureTarget.Checked,
            TargetSlots = (int)targetSlots.Value,
            PrimeStartHour = (int)startHour.Value,
            PrimeEndHour = (int)endHour.Value,
            FreshTarget = (int)freshTarget.Value,
            FreshHours = (int)freshHours.Value,
            OldHours = (int)oldHours.Value,
            RotationIntervalMinutes = (int)rotationMinutes.Value,
            PrepareMinutes = (int)prepareMinutes.Value,
            PreserveFreshOffPeak = preserveFresh.Checked
        });

        var target = selected.AutoEnsureTarget
            ? selected.TargetSlots
            : openCount;

        if (target <= 0)
        {
            ModernDialog.ShowMessage(
                this,
                "Chưa có PRF đang mở. Hãy chọn “Tự đảm bảo đủ số PRF” hoặc mở PRF trước.",
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

        // Prime Time luôn cần Tự bù để thay 1 slot. Chế độ tự bảo đảm target cũng
        // cần cùng engine này để giữ target về sau. Không thay đổi lựa chọn
        // CHỈ PRF CHỜ của user.
        if ((selected.Mode == RunAllStrategyMode.PrimeFresh
             || selected.AutoEnsureTarget)
            && !_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            _autoCloseSettings.OpenReplacementAfterAutoClose = true;
            SaveAutoCloseSettings();
        }

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
                    if (!_autoCloseSettings.ReuseOnlyNoCreateProfile
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
                if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
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

            if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
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

        if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
            return false;

        return await TryCreateRunStrategyReplacementAsync(
            "RUN_ALL_START",
            $"INITIAL_SLOT_{slotNumber}",
            token);
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

        ArmAutoReplacementSession("run_strategy_prime_start");

        _log.Info(
            $"[RUN_STRATEGY_START] mode=PRIME armed={settings.PrimeModeArmed} target={targetSlots} prime={settings.PrimeStartHour:00}:00-{settings.PrimeEndHour:00}:00 " +
            $"freshTarget={Math.Min(settings.FreshTarget, targetSlots)} freshUnder={settings.FreshHours}h oldFrom={settings.OldHours}h " +
            $"interval={settings.RotationIntervalMinutes}m prepare={settings.PrepareMinutes}m preserveOffPeak={settings.PreserveFreshOffPeak}");
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
                            ? DateTime.UtcNow.AddMinutes(settings.RotationIntervalMinutes)
                            : DateTime.UtcNow.AddMinutes(2);
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

                var canCreate = !_autoCloseSettings.ReuseOnlyNoCreateProfile;
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
            AllowCreateFallback: !_autoCloseSettings.ReuseOnlyNoCreateProfile);
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
        bool allowProtectedNightReserve = false)
    {
        var freshLimit = TimeSpan.FromHours(settings.FreshHours).TotalSeconds;
        var oldLimit = TimeSpan.FromHours(settings.OldHours).TotalSeconds;
        List<RunStrategyReusableCandidate> snapshot;

        lock (_reusableProfileQueueLock)
        {
            snapshot = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(entry =>
                    !entry.NameSyncPending
                    && !string.IsNullOrWhiteSpace(entry.ProfileName)
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
        if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
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
                    return BuildAutoProfileQueue(
                        requestedNew: 1,
                        requestedStartName: startName,
                        resumeIncomplete: false,
                        retryPaused: false);
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
        var filled = false;
        try
        {
            token.ThrowIfCancellationRequested();

            _log.Info(
                $"[RUN_STRATEGY_ROTATE_BEGIN] phase={plan.Phase} victim={victimName} runtime={TimeSpan.FromSeconds(GetRunStrategyTotalSeconds(plan.Victim)):c} " +
                $"lanes={string.Join(",", plan.PreferredLanes)} oneAtATime=true");

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

            foreach (var candidate in GetRunStrategyReusableCandidates(
                         settings,
                         lane,
                         excludeProfileName: outgoingProfileName,
                         preferLowerRuntime: preferLowerRuntime,
                         allowProtectedNightReserve: allowProtectedNightReserveBorrow))
            {
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
                         preferLowerRuntime: false))
            {
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
            && !_autoCloseSettings.ReuseOnlyNoCreateProfile
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
            && !_autoCloseSettings.ReuseOnlyNoCreateProfile)
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

        // Tận dụng toàn bộ one-attempt / Name Guard / 10 phút stabilization / cleanup
        // của queue reuse cũ. Để buộc helper chỉ lấy đúng lane đã chọn, đánh dấu mọi
        // profile khác là attempted trong request tạm thời này.
        var attempted = _contexts.Keys
            .Where(name =>
                !name.Equals(candidateProfileName, StringComparison.OrdinalIgnoreCase))
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
        if (_autoCloseSettings.ReuseOnlyNoCreateProfile)
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
