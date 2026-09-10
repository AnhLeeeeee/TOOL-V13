using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    /// <summary>
    /// Hard-ban handler dùng chung cho Auto Profile/Tự bù và thao tác mở Chrome thủ công.
    /// Chỉ được gọi sau khi Worker đã trả account_banned từ DOM hard marker.
    /// Không suy BAN từ timeout/login fail/CAPTCHA/2FA.
    /// </summary>
    async Task<bool> HandleDetectedLoginBanAsync(
        ProfileContext ctx,
        TikTokAccountPoolItem? accountSnapshot,
        string source,
        string detail,
        CancellationToken ct)
    {
        var profileName = ctx.Profile.Name;
        source = string.IsNullOrWhiteSpace(source) ? "login" : source.Trim();
        detail = string.IsNullOrWhiteSpace(detail)
            ? "LOGIN_BAN: TikTok xác nhận tài khoản bị cấm/đình chỉ/không tồn tại."
            : detail.Trim();

        _log.Warn($"[LOGIN_BAN_HANDLE_BEGIN] profile={profileName} source={source}");

        // LOGIN_BAN có priority BAN. Nếu profile vì lý do nào đó đang chờ TIME,
        // bỏ khỏi hàng chờ ngay để không phát sinh request TIME trùng sau này.
        try { CancelTimeReplacementForBan(profileName, detail); } catch { }
        try { MarkProfileSupplyState(profileName, "retired", "login_banned:" + source); } catch { }

        TikTokAccountPoolItem? account = accountSnapshot;
        if (account is null)
        {
            try
            {
                var items = await RunAccountPoolIoAsync(
                    () => _accountPoolService.Load(),
                    CancellationToken.None);

                account = items.FirstOrDefault(x =>
                    x.AssignedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase));

                if (account is null)
                {
                    var knownUsername = ResolveAutoActivityAccount(profileName);
                    if (!string.IsNullOrWhiteSpace(knownUsername))
                    {
                        account = items.FirstOrDefault(x =>
                            x.Username.Equals(knownUsername, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[LOGIN_BAN_ACCOUNT_LOOKUP_ERROR] profile={profileName} error={ex.Message}");
            }
        }

        var banVerified = false;
        if (account is not null)
        {
            try
            {
                // Dùng đúng writer BAN hiện tại (5 lần + verify lại Excel), nhưng
                // hoãn auto-delete cho tới khi Chrome/Worker đã đóng sạch.
                await MarkAccountSnapshotAsBanInBackgroundAsync(
                    profileName,
                    account,
                    detail,
                    queueAutoDelete: false);

                banVerified = _banExcelNoteMarkedProfiles.Contains(profileName);
            }
            catch (Exception ex)
            {
                _log.Warn($"[LOGIN_BAN_NOTE_ERROR] profile={profileName} account={account.Username} error={ex.Message}");
            }

            // +auto=FAIL chỉ là trạng thái phụ để UI/kho không coi đây là PROCESSING.
            // Note=ban mới là khóa loại account khỏi mọi lần reuse/tự bù sau.
            try
            {
                await RunAccountPoolIoAsync(
                    () => _accountPoolService.SetAutoProfileResult(account.Id, "FAIL"),
                    CancellationToken.None);
                _log.Info($"[LOGIN_BAN_AUTOPRF_FAIL_OK] profile={profileName} account={account.Username}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[LOGIN_BAN_AUTOPRF_FAIL_WARN] profile={profileName} account={account.Username} error={ex.Message}");
            }
        }
        else
        {
            _log.Warn($"[LOGIN_BAN_ACCOUNT_MISSING] profile={profileName} source={source}; vẫn đóng runtime nhưng chưa thể note Excel.");
            WriteAutoActivityLog(
                action: "LOGIN BAN",
                profile: profileName,
                account: "",
                reason: "BAN",
                result: "THIẾU ACCOUNT",
                detail: "TikTok báo BAN/không tồn tại nhưng không tìm thấy account đang gán profile để ghi Excel.");
        }

        var runtimeClosed = await CloseLoginBannedRuntimeAsync(ctx, source, detail);

        // Chỉ queue xóa sau khi note=ban đã verify. Nếu đây đang là một candidate
        // của Tự bù, hoãn xóa tới SAU CleanupCreatedReplacementAttemptAsync để tránh
        // hai luồng cùng đụng Worker/Chrome/catalog.
        var replacementClaimed = _autoReplacementClaimedProfiles.Contains(profileName);
        if (banVerified && !replacementClaimed)
            QueueAutoDeleteRetiredProfileAfterExcelNote(profileName, "BAN");
        else if (banVerified)
            _log.Info($"[LOGIN_BAN_AUTO_DELETE_DEFERRED] profile={profileName} source={source} reason=replacement_cleanup_first");

        WriteAutoActivityLog(
            action: "LOGIN BAN",
            profile: profileName,
            account: account?.Username ?? "",
            reason: "BAN",
            result: banVerified && runtimeClosed ? "THÀNH CÔNG" : "CẦN KIỂM TRA",
            detail: $"source={source}; noteBanVerified={banVerified}; runtimeClosed={runtimeClosed}; {detail}");

        _log.Warn($"[LOGIN_BAN_HANDLE_DONE] profile={profileName} account={account?.Username ?? ""} noteBanVerified={banVerified} runtimeClosed={runtimeClosed} source={source}");
        return banVerified && runtimeClosed;
    }

    async Task<bool> CloseLoginBannedRuntimeAsync(
        ProfileContext ctx,
        string source,
        string detail)
    {
        var profileName = ctx.Profile.Name;
        Exception? lastError = null;

        // VM chậm/CIM timeout không được làm LOGIN_BAN bỏ dở cleanup sau đúng 1 lần.
        // Retry ngắn 3 lượt; Worker luôn được shutdown trước rồi mới probe đúng ProfilePath.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var workerAlive = false;
                try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; } catch { }

                if (workerAlive)
                {
                    try
                    {
                        await SendCommandAsync(ctx, "stop", TimeSpan.FromSeconds(6));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"[LOGIN_BAN_STOP_WARN] profile={profileName} attempt={attempt}/3 error={ex.Message}");
                    }

                    try
                    {
                        var reply = await SendCloseChromeCommandAsync(ctx);
                        _log.Info($"[LOGIN_BAN_CLOSE_CHROME] profile={profileName} attempt={attempt}/3 reply={reply}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"[LOGIN_BAN_CLOSE_CHROME_WARN] profile={profileName} attempt={attempt}/3 error={ex.Message}");
                    }
                }

                await EnsureAutoCloseWorkerStoppedAsync(ctx);
                await EnsureAutoCloseChromeStoppedAsync(ctx);

                if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                    RemoveTab(ctx);

                ClearAutoCloseExpectedRunning(profileName, "login_banned:" + source);
                ResetAutoCloseProgressWatch(profileName, "login_banned:" + source);

                _log.Warn($"[LOGIN_BAN_RUNTIME_CLOSED] profile={profileName} worker=closed chrome=0 tab=removed source={source} attempt={attempt}/3");
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn($"[LOGIN_BAN_RUNTIME_CLOSE_RETRY] profile={profileName} source={source} attempt={attempt}/3 error={ex.Message}");
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        _log.Error($"[LOGIN_BAN_RUNTIME_CLOSE_ERROR] profile={profileName} source={source} detail={detail} error={lastError}");
        return false;
    }
}
