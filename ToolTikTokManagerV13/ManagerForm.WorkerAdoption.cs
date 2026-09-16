using System.Diagnostics;
using System.Text.Json;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    bool _startupWorkerAdoptionStarted;
    readonly System.Windows.Forms.Timer _managerUiHeartbeatTimer = new()
    {
        Interval = 1000,
        Enabled = false
    };
    System.Threading.Timer? _managerUiWatchdogTimer;
    long _managerUiLastHeartbeatTicks;
    int _managerUiStallReported;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        StartManagerUiWatchdog();

        // Fallback sau khi Form đã hiện: nếu patch UI trước đó đổi label toolbar
        // khiến lần inject trong constructor bị trượt, thử lại ở đây.
        try
        {
            InjectProfileLifecycleDiagnosticButton();
        }
        catch { }

        try
        {
            InitializeAutoReplacementManualControl();
        }
        catch (Exception ex)
        {
            try
            {
                _log.Warn(
                    $"[AUTO_REPLACE_MANUAL_UI_INIT_WARN] error={ex.Message}");
            }
            catch { }
        }

        if (_startupWorkerAdoptionStarted)
            return;

        _startupWorkerAdoptionStarted = true;

        try
        {
            // Cho cửa sổ Manager hoàn tất vẽ trước rồi mới dò Worker cũ.
            await Task.Delay(250);
            await AdoptExistingWorkersOnManagerStartupAsync();
        }
        catch (Exception ex)
        {
            try { _log.Warn("[WORKER_ADOPT_STARTUP_ERROR] " + ex.Message); } catch { }
        }
    }

    void StartManagerUiWatchdog()
    {
        if (_managerUiHeartbeatTimer.Enabled)
            return;

        Interlocked.Exchange(ref _managerUiLastHeartbeatTicks, DateTime.UtcNow.Ticks);

        _managerUiHeartbeatTimer.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref _managerUiLastHeartbeatTicks, DateTime.UtcNow.Ticks);

            if (Interlocked.Exchange(ref _managerUiStallReported, 0) != 0)
            {
                try { _log.Info("[MANAGER_UI_RECOVERED] UI Manager đã phản hồi lại."); } catch { }
            }
        };
        _managerUiHeartbeatTimer.Start();

        _managerUiWatchdogTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    if (_closing || IsDisposed || Disposing)
                        return;

                    var lastTicks = Interlocked.Read(ref _managerUiLastHeartbeatTicks);
                    if (lastTicks <= 0)
                        return;

                    var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
                    if (elapsed < TimeSpan.FromSeconds(8))
                        return;

                    if (Interlocked.Exchange(ref _managerUiStallReported, 1) != 0)
                        return;

                    var message =
                        $"[MANAGER_UI_STALL] UI không phản hồi khoảng {elapsed.TotalSeconds:0}s. " +
                        "Worker là process độc lập nên Automation vẫn có thể đang chạy. " +
                        "Có thể đóng riêng Manager và mở lại để tự nhận Worker cũ.";

                    try { _log.Warn(message); }
                    catch
                    {
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(_baseDir, "manager_ui_stall.log"),
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                        }
                        catch { }
                    }
                }
                catch { }
            },
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        FormClosed += (_, _) =>
        {
            try { _managerUiHeartbeatTimer.Stop(); } catch { }
            try { _managerUiWatchdogTimer?.Dispose(); } catch { }
            _managerUiWatchdogTimer = null;
        };
    }

    async Task AdoptExistingWorkersOnManagerStartupAsync()
    {
        if (_closing || IsDisposed || Disposing)
            return;

        var candidates = _contexts.Values
            .Where(ctx => ctx.Worker is null || IsWorkerProcessExited(ctx))
            .OrderBy(ctx => ctx.Profile.Name, NaturalProfileNameOrder)
            .ToList();

        if (candidates.Count == 0)
            return;

        _log.Info($"[WORKER_ADOPT_SCAN_START] profiles={candidates.Count}");

        // Dò song song có giới hạn để 50-60 profile không giữ UI.
        using var gate = new SemaphoreSlim(12, 12);

        var tasks = candidates.Select(async ctx =>
        {
            await gate.WaitAsync();
            try
            {
                return (Context: ctx, Snapshot: await ProbeExistingWorkerSnapshotAsync(ctx));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var probes = await Task.WhenAll(tasks);
        var adopted = 0;
        var selectedBefore = _tabs.SelectedTab;

        foreach (var probe in probes)
        {
            if (_closing)
                break;

            var snapshot = probe.Snapshot;
            if (snapshot is null || snapshot.Pid <= 0)
                continue;

            var ctx = probe.Context;

            try
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(snapshot.Pid);
                    if (process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                // Manager restart chỉ cần nhận lại Worker còn thực sự hoạt động.
                // Worker STOPPED còn sót từ phiên trước không được tự tạo tab PRF
                // khi vừa mở Tool; dọn Worker orphan rồi để profile ở trạng thái đóng.
                // Ngoại lệ: nếu Manager đang Dừng khẩn cấp thì tuyệt đối không cleanup;
                // cho nhánh adoption bên dưới nhận lại Worker/tab để giữ và nhìn thấy
                // đúng hiện trạng đã được đóng băng.
                if (string.Equals(
                        snapshot.RunState,
                        RuntimeStateStopped,
                        StringComparison.OrdinalIgnoreCase)
                    && !IsAutomationHalted)
                {
                    _log.Info(
                        $"[WORKER_ADOPT_SKIP_STOPPED] profile={ctx.Profile.Name} pid={snapshot.Pid} action=shutdown_orphan_no_tab");

                    try
                    {
                        await SendPipeAsync(
                            ctx.Profile.Name,
                            "shutdown",
                            TimeSpan.FromSeconds(3));
                    }
                    catch (Exception shutdownEx)
                    {
                        _log.Warn(
                            $"[WORKER_ADOPT_SKIP_STOPPED_SHUTDOWN_WARN] profile={ctx.Profile.Name} pid={snapshot.Pid} error={shutdownEx.Message}");
                    }

                    try
                    {
                        if (!await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5)))
                        {
                            process.Kill(true);
                            if (!await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(3)))
                            {
                                _log.Warn(
                                    $"[WORKER_ADOPT_SKIP_STOPPED_STILL_ALIVE] profile={ctx.Profile.Name} pid={snapshot.Pid}");
                            }
                        }
                    }
                    catch (Exception killEx)
                    {
                        _log.Warn(
                            $"[WORKER_ADOPT_SKIP_STOPPED_KILL_WARN] profile={ctx.Profile.Name} pid={snapshot.Pid} error={killEx.Message}");
                    }

                    try { process.Dispose(); } catch { }
                    ctx.Worker = null;
                    ctx.WorkerWindow = IntPtr.Zero;
                    ctx.Detached = false;
                    ctx.Opening = false;

                    // Worker STOPPED còn sót mà Chrome của cùng ProfilePath vẫn sống
                    // sẽ tạo một orphan khó nhìn thấy sau khi Manager restart. Dọn đúng
                    // Chrome gắn với ProfilePath này trước khi bỏ qua profile. Đây chỉ là
                    // cleanup startup của Worker STOPPED; không tác động profile khác.
                    try
                    {
                        var stoppedPids = await Task.Run(
                            () => ChromeProfileNameSyncService.StopChromeUsingProfile(ctx.Profile.ProfilePath));

                        await Task.Delay(250);

                        var verify = await Task.Run(
                            () => ChromeProfileNameSyncService.ProbeProfileProcesses(ctx.Profile.ProfilePath));

                        if (verify.Succeeded && verify.ProcessIds.Count > 0)
                        {
                            var retryPids = await Task.Run(
                                () => ChromeProfileNameSyncService.StopChromeUsingProfile(ctx.Profile.ProfilePath));
                            await Task.Delay(500);
                            verify = await Task.Run(
                                () => ChromeProfileNameSyncService.ProbeProfileProcesses(ctx.Profile.ProfilePath));

                            _log.Warn(
                                $"[WORKER_ADOPT_SKIP_STOPPED_CHROME_RETRY] profile={ctx.Profile.Name} first={string.Join(",", stoppedPids)} retry={string.Join(",", retryPids)} remaining={(verify.Succeeded ? string.Join(",", verify.ProcessIds) : "UNKNOWN")}");
                        }

                        if (!verify.Succeeded)
                        {
                            _log.Warn(
                                $"[WORKER_ADOPT_SKIP_STOPPED_CHROME_VERIFY_WARN] profile={ctx.Profile.Name} error={verify.Error}");
                        }
                        else if (verify.ProcessIds.Count > 0)
                        {
                            _log.Warn(
                                $"[WORKER_ADOPT_SKIP_STOPPED_CHROME_STILL_ALIVE] profile={ctx.Profile.Name} pids={string.Join(",", verify.ProcessIds)}");
                        }
                        else
                        {
                            _log.Info(
                                $"[WORKER_ADOPT_SKIP_STOPPED_CHROME_CLEAN] profile={ctx.Profile.Name} stopped={string.Join(",", stoppedPids)}");
                        }
                    }
                    catch (Exception chromeCleanupEx)
                    {
                        _log.Warn(
                            $"[WORKER_ADOPT_SKIP_STOPPED_CHROME_WARN] profile={ctx.Profile.Name} error={chromeCleanupEx.Message}");
                    }

                    continue;
                }

                if (ctx.Worker is not null && !ReferenceEquals(ctx.Worker, process))
                {
                    try { ctx.Worker.Dispose(); } catch { }
                }

                process.EnableRaisingEvents = true;
                ctx.Worker = process;
                ctx.WorkerWindow = snapshot.WindowHandle > 0
                    ? new IntPtr(snapshot.WindowHandle)
                    : IntPtr.Zero;
                ctx.Detached = false;
                ctx.Opening = false;
                ctx.LastSnapshot = snapshot;
                ctx.LastStatusRefreshUtc = DateTime.UtcNow;
                ctx.ConsecutiveStatusPollFailures = 0;
                ctx.LastStatusPollFailure = "";

                ApplyWorkerSnapshotRuntimeState(ctx, snapshot);
                process.Exited += (_, _) => OnWorkerProcessExited(ctx, process);

                EnsureTab(ctx);

                SetStatus(
                    ctx,
                    $"Đã nhận lại Worker PID {snapshot.Pid} | {GetEffectiveRuntimeState(ctx)}",
                    GetRuntimeStateColor(GetEffectiveRuntimeState(ctx)));

                if (ctx.Host is not null && !ctx.Host.IsDisposed)
                {
                    var attached = await AttachWorkerWithRetryAsync(
                        ctx,
                        maxAttempts: 8,
                        delayMs: 120,
                        reason: "manager_restart_adopt");

                    if (!attached)
                    {
                        _log.Warn(
                            $"[WORKER_ADOPT_EMBED_PENDING] profile={ctx.Profile.Name} pid={snapshot.Pid} hwnd={snapshot.WindowHandle}");
                    }
                }

                adopted++;
                _log.Info(
                    $"[WORKER_ADOPT_OK] profile={ctx.Profile.Name} pid={snapshot.Pid} hwnd={snapshot.WindowHandle} run={snapshot.RunState}");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[WORKER_ADOPT_FAILED] profile={ctx.Profile.Name} pid={snapshot.Pid} error={ex.Message}");
            }
        }

        if (selectedBefore is not null
            && !selectedBefore.IsDisposed
            && selectedBefore.Parent == _tabs)
        {
            SelectTabPageSafely(selectedBefore);
        }

        EnsureAddTab();
        RefreshAvailability();
        UpdateTitle();

        _log.Info($"[WORKER_ADOPT_SCAN_DONE] adopted={adopted}/{candidates.Count}");

        if (IsAutomationHalted && adopted > 0)
        {
            var stopped = await StopAllWorkerAutomationForEmergencyAsync();
            _log.Warn(
                $"[EMERGENCY_STOP_STARTUP_ENFORCED] adopted={adopted} workersStopped={stopped}");
        }
    }

    async Task<WorkerSnapshot?> ProbeExistingWorkerSnapshotAsync(ProfileContext ctx)
    {
        // Không dùng EnsureWorkerAsync ở đây: mục đích là nhận Worker cũ,
        // tuyệt đối không được spawn Worker mới khi Manager vừa mở lại.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var raw = await SendPipeAsync(
                    ctx.Profile.Name,
                    "status",
                    TimeSpan.FromMilliseconds(700));

                var snapshot = JsonSerializer.Deserialize<WorkerSnapshot>(
                    raw,
                    WorkerSnapshotJson);

                if (snapshot is null)
                    throw new InvalidDataException("status rỗng");

                if (!snapshot.Profile.Equals(
                        ctx.Profile.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"profile mismatch: {snapshot.Profile}");
                }

                if (snapshot.CdpPort != ctx.Profile.CdpPort)
                {
                    throw new InvalidDataException(
                        $"CDP mismatch: worker={snapshot.CdpPort}, expected={ctx.Profile.CdpPort}");
                }

                if (snapshot.Pid <= 0)
                    throw new InvalidDataException("Worker không trả PID.");

                if (!IsWorkerReportedRuntimeState(snapshot.RunState))
                    throw new InvalidDataException("RunState không hợp lệ: " + snapshot.RunState);

                return snapshot;
            }
            catch
            {
                if (attempt < 3)
                    await Task.Delay(120);
            }
        }

        return null;
    }
}
