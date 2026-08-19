namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly HashSet<string> _banExcelNoteMarkedProfiles = new(StringComparer.OrdinalIgnoreCase);
    bool _banExcelNoteWatcherInitialized;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InitializeBanExcelNoteWatcher();
    }

    void InitializeBanExcelNoteWatcher()
    {
        if (_banExcelNoteWatcherInitialized) return;
        _banExcelNoteWatcherInitialized = true;

        // Dùng luôn refresh timer 1 giây hiện có của Manager.
        // Không tạo timer/thread mới.
        _refreshTimer.Tick += (_, _) => CheckStoppedProfilesForBanNote();
    }

    void CheckStoppedProfilesForBanNote()
    {
        if (_closing || IsDisposed || Disposing) return;

        foreach (var ctx in _contexts.Values)
        {
            try
            {
                var state = GetEffectiveRuntimeState(ctx);
                if (!string.Equals(state, RuntimeStateStopped, StringComparison.OrdinalIgnoreCase))
                    continue;

                var detail = ctx.LastSnapshot?.Detail ?? "";
                if (!LooksLikeAccountBanStop(detail))
                    continue;

                if (_banExcelNoteMarkedProfiles.Contains(ctx.Profile.Name))
                    continue;

                MarkAssignedAccountAsBan(ctx.Profile.Name, detail);
            }
            catch (Exception ex)
            {
                // Không làm ảnh hưởng luồng chạy chính. Nếu Excel đang bị khóa,
                // tick sau sẽ thử lại.
                _log.Warn($"[BAN_EXCEL_NOTE_ERROR] profile={ctx.Profile.Name} error={ex.Message}");
            }
        }
    }

    static bool LooksLikeAccountBanStop(string? detail)
    {
        detail = (detail ?? "").Trim();
        if (detail.Length == 0) return false;

        // Chỉ nhận các dấu hiệu BAN tài khoản rõ ràng.
        // Không dùng từ "ban" đơn lẻ để tránh nhầm với "Bạn".
        return detail.Contains("tài khoản đã vi phạm", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("tai khoan da vi pham", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("TikTok báo tài khoản đã vi phạm", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_BANNED", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_BAN", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ACCOUNT_VIOLATION", StringComparison.OrdinalIgnoreCase);
    }

    void MarkAssignedAccountAsBan(string profileName, string detail)
    {
        var items = _accountPoolService.Load();

        var account = items.FirstOrDefault(x =>
            x.AssignedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            // Không đánh dấu handled để nếu sau đó người dùng gán lại account/profile,
            // lần tick tiếp theo vẫn có thể ghi chú.
            _log.Warn($"[BAN_EXCEL_NOTE_SKIP] profile={profileName} reason=no_assigned_account");
            return;
        }

        if (string.Equals((account.Note ?? "").Trim(), "ban", StringComparison.OrdinalIgnoreCase))
        {
            _banExcelNoteMarkedProfiles.Add(profileName);
            return;
        }

        // Upsert của AccountPool ghi đồng thời catalog JSON và ngược trở lại
        // đúng dòng trong file Excel/CSV/TXT đang dùng.
        _accountPoolService.Upsert(account with { Note = "ban" });
        _banExcelNoteMarkedProfiles.Add(profileName);

        _log.Info(
            $"[BAN_EXCEL_NOTE_OK] profile={profileName} user={account.Username} row={account.SourceRow} note=ban detail={detail}");
    }
}
