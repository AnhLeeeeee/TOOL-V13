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
            // Register chạy trước gate local để xử lý pending, remote lock và self-heal DeviceId sau reinstall.
            using var licenseServer = LicenseServerClient.TryCreate(
                baseDir,
                access.DeviceId,
                DeviceAccessService.GetFingerprintHash(),
                AppVersionInfo.Current);

            LicenseServerDecision? registerDecision = null;
            string? rebindPreviousDeviceId = null;
            string? rebindCanonicalDeviceId = null;
            if (licenseServer is not null)
            {
                registerDecision = licenseServer
                    .RegisterAsync()
                    .GetAwaiter()
                    .GetResult();

                ManagerProcessDiagnostics.Append(
                    $"[LICENSE_SERVER_SHADOW_STARTUP] device={access.DeviceId} " +
                    $"reachable={registerDecision.Reachable} http={registerDecision.HttpStatus} " +
                    $"allowed={(registerDecision.Allowed is null ? "unknown" : registerDecision.Allowed.Value ? "true" : "false")} " +
                    $"status={ManagerProcessDiagnostics.OneLine(registerDecision.Status)} " +
                    $"localAllowRun={access.AllowRun} cloudEligible={access.CloudApprovalEligible} " +
                    $"denyCode={ManagerProcessDiagnostics.OneLine(access.DenyCode)} action=shadow_or_safe_hybrid");
            }

            // QITool REINSTALL SELF-HEAL:
            // Nếu server nhận ra đúng fingerprint nhưng DeviceId local bị sinh lại sau reinstall,
            // phục hồi local về canonical DeviceId của server rồi register lại đúng 1 lần.
            if (licenseServer is not null
                && LicenseServerClient.IsRebindRequired(registerDecision))
            {
                var previousDeviceId = access.DeviceId;
                var canonicalDeviceId = (registerDecision?.CanonicalDeviceId ?? "").Trim();
                rebindPreviousDeviceId = previousDeviceId;
                rebindCanonicalDeviceId = canonicalDeviceId;

                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_REBIND_REQUIRED] oldDevice={previousDeviceId} " +
                    $"canonicalDevice={ManagerProcessDiagnostics.OneLine(canonicalDeviceId)} action=restore_local_identity");

                if (!DeviceAccessService.TryRebindCanonicalDeviceId(
                        baseDir,
                        previousDeviceId,
                        canonicalDeviceId,
                        out var rebindReason))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REBIND_LOCAL_FAIL] oldDevice={previousDeviceId} " +
                        $"canonicalDevice={ManagerProcessDiagnostics.OneLine(canonicalDeviceId)} " +
                        $"detail={ManagerProcessDiagnostics.OneLine(rebindReason)}");

                    MessageBox.Show(
                        "Không thể phục hồi nhận diện thiết bị sau khi cài lại Tool.\n\n" +
                        rebindReason +
                        "\n\nTool chưa thay đổi quyền sử dụng. Hãy gửi log chẩn đoán để kiểm tra.",
                        "Tool TikTok — Phục hồi mã thiết bị",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                access = DeviceAccessService
                    .EvaluateStartupAsync(baseDir, AppVersionInfo.Current)
                    .GetAwaiter()
                    .GetResult();

                if (!string.Equals(
                        access.DeviceId,
                        canonicalDeviceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REBIND_VERIFY_FAIL] expected={ManagerProcessDiagnostics.OneLine(canonicalDeviceId)} " +
                        $"actual={ManagerProcessDiagnostics.OneLine(access.DeviceId)} action=stop");
                    return;
                }

                licenseServer.UseCanonicalDeviceId(canonicalDeviceId);
                registerDecision = licenseServer
                    .RegisterAsync()
                    .GetAwaiter()
                    .GetResult();

                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_REBIND_REGISTER_RECHECK] device={access.DeviceId} " +
                    $"reachable={registerDecision.Reachable} http={registerDecision.HttpStatus} " +
                    $"allowed={(registerDecision.Allowed is null ? "unknown" : registerDecision.Allowed.Value ? "true" : "false")} " +
                    $"status={ManagerProcessDiagnostics.OneLine(registerDecision.Status)} " +
                    $"detail={ManagerProcessDiagnostics.OneLine(rebindReason)}");

                // Không chấp nhận vòng rebind lặp do server/client không nhất quán.
                if (LicenseServerClient.IsRebindRequired(registerDecision))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REBIND_LOOP_BLOCK] device={access.DeviceId} action=stop");
                    MessageBox.Show(
                        "QITool vẫn yêu cầu phục hồi mã máy sau khi đã re-bind.\n\n" +
                        "Tool đã dừng để tránh thay đổi nhận diện lặp. Hãy gửi log chẩn đoán.",
                        "Tool TikTok — Phục hồi mã thiết bị",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            // QITool ADMIN: chỉ server mới có quyền cấp.
            // Nếu đúng DeviceId + fingerprint được đánh dấu is_admin=true thì bỏ toàn bộ
            // gate thiết bị/update/version trong process hiện tại. Quyền này không được lưu local.
            LicenseServerDecision? adminDecision = null;
            var isAdmin = false;
            if (licenseServer is not null)
            {
                adminDecision = licenseServer
                    .CheckAdminAsync()
                    .GetAwaiter()
                    .GetResult();

                isAdmin = adminDecision is { Reachable: true, Ok: true, IsAdmin: true };
                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_ADMIN_CHECK] device={access.DeviceId} reachable={adminDecision.Reachable} " +
                    $"http={adminDecision.HttpStatus} isAdmin={adminDecision.IsAdmin} " +
                    $"result={(isAdmin ? "ADMIN_BYPASS" : "NORMAL")}");

                if (isAdmin)
                {
                    DeviceAccessService.SetAdminBypass(baseDir, true, "qitool_server");
                    VersionRollbackGuard.SetAdminBypass(true);
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_ADMIN_BYPASS_ENABLED] device={access.DeviceId} " +
                        $"action=allow_run_allow_update_allow_any_version");
                }
            }

            // QITool REMOTE LOCK - FAIL OPEN / CHỈ KHÓA KHI EXPLICIT BLOCKED:
            // - Không khóa vì pending, expired, timeout, HTTP lỗi hay response thiếu field.
            // - Chỉ khóa máy thường khi register trả status=blocked + allowed=false.
            // - Xác nhận lại lần 2 trước khi chặn startup để tránh response tạm/stale.
            if (!isAdmin
                && licenseServer is not null
                && LicenseServerClient.IsExplicitBlocked(registerDecision))
            {
                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_REMOTE_LOCK_STARTUP_SIGNAL] device={access.DeviceId} " +
                    $"status={ManagerProcessDiagnostics.OneLine(registerDecision?.Status)} action=confirm_again");

                LicenseServerDecision? confirmDecision = null;
                try
                {
                    Thread.Sleep(1200);
                    confirmDecision = licenseServer
                        .RegisterAsync()
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception confirmEx)
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REMOTE_LOCK_STARTUP_CONFIRM_FAIL_OPEN] device={access.DeviceId} " +
                        $"detail={ManagerProcessDiagnostics.OneLine(confirmEx.Message)}");
                }

                if (LicenseServerClient.IsExplicitBlocked(confirmDecision))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REMOTE_LOCK_STARTUP_CONFIRMED] device={access.DeviceId} " +
                        $"action=block_startup");

                    MessageBox.Show(
                        $"Thiết bị này đã bị khóa trên QITool.\n\nMã thiết bị: {access.DeviceId}\n\n" +
                        "Nếu đây là nhầm lẫn, hãy mở khóa thiết bị trên trang quản lý QITool rồi mở lại Tool.",
                        "Tool TikTok — Thiết bị đã bị khóa",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_REMOTE_LOCK_STARTUP_NOT_CONFIRMED] device={access.DeviceId} action=fail_open_continue");
            }

            // Nếu startup vừa self-heal DeviceId thì phục hồi Version Guard SAU admin/remote-lock.
            // ADMIN vẫn giữ full bypass như thiết kế cũ; máy bị block không cần sửa local guard.
            if (!isAdmin
                && !LicenseServerClient.IsExplicitBlocked(registerDecision)
                && !string.IsNullOrWhiteSpace(rebindPreviousDeviceId)
                && !string.IsNullOrWhiteSpace(rebindCanonicalDeviceId))
            {
                if (!VersionRollbackGuard.TryRebindDeviceId(
                        rebindPreviousDeviceId,
                        rebindCanonicalDeviceId,
                        out var versionRebindReason))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[QITOOL_REBIND_VERSION_GUARD_FAIL] oldDevice={rebindPreviousDeviceId} " +
                        $"canonicalDevice={ManagerProcessDiagnostics.OneLine(rebindCanonicalDeviceId)} " +
                        $"detail={ManagerProcessDiagnostics.OneLine(versionRebindReason)}");

                    MessageBox.Show(
                        "Đã nhận ra đúng máy nhưng dữ liệu bảo vệ phiên bản không thể tự phục hồi an toàn.\n\n" +
                        versionRebindReason +
                        "\n\nTool sẽ không tự xóa hoặc hạ mốc phiên bản.",
                        "Tool TikTok — Phiên bản không được phép",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_REBIND_VERSION_GUARD_OK] oldDevice={rebindPreviousDeviceId} " +
                    $"canonicalDevice={ManagerProcessDiagnostics.OneLine(rebindCanonicalDeviceId)} " +
                    $"detail={ManagerProcessDiagnostics.OneLine(versionRebindReason)}");
            }

            // HYBRID CHUYỂN TIẾP AN TOÀN:
            // - Máy cũ đã được DeviceAccess local duyệt: không phụ thuộc Supabase, chạy như trước.
            // - Chỉ máy mới PENDING thuần túy mới được QITool allowed kích hoạt local một lần.
            // - Fingerprint mismatch/copy dữ liệu/blocked policy/enforce allowlist không được bypass.
            if (!isAdmin
                && !access.AllowRun
                && access.CloudApprovalEligible
                && registerDecision is { Reachable: true, Ok: true, Allowed: true }
                && string.Equals(registerDecision.Status, "allowed", StringComparison.OrdinalIgnoreCase))
            {
                if (DeviceAccessService.TryActivateFromCloudApproval(
                        baseDir,
                        access.DeviceId,
                        out var cloudActivationReason))
                {
                    ManagerProcessDiagnostics.Append(
                        $"[DEVICE_ACCESS_QITOOL_HYBRID_ACTIVATED] id={access.DeviceId} " +
                        $"detail={ManagerProcessDiagnostics.OneLine(cloudActivationReason)}");

                    // Re-evaluate để remote legacy block/version settings vẫn có quyền ưu tiên.
                    access = DeviceAccessService
                        .EvaluateStartupAsync(baseDir, AppVersionInfo.Current)
                        .GetAwaiter()
                        .GetResult();

                    ManagerProcessDiagnostics.Append(
                        $"[DEVICE_ACCESS_QITOOL_HYBRID_RECHECK] id={access.DeviceId} allowRun={access.AllowRun} " +
                        $"allowUpdate={access.AllowUpdate} activated={access.Activated} source={access.ActivationSource} " +
                        $"remote={access.RemotePolicyApplied}");
                }
                else
                {
                    ManagerProcessDiagnostics.Append(
                        $"[DEVICE_ACCESS_QITOOL_HYBRID_REJECTED] id={access.DeviceId} " +
                        $"detail={ManagerProcessDiagnostics.OneLine(cloudActivationReason)}");
                }
            }

            if (!isAdmin && !access.AllowRun)
            {
                var cloudStatus = registerDecision?.Status?.Trim().ToLowerInvariant() ?? "";
                var cloudHint = access.CloudApprovalEligible
                    ? cloudStatus switch
                    {
                        "pending" => "\n\nTrạng thái QITool: Đang chờ duyệt.",
                        "blocked" => "\n\nTrạng thái QITool: Đã bị khóa.",
                        "expired" => "\n\nTrạng thái QITool: Đã hết hạn.",
                        "allowed" => "\n\nQITool đã duyệt nhưng kích hoạt local không thành công. Hãy kiểm tra log Device Access.",
                        _ when registerDecision is { Reachable: false } => "\n\nKhông kết nối được QITool để xác minh máy mới.",
                        _ => ""
                    }
                    : "";

                MessageBox.Show(
                    $"Thiết bị này chưa được cấp quyền sử dụng Tool.\n\nMã thiết bị: {access.DeviceId}\n\n{access.Reason}{cloudHint}",
                    "Tool TikTok — Thiết bị chưa được cấp quyền",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!isAdmin)
            {
                // Máy thường: giữ nguyên Rollback Guard.
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
            }
            else
            {
                ManagerProcessDiagnostics.Append(
                    $"[VERSION_GUARD_ADMIN_BYPASS] device={access.DeviceId} current={AppVersionInfo.Current} action=skip_all_version_blocks");
            }

            using var heartbeatCts = new CancellationTokenSource();
            Task? heartbeatTask = null;
            var remoteLockTriggered = 0;
            LicenseServerDecision? remoteLockDecision = null;
            using var managerForm = new ManagerForm();

            // QITool Remote Policy Runtime Gate.
            // 3C.7: máy thường bật enforcement; máy ADMIN vẫn full bypass.
            RemotePolicyRuntimeGate.Bind(
                policyProvider: () =>
                    licenseServer?.CurrentRemotePolicy
                    ?? RemotePolicySnapshot.AllowAll,
                adminBypass: isAdmin);

            // Nếu heartbeat xác nhận khóa đúng lúc form chưa tạo handle, đóng ngay khi form vừa hiện.
            managerForm.Shown += (_, _) =>
            {
                if (Volatile.Read(ref remoteLockTriggered) != 1 || managerForm.IsDisposed)
                    return;

                try { managerForm.BeginInvoke(new Action(managerForm.Close)); } catch { }
            };

            if (licenseServer is not null)
            {
                Func<LicenseServerDecision, Task>? onConfirmedBlock = null;

                // ADMIN Full Bypass giữ nguyên: máy ADMIN không bị remote lock.
                if (!isAdmin)
                {
                    onConfirmedBlock = decision =>
                    {
                        if (Interlocked.CompareExchange(ref remoteLockTriggered, 1, 0) != 0)
                            return Task.CompletedTask;

                        remoteLockDecision = decision;
                        var closeKind =
                            string.Equals(
                                decision.Status,
                                "policy_tool_access_blocked",
                                StringComparison.OrdinalIgnoreCase)
                                ? "REMOTE_POLICY_TOOL_ACCESS"
                                : "DEVICE_REMOTE_LOCK";

                        ManagerProcessDiagnostics.Append(
                            $"[QITOOL_RUNTIME_ACCESS_CLOSE_CONFIRMED] device={access.DeviceId} " +
                            $"kind={closeKind} status={ManagerProcessDiagnostics.OneLine(decision.Status)} " +
                            "action=close_manager_gracefully");

                        try
                        {
                            if (!managerForm.IsDisposed && managerForm.IsHandleCreated)
                            {
                                managerForm.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        if (!managerForm.IsDisposed)
                                            managerForm.Close();
                                    }
                                    catch (Exception closeEx)
                                    {
                                        ManagerProcessDiagnostics.Append(
                                            $"[QITOOL_REMOTE_LOCK_CLOSE_ERROR] detail={ManagerProcessDiagnostics.OneLine(closeEx.Message)}");
                                    }
                                }));
                            }
                        }
                        catch (Exception invokeEx)
                        {
                            ManagerProcessDiagnostics.Append(
                                $"[QITOOL_REMOTE_LOCK_INVOKE_ERROR] detail={ManagerProcessDiagnostics.OneLine(invokeEx.Message)}");
                        }

                        return Task.CompletedTask;
                    };
                }

                heartbeatTask = licenseServer.RunHeartbeatLoopAsync(
                    heartbeatCts.Token,
                    onConfirmedBlock,
                    managerForm.GetShadowHeartbeatSnapshot);

                ManagerProcessDiagnostics.Append(
                    $"[LICENSE_SERVER_HEARTBEAT_STARTED] session={licenseServer.SessionId} " +
                    $"remoteLockEnforced={!isAdmin} policyEnforcement={RemotePolicyRuntimeGate.EnforcementEnabled} " +
                    "safety=device_block_double_confirm+tool_access_double_confirm");
            }

            try
            {
                Application.Run(managerForm);
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

            if (Volatile.Read(ref remoteLockTriggered) == 1)
            {
                var policyToolAccessBlock =
                    string.Equals(
                        remoteLockDecision?.Status,
                        "policy_tool_access_blocked",
                        StringComparison.OrdinalIgnoreCase);

                ManagerProcessDiagnostics.Append(
                    $"[QITOOL_RUNTIME_ACCESS_CLOSED] device={access.DeviceId} " +
                    $"kind={(policyToolAccessBlock ? "REMOTE_POLICY_TOOL_ACCESS" : "DEVICE_REMOTE_LOCK")} " +
                    $"status={ManagerProcessDiagnostics.OneLine(remoteLockDecision?.Status)}");

                if (policyToolAccessBlock)
                {
                    MessageBox.Show(
                        $"Quyền sử dụng Tool của thiết bị này đang bị tắt bởi QITool policy.\n\n" +
                        $"Mã thiết bị: {access.DeviceId}\n\n" +
                        "Bật lại tool_access trên trang quản lý QITool trước khi chạy lại Tool.",
                        "Tool TikTok — Quyền sử dụng đang bị tắt",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show(
                        $"Thiết bị này vừa bị khóa trên QITool nên Tool đã dừng an toàn.\n\nMã thiết bị: {access.DeviceId}\n\n" +
                        "Mở khóa trên trang quản lý QITool trước khi chạy lại Tool.",
                        "Tool TikTok — Thiết bị đã bị khóa",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
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
