using System.Text;
using System.Text.Json;
using System.Threading;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    string _managedDetailSnapshot = "Bước: —";
    long _managedWindowHandleSnapshot;

    // Guard BAN độc lập với AutomationEngine:
    // Manager poll status mỗi giây, nhưng trước đây Worker chỉ dò trang BAN khi
    // workflow automation đang RUNNING. Nếu engine vừa STOPPED/đang đứng ở một
    // bước dài thì trang TikTok có thể hiện hard-ban mà Manager không nhận được
    // marker để note Excel + đóng/xóa profile. Timer này chỉ đọc DOM qua CDP và
    // latch một lần cho tới khi Worker bị đóng.
    System.Windows.Forms.Timer? _managedFatalBanTimer;
    bool _managedFatalBanProbeBusy;
    bool _managedFatalBanLatched;
    DateTime _managedFatalBanProbeErrorLogUtc = DateTime.MinValue;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Interlocked.Exchange(ref _managedWindowHandleSnapshot, Handle.ToInt64());
        InitializeManagedFatalBanWatcher();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        DisposeManagedFatalBanWatcher();
        Interlocked.Exchange(ref _managedWindowHandleSnapshot, 0);
        base.OnHandleDestroyed(e);
    }

    void InitializeManagedFatalBanWatcher()
    {
        if (_managedFatalBanTimer is not null) return;

        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += async (_, _) => await ProbeManagedFatalBanAsync();
        _managedFatalBanTimer = timer;
        timer.Start();
    }

    void DisposeManagedFatalBanWatcher()
    {
        var timer = _managedFatalBanTimer;
        _managedFatalBanTimer = null;
        if (timer is null) return;
        try { timer.Stop(); } catch { }
        try { timer.Dispose(); } catch { }
    }

    async Task ProbeManagedFatalBanAsync()
    {
        if (_managedFatalBanLatched
            || _managedFatalBanProbeBusy
            || IsDisposed
            || Disposing
            || !_chrome.Connected)
        {
            return;
        }

        _managedFatalBanProbeBusy = true;

        try
        {
            var marker = await _chrome.DetectFatalFeatureRestrictionAsync();
            if (string.IsNullOrWhiteSpace(marker)) return;

            _managedFatalBanLatched = true;
            _startupPreparationState = "ACCOUNT_BANNED";

            const string reason =
                "TikTok báo tài khoản đã vi phạm quy tắc và hiện không thể sử dụng tính năng này. Tool đã dừng để Manager ghi BAN và dọn profile.";

            _log.Error(
                $"[MANAGED_FATAL_BAN_DETECTED] marker={marker} running={_engine.Running} paused={_engine.Paused}");

            if (_engine.Running)
            {
                _engine.Stop(reason);
            }
            else
            {
                // Khi Automation đã STOPPED, vẫn phải đẩy marker vào snapshot status
                // để Manager Ban watcher nhìn thấy và chạy note/close/delete.
                OnEngineStatus("ĐÃ DỪNG\n" + reason);
            }
        }
        catch (Exception ex)
        {
            // Probe nền không được làm Worker lỗi vì CDP vừa reconnect/Chrome vừa đóng.
            // Chỉ log tối đa một lần/phút để tránh spam trên VM chậm.
            var now = DateTime.UtcNow;
            if (now - _managedFatalBanProbeErrorLogUtc >= TimeSpan.FromMinutes(1))
            {
                _managedFatalBanProbeErrorLogUtc = now;
                _log.Warn($"[MANAGED_FATAL_BAN_PROBE_ERROR] {ex.Message}");
            }
        }
        finally
        {
            _managedFatalBanProbeBusy = false;
        }
    }

    sealed class ManagedIdentityUpdateRequest
    {
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarPath { get; set; } = "";
        public string Bio { get; set; } = "";
        public bool SkipIfNameCooldown { get; set; }
        public string[] KnownDisplayNames { get; set; } = Array.Empty<string>();
        public bool VerifyExistingState { get; set; }
        public bool FastNameGuardMode { get; set; }
        public bool SkipPostSaveNameVerification { get; set; }
    }

    sealed class ManagedNameGuardProbeRequest
    {
        public string Username { get; set; } = "";
        public string[] AllowedDisplayNames { get; set; } = Array.Empty<string>();
    }

    public Task<string> HandleManagedCommandAsync(string rawCommand)
    {
        var raw = (rawCommand ?? "").Trim();
        var separator = raw.IndexOf('|');
        var command = (separator >= 0 ? raw[..separator] : raw).Trim().ToLowerInvariant();
        var commandPayload = separator >= 0 ? raw[(separator + 1)..] : "";
        if (command == "ping") return Task.FromResult("pong");
        if (IsDisposed || Disposing) return Task.FromResult("disposed");

        // Status is the hot IPC path (Manager polls every open profile once a
        // second).  It no longer needs to marshal to WinForms just to read a few
        // values.  The detail/window values are snapshots maintained by the UI,
        // while engine/chrome flags are safe lightweight reads.
        if (command == "status") return Task.FromResult(BuildManagedStatusResponse());
        if (command == "message_reply_status") return Task.FromResult(BuildManagedMessageReplyStatusResponse());
        if (command == "message_reply_log") return Task.FromResult(BuildManagedMessageReplyLogResponse());
        if (command == "message_reply_stop") return Task.FromResult(StopManagedMessageReply());

        return InvokeManagedOnUiAsync(async () =>
        {
            switch (command)
            {
                case "reload_config":
                    if (_engine.Running || IsMessageReplyRunning) return "busy_running";
                    _settings = _settingsService.Load();
                    ApplyManagedStartupOverrides();
                    ApplySelectedProfileToSettings(logSelection: false);
                    LoadToUi();
                    ApplyVmOptimizationSettings();
                    _log.Info("[MANAGED_CONFIG_RELOADED] source=manager_default_sync");
                    return "reloaded";
                case "apply_vm_mode":
                {
                    var requestedMode = (commandPayload ?? "").Trim();
                    if (!Enum.TryParse<ToolTikTokV11.Models.VmOptimizationMode>(
                            requestedMode,
                            ignoreCase: true,
                            out var vmMode))
                    {
                        return "invalid_vm_mode";
                    }

                    _settings.VmOptimization.Mode = vmMode;
                    _settingsService.Save(_settings);
                    LoadVmOptimizationToUi();
                    ApplyVmOptimizationSettings();
                    if (_chrome.Connected)
                        await _chrome.ApplyVmRuntimePolicyAsync();

                    var appliedName = vmMode switch
                    {
                        ToolTikTokV11.Models.VmOptimizationMode.VmSafe => "VmSafe",
                        ToolTikTokV11.Models.VmOptimizationMode.VmMax => "VmMax",
                        _ => "Normal"
                    };
                    _log.Info($"[MANAGED_VM_MODE_APPLIED] mode={appliedName} source=manager_global");
                    return "applied|" + appliedName;
                }
                case "start":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    if (IsMessageReplyRunning) return "message_reply_running";
                    await StartAsync();
                    return _engine.Running ? "started" : "not_started";
                case "start_auto":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    if (IsMessageReplyRunning) return "message_reply_running";
                    await StartAsync(suppressDialogs: true);
                    return _engine.Running ? "started" : "not_started";
                case "pause":
                    if (_engine.Running && !_engine.Paused) _engine.TogglePause();
                    return _engine.Paused ? "paused" : "not_paused";
                case "resume":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    if (_engine.Running && _engine.Paused) _engine.TogglePause();
                    return _engine.Running && !_engine.Paused ? "running" : "not_running";
                case "stop":
                    _engine.Stop();
                    return "stopped";
                case "emergency_stop":
                {
                    // Khác STOP thường: Dừng khẩn cấp phải chờ vòng AutomationEngine
                    // thật sự unwind. Nếu quá hạn, Manager sẽ fallback kill CHỈ Worker
                    // (không kill process tree) để giữ nguyên Chrome.
                    StopManagedMessageReply();
                    _engine.Stop("Dừng khẩn cấp từ Manager");
                    var fullyStopped = await _engine.WaitForStopAsync(TimeSpan.FromSeconds(2.5));
                    return fullyStopped ? "stopped" : "stop_pending";
                }
                case "launch":
                    await LaunchChromeAsync();
                    if (!_chrome.Connected) return "not_opened";
                    return MapManagedLaunchState();
                case "launch_auto":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    // Auto Profile không giữ cả hàng đợi 15 phút khi gặp CAPTCHA.
                    // Chrome vẫn được giữ nguyên để người dùng xử lý thủ công sau.
                    await LaunchChromeAsync(stopOnCaptcha: true, suppressDialogs: true);
                    if (!_chrome.Connected) return "not_opened";
                    return MapManagedLaunchState();
                case "runtime_relogin_auto":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    if (IsMessageReplyRunning) return "message_reply_running";

                    // runtime_login_lost đã được Worker xác nhận bằng popup đăng nhập
                    // lặp 3/3 sau Enter. Ở recovery này KHÔNG được tin lại cookie/session
                    // cũ ngay sau khi reopen, vì chính cookie stale làm launch_auto báo
                    // "opened" rồi Start lại automation -> popup login -> loop.
                    //
                    // Vẫn gọi nguyên flow đăng nhập hiện có: chỉ xóa cookie của phiên
                    // Chrome vừa reopen rồi gọi PrepareTikTokProfileStartupAsync().
                    // Flow đó tiếp tục giữ toàn bộ xử lý sẵn có: form login, CAPTCHA,
                    // TOTP/2FA, login failure, hard BAN và trạng thái startup.
                    await LaunchChromeAsync(stopOnCaptcha: true, suppressDialogs: true);
                    if (!_chrome.Connected) return "not_opened";

                    try
                    {
                        _log.Warn(
                            "[RUNTIME_RELOGIN_FORCE_BEGIN] reason=runtime_login_lost action=CLEAR_STALE_BROWSER_COOKIES_THEN_EXISTING_LOGIN_FLOW");

                        // Chỉ xóa cookie thuộc TikTok, không đụng cookie website khác
                        // trong cùng Chrome profile. Mục đích là buộc
                        // IsTikTokSessionActiveAsync không thể kết luận nhầm từ cookie
                        // TikTok stale sau khi runtime đã xác nhận logout.
                        var allCookies = await CallChromeCdpForRuntimeReloginAsync(
                            "Network.getAllCookies",
                            null);

                        var deletedTikTokCookies = 0;
                        if (allCookies.ValueKind == JsonValueKind.Object
                            && allCookies.TryGetProperty("cookies", out var cookies)
                            && cookies.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cookie in cookies.EnumerateArray())
                            {
                                var name = cookie.TryGetProperty("name", out var nameValue)
                                    ? nameValue.GetString() ?? ""
                                    : "";
                                var domain = cookie.TryGetProperty("domain", out var domainValue)
                                    ? domainValue.GetString() ?? ""
                                    : "";
                                var path = cookie.TryGetProperty("path", out var pathValue)
                                    ? pathValue.GetString() ?? "/"
                                    : "/";

                                if (string.IsNullOrWhiteSpace(name)
                                    || domain.IndexOf("tiktok.com", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }

                                await CallChromeCdpForRuntimeReloginAsync(
                                    "Network.deleteCookies",
                                    new { name, domain, path });
                                deletedTikTokCookies++;
                            }
                        }

                        _log.Warn(
                            $"[RUNTIME_RELOGIN_TIKTOK_COOKIES_CLEARED] count={deletedTikTokCookies}");

                        _startupPreparationState = "";

                        // openLiveWhenReady=false: chỉ hoàn tất đăng nhập ở đây.
                        // Manager sẽ gọi start_auto sau khi reply=opened; StartAsync sẽ
                        // đi tiếp workflow LIVE theo logic hiện tại.
                        await PrepareTikTokProfileStartupAsync(
                            openLiveWhenReady: false,
                            stopOnCaptcha: true);

                        var reloginState = MapManagedLaunchState();
                        _log.Warn(
                            $"[RUNTIME_RELOGIN_FORCE_RESULT] state={_startupPreparationState} reply={reloginState}");
                        return reloginState;
                    }
                    catch (Exception ex)
                    {
                        _startupPreparationState = "ERROR";
                        _log.Error(
                            $"[RUNTIME_RELOGIN_FORCE_ERROR] {ex}");
                        return "startup_error";
                    }
                case "captcha_check":
                    if (!_chrome.Connected) return "not_connected";
                    try { return await _chrome.IsCaptchaVisibleAsync() ? "captcha" : "clear"; }
                    catch (Exception ex)
                    {
                        _log.Warn("[CAPTCHA_CHECK] " + ex.Message);
                        return "probe_error";
                    }
                case "connect":
                    await ConnectChromeAsync();
                    return _chrome.Connected ? "connected" : "disconnected";
                case "close_chrome":
                    StopManagedMessageReply();
                    return await CloseChromeAsync();
                case "message_reply_start":
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    return await StartManagedMessageReplyAsync(commandPayload);
                case "identity_ready":
                {
                    if (!_chrome.Connected) return "not_connected";
                    try
                    {
                        // TikTok đôi lúc vào đúng trang cá nhân nhưng SPA/session tạm hiện
                        // như chưa đăng nhập. Tự F5 tối đa 2 lần trước khi báo not_logged_in.
                        return await _chrome.EnsureTikTokIdentitySessionReadyAsync()
                            ? "ready"
                            : "not_logged_in";
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("[TIKTOK_IDENTITY_READY_PROBE] " + ex.Message);
                        return "probe_error";
                    }
                }
                case "identity_name_probe":
                {
                    try
                    {
                        if (!_chrome.Connected)
                            return JsonSerializer.Serialize(new { ok = false, currentName = "", matched = false, currentHandle = "", source = "", message = "Chrome chưa kết nối." });
                        if (string.IsNullOrWhiteSpace(commandPayload))
                            throw new InvalidOperationException("Thiếu payload Name Guard.");

                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(commandPayload));
                        var request = JsonSerializer.Deserialize<ManagedNameGuardProbeRequest>(
                            json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new InvalidOperationException("Payload Name Guard không hợp lệ.");

                        var result = await _chrome.ProbeCurrentAccountDisplayNameAsync(
                            request.Username,
                            request.AllowedDisplayNames);
                        return JsonSerializer.Serialize(new
                        {
                            ok = result.Ok,
                            currentName = result.CurrentName,
                            matched = result.Matched,
                            currentHandle = result.CurrentHandle,
                            source = result.Source,
                            message = result.Message
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("[NAME_GUARD_PROBE] " + ex.Message);
                        return JsonSerializer.Serialize(new { ok = false, currentName = "", matched = false, currentHandle = "", source = "", message = ex.Message });
                    }
                }
                case "update_tiktok_identity":
                {
                    if (IsManagerEmergencyStopActive()) return "emergency_stopped";
                    try
                    {
                        if (IsMessageReplyRunning)
                            throw new InvalidOperationException("Profile đang xử lý Tin nhắn TikTok. Hãy dừng mục Tin nhắn trước khi cập nhật tên/ảnh.");
                        if (string.IsNullOrWhiteSpace(commandPayload))
                            throw new InvalidOperationException("Thiếu payload đổi tên/ảnh TikTok.");
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(commandPayload));
                        var request = JsonSerializer.Deserialize<ManagedIdentityUpdateRequest>(
                            json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new InvalidOperationException("Payload đổi tên/ảnh TikTok không hợp lệ.");
                        if (!request.FastNameGuardMode)
                        {
                            if (!await _chrome.EnsureTikTokIdentitySessionReadyAsync())
                                throw new InvalidOperationException("TikTok chưa đăng nhập sau khi Tool đã F5 thử lại 2 lần. Hãy kiểm tra tài khoản trên Chrome rồi cập nhật tên/ảnh lại.");

                            // Luồng Tên/ảnh đầy đủ giữ recovery cũ. Name Guard nhanh đã đứng
                            // ở trang Hồ sơ nên bỏ các bước chuẩn bị/F5 lặp này.
                            await _chrome.EnsureTikTokEditProfileEntranceReadyAsync();
                        }

                        var result = await _chrome.UpdateTikTokProfileIdentityAsync(
                            request.Username, request.DisplayName, request.AvatarPath, request.Bio,
                            request.SkipIfNameCooldown, request.KnownDisplayNames, request.VerifyExistingState,
                            request.FastNameGuardMode, request.SkipPostSaveNameVerification);
                        return JsonSerializer.Serialize(new
                        {
                            ok = true,
                            nameChanged = result.NameChanged,
                            avatarChanged = result.AvatarChanged,
                            bioChanged = result.BioChanged,
                            nameCooldown = result.NameCooldown,
                            alreadyConfigured = result.AlreadyConfigured,
                            skipped = result.Skipped,
                            message = result.Message,
                            error = ""
                        });
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            ok = false,
                            nameChanged = false,
                            avatarChanged = false,
                            bioChanged = false,
                            nameCooldown = false,
                            alreadyConfigured = false,
                            skipped = false,
                            message = "",
                            error = ex.Message
                        });
                    }
                }
                case "view_chrome":
                {
                    var profilePath = _startupOptions.ProfilePath;
                    if (string.IsNullOrWhiteSpace(profilePath)) return "window_not_found";
                    if (!_chrome.Connected) return "not_connected";

                    // Resolve PID/HWND theo đúng CDP port + profile path và retry
                    // EnumWindows tại Worker. Không restore, restart hoặc đổi
                    // foreground ở đây; Manager vẫn là nơi điều khiển cửa sổ.
                    var resolution = await _chrome.ResolveManagedWindowAsync(
                        profilePath,
                        _settings.ChromePort,
                        windowAttempts: 8,
                        retryDelayMs: 250);
                    return JsonSerializer.Serialize(resolution);
                }
                case "show":
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Show();
                    return "shown";
                case "shutdown":
                    StopManagedMessageReply();
                    _managedShutdownRequested = true;
                    BeginInvoke(new Action(Close));
                    return "bye";
                default:
                    return "unknown";
            }
        });
    }

    async Task<JsonElement> CallChromeCdpForRuntimeReloginAsync(string method, object? parameters)
    {
        // ChromeController.Cdp khong public, nen MainForm khong duoc truy cap truc tiep
        // (CS0122). Dung reflection chi o recovery nay de goi CHINH CdpClient dang duoc
        // ChromeController quan ly; khong tao them ket noi CDP va khong thay doi flow cu.
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        object? cdp = null;
        var chromeType = _chrome.GetType();

        try
        {
            var cdpProperty = chromeType.GetProperty("Cdp", flags);
            if (cdpProperty is not null)
                cdp = cdpProperty.GetValue(_chrome);
        }
        catch { }

        if (cdp is null)
        {
            // Fallback neu Cdp duoc luu bang field thay vi property o mot build khac.
            var cdpField = chromeType.GetField("Cdp", flags)
                ?? chromeType.GetField("_cdp", flags);
            if (cdpField is not null)
                cdp = cdpField.GetValue(_chrome);
        }

        if (cdp is null)
            throw new InvalidOperationException(
                "Khong lay duoc CdpClient hien tai tu ChromeController de runtime relogin.");

        System.Reflection.MethodInfo? callAsync = null;
        foreach (var candidate in cdp.GetType().GetMethods(flags))
        {
            if (!string.Equals(candidate.Name, "CallAsync", StringComparison.Ordinal))
                continue;

            var ps = candidate.GetParameters();
            if (ps.Length != 3)
                continue;

            if (ps[0].ParameterType != typeof(string))
                continue;

            if (ps[2].ParameterType != typeof(CancellationToken))
                continue;

            callAsync = candidate;
            break;
        }

        if (callAsync is null)
            throw new MissingMethodException(
                cdp.GetType().FullName,
                "CallAsync(string, ..., CancellationToken)");

        object? pending;
        try
        {
            pending = callAsync.Invoke(
                cdp,
                new object?[] { method, parameters, CancellationToken.None });
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }

        if (pending is Task<JsonElement> jsonTask)
            return await jsonTask;

        if (pending is Task task)
        {
            await task;
            var resultProperty = task.GetType().GetProperty("Result", flags);
            var result = resultProperty?.GetValue(task);
            if (result is JsonElement json)
                return json;
        }

        throw new InvalidOperationException(
            $"CDP CallAsync tra ve kieu khong ho tro: {pending?.GetType().FullName ?? "null"}.");
    }

    string MapManagedLaunchState()
        => _startupPreparationState switch
        {
            "CAPTCHA_REQUIRED" => "captcha_required",
            "TOTP_REQUIRED" => "totp_required",
            "LOGIN_REQUIRED" => "login_required",
            "ACCOUNT_BANNED" => "account_banned",
            "LOGIN_FAILED" => "login_failed",
            "LOGIN_FORM_NOT_FOUND" => "login_form_not_found",
            "ERROR" => "startup_error",
            _ => "opened"
        };

    string BuildManagedStatusResponse()
    {
        var profile = _managedMode && !string.IsNullOrWhiteSpace(_startupOptions.ProfileName)
            ? _startupOptions.ProfileName
            : CurrentProfileName;
        var periodic = _engine.GetPeriodicF5Snapshot();
        // Tận dụng RuntimeStatsTracker đã có trong Worker; Dashboard chỉ đọc snapshot này,
        // không tạo thêm bộ đếm/thời gian riêng ở Manager.
        var runtime = _runtimeStats.GetSnapshot();
        var f5RemainingSec = periodic.Enabled && periodic.DueAt != DateTime.MaxValue
            ? Math.Max(0, (int)Math.Ceiling((periodic.DueAt - DateTime.Now).TotalSeconds))
            : -1;
        return JsonSerializer.Serialize(new
        {
            Profile = profile,
            State = "WORKER_READY",
            RunState = !_engine.Running ? "STOPPED" : _engine.Paused ? "PAUSED" : "RUNNING",
            Detail = Volatile.Read(ref _managedDetailSnapshot),
            Chrome = _chrome.Connected ? "CONNECTED" : "DISCONNECTED",
            CdpPort = _settings.ChromePort,
            Pid = Environment.ProcessId,
            WindowHandle = Interlocked.Read(ref _managedWindowHandleSnapshot),
            ChromeWindowHandle = _chrome.GetManagedWindowHandleValue(),
            Viewer = _engine.LastViewerValue,
            Step = _engine.CurrentStep,
            Rounds = _engine.Rounds,
            TotalRunSeconds = Math.Max(0L, (long)Math.Round(runtime.Total.TotalSeconds)),
            F5Enabled = periodic.Enabled,
            F5RemainingSec = f5RemainingSec
            ,TikTokStartupState = _startupPreparationState
            ,RuntimeAuthState = _engine.RuntimeLoginLostConfirmed ? "LOGOUT_CONFIRMED" : "NORMAL"
            ,RuntimeAuthDetail = _engine.RuntimeLoginLostDetail
            ,MessageReplyRunning = IsMessageReplyRunning
        });
    }

    Task<string> InvokeManagedOnUiAsync(Func<Task<string>> action)
    {
        if (!InvokeRequired) return action();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try { tcs.TrySetResult(await action()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        return tcs.Task;
    }
}
