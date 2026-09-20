using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoCloseSettingsDocument
    {
        // V6: TIME mặc định 6 giờ. Bản cũ từng có default time=false / 3-5h,
        // khiến UI và runtime có thể lệch sau khi copy/cập nhật dist.
        public int Version { get; set; } = 7;
        public bool CloseOnBan { get; set; } = true;
        public bool CloseOnRunTime { get; set; } = true;
        public int RunHours { get; set; } = 6;
        public bool CloseOnNotRunning10Minutes { get; set; } = true;
        public bool OpenReplacementAfterAutoClose { get; set; } = true;

        // V7: chế độ dọn kho - Tự bù chỉ dùng profile đã có trong Chờ dùng lại,
        // tuyệt đối không tiêu account mới để tạo profile.
        public bool ReuseOnlyNoCreateProfile { get; set; }

        // TIME_xH: tùy chọn có tự xóa sau khi Excel đã ghi/xác minh hay không.
        // BAN luôn tự xóa sau khi note=ban đã được xác minh (nếu CloseOnBan bật).
        // Giữ nguyên tên property để tương thích manager_auto_close.json cũ.
        public bool DeleteProfileAfterBanOrLifetime { get; set; }
    }

    const int AutoCloseNotRunningMinutes = 10;
    static readonly TimeSpan AutoCloseStatusStaleAfter = TimeSpan.FromSeconds(30);

    readonly HashSet<string> _autoCloseInProgressProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoCloseBanHandledProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoCloseExpectedRunningProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoCloseNotRunningSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan AutoCloseCleanupRetryDelay = TimeSpan.FromSeconds(20);
    readonly Dictionary<string, DateTime> _autoCloseCleanupRetryUtc = new(StringComparer.OrdinalIgnoreCase);

    sealed record AutoCloseReasonDecision(
        string Reason,
        string Detail,
        int Priority,
        DateTime UpdatedUtc);

    readonly object _autoCloseReasonLock = new();
    readonly Dictionary<string, AutoCloseReasonDecision> _autoCloseReasonByProfile =
        new(StringComparer.OrdinalIgnoreCase);
    bool _autoCloseFeatureInitialized;
    bool _autoCloseRuntimeCheckBusy;
    readonly HashSet<string> _autoCloseVerifiedCleanProfiles = new(StringComparer.OrdinalIgnoreCase);
    AutoCloseSettingsDocument _autoCloseSettings = new();
    Button? _autoCloseToolbarButton;
    string _autoCloseToolbarDisplayText = "Tự động";

    string AutoCloseSettingsPath => Path.Combine(_baseDir, "manager_auto_close.json");

    void InitializeAutoCloseFeature()
    {
        if (_autoCloseFeatureInitialized) return;
        _autoCloseFeatureInitialized = true;

        _autoCloseSettings = LoadAutoCloseSettings();
        InjectAutoCloseToolbarButton();
        InitializeAutoReplacementFeature();
        InitializeManualRuntimeIntentGuard();

        // V13.8.6 style: dùng trực tiếp watchdog 1 giây. Đủ tổng giờ thì đóng ngay,
        // không qua hàng đợi/scheduler TIME 30 phút.
        _refreshTimer.Tick += async (_, _) => await CheckAutoCloseRuntimeAsync();

        _log.Info(
            $"[AUTO_CLOSE_INIT] ban={_autoCloseSettings.CloseOnBan} time={_autoCloseSettings.CloseOnRunTime} hours={_autoCloseSettings.RunHours} stuck10m={_autoCloseSettings.CloseOnNotRunning10Minutes} replace={_autoCloseSettings.OpenReplacementAfterAutoClose} reuseOnly={_autoCloseSettings.ReuseOnlyNoCreateProfile} deleteRetired={_autoCloseSettings.DeleteProfileAfterBanOrLifetime} settingsPath={AutoCloseSettingsPath}");
    }

    AutoCloseSettingsDocument LoadAutoCloseSettings()
    {
        try
        {
            if (!File.Exists(AutoCloseSettingsPath))
            {
                var defaults = NormalizeAutoCloseSettings(new AutoCloseSettingsDocument());
                PersistAutoCloseSettingsMigration(defaults, oldVersion: 0, reason: "missing_file");
                return defaults;
            }

            var loaded = JsonSerializer.Deserialize<AutoCloseSettingsDocument>(
                File.ReadAllText(AutoCloseSettingsPath));

            if (loaded is null)
                return NormalizeAutoCloseSettings(new AutoCloseSettingsDocument());

            var oldVersion = loaded.Version;

            // Migration V6: bản cũ có thể lưu time=false và RunHours=5 từ default
            // lịch sử, trong khi người dùng đang dùng preset 6h. Chỉ migrate MỘT LẦN.
            // Sau khi đã lên V6, người dùng vẫn có thể chủ động tắt TIME trong UI
            // và giá trị false sẽ được tôn trọng ở các lần mở sau.
            if (oldVersion < 6)
            {
                loaded.CloseOnRunTime = true;
                loaded.RunHours = 6;
            }

            // V7 chỉ bổ sung một cờ mới, mặc định false để không thay đổi hành vi
            // của máy khách sau khi nâng phiên bản.

            var normalized = NormalizeAutoCloseSettings(loaded);

            if (oldVersion < 7)
            {
                PersistAutoCloseSettingsMigration(
                    normalized,
                    oldVersion,
                    reason: "legacy_time_defaults_to_6h");
            }

            return normalized;
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_CLOSE_SETTINGS_READ] path={AutoCloseSettingsPath} error={ex.Message}");
            return NormalizeAutoCloseSettings(new AutoCloseSettingsDocument());
        }
    }

    static AutoCloseSettingsDocument NormalizeAutoCloseSettings(AutoCloseSettingsDocument settings)
    {
        settings.Version = 7;
        settings.RunHours = Math.Clamp(settings.RunHours, 3, 24);
        return settings;
    }

    void PersistAutoCloseSettingsMigration(
        AutoCloseSettingsDocument settings,
        int oldVersion,
        string reason)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true });

            var temp = AutoCloseSettingsPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, AutoCloseSettingsPath, overwrite: true);

            _log.Info(
                $"[AUTO_CLOSE_SETTINGS_MIGRATE_V7] oldVersion={oldVersion} time={settings.CloseOnRunTime} hours={settings.RunHours} reason={reason} path={AutoCloseSettingsPath}");
        }
        catch (Exception ex)
        {
            // Không chặn Manager khởi động chỉ vì không ghi được migration; runtime
            // hiện tại vẫn dùng settings đã normalize.
            _log.Warn(
                $"[AUTO_CLOSE_SETTINGS_MIGRATE_V7_WARN] oldVersion={oldVersion} reason={reason} path={AutoCloseSettingsPath} error={ex.Message}");
        }
    }

    void SaveAutoCloseSettings()
    {
        _autoCloseSettings = NormalizeAutoCloseSettings(_autoCloseSettings);

        var json = JsonSerializer.Serialize(
            _autoCloseSettings,
            new JsonSerializerOptions { WriteIndented = true });

        var temp = AutoCloseSettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, AutoCloseSettingsPath, overwrite: true);

        UpdateAutoCloseToolbarButtonText();
        NotifyAutoReplacementSettingsChanged();

        _log.Info(
            $"[AUTO_CLOSE_SETTINGS_SAVE] ban={_autoCloseSettings.CloseOnBan} time={_autoCloseSettings.CloseOnRunTime} hours={_autoCloseSettings.RunHours} stuck10m={_autoCloseSettings.CloseOnNotRunning10Minutes} replace={_autoCloseSettings.OpenReplacementAfterAutoClose} reuseOnly={_autoCloseSettings.ReuseOnlyNoCreateProfile} deleteRetired={_autoCloseSettings.DeleteProfileAfterBanOrLifetime}");
    }

    void InjectAutoCloseToolbarButton()
    {
        if (_autoCloseToolbarButton is not null && !_autoCloseToolbarButton.IsDisposed)
            return;

        var toolbar = EnumerateAutoCloseControls(this)
            .OfType<FlowLayoutPanel>()
            .FirstOrDefault(panel => panel.Controls
                .OfType<Button>()
                .Any(button => NormalizeToolbarButtonText(button.Text).Equals("Stop All", StringComparison.OrdinalIgnoreCase)));

        if (toolbar is null)
        {
            _log.Warn("[AUTO_CLOSE_UI] Không tìm thấy toolbar hàng 2 để thêm nút Tự đóng.");
            return;
        }

        _autoCloseToolbarButton = Button(
            "⚙ Cài đặt",
            (_, _) => ShowAutoCloseDialog(),
            UiButtonKind.Neutral);

        toolbar.Controls.Add(_autoCloseToolbarButton);

        if (_availability.Parent == toolbar)
        {
            var availabilityIndex = toolbar.Controls.GetChildIndex(_availability);
            toolbar.Controls.SetChildIndex(
                _autoCloseToolbarButton,
                Math.Max(0, availabilityIndex));
        }

        UpdateAutoCloseToolbarButtonText();
    }

    static IEnumerable<Control> EnumerateAutoCloseControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in EnumerateAutoCloseControls(child))
                yield return nested;
        }
    }

    int MeasureAutoCloseToolbarTextWidth(Button button, string text)
    {
        using var bold = new Font(button.Font, FontStyle.Bold);
        const string prefix = "Tự động:";
        var suffix = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..]
            : text;
        var prefixWidth = TextRenderer.MeasureText(prefix, bold, Size.Empty, TextFormatFlags.NoPadding).Width;
        var suffixWidth = TextRenderer.MeasureText(suffix, button.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        return prefixWidth + suffixWidth;
    }

    void PaintAutoCloseToolbarButtonText(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button || button.IsDisposed) return;
        var text = _autoCloseToolbarDisplayText ?? "";
        if (text.Length == 0) return;

        const string prefix = "Tự động:";
        var hasPrefix = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var leftText = hasPrefix ? prefix : text;
        var rightText = hasPrefix ? text[prefix.Length..] : "";

        using var bold = new Font(button.Font, FontStyle.Bold);
        var leftSize = TextRenderer.MeasureText(leftText, bold, Size.Empty, TextFormatFlags.NoPadding);
        var rightSize = TextRenderer.MeasureText(rightText, button.Font, Size.Empty, TextFormatFlags.NoPadding);
        var totalWidth = leftSize.Width + rightSize.Width;
        var x = Math.Max(4, (button.ClientSize.Width - totalWidth) / 2);
        var y = Math.Max(0, (button.ClientSize.Height - Math.Max(leftSize.Height, rightSize.Height)) / 2);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        TextRenderer.DrawText(e.Graphics, leftText, bold, new Point(x, y), button.ForeColor, flags);
        if (rightText.Length > 0)
            TextRenderer.DrawText(e.Graphics, rightText, button.Font, new Point(x + leftSize.Width, y), button.ForeColor, flags);
    }

    string GetAutoReplacementUiStatusText()
    {
        var prefix = _autoCloseSettings.ReuseOnlyNoCreateProfile
            ? "Bù PRF"
            : "Bù";

        if (!_autoCloseSettings.OpenReplacementAfterAutoClose)
            return $"{prefix}: TẮT";

        int target;
        bool targetInitialized;
        lock (_autoReplacementFixedSlotLock)
        {
            target = _autoReplacementTargetSlots;
            targetInitialized = _autoReplacementTargetInitialized;
        }

        // CHỜ BẮT ĐẦU chỉ đúng khi chưa có target thật. Trước đây return quá sớm
        // tại đây nên có thể che mất trạng thái 4/5 đang reconcile/xếp hàng.
        if (!targetInitialized || target <= 0)
        {
            return _autoReplacementSessionArmed
                ? $"{prefix}: SẴN SÀNG"
                : $"{prefix}: CHỜ BẮT ĐẦU";
        }

        var occupied = CountAutoReplacementFulfilledSlots();

        AutoReplacementRequest? firstPending = null;
        int pending;
        DateTime? earliestRetryUtc = null;
        lock (_autoReplacementQueueLock)
        {
            pending = _autoReplacementQueue.Count;
            if (pending > 0)
            {
                firstPending = _autoReplacementQueue
                    .OrderBy(x => x.NextAttemptUtc)
                    .ThenBy(x => x.QueuedUtc)
                    .FirstOrDefault();
                earliestRetryUtc = _autoReplacementQueue.Min(x => x.NextAttemptUtc);
            }
        }

        if (occupied >= target)
            return $"{prefix}: ĐỦ {occupied}/{target}";

        // Có target nhưng session đang bị giữ: hiển thị đúng trạng thái thay vì
        // "CHỜ BẮT ĐẦU" chung chung. Start thủ công/Auto Run bình thường sẽ arm lại.
        if (!_autoReplacementSessionArmed)
            return $"{prefix}: CHỜ KÍCH HOẠT · {occupied}/{target}";

        var nowUtc = DateTime.UtcNow;

        if (_autoReplacementStartAllInProgress)
            return $"{prefix}: AUTO RUN ĐANG BỔ SUNG · {occupied}/{target}";

        if (_autoReplacementQueueRunning)
        {
            var live = GetAutoReplacementUiPhaseSnapshot();
            if (!string.IsNullOrWhiteSpace(live.Phase))
            {
                var detail = FormatAutoReplacementUiPhaseDetail(live.Detail);
                var detailText = detail.Length > 0 ? $" {detail}" : "";
                var ageText = live.Age >= TimeSpan.FromSeconds(2)
                    ? $" · {FormatAutoReplacementUiWait(live.Age)}"
                    : "";

                return $"{prefix}: {live.Phase}{detailText}{ageText} · {occupied}/{target}";
            }
        }

        // Nếu đang cooldown riêng cho NHÁNH TẠO MỚI, ưu tiên nói rõ liệu đã có
        // PRF chờ sẵn hay chưa. PRF chờ có thể đánh thức request ngay, không phải
        // chờ hết countdown tạo mới.
        if (firstPending is not null
            && firstPending.CreateNotBeforeUtc.HasValue
            && firstPending.CreateNotBeforeUtc.Value > nowUtc)
        {
            if (TryFindUntestedEligibleReusableProfile(
                    firstPending,
                    out var reusableProfile,
                    out var reusableLane,
                    out _))
            {
                if (reusableLane.Equals("REUSE_READY", StringComparison.OrdinalIgnoreCase))
                    return $"{prefix}: PRF CHỜ SẴN {reusableProfile} · {occupied}/{target}";

                if (reusableLane.Contains("STATE_PENDING", StringComparison.OrdinalIgnoreCase))
                    return $"{prefix}: CHỜ PRF {reusableProfile} RẢNH · {occupied}/{target}";

                if (reusableLane.Equals("NAME_SYNC_PENDING", StringComparison.OrdinalIgnoreCase))
                    return $"{prefix}: CHỜ PRF {reusableProfile} ĐỒNG BỘ · {occupied}/{target}";
            }

            var createWait = firstPending.CreateNotBeforeUtc.Value - nowUtc;
            return $"{prefix}: CHỜ TẠO PRF MỚI {FormatAutoReplacementUiWait(createWait)} · {occupied}/{target}";
        }

        if (pending > 0
            && earliestRetryUtc.HasValue
            && earliestRetryUtc.Value > nowUtc.AddSeconds(1))
        {
            var wait = earliestRetryUtc.Value - nowUtc;
            return $"{prefix}: CHỜ KIỂM TRA {FormatAutoReplacementUiWait(wait)} · {occupied}/{target}";
        }

        if (_autoReplacementQueueRunning)
            return $"{prefix}: ĐANG XỬ LÝ · {occupied}/{target}";

        if (pending > 0)
            return $"{prefix}: XẾP HÀNG {pending} · {occupied}/{target}";

        // Chưa có request không đồng nghĩa Tool bị treo. Capacity reconcile cố ý
        // xác nhận thiếu suất 2 pass rồi mới xếp request để tránh mở bù thừa.
        if (_autoReplacementCapacityReconcileRunning)
            return $"{prefix}: XÁC NHẬN THIẾU · {occupied}/{target}";

        if (_autoReplacementNextCapacityReconcileUtc > nowUtc.AddSeconds(1))
        {
            var wait = _autoReplacementNextCapacityReconcileUtc - nowUtc;
            return $"{prefix}: CHỜ KIỂM TRA {FormatAutoReplacementUiWait(wait)} · {occupied}/{target}";
        }

        return $"{prefix}: SẮP KIỂM TRA · {occupied}/{target}";
    }

    static string FormatAutoReplacementUiPhaseDetail(string detail)
    {
        detail = (detail ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        while (detail.Contains("  ", StringComparison.Ordinal))
            detail = detail.Replace("  ", " ", StringComparison.Ordinal);

        const int maxLength = 24;
        return detail.Length <= maxLength
            ? detail
            : detail[..(maxLength - 1)] + "…";
    }

    static string FormatAutoReplacementUiWait(TimeSpan wait)
    {
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;

        var totalSeconds = Math.Max(0, (int)Math.Ceiling(wait.TotalSeconds));
        if (totalSeconds < 60)
            return $"{totalSeconds}s";

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return seconds == 0
            ? $"{minutes}m"
            : $"{minutes}m{seconds:00}s";
    }

    void UpdateAutoCloseToolbarButtonText()
    {
        if (_autoCloseToolbarButton is null || _autoCloseToolbarButton.IsDisposed)
            return;

        // V14.2 UI: nút Tự động cũ trở thành điểm vào cấu hình chung.
        // Trạng thái engine vẫn được ghi log/hiển thị trong các màn hình liên quan;
        // toolbar chỉ giữ một nhãn ổn định, dễ hiểu.
        _autoCloseToolbarDisplayText = "⚙ Cài đặt";
        _autoCloseToolbarButton.Text = "⚙ Cài đặt";
        _autoCloseToolbarButton.AutoSize = false;
        _autoCloseToolbarButton.Width = 124;
        _autoCloseToolbarButton.BackColor = UiTheme.Card;
        _autoCloseToolbarButton.ForeColor = Color.FromArgb(42, 57, 76);
        _autoCloseToolbarButton.Invalidate();
    }

    void ShowAutoCloseDialog()
    {
        // Cửa sổ này là trung tâm cấu hình chung cho Auto Run + AutoClose/Tự bù.
        // InitializeRunStrategyFeature an toàn khi gọi lặp và bảo đảm settings Dàn PRF
        // đã được load trước khi dựng UI.
        InitializeRunStrategyFeature();

        var currentRun = NormalizeRunStrategySettings(new RunAllStrategySettings
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

        var currentVmMode = LoadManagerVmOptimizationModeForUi();

        using var form = new Form
        {
            Text = $"Cài đặt tự động — {AppVersionInfo.Display}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(820, 700),
            MinimumSize = new Size(720, 600),
            BackColor = UiTheme.Canvas,
            Font = new Font("Segoe UI", 9F),
            AutoScaleMode = AutoScaleMode.Dpi
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = UiTheme.Canvas
        };
        var title = new Label
        {
            Text = "CÀI ĐẶT TỰ ĐỘNG",
            AutoSize = true,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Location = new Point(20, 17)
        };
        header.Controls.Add(title);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = UiTheme.Canvas
        };
        var save = new Button
        {
            Text = "Lưu",
            Width = 110,
            Height = 34
        };
        var cancel = new Button
        {
            Text = "Hủy",
            Width = 110,
            Height = 34,
            DialogResult = DialogResult.Cancel
        };
        void LayoutFooterButtons()
        {
            cancel.Left = Math.Max(12, footer.ClientSize.Width - cancel.Width - 12);
            save.Left = Math.Max(12, cancel.Left - save.Width - 10);
            save.Top = cancel.Top = 10;
        }
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        footer.Resize += (_, _) => LayoutFooterButtons();

        // Chỉ cuộn dọc. Content luôn được ép vừa đúng chiều rộng viewport,
        // không dùng min-width lớn hơn viewport để tránh scrollbar ngang ở DPI cao.
        var viewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.Canvas
        };
        var content = new Panel
        {
            Location = new Point(16, 8),
            Size = new Size(760, 850),
            BackColor = UiTheme.Canvas
        };

        // ------------------------------------------------------------
        // 1) SỐ LƯỢNG & NGUỒN PRF
        // ------------------------------------------------------------
        var sourceGroup = new GroupBox
        {
            Text = "SỐ LƯỢNG & NGUỒN PRF",
            Location = new Point(0, 0),
            Size = new Size(760, 152),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        var autoEnsureTarget = new CheckBox
        {
            Text = "Duy trì số lượng PRF",
            Checked = currentRun.AutoEnsureTarget,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.2F, FontStyle.Bold),
            Location = new Point(18, 30)
        };
        var targetLabel = new Label
        {
            Text = "Số PRF muốn duy trì:",
            AutoSize = true,
            Location = new Point(42, 70)
        };
        var targetSlots = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 50,
            Value = Math.Clamp(currentRun.TargetSlots, 1, 50),
            Width = 82,
            Location = new Point(190, 66)
        };
        var targetHint = new Label
        {
            AutoSize = false,
            ForeColor = Color.DimGray,
            Location = new Point(290, 68),
            Size = new Size(430, 30)
        };
        var reuseOnly = new CheckBox
        {
            Text = "Chỉ dùng PRF chờ — không tạo PRF mới",
            Checked = _autoCloseSettings.ReuseOnlyNoCreateProfile,
            AutoSize = true,
            Location = new Point(18, 110),
            ForeColor = Color.FromArgb(37, 77, 122)
        };
        void UpdateTargetUi()
        {
            var enabled = autoEnsureTarget.Checked;
            targetLabel.Enabled = enabled;
            targetSlots.Enabled = enabled;
            targetHint.Text = enabled
                ? "Bật: Tool duy trì đúng số lượng này."
                : "Tắt: chạy theo số PRF đang mở lúc bấm Bắt đầu.";
        }
        UpdateTargetUi();
        sourceGroup.Controls.Add(autoEnsureTarget);
        sourceGroup.Controls.Add(targetLabel);
        sourceGroup.Controls.Add(targetSlots);
        sourceGroup.Controls.Add(targetHint);
        sourceGroup.Controls.Add(reuseOnly);

        // ------------------------------------------------------------
        // 2) TỐI ƯU — tái sử dụng nguyên engine VM Safe / VM Max của Worker
        // ------------------------------------------------------------
        var optimizationGroup = new GroupBox
        {
            Text = "TỐI ƯU",
            Location = new Point(0, 164),
            Size = new Size(760, 150),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        var optimizationLabel = new Label
        {
            Text = "Chế độ tối ưu:",
            AutoSize = true,
            Location = new Point(18, 34)
        };
        var optimizationMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 145,
            Location = new Point(125, 29)
        };
        optimizationMode.Items.AddRange(["Bình thường", "VM Safe", "VM Max"]);
        optimizationMode.SelectedIndex = NormalizeManagerVmOptimizationMode(currentVmMode) switch
        {
            "Normal" => 0,
            "VmSafe" => 1,
            _ => 2
        };
        string SelectedVmMode() => optimizationMode.SelectedIndex switch
        {
            0 => "Normal",
            1 => "VmSafe",
            _ => "VmMax"
        };
        var applyOptimization = new Button
        {
            Text = "Áp dụng ngay cho tất cả PRF",
            Width = 220,
            Height = 32,
            Location = new Point(288, 27)
        };
        var optimizationStatus = new Label
        {
            AutoSize = false,
            Location = new Point(18, 72),
            Size = new Size(710, 24),
            ForeColor = Color.DarkGreen
        };
        var optimizationHint = new Label
        {
            Text = "Dùng nguyên cơ chế tối ưu sẵn có của Worker. PRF đang mở áp dụng ngay; PRF chưa mở được lưu cấu hình và tự dùng ở lần mở sau.",
            AutoSize = false,
            ForeColor = Color.DimGray,
            Location = new Point(18, 101),
            Size = new Size(710, 38)
        };
        applyOptimization.Click += async (_, _) =>
        {
            if (!applyOptimization.Enabled) return;
            applyOptimization.Enabled = false;
            optimizationStatus.ForeColor = Color.DarkOrange;
            optimizationStatus.Text = "Đang áp dụng cho toàn bộ PRF...";
            try
            {
                var result = await ApplyManagerVmOptimizationToAllProfilesAsync(
                    SelectedVmMode(),
                    "automation_settings_apply_now");
                optimizationStatus.ForeColor = result.LiveWorkersDeferred > 0
                    ? Color.DarkOrange
                    : Color.DarkGreen;
                optimizationStatus.Text =
                    $"✓ {ManagerVmOptimizationDisplayName(result.Mode)} · cấu hình {result.ConfiguredProfiles}/{result.TotalProfiles} PRF · đang mở {result.LiveWorkersApplied} áp dụng ngay"
                    + (result.LiveWorkersDeferred > 0 ? $" · {result.LiveWorkersDeferred} chờ lần mở sau" : "");
            }
            catch (Exception ex)
            {
                optimizationStatus.ForeColor = Color.Firebrick;
                optimizationStatus.Text = "Không áp dụng được: " + ex.Message;
            }
            finally
            {
                applyOptimization.Enabled = true;
            }
        };
        optimizationGroup.Controls.Add(optimizationLabel);
        optimizationGroup.Controls.Add(optimizationMode);
        optimizationGroup.Controls.Add(applyOptimization);
        optimizationGroup.Controls.Add(optimizationStatus);
        optimizationGroup.Controls.Add(optimizationHint);

        // ------------------------------------------------------------
        // 3) TỰ ĐỘNG ĐÓNG & BÙ
        // ------------------------------------------------------------
        var autoGroup = new GroupBox
        {
            Text = "TỰ ĐỘNG ĐÓNG, BÙ",
            Location = new Point(0, 326),
            Size = new Size(760, 242),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        CheckBox AutoCheck(string textValue, bool isChecked, int y, int height = 26)
            => new()
            {
                Text = textValue,
                Checked = isChecked,
                AutoSize = false,
                Location = new Point(18, y),
                Size = new Size(700, height)
            };
        var closeOnBan = AutoCheck(
            "Tự đóng khi tài khoản bị BAN",
            _autoCloseSettings.CloseOnBan,
            28);
        var closeOnTime = AutoCheck(
            "Tự đóng khi Tổng thời gian Automation chạy đủ",
            _autoCloseSettings.CloseOnRunTime,
            64);
        var hours = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 105,
            Location = new Point(620, 62)
        };
        var runHourOptions = new[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 16, 20, 24 };
        foreach (var value in runHourOptions)
            hours.Items.Add($"{value} giờ");
        var selectedRunHours = Math.Clamp(_autoCloseSettings.RunHours, 3, 24);
        var selectedHourIndex = Array.IndexOf(runHourOptions, selectedRunHours);
        if (selectedHourIndex < 0)
        {
            selectedHourIndex = Array.FindIndex(runHourOptions, value => value >= selectedRunHours);
            if (selectedHourIndex < 0)
                selectedHourIndex = runHourOptions.Length - 1;
        }
        hours.SelectedIndex = selectedHourIndex;
        hours.Enabled = closeOnTime.Checked;
        closeOnTime.CheckedChanged += (_, _) => hours.Enabled = closeOnTime.Checked;

        var closeOnStuck = AutoCheck(
            "Tự đóng nếu 10 phút lỗi / không RUNNING / không có tiến triển",
            _autoCloseSettings.CloseOnNotRunning10Minutes,
            100);
        var openReplacement = AutoCheck(
            "Tự bù sau khi đóng: ưu tiên PRF có sẵn; hết nguồn mới tạo nếu được phép",
            _autoCloseSettings.OpenReplacementAfterAutoClose,
            136,
            36);
        var deleteRetiredProfile = AutoCheck(
            "Tự xóa profile hết vòng đời TIME_xH (BAN luôn xóa sau khi đã ghi note=ban)",
            _autoCloseSettings.DeleteProfileAfterBanOrLifetime,
            182,
            36);
        autoGroup.Controls.Add(closeOnBan);
        autoGroup.Controls.Add(closeOnTime);
        autoGroup.Controls.Add(hours);
        autoGroup.Controls.Add(closeOnStuck);
        autoGroup.Controls.Add(openReplacement);
        autoGroup.Controls.Add(deleteRetiredProfile);

        // ------------------------------------------------------------
        // 4) GIỚI HẠN TẠO PRF — 2 hàng x 2 cột để không bị cắt ở DPI cao
        // ------------------------------------------------------------
        var createLimitGroup = new GroupBox
        {
            Text = "GIỚI HẠN TẠO PRF",
            Location = new Point(0, 580),
            Size = new Size(760, 176),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        var createLimitEnabled = new CheckBox
        {
            Text = "Bật giới hạn tạo PRF mới",
            Checked = currentRun.CreateLimitEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.1F, FontStyle.Bold),
            Location = new Point(18, 28)
        };
        NumericUpDown LimitNum(int value, int min, int max)
            => new()
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                Width = 70
            };
        Label LimitLabel(string textValue)
            => new()
            {
                Text = textValue,
                AutoSize = true
            };
        var slotLabel = LimitLabel("Mỗi slot");
        var perSlot = LimitNum(currentRun.CreateLimitPerSlot, 1, 50);
        var hourLabel = LimitLabel("Trong 1 giờ");
        var perHour = LimitNum(currentRun.CreateLimitPerHour, 1, 200);
        var sessionLabel = LimitLabel("Trong 1 phiên");
        var perSession = LimitNum(currentRun.CreateLimitPerSession, 1, 500);
        var retryLabel = LimitLabel("Retry PRF chờ (phút)");
        var retryMinutes = LimitNum(currentRun.CreateLimitReuseRetryMinutes, 1, 120);
        void UpdateCreateLimitUi()
        {
            var enabled = createLimitEnabled.Checked;
            perSlot.Enabled = perHour.Enabled = perSession.Enabled = retryMinutes.Enabled = enabled;
            slotLabel.Enabled = hourLabel.Enabled = sessionLabel.Enabled = retryLabel.Enabled = enabled;
        }
        createLimitEnabled.CheckedChanged += (_, _) => UpdateCreateLimitUi();
        UpdateCreateLimitUi();
        createLimitGroup.Controls.Add(createLimitEnabled);
        createLimitGroup.Controls.Add(slotLabel);
        createLimitGroup.Controls.Add(perSlot);
        createLimitGroup.Controls.Add(hourLabel);
        createLimitGroup.Controls.Add(perHour);
        createLimitGroup.Controls.Add(sessionLabel);
        createLimitGroup.Controls.Add(perSession);
        createLimitGroup.Controls.Add(retryLabel);
        createLimitGroup.Controls.Add(retryMinutes);

        // ------------------------------------------------------------
        // 5) KHUNG GIỜ KHÔNG TẠO PRF MỚI
        // ------------------------------------------------------------
        var noCreateGroup = new GroupBox
        {
            Text = "KHUNG GIỜ KHÔNG TẠO PRF MỚI",
            Location = new Point(0, 768),
            Size = new Size(760, 144),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        var noCreateEnabled = new CheckBox
        {
            Text = "Không tạo PRF mới trong khung giờ",
            Checked = currentRun.NoCreateScheduleEnabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.1F, FontStyle.Bold),
            Location = new Point(18, 28)
        };
        DateTime MinuteOfDayToPickerValue(int minute)
        {
            minute = Math.Clamp(minute, 0, (24 * 60) - 1);
            return DateTime.Today.AddMinutes(minute);
        }
        DateTimePicker TimePicker(int minute) => new()
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 86,
            Value = MinuteOfDayToPickerValue(minute)
        };
        var fromLabel = new Label { Text = "Từ", AutoSize = true, Location = new Point(42, 69) };
        var noCreateStart = TimePicker(currentRun.NoCreateStartMinute);
        noCreateStart.Location = new Point(80, 64);
        var toLabel = new Label { Text = "đến", AutoSize = true, Location = new Point(188, 69) };
        var noCreateEnd = TimePicker(currentRun.NoCreateEndMinute);
        noCreateEnd.Location = new Point(232, 64);
        var scheduleHint = new Label
        {
            Text = "Trong giờ cấm vẫn dùng PRF chờ; chỉ CREATE tự động bị chặn. Hỗ trợ khung qua đêm.",
            AutoSize = false,
            ForeColor = Color.DimGray,
            Location = new Point(42, 104),
            Size = new Size(680, 28)
        };
        void UpdateNoCreateUi()
        {
            var enabled = noCreateEnabled.Checked;
            noCreateStart.Enabled = noCreateEnd.Enabled = enabled;
            fromLabel.Enabled = toLabel.Enabled = enabled;
        }
        noCreateEnabled.CheckedChanged += (_, _) => UpdateNoCreateUi();
        UpdateNoCreateUi();
        noCreateGroup.Controls.Add(noCreateEnabled);
        noCreateGroup.Controls.Add(fromLabel);
        noCreateGroup.Controls.Add(noCreateStart);
        noCreateGroup.Controls.Add(toLabel);
        noCreateGroup.Controls.Add(noCreateEnd);
        noCreateGroup.Controls.Add(scheduleHint);

        // ------------------------------------------------------------
        // 6) NHẬT KÝ
        // ------------------------------------------------------------
        var logGroup = new GroupBox
        {
            Text = "NHẬT KÝ TỰ ĐỘNG",
            Location = new Point(0, 924),
            Size = new Size(760, 80),
            Padding = new Padding(12),
            ForeColor = Color.FromArgb(45, 67, 94)
        };
        var logHint = new Label
        {
            AutoSize = false,
            Text = "Ghi riêng giờ đóng/mở profile, lý do, tài khoản bù và kết quả.",
            ForeColor = Color.FromArgb(70, 82, 96),
            Location = new Point(16, 33),
            Size = new Size(560, 26)
        };
        var openLog = new Button
        {
            Text = "Xem nhật ký",
            Width = 125,
            Height = 30,
            Location = new Point(610, 25)
        };
        openLog.Click += (_, _) => ShowAutoActivityLogDialog(form);
        logGroup.Controls.Add(logHint);
        logGroup.Controls.Add(openLog);

        content.Controls.Add(sourceGroup);
        content.Controls.Add(optimizationGroup);
        content.Controls.Add(autoGroup);
        content.Controls.Add(createLimitGroup);
        content.Controls.Add(noCreateGroup);
        content.Controls.Add(logGroup);
        viewport.Controls.Add(content);

        void LayoutScrollableContent()
        {
            // Không bao giờ làm content rộng hơn viewport -> không có scrollbar ngang.
            var availableWidth = Math.Max(
                1,
                viewport.ClientSize.Width
                - content.Left
                - 16
                - SystemInformation.VerticalScrollBarWidth
                - 2);

            content.Width = availableWidth;
            sourceGroup.Width = availableWidth;
            optimizationGroup.Width = availableWidth;
            autoGroup.Width = availableWidth;
            createLimitGroup.Width = availableWidth;
            noCreateGroup.Width = availableWidth;
            logGroup.Width = availableWidth;

            // Số lượng PRF: hint chỉ chiếm phần còn lại bên phải.
            targetHint.Width = Math.Max(80, sourceGroup.ClientSize.Width - targetHint.Left - 18);

            // Tối ưu: nút giữ bên phải nếu đủ rộng, còn mô tả co theo group.
            applyOptimization.Left = Math.Max(288, optimizationGroup.ClientSize.Width - applyOptimization.Width - 18);
            optimizationStatus.Width = Math.Max(120, optimizationGroup.ClientSize.Width - optimizationStatus.Left - 18);
            optimizationHint.Width = Math.Max(120, optimizationGroup.ClientSize.Width - optimizationHint.Left - 18);

            // Auto close: checkbox dài co theo group; combobox giờ cố định bên phải.
            var autoTextWidth = Math.Max(220, autoGroup.ClientSize.Width - 36);
            closeOnBan.Width = autoTextWidth;
            closeOnStuck.Width = autoTextWidth;
            openReplacement.Width = autoTextWidth;
            deleteRetiredProfile.Width = autoTextWidth;
            hours.Left = Math.Max(360, autoGroup.ClientSize.Width - hours.Width - 18);
            closeOnTime.Width = Math.Max(220, hours.Left - closeOnTime.Left - 12);

            // 2 cột, mỗi cột gồm label + numeric trên cùng một hàng.
            var half = Math.Max(230, (createLimitGroup.ClientSize.Width - 36) / 2);
            var leftX = 18;
            var rightX = 18 + half;
            var leftNumX = Math.Min(leftX + 150, rightX - perSlot.Width - 14);
            var rightNumX = Math.Max(rightX + 150, createLimitGroup.ClientSize.Width - retryMinutes.Width - 18);

            slotLabel.Location = new Point(leftX, 76);
            perSlot.Location = new Point(leftNumX, 71);
            hourLabel.Location = new Point(rightX, 76);
            perHour.Location = new Point(rightNumX, 71);
            sessionLabel.Location = new Point(leftX, 124);
            perSession.Location = new Point(leftNumX, 119);
            retryLabel.Location = new Point(rightX, 124);
            retryMinutes.Location = new Point(rightNumX, 119);

            scheduleHint.Width = Math.Max(100, noCreateGroup.ClientSize.Width - scheduleHint.Left - 18);
            openLog.Left = Math.Max(380, logGroup.ClientSize.Width - openLog.Width - 16);
            logHint.Width = Math.Max(100, openLog.Left - logHint.Left - 16);

            content.Height = logGroup.Bottom + 12;
            viewport.AutoScrollMinSize = new Size(0, content.Height + 12);
        }
        viewport.Resize += (_, _) => LayoutScrollableContent();
        form.Shown += (_, _) => LayoutScrollableContent();

        RunAllStrategySettings BuildRunSettings()
            => NormalizeRunStrategySettings(new RunAllStrategySettings
            {
                Version = _runStrategySettings.Version,
                Mode = _runStrategySettings.Mode,
                PrimeModeArmed = _runStrategySettings.PrimeModeArmed,
                PrimeModeSuspended = _runStrategySettings.PrimeModeSuspended,
                AutoEnsureTarget = autoEnsureTarget.Checked,
                TargetSlots = (int)targetSlots.Value,
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
                CreateLimitEnabled = createLimitEnabled.Checked,
                CreateLimitPerSlot = (int)perSlot.Value,
                CreateLimitPerHour = (int)perHour.Value,
                CreateLimitPerSession = (int)perSession.Value,
                CreateLimitReuseRetryMinutes = (int)retryMinutes.Value,
                NoCreateScheduleEnabled = noCreateEnabled.Checked,
                NoCreateStartMinute = (noCreateStart.Value.Hour * 60) + noCreateStart.Value.Minute,
                NoCreateEndMinute = (noCreateEnd.Value.Hour * 60) + noCreateEnd.Value.Minute
            });

        autoEnsureTarget.CheckedChanged += (_, _) =>
        {
            UpdateTargetUi();

            // Giữ hành vi đã chốt ở patch trước: preference ON/OFF được lưu ngay,
            // nên thoát Tool mà chưa bấm Lưu vẫn nhớ đúng trạng thái.
            try
            {
                _runStrategySettings.AutoEnsureTarget = autoEnsureTarget.Checked;
                SaveRunStrategySettings(_runStrategySettings);
                _log.Info($"[AUTOMATION_SETTINGS_MAINTAIN_TARGET_PREF_SAVE] enabled={autoEnsureTarget.Checked}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[AUTOMATION_SETTINGS_MAINTAIN_TARGET_PREF_SAVE_WARN] enabled={autoEnsureTarget.Checked} error={ex.Message}");
            }
        };

        save.Click += (_, _) =>
        {
            if (noCreateEnabled.Checked
                && noCreateStart.Value.Hour == noCreateEnd.Value.Hour
                && noCreateStart.Value.Minute == noCreateEnd.Value.Minute)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Khung giờ không tạo PRF phải có giờ bắt đầu khác giờ kết thúc.",
                    "Cài đặt tự động",
                    MessageBoxIcon.Warning);
                return;
            }

            var previousReuseOnly = _autoCloseSettings.ReuseOnlyNoCreateProfile;
            try
            {
                // Nút Lưu chỉ lưu mode global cho các lần mở sau; nút "Áp dụng ngay"
                // phía trên mới đẩy runtime policy tới toàn bộ Worker đang mở.
                SaveManagerVmOptimizationMode(SelectedVmMode(), "automation_settings_save");

                _autoCloseSettings.CloseOnBan = closeOnBan.Checked;
                _autoCloseSettings.CloseOnRunTime = closeOnTime.Checked;
                _autoCloseSettings.RunHours = runHourOptions[Math.Clamp(hours.SelectedIndex, 0, runHourOptions.Length - 1)];
                _autoCloseSettings.CloseOnNotRunning10Minutes = closeOnStuck.Checked;
                _autoCloseSettings.OpenReplacementAfterAutoClose = openReplacement.Checked;
                _autoCloseSettings.ReuseOnlyNoCreateProfile = reuseOnly.Checked;
                _autoCloseSettings.DeleteProfileAfterBanOrLifetime = deleteRetiredProfile.Checked;
                SaveAutoCloseSettings();

                var savedRun = BuildRunSettings();
                SaveRunStrategySettings(savedRun);

                NotifyAutoReplacementReuseOnlySettingChanged(
                    previousReuseOnly,
                    _autoCloseSettings.ReuseOnlyNoCreateProfile,
                    "automation_settings_save");
                NotifyAutoReplacementCreateLimitSettingsChanged("automation_settings_save");
                NotifyAutoReplacementNoCreateScheduleSettingsChanged("automation_settings_save");

                _log.Info(
                    $"[AUTOMATION_SETTINGS_SAVE] maintainTarget={savedRun.AutoEnsureTarget} target={savedRun.TargetSlots} " +
                    $"reuseOnly={_autoCloseSettings.ReuseOnlyNoCreateProfile} replacement={_autoCloseSettings.OpenReplacementAfterAutoClose} " +
                    $"createLimit={savedRun.CreateLimitEnabled} noCreateSchedule={savedRun.NoCreateScheduleEnabled}");

                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Không lưu được Cài đặt tự động.\r\n\r\n" + ex.Message,
                    "Cài đặt tự động",
                    MessageBoxIcon.Error);
            }
        };

        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Controls.Add(viewport);
        form.Controls.Add(footer);
        form.Controls.Add(header);

        UiTheme.Apply(form);
        UiTheme.StyleButton(openLog, UiButtonKind.Neutral);
        UiTheme.StyleButton(applyOptimization, UiButtonKind.Primary);
        UiTheme.StyleButton(save, UiButtonKind.Primary);
        UiTheme.StyleButton(cancel, UiButtonKind.Neutral);

        LayoutScrollableContent();
        form.ShowDialog(this);
    }

    static string NormalizeAutoCloseReason(string? reason)
    {
        reason = (reason ?? "").Trim().ToUpperInvariant();

        if (reason == "BAN")
            return "BAN";

        if (reason == "FAULT_10M")
            return "FAULT_10M";

        if (reason.StartsWith("TIME_", StringComparison.OrdinalIgnoreCase)
            && reason.EndsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            return reason;
        }

        return reason;
    }

    static int GetAutoCloseReasonPriority(string? reason)
    {
        reason = NormalizeAutoCloseReason(reason);

        if (reason == "BAN")
            return 300;

        if (reason.StartsWith("TIME_", StringComparison.OrdinalIgnoreCase)
            && reason.EndsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        if (reason == "FAULT_10M")
            return 100;

        return 0;
    }

    static bool IsAutoCloseLifetimeReason(string? reason)
    {
        reason = NormalizeAutoCloseReason(reason);
        return reason.StartsWith("TIME_", StringComparison.OrdinalIgnoreCase)
            && reason.EndsWith("H", StringComparison.OrdinalIgnoreCase);
    }

    AutoCloseReasonDecision PromoteAutoCloseReason(
        string profileName,
        string reason,
        string detail)
    {
        profileName = (profileName ?? "").Trim();
        reason = NormalizeAutoCloseReason(reason);
        detail = (detail ?? "").Trim();

        var incoming = new AutoCloseReasonDecision(
            reason,
            detail,
            GetAutoCloseReasonPriority(reason),
            DateTime.UtcNow);

        lock (_autoCloseReasonLock)
        {
            if (!_autoCloseReasonByProfile.TryGetValue(profileName, out var current)
                || incoming.Priority > current.Priority
                || (incoming.Priority == current.Priority
                    && current.Detail.Length == 0
                    && incoming.Detail.Length > 0))
            {
                _autoCloseReasonByProfile[profileName] = incoming;

                if (current is not null
                    && incoming.Priority > current.Priority)
                {
                    _log.Warn(
                        $"[AUTO_CLOSE_REASON_PROMOTED] profile={profileName} from={current.Reason} to={incoming.Reason}");
                }

                return incoming;
            }

            return current;
        }
    }

    AutoCloseReasonDecision ResolveAutoCloseReasonDecision(
        string profileName,
        string fallbackReason,
        string fallbackDetail)
    {
        profileName = (profileName ?? "").Trim();

        lock (_autoCloseReasonLock)
        {
            if (_autoCloseReasonByProfile.TryGetValue(profileName, out var current))
                return current;
        }

        return PromoteAutoCloseReason(
            profileName,
            fallbackReason,
            fallbackDetail);
    }

    void ClearAutoCloseReasonDecision(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        AutoCloseReasonDecision? removed = null;

        lock (_autoCloseReasonLock)
        {
            if (_autoCloseReasonByProfile.Remove(profileName, out var value))
                removed = value;
        }

        if (removed is not null)
        {
            _log.Info(
                $"[AUTO_CLOSE_REASON_RESET] profile={profileName} old={removed.Reason} source={source}");
        }
    }

    void QueueAutoCloseForBan(ProfileContext ctx, string detail)
    {
        if (!_autoCloseFeatureInitialized || !_autoCloseSettings.CloseOnBan)
            return;

        var profileName = ctx.Profile.Name;

        // BAN là lý do ưu tiên cao nhất: chốt ngay trước mọi watchdog khác.
        PromoteAutoCloseReason(
            profileName,
            "BAN",
            string.IsNullOrWhiteSpace(detail) ? "TikTok account ban" : detail);

        // BAN không được tiếp tục mang đồng hồ FAULT_10M.
        _autoCloseNotRunningSinceUtc.Remove(profileName);

        if (!_autoCloseBanHandledProfiles.Add(profileName))
            return;

        _ = RunBanAutoCloseOnceAsync(
            ctx,
            string.IsNullOrWhiteSpace(detail) ? "TikTok account ban" : detail);
    }

    async Task RunBanAutoCloseOnceAsync(ProfileContext ctx, string detail)
    {
        var profileName = ctx.Profile.Name;

        await AutoCloseProfileAsync(
            ctx,
            "BAN",
            detail,
            source: "ban_watcher");

        var workerRunning = false;
        try
        {
            workerRunning = ctx.Worker is not null && !ctx.Worker.HasExited;
        }
        catch { }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        // FIX 13.7.5/13.7.6 UI FREEZE:
        // AutoCloseProfileAsync đã xác minh Chrome bằng Task.Run trước khi trả về thành công.
        // Không gọi ProbeProfileProcesses/PowerShell đồng bộ trên UI thread ở đây nữa.
        var runtimeStillPresent = workerRunning || tabOpen || ctx.Opening;

        if (runtimeStillPresent)
        {
            _autoCloseBanHandledProfiles.Remove(profileName);
            _log.Warn(
                $"[BAN_AUTO_CLOSE_RETRY_ARMED] profile={profileName} worker={workerRunning} tab={tabOpen} opening={ctx.Opening} runtimeStillPresent={runtimeStillPresent}");
        }
        else
        {
            _log.Info(
                $"[BAN_AUTO_CLOSE_HANDLED] profile={profileName} no_repeat=true");
        }
    }

    async Task CheckAutoCloseRuntimeAsync()
    {
        if (IsAutomationHalted
            || _autoCloseRuntimeCheckBusy
            || _closing
            || IsDisposed
            || Disposing
            || !_autoCloseFeatureInitialized
            || (!_autoCloseSettings.CloseOnRunTime
                && !_autoCloseSettings.CloseOnNotRunning10Minutes))
        {
            return;
        }

        _autoCloseRuntimeCheckBusy = true;

        try
        {
            var timeThreshold = TimeSpan.FromHours(_autoCloseSettings.RunHours);
            var stuckThreshold = TimeSpan.FromMinutes(AutoCloseNotRunningMinutes);
            var closeOnRunTime =
                _autoCloseSettings.CloseOnRunTime
                && !ShouldSuppressAutoCloseTimeForPrePrimeRefresh();

            // Không phụ thuộc tab Manager. Một profile có thể mất/tab bị detach nhưng
            // Worker/Chrome vẫn còn chạy và trang TikTok đã OOM. Nếu chỉ lọc theo Tab,
            // watchdog sẽ bỏ sót profile đó mãi mãi.
            var candidates = _contexts.Values
                .Where(IsAutoCloseRuntimeCandidate)
                .OrderBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
                .ToList();

            foreach (var ctx in candidates)
            {
                if (_closing)
                    break;

                var profileName = ctx.Profile.Name;
                var nowUtc = DateTime.UtcNow;

                if (_autoCloseInProgressProfiles.Contains(profileName))
                    continue;

                // Runtime-login recovery đang chủ động đóng/mở lại CHÍNH PRF. Không để
                // TIME/FAULT watchdog chen vào cùng lúc và phát sinh cleanup/replacement thứ hai.
                if (IsRuntimeLoginRecoveryInProgress(profileName))
                    continue;

                // Candidate đang thuộc một suất Tự bù (STABILIZING/CLEANUP) được chính
                // luồng Tự bù quản lý grace 10 phút. Watchdog global không được tạo
                // thêm FAULT_10M/suất bù thứ hai cho cùng profile.
                if (_autoReplacementClaimedProfiles.Contains(profileName)
                    || _autoReplacementCleanupProfiles.Contains(profileName))
                {
                    continue;
                }

                // Cleanup bị UNKNOWN (thường powershell_cim_timeout) không được giữ
                // cả watchdog 40-45 giây hoặc retry mỗi giây. Giữ riêng profile này
                // ở CLEANUP_PENDING 20s; các profile khác vẫn được xét bình thường.
                if (_autoCloseCleanupRetryUtc.TryGetValue(profileName, out var cleanupRetryUtc))
                {
                    if (nowUtc < cleanupRetryUtc)
                        continue;

                    _autoCloseCleanupRetryUtc.Remove(profileName);
                    _log.Info(
                        $"[AUTO_CLOSE_CLEANUP_RETRY_DUE] profile={profileName} retryAt={cleanupRetryUtc:O}");
                }

                var state = GetEffectiveRuntimeState(ctx);

                // RUNNING khỏe = Automation đang RUNNING + status còn tươi + Chrome CONNECTED
                // + không có chuỗi lỗi status/recovery.
                var healthyRunning = IsAutoCloseHealthyRunning(ctx, state, nowUtc);

                if (healthyRunning)
                {
                    MarkAutoCloseExpectedRunning(
                        profileName,
                        "healthy_running");

                    // Priority runtime: TIME luôn thắng FAULT_10M nếu cả hai cùng đến hạn.
                    if (closeOnRunTime)
                    {
                        var total = ReadStatisticsRuntime(ctx).Total;
                        if (total >= timeThreshold)
                        {
                            _log.Info(
                                $"[AUTO_CLOSE_TIME_DUE] profile={profileName} total={total:c} threshold={timeThreshold:c} state={state}");

                            await AutoCloseProfileAsync(
                                ctx,
                                $"TIME_{_autoCloseSettings.RunHours}H",
                                $"Đạt tổng thời gian chạy {_autoCloseSettings.RunHours} giờ (tổng={total:c}).",
                                source: "runtime_watchdog_time_healthy");

                            continue;
                        }
                    }

                    // RUNNING/CONNECTED chưa đủ để coi là khỏe:
                    // nếu Rounds + Step không thay đổi suốt 10 phút thì luồng thật đã treo.
                    if (_autoCloseSettings.CloseOnNotRunning10Minutes)
                    {
                        var progressFault =
                            ObserveAutoCloseProgressFault(
                                ctx,
                                nowUtc,
                                stuckThreshold);

                        if (progressFault.Length > 0)
                        {
                            _log.Warn(
                                $"[AUTO_CLOSE_PROGRESS_STUCK_DUE] profile={profileName} state={state} threshold={stuckThreshold:c} fault={progressFault}");

                            await AutoCloseProfileAsync(
                                ctx,
                                "FAULT_10M",
                                $"Không có tiến triển thực tế trong {AutoCloseNotRunningMinutes} phút dù Worker vẫn RUNNING. {progressFault}",
                                source: "runtime_watchdog_progress_stuck");

                            continue;
                        }
                    }
                    else
                    {
                        ResetAutoCloseProgressWatch(
                            profileName,
                            "watchdog_disabled");
                    }

                    if (_autoCloseNotRunningSinceUtc.Remove(profileName, out var faultStartedUtc))
                    {
                        var recoveredAfter = nowUtc - faultStartedUtc;
                        _log.Info(
                            $"[AUTO_CLOSE_STUCK_RECOVERED] profile={profileName} after={recoveredAfter:c}");
                    }

                    continue;
                }

                // Không còn ở RUNNING khỏe: watchdog trạng thái lỗi bên dưới sẽ đếm riêng.
                // Reset bộ đếm "không tiến triển khi vẫn RUNNING" để tránh cộng dồn sai.
                ResetAutoCloseProgressWatch(
                    profileName,
                    "not_healthy_running");

                // PAUSED là trạng thái hợp lệ. Nếu người dùng tạm dừng thì không coi là lỗi
                // và cũng không giữ đồng hồ lỗi cũ.
                if (state == RuntimeStatePaused)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                // Chỉ xét TIME/FAULT cho profile từng được xác nhận là đang chạy.
                // Khi Worker chết, hàm này probe đúng ProfilePath thay vì dựa HWND cache.
                var expectedToRun =
                    _autoCloseExpectedRunningProfiles.Contains(profileName)
                    || IsAutoCloseProfileExpectedToRun(ctx, state);

                // TIME vẫn phải thắng FAULT_10M nếu profile vừa đủ giờ đúng lúc runtime lỗi.
                if (closeOnRunTime && expectedToRun)
                {
                    var total = ReadStatisticsRuntime(ctx).Total;
                    if (total >= timeThreshold)
                    {
                        _log.Info(
                            $"[AUTO_CLOSE_TIME_DUE] profile={profileName} total={total:c} threshold={timeThreshold:c} state={state} healthy=false");

                        await AutoCloseProfileAsync(
                            ctx,
                            $"TIME_{_autoCloseSettings.RunHours}H",
                            $"Đạt tổng thời gian chạy {_autoCloseSettings.RunHours} giờ (tổng={total:c}) trong lúc runtime không khỏe.",
                            source: "runtime_watchdog_time_unhealthy");

                        continue;
                    }
                }

                if (!_autoCloseSettings.CloseOnNotRunning10Minutes)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                if (!expectedToRun)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                var fault = DescribeAutoCloseRuntimeFault(ctx, state, nowUtc);
                if (fault.Length == 0)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                if (!_autoCloseNotRunningSinceUtc.TryGetValue(profileName, out var startedUtc))
                {
                    startedUtc = nowUtc;
                    _autoCloseNotRunningSinceUtc[profileName] = startedUtc;

                    _log.Warn(
                        $"[AUTO_CLOSE_STUCK_BEGIN] profile={profileName} state={state} threshold={stuckThreshold:c} fault={fault}");

                    continue;
                }

                var stuckFor = nowUtc - startedUtc;
                if (stuckFor < stuckThreshold)
                    continue;

                _log.Warn(
                    $"[AUTO_CLOSE_STUCK_DUE] profile={profileName} state={state} stuck={stuckFor:c} threshold={stuckThreshold:c} fault={fault}");

                await AutoCloseProfileAsync(
                    ctx,
                    "FAULT_10M",
                    $"Không trở lại RUNNING trong {AutoCloseNotRunningMinutes} phút. state={state}; fault={fault}",
                    source: "runtime_watchdog_fault_10m");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_CLOSE_RUNTIME_CHECK_ERROR] {ex.Message}");
        }
        finally
        {
            _autoCloseRuntimeCheckBusy = false;
        }
    }

    bool IsAutoCloseHealthyRunning(ProfileContext ctx, string state, DateTime nowUtc)
    {
        if (state != RuntimeStateRunning)
            return false;

        if (ctx.RuntimeRecoveryInProgress
            || ctx.ConsecutiveStatusPollFailures > 0)
        {
            return false;
        }

        if (ctx.LastStatusRefreshUtc != DateTime.MinValue
            && nowUtc - ctx.LastStatusRefreshUtc > AutoCloseStatusStaleAfter)
        {
            return false;
        }

        var snapshot = ctx.LastSnapshot;
        if (snapshot is null)
            return false;

        if (!string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            return false;

        if (LooksLikeAutoCloseRuntimeFaultDetail(snapshot.Detail))
            return false;

        return true;
    }

    bool IsAutoCloseRuntimeCandidate(ProfileContext ctx)
    {
        if (ctx is null)
            return false;

        var profileName = ctx.Profile.Name;

        if (_autoCloseInProgressProfiles.Contains(profileName))
            return false;

        if (_autoCloseExpectedRunningProfiles.Contains(profileName)
            || _autoCloseNotRunningSinceUtc.ContainsKey(profileName))
        {
            return true;
        }

        if (ctx.Opening)
            return true;

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        if (tabOpen)
            return true;

        try
        {
            if (ctx.Worker is not null
                && !ctx.Worker.HasExited)
            {
                return true;
            }
        }
        catch
        {
            // Worker object tồn tại nhưng trạng thái process không đọc được:
            // vẫn cho watchdog kiểm tra thay vì bỏ qua.
            if (ctx.Worker is not null)
                return true;
        }

        var state = GetEffectiveRuntimeState(ctx);
        if (state is RuntimeStateRunning or RuntimeStateRecovering)
            return true;

        // Sau crash cứng Worker có thể biến mất trước khi Manager kịp lưu expected-set.
        // runtime_stats.json còn isRunning=true là dấu hiệu phiên trước chết khi đang RUNNING.
        return RuntimeStatsFileSaysRunning(ctx);
    }

    bool IsAutoCloseTimeEligibleAfterManagerRestart(ProfileContext ctx, string state)
    {
        // Người dùng Pause chủ động thì không TIME-close trong lúc đang pause.
        if (state == RuntimeStatePaused)
            return false;

        // State RUNNING/RECOVERING là đủ cho TIME. Không bắt buộc ctx.Worker phải
        // được Manager hiện tại adopt, vì Worker có thể đã chạy từ phiên Manager trước.
        if (state is RuntimeStateRunning or RuntimeStateRecovering)
            return true;

        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
                return true;
        }
        catch
        {
            if (ctx.Worker is not null)
                return true;
        }

        // Quan trọng cho trường hợp mở lại Manager: runtime_stats.json là nguồn
        // đang được UI dùng để hiển thị Tổng thời gian và có cờ isRunning. Với TIME,
        // cờ này được phép khôi phục eligibility dù Worker object chưa adopt được.
        // Luật FAULT_10M bên dưới vẫn KHÔNG dùng tín hiệu này một mình.
        return RuntimeStatsFileSaysRunning(ctx);
    }

    bool IsAutoCloseProfileExpectedToRun(ProfileContext ctx, string state)
    {
        var profileName = ctx.Profile.Name;

        // Luật V13.7.1:
        // - Không còn suy ra "đáng lẽ phải chạy" chỉ vì supply-state=used,
        //   NEW/TEST, hay vì profile đã có Tổng thời gian lịch sử.
        // - Profile mở để xem nhưng chưa Start sẽ không thể tự sinh FAULT_10M.
        if (_autoCloseExpectedRunningProfiles.Contains(profileName))
            return true;

        if (state == RuntimeStatePaused)
            return false;

        var workerAlive = false;

        try
        {
            workerAlive =
                ctx.Worker is not null
                && !ctx.Worker.HasExited;
        }
        catch
        {
            workerAlive = false;
        }

        // Sau khi Manager mở lại, một Worker còn sống và đang RUNNING/RECOVERING
        // là bằng chứng đủ mạnh rằng profile đã Start thật từ trước.
        if (workerAlive
            && state is RuntimeStateRunning or RuntimeStateRecovering)
        {
            MarkAutoCloseExpectedRunning(
                profileName,
                "live_worker_running");

            return true;
        }

        // Status IPC có thể tạm STOPPED/UNKNOWN trong lúc Worker vẫn sống.
        // Chỉ dùng runtime_stats.isRunning như tín hiệu phục hồi KHI Worker còn sống.
        // Không dùng file này để resurrect một Worker đã được người dùng đóng.
        if (workerAlive
            && RuntimeStatsFileSaysRunning(ctx))
        {
            MarkAutoCloseExpectedRunning(
                profileName,
                "live_worker_runtime_stats_running");

            return true;
        }

        // FIX 13.7.5/13.7.6 UI FREEZE:
        // Không chạy PowerShell/CIM đồng bộ trên UI thread chỉ vì runtime_stats cũ còn isRunning=true.
        // 13.7.4 không có bước orphan probe đồng bộ này. Nếu Worker đã mất thì không resurrect
        // expected-running từ runtime_stats; cleanup thật sự vẫn xác minh Chrome ở luồng async/Task.Run.
        if (!workerAlive
            && RuntimeStatsFileSaysRunning(ctx))
        {
            _log.Info(
                $"[AUTO_CLOSE_STALE_RUNTIME_STATS_IGNORED] profile={profileName} reason=worker_not_alive action=NO_SYNC_CHROME_PROBE");
        }

        return false;
    }

    bool RuntimeStatsFileSaysRunning(ProfileContext ctx)
    {
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            var path = Path.Combine(dataRoot, "runtime_stats.json");

            if (!File.Exists(path))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            return root.TryGetProperty("isRunning", out var running)
                && running.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    string DescribeAutoCloseRuntimeFault(ProfileContext ctx, string state, DateTime nowUtc)
    {
        var faults = new List<string>();

        if (state is RuntimeStateRecovering or RuntimeStateStopped or RuntimeStateUnknown)
            faults.Add("state=" + state);

        if (ctx.RuntimeRecoveryInProgress)
            faults.Add("recovery");

        if (ctx.ConsecutiveStatusPollFailures > 0)
            faults.Add($"status_fail={ctx.ConsecutiveStatusPollFailures}");

        if (ctx.LastStatusRefreshUtc == DateTime.MinValue)
        {
            faults.Add("status_never_ok");
        }
        else
        {
            var stale = nowUtc - ctx.LastStatusRefreshUtc;
            if (stale > AutoCloseStatusStaleAfter)
                faults.Add($"status_stale={stale.TotalSeconds:0}s");
        }

        if (ctx.Worker is null)
        {
            faults.Add("worker_missing");
        }
        else
        {
            try
            {
                if (ctx.Worker.HasExited)
                    faults.Add("worker_exited");
            }
            catch
            {
                faults.Add("worker_unknown");
            }
        }

        var snapshot = ctx.LastSnapshot;
        if (snapshot is not null)
        {
            if (!string.Equals(snapshot.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
                faults.Add("chrome=" + (snapshot.Chrome ?? ""));

            if (LooksLikeAutoCloseRuntimeFaultDetail(snapshot.Detail))
                faults.Add("detail=" + CompactAutoCloseFaultDetail(snapshot.Detail));
        }

        return string.Join("; ", faults.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    static bool LooksLikeAutoCloseRuntimeFaultDetail(string? detail)
    {
        detail = (detail ?? "").Trim();
        if (detail.Length == 0)
            return false;

        var normalized = detail.ToLowerInvariant();

        return normalized.Contains("recover")
            || normalized.Contains("reconnect")
            || normalized.Contains("out of memory")
            || normalized.Contains("oom")
            || normalized.Contains("crash")
            || normalized.Contains("aw, snap")
            || normalized.Contains("err_")
            || normalized.Contains("page error")
            || normalized.Contains("page crashed")
            || normalized.Contains("lỗi trang")
            || normalized.Contains("loi trang")
            || normalized.Contains("không thể truy cập")
            || normalized.Contains("khong the truy cap")
            || normalized.Contains("disconnected");
    }

    static string CompactAutoCloseFaultDetail(string? detail)
    {
        detail = (detail ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length <= 120)
            return detail;

        return detail[..120] + "...";
    }

    // Được RuntimeState gọi khi chính Manager gửi START/RESUME/PAUSE/STOP thành công.
    // Nhờ vậy PAUSE/STOP thủ công không bị watchdog hiểu nhầm là lỗi.
    void NotifyAutoCloseRuntimeCommand(
        ProfileContext ctx,
        string command,
        string confirmedState,
        bool explicitUserStartIntent = false)
    {
        if (!_autoCloseFeatureInitialized)
            return;

        var profileName = ctx.Profile.Name;
        command = (command ?? "").Trim().ToLowerInvariant();

        WriteAutoDiagnosticEvent(
            ctx,
            "runtime_command",
            command.ToUpperInvariant(),
            "CONFIRMED",
            $"confirmedState={confirmedState}");

        if (command is "start" or "start_auto" or "resume")
        {
            _autoCloseVerifiedCleanProfiles.Remove(profileName);

            // Một lần Start/Resume mới mở một vòng đời mới cho profile.
            ClearAutoCloseReasonDecision(
                profileName,
                $"runtime_command:{command}");

            MarkAutoCloseExpectedRunning(
                profileName,
                $"runtime_command:{command}");

            ResetAutoCloseProgressWatch(
                profileName,
                $"runtime_command:{command}");

            TrackAutoReplacementTargetRuntimeCommand(
                ctx,
                command,
                explicitUserStartIntent);

            // Người dùng/Manager vừa chủ động START/RESUME hoặc Auto Profile
            // đã gửi start_auto thành công.
            ArmAutoReplacementSession(
                $"runtime_command:{command}:{profileName}");

            return;
        }

        if (command is "pause" or "stop")
        {
            ClearAutoCloseExpectedRunning(
                profileName,
                $"runtime_command:{command}");

            ResetAutoCloseProgressWatch(
                profileName,
                $"runtime_command:{command}");

            TrackAutoReplacementTargetRuntimeCommand(
                ctx,
                command);
        }
    }

    enum AutoCloseChromePresence
    {
        Closed,
        Alive,
        Unknown
    }

    AutoCloseChromePresence ProbeAutoCloseChromePresence(
        string profileName,
        string profilePath,
        string source,
        out string detail)
    {
        profileName = (profileName ?? "").Trim();
        profilePath = (profilePath ?? "").Trim();
        source = (source ?? "").Trim();

        try
        {
            var probe = ChromeProfileNameSyncService.ProbeProfileProcesses(profilePath);

            if (!probe.Succeeded)
            {
                detail = probe.Error.Length > 0 ? probe.Error : "probe_failed";
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_PRESENCE_UNKNOWN] profile={profileName} source={source} error={detail} action=FAIL_CLOSED");
                return AutoCloseChromePresence.Unknown;
            }

            if (probe.ProcessIds.Count > 0)
            {
                detail = string.Join(",", probe.ProcessIds);
                return AutoCloseChromePresence.Alive;
            }

            detail = "";
            return AutoCloseChromePresence.Closed;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            _log.Warn(
                $"[AUTO_CLOSE_CHROME_PRESENCE_UNKNOWN] profile={profileName} source={source} error={detail} action=FAIL_CLOSED");
            return AutoCloseChromePresence.Unknown;
        }
    }

    bool IsAutoCloseRuntimeStillPresentStrict(ProfileContext ctx)
    {
        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
                return true;
        }
        catch
        {
            if (ctx.Worker is not null)
                return true;
        }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        if (tabOpen || ctx.Opening)
            return true;

        var chromePresence = ProbeAutoCloseChromePresence(
            ctx.Profile.Name,
            ctx.Profile.ProfilePath,
            "runtime_still_present",
            out _);

        // UNKNOWN không bao giờ được xem là CLOSED.
        return chromePresence != AutoCloseChromePresence.Closed;
    }

    async Task EnsureAutoCloseWorkerStoppedAsync(
        ProfileContext ctx,
        bool respectEmergencyStop = false)
    {
        void ThrowIfEmergencyStopRequested(string phase)
        {
            if (!respectEmergencyStop || !IsAutomationHalted)
                return;

            _log.Warn(
                $"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=worker_stop:{phase}");
            throw new OperationCanceledException(
                $"EMERGENCY_STOP_AUTOCLOSE: worker_stop:{phase}");
        }

        ThrowIfEmergencyStopRequested("begin");

        var worker = ctx.Worker;
        if (worker is null)
            return;

        bool IsExited()
        {
            try { return worker.HasExited; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Không xác minh được trạng thái Worker profile {ctx.Profile.Name}: {ex.Message}",
                    ex);
            }
        }

        if (!IsExited())
        {
            ThrowIfEmergencyStopRequested("before_shutdown");
            try
            {
                await SendPipeAsync(
                    ctx.Profile.Name,
                    "shutdown",
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_WORKER_SHUTDOWN_WARN] profile={ctx.Profile.Name} error={ex.Message}");
            }

            if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(7)))
            {
                ThrowIfEmergencyStopRequested("before_force_kill");
                try
                {
                    worker.Kill(true);
                    _log.Warn(
                        $"[AUTO_CLOSE_WORKER_FORCE_KILL] profile={ctx.Profile.Name}");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Không kill được Worker profile {ctx.Profile.Name}: {ex.Message}",
                        ex);
                }

                if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(3)))
                {
                    ThrowIfEmergencyStopRequested("after_force_kill_wait");
                    throw new InvalidOperationException(
                        $"Worker profile {ctx.Profile.Name} vẫn còn sống sau shutdown + force kill. Cleanup Barrier chặn Tự bù.");
                }
            }
        }

        ThrowIfEmergencyStopRequested("before_final_verify");

        if (!IsExited())
        {
            throw new InvalidOperationException(
                $"Worker profile {ctx.Profile.Name} chưa thoát hoàn toàn. Cleanup Barrier chặn Tự bù.");
        }

        _log.Info(
            $"[AUTO_CLOSE_WORKER_VERIFIED_EXITED] profile={ctx.Profile.Name}");

        try { worker.Dispose(); } catch { }
        if (ReferenceEquals(ctx.Worker, worker))
            ctx.Worker = null;
    }

    async Task AutoCloseProfileAsync(ProfileContext ctx, string reason, string detail, string source = "unknown")
    {
        if (IsAutomationHalted || _closing || IsDisposed || Disposing)
            return;

        var initialDecision = PromoteAutoCloseReason(
            ctx.Profile.Name,
            reason,
            detail);

        reason = initialDecision.Reason;
        detail = initialDecision.Detail;

        if (!_autoCloseInProgressProfiles.Add(ctx.Profile.Name))
            return;

        _autoCloseVerifiedCleanProfiles.Remove(ctx.Profile.Name);

        // Chốt số suất mục tiêu TRƯỚC khi profile lỗi bị gỡ khỏi expected-running.
        // Ví dụ đang chạy 6 profile thì Tự bù chỉ được duy trì tối đa 6 suất.
        CaptureAutoReplacementTargetBeforeAutoClose(ctx, reason);

        // Chỉ TIME_xH cần ghi Ghi chú tại đây. BAN có watcher riêng,
        // FAULT_10M/OOM/403/lỗi khác tuyệt đối không ghi Ghi chú Excel.
        QueueLifetimeExcelNoteIfNeeded(
            ctx,
            reason,
            detail);

        ClearAutoCloseExpectedRunning(
            ctx.Profile.Name,
            $"auto_close:{reason}");

        try
        {
            // Đọc lại quyết định lần cuối trước khi ghi log bắt đầu;
            // nếu BAN vừa được phát hiện thì BAN thắng TIME/FAULT.
            var beginDecision = ResolveAutoCloseReasonDecision(
                ctx.Profile.Name,
                reason,
                detail);

            reason = beginDecision.Reason;
            detail = beginDecision.Detail;

            _log.Info(
                $"[AUTO_CLOSE_BEGIN] profile={ctx.Profile.Name} reason={reason} detail={detail}");

            WriteAutoActivityLog(
                action: "TỰ ĐÓNG",
                profile: ctx.Profile.Name,
                account: ResolveAutoActivityAccount(ctx.Profile.Name),
                reason: reason,
                result: "BẮT ĐẦU",
                detail: detail);

            WriteAutoDiagnosticEvent(
                ctx,
                source,
                reason,
                "TRIGGER",
                $"decisionDetail={detail}");

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=before_stop");
                return;
            }

            var worker = ctx.Worker;

            if (worker is not null && !worker.HasExited)
            {
                // Với trigger theo giờ, Automation vẫn đang RUNNING nên phải STOP trước
                // thì Worker mới cho phép đóng Chrome an toàn.
                var state = GetEffectiveRuntimeState(ctx);
                if (state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering)
                {
                    WriteAutoDiagnosticEvent(
                        ctx, source, reason, "STEP", "step=STOP_AUTOMATION");

                    try
                    {
                        var stopReply = await SendCommandAsync(
                            ctx,
                            "stop",
                            TimeSpan.FromSeconds(8));

                        _log.Info(
                            $"[AUTO_CLOSE_STOP] profile={ctx.Profile.Name} reply={stopReply}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"[AUTO_CLOSE_STOP_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                    }

                    await Task.Delay(350);
                }
            }

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=before_close_chrome");
                return;
            }

            var chromeClosedByWorker = false;

            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                WriteAutoDiagnosticEvent(
                    ctx, source, reason, "STEP", "step=CLOSE_CHROME_BY_WORKER");

                try
                {
                    var closeReply = await SendCloseChromeCommandAsync(ctx);
                    chromeClosedByWorker = closeReply is "closed" or "not_running";

                    _log.Info(
                        $"[AUTO_CLOSE_CHROME] profile={ctx.Profile.Name} reply={closeReply}");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_CLOSE_CHROME_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            // V13.7.9 HOTFIX:
            // Không chạy PowerShell/CIM trước rồi lại chạy lần hai sau khi Worker chết.
            // close_chrome của Worker đã Browser.close + verify CDP/PID. Dù bước đó
            // thành công hay không, shutdown Worker trước để không còn nguồn tái sinh
            // Chrome; sau đó chỉ chạy MỘT cleanup probe theo đúng ProfilePath.
            if (!chromeClosedByWorker)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_CHROME_NEEDS_FINAL_CLEANUP] profile={ctx.Profile.Name} workerCloseVerified=false");
            }

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=before_shutdown_worker");
                return;
            }

            // Worker phải chết THẬT trước khi được phép giải phóng slot.
            WriteAutoDiagnosticEvent(
                ctx, source, reason, "STEP", "step=SHUTDOWN_WORKER");
            await EnsureAutoCloseWorkerStoppedAsync(ctx, respectEmergencyStop: true);

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=after_shutdown_worker");
                return;
            }

            // Dù Worker trả "closed"/"not_running" cũng KHÔNG được coi CDP tắt là
            // bằng chứng cuối cùng. Chrome có thể mất CDP trước nhưng process theo
            // ProfilePath vẫn còn sống. Luôn chạy strict ProfilePath 2-pass sau khi
            // Worker đã chết; chỉ khi pass 2/2 sạch mới được gỡ tab và tạo suất bù.
            WriteAutoDiagnosticEvent(
                ctx, source, reason, "STEP",
                chromeClosedByWorker
                    ? "step=FINAL_CHROME_STRICT_VERIFY_AFTER_WORKER_CLOSE"
                    : "step=FINAL_CHROME_STRICT_CLEANUP_FALLBACK");

            await EnsureAutoCloseChromeStoppedAsync(ctx, respectEmergencyStop: true);

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=after_chrome_cleanup");
                return;
            }

            _autoCloseVerifiedCleanProfiles.Add(ctx.Profile.Name);

            _log.Info(
                $"[AUTO_CLOSE_CHROME_CLEAN_CONFIRMED] profile={ctx.Profile.Name} source=profile_path_strict_2pass workerCloseVerified={chromeClosedByWorker}");

            WriteAutoDiagnosticEvent(
                ctx, source, reason, "STEP", "step=REMOVE_TAB");

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=before_remove_tab");
                return;
            }

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            // Cleanup đã xác minh Worker chết + Chrome sạch + tab đã gỡ. Chốt STOPPED
            // một lần nữa ở cuối để state cache RECOVERING cũ không thể giữ slot ảo.
            ConfirmRuntimeState(
                ctx,
                RuntimeStateStopped,
                "auto_close_cleanup_verified");

            // Reason có thể được nâng cấp trong lúc đang đóng (ví dụ FAULT -> BAN).
            var finalDecision = ResolveAutoCloseReasonDecision(
                ctx.Profile.Name,
                reason,
                detail);

            reason = finalDecision.Reason;
            detail = finalDecision.Detail;

            QueueLifetimeExcelNoteIfNeeded(
                ctx,
                reason,
                detail);

            ResetAutoCloseProgressWatch(
                ctx.Profile.Name,
                $"auto_close_done:{reason}");

            _autoCloseCleanupRetryUtc.Remove(ctx.Profile.Name);

            _log.Info(
                $"[AUTO_CLOSE_DONE] profile={ctx.Profile.Name} reason={reason}");

            WriteAutoActivityLog(
                action: "TỰ ĐÓNG",
                profile: ctx.Profile.Name,
                account: ResolveAutoActivityAccount(ctx.Profile.Name),
                reason: reason,
                result: "THÀNH CÔNG",
                detail: "Đã dừng Automation, đóng Chrome/Worker và gỡ tab profile.");

            WriteAutoDiagnosticEvent(
                ctx, source, reason, "DONE", "cleanup=success; replacementQueue=next");

            if (IsAutomationHalted)
            {
                _log.Warn($"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=before_queue_replacement");
                return;
            }

            QueueAutoReplacementAfterAutoClose(ctx.Profile.Name, reason);
        }
        catch (OperationCanceledException ex)
            when (IsAutomationHalted
                  || ex.Message.StartsWith("EMERGENCY_STOP_AUTOCLOSE:", StringComparison.Ordinal))
        {
            _log.Warn(
                $"[AUTO_CLOSE_ABORT_EMERGENCY] profile={ctx.Profile.Name} phase=cleanup_cancelled detail={ex.Message}");

            WriteAutoDiagnosticEvent(
                ctx,
                source,
                reason,
                "EMERGENCY_STOP",
                $"cleanup_cancelled={ex.Message}");

            WriteAutoActivityLog(
                action: "TỰ ĐÓNG",
                profile: ctx.Profile.Name,
                account: ResolveAutoActivityAccount(ctx.Profile.Name),
                reason: reason,
                result: "DỪNG KHẨN CẤP",
                detail: "Đã dừng cleanup tự động ngay khi nhận Dừng khẩn cấp; không tạo suất bù.");
        }
        catch (Exception ex)
        {
            var cleanupPending = ex is AutoCloseCleanupPendingException;
            var retryUtc = DateTime.UtcNow.Add(AutoCloseCleanupRetryDelay);
            _autoCloseCleanupRetryUtc[ctx.Profile.Name] = retryUtc;

            if (cleanupPending)
            {
                _log.Warn(
                    $"[AUTO_CLOSE_CLEANUP_PENDING] profile={ctx.Profile.Name} reason={reason} retry={retryUtc:O} error={ex.Message}");
            }
            else
            {
                _log.Error(
                    $"[AUTO_CLOSE_ERROR] profile={ctx.Profile.Name} reason={reason} retry={retryUtc:O} error={ex}");
            }

            // Chưa cleanup xong => tuyệt đối chưa QueueAutoReplacementAfterAutoClose.
            // Re-arm profile để watchdog thử lại sau thời điểm retry, nhưng không giữ
            // vòng hiện tại 40-45 giây và không retry mỗi 1 giây.
            MarkAutoCloseExpectedRunning(
                ctx.Profile.Name,
                cleanupPending
                    ? "auto_close_cleanup_pending"
                    : "auto_close_failed_retry");

            if (NormalizeAutoCloseReason(reason) == "FAULT_10M")
            {
                _autoCloseNotRunningSinceUtc[ctx.Profile.Name] =
                    DateTime.UtcNow.AddMinutes(-AutoCloseNotRunningMinutes);
            }

            WriteAutoDiagnosticEvent(
                ctx,
                source,
                reason,
                cleanupPending ? "CLEANUP_PENDING" : "ERROR",
                $"exception={ex.GetType().Name}; retry={retryUtc:O}; message={ex.Message}");

            WriteAutoActivityLog(
                action: "TỰ ĐÓNG",
                profile: ctx.Profile.Name,
                account: ResolveAutoActivityAccount(ctx.Profile.Name),
                reason: reason,
                result: cleanupPending ? "CHỜ DỌN" : "LỖI",
                detail: cleanupPending
                    ? $"{ex.Message} Thử lại sau {AutoCloseCleanupRetryDelay.TotalSeconds:0}s."
                    : ex.Message);
        }
        finally
        {
            _autoCloseInProgressProfiles.Remove(ctx.Profile.Name);
        }
    }
}
