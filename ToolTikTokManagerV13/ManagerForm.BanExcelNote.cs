using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly HashSet<string> _banExcelNoteMarkedProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _banExcelNoteWriteInProgressProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _lifetimeExcelNoteMarkedProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _lifetimeExcelNoteWriteInProgressProfiles = new(StringComparer.OrdinalIgnoreCase);
    bool _banExcelNoteWatcherInitialized;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InitializeBanExcelNoteWatcher();
        InitializeAutoCloseFeature();
    }

    void InitializeBanExcelNoteWatcher()
    {
        if (_banExcelNoteWatcherInitialized) return;
        _banExcelNoteWatcherInitialized = true;

        // Chỉ đọc snapshot/runtime đã có trong RAM.
        // Không được chạy PowerShell / Get-CimInstance trên timer UI 1 giây.
        _refreshTimer.Tick += (_, _) => CheckStoppedProfilesForBanNote();
    }

    void CheckStoppedProfilesForBanNote()
    {
        if (_closing || IsDisposed || Disposing) return;

        foreach (var ctx in _contexts.Values.ToList())
        {
            string detail;

            try
            {
                var state = GetEffectiveRuntimeState(ctx);
                if (!string.Equals(state, RuntimeStateStopped, StringComparison.OrdinalIgnoreCase))
                    continue;

                detail = ctx.LastSnapshot?.Detail ?? "";
                if (!LooksLikeAccountBanStop(detail))
                    continue;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[BAN_WATCH_CHECK_ERROR] profile={ctx.Profile.Name} error={ex.Message}");
                continue;
            }

            // QUAN TRỌNG:
            // Chụp đúng account ngay lúc phát hiện BAN, trước khi AutoClose/Tự bù tiếp tục.
            // Như vậy nhánh Excel không còn phụ thuộc "Profile đã gán" ở vài giây sau.
            TikTokAccountPoolItem? banAccountSnapshot = null;
            var shouldStartExcelWrite =
                !_banExcelNoteMarkedProfiles.Contains(ctx.Profile.Name)
                && _banExcelNoteWriteInProgressProfiles.Add(ctx.Profile.Name);

            if (shouldStartExcelWrite)
            {
                try
                {
                    var items = _accountPoolService.Load();

                    banAccountSnapshot = items.FirstOrDefault(x =>
                        x.AssignedProfile.Equals(
                            ctx.Profile.Name,
                            StringComparison.OrdinalIgnoreCase));

                    // Fallback: nếu mapping Profile đã gán vừa bị thay đổi/race,
                    // dùng username Manager đang biết để bắt đúng account cũ.
                    if (banAccountSnapshot is null)
                    {
                        var knownUsername =
                            ResolveAutoActivityAccount(ctx.Profile.Name);

                        if (!string.IsNullOrWhiteSpace(knownUsername))
                        {
                            banAccountSnapshot = items.FirstOrDefault(x =>
                                x.Username.Equals(
                                    knownUsername,
                                    StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    if (banAccountSnapshot is null)
                    {
                        _log.Warn(
                            $"[BAN_EXCEL_NOTE_SNAPSHOT_MISSING] profile={ctx.Profile.Name} reason=no_assigned_account");

                        _banExcelNoteWriteInProgressProfiles.Remove(ctx.Profile.Name);
                        shouldStartExcelWrite = false;
                    }
                    else
                    {
                        _log.Info(
                            $"[BAN_EXCEL_NOTE_SNAPSHOT] profile={ctx.Profile.Name} user={banAccountSnapshot.Username} row={banAccountSnapshot.SourceRow} id={banAccountSnapshot.Id}");
                    }
                }
                catch (Exception ex)
                {
                    _banExcelNoteWriteInProgressProfiles.Remove(ctx.Profile.Name);
                    shouldStartExcelWrite = false;

                    _log.Warn(
                        $"[BAN_EXCEL_NOTE_SNAPSHOT_ERROR] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            // AutoClose vẫn chạy độc lập với Excel:
            // Excel có bị khóa/lỗi thì profile BAN vẫn phải được đóng + bù.
            var workerRunning = false;
            try
            {
                workerRunning =
                    ctx.Worker is not null
                    && !ctx.Worker.HasExited;
            }
            catch { }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            var hasActiveRuntime =
                tabOpen
                || workerRunning
                || ctx.Opening;

            if (hasActiveRuntime)
            {
                try
                {
                    QueueAutoCloseForBan(ctx, detail);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[BAN_AUTO_CLOSE_ERROR] profile={ctx.Profile.Name} error={ex.Message}");
                }
            }

            if (shouldStartExcelWrite
                && banAccountSnapshot is not null)
            {
                _ = MarkAccountSnapshotAsBanInBackgroundAsync(
                    ctx.Profile.Name,
                    banAccountSnapshot,
                    detail);
            }
        }
    }

    async Task MarkAccountSnapshotAsBanInBackgroundAsync(
        string profileName,
        TikTokAccountPoolItem accountSnapshot,
        string detail)
    {
        const int maxAttempts = 5;
        Exception? lastError = null;

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var verified = await RunAccountPoolIoAsync(
                        () => MarkAccountSnapshotAsBanCore(
                            profileName,
                            accountSnapshot,
                            detail),
                        CancellationToken.None);

                    if (verified)
                    {
                        _banExcelNoteMarkedProfiles.Add(profileName);

                        WriteAutoActivityLog(
                            action: "GHI EXCEL BAN",
                            profile: profileName,
                            account: accountSnapshot.Username,
                            reason: "BAN",
                            result: "THÀNH CÔNG",
                            detail:
                                $"Đã ghi ban và xác minh lại dòng {accountSnapshot.SourceRow} trong Excel.");

                        // CHỈ sau khi note=ban đã được đọc lại/xác minh thành công
                        // mới được phép xóa profile BAN.
                        QueueAutoDeleteRetiredProfileAfterExcelNote(
                            profileName,
                            "BAN");

                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    _log.Warn(
                        $"[BAN_EXCEL_NOTE_RETRY] profile={profileName} user={accountSnapshot.Username} row={accountSnapshot.SourceRow} attempt={attempt}/{maxAttempts} error={ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(700 * attempt);
            }

            var finalMessage =
                lastError?.Message
                ?? "Không xác minh được ghi chú ban sau khi ghi.";

            _log.Warn(
                $"[BAN_EXCEL_NOTE_FAILED] profile={profileName} user={accountSnapshot.Username} row={accountSnapshot.SourceRow} attempts={maxAttempts} error={finalMessage}");

            WriteAutoActivityLog(
                action: "GHI EXCEL BAN",
                profile: profileName,
                account: accountSnapshot.Username,
                reason: "BAN",
                result: "LỖI",
                detail:
                    $"Không ghi/xác minh được ban sau {maxAttempts} lần: {finalMessage}");
        }
        finally
        {
            _banExcelNoteWriteInProgressProfiles.Remove(profileName);
        }
    }

    bool MarkAccountSnapshotAsBanCore(
        string profileName,
        TikTokAccountPoolItem snapshot,
        string detail)
    {
        var items = _accountPoolService.Load();

        // Ưu tiên ID vì không đổi khi profile/account mapping thay đổi.
        var account = items.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(snapshot.Id)
            && x.Id.Equals(
                snapshot.Id,
                StringComparison.OrdinalIgnoreCase));

        // Fallback theo username + source row cho catalog cũ.
        account ??= items.FirstOrDefault(x =>
            x.SourceRow == snapshot.SourceRow
            && x.Username.Equals(
                snapshot.Username,
                StringComparison.OrdinalIgnoreCase));

        account ??= items.FirstOrDefault(x =>
            x.Username.Equals(
                snapshot.Username,
                StringComparison.OrdinalIgnoreCase));

        if (account is null)
            throw new InvalidOperationException(
                $"Không còn tìm thấy account BAN {snapshot.Username} (row={snapshot.SourceRow}).");

        // Luôn Upsert kể cả JSON đang ghi ban:
        // mục tiêu là đảm bảo chính file Excel nguồn cũng có chữ "ban".
        _accountPoolService.Upsert(
            account with { Note = "ban" });

        // Đọc lại Excel ngay trong cùng hàng đợi I/O để đồng bộ catalog
        // và xác minh note thật sự đã nằm trong nguồn.
        _accountPoolService.ReloadCurrentExcel();

        var verified = _accountPoolService.Load()
            .FirstOrDefault(x =>
                x.Id.Equals(
                    account.Id,
                    StringComparison.OrdinalIgnoreCase));

        verified ??= _accountPoolService.Load()
            .FirstOrDefault(x =>
                x.SourceRow == account.SourceRow
                && x.Username.Equals(
                    account.Username,
                    StringComparison.OrdinalIgnoreCase));

        if (verified is null
            || !string.Equals(
                (verified.Note ?? "").Trim(),
                "ban",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Đã ghi nhưng đọc lại chưa thấy note=ban (user={account.Username}, row={account.SourceRow}).");
        }

        _log.Info(
            $"[BAN_EXCEL_NOTE_OK] profile={profileName} user={account.Username} row={account.SourceRow} note=ban verified=true detail={detail}");

        return true;
    }

    void QueueLifetimeExcelNoteIfNeeded(
        ProfileContext ctx,
        string reason,
        string detail)
    {
        reason = NormalizeAutoCloseReason(reason);

        if (!IsAutoCloseLifetimeReason(reason))
            return;

        var profileName = ctx.Profile.Name;

        if (_lifetimeExcelNoteMarkedProfiles.Contains(profileName)
            || !_lifetimeExcelNoteWriteInProgressProfiles.Add(profileName))
        {
            return;
        }

        TikTokAccountPoolItem? snapshot = null;

        try
        {
            var items = _accountPoolService.Load();

            snapshot = items.FirstOrDefault(x =>
                x.AssignedProfile.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

            if (snapshot is null)
            {
                var knownUsername =
                    ResolveAutoActivityAccount(profileName);

                if (!string.IsNullOrWhiteSpace(knownUsername))
                {
                    snapshot = items.FirstOrDefault(x =>
                        x.Username.Equals(
                            knownUsername,
                            StringComparison.OrdinalIgnoreCase));
                }
            }

            if (snapshot is null)
            {
                _lifetimeExcelNoteWriteInProgressProfiles.Remove(profileName);

                _log.Warn(
                    $"[LIFETIME_EXCEL_NOTE_SNAPSHOT_MISSING] profile={profileName} reason={reason}");

                return;
            }

            _log.Info(
                $"[LIFETIME_EXCEL_NOTE_SNAPSHOT] profile={profileName} user={snapshot.Username} row={snapshot.SourceRow} reason={reason}");
        }
        catch (Exception ex)
        {
            _lifetimeExcelNoteWriteInProgressProfiles.Remove(profileName);

            _log.Warn(
                $"[LIFETIME_EXCEL_NOTE_SNAPSHOT_ERROR] profile={profileName} reason={reason} error={ex.Message}");

            return;
        }

        _ = MarkAccountSnapshotWithLifetimeNoteInBackgroundAsync(
            profileName,
            snapshot,
            reason,
            detail);
    }

    async Task MarkAccountSnapshotWithLifetimeNoteInBackgroundAsync(
        string profileName,
        TikTokAccountPoolItem accountSnapshot,
        string reason,
        string detail)
    {
        const int maxAttempts = 5;
        Exception? lastError = null;

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var result = await RunAccountPoolIoAsync(
                        () => MarkAccountSnapshotWithLifetimeNoteCore(
                            profileName,
                            accountSnapshot,
                            reason,
                            detail),
                        CancellationToken.None);

                    if (result)
                    {
                        _lifetimeExcelNoteMarkedProfiles.Add(profileName);

                        WriteAutoActivityLog(
                            action: "GHI EXCEL VÒNG ĐỜI",
                            profile: profileName,
                            account: accountSnapshot.Username,
                            reason: reason,
                            result: "THÀNH CÔNG",
                            detail:
                                $"Đã ghi {reason} và xác minh lại dòng {accountSnapshot.SourceRow} trong Excel.");

                        // CHỈ sau khi TIME_xH đã được ghi/xác minh (hoặc BAN thắng TIME)
                        // mới được phép xóa profile hết vòng đời.
                        QueueAutoDeleteRetiredProfileAfterExcelNote(
                            profileName,
                            reason);

                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    _log.Warn(
                        $"[LIFETIME_EXCEL_NOTE_RETRY] profile={profileName} user={accountSnapshot.Username} row={accountSnapshot.SourceRow} reason={reason} attempt={attempt}/{maxAttempts} error={ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(700 * attempt);
            }

            var finalMessage =
                lastError?.Message
                ?? $"Không xác minh được ghi chú {reason} sau khi ghi.";

            _log.Warn(
                $"[LIFETIME_EXCEL_NOTE_FAILED] profile={profileName} user={accountSnapshot.Username} row={accountSnapshot.SourceRow} reason={reason} attempts={maxAttempts} error={finalMessage}");

            WriteAutoActivityLog(
                action: "GHI EXCEL VÒNG ĐỜI",
                profile: profileName,
                account: accountSnapshot.Username,
                reason: reason,
                result: "LỖI",
                detail:
                    $"Không ghi/xác minh được {reason} sau {maxAttempts} lần: {finalMessage}");
        }
        finally
        {
            _lifetimeExcelNoteWriteInProgressProfiles.Remove(profileName);
        }
    }

    bool MarkAccountSnapshotWithLifetimeNoteCore(
        string profileName,
        TikTokAccountPoolItem snapshot,
        string reason,
        string detail)
    {
        reason = NormalizeAutoCloseReason(reason);

        if (!IsAutoCloseLifetimeReason(reason))
            return true;

        var items = _accountPoolService.Load();

        var account = items.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(snapshot.Id)
            && x.Id.Equals(
                snapshot.Id,
                StringComparison.OrdinalIgnoreCase));

        account ??= items.FirstOrDefault(x =>
            x.SourceRow == snapshot.SourceRow
            && x.Username.Equals(
                snapshot.Username,
                StringComparison.OrdinalIgnoreCase));

        account ??= items.FirstOrDefault(x =>
            x.Username.Equals(
                snapshot.Username,
                StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw new InvalidOperationException(
                $"Không còn tìm thấy account hết vòng đời {snapshot.Username} (row={snapshot.SourceRow}).");
        }

        // BAN luôn có ưu tiên cao hơn TIME_xH.
        // Nếu BAN đã được ghi trước thì tuyệt đối không ghi đè bằng TIME_xH.
        if (string.Equals(
                (account.Note ?? "").Trim(),
                "ban",
                StringComparison.OrdinalIgnoreCase))
        {
            _log.Info(
                $"[LIFETIME_EXCEL_NOTE_SKIP_BAN] profile={profileName} user={account.Username} requested={reason} current=ban");

            return true;
        }

        _accountPoolService.Upsert(
            account with { Note = reason });

        _accountPoolService.ReloadCurrentExcel();

        var verified = _accountPoolService.Load()
            .FirstOrDefault(x =>
                x.Id.Equals(
                    account.Id,
                    StringComparison.OrdinalIgnoreCase));

        verified ??= _accountPoolService.Load()
            .FirstOrDefault(x =>
                x.SourceRow == account.SourceRow
                && x.Username.Equals(
                    account.Username,
                    StringComparison.OrdinalIgnoreCase));

        var verifiedNote =
            (verified?.Note ?? "").Trim();

        // Nếu BAN xuất hiện đúng lúc TIME đang ghi thì BAN vẫn được coi là kết quả đúng,
        // vì BAN có priority cao hơn và không được TIME ghi đè ngược lại.
        if (!string.Equals(
                verifiedNote,
                reason,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                verifiedNote,
                "ban",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Đã ghi nhưng đọc lại chưa thấy note={reason} (user={account.Username}, row={account.SourceRow}, actual={verifiedNote}).");
        }

        _log.Info(
            $"[LIFETIME_EXCEL_NOTE_OK] profile={profileName} user={account.Username} row={account.SourceRow} note={verifiedNote} requested={reason} detail={detail}");

        return true;
    }

    static bool LooksLikeAccountBanStop(string? detail)
    {
        detail = (detail ?? "").Trim();
        if (detail.Length == 0) return false;

        return detail.Contains("tài khoản đã vi phạm", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("tai khoan da vi pham", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("TikTok báo tài khoản đã vi phạm", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_BANNED", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_BAN", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_VIOLATION", StringComparison.OrdinalIgnoreCase);
    }

}
