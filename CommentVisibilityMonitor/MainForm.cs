using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CommentVisibilityMonitor;

internal sealed class MainForm : Form
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

    const double InitialCommentTimeoutSeconds = 20.0;
    const double MinCommentTimeoutSeconds = 12.0;
    const double MaxCommentTimeoutSeconds = 30.0;
    const double CommentTimeoutBufferSeconds = 6.0;
    const int MaxLatencySamplesPerLive = 20;

    readonly Label _observerState = new() { AutoSize = true, Text = "Observer: chưa mở" };
    readonly Label _targetState = new() { AutoSize = true, Text = "Đang kiểm tra: —" };
    readonly Label _cycleState = new() { AutoSize = true, Text = "Vòng: —" };
    readonly Button _openObserver = new() { Text = "Mở Chrome Observer", AutoSize = true };
    readonly Button _start = new() { Text = "Bắt đầu kiểm tra", AutoSize = true };
    readonly Button _stop = new() { Text = "Dừng kiểm tra", AutoSize = true, Enabled = false };
    readonly Button _exportDiagnostic = new() { Text = "Xuất ZIP chẩn đoán", AutoSize = true };
    readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false };

    bool _checking;
    string _targetProfile = "";
    CycleState? _cycle;
    int _rotationSeed = -1;
    bool _followInFlight;
    string _followRequestedUrl = "";

    public MainForm()
    {
        Text = "Kiểm tra hiển thị CMT TikTok — Observer độc lập";
        Width = 1050; Height = 720; MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Directory.CreateDirectory(_dataDir);
        _observer = new ObserverChromeSession(49335, Path.Combine(_dataDir, "ObserverChrome"));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, AutoSize = false, Padding = new Padding(8), WrapContents = true };
        top.Controls.AddRange(new Control[] { _openObserver, _start, _stop, _exportDiagnostic, _observerState, _targetState, _cycleState });

        _grid.Columns.Add("Profile", "PRF");
        _grid.Columns.Add("Username", "Tài khoản");
        _grid.Columns.Add("State", "Trạng thái");
        _grid.Columns.Add("Progress", "Tiến độ");
        _grid.Columns.Add("Result", "Kết quả gần nhất");
        _grid.Columns.Add("Last", "Lần cuối");

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 390 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_log);
        Controls.Add(split); Controls.Add(top);

        _openObserver.Click += async (_, _) => await OpenObserverAsync();
        _start.Click += (_, _) => StartChecking();
        _stop.Click += (_, _) => StopChecking("Người dùng dừng");
        _exportDiagnostic.Click += (_, _) => ExportDiagnostics();
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();
        FormClosing += async (_, _) =>
        {
            _cts.Cancel();
            try { _udp.Close(); } catch { }
            try { await _observer.DisposeAsync(); } catch { }
        };

        _ = ReceiveLoopAsync();
        Log($"Đang nghe telemetry localhost UDP {TelemetryPort}. Module này không gửi lệnh điều khiển về Worker/Manager.");
        Log("Bước đầu: bấm 'Mở Chrome Observer', đăng nhập một tài khoản TikTok dùng để nhìn LIVE, sau đó bấm 'Bắt đầu kiểm tra'.");
        Log("Nút 'Xuất ZIP chẩn đoán' chỉ gom log/trạng thái; KHÔNG lấy thư mục ObserverChrome, cookie hay dữ liệu đăng nhập.");
    }

    async Task OpenObserverAsync()
    {
        try
        {
            _openObserver.Enabled = false;
            await _observer.EnsureStartedAsync();
            _observerState.Text = "Observer: 🟢 đã kết nối — hãy bảo đảm tài khoản observer đã đăng nhập";
            Log("Observer Chrome đã mở/kết nối. Login được lưu riêng trong CommentCheckData\\ObserverChrome.");
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
        _checking = true;
        _start.Enabled = false; _stop.Enabled = true;
        _cycle = null;
        _targetProfile = "";
        ChooseNextTarget();
        Log("Đã bật kiểm tra xoay vòng. Chỉ đọc telemetry + chat observer; không Pause/Stop/đổi LIVE của PRF chính.");
    }

    void StopChecking(string reason)
    {
        _checking = false;
        _start.Enabled = true; _stop.Enabled = false;
        _cycle = null; _pending.Clear();
        _targetProfile = "";
        _targetState.Text = "Đang kiểm tra: —";
        _cycleState.Text = "Vòng: —";
        Log("Đã dừng kiểm tra: " + reason);
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
        if (!_profiles.TryGetValue(m.Profile, out var p))
        {
            p = new ProfileState { Profile = m.Profile };
            _profiles[m.Profile] = p;
        }
        var workerRestarted = p.Pid != 0 && m.Pid != 0 && p.Pid != m.Pid;
        if (workerRestarted && _cycle is not null && m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            Log($"[CYCLE_RESET] PRF={m.Profile} Worker PID đổi {p.Pid} -> {m.Pid}; bỏ vòng cũ để tránh trộn dữ liệu.");
            _cycle = null;
            foreach (var key in _pending.Values.Where(x => x.Profile.Equals(m.Profile, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList())
                _pending.Remove(key);
        }
        p.Pid = m.Pid;
        p.Username = m.Username ?? p.Username;
        p.RunState = m.RunState ?? p.RunState;
        p.LiveUrl = m.LiveUrl ?? p.LiveUrl;
        p.ContentTotal = m.ContentTotal > 0 ? m.ContentTotal : p.ContentTotal;
        p.ContentIndex = m.ContentIndex > 0 ? m.ContentIndex : p.ContentIndex;
        p.LastSeenUtc = DateTime.UtcNow;

        if (!m.Type.Equals("HEARTBEAT", StringComparison.OrdinalIgnoreCase))
            Log($"[TELEMETRY_{m.Type}] PRF={m.Profile} pid={m.Pid} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} state={m.RunState} live={ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl)}");

        if (_checking && string.IsNullOrWhiteSpace(_targetProfile)) ChooseNextTarget();
        if (!_checking || !m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            RefreshGrid();
            return;
        }

        if (m.Type.Equals("HEARTBEAT", StringComparison.OrdinalIgnoreCase))
        {
            _ = FollowTargetLiveAsync(m.LiveUrl);
        }
        else if (m.Type.Equals("WILL_SEND", StringComparison.OrdinalIgnoreCase))
        {
            _ = PrepareSendAsync(m);
        }
        else if (m.Type.Equals("SENT", StringComparison.OrdinalIgnoreCase))
        {
            HandleSent(m);
        }
        RefreshGrid();
    }

    async Task FollowTargetLiveAsync(string? liveUrl)
    {
        var wanted = ObserverChromeSession.NormalizeLiveUrl(liveUrl);
        if (wanted.Length == 0 || _observer.IsOnLive(wanted)) return;
        if (_followInFlight && string.Equals(_followRequestedUrl, wanted, StringComparison.OrdinalIgnoreCase)) return;
        _followInFlight = true;
        _followRequestedUrl = wanted;
        try
        {
            Log($"[FOLLOW_BEGIN] PRF={_targetProfile} wanted={wanted} current={_observer.CurrentLiveUrl}");
            var ok = await _observer.EnsureLiveAsync(wanted);
            if (!IsDisposed) BeginInvoke(new Action(() =>
            {
                _observerState.Text = ok ? "Observer: 🟢 bám đúng LIVE" : "Observer: 🟠 chưa bám được LIVE";
                Log($"[FOLLOW_DONE] ok={ok} wanted={wanted} current={_observer.CurrentLiveUrl}");
            }));
        }
        catch (Exception ex)
        {
            if (!IsDisposed) BeginInvoke(new Action(() => Log("FOLLOW LIVE WARN: " + ex)));
        }
        finally
        {
            _followInFlight = false;
        }
    }

    async Task PrepareSendAsync(TelemetryMessage m)
    {
        if (!_checking || !m.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase)) return;
        var key = MakeSendKey(m.Profile, m.Pid, m.SendId);
        var wanted = ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl);
        var ready = wanted.Length > 0 && _observer.Connected && _observer.IsOnLive(wanted);
        Log($"[PREPARE_SEND] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} ready={ready} observerConnected={_observer.Connected} wanted={wanted} current={_observer.CurrentLiveUrl}");

        // Chỉ bắt đầu một vòng khi observer đã đứng đúng LIVE trước lúc Enter.
        if (_cycle is null)
        {
            if (!ready || m.ContentTotal <= 0)
            {
                _ = FollowTargetLiveAsync(m.LiveUrl);
                return;
            }
            _cycle = new CycleState
            {
                Profile = m.Profile,
                Expected = m.ContentTotal,
                StartIndex = m.ContentIndex,
                StartedUtc = DateTime.UtcNow
            };
            Log($"[CYCLE_START] PRF={m.Profile} bắt đầu tại CMT {m.ContentIndex}/{m.ContentTotal}.");
            if (string.IsNullOrWhiteSpace(m.Username))
                Log($"[MATCH_WARN] PRF={m.Profile} không có TikTok handle hợp lệ trong tiktok_auth.json; lượt này phải match theo nội dung và độ tin cậy thấp hơn.");
        }

        if (_cycle.Profile != m.Profile || _cycle.SentCount >= _cycle.Expected) return;
        var willSendUtc = TelemetryUtcOrNow(m.SentAtUtcMs);
        var pending = new PendingSend
        {
            Key = key,
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

        if (ready)
        {
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
        }
        else
        {
            _ = FollowTargetLiveAsync(m.LiveUrl);
        }
    }

    void HandleSent(TelemetryMessage m)
    {
        if (_cycle is null || _cycle.Profile != m.Profile || _cycle.SentCount >= _cycle.Expected) return;
        var key = MakeSendKey(m.Profile, m.Pid, m.SendId);
        if (!_cycle.SeenSendIds.Add(m.SendId)) return;
        _cycle.SentCount++;

        if (!_pending.TryGetValue(key, out var pending))
        {
            var liveUrl = ObserverChromeSession.NormalizeLiveUrl(m.LiveUrl);
            pending = new PendingSend
            {
                Key = key,
                Profile = m.Profile,
                SendId = m.SendId,
                ContentIndex = m.ContentIndex,
                Content = m.Content ?? "",
                Username = m.Username ?? "",
                LiveUrl = liveUrl,
                ObserverReady = false,
                Armed = false,
                ArmedUtc = DateTime.UtcNow,
                WillSendUtc = TelemetryUtcOrNow(m.SentAtUtcMs),
                TimeoutSeconds = GetAdaptiveTimeoutSeconds(liveUrl)
            };
            _pending[key] = pending;
        }

        pending.SentConfirmedUtc = TelemetryUtcOrNow(m.SentAtUtcMs);
        // Never shorten a pending request if this LIVE has just learned a longer delay.
        pending.TimeoutSeconds = Math.Max(pending.TimeoutSeconds, GetAdaptiveTimeoutSeconds(pending.LiveUrl));

        Log($"[SENT_ACCEPT] PRF={m.Profile} send={m.SendId} cmt={m.ContentIndex}/{m.ContentTotal} cycleSent={_cycle.SentCount}/{_cycle.Expected} timeout={pending.TimeoutSeconds:0.0}s");
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
        _pending.Remove(p.Key);
        if (_cycle is null || _cycle.Profile != p.Profile) return;

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
            p.ContentIndex,
            result.ToString(),
            mode,
            p.LiveUrl,
            result == ResultKind.Visible ? latencySeconds : null,
            timeoutUsed));

        Log($"[CMT_RESULT] PRF={p.Profile} send={p.SendId} cmt={p.ContentIndex} result={result} mode={mode} latency={(result == ResultKind.Visible ? latencySeconds.ToString("0.00") + "s" : "-")} timeout={timeoutUsed:0.0}s resolved={_cycle.ResolvedCount}/{_cycle.Expected}");
        UpdateCycleLabel();
        TryFinishCycle();
    }

    void TryFinishCycle()
    {
        if (_cycle is null || _cycle.SentCount < _cycle.Expected || _cycle.ResolvedCount < _cycle.Expected) return;
        var done = _cycle;
        if (_profiles.TryGetValue(done.Profile, out var p))
        {
            p.LastResult = $"{done.Visible}/{done.Expected} hiện • {done.Missing} không thấy • {done.Unknown} không xác minh";
            p.LastCheck = DateTime.Now;
        }
        SaveResult(done);
        Log($"[CYCLE_DONE] PRF={done.Profile} visible={done.Visible}/{done.Expected} missing={done.Missing} unknown={done.Unknown}");
        _cycle = null;
        ChooseNextTarget();
        RefreshGrid();
    }

    void ChooseNextTarget()
    {
        if (!_checking) return;
        var active = _profiles.Values
            .Where(p => DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromSeconds(20)
                        && p.RunState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => NaturalProfileKey(p.Profile), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (active.Count == 0)
        {
            _targetProfile = "";
            _targetState.Text = "Đang kiểm tra: chờ PRF RUNNING...";
            return;
        }

        var currentIndex = active.FindIndex(p => p.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase));
        var next = currentIndex >= 0 ? (currentIndex + 1) % active.Count : (_rotationSeed + 1 + active.Count) % active.Count;
        _rotationSeed = next;
        _targetProfile = active[next].Profile;
        _cycle = null;
        _targetState.Text = $"Đang kiểm tra: {_targetProfile}";
        _cycleState.Text = "Vòng: đang đồng bộ Observer với LIVE...";
        Log($"[TARGET] chuyển sang PRF {_targetProfile}.");
        _ = FollowTargetLiveAsync(active[next].LiveUrl);
    }

    void OnUiTick()
    {
        if (_checking)
        {
            if (string.IsNullOrWhiteSpace(_targetProfile)) ChooseNextTarget();
            else if (!_profiles.TryGetValue(_targetProfile, out var p) || DateTime.UtcNow - p.LastSeenUtc > TimeSpan.FromSeconds(25))
            {
                Log($"[TARGET_SKIP] {_targetProfile} không còn RUNNING/telemetry quá 25s.");
                _cycle = null;
                ChooseNextTarget();
            }
            else if (_cycle is not null && DateTime.UtcNow - _cycle.StartedUtc > TimeSpan.FromMinutes(20))
            {
                Log($"[CYCLE_TIMEOUT] {_cycle.Profile} quá 20 phút; bỏ vòng hiện tại và chuyển PRF tiếp theo.");
                _cycle = null;
                ChooseNextTarget();
            }
        }
        RefreshGrid();
    }

    void UpdateCycleLabel()
    {
        if (_cycle is null) { _cycleState.Text = "Vòng: đang chờ bắt đầu"; return; }
        _cycleState.Text = $"Vòng: {_cycle.SentCount}/{_cycle.Expected} • hiện {_cycle.Visible} • không thấy {_cycle.Missing} • chưa xác minh {_cycle.Unknown}";
    }

    void RefreshGrid()
    {
        var selected = _grid.CurrentRow?.Cells["Profile"].Value?.ToString();
        _grid.Rows.Clear();
        foreach (var p in _profiles.Values.OrderBy(x => NaturalProfileKey(x.Profile), StringComparer.OrdinalIgnoreCase))
        {
            var fresh = DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromSeconds(20);
            var state = fresh ? p.RunState : "OFFLINE";
            var progress = p.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase)
                ? (_cycle is null ? "Đồng bộ LIVE" : $"{_cycle.SentCount}/{_cycle.Expected}")
                : "Chờ";
            _grid.Rows.Add(p.Profile, p.Username, state, progress, p.LastResult, p.LastCheck == default ? "" : p.LastCheck.ToString("HH:mm:ss dd/MM"));
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
                RequestedUrl = _followRequestedUrl
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
                _cycle.StartedUtc
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
                    x.LastCheck
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

    sealed class TelemetryMessage
    {
        public string Type { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Username { get; set; } = "";
        public string RunState { get; set; } = "";
        public string LiveUrl { get; set; } = "";
        public int ContentIndex { get; set; }
        public int ContentTotal { get; set; }
        public string Content { get; set; } = "";
        public long SendId { get; set; }
        public long Seq { get; set; }
        public long SentAtUtcMs { get; set; }
        public int Pid { get; set; }
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
        public DateTime LastCheck { get; set; }
    }

    sealed class PendingSend
    {
        public string Key { get; set; } = "";
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
    }

    sealed class LiveLatencyState
    {
        public Queue<double> Samples { get; } = new();
        public DateTime LastUpdatedUtc { get; set; }
    }

    sealed class CycleState
    {
        public string Profile { get; set; } = "";
        public int Expected { get; set; }
        public int StartIndex { get; set; }
        public int SentCount { get; set; }
        public int ResolvedCount { get; set; }
        public int Visible { get; set; }
        public int Missing { get; set; }
        public int Unknown { get; set; }
        public DateTime StartedUtc { get; set; }
        public HashSet<long> SeenSendIds { get; } = new();
        public List<CommentResult> Details { get; } = new();
    }

    sealed record CommentResult(
        int ContentIndex,
        string Result,
        string MatchMode,
        string LiveUrl,
        double? LatencySeconds,
        double TimeoutSeconds);
    enum ResultKind { Visible, Missing, Unknown }
}
