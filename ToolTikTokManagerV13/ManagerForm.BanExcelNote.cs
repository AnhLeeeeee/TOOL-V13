using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly HashSet<string> _banExcelNoteMarkedProfiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _banExcelNoteWriteInProgressProfiles = new(StringComparer.OrdinalIgnoreCase);
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

            // Không gọi IsProfileInUse() ở đây.
            // Snapshot BAN chỉ xuất hiện khi profile đã/đang có Worker runtime.
            // AutoClose tự chịu trách nhiệm đóng Chrome bằng Worker và fallback nền khi cần.
            var workerRunning = false;
            try
            {
                workerRunning = ctx.Worker is not null && !ctx.Worker.HasExited;
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

            // Excel là nhánh độc lập. Ghi nền + chống trùng để timer không rewrite XLSX mỗi giây.
            if (_banExcelNoteMarkedProfiles.Contains(ctx.Profile.Name)
                || !_banExcelNoteWriteInProgressProfiles.Add(ctx.Profile.Name))
            {
                continue;
            }

            _ = MarkAssignedAccountAsBanInBackgroundAsync(
                ctx.Profile.Name,
                detail);
        }
    }

    async Task MarkAssignedAccountAsBanInBackgroundAsync(
        string profileName,
        string detail)
    {
        try
        {
            var handled = await RunAccountPoolIoAsync(
                () => MarkAssignedAccountAsBanCore(profileName, detail),
                CancellationToken.None);

            if (handled)
                _banExcelNoteMarkedProfiles.Add(profileName);
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[BAN_EXCEL_NOTE_ERROR] profile={profileName} error={ex.Message}");
        }
        finally
        {
            _banExcelNoteWriteInProgressProfiles.Remove(profileName);
        }
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

    bool MarkAssignedAccountAsBanCore(string profileName, string detail)
    {
        var items = _accountPoolService.Load();

        var account = items.FirstOrDefault(x =>
            x.AssignedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            _log.Warn($"[BAN_EXCEL_NOTE_SKIP] profile={profileName} reason=no_assigned_account");
            return false;
        }

        if (string.Equals((account.Note ?? "").Trim(), "ban", StringComparison.OrdinalIgnoreCase))
            return true;

        _accountPoolService.Upsert(account with { Note = "ban" });

        _log.Info(
            $"[BAN_EXCEL_NOTE_OK] profile={profileName} user={account.Username} row={account.SourceRow} note=ban detail={detail}");

        return true;
    }
}
