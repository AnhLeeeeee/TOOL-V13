using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CommentVisibilityMonitor;

internal sealed partial class MainForm : Form
{
    const int TelemetryPort = 47771;
    readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "CommentCheckData");
    readonly UdpClient _udp = new(TelemetryPort);
    readonly CancellationTokenSource _cts = new();
    readonly ObserverChromeSession _observer;
    readonly Dictionary<string, ProfileState> _profiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PendingSend> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, LiveLatencyState> _liveLatency = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    // User rule: mỗi comment được Observer chờ/quét tối đa cố định 30 giây.
    // Giữ cùng một giá trị min/max để adaptive learning không rút ngắn timeout
    // khi TikTok hiển thị comment chậm.
    const double InitialCommentTimeoutSeconds = 30.0;
    const double MinCommentTimeoutSeconds = 30.0;
    const double MaxCommentTimeoutSeconds = 30.0;
    const double CommentTimeoutBufferSeconds = 0.0;
    const int MaxLatencySamplesPerLive = 20;
    const int ProfileCheckMinutes = 5;
    static readonly TimeSpan ProfileCheckDuration = TimeSpan.FromMinutes(ProfileCheckMinutes);
    static readonly TimeSpan SessionPendingGrace = TimeSpan.FromSeconds(5);

    readonly Label _observerState = new() { AutoSize = true, Text = "Observer: chưa mở" };
    readonly Label _targetState = new() { AutoSize = true, Text = "Đang kiểm tra: —" };
    readonly Label _cycleState = new() { AutoSize = true, Text = "Phiên: —" };
    readonly Label _summaryState = new() { AutoSize = true, Text = "Tỷ lệ TB: —" };
    readonly Button _openObserver = new() { Text = "Mở Chrome Observer", AutoSize = true };
    readonly Button _start = new() { Text = "Bắt đầu kiểm tra", AutoSize = true };
    readonly Button _stop = new() { Text = "Dừng kiểm tra", AutoSize = true, Enabled = false };
    readonly Button _exportDiagnostic = new() { Text = "Xuất ZIP chẩn đoán", AutoSize = true };
    readonly Button _banCheck = new() { Text = "CHECK BAN", AutoSize = true };
    readonly Button _observerLogin = new() { Text = "Đăng nhập", AutoSize = true };
    readonly ComboBox _quickProfile = new() { Width = 92, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(8, 4, 2, 0) };
    readonly Button _quickCheck = new() { Text = "CHECK NGAY", AutoSize = true };
    readonly Button _history = new() { Text = "LỊCH SỬ CHECK", AutoSize = true };
    readonly Label _observerLoginState = new() { AutoSize = true, Text = "Login: —", Margin = new Padding(8, 7, 0, 0) };
    string _observerLoginUsernameValue = "";
    string _observerLoginPasswordValue = "";
    string _observerLoginTotpValue = "";
    readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false };

    bool _checking;
    bool _banCheckRunning;
    BanCheckForm? _banCheckForm;
    string _targetProfile = "";
    CycleState? _cycle;
    int _rotationSeed = -1;
    bool _followInFlight;
    string _followRequestedUrl = "";
    string _authorizedLiveUrl = "";
    bool _manualTarget;
    string _resumeProfile = "";
    CycleState? _resumeCycle;
    readonly CommentCheckHistoryStore _historyStore;
    readonly Dictionary<string, HistoryAverageState> _historyAverageByProfile = new(StringComparer.OrdinalIgnoreCase);
    // Thống kê của LƯỢT KIỂM TRA HIỆN TẠI: reset khi bấm "Bắt đầu kiểm tra".
    // Mỗi PRF cộng dồn qua nhiều phiên 5 phút cho tới khi người dùng bấm Bắt đầu lần kế tiếp.
    readonly Dictionary<string, RunAggregateState> _currentRunByProfile = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "Kiểm tra hiển thị CMT TikTok — Observer độc lập";
        Width = 1050; Height = 720; MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Directory.CreateDirectory(_dataDir);
        _observer = new ObserverChromeSession(49335, Path.Combine(_dataDir, "ObserverChrome"));
        _historyStore = new CommentCheckHistoryStore(_dataDir, Log);
        ReloadHistoryAverages();
        LoadObserverLoginSettings();

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 128,
            AutoSize = false,
            Padding = new Padding(8, 6, 8, 4),
            ColumnCount = 1,
            RowCount = 3
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        toolbar.Controls.AddRange(new Control[] { _openObserver, _observerLogin, _start, _stop, _exportDiagnostic, _banCheck, _observerState, _observerLoginState });

        var quickBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        quickBar.Controls.AddRange(new Control[]
        {
            new Label { Text = "PRF cần kiểm tra:", AutoSize = true, Margin = new Padding(0, 8, 2, 0) },
            _quickProfile, _quickCheck, _history
        });

        var statusBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        statusBar.Controls.AddRange(new Control[] { _targetState, _cycleState, _summaryState });

        top.Controls.Add(toolbar, 0, 0);
        top.Controls.Add(quickBar, 0, 1);
        top.Controls.Add(statusBar, 0, 2);

        AddMainGridColumn("Profile", "PRF", 10F, 65, DataGridViewContentAlignment.MiddleCenter);
        AddMainGridColumn("Username", "Tài khoản", 22F, 135);
        AddMainGridColumn("State", "Trạng thái", 17F, 100, DataGridViewContentAlignment.MiddleCenter);
        AddMainGridColumn("Cmt", "CMT", 8F, 68, DataGridViewContentAlignment.MiddleCenter);
        AddMainGridColumn("Progress", "Tiến độ", 14F, 90, DataGridViewContentAlignment.MiddleCenter);
        AddMainGridColumn("AverageResult", "Tỷ lệ TB", 11F, 85, DataGridViewContentAlignment.MiddleCenter);
        AddMainGridColumn("Last", "Lần cuối", 13F, 110, DataGridViewContentAlignment.MiddleCenter);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 390 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_log);
        Controls.Add(split); Controls.Add(top);

        _openObserver.Click += async (_, _) => await OpenObserverAsync();
        _observerLogin.Click += async (_, _) => await LoginObserverAsync();
        _start.Click += (_, _) => StartChecking();
        _stop.Click += (_, _) => StopChecking("Người dùng dừng");
        _exportDiagnostic.Click += (_, _) => ExportDiagnostics();
        _quickCheck.Click += (_, _) => StartQuickCheck();
        _history.Click += (_, _) => OpenHistoryWindow();
        _banCheck.Click += (_, _) => OpenBanCheckWindow();
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();
        FormClosing += async (_, _) =>
        {
            SaveObserverLoginSettings();
            _cts.Cancel();
            try { _udp.Close(); } catch { }
            try { await _observer.DisposeAsync(); } catch { }
        };

        _ = ReceiveLoopAsync();
        Log($"Đang nghe telemetry localhost UDP {TelemetryPort}. Module này không gửi lệnh điều khiển về Worker/Manager.");
        Log("Bước đầu: bấm 'Mở Chrome Observer'. Khi cần đăng nhập, bấm nút 'Đăng nhập' để mở cửa sổ TK/MK/2FA; sau đó bấm 'Bắt đầu kiểm tra'.");
        Log("Nút 'Xuất ZIP chẩn đoán' chỉ gom log/trạng thái; KHÔNG lấy thư mục ObserverChrome, cookie hay file thông tin đăng nhập Observer.");
    }

    void AddMainGridColumn(string name, string header, float fillWeight, int minimumWidth, DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            MinimumWidth = minimumWidth,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        });
    }

    void ReloadHistoryAverages()
    {
        _historyAverageByProfile.Clear();
        try
        {
            foreach (var session in _historyStore.LoadAll())
                AddCompletedSessionToAverage(session);
        }
        catch (Exception ex)
        {
            Log("[HISTORY_AVERAGE_LOAD_WARN] " + ex.Message);
        }
    }

    void AddCompletedSessionToAverage(CommentCheckSessionHistory session)
    {
        if (string.IsNullOrWhiteSpace(session.Profile)) return;

        if (!_historyAverageByProfile.TryGetValue(session.Profile, out var average))
        {
            average = new HistoryAverageState();
            _historyAverageByProfile[session.Profile] = average;
        }

        // Tỷ lệ TB là tỷ lệ gộp theo toàn bộ CMT có kết luận của các phiên đã lưu:
        // Tổng Hiện / (Tổng Hiện + Tổng Mất). Không rõ không tham gia mẫu số.
        average.Visible += Math.Max(0, session.Visible);
        average.Missing += Math.Max(0, session.Missing);
        average.SessionCount++;

        var endedLocal = session.EndedAt.ToLocalTime().DateTime;
        if (endedLocal > average.LastEndedLocal)
            average.LastEndedLocal = endedLocal;
    }

    string FormatHistoryAverage(string profile)
    {
        if (!_historyAverageByProfile.TryGetValue(profile, out var average))
            return "—";

        var known = average.Visible + average.Missing;
        return known <= 0 ? "—" : $"{average.Visible * 100.0 / known:0.0}%";
    }

    DateTime GetHistoryLastCheck(string profile)
        => _historyAverageByProfile.TryGetValue(profile, out var average)
            ? average.LastEndedLocal
            : default;

    void ResetCurrentRunStatistics(string reason = "START_CHECKING")
    {
        _currentRunByProfile.Clear();
        Log($"[RUN_STATS_RESET] reason={reason}");
    }

    void AddCompletedCycleToCurrentRun(CycleState cycle)
    {
        if (!_checking || cycle is null || string.IsNullOrWhiteSpace(cycle.Profile)) return;
        if (!_currentRunByProfile.TryGetValue(cycle.Profile, out var state))
        {
            state = new RunAggregateState();
            _currentRunByProfile[cycle.Profile] = state;
        }

        state.Sent += Math.Max(0, cycle.SentCount);
        state.Visible += Math.Max(0, cycle.Visible);
        state.Missing += Math.Max(0, cycle.Missing);
        state.Unknown += Math.Max(0, cycle.Unknown);
        state.CompletedSessions++;
    }

    RunAggregateSnapshot GetCurrentRunSnapshot(string profile)
    {
        var sent = 0;
        var visible = 0;
        var missing = 0;
        var unknown = 0;

        if (_currentRunByProfile.TryGetValue(profile, out var saved))
        {
            sent += saved.Sent;
            visible += saved.Visible;
            missing += saved.Missing;
            unknown += saved.Unknown;
        }

        // Phiên đang chạy chưa được PersistCycle nên cộng trực tiếp để UI cập nhật realtime mỗi giây.
        if (_cycle is not null && _cycle.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            sent += Math.Max(0, _cycle.SentCount);
            visible += Math.Max(0, _cycle.Visible);
            missing += Math.Max(0, _cycle.Missing);
            unknown += Math.Max(0, _cycle.Unknown);
        }

        // Khi CHECK NGAY tạm nhường phiên auto, phiên auto nằm ở _resumeCycle và vẫn thuộc lượt hiện tại.
        if (_resumeCycle is not null && _resumeCycle.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            sent += Math.Max(0, _resumeCycle.SentCount);
            visible += Math.Max(0, _resumeCycle.Visible);
            missing += Math.Max(0, _resumeCycle.Missing);
            unknown += Math.Max(0, _resumeCycle.Unknown);
        }

        return new RunAggregateSnapshot(sent, visible, missing, unknown);
    }

    string FormatCurrentRunAverage(string profile)
    {
        var snapshot = GetCurrentRunSnapshot(profile);
        var known = snapshot.Visible + snapshot.Missing;
        return known <= 0 ? "—" : $"{snapshot.Visible * 100.0 / known:0.0}%";
    }

    string FormatCurrentRunCmt(string profile)
    {
        var snapshot = GetCurrentRunSnapshot(profile);
        return snapshot.Sent <= 0 ? "—" : $"{snapshot.Visible}/{snapshot.Sent}";
    }

    string ObserverLoginSettingsPath => Path.Combine(_dataDir, "observer_login.json");

    void LoadObserverLoginSettings()
    {
        try
        {
            if (!File.Exists(ObserverLoginSettingsPath)) return;
            var saved = JsonSerializer.Deserialize<ObserverLoginSettings>(File.ReadAllText(ObserverLoginSettingsPath));
            if (saved is null) return;
            _observerLoginUsernameValue = saved.Username ?? "";
            _observerLoginPasswordValue = saved.Password ?? "";
            _observerLoginTotpValue = saved.TotpSecret ?? "";
        }
        catch (Exception ex)
        {
            Log("[OBSERVER_LOGIN_LOAD_WARN] " + ex.Message);
        }
    }

    void SaveObserverLoginSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var settings = new ObserverLoginSettings
            {
                Username = _observerLoginUsernameValue.Trim(),
                Password = _observerLoginPasswordValue,
                TotpSecret = _observerLoginTotpValue.Trim()
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ObserverLoginSettingsPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, ObserverLoginSettingsPath, true);
        }
        catch (Exception ex)
        {
            Log("[OBSERVER_LOGIN_SAVE_WARN] " + ex.Message);
        }
    }

    async Task LoginObserverAsync()
    {
        if (!_observer.Connected)
        {
            MessageBox.Show(
                this,
                "Hãy bấm 'Mở Chrome Observer' trước rồi mới đăng nhập.",
                "Đăng nhập Observer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ObserverLoginDialog(
            _observerLoginUsernameValue,
            _observerLoginPasswordValue,
            _observerLoginTotpValue);

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var username = dialog.Username.Trim();
        var password = dialog.Password;
        var totpSecret = dialog.TotpSecret.Trim();

        _observerLoginUsernameValue = username;
        _observerLoginPasswordValue = password;
        _observerLoginTotpValue = totpSecret;
        SaveObserverLoginSettings();

        _observerLogin.Enabled = false;
        _observerLoginState.Text = "Login: 🟡 đang xử lý...";
        UseWaitCursor = true;

        try
        {
            Log($"[OBSERVER_LOGIN_BEGIN] usernameConfigured=true totpConfigured={totpSecret.Length > 0}");
            var result = await _observer.LoginAsync(username, password, totpSecret, _cts.Token);

            var (status, message, icon) = result.State switch
            {
                "READY" => ("Login: 🟢 thành công", "Đăng nhập Observer thành công.", MessageBoxIcon.Information),
                "CAPTCHA_REQUIRED" => ("Login: 🟠 CAPTCHA", "TikTok đang yêu cầu CAPTCHA. Hãy xử lý trực tiếp trên Chrome Observer.", MessageBoxIcon.Information),
                "TOTP_REQUIRED" => ("Login: 🟠 thiếu 2FA", "TikTok yêu cầu 2FA nhưng secret 2FA đang trống hoặc không hợp lệ.", MessageBoxIcon.Warning),
                "ACCOUNT_BANNED" => ("Login: 🔴 tài khoản lỗi", "Tài khoản Observer bị TikTok từ chối/cấm. Xem Chrome và log để biết chi tiết.", MessageBoxIcon.Warning),
                "LOGIN_FAILED" => ("Login: 🟠 chưa thành công", "Đăng nhập Observer chưa thành công sau thời gian chờ.", MessageBoxIcon.Warning),
                "LOGIN_FORM_NOT_FOUND" => ("Login: 🟠 không thấy form", "Không tìm thấy form đăng nhập TikTok.", MessageBoxIcon.Warning),
                _ => ($"Login: 🟠 {result.State}", result.Message, MessageBoxIcon.Information)
            };

            _observerLoginState.Text = status;
            _observerState.Text = _observer.Connected ? "Observer: 🟢 đã kết nối" : "Observer: 🟠 mất kết nối";
            Log($"[OBSERVER_LOGIN_RESULT] state={result.State} loginPerformed={result.LoginPerformed} liveOpened={result.LiveOpened} message={result.Message}");

            MessageBox.Show(
                this,
                message,
                "Đăng nhập Observer",
                MessageBoxButtons.OK,
                icon);
        }
        catch (OperationCanceledException)
        {
            _observerLoginState.Text = "Login: —";
        }
        catch (Exception ex)
        {
            _observerLoginState.Text = "Login: 🔴 lỗi";
            Log("[OBSERVER_LOGIN_ERROR] " + ex);
            MessageBox.Show(
                this,
                ex.Message,
                "Không đăng nhập được Observer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _observerLogin.Enabled = true;
            UseWaitCursor = false;
        }
    }

    async Task OpenObserverAsync()
    {
        try
        {
            _openObserver.Enabled = false;
            await _observer.EnsureStartedAsync();
            _observerState.Text = "Observer: 🟢 đã kết nối";
            Log("Observer Chrome đã mở/kết nối. Nếu cần đăng nhập, bấm nút 'Đăng nhập' để mở cửa sổ TK/MK/2FA.");
        }
        catch (Exception ex)
        {
            _observerState.Text = "Observer: 🔴 lỗi";
            Log("OBSERVER ERROR: " + ex);
        }
        finally { _openObserver.Enabled = true; }
    }

    void StartChecking()
    {
        if (_checking) return;

        var policy = RemotePolicyShadowReader.Evaluate("comment_check");
        Log(
            $"[REMOTE_POLICY_COMMENT_CHECK_RUNTIME_CHECK] revision={policy.Revision} " +
            $"mode={policy.Mode} wouldBlock={policy.WouldBlock} enforcement={policy.Enforcement} " +
            $"adminBypass={policy.AdminBypass} fresh={policy.Fresh} allowed={policy.Allowed} " +
            $"detail={policy.Error}");

        if (!policy.Allowed)
        {
            MessageBox.Show(
                this,
                "Remote Policy đang chặn chức năng CHECK CMT trên thiết bị này.",
                "Check CMT",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_banCheckRunning)
        {
            MessageBox.Show(
                this,
                "CHECK BAN đang chạy. Hãy dừng CHECK BAN trước khi kiểm tra CMT.",
                "Check CMT",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        ResetCurrentRunStatistics();
        _checking = true;
        _start.Enabled = false; _stop.Enabled = true;
        _cycle = null;
        _resumeCycle = null;
        _resumeProfile = "";
        _manualTarget = false;
        _targetProfile = "";
        ChooseNextTarget();
        Log($"Đã bật kiểm tra xoay vòng {ProfileCheckMinutes} phút/PRF. Observer chỉ follow LIVE sau SENT thành công; lỗi tạm không đổi PRF, chỉ Manager xác nhận thay/retire mới chuyển sớm.");
    }

    void StopChecking(string reason)
    {
        if (_cycle is not null)
        {
            CancelPendingForProfile(_cycle.Profile, countUnknown: true, reason: "USER_STOP");
            PersistCycle(_cycle, reason);
        }
        if (_resumeCycle is not null)
            PersistDetachedCycle(_resumeCycle, reason + " (phiên đang nhường CHECK NGAY)");

        foreach (var item in _pending.Values.ToList())
            _ = _observer.ClearAsync(item.Key);
        _pending.Clear();

        _checking = false;
        _start.Enabled = true; _stop.Enabled = false;
        _cycle = null;
        _resumeCycle = null;
        _resumeProfile = "";
        _manualTarget = false;
        _targetProfile = "";
        _targetState.Text = "Đang kiểm tra: —";
        _cycleState.Text = "Phiên: —";
        _summaryState.Text = "Tỷ lệ TB: —";
        Log("Đã dừng kiểm tra: " + reason);
    }

    void OpenBanCheckWindow()
    {
        if (_banCheckForm is not null && !_banCheckForm.IsDisposed)
        {
            try
            {
                if (_banCheckForm.WindowState == FormWindowState.Minimized)
                    _banCheckForm.WindowState = FormWindowState.Normal;
                _banCheckForm.BringToFront();
                _banCheckForm.Focus();
            }
            catch { }
            return;
        }

        _banCheckForm = new BanCheckForm(
            _observer,
            _dataDir,
            Log,
            canStart: () => !_checking && !_banCheckRunning,
            runningChanged: running =>
            {
                _banCheckRunning = running;
                _start.Enabled = !running && !_checking;
                _banCheck.Text = running ? "CHECK BAN · ĐANG CHẠY" : "CHECK BAN";
            });
        _banCheckForm.FormClosed += (_, _) =>
        {
            _banCheckRunning = false;
            _start.Enabled = !_checking;
            _banCheck.Text = "CHECK BAN";
            _banCheckForm = null;
        };
        _banCheckForm.Show(this);
    }

    async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                var raw = Encoding.UTF8.GetString(r.Buffer);
                var msg = JsonSerializer.Deserialize<TelemetryMessage>(raw);
                if (msg is null || string.IsNullOrWhiteSpace(msg.Profile)) continue;
                if (!IsDisposed) BeginInvoke(new Action(() => HandleTelemetry(msg)));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!IsDisposed) BeginInvoke(new Action(() => Log("TELEMETRY ERROR: " + ex)));
                await Task.Delay(500);
            }
        }
    }

    void HandleTelemetry(TelemetryMessage m)
    {
        if (m.Type.Equals("MANAGER_STATE", StringComparison.OrdinalIgnoreCase))
        {
            HandleManagerStateTelemetry(m);
            RefreshGrid();
            return;
        }

        if (!_profiles.TryGetValue(m.Profile, out var p))
        {
            p = new ProfileState { Profile = m.Profile };
            _profiles[m.Profile] = p;
        }

        var workerRestarted = p.Pid != 0 && m.Pid != 0 && p.Pid != m.Pid;
        if (workerRestarted && m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            // Worker restart/recovery là lỗi tạm: giữ nguyên phiên + deadline 5 phút,
            // chỉ hủy pending gắn với PID cũ để không trộn sendId của hai process.
            CancelPendingForProfile(m.Profile, countUnknown: true, reason: "WORKER_PID_CHANGED");
            Log($"[SESSION_KEEP_WORKER_RESTART] PRF={m.Profile} Worker PID đổi {p.Pid} -> {m.Pid}; giữ target/deadline, chỉ dọn pending PID cũ.");
        }

        p.Pid = m.Pid;
        p.Username = m.Username ?? p.Username;
        p.RunState = m.RunState ?? p.RunState;
        // Chỉ thay LIVE đã biết bằng URL vừa được Worker xác minh thật ngay trước Enter.
        // HEARTBEAT/Page.Url có thể stale sau khi tab đã chuyển LIVE nên không được ghi đè URL fresh.
        if (m.LiveUrlVerified && !string.IsNullOrWhiteSpace(m.LiveUrl))
            p.LiveUrl = m.LiveUrl;
        else if (string.IsNullOrWhiteSpace(p.LiveUrl) && !string.IsNullOrWhiteSpace(m.LiveUrl))
            p.LiveUrl = m.LiveUrl;
        p.ContentTotal = m.ContentTotal > 0 ? m.ContentTotal : p.ContentTotal;
        p.ContentIndex = m.ContentIndex > 0 ? m.ContentIndex : p.ContentIndex;
        p.LastSeenUtc = DateTime.UtcNow;

        if (!m.Type.Equals("HEARTBEAT", StringComparison.OrdinalIgnoreCase))
            Log($"[TELEMETRY_{m.Type}] PRF={m.Profile} pid={m.Pid} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} state={m.RunState} live={ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl)} verified={m.LiveUrlVerified}");

        if (_checking && string.IsNullOrWhiteSpace(_targetProfile)) ChooseNextTarget();
        if (!_checking || !m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            RefreshGrid();
            return;
        }

        if (m.Type.Equals("WILL_SEND", StringComparison.OrdinalIgnoreCase))
        {
            _ = PrepareSendAsync(m);
        }
        else if (m.Type.Equals("SENT", StringComparison.OrdinalIgnoreCase))
        {
            _ = HandleSentAsync(m);
        }
        // HEARTBEAT chỉ cập nhật trạng thái. Observer KHÔNG chạy theo mỗi lần PRF đổi LIVE.
        // Việc follow LIVE mới chỉ được kích hoạt sau khi nhận SENT thành công.
        RefreshGrid();
    }

    async Task FollowTargetLiveAsync(string? liveUrl)
    {
        var wanted = ObserverChromeSession.NormalizeLiveUrl(liveUrl);
        if (wanted.Length == 0) return;

        // Nếu đang follow một URL cũ mà WILL_SEND vừa xác minh được LIVE mới,
        // chỉ ghi đè đích yêu cầu. Task đang chạy sẽ tự follow tiếp đích mới nhất,
        // tránh hai Navigate song song kéo Observer qua lại giữa hai LIVE.
        _followRequestedUrl = wanted;
        if (_followInFlight) return;

        _followInFlight = true;
        try
        {
            while (true)
            {
                var requested = _followRequestedUrl;
                if (requested.Length == 0) break;

                bool ok;
                if (_observer.IsOnLive(requested))
                {
                    ok = true;
                }
                else
                {
                    Log($"[FOLLOW_BEGIN] PRF={_targetProfile} wanted={requested} current={_observer.CurrentLiveUrl}");
                    ok = await _observer.EnsureLiveAsync(requested);
                }

                if (!IsDisposed)
                {
                    _observerState.Text = ok ? "Observer: 🟢 bám đúng LIVE" : "Observer: 🟠 chưa bám được LIVE";
                    Log($"[FOLLOW_DONE] ok={ok} wanted={requested} current={_observer.CurrentLiveUrl}");
                }

                if (string.Equals(requested, _followRequestedUrl, StringComparison.OrdinalIgnoreCase))
                    break;

                Log($"[FOLLOW_COALESCE] old={requested} newest={_followRequestedUrl} action=FOLLOW_NEWEST");
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Log("FOLLOW LIVE WARN: " + ex);
        }
        finally
        {
            _followInFlight = false;
        }
    }

    async Task PrepareSendAsync(TelemetryMessage m)
    {
        if (!_checking || !m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase)) return;

        if (_cycle is not null)
        {
            if (_cycle.Closing) return;
            if (_cycle.StartedUtc != default && DateTime.UtcNow >= _cycle.DeadlineUtc)
            {
                MarkCurrentSessionDeadlineReached();
                return;
            }
        }

        var key = MakeSendKey(m.Profile, m.Pid, m.SendId);
        var wanted = ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl);
        var freshLiveVerified = m.LiveUrlVerified && wanted.Length > 0;
        var liveAuthorized = freshLiveVerified
                             && string.Equals(wanted, _authorizedLiveUrl, StringComparison.OrdinalIgnoreCase);
        var ready = liveAuthorized && _observer.Connected && _observer.IsOnLive(wanted);
        Log($"[PREPARE_SEND] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} ready={ready} liveVerified={freshLiveVerified} liveAuthorized={liveAuthorized} observerConnected={_observer.Connected} wanted={wanted} current={_observer.CurrentLiveUrl}");

        // Không follow ở WILL_SEND. PRF gốc có thể đổi qua nhiều LIVE để tìm nguồn phù hợp;
        // Observer chỉ chạy theo sau khi chính PRF đã SENT thành công ở LIVE mới.
        if (!ready)
        {
            Log($"[PREPARE_SKIP_WAIT_SENT_TO_FOLLOW] PRF={m.Profile} send={m.SendId} liveVerified={freshLiveVerified} wanted={wanted} current={_observer.CurrentLiveUrl}");
            return;
        }

        if (_cycle is null)
        {
            _cycle = new CycleState
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Profile = m.Profile,
                Username = m.Username ?? "",
                Expected = m.ContentTotal,
                StartIndex = m.ContentIndex,
                CreatedUtc = DateTime.UtcNow,
                IsManual = _manualTarget
            };
            Log($"[SESSION_READY] PRF={m.Profile} observer đã đúng LIVE; chờ SENT đầu tiên để bắt đầu đồng hồ {ProfileCheckMinutes} phút.");
            if (string.IsNullOrWhiteSpace(m.Username))
                Log($"[MATCH_WARN] PRF={m.Profile} không có TikTok handle hợp lệ trong tiktok_auth.json; lượt này phải match theo nội dung và độ tin cậy thấp hơn.");
        }

        if (_cycle.Profile != m.Profile || _cycle.Closing) return;

        var willSendUtc = TelemetryUtcOrNow(m.SentAtUtcMs);
        if (_cycle.StartedUtc != default && willSendUtc >= _cycle.DeadlineUtc)
        {
            MarkCurrentSessionDeadlineReached();
            return;
        }

        var pending = new PendingSend
        {
            Key = key,
            SessionId = _cycle.SessionId,
            Profile = m.Profile,
            SendId = m.SendId,
            ContentIndex = m.ContentIndex,
            Content = m.Content ?? "",
            Username = m.Username ?? "",
            LiveUrl = wanted,
            ObserverReady = ready,
            ArmedUtc = DateTime.UtcNow,
            WillSendUtc = willSendUtc,
            TimeoutSeconds = GetAdaptiveTimeoutSeconds(wanted)
        };
        _pending[key] = pending;

        try
        {
            pending.Armed = await _observer.ArmAsync(key, pending.Username, pending.Content);
            Log($"[ARM] PRF={pending.Profile} send={pending.SendId} cmt={pending.ContentIndex} armed={pending.Armed} timeout={pending.TimeoutSeconds:0.0}s user={(string.IsNullOrWhiteSpace(pending.Username) ? "EMPTY" : "OK")}");
            if (!pending.Armed) pending.ObserverReady = false;
        }
        catch (Exception ex)
        {
            pending.Armed = false;
            pending.ObserverReady = false;
            Log($"[ARM_ERROR] PRF={pending.Profile} send={pending.SendId} cmt={pending.ContentIndex} error={ex.Message}");
        }
        finally
        {
            pending.PrepareDone.TrySetResult(pending.Armed && pending.ObserverReady);
        }
    }

    async Task HandleSentAsync(TelemetryMessage m)
    {
        if (!_checking || !m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase)) return;
        if (_cycle is not null && _cycle.StartedUtc != default && DateTime.UtcNow >= _cycle.DeadlineUtc)
        {
            MarkCurrentSessionDeadlineReached();
            return;
        }
        if (_cycle?.Closing == true) return;

        var key = MakeSendKey(m.Profile, m.Pid, m.SendId);
        var wanted = ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl);
        var freshLiveVerified = m.LiveUrlVerified && wanted.Length > 0;

        // Không có WILL_SEND đã arm nghĩa là Observer chưa đứng đúng LIVE. Đây chính là
        // comment mốc: sau khi SENT thành công mới cho Observer follow sang LIVE đó.
        if (!_pending.TryGetValue(key, out var pending))
        {
            Log($"[SENT_MARK_FOLLOW] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} liveVerified={freshLiveVerified} wanted={wanted} current={_observer.CurrentLiveUrl}");
            if (freshLiveVerified)
            {
                _authorizedLiveUrl = wanted;
                _ = FollowTargetLiveAsync(wanted);
            }
            return;
        }

        if (!pending.PrepareDone.Task.IsCompleted)
            await Task.WhenAny(pending.PrepareDone.Task, Task.Delay(2000));

        if (_cycle is null
            || _cycle.Profile != m.Profile
            || !string.Equals(_cycle.SessionId, pending.SessionId, StringComparison.Ordinal))
        {
            _pending.Remove(key);
            _ = _observer.ClearAsync(pending.Key);
            return;
        }

        if (!pending.PrepareDone.Task.IsCompletedSuccessfully
            || !pending.PrepareDone.Task.Result
            || !pending.ObserverReady
            || !pending.Armed
            || !_observer.IsOnLive(pending.LiveUrl))
        {
            _pending.Remove(key);
            Log($"[SENT_SKIP_NOT_SYNCED] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} wanted={pending.LiveUrl} current={_observer.CurrentLiveUrl}");
            _ = _observer.ClearAsync(pending.Key);
            if (freshLiveVerified)
            {
                _authorizedLiveUrl = wanted;
                _ = FollowTargetLiveAsync(wanted);
            }
            return;
        }

        pending.SentConfirmedUtc = TelemetryUtcOrNow(m.SentAtUtcMs);

        if (_cycle.StartedUtc == default)
        {
            _cycle.StartedUtc = pending.SentConfirmedUtc;
            _cycle.DeadlineUtc = _cycle.StartedUtc.Add(ProfileCheckDuration);
            _cycle.Username = string.IsNullOrWhiteSpace(m.Username) ? _cycle.Username : m.Username;
            Log($"[SESSION_START_5M] PRF={m.Profile} start={_cycle.StartedUtc:O} deadline={_cycle.DeadlineUtc:O} manual={_cycle.IsManual}");
        }

        if (_cycle.Closing || pending.WillSendUtc >= _cycle.DeadlineUtc)
        {
            _pending.Remove(key);
            _ = _observer.ClearAsync(pending.Key);
            MarkCurrentSessionDeadlineReached();
            return;
        }

        if (!_cycle.SeenSendIds.Add(m.SendId)) return;
        _cycle.SentCount++;
        pending.TimeoutSeconds = Math.Max(pending.TimeoutSeconds, GetAdaptiveTimeoutSeconds(pending.LiveUrl));

        Log($"[SENT_ACCEPT] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} sessionSent={_cycle.SentCount} remain={FormatRemaining(_cycle)} timeout={pending.TimeoutSeconds:0.0}s");
        _ = ResolveSentAsync(pending);
        UpdateCycleLabel();
    }

    async Task ResolveSentAsync(PendingSend p)
    {
        ResultKind result;
        string mode = "";
        double latencySeconds = 0;
        double timeoutUsed = p.TimeoutSeconds;

        try
        {
            if (!p.ObserverReady || !p.Armed || !_observer.IsOnLive(p.LiveUrl))
            {
                result = ResultKind.Unknown;
            }
            else
            {
                ObserverMatch hit = new(false, false, "", "", 0);
                var deadline = p.WillSendUtc.AddSeconds(timeoutUsed);

                while (true)
                {
                    if (!_observer.IsOnLive(p.LiveUrl)) break;

                    hit = await _observer.CheckAsync(p.Key);
                    if (hit.Matched) break;

                    // Adaptive deadline can grow while this item is pending if another
                    // comment on the same LIVE reveals that the LIVE is slower.
                    var learnedTimeout = GetAdaptiveTimeoutSeconds(p.LiveUrl);
                    if (learnedTimeout > timeoutUsed)
                    {
                        timeoutUsed = learnedTimeout;
                        var learnedDeadline = p.WillSendUtc.AddSeconds(timeoutUsed);
                        if (learnedDeadline > deadline) deadline = learnedDeadline;
                    }

                    if (DateTime.UtcNow >= deadline) break;
                    await Task.Delay(300);
                }

                if (hit.Matched)
                {
                    result = ResultKind.Visible;
                    mode = hit.Mode;
                    if (hit.MatchedAt > 0)
                    {
                        try
                        {
                            var seenUtc = DateTimeOffset.FromUnixTimeMilliseconds(hit.MatchedAt).UtcDateTime;
                            latencySeconds = Math.Max(0, (seenUtc - p.WillSendUtc).TotalSeconds);
                        }
                        catch
                        {
                            latencySeconds = Math.Max(0, (DateTime.UtcNow - p.WillSendUtc).TotalSeconds);
                        }
                    }
                    else
                    {
                        latencySeconds = Math.Max(0, (DateTime.UtcNow - p.WillSendUtc).TotalSeconds);
                    }
                }
                else
                {
                    result = _observer.IsOnLive(p.LiveUrl) ? ResultKind.Missing : ResultKind.Unknown;
                    mode = hit.Mode;
                }
            }
        }
        catch (Exception ex)
        {
            result = ResultKind.Unknown;
            if (!IsDisposed) BeginInvoke(new Action(() => Log($"[RESOLVE_ERROR] PRF={p.Profile} send={p.SendId} cmt={p.ContentIndex} error={ex}")));
        }
        finally
        {
            try { await _observer.ClearAsync(p.Key); } catch { }
        }

        if (IsDisposed) return;
        BeginInvoke(new Action(() => ApplyResolvedResult(p, result, mode, latencySeconds, timeoutUsed)));
    }

    void ApplyResolvedResult(PendingSend p, ResultKind result, string mode, double latencySeconds, double timeoutUsed)
    {
        if (!_pending.Remove(p.Key)) return;
        if (_cycle is null
            || _cycle.Profile != p.Profile
            || !string.Equals(_cycle.SessionId, p.SessionId, StringComparison.Ordinal))
            return;

        if (result == ResultKind.Visible && latencySeconds >= 0)
        {
            RecordVisibleLatency(p.LiveUrl, latencySeconds);
            var nextTimeout = GetAdaptiveTimeoutSeconds(p.LiveUrl);
            Log($"[LIVE_LATENCY] PRF={p.Profile} live={p.LiveUrl} cmt={p.ContentIndex} latency={latencySeconds:0.00}s nextTimeout={nextTimeout:0.0}s");
        }

        _cycle.ResolvedCount++;
        switch (result)
        {
            case ResultKind.Visible: _cycle.Visible++; break;
            case ResultKind.Missing: _cycle.Missing++; break;
            default: _cycle.Unknown++; break;
        }

        _cycle.Details.Add(new CommentResult(
            DateTimeOffset.Now,
            p.SendId,
            p.ContentIndex,
            p.Username,
            p.Content,
            result.ToString(),
            mode,
            p.LiveUrl,
            result == ResultKind.Visible ? latencySeconds : null,
            timeoutUsed));

        Log($"[CMT_RESULT] PRF={p.Profile} send={p.SendId} cmt={p.ContentIndex} result={result} mode={mode} latency={(result == ResultKind.Visible ? latencySeconds.ToString("0.00") + "s" : "-")} timeout={timeoutUsed:0.0}s resolved={_cycle.ResolvedCount} remain={FormatRemaining(_cycle)}");
        UpdateCycleLabel();
        TryCompleteClosingSession();
    }

    void ChooseNextTarget()
    {
        if (!_checking) return;

        // CHECK NGAY đã kết thúc: nếu phiên auto trước đó còn thời hạn và Manager chưa
        // xác nhận thay/retire thì quay lại đúng PRF cũ. Deadline tuyệt đối không pause.
        if (_manualTarget)
        {
            _manualTarget = false;
            if (TryResumeInterruptedTarget()) return;
        }

        var active = _profiles.Values
            .Where(IsEligibleForNewTarget)
            .OrderBy(p => NaturalProfileKey(p.Profile), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (active.Count == 0)
        {
            _targetProfile = "";
            _cycle = null;
            _targetState.Text = "Đang kiểm tra: chờ PRF hoạt động...";
            _cycleState.Text = "Phiên: —";
            return;
        }

        var currentIndex = active.FindIndex(p => p.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase));
        var next = currentIndex >= 0 ? (currentIndex + 1) % active.Count : (_rotationSeed + 1 + active.Count) % active.Count;
        _rotationSeed = next;
        ActivateTarget(active[next].Profile, manual: false, cycle: null, reason: "AUTO_ROTATION");
    }

    void OnUiTick()
    {
        if (_checking)
        {
            RefreshQuickProfileChoices();
            ExpireInterruptedSessionIfNeeded();

            if (string.IsNullOrWhiteSpace(_targetProfile))
            {
                ChooseNextTarget();
            }
            else if (_profiles.TryGetValue(_targetProfile, out var p) && p.ManagerTerminal)
            {
                Log($"[TARGET_MANAGER_REPLACED] PRF={_targetProfile} reason={p.ManagerReason} action=FINISH_AND_ROTATE");
                FinishCurrentSession("MANAGER_REPLACED: " + (string.IsNullOrWhiteSpace(p.ManagerReason) ? "terminal" : p.ManagerReason), cancelPendingAsUnknown: true);
            }
            else if (_cycle is not null && _cycle.StartedUtc != default)
            {
                if (DateTime.UtcNow >= _cycle.DeadlineUtc)
                    MarkCurrentSessionDeadlineReached();

                if (_cycle.Closing)
                {
                    var pendingForSession = PendingForSession(_cycle.SessionId).ToList();
                    if (pendingForSession.Count == 0)
                    {
                        FinishCurrentSession(_cycle.EndReason.Length > 0 ? _cycle.EndReason : "Hết 5 phút", cancelPendingAsUnknown: false);
                    }
                    else if (_cycle.DeadlineReachedUtc != default
                             && DateTime.UtcNow - _cycle.DeadlineReachedUtc >= SessionPendingGrace)
                    {
                        Log($"[SESSION_PENDING_GRACE_EXPIRED] PRF={_cycle.Profile} pending={pendingForSession.Count} grace={SessionPendingGrace.TotalSeconds:0}s");
                        FinishCurrentSession(_cycle.EndReason.Length > 0 ? _cycle.EndReason : "Hết 5 phút", cancelPendingAsUnknown: true);
                    }
                }
            }

            // Mất Worker telemetry hoặc RUNNING tạm thời KHÔNG còn là lý do đổi PRF.
            // Chỉ ManagerTerminal mới cho phép chuyển sớm trước deadline.
            if (!string.IsNullOrWhiteSpace(_targetProfile)
                && _profiles.TryGetValue(_targetProfile, out var target)
                && DateTime.UtcNow - target.LastSeenUtc > TimeSpan.FromSeconds(25)
                && !target.ManagerTerminal)
            {
                _targetState.Text = $"Đang kiểm tra: {_targetProfile} · chờ telemetry/lỗi tạm";
            }
        }
        else
        {
            RefreshQuickProfileChoices();
        }
        RefreshGrid();
    }

    void UpdateCycleLabel()
    {
        if (_cycle is null)
        {
            _cycleState.Text = $"Phiên {ProfileCheckMinutes} phút: chờ CMT đầu tiên sau khi Observer bám đúng LIVE";
            _summaryState.Text = string.IsNullOrWhiteSpace(_targetProfile)
                ? "Tỷ lệ TB: —"
                : $"Tỷ lệ TB {_targetProfile}: {FormatCurrentRunAverage(_targetProfile)}";
            return;
        }

        var remain = _cycle.StartedUtc == default ? "chưa chạy giờ" : FormatRemaining(_cycle);
        _cycleState.Text = $"Phiên {ProfileCheckMinutes} phút: gửi {_cycle.SentCount} • đã quét {_cycle.ResolvedCount} • còn {remain}";
        // Tỷ lệ TB ngoài giao diện chính là tỷ lệ cộng dồn của PRF trong LƯỢT hiện tại,
        // tính từ lúc bấm "Bắt đầu kiểm tra". Nó không reset theo từng phiên 5 phút.
        _summaryState.Text = $"Tỷ lệ TB {_cycle.Profile}: {FormatCurrentRunAverage(_cycle.Profile)}";
    }

    static string FormatVisibilityRate(int visible, int missing)
    {
        var known = visible + missing;
        return known <= 0 ? "—" : $"{visible * 100.0 / known:0.0}%";
    }

    static bool IsExplicitlyInactiveProfileState(string? state)
    {
        state = (state ?? "").Trim();

        return state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase)
               || state.Equals("OFFLINE", StringComparison.OrdinalIgnoreCase)
               || state.Equals("CLOSED", StringComparison.OrdinalIgnoreCase)
               || state.Equals("RETIRED", StringComparison.OrdinalIgnoreCase)
               || state.Equals("RETIRE", StringComparison.OrdinalIgnoreCase)
               || state.Equals("ĐANG THAY/RETIRE", StringComparison.OrdinalIgnoreCase);
    }

    bool IsVisibleOnMainGrid(ProfileState p)
    {
        // Manager đã xác nhận retire/delete/auto-close là nguồn sự thật ưu tiên.
        if (p.ManagerTerminal)
            return false;

        var now = DateTime.UtcNow;
        var managerFresh =
            p.ManagerPresent
            && p.LastManagerSeenUtc != default
            && now - p.LastManagerSeenUtc < TimeSpan.FromSeconds(8);

        if (managerFresh && !string.IsNullOrWhiteSpace(p.ManagerRunState))
        {
            // Manager hiện chỉ phát telemetry cho RUNNING / PAUSED / RECOVERING
            // (ngoài terminal). Không khóa cứng 2 tên RUNNING/RECOVERING để nếu
            // sau này Manager có trạng thái trung gian nhưng vẫn thuộc active fleet
            // thì Check CMT vẫn hiển thị, chỉ loại các trạng thái inactive rõ ràng.
            return !IsExplicitlyInactiveProfileState(p.ManagerRunState);
        }

        var workerFresh =
            p.LastSeenUtc != default
            && now - p.LastSeenUtc < TimeSpan.FromSeconds(20);

        if (!workerFresh || string.IsNullOrWhiteSpace(p.RunState))
            return false;

        // Fallback chỉ dùng telemetry Worker đang còn sống/fresh.
        return !IsExplicitlyInactiveProfileState(p.RunState);
    }

    string GetMainGridProfileState(ProfileState p)
    {
        var now = DateTime.UtcNow;
        var managerFresh =
            p.ManagerPresent
            && p.LastManagerSeenUtc != default
            && now - p.LastManagerSeenUtc < TimeSpan.FromSeconds(8);

        if (managerFresh && !string.IsNullOrWhiteSpace(p.ManagerRunState))
            return p.ManagerRunState;

        var workerFresh =
            p.LastSeenUtc != default
            && now - p.LastSeenUtc < TimeSpan.FromSeconds(20);

        return workerFresh && !string.IsNullOrWhiteSpace(p.RunState)
            ? p.RunState
            : "OFFLINE";
    }

    void RefreshGrid()
    {
        var selected = _grid.CurrentRow?.Cells["Profile"].Value?.ToString();
        _grid.Rows.Clear();

        foreach (var p in _profiles.Values
                     .Where(IsVisibleOnMainGrid)
                     .OrderBy(x => NaturalProfileKey(x.Profile), StringComparer.OrdinalIgnoreCase))
        {
            var state = GetMainGridProfileState(p);
            var isTarget = p.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase);
            var progress = isTarget
                ? (_cycle is null
                    ? "Chờ SENT"
                    : _cycle.StartedUtc == default
                        ? "Chờ CMT"
                        : FormatRemaining(_cycle))
                : "Chờ";

            // CMT = số HIỆN / số ĐÃ GỬI, cộng dồn từ lúc bấm Bắt đầu kiểm tra.
            var cmtProgress = FormatCurrentRunCmt(p.Profile);
            // Tỷ lệ TB ngoài màn chính = Hiện / (Hiện + Mất) của lượt hiện tại.
            // Không rõ và CMT đang chờ kết quả không tham gia mẫu số.
            var averageScore = FormatCurrentRunAverage(p.Profile);
            var historyLastCheck = GetHistoryLastCheck(p.Profile);
            var lastCheck = historyLastCheck != default ? historyLastCheck : p.LastCheck;

            _grid.Rows.Add(
                p.Profile, p.Username, state, cmtProgress, progress,
                averageScore,
                lastCheck == default ? "" : lastCheck.ToString("HH:mm:ss dd/MM"));
        }
        if (selected is not null)
        {
            foreach (DataGridViewRow row in _grid.Rows)
                if (string.Equals(row.Cells["Profile"].Value?.ToString(), selected, StringComparison.OrdinalIgnoreCase)) { row.Selected = true; break; }
        }
    }

    static DateTime TelemetryUtcOrNow(long unixMs)
    {
        if (unixMs <= 0) return DateTime.UtcNow;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime; }
        catch { return DateTime.UtcNow; }
    }

    double GetAdaptiveTimeoutSeconds(string? liveUrl)
    {
        var key = ObserverChromeSession.NormalizeLiveUrl(liveUrl);
        if (key.Length == 0 || !_liveLatency.TryGetValue(key, out var state) || state.Samples.Count == 0)
            return InitialCommentTimeoutSeconds;

        var values = state.Samples.OrderBy(x => x).ToArray();
        double baseLatency;

        if (values.Length < 5)
        {
            // With only a few observations, stay conservative and follow the
            // slowest sample seen so far.
            baseLatency = values[^1];
        }
        else
        {
            // P95 of recent visible comments + safety buffer.
            var rank = (int)Math.Ceiling(values.Length * 0.95) - 1;
            rank = Math.Clamp(rank, 0, values.Length - 1);
            baseLatency = values[rank];
        }

        return Math.Clamp(baseLatency + CommentTimeoutBufferSeconds, MinCommentTimeoutSeconds, MaxCommentTimeoutSeconds);
    }

    void RecordVisibleLatency(string? liveUrl, double seconds)
    {
        var key = ObserverChromeSession.NormalizeLiveUrl(liveUrl);
        if (key.Length == 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 60)
            return;

        if (!_liveLatency.TryGetValue(key, out var state))
        {
            state = new LiveLatencyState();
            _liveLatency[key] = state;
        }

        state.Samples.Enqueue(seconds);
        while (state.Samples.Count > MaxLatencySamplesPerLive)
            state.Samples.Dequeue();
        state.LastUpdatedUtc = DateTime.UtcNow;
    }

    void SaveResult(CycleState c)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var line = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                c.Profile, c.Expected, c.StartIndex, c.Visible, c.Missing, c.Unknown,
                Details = c.Details
            });
            File.AppendAllText(Path.Combine(_dataDir, "results.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) { Log("SAVE RESULT WARN: " + ex.Message); }
    }

    void ExportDiagnostics()
    {
        try
        {
            _exportDiagnostic.Enabled = false;
            var snapshot = BuildDiagnosticSnapshot();
            var zip = DiagnosticExporter.Export(_dataDir, _observer.CoreLogDirectory, snapshot);
            Log($"[DIAGNOSTIC_EXPORT_OK] {zip}");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zip}\"",
                    UseShellExecute = true
                });
            }
            catch { }

            MessageBox.Show(
                this,
                "Đã tạo ZIP chẩn đoán:\n" + zip + "\n\nFile ZIP không chứa cookie/profile đăng nhập của Observer.",
                "Xuất chẩn đoán",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("[DIAGNOSTIC_EXPORT_ERROR] " + ex);
            MessageBox.Show(this, "Không xuất được ZIP chẩn đoán:\n" + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _exportDiagnostic.Enabled = true;
        }
    }

    object BuildDiagnosticSnapshot()
    {
        return new
        {
            Timestamp = DateTimeOffset.Now,
            Checking = _checking,
            TargetProfile = _targetProfile,
            Observer = new
            {
                _observer.Connected,
                _observer.Port,
                CurrentUrl = _observer.CurrentLiveUrl,
                NormalizedLiveUrl = ObserverChromeSession.NormalizeLiveUrl(_observer.CurrentLiveUrl)
            },
            Follow = new
            {
                InFlight = _followInFlight,
                RequestedUrl = _followRequestedUrl,
                AuthorizedLiveUrl = _authorizedLiveUrl
            },
            Cycle = _cycle is null ? null : new
            {
                _cycle.Profile,
                _cycle.Expected,
                _cycle.StartIndex,
                _cycle.SentCount,
                _cycle.ResolvedCount,
                _cycle.Visible,
                _cycle.Missing,
                _cycle.Unknown,
                _cycle.StartedUtc,
                _cycle.DeadlineUtc,
                _cycle.Closing,
                _cycle.IsManual,
                Remaining = FormatRemaining(_cycle)
            },
            Pending = _pending.Values.Select(x => new
            {
                x.Profile,
                x.SendId,
                x.ContentIndex,
                x.Username,
                x.LiveUrl,
                x.ObserverReady,
                x.Armed,
                x.ArmedUtc,
                x.WillSendUtc,
                x.SentConfirmedUtc,
                x.TimeoutSeconds
            }).ToArray(),
            LiveLatency = _liveLatency.Select(x => new
            {
                LiveUrl = x.Key,
                Samples = x.Value.Samples.ToArray(),
                AdaptiveTimeoutSeconds = GetAdaptiveTimeoutSeconds(x.Key),
                x.Value.LastUpdatedUtc
            }).ToArray(),
            Profiles = _profiles.Values
                .OrderBy(x => NaturalProfileKey(x.Profile), StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    x.Profile,
                    x.Username,
                    x.RunState,
                    x.LiveUrl,
                    x.ContentIndex,
                    x.ContentTotal,
                    x.LastSeenUtc,
                    x.Pid,
                    x.LastResult,
                    x.LastVisible,
                    x.LastMissing,
                    x.LastUnknown,
                    x.LastExpected,
                    x.LastCheck,
                    x.ManagerPresent,
                    x.ManagerTerminal,
                    x.ManagerReason,
                    x.ManagerRunState,
                    x.LastManagerSeenUtc
                })
                .ToArray()
        };
    }

    void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        _log.AppendText(line + Environment.NewLine);
        try { File.AppendAllText(Path.Combine(_dataDir, "monitor.log"), line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
    }

    static string MakeSendKey(string profile, int pid, long sendId) => profile + ":" + pid + ":" + sendId;
    static string NaturalProfileKey(string value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n.ToString("D10") + "_" + value : "9999999999_" + value;
    }

    sealed class ObserverLoginDialog : Form
    {
        readonly TextBox _username = new() { Dock = DockStyle.Fill };
        readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        readonly TextBox _totp = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        public string Username => _username.Text;
        public string Password => _password.Text;
        public string TotpSecret => _totp.Text;

        public ObserverLoginDialog(string username, string password, string totpSecret)
        {
            Text = "Đăng nhập Observer";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(470, 210);
            Font = new Font("Segoe UI", 9F);

            _username.Text = username ?? "";
            _password.Text = password ?? "";
            _totp.Text = totpSecret ?? "";

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 5
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            grid.Controls.Add(new Label { Text = "Tài khoản:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            grid.Controls.Add(_username, 1, 0);
            grid.Controls.Add(new Label { Text = "Mật khẩu:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(_password, 1, 1);
            grid.Controls.Add(new Label { Text = "2FA:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(_totp, 1, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var cancel = new Button { Text = "Hủy", AutoSize = true, DialogResult = DialogResult.Cancel };
            var login = new Button { Text = "Đăng nhập", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(login);
            grid.Controls.Add(buttons, 0, 4);
            grid.SetColumnSpan(buttons, 2);

            login.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_username.Text) || string.IsNullOrEmpty(_password.Text))
                {
                    MessageBox.Show(
                        this,
                        "Hãy nhập đủ tài khoản và mật khẩu.",
                        "Đăng nhập Observer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                DialogResult = DialogResult.OK;
                Close();
            };

            AcceptButton = login;
            CancelButton = cancel;
            Controls.Add(grid);
        }
    }

    sealed class ObserverLoginSettings
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string TotpSecret { get; set; } = "";
    }

    sealed class TelemetryMessage
    {
        public string Type { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public string RunState { get; set; } = "";
        public string LiveUrl { get; set; } = "";
        public bool LiveUrlVerified { get; set; }
        public int ContentIndex { get; set; }
        public int ContentTotal { get; set; }
        public string Content { get; set; } = "";
        public long SendId { get; set; }
        public long Seq { get; set; }
        public long SentAtUtcMs { get; set; }
        public int Pid { get; set; }
        public bool ManagerPresent { get; set; }
        public bool ManagerTerminal { get; set; }
        public string ManagerReason { get; set; } = "";
        public string ManagerRunState { get; set; } = "";
        public long ManagerSeq { get; set; }
    }

    sealed class ProfileState
    {
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public string RunState { get; set; } = "";
        public string LiveUrl { get; set; } = "";
        public int ContentIndex { get; set; }
        public int ContentTotal { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int Pid { get; set; }
        public string LastResult { get; set; } = "";
        public int LastVisible { get; set; }
        public int LastMissing { get; set; }
        public int LastUnknown { get; set; }
        public int LastExpected { get; set; }
        public DateTime LastCheck { get; set; }
        public bool ManagerPresent { get; set; }
        public bool ManagerTerminal { get; set; }
        public string ManagerReason { get; set; } = "";
        public string ManagerRunState { get; set; } = "";
        public DateTime LastManagerSeenUtc { get; set; }
        public long ManagerSeq { get; set; }
    }

    sealed class HistoryAverageState
    {
        public int Visible { get; set; }
        public int Missing { get; set; }
        public int SessionCount { get; set; }
        public DateTime LastEndedLocal { get; set; }
    }

    sealed class RunAggregateState
    {
        public int Sent { get; set; }
        public int Visible { get; set; }
        public int Missing { get; set; }
        public int Unknown { get; set; }
        public int CompletedSessions { get; set; }
    }

    readonly record struct RunAggregateSnapshot(int Sent, int Visible, int Missing, int Unknown);

    sealed class PendingSend
    {
        public string Key { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Profile { get; set; } = "";
        public long SendId { get; set; }
        public int ContentIndex { get; set; }
        public string Content { get; set; } = "";
        public string Username { get; set; } = "";
        public string LiveUrl { get; set; } = "";
        public bool ObserverReady { get; set; }
        public bool Armed { get; set; }
        public DateTime ArmedUtc { get; set; }
        public DateTime WillSendUtc { get; set; }
        public DateTime SentConfirmedUtc { get; set; }
        public double TimeoutSeconds { get; set; }
        public TaskCompletionSource<bool> PrepareDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class LiveLatencyState
    {
        public Queue<double> Samples { get; } = new();
        public DateTime LastUpdatedUtc { get; set; }
    }

    sealed class CycleState
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public int Expected { get; set; }
        public int StartIndex { get; set; }
        public int SentCount { get; set; }
        public int ResolvedCount { get; set; }
        public int Visible { get; set; }
        public int Missing { get; set; }
        public int Unknown { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime DeadlineUtc { get; set; }
        public DateTime DeadlineReachedUtc { get; set; }
        public bool Closing { get; set; }
        public bool IsManual { get; set; }
        public string EndReason { get; set; } = "";
        public HashSet<long> SeenSendIds { get; } = new();
        public List<CommentResult> Details { get; } = new();
    }

    sealed record CommentResult(
        DateTimeOffset Timestamp,
        long SendId,
        int ContentIndex,
        string Username,
        string Content,
        string Result,
        string MatchMode,
        string LiveUrl,
        double? LatencySeconds,
        double TimeoutSeconds);
    enum ResultKind { Visible, Missing, Unknown }
}
