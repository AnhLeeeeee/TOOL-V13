using System.Drawing;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly HashSet<string> _runtimeLoginRecoveryProfiles = new(StringComparer.OrdinalIgnoreCase);

    bool IsRuntimeLoginRecoveryInProgress(string profileName)
        => _runtimeLoginRecoveryProfiles.Contains((profileName ?? "").Trim());

    void TryQueueRuntimeLoginRecovery(ProfileContext ctx, WorkerSnapshot snapshot)
    {
        if (IsAutomationHalted || _closing || IsDisposed || Disposing)
            return;

        if (!string.Equals(
                snapshot.RuntimeAuthState,
                "LOGOUT_CONFIRMED",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var profileName = ctx.Profile.Name;
        if (!_runtimeLoginRecoveryProfiles.Add(profileName))
            return;

        // Đánh dấu Opening ngay, trước await đầu tiên. Fixed-slot counter sẽ coi
        // profile này vẫn giữ suất, tránh Tự bù mở PRF thứ 6 trong lúc ta đóng/mở lại.
        ctx.Opening = true;

        _log.Warn(
            $"[RUNTIME_LOGIN_RECOVERY_QUEUED] profile={profileName} detail={snapshot.RuntimeAuthDetail}");

        _ = RecoverRuntimeLoginAsync(ctx, snapshot.RuntimeAuthDetail);
    }

    async Task RecoverRuntimeLoginAsync(ProfileContext ctx, string workerDetail)
    {
        var profileName = ctx.Profile.Name;
        var source = "runtime_login_lost";

        try
        {
            SetStatus(
                ctx,
                "Mất đăng nhập đã xác nhận — đang đóng sạch và đăng nhập lại chính PRF...",
                Color.DarkOrange);

            _log.Warn(
                $"[RUNTIME_LOGIN_RECOVERY_BEGIN] profile={profileName} detail={workerDetail}");

            // Watchdog TIME/FAULT không được chen vào giao dịch recovery này.
            _autoCloseNotRunningSinceUtc.Remove(profileName);
            ResetAutoCloseProgressWatch(profileName, source);

            // 1) Dừng automation cũ. Lỗi IPC không làm bỏ dở cleanup; helper phía dưới
            // vẫn xác minh process thật đã chết.
            try
            {
                var stopReply = await SendCommandAsync(
                    ctx,
                    "stop",
                    TimeSpan.FromSeconds(6));
                _log.Info(
                    $"[RUNTIME_LOGIN_RECOVERY_STOP] profile={profileName} reply={stopReply}");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[RUNTIME_LOGIN_RECOVERY_STOP_WARN] profile={profileName} error={ex.Message}");
            }

            // 2) Yêu cầu Worker đóng đúng Chrome/profile trước khi kill Worker.
            try
            {
                var closeReply = await SendCloseChromeCommandAsync(ctx);
                _log.Info(
                    $"[RUNTIME_LOGIN_RECOVERY_CLOSE_CHROME] profile={profileName} reply={closeReply}");
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[RUNTIME_LOGIN_RECOVERY_CLOSE_CHROME_WARN] profile={profileName} error={ex.Message}");
            }

            await EnsureAutoCloseWorkerStoppedAsync(ctx);
            await EnsureAutoCloseChromeStoppedAsync(ctx);

            ctx.LastSnapshot = null;
            ctx.WorkerWindow = IntPtr.Zero;

            // Mở phiên Chrome mới phải được Name/identity flow xem như session mới.
            _autoIdentityHandledSession.Remove(profileName);
            _autoIdentityNextProbeUtc.Remove(profileName);
            _nameGuardVerifiedSessionAccount.Remove(profileName);

            _log.Info(
                $"[RUNTIME_LOGIN_RECOVERY_CLEAN_CONFIRMED] profile={profileName} worker=closed chrome=closed action=REOPEN_SAME_PROFILE");

            // 3) Mở lại CHÍNH PRF, không lấy PRF khác và không tạo mới ở bước này.
            await EnsureWorkerAsync(ctx);
            if (ctx.Tab is not null && ctx.Host is not null)
            {
                try { await EmbedWorkerAsync(ctx); }
                catch (Exception ex)
                {
                    // Embed UI không phải điều kiện để login/automation chạy.
                    _log.Warn(
                        $"[RUNTIME_LOGIN_RECOVERY_EMBED_WARN] profile={profileName} error={ex.Message}");
                }
            }

            // 4) Runtime đã xác nhận mất login bằng popup lặp 3/3. Sau clean reopen
            // phải đi THẲNG vào flow đăng nhập, không được để launch_auto tin cookie
            // stale rồi trả "opened". Worker command này chỉ ép bỏ session cookie cũ,
            // sau đó gọi nguyên PrepareTikTokProfileStartupAsync hiện có để giữ toàn bộ
            // xử lý login/CAPTCHA/TOTP/BAN.
            SetStatus(
                ctx,
                "Đang mở lại Chrome và vào thẳng luồng đăng nhập hiện có...",
                Color.DarkOrange);

            var launchReply = await SendCommandAsync(
                ctx,
                "runtime_relogin_auto",
                TimeSpan.FromSeconds(150));

            _log.Warn(
                $"[RUNTIME_LOGIN_RECOVERY_LOGIN_RESULT] profile={profileName} reply={launchReply}");

            if (string.Equals(launchReply, "account_banned", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(
                    ctx,
                    "TikTok xác nhận BAN khi đăng nhập lại — chuyển logic BAN hiện tại...",
                    Color.Firebrick);

                await HandleRuntimeLoginRecoveryBanAsync(
                    ctx,
                    source: "runtime_logout_relogin_ban_confirmed",
                    detail:
                        "LOGIN_BAN: Sau khi mất đăng nhập runtime, Tool đóng sạch/mở lại chính PRF; logic đăng nhập hiện tại xác nhận tài khoản bị cấm/đình chỉ/không tồn tại.");
                return;
            }

            if (string.Equals(launchReply, "login_failed", StringComparison.OrdinalIgnoreCase))
            {
                // Theo rule đã chốt cho flow này: logout đã được xác nhận qua popup
                // Đăng nhập lặp 3/3 sau Enter; sau clean reopen mà login flow hiện tại
                // vẫn kết thúc LOGIN_FAILED thì
                // xử lý operational như BAN. Các trạng thái CAPTCHA/TOTP/config/error khác
                // KHÔNG đi nhánh này để tránh false-positive.
                SetStatus(
                    ctx,
                    "Đăng nhập lại thất bại sau mất session — chuyển logic BAN hiện tại...",
                    Color.Firebrick);

                await HandleRuntimeLoginRecoveryBanAsync(
                    ctx,
                    source: "runtime_logout_relogin_failed",
                    detail:
                        "BAN_INFERRED: Runtime đã xác nhận popup Đăng nhập lặp 3/3 sau Enter; Chrome/Worker đã đóng sạch và mở lại đúng PRF nhưng logic đăng nhập hiện tại trả LOGIN_FAILED.");
                return;
            }

            if (!string.Equals(launchReply, "opened", StringComparison.OrdinalIgnoreCase))
            {
                // CAPTCHA / TOTP / thiếu config / login form thay đổi / startup error:
                // chưa đủ bằng chứng để BAN. Giữ PRF để người dùng/logic cũ xử lý.
                SetStatus(
                    ctx,
                    $"Đăng nhập lại chưa hoàn tất ({launchReply}) — chưa kết luận BAN.",
                    Color.DarkOrange);

                _log.Warn(
                    $"[RUNTIME_LOGIN_RECOVERY_DEFERRED] profile={profileName} reply={launchReply} action=NO_BAN_NO_REPLACEMENT");
                return;
            }

            // 5) Login/session OK -> Start lại đúng logic automation cũ.
            var startReply = await SendCommandAsync(
                ctx,
                "start_auto",
                TimeSpan.FromSeconds(95));

            if (string.Equals(startReply, "started", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startReply, "running", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(
                    ctx,
                    "Đăng nhập lại thành công — PRF đã chạy lại bình thường.",
                    Color.DarkGreen);

                _log.Info(
                    $"[RUNTIME_LOGIN_RECOVERY_OK] profile={profileName} launch={launchReply} start={startReply}");

                _autoCloseNotRunningSinceUtc.Remove(profileName);
                ResetAutoCloseProgressWatch(profileName, "runtime_login_recovered");
                return;
            }

            // Login thành công nhưng Automation không Start được là lỗi runtime/start,
            // không phải bằng chứng BAN.
            SetStatus(
                ctx,
                $"Đăng nhập lại thành công nhưng Automation chưa chạy ({startReply}).",
                Color.DarkOrange);

            _log.Warn(
                $"[RUNTIME_LOGIN_RECOVERY_START_FAILED] profile={profileName} launch={launchReply} start={startReply} action=NO_BAN");
        }
        catch (Exception ex)
        {
            // Recovery kỹ thuật lỗi không được biến thành BAN.
            _log.Error(
                $"[RUNTIME_LOGIN_RECOVERY_ERROR] profile={profileName} error={ex}");

            try
            {
                SetStatus(
                    ctx,
                    "Khôi phục đăng nhập lỗi kỹ thuật — chưa kết luận BAN: " + ex.Message,
                    Color.Firebrick);
            }
            catch { }
        }
        finally
        {
            ctx.Opening = false;
            _runtimeLoginRecoveryProfiles.Remove(profileName);
            _log.Info(
                $"[RUNTIME_LOGIN_RECOVERY_END] profile={profileName}");
        }
    }

    async Task HandleRuntimeLoginRecoveryBanAsync(
        ProfileContext ctx,
        string source,
        string detail)
    {
        await HandleDetectedLoginBanAsync(
            ctx,
            accountSnapshot: null,
            source: source,
            detail: detail,
            CancellationToken.None);

        // HandleDetectedLoginBanAsync dùng đúng writer note-ban + clean-close hiện tại.
        // Recovery đã giữ ctx.Opening=true để reserve đúng slot. Sau BAN handler phải nhả
        // reservation này TRƯỚC khi probe runtime, vì IsAutoCloseRuntimeStillPresentStrict()
        // coi Opening=true là runtime vẫn còn. Chỉ queue bù sau khi Worker/Chrome/tab cũ
        // đã thật sự biến mất.
        ctx.Opening = false;
        if (!IsAutoCloseRuntimeStillPresentStrict(ctx))
        {
            QueueAutoReplacementAfterAutoClose(closedProfileName: ctx.Profile.Name, reason: "BAN");
            _log.Info(
                $"[RUNTIME_LOGIN_RECOVERY_BAN_REPLACEMENT] profile={ctx.Profile.Name} action=QUEUED reason=BAN");
        }
        else
        {
            _log.Warn(
                $"[RUNTIME_LOGIN_RECOVERY_BAN_REPLACEMENT_BLOCKED] profile={ctx.Profile.Name} reason=runtime_still_present action=NO_OPEN_OVERLAP");
        }
    }
}
