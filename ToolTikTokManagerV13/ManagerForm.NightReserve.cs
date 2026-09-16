using System.Text;
using System.Text.Json;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class NightReserveSettings
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public int CreateStartHour { get; set; } = 12;
        public int CreateEndHour { get; set; } = 18;
        public int TargetCount { get; set; } = 3;
    }

    sealed class NightReserveStateDocument
    {
        public int Version { get; set; } = 1;
        public List<string> Profiles { get; set; } = new();
    }

    readonly object _nightReserveLock = new();
    readonly SemaphoreSlim _nightReserveGate = new(1, 1);
    bool _nightReserveInitialized;
    bool _nightReserveTickBusy;
    DateTime _nightReserveNextCheckUtc = DateTime.MinValue;
    CancellationTokenSource _nightReserveCts = new();
    int _nightReserveConsecutiveLoginErrors;
    NightReserveSettings _nightReserveSettings = new();
    NightReserveStateDocument _nightReserveState = new();

    string NightReserveSettingsPath
        => Path.Combine(_baseDir, "manager_night_reserve_settings.json");

    string NightReserveStatePath
        => Path.Combine(_baseDir, "manager_night_reserve_profiles.json");

    void InitializeNightReserveFeature()
    {
        if (_nightReserveInitialized)
            return;

        _nightReserveInitialized = true;

        // Dự phòng đêm dùng trực tiếp phân loại MỚI/TB/CŨ của Run Strategy và
        // toàn bộ pipeline Auto Profile/Tạo trước hiện có.
        InitializeRunStrategyFeature();
        _nightReserveSettings = LoadNightReserveSettings();
        _nightReserveState = LoadNightReserveState();

        _refreshTimer.Tick += async (_, _) => await CheckNightReserveAsync();
        FormClosing += (_, _) =>
        {
            try { _nightReserveCts.Cancel(); } catch { }
        };
    }

    NightReserveSettings LoadNightReserveSettings()
    {
        try
        {
            if (!File.Exists(NightReserveSettingsPath))
                return NormalizeNightReserveSettings(new NightReserveSettings());

            var loaded = JsonSerializer.Deserialize<NightReserveSettings>(
                File.ReadAllText(NightReserveSettingsPath, Encoding.UTF8));

            return NormalizeNightReserveSettings(
                loaded ?? new NightReserveSettings());
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_SETTINGS_READ] error={ex.Message}");
            return NormalizeNightReserveSettings(new NightReserveSettings());
        }
    }

    static NightReserveSettings NormalizeNightReserveSettings(
        NightReserveSettings settings)
    {
        settings.Version = 1;
        settings.CreateStartHour = Math.Clamp(settings.CreateStartHour, 0, 23);
        settings.CreateEndHour = Math.Clamp(settings.CreateEndHour, 1, 24);
        settings.TargetCount = Math.Clamp(settings.TargetCount, 1, 20);
        return settings;
    }

    void SaveNightReserveSettings(NightReserveSettings settings)
    {
        settings = NormalizeNightReserveSettings(settings);
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });
        var temp = NightReserveSettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, NightReserveSettingsPath, overwrite: true);
        _nightReserveSettings = settings;
    }

    NightReserveStateDocument LoadNightReserveState()
    {
        try
        {
            if (!File.Exists(NightReserveStatePath))
                return new NightReserveStateDocument();

            var loaded = JsonSerializer.Deserialize<NightReserveStateDocument>(
                File.ReadAllText(NightReserveStatePath, Encoding.UTF8))
                ?? new NightReserveStateDocument();

            loaded.Version = 1;
            loaded.Profiles ??= new List<string>();
            loaded.Profiles = loaded.Profiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_STATE_READ] error={ex.Message}");
            return new NightReserveStateDocument();
        }
    }

    void SaveNightReserveStateUnsafe()
    {
        _nightReserveState.Version = 1;
        _nightReserveState.Profiles = _nightReserveState.Profiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(
            _nightReserveState,
            new JsonSerializerOptions { WriteIndented = true });
        var temp = NightReserveStatePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, NightReserveStatePath, overwrite: true);
    }

    bool IsNightReserveProfile(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        lock (_nightReserveLock)
        {
            return _nightReserveState.Profiles.Any(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    bool IsNightReserveProfileProtected(string profileName)
    {
        if (!_nightReserveInitialized || !_nightReserveSettings.Enabled)
            return false;

        if (!IsNightReserveProfile(profileName))
            return false;

        // Dự phòng chỉ được tiêu ở OFFPEAK. Từ PREPARE trở đi phải giữ lại,
        // tránh giờ vàng lấy mất kho vừa chuẩn bị ban ngày.
        var phase = GetRunStrategyPhase(GetToolNow(), _runStrategySettings);
        return !phase.Equals("OFFPEAK", StringComparison.OrdinalIgnoreCase);
    }

    void MarkNightReserveConsumed(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var removed = false;
        lock (_nightReserveLock)
        {
            removed = _nightReserveState.Profiles.RemoveAll(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveNightReserveStateUnsafe();
        }

        if (removed)
        {
            _log.Info(
                $"[NIGHT_RESERVE_CONSUMED] profile={profileName} source={source} remaining={GetNightReserveCountSnapshot()}");
        }
    }

    void AddNightReserveProfile(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var added = false;
        lock (_nightReserveLock)
        {
            if (!_nightReserveState.Profiles.Any(x =>
                    x.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
            {
                _nightReserveState.Profiles.Add(profileName);
                SaveNightReserveStateUnsafe();
                added = true;
            }
        }

        if (added)
        {
            _log.Info(
                $"[NIGHT_RESERVE_ADD] profile={profileName} source={source} count={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");
        }
    }

    int GetNightReserveCountSnapshot()
    {
        lock (_nightReserveLock)
            return _nightReserveState.Profiles.Count;
    }

    static bool IsHourInsideNightReserveCreateWindow(
        DateTimeOffset now,
        NightReserveSettings settings)
    {
        var hour = now.Hour;
        var start = settings.CreateStartHour;
        var end = settings.CreateEndHour;

        if (end == 24)
            return hour >= start;

        if (start < end)
            return hour >= start && hour < end;

        // Cho phép cấu hình qua nửa đêm nếu sau này cần.
        return hour >= start || hour < end;
    }

    async Task<int> ReconcileNightReserveAsync(
        bool adoptExistingFresh,
        CancellationToken token)
    {
        await RefreshReusableProfileQueueAsync(
            "night_reserve_reconcile",
            token);

        IReadOnlyDictionary<string, string> identityResults;
        try
        {
            identityResults = await RunAccountPoolIoAsync(
                () => _accountPoolService.GetIdentityResults(),
                token);
        }
        catch
        {
            identityResults = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        List<ReusableProfileQueueEntry> ready;
        lock (_reusableProfileQueueLock)
        {
            ready = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x =>
                    !x.NameSyncPending
                    && !string.IsNullOrWhiteSpace(x.ProfileName))
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        var byProfile = ready.ToDictionary(
            x => x.ProfileName,
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var freshLimit = TimeSpan.FromHours(
            Math.Max(1, _runStrategySettings.FreshHours)).TotalSeconds;

        lock (_nightReserveLock)
        {
            var before = _nightReserveState.Profiles.Count;

            // Bỏ marker đã không còn nằm trong queue READY (đã dùng, bị BAN,
            // chuyển NAME_SYNC_PENDING hoặc bị người dùng bỏ chờ).
            _nightReserveState.Profiles.RemoveAll(name =>
                !byProfile.ContainsKey(name));

            // Nếu user giảm target, giữ những PRF trẻ nhất làm dự phòng.
            if (_nightReserveState.Profiles.Count > _nightReserveSettings.TargetCount)
            {
                var keep = _nightReserveState.Profiles
                    .Where(byProfile.ContainsKey)
                    .OrderBy(name => byProfile[name].TotalRunSeconds)
                    .ThenBy(name => name, NaturalProfileNameOrder)
                    .Take(_nightReserveSettings.TargetCount)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _nightReserveState.Profiles.RemoveAll(name => !keep.Contains(name));
            }

            if (adoptExistingFresh
                && _nightReserveState.Profiles.Count < _nightReserveSettings.TargetCount)
            {
                var need = _nightReserveSettings.TargetCount - _nightReserveState.Profiles.Count;
                var candidates = ready
                    .Where(x =>
                        x.TotalRunSeconds < freshLimit
                        && !_nightReserveState.Profiles.Any(r =>
                            r.Equals(x.ProfileName, StringComparison.OrdinalIgnoreCase))
                        && !IsReusableProfileBusy(x.ProfileName)
                        && identityResults.TryGetValue(x.Username, out var result)
                        && string.Equals(
                            (result ?? "").Trim(),
                            "DONE",
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.TotalRunSeconds)
                    .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                    .Take(need)
                    .Select(x => x.ProfileName)
                    .ToList();

                foreach (var profileName in candidates)
                    _nightReserveState.Profiles.Add(profileName);
            }

            if (before != _nightReserveState.Profiles.Count || adoptExistingFresh)
                SaveNightReserveStateUnsafe();

            return _nightReserveState.Profiles.Count;
        }
    }

    async Task CheckNightReserveAsync()
    {
        if (IsAutomationHalted
            || !_nightReserveInitialized
            || !_nightReserveSettings.Enabled
            || _nightReserveTickBusy
            || _closing
            || IsDisposed
            || Disposing
            || DateTime.UtcNow < _nightReserveNextCheckUtc)
        {
            return;
        }

        _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(2);

        if (!IsHourInsideNightReserveCreateWindow(
                GetToolNow(),
                _nightReserveSettings))
        {
            return;
        }

        // Không chen vào Tự bù/Prime rotation đang xử lý một slot.
        if (_autoReplacementQueueRunning
            || _autoReplacementStartAllInProgress
            || GetAutoReplacementPendingCount() > 0
            || _runStrategyRotationRunning)
        {
            return;
        }

        _nightReserveTickBusy = true;
        try
        {
            if (!await _nightReserveGate.WaitAsync(0))
                return;

            try
            {
                var token = _nightReserveCts.Token;

                // Gate dùng chung với +Auto Profile và "Tạo trước PRF chờ".
                // Không bao giờ login/create xen kẽ hai luồng.
                if (!await _autoProfileQueueGate.WaitAsync(0, token))
                    return;

                try
                {
                    var count = await ReconcileNightReserveAsync(
                        adoptExistingFresh: true,
                        token);

                    if (count >= _nightReserveSettings.TargetCount)
                        return;

                    var outcome = await CreateOneNightReserveProfileAsync(token);
                    if (outcome is not null && outcome.Skipped != true)
                    {
                        var cooldownSettings = LoadAutoProfileCooldownSettings();
                        var kind = ResolveAutoProfileCooldownKind(
                            outcome,
                            ref _nightReserveConsecutiveLoginErrors);

                        await WaitAutoProfileCooldownAsync(
                            cooldownSettings,
                            kind,
                            () => false,
                            token,
                            text => _log.Info($"[NIGHT_RESERVE_COOLDOWN] {text}"),
                            () => _closing);
                    }
                }
                finally
                {
                    _autoProfileQueueGate.Release();
                }
            }
            finally
            {
                _nightReserveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Manager đang đóng.
        }
        catch (Exception ex)
        {
            _log.Warn($"[NIGHT_RESERVE_TICK_WARN] {ex.Message}");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(5);
        }
        finally
        {
            _nightReserveTickBusy = false;
        }
    }

    async Task<AutoProfileProcessOutcome?> CreateOneNightReserveProfileAsync(
        CancellationToken token)
    {
        var startName = DetectNextAutoProfileName();
        var identity = LoadIdentityToolState();
        var names = SplitIdentityNames(identity.NamesText);
        if (names.Count == 0)
        {
            _log.Warn("[NIGHT_RESERVE_WAIT] reason=identity_names_empty");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(10);
            return null;
        }

        RememberAutoProfileSequenceStart(startName);

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

        var item = queue.FirstOrDefault(x => !x.ResumeExisting);
        if (item is null)
        {
            _log.Warn("[NIGHT_RESERVE_WAIT] reason=no_unassigned_account");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(10);
            return null;
        }

        _log.Info(
            $"[NIGHT_RESERVE_CREATE_BEGIN] profile={item.ProfileName} account={item.Account.Username} target={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");

        AutoProfileProcessOutcome? outcome = null;
        _autoReplacementCleanupProfiles.Add(item.ProfileName);
        try
        {
            outcome = await ProcessAutoProfileQueueItemAsync(
                item,
                autoRename: true,
                autoStart: false,
                isPaused: () => false,
                ct: token,
                ui: (step, result, _) =>
                    _log.Info(
                        $"[NIGHT_RESERVE_CREATE_STEP] profile={item.ProfileName} step={step} result={result}"),
                verifyIdentityAfterRename: true,
                writeIdentityDoneToExcel: true,
                tolerateIdentityValidationFailure: true);

            await ClosePreparedProfileRuntimeAsync(
                item.ProfileName,
                item.Account.Username);

            if (!outcome.RenameSucceeded)
            {
                _log.Warn(
                    $"[NIGHT_RESERVE_NOT_READY] profile={item.ProfileName} status={outcome.Status} step={outcome.Step} reason=rename_not_succeeded");
                return outcome;
            }

            // Kho dự phòng chỉ nhận PRF đã xác minh tên + ghi DONE Excel. Nếu TikTok
            // chưa kịp cập nhật tên hoặc Excel chưa ghi được DONE, tận dụng CHÍNH lane
            // NAME_SYNC_PENDING cũ để profile không bị lấy chạy như một PRF READY.
            if (!outcome.IdentityVerified || !outcome.IdentityExcelDone)
            {
                QueueReusableProfileNameSyncPending(
                    item,
                    outcome.IdentityVerified
                        ? "night_reserve_identity_done_pending"
                        : "night_reserve_name_not_verified");

                _log.Info(
                    $"[NIGHT_RESERVE_PENDING] profile={item.ProfileName} account={item.Account.Username} "
                    + $"verified={outcome.IdentityVerified} excelDone={outcome.IdentityExcelDone} action=NAME_SYNC_PENDING_NOT_RESERVE");
                return outcome;
            }

            if (!TryAddReusableProfileManual(
                    item.Account.Id,
                    item.Account.Username,
                    item.ProfileName,
                    item.Account.Note,
                    out var queueMessage))
            {
                _log.Warn(
                    $"[NIGHT_RESERVE_QUEUE_FAIL] profile={item.ProfileName} message={queueMessage}");
                return outcome;
            }

            AddNightReserveProfile(
                item.ProfileName,
                "auto_daytime_create_verified");

            _log.Info(
                $"[NIGHT_RESERVE_CREATE_OK] profile={item.ProfileName} account={item.Account.Username} count={GetNightReserveCountSnapshot()}/{_nightReserveSettings.TargetCount}");

            return outcome;
        }
        catch (OperationCanceledException)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            throw;
        }
        catch (AutoProfileLoginBanException ex)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            _log.Warn(
                $"[NIGHT_RESERVE_CREATE_BAN] profile={item.ProfileName} account={item.Account.Username} error={ex.Message}");
            return new AutoProfileProcessOutcome(
                false,
                false,
                "LOGIN_BANNED",
                "WAIT_LOGIN",
                ex.Message);
        }
        catch (Exception ex)
        {
            await TryClosePreparedProfileRuntimeQuietlyAsync(
                item.ProfileName,
                item.Account.Username);
            _log.Warn(
                $"[NIGHT_RESERVE_CREATE_FAIL] profile={item.ProfileName} account={item.Account.Username} error={ex.Message}");
            _nightReserveNextCheckUtc = DateTime.UtcNow.AddMinutes(5);
            return outcome;
        }
        finally
        {
            _autoReplacementCleanupProfiles.Remove(item.ProfileName);
        }
    }

    void ShowNightReserveSettingsDialog(IWin32Window owner)
    {
        InitializeNightReserveFeature();
        var current = LoadNightReserveSettings();

        var form = new Form
        {
            Text = $"Dự phòng PRF ban đêm — {AppVersionInfo.Display}",
            Width = 590,
            Height = 410,
            MinimumSize = new Size(560, 385),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: true);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 18),
            ColumnCount = 3,
            RowCount = 7,
            BackColor = ModernDialog.Canvas
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        var enabled = new CheckBox
        {
            Text = "Tự chuẩn bị PRF dự phòng ban ngày",
            Checked = current.Enabled,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 91, 152),
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(enabled, 0, 0);
        panel.SetColumnSpan(enabled, 3);

        NumericUpDown Hour(int value, int min, int max) => new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 80,
            Margin = new Padding(0, 6, 0, 0)
        };

        var startHour = Hour(current.CreateStartHour, 0, 23);
        var endHour = Hour(current.CreateEndHour, 1, 24);
        var target = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 20,
            Value = Math.Clamp(current.TargetCount, 1, 20),
            Width = 80,
            Margin = new Padding(0, 6, 0, 0)
        };

        Label Field(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(46, 65, 88),
            Margin = new Padding(0, 10, 8, 0)
        };

        panel.Controls.Add(Field("Bắt đầu tạo dự phòng"), 0, 1);
        panel.Controls.Add(startHour, 1, 1);
        panel.Controls.Add(Field("giờ"), 2, 1);

        panel.Controls.Add(Field("Kết thúc tạo dự phòng"), 0, 2);
        panel.Controls.Add(endHour, 1, 2);
        panel.Controls.Add(Field("giờ · 24 = 00:00"), 2, 2);

        panel.Controls.Add(Field("Số PRF dự phòng cần giữ"), 0, 3);
        panel.Controls.Add(target, 1, 3);
        panel.Controls.Add(Field("PRF"), 2, 3);

        var currentCount = new Label
        {
            Text = $"Hiện có: {GetNightReserveCountSnapshot()} / {current.TargetCount} PRF dự phòng",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.DarkGreen,
            Margin = new Padding(0, 10, 0, 0)
        };
        panel.Controls.Add(currentCount, 0, 4);
        panel.SetColumnSpan(currentCount, 3);

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            Text =
                "Tận dụng đúng pipeline ‘Tạo trước PRF chờ’: tạo tuần tự 1 PRF/lần, dùng cooldown chung, login + đổi tên + xác nhận tên DONE rồi đóng sạch. "
                + "PRF dự phòng được bảo vệ trong PRIME/PREPARE; ngoài giờ vàng chỉ dùng sau các PRF thường phù hợp. Nếu kho đã có PRF MỚI + Tên/ảnh=DONE thì Tool ưu tiên đánh dấu chúng làm dự phòng trước, không tạo dư.",
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(info, 0, 5);
        panel.SetColumnSpan(info, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0)
        };
        var cancel = new Button { Text = "Hủy", Size = new Size(100, 40) };
        var save = new Button { Text = "Lưu", Size = new Size(110, 40) };
        ModernDialog.StyleSecondaryButton(cancel);
        ModernDialog.StylePrimaryButton(save);
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        panel.Controls.Add(actions, 0, 6);
        panel.SetColumnSpan(actions, 3);

        cancel.Click += (_, _) => form.Close();
        save.Click += async (_, _) =>
        {
            var updated = new NightReserveSettings
            {
                Enabled = enabled.Checked,
                CreateStartHour = (int)startHour.Value,
                CreateEndHour = (int)endHour.Value,
                TargetCount = (int)target.Value
            };

            SaveNightReserveSettings(updated);
            _nightReserveNextCheckUtc = DateTime.MinValue;

            try
            {
                await ReconcileNightReserveAsync(
                    adoptExistingFresh: updated.Enabled,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warn($"[NIGHT_RESERVE_UI_RECONCILE_WARN] {ex.Message}");
            }

            currentCount.Text =
                $"Hiện có: {GetNightReserveCountSnapshot()} / {updated.TargetCount} PRF dự phòng";

            form.Close();
        };

        form.Controls.Add(panel);
        form.ShowDialog(owner);
    }
}
