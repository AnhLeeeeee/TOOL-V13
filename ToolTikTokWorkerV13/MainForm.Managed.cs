using System.Text;
using System.Text.Json;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    string _managedDetailSnapshot = "Bước: —";
    long _managedWindowHandleSnapshot;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Interlocked.Exchange(ref _managedWindowHandleSnapshot, Handle.ToInt64());
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Interlocked.Exchange(ref _managedWindowHandleSnapshot, 0);
        base.OnHandleDestroyed(e);
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
                case "start":
                    if (IsMessageReplyRunning) return "message_reply_running";
                    await StartAsync();
                    return _engine.Running ? "started" : "not_started";
                case "pause":
                    if (_engine.Running && !_engine.Paused) _engine.TogglePause();
                    return _engine.Paused ? "paused" : "not_paused";
                case "resume":
                    if (_engine.Running && _engine.Paused) _engine.TogglePause();
                    return _engine.Running && !_engine.Paused ? "running" : "not_running";
                case "stop":
                    _engine.Stop();
                    return "stopped";
                case "launch":
                    await LaunchChromeAsync();
                    if (!_chrome.Connected) return "not_opened";
                    return _startupPreparationState switch
                    {
                        "CAPTCHA_REQUIRED" => "captcha_required",
                        "TOTP_REQUIRED" => "totp_required",
                        "LOGIN_REQUIRED" => "login_required",
                        "LOGIN_FAILED" => "login_failed",
                        "LOGIN_FORM_NOT_FOUND" => "login_form_not_found",
                        "ERROR" => "startup_error",
                        _ => "opened"
                    };
                case "connect":
                    await ConnectChromeAsync();
                    return _chrome.Connected ? "connected" : "disconnected";
                case "close_chrome":
                    StopManagedMessageReply();
                    return await CloseChromeAsync();
                case "message_reply_start":
                    return await StartManagedMessageReplyAsync(commandPayload);
                case "identity_ready":
                {
                    if (!_chrome.Connected) return "not_connected";
                    try
                    {
                        return await _chrome.IsTikTokSessionActiveAsync() ? "ready" : "not_logged_in";
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("[TIKTOK_IDENTITY_READY_PROBE] " + ex.Message);
                        return "probe_error";
                    }
                }
                case "update_tiktok_identity":
                {
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
                        if (!await _chrome.IsTikTokSessionActiveAsync())
                            throw new InvalidOperationException("TikTok chưa đăng nhập. Hãy đăng nhập tài khoản trên Chrome rồi cập nhật tên/ảnh lại; thao tác này không tự vào LIVE.");
                        var result = await _chrome.UpdateTikTokProfileIdentityAsync(
                            request.Username, request.DisplayName, request.AvatarPath, request.Bio,
                            request.SkipIfNameCooldown, request.KnownDisplayNames, request.VerifyExistingState);
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
                    BeginInvoke(new Action(Close));
                    return "bye";
                default:
                    return "unknown";
            }
        });
    }

    string BuildManagedStatusResponse()
    {
        var profile = _managedMode && !string.IsNullOrWhiteSpace(_startupOptions.ProfileName)
            ? _startupOptions.ProfileName
            : CurrentProfileName;
        var periodic = _engine.GetPeriodicF5Snapshot();
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
            F5Enabled = periodic.Enabled,
            F5RemainingSec = f5RemainingSec
            ,TikTokStartupState = _startupPreparationState
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
