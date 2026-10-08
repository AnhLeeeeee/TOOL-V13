using System.Text;

namespace CommentVisibilityMonitor;

internal sealed partial class MainForm
{
    void HandleManagerOpenSnapshot(TelemetryMessage m)
    {
        var now = DateTime.UtcNow;
        var instanceId = (m.ManagerInstanceId ?? "").Trim();
        if (instanceId.Length == 0) instanceId = "legacy-snapshot";

        // UDP có thể đến lệch thứ tự. Seq chỉ so sánh trong cùng một process Manager;
        // khi Manager restart InstanceId đổi thì chấp nhận snapshot mới ngay.
        if (string.Equals(_managerOpenSnapshotInstanceId, instanceId, StringComparison.Ordinal)
            && m.ManagerSeq > 0
            && _lastManagerOpenSnapshotSeq > 0
            && m.ManagerSeq <= _lastManagerOpenSnapshotSeq)
        {
            return;
        }

        var managerRestarted = _managerOpenSnapshotInstanceId.Length > 0
                               && !string.Equals(_managerOpenSnapshotInstanceId, instanceId, StringComparison.Ordinal);
        if (managerRestarted)
            Log($"[MANAGER_SNAPSHOT_INSTANCE_CHANGED] old={_managerOpenSnapshotInstanceId} new={instanceId}");

        _managerOpenSnapshotInstanceId = instanceId;
        _lastManagerOpenSnapshotSeq = m.ManagerSeq;
        _lastManagerOpenSnapshotUtc = now;

        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reopened = new List<string>();
        foreach (var item in m.Profiles ?? new List<ManagerOpenProfileTelemetry>())
        {
            var profile = (item.Profile ?? "").Trim();
            if (profile.Length == 0 || !open.Add(profile)) continue;

            if (!_profiles.TryGetValue(profile, out var p))
            {
                p = new ProfileState { Profile = profile };
                _profiles[profile] = p;
            }

            var wasManagerPresent = p.ManagerPresent;
            var incomingOpenSessionId = (item.OpenSessionId ?? "").Trim();
            if (incomingOpenSessionId.Length > 0)
            {
                if (!string.Equals(p.ManagerOpenSessionId, incomingOpenSessionId, StringComparison.Ordinal))
                {
                    var previousOpenSessionId = p.ManagerOpenSessionId;
                    if (wasManagerPresent && !string.IsNullOrWhiteSpace(previousOpenSessionId))
                        reopened.Add(profile);
                    p.ManagerOpenSessionId = incomingOpenSessionId;
                    p.ManagerOpenedAtUtc = now;
                    if (item.OpenedAtUtcMs > 0)
                    {
                        try { p.ManagerOpenedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(item.OpenedAtUtcMs).UtcDateTime; }
                        catch { }
                    }
                    Log($"[PROFILE_OPEN_SESSION] PRF={profile} session={incomingOpenSessionId} opened={p.ManagerOpenedAtUtc:O}");
                }
            }
            else if (!wasManagerPresent || string.IsNullOrWhiteSpace(p.ManagerOpenSessionId))
            {
                // Tương thích Manager cũ: vẫn tạo generation cục bộ để dữ liệu thống kê
                // không bị trộn giữa hai lần profile biến mất rồi xuất hiện lại.
                p.ManagerOpenSessionId = "legacy-" + Guid.NewGuid().ToString("N");
                p.ManagerOpenedAtUtc = now;
            }

            p.ManagerSeq = m.ManagerSeq;
            p.ManagerPresent = true;
            p.ManagerTerminal = item.ManagerTerminal;
            p.ManagerReason = item.ManagerReason ?? "";
            p.ManagerRunState = item.RunState ?? "";
            p.LastManagerSeenUtc = now;
        }

        // FULL SNAPSHOT là authoritative: profile từng được Manager báo mở nhưng không
        // còn nằm trong snapshot mới được coi là đã đóng. Không xóa object khỏi bộ nhớ
        // để lịch sử/thống kê phiên cũ vẫn còn, nhưng loại ngay khỏi UI và queue.
        var removed = new List<string>();
        foreach (var p in _profiles.Values)
        {
            if (!p.ManagerPresent || open.Contains(p.Profile)) continue;
            p.ManagerPresent = false;
            p.ManagerTerminal = false;
            p.ManagerReason = "NOT_IN_OPEN_SNAPSHOT";
            p.ManagerRunState = "CLOSED";
            p.LastManagerSeenUtc = now;
            p.ManagerSeq = m.ManagerSeq;
            removed.Add(p.Profile);
        }

        var orderedOpen = open.OrderBy(NaturalProfileKey, StringComparer.OrdinalIgnoreCase).ToArray();
        var openSetKey = string.Join("|", orderedOpen);
        if (!string.Equals(openSetKey, _lastManagerOpenSetKey, StringComparison.Ordinal))
        {
            var removedText = removed.Count == 0 ? "-" : string.Join(",", removed.OrderBy(NaturalProfileKey, StringComparer.OrdinalIgnoreCase));
            Log($"[MANAGER_OPEN_SNAPSHOT] open={orderedOpen.Length} profiles={(orderedOpen.Length == 0 ? "-" : string.Join(",", orderedOpen))} removed={removedText}");
            _lastManagerOpenSetKey = openSetKey;
        }

        // Nếu Manager báo cùng PRF nhưng OpenSessionId đã đổi, nghĩa là profile đã
        // đóng/mở lại giữa hai snapshot mà Monitor nhận được. Không để một phiên 5 phút
        // kéo dài xuyên qua hai lần mở Chrome khác nhau.
        if (!string.IsNullOrWhiteSpace(_targetProfile)
            && reopened.Contains(_targetProfile, StringComparer.OrdinalIgnoreCase))
        {
            var reopenedTarget = _targetProfile;
            Log($"[TARGET_REOPENED_BY_MANAGER_SNAPSHOT] PRF={reopenedTarget} action=FINISH_AND_ROTATE");
            FinishCurrentSession("PRF_REOPENED_BY_MANAGER_SNAPSHOT", cancelPendingAsUnknown: true);
        }

        // Nếu PRF đang là target mà tab đã đóng, bỏ ngay target/queue và chuyển PRF
        // RUNNING kế tiếp. Đây là khác biệt với lỗi telemetry tạm: lỗi tạm vẫn giữ target.
        if (!string.IsNullOrWhiteSpace(_targetProfile) && !open.Contains(_targetProfile))
        {
            var closed = _targetProfile;
            Log($"[TARGET_CLOSED_BY_MANAGER_SNAPSHOT] PRF={closed} action=FINISH_AND_ROTATE");
            FinishCurrentSession("PRF_CLOSED_BY_MANAGER_SNAPSHOT", cancelPendingAsUnknown: true);
        }

        // Phiên auto đang tạm nhường cho CHECK NGAY cũng không được resume một PRF
        // đã đóng hoặc đã đóng/mở lại thành một OpenSession mới.
        if (!string.IsNullOrWhiteSpace(_resumeProfile)
            && (!open.Contains(_resumeProfile)
                || reopened.Contains(_resumeProfile, StringComparer.OrdinalIgnoreCase)))
        {
            var closedResume = _resumeProfile;
            var wasReopened = reopened.Contains(_resumeProfile, StringComparer.OrdinalIgnoreCase);
            if (_resumeCycle is not null)
                PersistDetachedCycle(_resumeCycle, wasReopened ? "PRF_REOPENED_WHILE_QUICK_CHECK" : "PRF_CLOSED_WHILE_QUICK_CHECK");
            _resumeProfile = "";
            _resumeCycle = null;
            Log($"[RESUME_TARGET_INVALID_BY_MANAGER_SNAPSHOT] PRF={closedResume} reopened={wasReopened} action=DROP_RESUME");
        }
    }

    void HandleManagerStateTelemetry(TelemetryMessage m)
    {
        // Khi FULL SNAPSHOT đang hoạt động, bỏ MANAGER_STATE cũ/out-of-order để một
        // gói UDP trễ không thể làm sống lại PRF vừa bị snapshot xác nhận đã đóng.
        if (HasFreshManagerOpenSnapshot())
        {
            var instanceId = (m.ManagerInstanceId ?? "").Trim();
            // Manager mới luôn gắn InstanceId. Gói MANAGER_STATE không có InstanceId
            // trong khi snapshot mới đang fresh được coi là legacy/stale và bỏ qua.
            if (instanceId.Length == 0)
                return;
            if (_managerOpenSnapshotInstanceId.Length > 0
                && !string.Equals(instanceId, _managerOpenSnapshotInstanceId, StringComparison.Ordinal))
                return;

            if (m.ManagerSeq > 0
                && _lastManagerOpenSnapshotSeq > 0
                && m.ManagerSeq <= _lastManagerOpenSnapshotSeq)
                return;
        }

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
        // FULL SNAPSHOT quyết định profile có đang mở hay không. Chỉ RUNNING mới
        // được chọn làm target MỚI; OPENING/HOLD/PAUSED/RECOVERING vẫn hiện ở UI
        // nhưng chỉ ở trạng thái chờ, không bắt đầu đồng hồ quét 5 phút.
        if (HasFreshManagerOpenSnapshot())
        {
            return p.ManagerPresent
                   && !p.ManagerTerminal
                   && p.ManagerRunState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase);
        }

        // Tương thích Manager cũ/chưa nhận snapshot: giữ fallback Worker hiện có.
        // Nhưng nếu FULL SNAPSHOT trước đó đã xác nhận đóng thì không cho Worker
        // heartbeat cũ làm PRF quay lại hàng quét.
        if (!p.ManagerPresent
            && p.ManagerReason.Equals("NOT_IN_OPEN_SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            return false;
        if (p.ManagerTerminal) return false;

        var managerFresh = p.LastManagerSeenUtc != default
                           && DateTime.UtcNow - p.LastManagerSeenUtc < ManagerOpenSnapshotFreshness;
        if (managerFresh && p.ManagerRunState.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
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
            _rotationVisited.Clear();
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

    void OpenStatisticsWindow()
    {
        try
        {
            if (_statisticsForm is not null && !_statisticsForm.IsDisposed)
            {
                if (_statisticsForm.WindowState == FormWindowState.Minimized)
                    _statisticsForm.WindowState = FormWindowState.Normal;
                _statisticsForm.BringToFront();
                _statisticsForm.Focus();
                _statisticsForm.RefreshStatistics();
                return;
            }

            _statisticsForm = new CommentCheckStatisticsForm(
                _historyStore,
                GetOpenProfileNamesForStatistics,
                Log);
            _statisticsForm.FormClosed += (_, _) => _statisticsForm = null;
            _statisticsForm.Show(this);
        }
        catch (Exception ex)
        {
            Log("[STATISTICS_OPEN_ERROR] " + ex);
            MessageBox.Show(this, "Không mở được Thống kê Check CMT:\n" + ex.Message, "Thống kê Check CMT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    IReadOnlyList<string> GetOpenProfileNamesForStatistics()
        => _profiles.Values
            .Where(IsVisibleOnMainGrid)
            .Select(x => x.Profile)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NaturalProfileKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

    CycleState CreateTargetCycle(string profile, bool manual)
    {
        var now = DateTime.UtcNow;
        _profiles.TryGetValue(profile, out var p);
        return new CycleState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Profile = profile,
            Username = p?.Username ?? "",
            CreatedUtc = now,
            StartedUtc = now,
            DeadlineUtc = now.Add(ProfileCheckDuration),
            IsManual = manual,
            OpenSessionId = p?.ManagerOpenSessionId ?? "",
            ProfileOpenedAtUtc = p?.ManagerOpenedAtUtc ?? default
        };
    }

    void ActivateTarget(string profile, bool manual, CycleState? cycle, string reason)
    {
        profile = (profile ?? "").Trim();
        if (profile.Length == 0) return;

        _targetProfile = profile;
        _manualTarget = manual;
        _cycle = cycle ?? CreateTargetCycle(profile, manual);

        // Defensive cho cycle cũ/resume: không bao giờ cho một target tồn tại mà thiếu
        // đồng hồ tuyệt đối. Resume giữ deadline cũ, tuyệt đối không pause/reset 5 phút.
        if (_cycle.StartedUtc == default)
            _cycle.StartedUtc = DateTime.UtcNow;
        if (_cycle.DeadlineUtc == default)
            _cycle.DeadlineUtc = _cycle.StartedUtc.Add(ProfileCheckDuration);

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
        Log($"[TARGET] PRF={profile} mode={(manual ? "QUICK" : cycle is null ? "AUTO" : "RESUME")} reason={reason} slotStart={_cycle.StartedUtc:O} deadline={_cycle.DeadlineUtc:O}; tối đa {ProfileCheckMinutes} phút rồi bắt buộc chuyển.");
    }

    bool TryResumeInterruptedTarget()
    {
        var profile = _resumeProfile;
        var cycle = _resumeCycle;
        _resumeProfile = "";
        _resumeCycle = null;

        if (profile.Length == 0) return false;
        if (_profiles.TryGetValue(profile, out var p)
            && (p.ManagerTerminal || (HasFreshManagerOpenSnapshot() && !p.ManagerPresent)))
        {
            if (cycle is not null) PersistDetachedCycle(cycle, "MANAGER_REPLACED_OR_CLOSED_WHILE_QUICK");
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
        if (_cycle is null || _cycle.StartedUtc == default) return;

        var done = _cycle;
        var pending = PendingForSession(done.SessionId).Count();
        done.Closing = true;
        done.EndReason = "Hết 5 phút";
        done.DeadlineReachedUtc = DateTime.UtcNow;
        Log($"[SESSION_DEADLINE_HARD] PRF={done.Profile} start={done.StartedUtc:O} deadline={done.DeadlineUtc:O} sent={done.SentCount} resolved={done.ResolvedCount} pending={pending} action=CANCEL_PENDING_AND_ROTATE");

        // Quy tắc chốt: 5 phút là ngân sách TỔNG của PRF kể từ lúc được chọn.
        // Không đợi thêm pending và không đợi SENT đầu tiên; chốt pending là Unknown
        // rồi chuyển sang PRF chưa được ghé trong vòng hiện tại.
        FinishCurrentSession("Hết 5 phút", cancelPendingAsUnknown: true);
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
            OpenSessionId = done.OpenSessionId,
            ProfileOpenedAt = done.ProfileOpenedAtUtc == default
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(done.ProfileOpenedAtUtc, DateTimeKind.Utc)),
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
