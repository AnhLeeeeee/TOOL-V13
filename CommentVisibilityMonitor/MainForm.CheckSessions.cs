using System.Text;

namespace CommentVisibilityMonitor;

internal sealed partial class MainForm
{
    void HandleManagerStateTelemetry(TelemetryMessage m)
    {
        if (!_profiles.TryGetValue(m.Profile, out var p))
        {
            p = new ProfileState { Profile = m.Profile };
            _profiles[m.Profile] = p;
        }

        var wasTerminal = p.ManagerTerminal;
        var previousReason = p.ManagerReason;
        // ManagerSeq chỉ có ý nghĩa trong một process Manager và sẽ reset khi Manager
        // khởi động lại, vì vậy không dùng nó để loại telemetry mới.
        p.ManagerSeq = m.ManagerSeq;
        p.ManagerPresent = m.ManagerPresent;
        p.ManagerTerminal = m.ManagerTerminal;
        p.ManagerReason = m.ManagerReason ?? "";
        p.ManagerRunState = m.RunState ?? "";
        p.LastManagerSeenUtc = DateTime.UtcNow;

        if (m.ManagerTerminal
            && (!wasTerminal || !string.Equals(previousReason, p.ManagerReason, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"[MANAGER_STATE] PRF={m.Profile} terminal=true state={p.ManagerRunState} reason={p.ManagerReason}");
        }
    }

    bool IsEligibleForNewTarget(ProfileState p)
    {
        if (p.ManagerTerminal) return false;

        var managerFresh = p.LastManagerSeenUtc != default
                           && DateTime.UtcNow - p.LastManagerSeenUtc < TimeSpan.FromSeconds(8);
        if (managerFresh
            && (p.ManagerRunState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase)
                || p.ManagerRunState.Equals("RECOVERING", StringComparison.OrdinalIgnoreCase)))
            return true;

        var workerFresh = p.LastSeenUtc != default
                          && DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromSeconds(20);
        return workerFresh && p.RunState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    void RefreshQuickProfileChoices()
    {
        var current = _quickProfile.SelectedItem?.ToString() ?? "";
        var names = _profiles.Values
            .Where(p => !p.ManagerTerminal
                        && (IsEligibleForNewTarget(p)
                            || (p.LastManagerSeenUtc != default
                                && DateTime.UtcNow - p.LastManagerSeenUtc < TimeSpan.FromSeconds(8)
                                && p.ManagerRunState.Equals("PAUSED", StringComparison.OrdinalIgnoreCase))
                            || p.Profile.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => NaturalProfileKey(p.Profile), StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Profile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var same = _quickProfile.Items.Count == names.Count;
        if (same)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(_quickProfile.Items[i]?.ToString(), names[i], StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
        }
        if (same) return;

        _quickProfile.BeginUpdate();
        try
        {
            _quickProfile.Items.Clear();
            foreach (var name in names) _quickProfile.Items.Add(name);
            if (current.Length > 0)
            {
                var index = names.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _quickProfile.SelectedIndex = index;
            }
            if (_quickProfile.SelectedIndex < 0 && _quickProfile.Items.Count > 0)
                _quickProfile.SelectedIndex = 0;
        }
        finally { _quickProfile.EndUpdate(); }
    }

    void StartQuickCheck()
    {
        if (_banCheckRunning)
        {
            MessageBox.Show(this, "CHECK BAN đang chạy. Hãy dừng CHECK BAN trước.", "CHECK NGAY", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selected = (_quickProfile.SelectedItem?.ToString() ?? "").Trim();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Chưa có PRF để chọn.", "CHECK NGAY", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_profiles.TryGetValue(selected, out var selectedState) && selectedState.ManagerTerminal)
        {
            MessageBox.Show(this, $"{selected} đang được Manager thay/retire nên không thể CHECK NGAY.", "CHECK NGAY", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_checking)
        {
            // CHECK NGAY khi tool đang dừng cũng mở một lượt đếm mới, không kế thừa số liệu lượt trước.
            ResetCurrentRunStatistics("QUICK_CHECK_START");
            _checking = true;
            _start.Enabled = false;
            _stop.Enabled = true;
        }

        if (selected.Equals(_targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            _manualTarget = true;
            _targetState.Text = $"Đang kiểm tra: {_targetProfile} · CHECK NGAY";
            Log($"[QUICK_CHECK_ALREADY_TARGET] PRF={selected}");
            return;
        }

        // Nếu đang ở một CHECK NGAY khác thì kết thúc phiên thủ công hiện tại trước,
        // nhưng vẫn giữ phiên auto gốc đã bị nhường Observer ở _resumeCycle.
        if (_manualTarget && !string.IsNullOrWhiteSpace(_targetProfile))
        {
            FinishCurrentSession("CHECK_NGAY_CHUYEN_PRF", cancelPendingAsUnknown: true, chooseNext: false);
        }
        else if (!string.IsNullOrWhiteSpace(_targetProfile))
        {
            _resumeProfile = _targetProfile;
            _resumeCycle = _cycle;
            CancelPendingForProfile(_targetProfile, countUnknown: true, reason: "YIELD_TO_QUICK_CHECK");
            _cycle = null;
            Log($"[QUICK_CHECK_YIELD] from={_resumeProfile} hasSession={_resumeCycle is not null} deadline={(_resumeCycle?.DeadlineUtc.ToString("O") ?? "-")}");
        }

        ActivateTarget(selected, manual: true, cycle: null, reason: "CHECK_NGAY");
    }

    void OpenHistoryWindow()
    {
        try
        {
            using var form = new CommentCheckHistoryForm(_historyStore, Log);
            form.ShowDialog(this);
            // Cửa sổ lịch sử có thể đã xóa dữ liệu; nạp lại tỷ lệ TB ngay khi đóng.
            ReloadHistoryAverages();
            RefreshGrid();
            UpdateCycleLabel();
        }
        catch (Exception ex)
        {
            Log("[HISTORY_OPEN_ERROR] " + ex);
            MessageBox.Show(this, "Không mở được lịch sử Check CMT:\n" + ex.Message, "Lịch sử Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ActivateTarget(string profile, bool manual, CycleState? cycle, string reason)
    {
        profile = (profile ?? "").Trim();
        if (profile.Length == 0) return;

        _targetProfile = profile;
        _manualTarget = manual;
        _cycle = cycle;
        if (manual && string.IsNullOrWhiteSpace(_resumeProfile))
        {
            var ordered = _profiles.Values.Where(IsEligibleForNewTarget)
                .OrderBy(x => NaturalProfileKey(x.Profile), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Profile)
                .ToList();
            var index = ordered.FindIndex(x => x.Equals(profile, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _rotationSeed = index;
        }
        _followRequestedUrl = "";
        _authorizedLiveUrl = "";
        _targetState.Text = $"Đang kiểm tra: {profile}" + (manual ? " · CHECK NGAY" : "");
        UpdateCycleLabel();
        Log($"[TARGET] PRF={profile} mode={(manual ? "QUICK" : cycle is null ? "AUTO" : "RESUME")} reason={reason}; chờ SENT thành công rồi mới follow LIVE.");
    }

    bool TryResumeInterruptedTarget()
    {
        var profile = _resumeProfile;
        var cycle = _resumeCycle;
        _resumeProfile = "";
        _resumeCycle = null;

        if (profile.Length == 0) return false;
        if (_profiles.TryGetValue(profile, out var p) && p.ManagerTerminal)
        {
            if (cycle is not null) PersistDetachedCycle(cycle, "MANAGER_REPLACED_WHILE_QUICK");
            return false;
        }

        if (cycle is not null && cycle.StartedUtc != default && DateTime.UtcNow >= cycle.DeadlineUtc)
        {
            PersistDetachedCycle(cycle, "Hết 5 phút trong lúc nhường CHECK NGAY");
            return false;
        }

        ActivateTarget(profile, manual: false, cycle: cycle, reason: "RESUME_AFTER_QUICK_CHECK");
        return true;
    }

    void ExpireInterruptedSessionIfNeeded()
    {
        if (_resumeCycle is null || _resumeCycle.StartedUtc == default) return;
        if (DateTime.UtcNow < _resumeCycle.DeadlineUtc) return;

        var expired = _resumeCycle;
        _resumeCycle = null;
        _resumeProfile = "";
        PersistDetachedCycle(expired, "Hết 5 phút trong lúc nhường CHECK NGAY");
    }

    static string FormatRemaining(CycleState cycle)
    {
        if (cycle.StartedUtc == default || cycle.DeadlineUtc == default) return $"{ProfileCheckMinutes:00}:00";
        var remain = cycle.DeadlineUtc - DateTime.UtcNow;
        if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;
        return $"{(int)remain.TotalMinutes:00}:{remain.Seconds:00}";
    }

    IEnumerable<PendingSend> PendingForSession(string sessionId)
        => _pending.Values.Where(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));

    void MarkCurrentSessionDeadlineReached()
    {
        if (_cycle is null || _cycle.StartedUtc == default || _cycle.Closing) return;
        _cycle.Closing = true;
        _cycle.EndReason = "Hết 5 phút";
        _cycle.DeadlineReachedUtc = DateTime.UtcNow;
        Log($"[SESSION_DEADLINE] PRF={_cycle.Profile} start={_cycle.StartedUtc:O} deadline={_cycle.DeadlineUtc:O} sent={_cycle.SentCount} resolved={_cycle.ResolvedCount} pending={PendingForSession(_cycle.SessionId).Count()}");
        UpdateCycleLabel();
    }

    void TryCompleteClosingSession()
    {
        if (_cycle is null || !_cycle.Closing) return;
        if (PendingForSession(_cycle.SessionId).Any()) return;
        FinishCurrentSession(_cycle.EndReason.Length > 0 ? _cycle.EndReason : "Kết thúc phiên", cancelPendingAsUnknown: false);
    }

    void CancelPendingForProfile(string profile, bool countUnknown, string reason)
    {
        var list = _pending.Values
            .Where(x => x.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in list)
        {
            if (!_pending.Remove(item.Key)) continue;
            _ = _observer.ClearAsync(item.Key);

            CycleState? cycle = null;
            if (_cycle is not null && string.Equals(_cycle.SessionId, item.SessionId, StringComparison.Ordinal)) cycle = _cycle;
            else if (_resumeCycle is not null && string.Equals(_resumeCycle.SessionId, item.SessionId, StringComparison.Ordinal)) cycle = _resumeCycle;

            if (countUnknown && cycle is not null)
            {
                cycle.ResolvedCount++;
                cycle.Unknown++;
                cycle.Details.Add(new CommentResult(
                    DateTimeOffset.Now,
                    item.SendId,
                    item.ContentIndex,
                    item.Username,
                    item.Content,
                    ResultKind.Unknown.ToString(),
                    "CANCELLED:" + reason,
                    item.LiveUrl,
                    null,
                    item.TimeoutSeconds));
            }
        }
    }

    void FinishCurrentSession(string reason, bool cancelPendingAsUnknown, bool chooseNext = true)
    {
        var done = _cycle;
        var profile = _targetProfile;
        if (done is not null)
        {
            if (cancelPendingAsUnknown)
                CancelPendingForProfile(done.Profile, countUnknown: true, reason: reason);
            PersistCycle(done, reason);
        }
        else if (profile.Length > 0)
        {
            CancelPendingForProfile(profile, countUnknown: false, reason: reason);
            Log($"[SESSION_END_NO_DATA] PRF={profile} reason={reason}");
        }

        _cycle = null;
        _targetProfile = "";
        _followRequestedUrl = "";
        _authorizedLiveUrl = "";
        UpdateCycleLabel();

        if (chooseNext && _checking)
            ChooseNextTarget();
        else
            RefreshGrid();
    }

    void PersistDetachedCycle(CycleState cycle, string reason)
    {
        if (cycle is null) return;
        PersistCycle(cycle, reason);
    }

    void PersistCycle(CycleState done, string reason)
    {
        if (done.StartedUtc == default)
        {
            Log($"[SESSION_END_NO_CHECK] PRF={done.Profile} reason={reason}");
            return;
        }

        var endedUtc = DateTime.UtcNow;
        if (reason.StartsWith("Hết 5 phút", StringComparison.OrdinalIgnoreCase) && done.DeadlineUtc != default)
            endedUtc = done.DeadlineUtc;

        if (_profiles.TryGetValue(done.Profile, out var p))
        {
            p.LastVisible = done.Visible;
            p.LastMissing = done.Missing;
            p.LastUnknown = done.Unknown;
            p.LastExpected = done.ResolvedCount;
            p.LastResult = $"{done.Visible} hiện • {done.Missing} mất • {done.Unknown} không rõ";
            p.LastCheck = DateTime.Now;
        }

        var history = new CommentCheckSessionHistory
        {
            SessionId = done.SessionId,
            Profile = done.Profile,
            Username = done.Username,
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(done.StartedUtc, DateTimeKind.Utc)),
            EndedAt = new DateTimeOffset(DateTime.SpecifyKind(endedUtc, DateTimeKind.Utc)),
            DurationSeconds = Math.Max(0, (endedUtc - done.StartedUtc).TotalSeconds),
            Sent = done.SentCount,
            Resolved = done.ResolvedCount,
            Visible = done.Visible,
            Missing = done.Missing,
            Unknown = done.Unknown,
            VisibilityRate = done.Visible + done.Missing > 0 ? done.Visible * 100.0 / (done.Visible + done.Missing) : null,
            EndReason = reason,
            Manual = done.IsManual,
            Comments = done.Details.Select(x => new CommentCheckCommentHistory
            {
                Timestamp = x.Timestamp,
                SendId = x.SendId,
                ContentIndex = x.ContentIndex,
                Username = x.Username,
                Content = x.Content,
                Result = x.Result,
                MatchMode = x.MatchMode,
                LiveUrl = x.LiveUrl,
                LatencySeconds = x.LatencySeconds,
                TimeoutSeconds = x.TimeoutSeconds
            }).ToList()
        };

        // Lượt hiện tại cộng dồn qua các phiên 5 phút. Việc cộng này độc lập với file lịch sử
        // để tỷ lệ ngoài màn chính vẫn đúng ngay cả khi lưu lịch sử gặp lỗi tạm thời.
        AddCompletedCycleToCurrentRun(done);

        try
        {
            _historyStore.Append(history);
            // Lịch sử vẫn giữ thống kê dài hạn của PRF, dùng trong cửa sổ LỊCH SỬ CHECK.
            AddCompletedSessionToAverage(history);
        }
        catch (Exception ex) { Log("[HISTORY_SAVE_WARN] " + ex.Message); }

        // Giữ results.jsonl cũ để không phá workflow chẩn đoán hiện có.
        SaveResult(done);
        Log($"[SESSION_DONE] PRF={done.Profile} duration={history.DurationSeconds:0}s visible={done.Visible} missing={done.Missing} unknown={done.Unknown} rate={(history.VisibilityRate.HasValue ? history.VisibilityRate.Value.ToString("0.0") + "%" : "-")} reason={reason}");
    }
}
