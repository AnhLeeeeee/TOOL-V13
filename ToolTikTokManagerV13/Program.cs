using ToolTikTokV12.Services;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ManagerProcessDiagnostics.Append(
            $"[MANAGER_PROCESS_MAIN_START] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} app={AppVersionInfo.Display}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_UNHANDLED_EXCEPTION] pid={Environment.ProcessId} terminating={e.IsTerminating} exception={ManagerProcessDiagnostics.OneLine(e.ExceptionObject?.ToString())}");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_PROCESS_EXIT] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} exitCode={Environment.ExitCode}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_UNOBSERVED_TASK_EXCEPTION] pid={Environment.ProcessId} observed={e.Observed} exception={ManagerProcessDiagnostics.ExceptionOneLine(e.Exception)}");
        };

        Application.ApplicationExit += (_, _) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_APPLICATION_EXIT] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        };

        ApplicationConfiguration.Initialize();

        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            DeviceAccessService.DeviceAccessDecision access;
            try
            {
                access = DeviceAccessService
                    .EvaluateStartupAsync(baseDir, AppVersionInfo.Current)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception accessEx)
            {
                ManagerProcessDiagnostics.Append(
                    $"[DEVICE_ACCESS_FATAL] pid={Environment.ProcessId} exception={ManagerProcessDiagnostics.ExceptionOneLine(accessEx)}");
                MessageBox.Show(
                    "Không thể xác minh quyền sử dụng trên thiết bị này.\n\n" + accessEx.Message,
                    "Tool TikTok — Xác minh thiết bị",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ManagerProcessDiagnostics.Append(
                $"[DEVICE_ACCESS] id={access.DeviceId} allowRun={access.AllowRun} allowUpdate={access.AllowUpdate} " +
                $"activated={access.Activated} source={access.ActivationSource} remote={access.RemotePolicyApplied}");

            // QITool License Server - SHADOW MODE.
            // Register được gọi trước gate local để máy mới/PENDING cũng xuất hiện trên web quản lý.
            // Kết quả server ở bản vá này CHỈ ghi log, chưa thay đổi quyền AllowRun cũ.
            using var licenseServer = LicenseServerClient.TryCreate(
                baseDir,
                access.DeviceId,
                DeviceAccessService.GetFingerprintHash(),
                AppVersionInfo.Current);

            if (licenseServer is not null)
            {
                var registerDecision = licenseServer
                    .RegisterAsync()
                    .GetAwaiter()
                    .GetResult();

                ManagerProcessDiagnostics.Append(
                    $"[LICENSE_SERVER_SHADOW_STARTUP] device={access.DeviceId} " +
                    $"reachable={registerDecision.Reachable} http={registerDecision.HttpStatus} " +
                    $"allowed={(registerDecision.Allowed is null ? "unknown" : registerDecision.Allowed.Value ? "true" : "false")} " +
                    $"status={ManagerProcessDiagnostics.OneLine(registerDecision.Status)} " +
                    $"localAllowRun={access.AllowRun} action=no_enforcement");
            }

            if (!access.AllowRun)
            {
                MessageBox.Show(
                    $"Thiết bị này chưa được cấp quyền sử dụng Tool.\n\nMã thiết bị: {access.DeviceId}\n\n{access.Reason}",
                    "Tool TikTok — Thiết bị chưa được cấp quyền",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Rollback Guard local luôn chạy để ghi HighestVersionEver và chặn bản thấp hơn.
            // versionControl.enabled chỉ bật/tắt lớp server ngoại lệ rollback; local guard không bypass.
            var versionGuard = VersionRollbackGuard
                .EvaluateAndRecordAsync(
                    AppVersionInfo.Current,
                    access.DeviceId,
                    access.VersionControlEnabled,
                    access.VersionPolicyUrl,
                    access.VersionPolicyFailClosedOnDowngrade)
                .GetAwaiter()
                .GetResult();

            ManagerProcessDiagnostics.Append(
                $"[VERSION_GUARD] allowRun={versionGuard.AllowRun} current={versionGuard.CurrentVersion} " +
                $"highest={versionGuard.HighestVersionEver} updated={versionGuard.RecordUpdated} " +
                $"remoteControl={access.VersionControlEnabled} serverApplied={versionGuard.ServerPolicyApplied} " +
                $"mode={versionGuard.DowngradeMode} reason={ManagerProcessDiagnostics.OneLine(versionGuard.Reason)}");

            if (!versionGuard.AllowRun)
            {
                MessageBox.Show(
                    versionGuard.Reason +
                    $"\n\nPhiên bản hiện tại: {versionGuard.CurrentVersion}" +
                    (string.IsNullOrWhiteSpace(versionGuard.HighestVersionEver)
                        ? ""
                        : $"\nPhiên bản cao nhất đã dùng: {versionGuard.HighestVersionEver}"),
                    "Tool TikTok — Phiên bản không được phép",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            using var heartbeatCts = new CancellationTokenSource();
            Task? heartbeatTask = null;
            if (licenseServer is not null)
            {
                heartbeatTask = licenseServer.RunHeartbeatLoopAsync(heartbeatCts.Token);
                ManagerProcessDiagnostics.Append(
                    $"[LICENSE_SERVER_HEARTBEAT_STARTED] session={licenseServer.SessionId} mode=shadow");
            }

            try
            {
                Application.Run(new ManagerForm());
            }
            finally
            {
                heartbeatCts.Cancel();
                if (heartbeatTask is not null)
                {
                    try
                    {
                        heartbeatTask.Wait(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Shutdown không được bị chặn vì heartbeat.
                    }
                }
            }

            ManagerProcessDiagnostics.Append(
                $"[MANAGER_APPLICATION_RUN_RETURNED] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_MAIN_FATAL_EXCEPTION] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} exception={ManagerProcessDiagnostics.ExceptionOneLine(ex)}");
            throw;
        }
        finally
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_MAIN_FINALLY] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        }
    }
}
