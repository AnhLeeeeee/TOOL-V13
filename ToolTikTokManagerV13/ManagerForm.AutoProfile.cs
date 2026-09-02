using System.Diagnostics;
using ToolTikTokV12.Controls;
using ToolTikTokV12.Models;
using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed record AutoProfileQueueItem(TikTokAccountPoolItem Account, string ProfileName, bool ResumeExisting);
    sealed record AutoProfileProcessOutcome(
        bool Success,
        bool Paused,
        string Status,
        string Step,
        string Note,
        bool Skipped = false);

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
        var initialIdentityDone = _accountPoolService.GetIdentityDoneUsernames();
        var initialStartName = DetectNextAutoProfileName();
        var initialAvailable = initialAccounts.Count(x =>
            !x.IsAssigned
            && !string.IsNullOrWhiteSpace(x.Password)
            && !TikTokAccountPoolService.IsBanNoteValue(x.Note)
            && (!initialStates.TryGetValue(x.Id, out var state)
                || (!state.IsReady && !state.IsInProgress)));
        var initialResume = initialAccounts.Count(x =>
            ShouldResumeAutoProfileAccount(
                x,
                initialStates,
                initialIdentityDone,
                initialStartName,
                retryPaused: false,
                out _));

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
            Text = initialStartName,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3)
        };
        ModernDialog.StyleTextInput(nextProfile);

        var saveProfileTemplate = new Button
        {
            Text = "Lưu mẫu",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 16, 2)
        };
        ModernDialog.StyleSecondaryButton(saveProfileTemplate);

        var profileStartPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        profileStartPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));
        profileStartPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 92));
        profileStartPanel.Controls.Add(nextProfile, 0, 0);
        profileStartPanel.Controls.Add(saveProfileTemplate, 1, 0);

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
            Text = "Thử lại CAPTCHA / lỗi cần xử lý",
            Checked = false,
            AutoSize = true,
            Margin = new Padding(0, 8, 14, 0)
        };

        void RefreshResumeEstimate()
        {
            var estimated = initialAccounts.Count(x =>
                ShouldResumeAutoProfileAccount(
                    x,
                    initialStates,
                    initialIdentityDone,
                    nextProfile.Text.Trim(),
                    retryPaused.Checked,
                    out _));

            resumeIncomplete.Text =
                $"Tiếp tục profile tạo dở hợp lệ ({estimated})";
        }

        nextProfile.TextChanged += (_, _) => RefreshResumeEstimate();
        retryPaused.CheckedChanged += (_, _) => RefreshResumeEstimate();
        RefreshResumeEstimate();

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
            Text = "VM chậm: chạy tuần tự 1 profile. FAIL cũ có Tên/ảnh DONE sẽ tự phục hồi; CAPTCHA/cooldown/config chỉ thử lại khi bạn bật tùy chọn.",
            AutoSize = true,
            MaximumSize = new Size(780, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0)
        };

        config.Controls.Add(FieldLabel("Profile bắt đầu"), 0, 0);
        config.Controls.Add(profileStartPanel, 1, 0);
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
            saveProfileTemplate.Enabled = enabled;
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

        saveProfileTemplate.Click += (_, _) =>
        {
            var template =
                nextProfile.Text.Trim();

            if (!TryParseAutoProfileName(
                    template,
                    out var prefix,
                    out var number,
                    out var width))
            {
                ModernDialog.ShowMessage(
                    form,
                    "Mẫu profile không hợp lệ.\r\n\r\n"
                    + "Ví dụ: 01, 30, 001 hoặc acc01.",
                    "Lưu mẫu profile",
                    MessageBoxIcon.Warning);

                nextProfile.Focus();
                nextProfile.SelectAll();
                return;
            }

            try
            {
                RememberAutoProfileSequenceStart(
                    template);

                var nextPreview =
                    FormatAutoProfileName(
                        prefix,
                        number + 1,
                        width);

                status.Text =
                    $"Đã lưu mẫu {template} cho {Path.GetFileName(_accountPoolService.CurrentSourcePath)}. "
                    + $"Tạo {template} thành công xong sẽ tiếp tục {nextPreview}...";

                status.ForeColor =
                    Color.FromArgb(35, 91, 152);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage(
                    form,
                    "Không lưu được mẫu profile.\r\n\r\n"
                    + ex.Message,
                    "Lưu mẫu profile",
                    MessageBoxIcon.Warning);
            }
        };

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
                await RunAccountPoolIoAsync(
                    () =>
                    {
                        if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                            _accountPoolService.ReloadCurrentExcel();
                        _accountPoolService.EnsureAutoColumns();
                    },
                    CancellationToken.None);
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

                var requestedStartName = nextProfile.Text.Trim();
                var resumeIncompleteValue = resumeIncomplete.Checked;
                var retryPausedValue = retryPaused.Checked;

                // V13.7.1: ghi nhớ dãy số RIÊNG cho file Excel hiện tại.
                // Nếu người dùng sửa 73 -> 01 thì lần mở sau sẽ tiếp tục từ dãy 01,02,03...
                // thay vì quay lại số lớn nhất của toàn bộ catalog.
                if (requestedNew > 0)
                    RememberAutoProfileSequenceStart(requestedStartName);

                var queue = await RunAccountPoolIoAsync(
                    () => BuildAutoProfileQueue(
                        requestedNew,
                        requestedStartName,
                        resumeIncompleteValue,
                        retryPausedValue),
                    CancellationToken.None);
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
                var skippedByExcel = 0;
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
                        else if (outcome.Skipped) skippedByExcel++;
                        else pausedOrError++;

                        if (i + 1 < queue.Count)
                        {
                            await WaitAutoProfilePausePointAsync(() => paused, runCts.Token);
                            status.Text = $"Nghỉ {(int)AutoProfileBetweenProfilesDelay.TotalSeconds}s để VM ổn định trước profile tiếp theo...";
                            await Task.Delay(AutoProfileBetweenProfilesDelay, runCts.Token);
                        }
                    }

                    status.Text = $"Hoàn tất. DONE: {success} | BỎ QUA EXCEL: {skippedByExcel} | CHƯA XONG: {pausedOrError}. Lỗi tạm thời giữ PROCESSING; CAPTCHA/cooldown/config mới giữ FAIL.";
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
        // Hàm này luôn được gọi bên trong _accountPoolBackgroundIoGate.
        // Caller đã ReloadCurrentExcel() ngay trước đó, vì vậy Note/Profile đã gán
        // trong account và Auto Profile trong states phản ánh file Excel mới nhất.
        var accounts = _accountPoolService.Load().OrderBy(x => x.SourceRow).ToList();
        var states = _accountPoolService.LoadAutoStates();
        var identityDoneUsers = _accountPoolService.GetIdentityDoneUsernames();
        var queue = new List<AutoProfileQueueItem>();

        bool IsDone(TikTokAccountPoolItem account)
            => states.TryGetValue(account.Id, out var state) && state.IsReady;

        bool IsBan(TikTokAccountPoolItem account)
            => TikTokAccountPoolService.IsBanNoteValue(account.Note);

        if (resumeIncomplete)
        {
            foreach (var account in accounts.Where(x => x.IsAssigned))
            {
                if (!ShouldResumeAutoProfileAccount(
                        account,
                        states,
                        identityDoneUsers,
                        requestedStartName,
                        retryPaused,
                        out var resumeReason))
                {
                    _log.Info(
                        $"[AUTO_PROFILE_RESUME_SKIP] user={account.Username} row={account.SourceRow} profile={account.AssignedProfile} auto={(states.TryGetValue(account.Id, out var skippedState) ? skippedState.Status : "")} reason={resumeReason} note={account.Note}");
                    continue;
                }

                _log.Info(
                    $"[AUTO_PROFILE_RESUME_ALLOW] user={account.Username} row={account.SourceRow} profile={account.AssignedProfile} auto={(states.TryGetValue(account.Id, out var allowedState) ? allowedState.Status : "")} reason={resumeReason}");

                queue.Add(new AutoProfileQueueItem(
                    account,
                    account.AssignedProfile.Trim(),
                    ResumeExisting: true));
            }
            queue = queue
                .OrderBy(x => x.ProfileName, NaturalProfileNameOrder)
                .ThenBy(x => x.Account.SourceRow)
                .ToList();
        }

        if (requestedNew <= 0) return queue;

        // Với profile MỚI, Excel là gate bắt buộc:
        // - Ghi chú=ban => tuyệt đối không gán.
        // - Auto Profile/AutoPrf=DONE => không tạo lại dù Profile đã gán đang trống.
        var eligible = accounts
            .Where(x => !x.IsAssigned && !string.IsNullOrWhiteSpace(x.Password))
            .OrderBy(x => x.SourceRow)
            .ToList();

        foreach (var account in eligible.Where(IsBan))
        {
            _log.Info(
                $"[AUTO_PROFILE_EXCEL_SKIP] mode=new user={account.Username} row={account.SourceRow} reason=NOTE_BAN note={account.Note}");
        }

        foreach (var account in eligible.Where(x => !IsBan(x) && IsDone(x)))
        {
            _log.Info(
                $"[AUTO_PROFILE_EXCEL_SKIP] mode=new user={account.Username} row={account.SourceRow} reason=AUTOPRF_DONE");
        }

        foreach (var account in eligible.Where(x =>
                     !IsBan(x)
                     && states.TryGetValue(x.Id, out var state)
                     && state.IsInProgress))
        {
            _log.Info(
                $"[AUTO_PROFILE_EXCEL_SKIP] mode=new user={account.Username} row={account.SourceRow} reason=AUTOPRF_PROCESSING");
        }

        var candidates = eligible
            .Where(x =>
                !IsBan(x)
                && !IsDone(x)
                && (!states.TryGetValue(x.Id, out var state) || !state.IsInProgress))
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

    bool ShouldResumeAutoProfileAccount(
        TikTokAccountPoolItem account,
        IReadOnlyDictionary<string, TikTokAccountPoolService.TikTokAccountAutoState> states,
        IReadOnlySet<string> identityDoneUsers,
        string requestedStartName,
        bool retryPaused,
        out string reason)
    {
        reason = "";

        if (!account.IsAssigned)
        {
            reason = "NOT_ASSIGNED";
            return false;
        }

        if (TikTokAccountPoolService.IsBanNoteValue(account.Note))
        {
            reason = "NOTE_BAN";
            return false;
        }

        if (states.TryGetValue(account.Id, out var state))
        {
            if (state.IsReady)
            {
                reason = "AUTOPRF_DONE";
                return false;
            }

            if (state.IsPausedOrError)
            {
                // Self-heal: FAIL cũ không còn là khóa vĩnh viễn. Nếu cột Tên/ảnh
                // đã DONE thì profile đủ căn cứ để tiếp tục từ trạng thái thực tế.
                // CAPTCHA/cooldown/config chưa có Tên/ảnh DONE vẫn chỉ retry khi
                // người dùng chủ động bật checkbox.
                if (IsAutoProfileFailAutoRecoverable(account, identityDoneUsers))
                {
                    reason = "AUTOPRF_FAIL_SELF_HEAL";
                    return true;
                }

                reason = retryPaused
                    ? "RETRY_BLOCKED_FAIL_ALLOWED"
                    : "AUTOPRF_FAIL_NEEDS_MANUAL_RETRY";
                return retryPaused;
            }

            // PROCESSING là checkpoint rõ ràng do Auto Profile mới ghi.
            // Đây mới là profile tạo dở thật sự, nên được resume kể cả số profile
            // nhỏ hơn ô "Profile bắt đầu".
            if (state.IsInProgress)
            {
                reason = "AUTOPRF_PROCESSING";
                return true;
            }

            if (!state.IsEmpty)
            {
                reason = "AUTOPRF_NONFINAL";
                return true;
            }
        }

        // Tương thích dữ liệu cũ: trước V13.7.5 patch, checkpoint kỹ thuật không
        // được lưu thành PROCESSING. Khi +auto trống, chỉ coi là profile tạo dở
        // nếu nó nằm trong dãy hiện tại (>= Profile bắt đầu). Account cũ đã dùng
        // như 22/23 trong khi người dùng bắt đầu từ 30 sẽ bị bỏ qua, không chạy lại.
        if (IsAssignedProfileBeforeRequestedStart(
                account.AssignedProfile,
                requestedStartName))
        {
            reason = "LEGACY_ASSIGNED_BEFORE_START_NO_CHECKPOINT";
            return false;
        }

        reason = "LEGACY_NO_CHECKPOINT_AT_OR_AFTER_START";
        return true;
    }

    static bool IsAutoProfileFailAutoRecoverable(
        TikTokAccountPoolItem account,
        IReadOnlySet<string> identityDoneUsers)
    {
        if (!account.IsAssigned)
            return false;

        // Đây là tín hiệu bền vững nhất trong Excel: tên/ảnh đã được xác minh
        // thực tế. Khi Auto Profile còn FAIL vì lỗi bước sau (START/ghi trạng thái),
        // cho phép tự tiếp tục để sửa FAIL -> DONE.
        return identityDoneUsers.Contains((account.Username ?? "").Trim());
    }

    static bool IsAssignedProfileBeforeRequestedStart(
        string assignedProfile,
        string requestedStartName)
    {
        if (!TryParseAutoProfileName(
                (assignedProfile ?? "").Trim(),
                out var assignedPrefix,
                out var assignedNumber,
                out _))
        {
            return false;
        }

        if (!TryParseAutoProfileName(
                (requestedStartName ?? "").Trim(),
                out var startPrefix,
                out var startNumber,
                out _))
        {
            return false;
        }

        if (!assignedPrefix.Equals(
                startPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return assignedNumber < startNumber;
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

            // RE-CHECK ngay trước hành động đầu tiên. Hàng đợi có thể đã được dựng
            // vài giây/phút trước và người dùng có thể sửa Excel trong thời gian đó.
            // Đọc trực tiếp file Excel, không dùng snapshot JSON trong RAM.
            var freshExcel = await RunAccountPoolIoAsync(
                () => _accountPoolService.ReadFreshExcelSnapshot(item.Account.Id),
                ct);

            var gateDecision = "ALLOW";
            var gateReason = "";
            var wasAutoProfileFail = freshExcel.AutoProfileResult.Equals(
                "FAIL",
                StringComparison.OrdinalIgnoreCase);

            if (freshExcel.IsBanBlocked)
            {
                gateDecision = "SKIP";
                gateReason = "NOTE_BAN";
            }
            else if (freshExcel.IsAutoProfileDone)
            {
                gateDecision = "SKIP";
                gateReason = "AUTOPRF_DONE";
            }
            else if (!item.ResumeExisting
                     && freshExcel.IsAutoProfileInProgress)
            {
                gateDecision = "SKIP";
                gateReason = "AUTOPRF_PROCESSING";
            }
            else if (!item.ResumeExisting
                     && !string.IsNullOrWhiteSpace(freshExcel.AssignedProfile)
                     && !freshExcel.AssignedProfile.Equals(
                         item.ProfileName,
                         StringComparison.OrdinalIgnoreCase))
            {
                gateDecision = "SKIP";
                gateReason = "ALREADY_ASSIGNED";
            }
            else if (item.ResumeExisting
                     && !freshExcel.AssignedProfile.Equals(
                         item.ProfileName,
                         StringComparison.OrdinalIgnoreCase))
            {
                gateDecision = "SKIP";
                gateReason = string.IsNullOrWhiteSpace(freshExcel.AssignedProfile)
                    ? "ASSIGNMENT_CLEARED"
                    : "ASSIGNMENT_CHANGED";
            }

            _log.Info(
                $"[AUTO_PROFILE_EXCEL_GATE] profile={item.ProfileName} user={freshExcel.Username} row={freshExcel.SourceRow} note={freshExcel.Note} auto={freshExcel.AutoProfileResult} assigned={freshExcel.AssignedProfile} decision={gateDecision} reason={gateReason}");

            if (gateDecision == "SKIP")
            {
                var message = gateReason switch
                {
                    "NOTE_BAN" => "Bỏ qua: Excel đang ghi chú BAN; không gán tài khoản vào profile.",
                    "AUTOPRF_DONE" => "Bỏ qua: cột Auto Profile/AutoPrf đã DONE; không tạo lại profile.",
                    "AUTOPRF_PROCESSING" => "Bỏ qua: cột Auto Profile/AutoPrf đang PROCESSING; account đang thuộc một luồng Auto Profile khác.",
                    "ALREADY_ASSIGNED" => $"Bỏ qua: Excel đã gán tài khoản cho profile {freshExcel.AssignedProfile}.",
                    "ASSIGNMENT_CLEARED" => "Bỏ qua: mapping Profile đã gán đã bị xóa trong Excel.",
                    "ASSIGNMENT_CHANGED" => $"Bỏ qua: Excel đã đổi mapping sang profile {freshExcel.AssignedProfile}.",
                    _ => "Bỏ qua theo trạng thái Excel mới nhất."
                };

                ui("EXCEL_GATE", message, Color.DarkOrange);
                _log.Info(
                    $"[AUTO_PROFILE_SKIPPED_BY_EXCEL] profile={item.ProfileName} user={freshExcel.Username} row={freshExcel.SourceRow} reason={gateReason}");

                return new AutoProfileProcessOutcome(
                    false,
                    false,
                    "SKIPPED_EXCEL",
                    "EXCEL_GATE",
                    message,
                    Skipped: true);
            }

            if (!item.ResumeExisting)
            {
                step = "ASSIGN_ACCOUNT";
                ui(step, "Đang giữ chỗ tài khoản + profile trong Excel...", Color.RoyalBlue);
                await RunAccountPoolIoAsync(
                    () => _accountPoolService.Assign(item.Account.Id, item.ProfileName),
                    ct);
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

            // Chỉ tiến bộ đếm sau khi profile đã tồn tại thật.
            // Nếu CREATE_PROFILE thất bại và account được trả lại thì số đó vẫn có thể dùng lại.
            AdvanceAutoProfileSequenceAfterCreated(item.ProfileName);

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
            var workerHealthy = await IsAutoProfileWorkerHealthyAsync(ctx);
            var identityDone = !autoRename || await RunAccountPoolIoAsync(
                () => _accountPoolService.IsIdentityDone(item.Account.Username),
                ct);

            // SELF-HEAL nhanh: lần trước Excel còn FAIL nhưng trạng thái thực tế
            // đã hoàn tất (Tên/ảnh DONE + Worker RUNNING/RECOVERING). Không login,
            // không đổi tên, không START lại; chỉ sửa Auto Profile -> DONE và verify.
            if (item.ResumeExisting
                && wasAutoProfileFail
                && identityDone
                && (!autoStart || workerHealthy))
            {
                await SetAutoProfileDoneVerifiedAsync(
                    item.Account.Id,
                    AutoProfileNote("Self-heal: trạng thái thực tế đã hoàn tất; sửa FAIL cũ thành DONE."),
                    ct);
                _autoIdentityHandledSession.Add(item.ProfileName);
                _autoIdentityHandledSession.Add("account:" + item.Account.Username.Trim().ToLowerInvariant());
                ui("DONE", workerHealthy
                    ? "SELF-HEAL — trạng thái thực tế đã RUNNING; Auto Profile đã sửa FAIL → DONE."
                    : "SELF-HEAL — các bước bắt buộc đã hoàn tất; Auto Profile đã sửa FAIL → DONE.",
                    Color.DarkGreen);
                _log.Info($"[AUTO_PROFILE_SELF_HEAL_DONE] profile={item.ProfileName} account={item.Account.Username} workerHealthy={workerHealthy} identityDone={identityDone}");
                return new AutoProfileProcessOutcome(true, false, "READY", "DONE", "Self-heal FAIL -> DONE");
            }

            await WaitAutoProfilePausePointAsync(isPaused, ct);
            step = "WAIT_LOGIN";
            ui(step, "Đang mở Chrome và xác minh đăng nhập...", Color.RoyalBlue);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "WAIT_LOGIN", step,
                AutoProfileNote("Chrome/đăng nhập đang được xác minh; CAPTCHA sẽ dừng riêng profile này."), ct);

            await EnsureAutoProfileLoggedInAsync(ctx, item, ct);
            await SetAutoCheckpointWithRetryAsync(item.Account.Id, "LOGIN_OK", step,
                AutoProfileNote("Đăng nhập TikTok đã xác nhận bằng session."), ct);
            ui("LOGIN_OK", "Đăng nhập thành công; chờ thêm 3 giây cho trang ổn định...", Color.DarkGreen);
            await Task.Delay(AutoProfileAfterLoginDelay, ct);

            if (autoRename)
            {
                identityDone = await RunAccountPoolIoAsync(
                    () => _accountPoolService.IsIdentityDone(item.Account.Username),
                    ct);

                if (identityDone)
                {
                    ui("RENAMED", "Tên/ảnh trong Excel đã DONE — bỏ qua đổi lại.", Color.DarkGreen);
                    _log.Info($"[AUTO_PROFILE_IDENTITY_ALREADY_DONE] profile={item.ProfileName} account={item.Account.Username}");
                }
                else
                {
                    await WaitAutoProfilePausePointAsync(isPaused, ct);
                    step = "RENAME";
                    ui(step, "Đang đổi tên / áp dụng Tên & ảnh TikTok...", Color.RoyalBlue);
                    await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RENAMING", step,
                        AutoProfileNote("Bắt đầu luồng Tên & ảnh TikTok."), ct);
                    await ApplyAutoProfileIdentityAsync(ctx, item, ct);
                    await SetAutoCheckpointWithRetryAsync(item.Account.Id, "RENAMED", step,
                        AutoProfileNote("Tên/ảnh đã xử lý và Excel đã xác minh DONE."), ct);
                    identityDone = true;
                    ui("RENAMED", "Đổi tên/ảnh hoàn tất.", Color.DarkGreen);
                }
            }

            if (autoStart)
            {
                // Nếu Worker đã RUNNING thì đây cũng là self-heal: không gửi START lần nữa.
                workerHealthy = await IsAutoProfileWorkerHealthyAsync(ctx);
                if (workerHealthy)
                {
                    ui("START_TOOL", "Worker đã RUNNING/RECOVERING — bỏ qua Start lại.", Color.DarkGreen);
                    _log.Info($"[AUTO_PROFILE_START_SKIP_ALREADY_RUNNING] profile={item.ProfileName}");
                }
                else
                {
                    await WaitAutoProfilePausePointAsync(isPaused, ct);
                    step = "START_TOOL";
                    ui(step, "Đang Bắt đầu tool...", Color.RoyalBlue);
                    await SetAutoCheckpointWithRetryAsync(item.Account.Id, "STARTING", step,
                        AutoProfileNote("Đang chuẩn bị LIVE/PAGE_READY và Bắt đầu Worker."), ct);
                    await StartAutoProfileWorkerWithRetryAsync(ctx, item, ct, requireIdentityDone: autoRename);
                }
            }

            await SetAutoProfileDoneVerifiedAsync(
                item.Account.Id,
                AutoProfileNote($"Hoàn tất Auto Profile sau {(int)stopwatch.Elapsed.TotalMinutes:00}:{stopwatch.Elapsed.Seconds:00}."),
                ct);
            _autoIdentityHandledSession.Add(item.ProfileName);
            _autoIdentityHandledSession.Add("account:" + item.Account.Username.Trim().ToLowerInvariant());
            ui("DONE", autoStart ? "READY — tool đang chạy." : "READY — chưa tự Bắt đầu theo cấu hình.", Color.DarkGreen);
            _log.Info($"[AUTO_PROFILE_READY] profile={item.ProfileName} account={item.Account.Username} elapsed={stopwatch.Elapsed}");
            return new AutoProfileProcessOutcome(true, false, "READY", "DONE", "Hoàn tất");
        }
        catch (AutoProfilePauseException ex)
        {
            if (!ex.Step.Equals("RENAME", StringComparison.OrdinalIgnoreCase))
                _autoIdentityHandledSession.Add(item.ProfileName);
            else
                _log.Warn($"[AUTO_PROFILE_IDENTITY_RETRYABLE] profile={item.ProfileName} account={item.Account.Username} status={ex.Status}; giữ nguyên cột Tên/ảnh, không ghi FAIL và không khóa scheduler để lần sau xác minh lại TikTok.");

            await TryWriteAutoPauseCheckpointAsync(item.Account.Id, ex.Status, ex.Step, ex.Message, ct);
            ui(ex.Step, ex.Status + " — " + ex.Message, Color.DarkOrange);
            _log.Warn($"[AUTO_PROFILE_PAUSED] profile={item.ProfileName} account={item.Account.Username} status={ex.Status} step={ex.Step} message={ex.Message}");
            return new AutoProfileProcessOutcome(false, true, ex.Status, ex.Step, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _autoIdentityHandledSession.Add(item.ProfileName);
            await TryWriteAutoPauseCheckpointAsync(item.Account.Id, "STOPPED", step,
                "Người dùng dừng hàng đợi; giữ PROCESSING để tự tiếp tục ở lần sau, không coi là FAIL.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            if (!step.Equals("RENAME", StringComparison.OrdinalIgnoreCase))
                _autoIdentityHandledSession.Add(item.ProfileName);
            else
                _log.Warn($"[AUTO_PROFILE_IDENTITY_RETRYABLE] profile={item.ProfileName} account={item.Account.Username}; lỗi RENAME chưa đủ căn cứ ghi FAIL, không khóa scheduler để kiểm tra/retry sau.");

            var captcha = ctx is not null && await TryIsCaptchaVisibleAsync(ctx);
            var status = captcha
                ? (step == "RENAME" ? "PAUSED_CAPTCHA_RENAME" : "PAUSED_CAPTCHA")
                : "PAUSED_ERROR";
            var note = ex.Message;

            if (step == "CREATE_PROFILE" && reservedNow && !_contexts.ContainsKey(item.ProfileName))
            {
                try
                {
                    await RunAccountPoolIoAsync(
                        () => _accountPoolService.ReleaseAccount(item.Account.Id),
                        CancellationToken.None);
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
        if (await RunAccountPoolIoAsync(
                () => _accountPoolService.IsIdentityDone(item.Account.Username),
                ct))
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

        var excelDone = await MarkIdentityDoneVerifiedAsync(
            item.Account.Username, item.ProfileName, ct);
        if (!excelDone.Ok)
            throw new AutoProfilePauseException(
                "PAUSED_RENAME_EXCEL",
                "RENAME",
                "TikTok đã xử lý tên/ảnh nhưng Excel chưa ghi/xác minh được DONE: " + excelDone.Error);

        if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(avatarPath))
        {
            state.LastAvatarByProfile[ctx.Profile.Name] = avatarPath;
            SaveIdentityToolState(state);
        }
        _log.Info($"[AUTO_PROFILE_RENAME_DONE] profile={item.ProfileName} account={item.Account.Username} nameChanged={reply.NameChanged} alreadyConfigured={reply.AlreadyConfigured} Excel=DONE verified=true");
    }

    async Task StartAutoProfileWorkerWithRetryAsync(
        ProfileContext ctx,
        AutoProfileQueueItem item,
        CancellationToken ct,
        bool requireIdentityDone = true)
    {
        // Khi tùy chọn Tên/ảnh bật, START chỉ được phép sau khi Excel đọc lại DONE.
        // Nếu người dùng chủ động tắt Tên/ảnh thì không ép cột này, đúng nghĩa checkbox.
        if (requireIdentityDone)
        {
            var identityDone = await RunAccountPoolIoAsync(
                () => _accountPoolService.IsIdentityDone(item.Account.Username),
                ct);
            if (!identityDone)
                throw new AutoProfilePauseException(
                    "PAUSED_IDENTITY_NOT_DONE",
                    "START_TOOL",
                    "Chặn Bắt đầu: cột Tên/ảnh trong Excel chưa xác nhận DONE.");
        }

        string last = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log.Info($"[AUTO_PROFILE_START] profile={item.ProfileName} attempt={attempt}/2");
            last = await SendCommandAsync(ctx, "start_auto", TimeSpan.FromSeconds(95));
            if (string.Equals(last, "started", StringComparison.OrdinalIgnoreCase))
            {
                if (await IsAutoProfileWorkerHealthyAsync(ctx))
                    return;

                last = "Worker trả started nhưng status chưa xác nhận RUNNING/RECOVERING.";
            }
            if (await TryIsCaptchaVisibleAsync(ctx))
                throw new AutoProfilePauseException("PAUSED_CAPTCHA_START", "START_TOOL", "Phát hiện CAPTCHA trước khi Bắt đầu tool.");
            if (attempt < 2)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new AutoProfilePauseException("PAUSED_START", "START_TOOL", "Worker chưa Bắt đầu được sau 2 lần thử. Phản hồi cuối: " + last);
    }

    async Task<bool> IsAutoProfileWorkerHealthyAsync(ProfileContext ctx)
    {
        try
        {
            if (ctx.Worker is null || ctx.Worker.HasExited)
                return false;

            await RefreshStatusAsync(ctx);
            var state = GetEffectiveRuntimeState(ctx);
            return state is RuntimeStateRunning or RuntimeStateRecovering;
        }
        catch (Exception ex)
        {
            _log.Warn($"[AUTO_PROFILE_HEALTH_CHECK] profile={ctx.Profile.Name} healthy=false error={ex.Message}");
            return false;
        }
    }

    async Task SetAutoProfileDoneVerifiedAsync(string accountId, string note, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SetAutoCheckpointWithRetryAsync(accountId, "READY", "DONE", note, ct);
                var fresh = await RunAccountPoolIoAsync(
                    () => _accountPoolService.ReadFreshExcelSnapshot(accountId),
                    ct);
                if (fresh.IsAutoProfileDone)
                {
                    _log.Info($"[AUTO_PROFILE_DONE_VERIFIED] user={fresh.Username} profile={fresh.AssignedProfile} row={fresh.SourceRow} attempt={attempt}/3");
                    return;
                }

                last = new InvalidOperationException(
                    $"Excel đọc lại Auto Profile='{fresh.AutoProfileResult}' thay vì DONE.");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < 3)
                await Task.Delay(700, ct);
        }

        throw new InvalidOperationException(
            "Đã hoàn tất Auto Profile nhưng không ghi/xác minh được Auto Profile = DONE trong Excel sau 3 lần thử: " + last?.Message,
            last);
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
                await RunAccountPoolIoAsync(
                    () => _accountPoolService.SetAutoState(accountId, status, step, note),
                    ct);
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
        // V13.7.1: Excel hiện tại quyết định dãy số.
        // Catalog toàn hệ thống chỉ dùng để chống trùng tên.
        return DetectNextAutoProfileNameFromCurrentExcel();
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
