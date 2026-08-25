namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
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
        try
        {
            // Excel có thể ghi xong trước khi AutoClose đóng Chrome/Worker xong.
            // Chờ AutoClose hoàn tất để không xóa dữ liệu giữa lúc Worker còn dùng.
            var waitDeadline =
                DateTime.UtcNow.AddSeconds(45);

            while (_autoCloseInProgressProfiles.Contains(profileName)
                   && DateTime.UtcNow < waitDeadline)
            {
                await Task.Delay(400);
            }

            if (_autoCloseInProgressProfiles.Contains(profileName))
            {
                throw new TimeoutException(
                    $"Profile {profileName} vẫn đang trong luồng Tự đóng sau 45 giây; chưa tự xóa.");
            }

            if (!_autoCloseSettings.DeleteProfileAfterBanOrLifetime)
            {
                _log.Info(
                    $"[AUTO_RETIRED_DELETE_CANCELLED] profile={profileName} reason=setting_disabled");
                return;
            }

            // Xác minh LẠI Excel ngay trước khi xóa.
            // Chỉ ban hoặc TIME_xH mới được phép làm mất profile.
            var account =
                _accountPoolService
                    .Load()
                    .FirstOrDefault(item =>
                        item.AssignedProfile.Equals(
                            profileName,
                            StringComparison.OrdinalIgnoreCase));

            if (account is null)
            {
                throw new InvalidOperationException(
                    $"Không tìm thấy tài khoản đang gán profile {profileName} để xác minh Ghi chú trước khi xóa.");
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
                throw new InvalidOperationException(
                    $"Ghi chú Excel của profile {profileName} chưa phải ban/TIME_xH (actual={verifiedNote}). Không tự xóa.");
            }

            // Nếu yêu cầu TIME nhưng BAN đã thắng race, vẫn được xóa vì BAN có ưu tiên cao hơn.
            if (requestedReason == "BAN"
                && !noteIsBan)
            {
                throw new InvalidOperationException(
                    $"Profile {profileName} được yêu cầu xóa vì BAN nhưng Excel chưa xác minh note=ban (actual={verifiedNote}).");
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

                throw new InvalidOperationException(
                    $"Không tìm thấy ProfileContext của {profileName}; không tự xóa để tránh xóa nhầm.");
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

            _log.Warn(
                $"[AUTO_RETIRED_DELETE_BEGIN] profile={profileName} requested={requestedReason} excelNote={verifiedNote}");

            WriteAutoActivityLog(
                action: "TỰ XÓA PROFILE",
                profile: profileName,
                account: account.Username,
                reason: noteIsBan ? "BAN" : verifiedNote,
                result: "BẮT ĐẦU",
                detail:
                    $"Excel đã xác minh Ghi chú={verifiedNote}; bắt đầu xóa profile.");

            // An toàn lần cuối: nếu Chrome/Worker còn sót thì đóng đúng profile trước.
            await StopProfileRuntimeForDeletionAsync(
                plan);

            // Chrome phải đóng thật trước khi xóa dữ liệu/catalog.
            await EnsureAutoCloseChromeStoppedAsync(
                ctx);

            DeleteDirectoryStrict(
                plan.Profile.Name,
                "dữ liệu Tool",
                plan.DataRoot);

            await DeleteChromeProfileDirectoryWithRetryAsync(
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
                $"[AUTO_RETIRED_DELETE_DONE] profile={profileName} excelNote={verifiedNote}");

            WriteAutoActivityLog(
                action: "TỰ XÓA PROFILE",
                profile: profileName,
                account: account.Username,
                reason: noteIsBan ? "BAN" : verifiedNote,
                result: "THÀNH CÔNG",
                detail:
                    "Đã xóa dữ liệu Tool, Chrome profile và catalog sau khi Excel được xác minh.");
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
