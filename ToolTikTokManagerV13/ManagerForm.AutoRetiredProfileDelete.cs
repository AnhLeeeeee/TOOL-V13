using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoRetiredDeleteRetryException : InvalidOperationException
    {
        public AutoRetiredDeleteRetryException(string message)
            : base(message) { }

        public AutoRetiredDeleteRetryException(string message, Exception inner)
            : base(message, inner) { }
    }

    static readonly TimeSpan AutoRetiredDeleteRetryDelay =
        TimeSpan.FromSeconds(20);

    readonly HashSet<string>
        _autoRetiredProfileDeleteInProgress =
            new(StringComparer.OrdinalIgnoreCase);

    readonly HashSet<string>
        _autoRetiredProfileDeleted =
            new(StringComparer.OrdinalIgnoreCase);

    void QueueAutoDeleteRetiredProfileAfterExcelNote(
        string profileName,
        string requestedReason)
    {
        if (!_autoCloseSettings.DeleteProfileAfterBanOrLifetime)
            return;

        profileName =
            (profileName ?? "").Trim();

        requestedReason =
            NormalizeAutoCloseReason(
                requestedReason);

        if (profileName.Length == 0)
            return;

        if (requestedReason != "BAN"
            && !IsAutoCloseLifetimeReason(requestedReason))
        {
            return;
        }

        // Tùy chọn tự xóa không được phép vượt qua công tắc Tự đóng tương ứng.
        // Ví dụ người dùng tắt "Tự đóng khi BAN" thì việc ghi note=ban vẫn có thể chạy,
        // nhưng profile BAN không được tự xóa.
        if (requestedReason == "BAN"
            && !_autoCloseSettings.CloseOnBan)
        {
            return;
        }

        if (IsAutoCloseLifetimeReason(requestedReason)
            && !_autoCloseSettings.CloseOnRunTime)
        {
            return;
        }

        if (_autoRetiredProfileDeleted.Contains(profileName)
            || !_autoRetiredProfileDeleteInProgress.Add(profileName))
        {
            return;
        }

        _ = AutoDeleteRetiredProfileAfterExcelNoteAsync(
            profileName,
            requestedReason);
    }

    async Task AutoDeleteRetiredProfileAfterExcelNoteAsync(
        string profileName,
        string requestedReason)
    {
        var retryAttempt = 0;

        try
        {
            while (!_closing)
            {
                if (_autoRetiredProfileDeleted.Contains(profileName))
                    return;

                if (!_autoCloseSettings.DeleteProfileAfterBanOrLifetime)
                {
                    _log.Info(
                        $"[AUTO_RETIRED_DELETE_CANCELLED] profile={profileName} reason=setting_disabled");
                    return;
                }

                // DELETE_PENDING phải đi sau cleanup của Tự đóng. Không chờ cứng 45 giây
                // rồi bỏ luôn job: VM chậm/CIM timeout có thể cần nhiều lượt cleanup 20s.
                if (_autoCloseInProgressProfiles.Contains(profileName))
                {
                    retryAttempt++;
                    await WaitAutoRetiredDeleteRetryAsync(
                        profileName,
                        requestedReason,
                        retryAttempt,
                        "auto_close_in_progress");
                    continue;
                }

                if (_autoCloseCleanupRetryUtc.TryGetValue(
                        profileName,
                        out var cleanupRetryUtc))
                {
                    // The old code waited forever as long as the dictionary key
                    // existed, even after cleanupRetryUtc had already passed. If the
                    // watchdog no longer considered this profile a runtime candidate,
                    // nobody removed the stale key and DELETE_PENDING looped forever.
                    if (DateTime.UtcNow < cleanupRetryUtc)
                    {
                        retryAttempt++;
                        await WaitAutoRetiredDeleteRetryAsync(
                            profileName,
                            requestedReason,
                            retryAttempt,
                            $"auto_close_cleanup_pending_until={cleanupRetryUtc:O}");
                        continue;
                    }

                    _autoCloseCleanupRetryUtc.Remove(profileName);
                    _log.Info(
                        $"[AUTO_RETIRED_DELETE_CLEANUP_RETRY_DUE] profile={profileName} retryAt={cleanupRetryUtc:O} action=continue_cleanup");
                }

                // Xác minh LẠI Excel ngay trước mỗi lượt xóa.
                // Chỉ BAN hoặc TIME_xH đã xác minh mới được phép làm mất profile.
                TikTokAccountPoolItem? account;
                try
                {
                    account =
                        _accountPoolService
                            .Load()
                            .FirstOrDefault(item =>
                                item.AssignedProfile.Equals(
                                    profileName,
                                    StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    retryAttempt++;
                    await WaitAutoRetiredDeleteRetryAsync(
                        profileName,
                        requestedReason,
                        retryAttempt,
                        "excel_read_failed:" + ex.Message);
                    continue;
                }

                if (account is null)
                {
                    // Không còn mapping tài khoản => không có đủ bằng chứng để tự xóa.
                    // Đây là safety cancel, không phải lỗi VM chậm.
                    WriteAutoActivityLog(
                        action: "TỰ XÓA PROFILE",
                        profile: profileName,
                        account: "",
                        reason: requestedReason,
                        result: "HỦY",
                        detail:
                            "Không còn tìm thấy tài khoản đang gán profile để xác minh Ghi chú; không tự xóa để tránh xóa nhầm.");
                    return;
                }

                var verifiedNote =
                    (account.Note ?? "").Trim();

                var noteIsBan =
                    verifiedNote.Equals(
                        "ban",
                        StringComparison.OrdinalIgnoreCase);

                var noteIsLifetime =
                    IsAutoCloseLifetimeReason(
                        verifiedNote);

                if (!noteIsBan
                    && !noteIsLifetime)
                {
                    WriteAutoActivityLog(
                        action: "TỰ XÓA PROFILE",
                        profile: profileName,
                        account: account.Username,
                        reason: requestedReason,
                        result: "HỦY",
                        detail:
                            $"Ghi chú Excel không còn là ban/TIME_xH (actual={verifiedNote}); hủy tự xóa để tránh xóa nhầm.");
                    return;
                }

                // Nếu yêu cầu BAN thì Excel vẫn phải là BAN. TIME có thể bị BAN thắng race.
                if (requestedReason == "BAN"
                    && !noteIsBan)
                {
                    WriteAutoActivityLog(
                        action: "TỰ XÓA PROFILE",
                        profile: profileName,
                        account: account.Username,
                        reason: requestedReason,
                        result: "HỦY",
                        detail:
                            $"Yêu cầu xóa vì BAN nhưng Excel không còn note=ban (actual={verifiedNote}); không tự xóa.");
                    return;
                }

                if (!_contexts.TryGetValue(
                        profileName,
                        out var ctx))
                {
                    // Có thể profile đã được xóa bằng tay trước khi job này chạy.
                    var catalogNow =
                        _profileService.Load();

                    if (!catalogNow.Profiles.Any(profile =>
                            profile.Name.Equals(
                                profileName,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        _autoRetiredProfileDeleted.Add(profileName);

                        _log.Info(
                            $"[AUTO_RETIRED_DELETE_SKIP] profile={profileName} reason=already_missing");

                        return;
                    }

                    retryAttempt++;
                    await WaitAutoRetiredDeleteRetryAsync(
                        profileName,
                        requestedReason,
                        retryAttempt,
                        "profile_context_missing_but_catalog_exists");
                    continue;
                }

                var plans =
                    BuildDeletionPlans(
                        new[] { ctx });

                if (plans.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Không dựng được deletion plan duy nhất cho profile {profileName}.");
                }

                var plan =
                    plans[0];

                try
                {
                    _log.Warn(
                        $"[AUTO_RETIRED_DELETE_BEGIN] profile={profileName} requested={requestedReason} excelNote={verifiedNote} retryAttempt={retryAttempt}");

                    WriteAutoActivityLog(
                        action: "TỰ XÓA PROFILE",
                        profile: profileName,
                        account: account.Username,
                        reason: noteIsBan ? "BAN" : verifiedNote,
                        result: "BẮT ĐẦU",
                        detail:
                            retryAttempt == 0
                                ? $"Excel đã xác minh Ghi chú={verifiedNote}; bắt đầu xóa profile."
                                : $"DELETE_PENDING retry={retryAttempt}; Excel vẫn xác minh Ghi chú={verifiedNote}; thử xóa lại.");

                    // Đường auto-delete riêng: đóng Worker trước, sau đó chỉ MỘT probe
                    // ProfilePath ở background. Không gọi chuỗi StopChromeUsingProfile /
                    // IsProfileInUse đồng bộ nhiều lần trên UI thread.
                    await StopProfileRuntimeForAutoRetiredDeletionAsync(
                        plan);

                    await Task.Run(
                        () => DeleteDirectoryStrict(
                            plan.Profile.Name,
                            "dữ liệu Tool",
                            plan.DataRoot));

                    await DeleteChromeProfileDirectoryForAutoRetiredAsync(
                        plan.Profile.Name,
                        plan.ChromeProfilePath);

                    RemoveManagedProfileContainerIfEmpty(
                        plan.ChromeProfilePath);

                    var catalog =
                        _profileService.Load();

                    _profileService.RemoveFromCatalog(
                        catalog,
                        plan.Profile.Name);

                    PersistCatalogWithoutDeletedReferences(
                        catalog);

                    FinalizeDeletedProfiles(
                        new[] { plan });

                    CleanupAutoRetiredProfileStateAfterDelete(
                        profileName);

                    _autoRetiredProfileDeleted.Add(profileName);

                    _log.Warn(
                        $"[AUTO_RETIRED_DELETE_DONE] profile={profileName} excelNote={verifiedNote} retryAttempt={retryAttempt}");

                    WriteAutoActivityLog(
                        action: "TỰ XÓA PROFILE",
                        profile: profileName,
                        account: account.Username,
                        reason: noteIsBan ? "BAN" : verifiedNote,
                        result: "THÀNH CÔNG",
                        detail:
                            retryAttempt == 0
                                ? "Đã xóa dữ liệu Tool, Chrome profile và catalog sau khi Excel được xác minh."
                                : $"Đã xóa thành công sau {retryAttempt} lượt DELETE_PENDING retry.");

                    return;
                }
                catch (Exception ex) when (
                    ex is AutoCloseCleanupPendingException
                    or AutoRetiredDeleteRetryException
                    or IOException
                    or UnauthorizedAccessException
                    or TimeoutException)
                {
                    retryAttempt++;

                    _log.Warn(
                        $"[AUTO_RETIRED_DELETE_PENDING] profile={profileName} requested={requestedReason} retryAttempt={retryAttempt} error={ex.Message}");

                    await WaitAutoRetiredDeleteRetryAsync(
                        profileName,
                        requestedReason,
                        retryAttempt,
                        ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(
                $"[AUTO_RETIRED_DELETE_ERROR] profile={profileName} requested={requestedReason} error={ex}");

            WriteAutoActivityLog(
                action: "TỰ XÓA PROFILE",
                profile: profileName,
                account: ResolveAutoActivityAccount(profileName),
                reason: requestedReason,
                result: "LỖI",
                detail: ex.Message);
        }
        finally
        {
            _autoRetiredProfileDeleteInProgress.Remove(
                profileName);
        }
    }

    async Task WaitAutoRetiredDeleteRetryAsync(
        string profileName,
        string requestedReason,
        int retryAttempt,
        string detail)
    {
        var retryUtc =
            DateTime.UtcNow.Add(
                AutoRetiredDeleteRetryDelay);

        _log.Warn(
            $"[AUTO_RETIRED_DELETE_RETRY] profile={profileName} requested={requestedReason} attempt={retryAttempt} retryAt={retryUtc:O} detail={detail}");

        WriteAutoActivityLog(
            action: "TỰ XÓA PROFILE",
            profile: profileName,
            account: ResolveAutoActivityAccount(profileName),
            reason: requestedReason,
            result: "RETRY",
            detail:
                $"DELETE_PENDING; thử lại sau {(int)AutoRetiredDeleteRetryDelay.TotalSeconds}s. attempt={retryAttempt}; {detail}");

        await Task.Delay(
            AutoRetiredDeleteRetryDelay);
    }

    async Task StopProfileRuntimeForAutoRetiredDeletionAsync(
        ProfileDeletionPlan plan)
    {
        var context = plan.Context;

        await context.CommandGate.WaitAsync();
        try
        {
            var worker = context.Worker;

            if (worker is not null
                && !worker.HasExited)
            {
                try
                {
                    await SendPipeAsync(
                        plan.Profile.Name,
                        "stop",
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[{plan.Profile.Name}] [AUTO_RETIRED_DELETE_STOP_WARN] {ex.Message}");
                }

                try
                {
                    var closeReply = await SendPipeAsync(
                        plan.Profile.Name,
                        "close_chrome",
                        ChromeCloseTimeout);

                    _log.Info(
                        $"[{plan.Profile.Name}] [AUTO_RETIRED_DELETE_CLOSE_CHROME] reply={closeReply}");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[{plan.Profile.Name}] [AUTO_RETIRED_DELETE_CLOSE_WARN] {ex.Message}");
                }

                try
                {
                    await SendPipeAsync(
                        plan.Profile.Name,
                        "shutdown",
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[{plan.Profile.Name}] [AUTO_RETIRED_DELETE_SHUTDOWN_WARN] {ex.Message}");
                }

                if (!await WaitForProcessExitAsync(
                        worker,
                        TimeSpan.FromSeconds(7)))
                {
                    try
                    {
                        worker.Kill(
                            entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"[{plan.Profile.Name}] [AUTO_RETIRED_DELETE_WORKER_KILL_WARN] {ex.Message}");
                    }

                    if (!await WaitForProcessExitAsync(
                            worker,
                            TimeSpan.FromSeconds(3)))
                    {
                        throw new AutoRetiredDeleteRetryException(
                            $"Worker profile {plan.Profile.Name} chưa dừng được; giữ DELETE_PENDING.");
                    }
                }

                try { worker.Dispose(); } catch { }
                context.Worker = null;
            }
        }
        finally
        {
            context.CommandGate.Release();
        }

        // MỘT probe CIM duy nhất, bản thân helper chạy ProbeProfileProcesses trong
        // Task.Run. UNKNOWN => AutoCloseCleanupPendingException => outer loop retry 20s.
        await EnsureAutoCloseChromeStoppedByPathAsync(
            plan.Profile.Name,
            plan.ChromeProfilePath);
    }

    async Task DeleteChromeProfileDirectoryForAutoRetiredAsync(
        string profileName,
        string path)
    {
        if (!Directory.Exists(path))
            return;

        var retryDelaysMs =
            new[] { 250, 500, 900 };

        Exception? lastError = null;

        for (var attempt = 1;
             attempt <= retryDelaysMs.Length;
             attempt++)
        {
            await Task.Delay(
                retryDelaysMs[attempt - 1]);

            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                });

                if (!Directory.Exists(path))
                {
                    if (attempt > 1)
                    {
                        _log.Info(
                            $"[{profileName}] [AUTO_RETIRED_DELETE_DIR_RETRY_OK] attempt={attempt}/{retryDelaysMs.Length}");
                    }

                    return;
                }
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException)
            {
                lastError = ex;

                _log.Warn(
                    $"[{profileName}] [AUTO_RETIRED_DELETE_DIR_LOCKED] attempt={attempt}/{retryDelaysMs.Length} detail={ex.Message}");
            }
        }

        throw new AutoRetiredDeleteRetryException(
            $"Chrome profile {profileName} vẫn đang bị Windows khóa; giữ DELETE_PENDING và thử lại sau. path={path}",
            lastError ?? new IOException("Directory still exists."));
    }

    void CleanupAutoRetiredProfileStateAfterDelete(
        string profileName)
    {
        try
        {
            RemoveReusableProfileQueueEntry(
                profileName,
                "auto_retired_profile_deleted");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_RETIRED_DELETE_REUSE_CLEAN_WARN] profile={profileName} error={ex.Message}");
        }

        _autoReplacementRetiredProfiles.Remove(profileName);
        _autoReplacementClaimedProfiles.Remove(profileName);
        _autoReplacementFailedProfileRetryUtc.Remove(profileName);
        _autoCloseExpectedRunningProfiles.Remove(profileName);
        _autoCloseNotRunningSinceUtc.Remove(profileName);
        _autoCloseBanHandledProfiles.Remove(profileName);
        ResetAutoCloseProgressWatch(
            profileName,
            "profile_deleted");
        ClearAutoCloseReasonDecision(
            profileName,
            "profile_deleted");

        // Xóa supply-state theo TÊN profile để nếu sau này người dùng tạo lại
        // cùng số/tên (đặc biệt khi đổi file Excel), profile mới không bị kế thừa
        // trạng thái retired của profile đã xóa.
        try
        {
            lock (_profileSupplyStateLock)
            {
                var document =
                    LoadProfileSupplyStateDocumentUnsafe();

                if (document.Profiles.Remove(profileName))
                    SaveProfileSupplyStateDocumentUnsafe(document);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_RETIRED_DELETE_SUPPLY_CLEAN_WARN] profile={profileName} error={ex.Message}");
        }
    }

}
