using System.Diagnostics;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Models;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed record AutoProfileQueueItem(TikTokAccountPoolItem Account, string ProfileName, bool ResumeExisting);
    sealed record AutoProfileProcessOutcome(bool Success, bool Paused, string Status, string Step, string Note);

    sealed class AutoProfilePauseException : Exception
    {
        public string Status { get; }
        public string Step { get; }
        public AutoProfilePauseException(string status, string step, string message) : base(message)
        {
            Status = status;
            Step = step;
        }
    }

    readonly SemaphoreSlim _autoProfileQueueGate = new(1, 1);
    Form? _autoProfileDialog;

    static readonly TimeSpan AutoProfileAfterCreateDelay = TimeSpan.FromSeconds(2);
    static readonly TimeSpan AutoProfileAfterLoginDelay = TimeSpan.FromSeconds(3);
    static readonly TimeSpan AutoProfileBetweenProfilesDelay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan AutoProfileRetryDelay = TimeSpan.FromSeconds(4);

    void ShowAutoProfileDialog()
    {
        if (_autoProfileDialog is not null && !_autoProfileDialog.IsDisposed)
        {
            try { _autoProfileDialog.Activate(); } catch { }
            return;
        }

        try
        {
            // Excel là nguồn sự thật của cột "Profile đã gán". Nạp lại ngay trước khi
            // dựng hàng đợi để các thay đổi người dùng vừa sửa trong Excel được áp dụng.
            if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                _accountPoolService.ReloadCurrentExcel();
            _accountPoolService.EnsureAutoColumns();
        }
        catch (Exception ex)
        {
            ModernDialog.ShowMessage(this,
                "Không thể chuẩn bị các cột +auto trong file tài khoản:\n\n" + ex.Message,
                "Tạo profile tự động", MessageBoxIcon.Error);
            return;
        }

        var initialAccounts = _accountPoolService.Load();
        var initialStates = _accountPoolService.LoadAutoStates();
        var initialAvailable = initialAccounts.Count(x => !x.IsAssigned && !string.IsNullOrWhiteSpace(x.Password));
        var initialResume = initialAccounts.Count(x =>
            x.IsAssigned
            && initialStates.TryGetValue(x.Id, out var state)
            && !state.IsEmpty
            && !state.IsReady
            && !state.IsPausedOrError);

        var form = new Form
        {
            Text = $"Tạo profile tự động — {AppVersionInfo.Display}",
            Width = 1040,
            Height = 700,
            MinimumSize = new Size(900, 600),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        _autoProfileDialog = form;
        ModernDialog.Apply(form, fixedDialog: false);

        Label FieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 7, 8, 0),
            ForeColor = Color.FromArgb(46, 65, 88)
        };

        var source = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(14, 10, 14, 4),
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            Text = "Excel: " + _accountPoolService.CurrentSourcePath
        };

        var config = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            Padding = new Padding(14, 8, 14, 8),
            ColumnCount = 4,
            RowCount = 3,
            BackColor = ModernDialog.Canvas
        };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var nextProfile = new TextBox
        {
            Text = DetectNextAutoProfileName(),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 16, 3)
        };
        ModernDialog.StyleTextInput(nextProfile);

        var count = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 200,
            Value = initialAvailable > 0 ? 1 : 0,
            Dock = DockStyle.Left,
            Width = 100,
            Margin = new Padding(0, 3, 16, 3)
        };

        var resumeIncomplete = new CheckBox
        {
            Text = $"Tiếp tục profile tạo dở ({initialResume})",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 8, 14, 0)
        };
        var retryPaused = new CheckBox
        {
            Text = "Thử lại cả profile lỗi/CAPTCHA đã ghi",
            Checked = false,
            AutoSize = true,
            Margin = new Padding(0, 8, 14, 0)
        };
        var autoRename = new CheckBox
        {
            Text = "Tự đổi tên bằng cấu hình Tên & ảnh TikTok",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 8, 14, 0)
        };
        var autoStart = new CheckBox
        {
            Text = "Tự Bắt đầu tool sau khi hoàn tất",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 8, 14, 0)
        };

        var availableLabel = new Label
        {
            Text = $"Tài khoản chưa gán + có mật khẩu: {initialAvailable}",
            AutoSize = true,
            ForeColor = Color.FromArgb(35, 91, 152),
            Margin = new Padding(0, 8, 0, 0)
        };
        var vmHint = new Label
        {
            Text = "VM chậm: chạy tuần tự 1 profile; chờ điều kiện thay vì delay cứng; lỗi/CAPTCHA được ghi Excel rồi bỏ qua profile đó.",
            AutoSize = true,
            MaximumSize = new Size(780, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0)
        };

        config.Controls.Add(FieldLabel("Profile bắt đầu"), 0, 0);
        config.Controls.Add(nextProfile, 1, 0);
        config.Controls.Add(FieldLabel("Số profile mới"), 2, 0);
        config.Controls.Add(count, 3, 0);
        config.Controls.Add(resumeIncomplete, 0, 1);
        config.SetColumnSpan(resumeIncomplete, 2);
        config.Controls.Add(retryPaused, 2, 1);
        config.SetColumnSpan(retryPaused, 2);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        options.Controls.Add(autoRename);
        options.Controls.Add(autoStart);
        options.Controls.Add(availableLabel);
        options.Controls.Add(vmHint);
        config.Controls.Add(options, 0, 2);
        config.SetColumnSpan(options, 4);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = false
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Loại", FillWeight = 12 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "profile", HeaderText = "Profile", FillWeight = 15 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "account", HeaderText = "Tài khoản", FillWeight = 25 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "step", HeaderText = "Bước", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "result", HeaderText = "Trạng thái / ghi chú", FillWeight = 48 });

        var status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(14, 8, 14, 4),
            ForeColor = Color.FromArgb(46, 65, 88),
            Text = "Sẵn sàng."
        };

        var start = new Button { Text = "Bắt đầu", Size = new Size(120, 42) };
        var pause = new Button { Text = "Tạm dừng", Size = new Size(120, 42), Enabled = false };
        var stop = new Button { Text = "Dừng", Size = new Size(110, 42), Enabled = false };
        var close = new Button { Text = "Đóng", Size = new Size(100, 42) };
        ModernDialog.StylePrimaryButton(start);
        ModernDialog.StyleSecondaryButton(pause);
        ModernDialog.StyleSecondaryButton(stop);
        ModernDialog.StyleSecondaryButton(close);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(14, 10, 14, 12), BackColor = ModernDialog.Canvas };
        var footerFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        footerFlow.Controls.Add(close);
        footerFlow.Controls.Add(stop);
        footerFlow.Controls.Add(pause);
        footerFlow.Controls.Add(start);
        footer.Controls.Add(footerFlow);

        form.Controls.Add(grid);
        form.Controls.Add(status);
        form.Controls.Add(footer);
        form.Controls.Add(config);
        form.Controls.Add(source);

        CancellationTokenSource? runCts = null;
        var running = false;
        var paused = false;

        void SetInputsEnabled(bool enabled)
        {
            nextProfile.Enabled = enabled;
            count.Enabled = enabled;
            resumeIncomplete.Enabled = enabled;
            retryPaused.Enabled = enabled;
            autoRename.Enabled = enabled;
            autoStart.Enabled = enabled;
            start.Enabled = enabled;
            close.Enabled = enabled;
            pause.Enabled = !enabled;
            stop.Enabled = !enabled;
        }

        void UpdateGridRow(AutoProfileQueueItem item, string stepText, string resultText, Color color)
        {
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, item));
            if (row is null) return;
            row.Cells["step"].Value = stepText;
            row.Cells["result"].Value = resultText;
            row.DefaultCellStyle.ForeColor = color;
            if (grid.CurrentCell is null || !row.Selected)
            {
                grid.ClearSelection();
                row.Selected = true;
                grid.CurrentCell = row.Cells["profile"];
            }
        }

        pause.Click += (_, _) =>
        {
            if (!running) return;
            paused = !paused;
            pause.Text = paused ? "Tiếp tục" : "Tạm dừng";
            status.Text = paused ? "Đã tạm dừng hàng đợi; profile đang ở checkpoint an toàn tiếp theo." : "Đã tiếp tục hàng đợi.";
        };

        stop.Click += (_, _) =>
        {
            if (!running) return;
            stop.Enabled = false;
            status.Text = "Đang yêu cầu dừng sau bước hiện tại...";
            try { runCts?.Cancel(); } catch { }
        };

        close.Click += (_, _) => form.Close();

        start.Click += async (_, _) =>
        {
            if (running) return;
            try
            {
                // Người dùng có thể sửa Excel trong lúc cửa sổ Auto Profile đang mở.
                // Nạp lại lần cuối trước khi chọn account để tuyệt đối không lấy dòng đã được gán.
                if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                    _accountPoolService.ReloadCurrentExcel();
                _accountPoolService.EnsureAutoColumns();
                var requestedNew = (int)count.Value;
                if (requestedNew > 0 && !TryParseAutoProfileName(nextProfile.Text.Trim(), out _, out _, out _))
                    throw new InvalidOperationException("Tên profile bắt đầu phải kết thúc bằng số, ví dụ 46 hoặc a02.");

                if (autoRename.Checked)
                {
                    var identity = LoadIdentityToolState();
                    var names = SplitIdentityNames(identity.NamesText);
                    if (names.Count == 0)
                        throw new InvalidOperationException("Tự đổi tên đang bật nhưng danh sách tên trong mục ‘Tên & ảnh TikTok’ đang trống.");
                }

                var queue = BuildAutoProfileQueue(
                    requestedNew,
                    nextProfile.Text.Trim(),
                    resumeIncomplete.Checked,
                    retryPaused.Checked);
                if (queue.Count == 0)
                    throw new InvalidOperationException("Không có profile tạo dở cần tiếp tục và không có tài khoản chưa gán phù hợp để tạo mới.");

                grid.Rows.Clear();
                foreach (var item in queue)
                {
                    var index = grid.Rows.Add(
                        item.ResumeExisting ? "Tiếp tục" : "Mới",
                        item.ProfileName,
                        $"Dòng {item.Account.SourceRow}: {item.Account.Username}",
                        "WAITING",
                        "Chờ tới lượt");
                    grid.Rows[index].Tag = item;
                }

                running = true;
                paused = false;
                pause.Text = "Tạm dừng";
                runCts = new CancellationTokenSource();
                SetInputsEnabled(false);
                status.Text = $"Bắt đầu hàng đợi {queue.Count} profile — xử lý tuần tự 1 profile/lần.";

                await _autoProfileQueueGate.WaitAsync(runCts.Token);
                var success = 0;
                var pausedOrError = 0;
                try
                {
                    for (var i = 0; i < queue.Count; i++)
                    {
                        var item = queue[i];
                        await WaitAutoProfilePausePointAsync(() => paused, runCts.Token);
                        status.Text = $"Đang xử lý {i + 1}/{queue.Count}: {item.ProfileName} — {item.Account.Username}";
                        UpdateGridRow(item, "PREPARE", "Đang chuẩn bị...", Color.RoyalBlue);

                        AutoProfileProcessOutcome outcome;
                        try
                        {
                            outcome = await ProcessAutoProfileQueueItemAsync(
                                item,
                                autoRename.Checked,
                                autoStart.Checked,
                                () => paused,
                                runCts.Token,
                                (stepText, resultText, color) => UpdateGridRow(item, stepText, resultText, color));
                        }
                        catch (OperationCanceledException)
                        {
                            UpdateGridRow(item, "STOPPED", "Đã dừng theo yêu cầu; checkpoint đã giữ để tiếp tục sau.", Color.DarkOrange);
                            throw;
                        }

                        if (outcome.Success) success++;
                        else pausedOrError++;

                        if (i + 1 < queue.Count)
                        {
                            await WaitAutoProfilePausePointAsync(() => paused, runCts.Token);
                            status.Text = $"Nghỉ {(int)AutoProfileBetweenProfilesDelay.TotalSeconds}s để VM ổn định trước profile tiếp theo...";
                            await Task.Delay(AutoProfileBetweenProfilesDelay, runCts.Token);
                        }
                    }

                    status.Text = $"Hoàn tất. READY: {success} | Tạm dừng/lỗi: {pausedOrError}. Chi tiết đã ghi trong +auto/+auto_step/+auto_note.";
                }
                finally
                {
                    _autoProfileQueueGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                status.Text = "Đã dừng hàng đợi. Profile đang dở được giữ checkpoint trong Excel để tiếp tục lần sau.";
            }
            catch (Exception ex)
            {
                _log.Error("[AUTO_PROFILE_MANAGER] " + ex);
                status.Text = "Lỗi hàng đợi: " + ex.Message;
                ModernDialog.ShowMessage(form, ex.Message, "Tạo profile tự động", MessageBoxIcon.Error);
            }
            finally
            {
                running = false;
                paused = false;
                try { runCts?.Dispose(); } catch { }
                runCts = null;
                SetInputsEnabled(true);
                pause.Text = "Tạm dừng";
                stop.Enabled = false;
            }
        };

        form.FormClosing += (_, e) =>
        {
            if (!running || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            ModernDialog.ShowMessage(form,
                "Hàng đợi Auto Profile đang chạy. Hãy bấm Dừng và chờ bước hiện tại kết thúc trước khi đóng cửa sổ.",
                "Tạo profile tự động", MessageBoxIcon.Information);
        };
        form.FormClosed += (_, _) => _autoProfileDialog = null;
        form.Shown += (_, _) => ModernDialog.FitToWorkingArea(form);
        form.ShowDialog(this);
    }

    List<AutoProfileQueueItem> BuildAutoProfileQueue(int requestedNew, string requestedStartName, bool resumeIncomplete, bool retryPaused)
    {
        var accounts = _accountPoolService.Load().OrderBy(x => x.SourceRow).ToList();
        var states = _accountPoolService.LoadAutoStates();
        var queue = new List<AutoProfileQueueItem>();

        if (resumeIncomplete)
        {
            foreach (var account in accounts.Where(x => x.IsAssigned))
            {
                if (!states.TryGetValue(account.Id, out var state) || state.IsEmpty || state.IsReady) continue;
                if (state.IsPausedOrError && !retryPaused) continue;
                queue.Add(new AutoProfileQueueItem(account, account.AssignedProfile.Trim(), ResumeExisting: true));
            }
            queue = queue
                .OrderBy(x => x.ProfileName, NaturalProfileNameOrder)
                .ThenBy(x => x.Account.SourceRow)
                .ToList();
        }

        if (requestedNew <= 0) return queue;

        var candidates = accounts
            .Where(x => !x.IsAssigned && !string.IsNullOrWhiteSpace(x.Password))
            .OrderBy(x => x.SourceRow)
            .Take(requestedNew)
            .ToList();
        if (candidates.Count == 0) return queue;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _profileService.Load().Profiles) usedNames.Add(profile.Name);
        foreach (var account in accounts.Where(x => x.IsAssigned)) usedNames.Add(account.AssignedProfile.Trim());
        foreach (var existing in queue) usedNames.Add(existing.ProfileName);

        var generatedNames = GenerateAutoProfileNames(requestedStartName, candidates.Count, usedNames);
        for (var i = 0; i < candidates.Count; i++)
            queue.Add(new AutoProfileQueueItem(candidates[i], generatedNames[i], ResumeExisting: false));
        return queue;
    }

    async Task<AutoProfileProcessOutcome> ProcessAutoProfileQueueItemAsync(
        AutoProfileQueueItem item,
        bool autoRename,
        bool autoStart,
        Func<bool> isPaused,
        CancellationToken ct,
        Action<string, string, Color> ui)
    {
        var stopwatch = Stopwatch.StartNew();
        var step = "RESERVE";
        var reservedNow = false;
        ProfileContext? ctx = null;
        _autoIdentityInFlight.Add(item.ProfileName);
        try
        {
            await WaitAutoProfilePausePointAsync(isPaused, ct);

            if (!item.ResumeExisting)
            {
                step = "ASSIGN_ACCOUNT";
                ui(step, "Đang giữ chỗ tài khoản + profile trong Excel...", Color.RoyalBlue);
                _accountPoolService.Assign(item.Account.Id, item.ProfileName);
                reservedNow = true;
                await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RESERVED", step,
                    AutoProfileNote($"Đã giữ chỗ profile {item.ProfileName}."), ct);
            }
            else
            {
                await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RESUMING", "RESUME",
                    AutoProfileNote("Tiếp tục profile từ checkpoint cũ; trạng thái thực tế sẽ được xác minh lại."), ct);
            }

            await WaitAutoProfilePausePointAsync(isPaused, ct);
            step = "CREATE_PROFILE";
            ui(step, "Đang tạo/kiểm tra profile...", Color.RoyalBlue);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "CREATING", step,
                AutoProfileNote("Đang tạo hoặc xác minh profile trong catalog."), ct);

            var ensured = EnsureAutoProfileExists(item);
            ctx = ensured.Context;
            if (ensured.CreatedNow)
                MarkAutoReplacementProfileCreated(item.ProfileName);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "CREATED", step,
                AutoProfileNote(ensured.CreatedNow ? "Đã tạo profile." : "Profile đã tồn tại; tiếp tục xác minh."), ct);

            ui(step, "Profile sẵn sàng; chờ VM ổn định 2 giây...", Color.RoyalBlue);
            await Task.Delay(AutoProfileAfterCreateDelay, ct);

            await WaitAutoProfilePausePointAsync(isPaused, ct);
            step = "OPEN_WORKER";
            ui(step, "Đang mở Worker...", Color.RoyalBlue);
            var workerOpened = await OpenProfileAsync(ctx, $"Auto Profile: {item.ProfileName}");
            if (!workerOpened && (ctx.Worker is null || ctx.Worker.HasExited))
                throw new AutoProfilePauseException("PAUSED_ERROR", step, "Không mở được Worker của profile.");

            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
            try { await RefreshStatusAsync(ctx); } catch { }

            await WaitAutoProfilePausePointAsync(isPaused, ct);
            step = "WAIT_LOGIN";
            ui(step, "Đang mở Chrome và chờ đăng nhập...", Color.RoyalBlue);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "WAIT_LOGIN", step,
                AutoProfileNote("Chrome/đăng nhập đang được xử lý; CAPTCHA sẽ dừng riêng profile này."), ct);

            await EnsureAutoProfileLoggedInAsync(ctx, item, ct);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "LOGIN_OK", step,
                AutoProfileNote("Đăng nhập TikTok đã xác nhận bằng session."), ct);
            ui("LOGIN_OK", "Đăng nhập thành công; chờ thêm 3 giây cho trang ổn định...", Color.DarkGreen);
            await Task.Delay(AutoProfileAfterLoginDelay, ct);

            if (autoRename)
            {
                await WaitAutoProfilePausePointAsync(isPaused, ct);
                step = "RENAME";
                ui(step, "Đang đổi tên / áp dụng Tên & ảnh TikTok...", Color.RoyalBlue);
                await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RENAMING", step,
                    AutoProfileNote("Bắt đầu luồng Tên & ảnh TikTok."), ct);
                await ApplyAutoProfileIdentityAsync(ctx, item, ct);
                await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RENAMED", step,
                    AutoProfileNote("Tên/ảnh đã xử lý; trang đã được refresh ổn định theo luồng hiện tại."), ct);
                ui("RENAMED", "Đổi tên/ảnh hoàn tất.", Color.DarkGreen);
            }

            if (autoStart)
            {
                await WaitAutoProfilePausePointAsync(isPaused, ct);
                step = "START_TOOL";
                ui(step, "Đang Bắt đầu tool...", Color.RoyalBlue);
                await SetAutoCheckpointWithRetryAsync(item.Account.Id, "STARTING", step,
                    AutoProfileNote("Đang chuẩn bị LIVE/PAGE_READY và Bắt đầu Worker."), ct);
                await StartAutoProfileWorkerWithRetryAsync(ctx, item, ct);
            }

            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "READY", "DONE",
                AutoProfileNote($"Hoàn tất Auto Profile sau {(int)stopwatch.Elapsed.TotalMinutes:00}:{stopwatch.Elapsed.Seconds:00}."), ct);
            _autoIdentityHandledSession.Add(item.ProfileName);
            _autoIdentityHandledSession.Add("account:" + item.Account.Username.Trim().ToLowerInvariant());
            ui("DONE", autoStart ? "READY — tool đang chạy." : "READY — chưa tự Bắt đầu theo cấu hình.", Color.DarkGreen);
            _log.Info($"[AUTO_PROFILE_READY] profile={item.ProfileName} account={item.Account.Username} elapsed={stopwatch.Elapsed}");
            return new AutoProfileProcessOutcome(true, false, "READY", "DONE", "Hoàn tất");
        }
        catch (AutoProfilePauseException ex)
        {
            _autoIdentityHandledSession.Add(item.ProfileName);
            await TryWriteAutoPauseCheckpointAsync(item.Account.Id, ex.Status, ex.Step, ex.Message, ct);
            ui(ex.Step, ex.Status + " — " + ex.Message, Color.DarkOrange);
            _log.Warn($"[AUTO_PROFILE_PAUSED] profile={item.ProfileName} account={item.Account.Username} status={ex.Status} step={ex.Step} message={ex.Message}");
            return new AutoProfileProcessOutcome(false, true, ex.Status, ex.Step, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _autoIdentityHandledSession.Add(item.ProfileName);
            await TryWriteAutoPauseCheckpointAsync(item.Account.Id, "STOPPED", step,
                "Người dùng dừng hàng đợi; có thể tiếp tục profile này ở lần sau.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _autoIdentityHandledSession.Add(item.ProfileName);
            var captcha = ctx is not null && await TryIsCaptchaVisibleAsync(ctx);
            var status = captcha
                ? (step == "RENAME" ? "PAUSED_CAPTCHA_RENAME" : "PAUSED_CAPTCHA")
                : "PAUSED_ERROR";
            var note = ex.Message;

            if (step == "CREATE_PROFILE" && reservedNow && !_contexts.ContainsKey(item.ProfileName))
            {
                try
                {
                    _accountPoolService.ReleaseAccount(item.Account.Id);
                    note += " | Đã trả tài khoản về trạng thái chưa gán vì profile chưa được tạo hoàn chỉnh.";
                }
                catch (Exception releaseEx) { note += " | Không trả được tài khoản: " + releaseEx.Message; }
            }

            await TryWriteAutoPauseCheckpointAsync(item.Account.Id, status, step, note, CancellationToken.None);
            ui(step, status + " — " + note, captcha ? Color.DarkOrange : Color.Firebrick);
            _log.Warn($"[AUTO_PROFILE_ERROR] profile={item.ProfileName} account={item.Account.Username} step={step} {ex}");
            return new AutoProfileProcessOutcome(false, true, status, step, note);
        }
        finally
        {
            // Chỉ khóa scheduler Tên/ảnh nền trong lúc Auto Profile đang điều phối profile này.
            _autoIdentityInFlight.Remove(item.ProfileName);
        }
    }

    (ProfileContext Context, bool CreatedNow) EnsureAutoProfileExists(AutoProfileQueueItem item)
    {
        var catalog = _profileService.Load();
        var existing = catalog.Profiles.FirstOrDefault(p => p.Name.Equals(item.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var dataRoot = _profileService.ResolveDataRoot(existing);
            Directory.CreateDirectory(dataRoot);
            _tiktokAuthService.Save(dataRoot, item.Account.Username, item.Account.Password, item.Account.TotpSecret, autoLogin: true);
            RefreshContextsFromCatalog(catalog);
            if (!_contexts.TryGetValue(existing.Name, out var existingContext))
                throw new InvalidOperationException("Không tạo được context Manager cho profile đã tồn tại: " + existing.Name);
            return (existingContext, false);
        }

        TikTokProfileEntry? entry = null;
        try
        {
            var normalized = ValidateNewProfileName(item.ProfileName, catalog);
            entry = _profileService.CreateManagedProfile(normalized);
            _chromeProfileNameSync.SyncBeforeLaunch(entry.ProfilePath, entry.Name);
            var dataRoot = _profileService.ResolveDataRoot(entry);
            Directory.CreateDirectory(dataRoot);
            ApplyManagerDefaultConfigToNewProfile(dataRoot);
            _tiktokAuthService.Save(dataRoot, item.Account.Username, item.Account.Password, item.Account.TotpSecret, autoLogin: true);
            catalog.Profiles.Add(entry);
            catalog.SelectedProfile = entry.Name;
            _profileService.EnsurePorts(catalog.Profiles);
            _profileService.SaveWithBackup(catalog);
            ReloadCatalog();
            if (!_contexts.TryGetValue(entry.Name, out var context))
                throw new InvalidOperationException("Đã tạo profile nhưng Manager chưa nạp được context: " + entry.Name);
            _log.Info($"[AUTO_PROFILE_CREATED] profile={entry.Name} account={item.Account.Username}");
            return (context, true);
        }
        catch
        {
            if (entry is not null)
            {
                var rollback = TryRollbackCreatedProfile(entry);
                if (!string.IsNullOrWhiteSpace(rollback))
                    _log.Warn($"[AUTO_PROFILE_CREATE_ROLLBACK] profile={entry.Name} error={rollback}");
            }
            try { ReloadCatalog(); } catch { }
            throw;
        }
    }

    async Task EnsureAutoProfileLoggedInAsync(ProfileContext ctx, AutoProfileQueueItem item, CancellationToken ct)
    {
        try { await RefreshStatusAsync(ctx); } catch { }
        if (string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            var readyExisting = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(7));
            if (string.Equals(readyExisting, "ready", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"[AUTO_PROFILE_LOGIN_SKIP] profile={item.ProfileName} reason=session_already_ready");
                return;
            }
        }

        string last = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log.Info($"[AUTO_PROFILE_LAUNCH] profile={item.ProfileName} attempt={attempt}/2");
            last = await SendCommandAsync(ctx, "launch_auto", TimeSpan.FromSeconds(105));
            if (string.Equals(last, "captcha_required", StringComparison.OrdinalIgnoreCase))
                throw new AutoProfilePauseException("PAUSED_CAPTCHA_LOGIN", "WAIT_LOGIN", "Phát hiện CAPTCHA khi đăng nhập. Chrome được giữ nguyên để xử lý thủ công sau.");
            if (string.Equals(last, "totp_required", StringComparison.OrdinalIgnoreCase))
                throw new AutoProfilePauseException("PAUSED_LOGIN_2FA", "WAIT_LOGIN", "TikTok yêu cầu 2FA nhưng tài khoản chưa có secret TOTP hợp lệ.");
            if (string.Equals(last, "login_required", StringComparison.OrdinalIgnoreCase))
                throw new AutoProfilePauseException("PAUSED_LOGIN_CONFIG", "WAIT_LOGIN", "Worker báo chưa có thông tin tự đăng nhập.");
            if (string.Equals(last, "login_failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(last, "login_form_not_found", StringComparison.OrdinalIgnoreCase))
                throw new AutoProfilePauseException("PAUSED_LOGIN", "WAIT_LOGIN", "Đăng nhập chưa thành công: " + last);

            if (string.Equals(last, "opened", StringComparison.OrdinalIgnoreCase))
            {
                if (await WaitForAutoProfileIdentityReadyAsync(ctx, TimeSpan.FromSeconds(25), ct)) return;
                if (await TryIsCaptchaVisibleAsync(ctx))
                    throw new AutoProfilePauseException("PAUSED_CAPTCHA_LOGIN", "WAIT_LOGIN", "Trang đăng nhập xuất hiện CAPTCHA sau khi Chrome đã mở.");
            }

            if (attempt < 2)
                await Task.Delay(AutoProfileRetryDelay, ct);
        }

        throw new AutoProfilePauseException("PAUSED_LOGIN", "WAIT_LOGIN", "Chrome/TikTok chưa sẵn sàng sau 2 lần thử. Phản hồi cuối: " + last);
    }

    async Task<bool> WaitForAutoProfileIdentityReadyAsync(ProfileContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ready = await SendCommandAsync(ctx, "identity_ready", TimeSpan.FromSeconds(7));
                if (string.Equals(ready, "ready", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(ready, "not_connected", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    async Task ApplyAutoProfileIdentityAsync(ProfileContext ctx, AutoProfileQueueItem item, CancellationToken ct)
    {
        if (_accountPoolService.IsIdentityDone(item.Account.Username))
        {
            _log.Info($"[AUTO_PROFILE_RENAME_SKIP_DONE] profile={item.ProfileName} account={item.Account.Username}");
            return;
        }

        var state = LoadIdentityToolState();
        var names = SplitIdentityNames(state.NamesText);
        if (names.Count == 0)
            throw new AutoProfilePauseException("PAUSED_RENAME_CONFIG", "RENAME", "Danh sách tên trong ‘Tên & ảnh TikTok’ đang trống.");

        var displayName = names.Count == 1 ? names[0] : names[Random.Shared.Next(names.Count)];
        var avatarPath = "";
        if (state.UpdateAvatar && Directory.Exists(state.ImageFolder))
        {
            var images = Directory.EnumerateFiles(state.ImageFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(x => new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }
                    .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (images.Count > 0)
            {
                var candidates = images;
                if (state.AvoidLastAvatar && images.Count > 1 && state.LastAvatarByProfile.TryGetValue(ctx.Profile.Name, out var previous))
                    candidates = images.Where(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase)).ToList();
                avatarPath = candidates[Random.Shared.Next(candidates.Count)];
            }
        }
        var bio = state.UpdateBio ? (state.BioText ?? "").Trim() : "";

        var reply = await UpdateTikTokIdentityAsync(
            ctx,
            displayName,
            avatarPath,
            bio,
            skipIfNameCooldown: true,
            resumeAutomation: false,
            knownDisplayNames: names,
            verifyExistingState: true,
            workerTimeout: TimeSpan.FromSeconds(150));

        if (!reply.Ok)
        {
            if (await TryIsCaptchaVisibleAsync(ctx))
                throw new AutoProfilePauseException("PAUSED_CAPTCHA_RENAME", "RENAME", "Phát hiện CAPTCHA trong lúc đổi tên. Bỏ qua profile này để xử lý thủ công sau.");
            throw new AutoProfilePauseException("PAUSED_RENAME", "RENAME", string.IsNullOrWhiteSpace(reply.Error) ? "Đổi tên/ảnh không thành công." : reply.Error);
        }
        if (reply.NameCooldown)
            throw new AutoProfilePauseException("PAUSED_RENAME_COOLDOWN", "RENAME", "TikTok đang giới hạn thời gian đổi biệt danh; giữ profile để xử lý sau.");
        if (reply.Skipped && !reply.AlreadyConfigured)
            throw new AutoProfilePauseException("PAUSED_RENAME", "RENAME", string.IsNullOrWhiteSpace(reply.Message) ? "TikTok bỏ qua thao tác đổi tên." : reply.Message);

        _accountPoolService.MarkIdentityDone(item.Account.Username);
        if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(avatarPath))
        {
            state.LastAvatarByProfile[ctx.Profile.Name] = avatarPath;
            SaveIdentityToolState(state);
        }
        _log.Info($"[AUTO_PROFILE_RENAME_DONE] profile={item.ProfileName} account={item.Account.Username} nameChanged={reply.NameChanged} alreadyConfigured={reply.AlreadyConfigured}");
    }

    async Task StartAutoProfileWorkerWithRetryAsync(ProfileContext ctx, AutoProfileQueueItem item, CancellationToken ct)
    {
        string last = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log.Info($"[AUTO_PROFILE_START] profile={item.ProfileName} attempt={attempt}/2");
            last = await SendCommandAsync(ctx, "start_auto", TimeSpan.FromSeconds(95));
            if (string.Equals(last, "started", StringComparison.OrdinalIgnoreCase))
            {
                try { await RefreshStatusAsync(ctx); } catch { }
                return;
            }
            if (await TryIsCaptchaVisibleAsync(ctx))
                throw new AutoProfilePauseException("PAUSED_CAPTCHA_START", "START_TOOL", "Phát hiện CAPTCHA trước khi Bắt đầu tool.");
            if (attempt < 2)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new AutoProfilePauseException("PAUSED_START", "START_TOOL", "Worker chưa Bắt đầu được sau 2 lần thử. Phản hồi cuối: " + last);
    }

    async Task<bool> TryIsCaptchaVisibleAsync(ProfileContext ctx)
    {
        try
        {
            var result = await SendCommandAsync(ctx, "captcha_check", TimeSpan.FromSeconds(7));
            return string.Equals(result, "captcha", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    async Task SetAutoCheckpointWithRetryAsync(string accountId, string status, string step, string note, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _accountPoolService.SetAutoState(accountId, status, step, note);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 4) await Task.Delay(650, ct);
            }
        }
        throw new InvalidOperationException("Không ghi được checkpoint +auto vào Excel sau 4 lần thử: " + last?.Message, last);
    }

    async Task TryWriteAutoPauseCheckpointAsync(string accountId, string status, string step, string note, CancellationToken ct)
    {
        try
        {
            await SetAutoCheckpointWithRetryAsync(accountId, status, step, AutoProfileNote(note), ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_PROFILE_CHECKPOINT_FAILED] accountId={accountId} status={status} step={step} error={ex.Message}");
        }
    }

    static async Task WaitAutoProfilePausePointAsync(Func<bool> isPaused, CancellationToken ct)
    {
        while (isPaused())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    string DetectNextAutoProfileName()
    {
        var names = _profileService.Load().Profiles
            .Select(x => x.Name)
            .OrderBy(x => x, NaturalProfileNameOrder)
            .ToList();
        for (var i = names.Count - 1; i >= 0; i--)
        {
            if (!TryParseAutoProfileName(names[i], out var prefix, out var number, out var width)) continue;
            return FormatAutoProfileName(prefix, number + 1, width);
        }
        return "1";
    }

    static bool TryParseAutoProfileName(string value, out string prefix, out int number, out int width)
    {
        prefix = "";
        number = 0;
        width = 0;
        value = (value ?? "").Trim();
        if (value.Length == 0) return false;
        var index = value.Length - 1;
        while (index >= 0 && char.IsDigit(value[index])) index--;
        var digitStart = index + 1;
        if (digitStart >= value.Length) return false;
        var digits = value[digitStart..];
        if (!int.TryParse(digits, out number) || number < 0) return false;
        prefix = value[..digitStart];
        width = digits.Length;
        return true;
    }

    static string FormatAutoProfileName(string prefix, int number, int width)
    {
        var digits = width > 1 ? number.ToString("D" + width) : number.ToString();
        return prefix + digits;
    }

    static List<string> GenerateAutoProfileNames(string startName, int count, HashSet<string> used)
    {
        if (!TryParseAutoProfileName(startName, out var prefix, out var number, out var width))
            throw new InvalidOperationException("Tên profile bắt đầu phải kết thúc bằng số, ví dụ 46 hoặc a02.");
        var result = new List<string>(count);
        var current = number;
        var guard = 0;
        while (result.Count < count)
        {
            if (++guard > 10000) throw new InvalidOperationException("Không tìm được tên profile trống tiếp theo.");
            var candidate = FormatAutoProfileName(prefix, current, width);
            current++;
            if (used.Contains(candidate)) continue;
            used.Add(candidate);
            result.Add(candidate);
        }
        return result;
    }

    static List<string> SplitIdentityNames(string text)
        => (text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string AutoProfileNote(string message)
    {
        var cleaned = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleaned.Length > 420) cleaned = cleaned[..420] + "...";
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {cleaned}";
    }
}
