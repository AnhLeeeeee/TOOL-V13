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

    public Task<string> HandleManagedCommandAsync(string rawCommand)
    {
        var command = (rawCommand ?? "").Trim().ToLowerInvariant();
        if (command == "ping") return Task.FromResult("pong");
        if (IsDisposed || Disposing) return Task.FromResult("disposed");

        // Status is the hot IPC path (Manager polls every open profile once a
        // second).  It no longer needs to marshal to WinForms just to read a few
        // values.  The detail/window values are snapshots maintained by the UI,
        // while engine/chrome flags are safe lightweight reads.
        if (command == "status") return Task.FromResult(BuildManagedStatusResponse());

        return InvokeManagedOnUiAsync(async () =>
        {
            switch (command)
            {
                case "start":
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
                    return _chrome.Connected ? "opened" : "not_opened";
                case "connect":
                    await ConnectChromeAsync();
                    return _chrome.Connected ? "connected" : "disconnected";
                case "close_chrome":
                    return await CloseChromeAsync();
                case "show":
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Show();
                    return "shown";
                case "shutdown":
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
        return JsonSerializer.Serialize(new
        {
            Profile = profile,
            State = "WORKER_READY",
            RunState = !_engine.Running ? "STOPPED" : _engine.Paused ? "PAUSED" : "RUNNING",
            Detail = Volatile.Read(ref _managedDetailSnapshot),
            Chrome = _chrome.Connected ? "CONNECTED" : "DISCONNECTED",
            CdpPort = _settings.ChromePort,
            Pid = Environment.ProcessId,
            WindowHandle = Interlocked.Read(ref _managedWindowHandleSnapshot)
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
