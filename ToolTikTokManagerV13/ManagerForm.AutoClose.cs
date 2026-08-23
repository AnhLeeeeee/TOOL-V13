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
        public int Version { get; set; } = 1;
        public bool CloseOnBan { get; set; } = true;
        public bool CloseOnRunTime { get; set; }
        public int RunHours { get; set; } = 3;
        public bool CloseOnNotRunning10Minutes { get; set; } = true;
        public bool OpenReplacementAfterAutoClose { get; set; } = true;
    }

    const int AutoCloseNotRunningMinutes = 10;
    static readonly TimeSpan AutoCloseStatusStaleAfter = TimeSpan.FromSeconds(30);

    readonly HashSet<string> _autoCloseInProgressProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoCloseBanHandledProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _autoCloseExpectedRunningProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _autoCloseNotRunningSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    bool _autoCloseFeatureInitialized;
    bool _autoCloseRuntimeCheckBusy;
    AutoCloseSettingsDocument _autoCloseSettings = new();
    Button? _autoCloseToolbarButton;

    string AutoCloseSettingsPath => Path.Combine(_baseDir, "manager_auto_close.json");

    void InitializeAutoCloseFeature()
    {
        if (_autoCloseFeatureInitialized) return;
        _autoCloseFeatureInitialized = true;

        _autoCloseSettings = LoadAutoCloseSettings();
        InjectAutoCloseToolbarButton();
        InitializeAutoReplacementFeature();

        // Dùng chung timer 1 giây hiện có của Manager, không tạo thread/timer mới.
        _refreshTimer.Tick += async (_, _) => await CheckAutoCloseRuntimeAsync();

        _log.Info(
            $"[AUTO_CLOSE_INIT] ban={_autoCloseSettings.CloseOnBan} time={_autoCloseSettings.CloseOnRunTime} hours={_autoCloseSettings.RunHours} stuck10m={_autoCloseSettings.CloseOnNotRunning10Minutes} replace={_autoCloseSettings.OpenReplacementAfterAutoClose}");
    }

    AutoCloseSettingsDocument LoadAutoCloseSettings()
    {
        try
        {
            if (!File.Exists(AutoCloseSettingsPath))
                return NormalizeAutoCloseSettings(new AutoCloseSettingsDocument());

            var loaded = JsonSerializer.Deserialize<AutoCloseSettingsDocument>(
                File.ReadAllText(AutoCloseSettingsPath));

            return NormalizeAutoCloseSettings(loaded ?? new AutoCloseSettingsDocument());
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_CLOSE_SETTINGS_READ] error={ex.Message}");
            return NormalizeAutoCloseSettings(new AutoCloseSettingsDocument());
        }
    }

    static AutoCloseSettingsDocument NormalizeAutoCloseSettings(AutoCloseSettingsDocument settings)
    {
        settings.Version = 3;
        settings.RunHours = Math.Clamp(settings.RunHours, 3, 8);
        return settings;
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
            $"[AUTO_CLOSE_SETTINGS_SAVE] ban={_autoCloseSettings.CloseOnBan} time={_autoCloseSettings.CloseOnRunTime} hours={_autoCloseSettings.RunHours} stuck10m={_autoCloseSettings.CloseOnNotRunning10Minutes} replace={_autoCloseSettings.OpenReplacementAfterAutoClose}");
    }

    void InjectAutoCloseToolbarButton()
    {
        if (_autoCloseToolbarButton is not null && !_autoCloseToolbarButton.IsDisposed)
            return;

        var toolbar = EnumerateAutoCloseControls(this)
            .OfType<FlowLayoutPanel>()
            .FirstOrDefault(panel => panel.Controls
                .OfType<Button>()
                .Any(button => button.Text.Equals("Dừng tất cả", StringComparison.OrdinalIgnoreCase)));

        if (toolbar is null)
        {
            _log.Warn("[AUTO_CLOSE_UI] Không tìm thấy toolbar hàng 2 để thêm nút Tự đóng.");
            return;
        }

        _autoCloseToolbarButton = Button(
            "Tự đóng",
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

    void UpdateAutoCloseToolbarButtonText()
    {
        if (_autoCloseToolbarButton is null || _autoCloseToolbarButton.IsDisposed)
            return;

        var parts = new List<string>();
        if (_autoCloseSettings.CloseOnBan) parts.Add("BAN");
        if (_autoCloseSettings.CloseOnRunTime) parts.Add($"{_autoCloseSettings.RunHours}h");
        if (_autoCloseSettings.CloseOnNotRunning10Minutes) parts.Add("Lỗi10p");

        if (parts.Count > 0 && _autoCloseSettings.OpenReplacementAfterAutoClose)
            parts.Add(_autoReplacementSessionArmed ? "Bù" : "Bù: CHỜ");

        _autoCloseToolbarButton.Text = parts.Count == 0
            ? "Tự đóng: Tắt"
            : $"Tự đóng: {string.Join(" + ", parts)}";
    }

    void ShowAutoCloseDialog()
    {
        using var form = new Form
        {
            Text = $"Tự đóng Chrome + profile — {AppVersionInfo.Display}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Width = 610,
            Height = 510,
            BackColor = UiTheme.Canvas,
            Font = new Font("Segoe UI", 9F)
        };

        var title = new Label
        {
            Text = "TỰ ĐÓNG CHROME + PROFILE",
            AutoSize = true,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 77, 122),
            Location = new Point(24, 22)
        };

        var closeOnBan = new CheckBox
        {
            Text = "Tự đóng khi tài khoản bị BAN",
            Checked = _autoCloseSettings.CloseOnBan,
            AutoSize = true,
            Location = new Point(28, 75)
        };

        var closeOnTime = new CheckBox
        {
            Text = "Tự đóng khi Automation chạy đủ",
            Checked = _autoCloseSettings.CloseOnRunTime,
            AutoSize = true,
            Location = new Point(28, 120)
        };

        var hours = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
            Location = new Point(280, 116)
        };

        foreach (var value in new[] { 3, 4, 5, 6, 7, 8 })
            hours.Items.Add($"{value} giờ");

        hours.SelectedIndex = Math.Clamp(_autoCloseSettings.RunHours - 3, 0, 5);
        hours.Enabled = closeOnTime.Checked;
        closeOnTime.CheckedChanged += (_, _) => hours.Enabled = closeOnTime.Checked;

        var closeOnStuck = new CheckBox
        {
            Text = "Tự đóng nếu 10 phút không trở lại trạng thái RUNNING",
            Checked = _autoCloseSettings.CloseOnNotRunning10Minutes,
            AutoSize = true,
            Location = new Point(28, 165)
        };

        var openReplacement = new CheckBox
        {
            Text = "Sau khi Tự đóng: lấy tài khoản chưa gán và tự tạo profile mới",
            Checked = _autoCloseSettings.OpenReplacementAfterAutoClose,
            AutoSize = true,
            Location = new Point(28, 205)
        };

        var explanation = new Label
        {
            AutoSize = false,
            Width = 540,
            Height = 175,
            Location = new Point(28, 242),
            ForeColor = Color.FromArgb(70, 82, 96),
            Text =
                "Cách hoạt động:\r\n" +
                "• BAN: nhận đúng lỗi BAN là đóng ngay; ghi “ban” vào Excel chạy độc lập, Excel lỗi/đang khóa không cản Tự đóng.\r\n" +
                "• Theo giờ: tính thời gian Automation RUNNING của phiên hiện tại; Pause/Stopped không cộng thời gian.\r\n" +
                "• Lỗi 10p: profile đang treo mà RECOVERING/STOPPED/UNKNOWN, Chrome mất kết nối, status lỗi hoặc lỗi trang liên tục 10 phút sẽ tự đóng + bù. PAUSED thủ công không tính lỗi.\r\n" +
                "• Nếu profile trở lại RUNNING khỏe trước 10 phút, đồng hồ lỗi được xóa và tính lại từ đầu nếu lỗi lần sau.\r\n" +
                "• Tự bù: đóng 1 → bù 1; đóng nhiều → giữ đúng số suất bù. Suất chưa bù được sẽ lưu file và tự thử lại 2→3→5 phút.\r\n" +
                "• Mỗi suất bù lấy tài khoản chưa gán trong Excel và tạo profile mới; không quét/tái sử dụng profile cũ. Profile bù chỉ tính thành công sau khi RUNNING khỏe 30 giây.\r\n" +
                "• Hàng đợi bù được lưu lại. Khi vừa mở Manager, queue chỉ được khôi phục ở trạng thái CHỜ và không tự tạo profile. Sau START/RESUME hoặc một sự kiện Tự đóng mới trong phiên hiện tại, queue mới được phép chạy. Đóng profile thủ công không tự mở lại."
        };

        var save = new Button
        {
            Text = "Lưu",
            Width = 110,
            Height = 34,
            Location = new Point(338, 425)
        };

        var cancel = new Button
        {
            Text = "Hủy",
            Width = 110,
            Height = 34,
            Location = new Point(458, 425),
            DialogResult = DialogResult.Cancel
        };

        save.Click += (_, _) =>
        {
            _autoCloseSettings.CloseOnBan = closeOnBan.Checked;
            _autoCloseSettings.CloseOnRunTime = closeOnTime.Checked;
            _autoCloseSettings.RunHours = Math.Clamp(hours.SelectedIndex + 3, 3, 8);
            _autoCloseSettings.CloseOnNotRunning10Minutes = closeOnStuck.Checked;
            _autoCloseSettings.OpenReplacementAfterAutoClose = openReplacement.Checked;

            try
            {
                SaveAutoCloseSettings();
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Không lưu được cấu hình Tự đóng.\r\n\r\n" + ex.Message,
                    "Tự đóng",
                    MessageBoxIcon.Error);
            }
        };

        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Controls.Add(title);
        form.Controls.Add(closeOnBan);
        form.Controls.Add(closeOnTime);
        form.Controls.Add(hours);
        form.Controls.Add(closeOnStuck);
        form.Controls.Add(openReplacement);
        form.Controls.Add(explanation);
        form.Controls.Add(save);
        form.Controls.Add(cancel);

        UiTheme.Apply(form);
        UiTheme.StyleButton(save, UiButtonKind.Primary);
        UiTheme.StyleButton(cancel, UiButtonKind.Neutral);
        form.ShowDialog(this);
    }

    void QueueAutoCloseForBan(ProfileContext ctx, string detail)
    {
        if (!_autoCloseFeatureInitialized || !_autoCloseSettings.CloseOnBan)
            return;

        var profileName = ctx.Profile.Name;

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
            detail);

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

        if (workerRunning || tabOpen || ctx.Opening)
        {
            _autoCloseBanHandledProfiles.Remove(profileName);
            _log.Warn(
                $"[BAN_AUTO_CLOSE_RETRY_ARMED] profile={profileName} worker={workerRunning} tab={tabOpen} opening={ctx.Opening}");
        }
        else
        {
            _log.Info(
                $"[BAN_AUTO_CLOSE_HANDLED] profile={profileName} no_repeat=true");
        }
    }

    async Task CheckAutoCloseRuntimeAsync()
    {
        if (_autoCloseRuntimeCheckBusy
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

            var candidates = _contexts.Values
                .Where(ctx =>
                    ctx.Tab is not null
                    && !ctx.Tab.IsDisposed
                    && ctx.Tab.Parent == _tabs)
                .OrderBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
                .ToList();

            foreach (var ctx in candidates)
            {
                if (_closing)
                    break;

                var profileName = ctx.Profile.Name;

                if (_autoCloseInProgressProfiles.Contains(profileName))
                    continue;

                var state = GetEffectiveRuntimeState(ctx);
                var nowUtc = DateTime.UtcNow;

                // RUNNING khỏe = Automation đang RUNNING + status còn tươi + Chrome CONNECTED
                // + không có chuỗi lỗi status/recovery.
                var healthyRunning = IsAutoCloseHealthyRunning(ctx, state, nowUtc);

                if (healthyRunning)
                {
                    _autoCloseExpectedRunningProfiles.Add(profileName);

                    if (_autoCloseNotRunningSinceUtc.Remove(profileName, out var faultStartedUtc))
                    {
                        var recoveredAfter = nowUtc - faultStartedUtc;
                        _log.Info(
                            $"[AUTO_CLOSE_STUCK_RECOVERED] profile={profileName} after={recoveredAfter:c}");
                    }

                    if (_autoCloseSettings.CloseOnRunTime)
                    {
                        var session = ReadStatisticsRuntime(ctx).Session;
                        if (session >= timeThreshold)
                        {
                            _log.Info(
                                $"[AUTO_CLOSE_TIME_DUE] profile={profileName} session={session:c} threshold={timeThreshold:c} state={state}");

                            await AutoCloseProfileAsync(
                                ctx,
                                $"TIME_{_autoCloseSettings.RunHours}H",
                                $"Đạt thời gian chạy {_autoCloseSettings.RunHours} giờ (phiên={session:c}).");

                            continue;
                        }
                    }

                    continue;
                }

                // PAUSED là trạng thái hợp lệ. Nếu người dùng tạm dừng thì không coi là lỗi
                // và cũng không giữ đồng hồ lỗi cũ.
                if (state == RuntimeStatePaused)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                if (!_autoCloseSettings.CloseOnNotRunning10Minutes)
                {
                    _autoCloseNotRunningSinceUtc.Remove(profileName);
                    continue;
                }

                // Chỉ giám sát profile từng được xác nhận là đang chạy/đang treo.
                // Khi Manager vừa mở lại giữa lúc profile đang lỗi, dùng runtime lịch sử
                // hoặc trạng thái supply=used làm tín hiệu đây là profile đã treo thật.
                if (!_autoCloseExpectedRunningProfiles.Contains(profileName)
                    && !IsAutoCloseProfileExpectedToRun(ctx))
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
                    $"Không trở lại RUNNING trong {AutoCloseNotRunningMinutes} phút. state={state}; fault={fault}");
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

    bool IsAutoCloseProfileExpectedToRun(ProfileContext ctx)
    {
        var persisted = GetProfileSupplyState(ctx.Profile.Name);

        // NEW/TEST là kho dự phòng: không tự kết luận lỗi chỉ vì đang mở mà chưa treo.
        if (persisted is not null
            && (persisted.State.Equals("new", StringComparison.OrdinalIgnoreCase)
                || persisted.State.Equals("test", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (persisted is not null
            && persisted.State.Equals("used", StringComparison.OrdinalIgnoreCase))
        {
            _autoCloseExpectedRunningProfiles.Add(ctx.Profile.Name);
            return true;
        }

        var runtime = ReadStatisticsRuntime(ctx);

        // Profile đã chạy đủ chuẩn ĐÃ TREO trước đây thì coi là profile chạy chính.
        if (runtime.Total >= TimeSpan.FromMinutes(AutoReplacementTreoThresholdMinutes))
        {
            _autoCloseExpectedRunningProfiles.Add(ctx.Profile.Name);
            return true;
        }

        return false;
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
    void NotifyAutoCloseRuntimeCommand(ProfileContext ctx, string command, string confirmedState)
    {
        if (!_autoCloseFeatureInitialized)
            return;

        var profileName = ctx.Profile.Name;
        command = (command ?? "").Trim().ToLowerInvariant();

        if (command is "start" or "resume")
        {
            _autoCloseExpectedRunningProfiles.Add(profileName);
            _autoCloseNotRunningSinceUtc.Remove(profileName);

            // Người dùng/Manager vừa chủ động START hoặc RESUME.
            // Từ thời điểm này mới cho phép xử lý các suất bù đang chờ.
            ArmAutoReplacementSession($"runtime_command:{command}:{profileName}");
            return;
        }

        if (command is "pause" or "stop")
        {
            _autoCloseExpectedRunningProfiles.Remove(profileName);
            _autoCloseNotRunningSinceUtc.Remove(profileName);
        }
    }

    async Task AutoCloseProfileAsync(ProfileContext ctx, string reason, string detail)
    {
        if (_closing || IsDisposed || Disposing)
            return;

        if (!_autoCloseInProgressProfiles.Add(ctx.Profile.Name))
            return;

        _autoCloseExpectedRunningProfiles.Remove(ctx.Profile.Name);
        _autoCloseNotRunningSinceUtc.Remove(ctx.Profile.Name);

        try
        {
            _log.Info(
                $"[AUTO_CLOSE_BEGIN] profile={ctx.Profile.Name} reason={reason} detail={detail}");

            var worker = ctx.Worker;

            if (worker is not null && !worker.HasExited)
            {
                // Với trigger theo giờ, Automation vẫn đang RUNNING nên phải STOP trước
                // thì Worker mới cho phép đóng Chrome an toàn.
                var state = GetEffectiveRuntimeState(ctx);
                if (state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering)
                {
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

            var chromeClosedByWorker = false;

            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
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

            // Fallback theo đúng ProfilePath đã lưu. Không đụng Chrome của profile khác.
            if (!chromeClosedByWorker)
            {
                try
                {
                    var stoppedPids = await Task.Run(
                        () => ChromeProfileNameSyncService.StopChromeUsingProfile(
                            ctx.Profile.ProfilePath));

                    _log.Info(
                        $"[AUTO_CLOSE_CHROME_FALLBACK] profile={ctx.Profile.Name} stopped={stoppedPids.Count} pids={string.Join(",", stoppedPids)}");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_CLOSE_CHROME_FALLBACK_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            worker = ctx.Worker;
            if (worker is not null && !worker.HasExited)
            {
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

                try
                {
                    if (!await WaitForProcessExitAsync(worker, TimeSpan.FromSeconds(7)))
                        worker.Kill(true);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[AUTO_CLOSE_WORKER_KILL_WARN] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            worker = ctx.Worker;
            if (worker is not null)
            {
                var exited = false;
                try { exited = worker.HasExited; } catch { }
                if (exited)
                {
                    try { worker.Dispose(); } catch { }
                    if (ReferenceEquals(ctx.Worker, worker))
                        ctx.Worker = null;
                }
            }

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            _log.Info(
                $"[AUTO_CLOSE_DONE] profile={ctx.Profile.Name} reason={reason}");

            QueueAutoReplacementAfterAutoClose(ctx.Profile.Name, reason);
        }
        catch (Exception ex)
        {
            _log.Error(
                $"[AUTO_CLOSE_ERROR] profile={ctx.Profile.Name} reason={reason} error={ex}");
        }
        finally
        {
            _autoCloseInProgressProfiles.Remove(ctx.Profile.Name);
        }
    }
}
