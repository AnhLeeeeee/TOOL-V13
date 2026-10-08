using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const int CommentCheckManagerTelemetryPort = 47771;
    UdpClient? _commentCheckManagerTelemetryUdp;
    long _commentCheckManagerTelemetrySeq;
    DateTime _commentCheckManagerTelemetryLastUtc = DateTime.MinValue;
    readonly string _commentCheckManagerTelemetryInstanceId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    readonly Dictionary<string, CommentCheckManagerOpenSession> _commentCheckManagerOpenSessions = new(StringComparer.OrdinalIgnoreCase);

    sealed record CommentCheckManagerOpenSession(string SessionId, DateTime OpenedAtUtc);

    sealed record CommentCheckManagerOpenProfileSnapshot(
        string Profile,
        string RunState,
        bool ManagerTerminal,
        string ManagerReason,
        string OpenSessionId,
        long OpenedAtUtcMs);

    void InitializeCommentCheckManagerStateTelemetry()
    {
        _refreshTimer.Tick += (_, _) => EmitCommentCheckManagerStateTelemetry();
        FormClosed += (_, _) =>
        {
            try { _commentCheckManagerTelemetryUdp?.Dispose(); } catch { }
            _commentCheckManagerTelemetryUdp = null;
        };
        EmitCommentCheckManagerStateTelemetry(force: true);
    }

    void EmitCommentCheckManagerStateTelemetry(bool force = false)
    {
        if (_closing) return;
        var now = DateTime.UtcNow;
        if (!force && now - _commentCheckManagerTelemetryLastUtc < TimeSpan.FromSeconds(1)) return;
        _commentCheckManagerTelemetryLastUtc = now;

        try
        {
            _commentCheckManagerTelemetryUdp ??= new UdpClient(AddressFamily.InterNetwork);
            var endpoint = new IPEndPoint(IPAddress.Loopback, CommentCheckManagerTelemetryPort);

            // IMPORTANT: nguồn sự thật của giao diện Check CMT là danh sách TAB profile
            // đang mở trong Manager, KHÔNG phải trạng thái RUNNING của Worker.
            // Vì vậy cả OPENING / STOPPED / PAUSED / RECOVERING vẫn phải có mặt trong
            // snapshot; RUNNING chỉ được Monitor dùng để quyết định PRF có đủ điều kiện
            // bắt đầu một lượt quét mới hay chưa.
            var openContexts = _contexts.Values
                .Where(IsDashboardProfileOpen)
                .OrderBy(x => x.Profile.Name, NaturalProfileNameOrder)
                .ToArray();

            var profiles = new List<CommentCheckManagerOpenProfileSnapshot>(openContexts.Length);
            foreach (var ctx in openContexts)
            {
                var profile = (ctx.Profile.Name ?? "").Trim();
                if (profile.Length == 0) continue;

                var inAutoClose = _autoCloseInProgressProfiles.Contains(profile);
                var deletePending = _autoRetiredProfileDeleteInProgress.Contains(profile);
                var deleted = _autoRetiredProfileDeleted.Contains(profile);
                var retiredForReplacement = _autoReplacementRetiredProfiles.Contains(profile);
                var terminal = inAutoClose || deletePending || deleted || retiredForReplacement;

                var reason = terminal
                    ? inAutoClose ? "AUTO_CLOSE_IN_PROGRESS"
                    : deletePending ? "DELETE_PENDING"
                    : deleted ? "DELETED"
                    : "RETIRED_FOR_REPLACEMENT"
                    : "";

                // Lấy source đã persist chỉ để làm rõ lý do trong log/history. Nếu đọc lỗi
                // thì giữ reason in-memory; lỗi phụ tuyệt đối không ảnh hưởng Manager.
                if (terminal)
                {
                    try
                    {
                        var supply = GetProfileSupplyState(profile);
                        if (!string.IsNullOrWhiteSpace(supply?.Source)) reason = supply!.Source;
                    }
                    catch { }
                }

                var effectiveState = ctx.Opening ? "OPENING" : GetEffectiveRuntimeState(ctx);
                if (string.IsNullOrWhiteSpace(effectiveState)) effectiveState = "OPEN";

                if (!_commentCheckManagerOpenSessions.TryGetValue(profile, out var openSession))
                {
                    openSession = new CommentCheckManagerOpenSession(
                        Guid.NewGuid().ToString("N"),
                        now);
                    _commentCheckManagerOpenSessions[profile] = openSession;
                }

                profiles.Add(new CommentCheckManagerOpenProfileSnapshot(
                    profile,
                    effectiveState,
                    terminal,
                    reason,
                    openSession.SessionId,
                    new DateTimeOffset(DateTime.SpecifyKind(openSession.OpenedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()));
            }

            // Khi profile biến mất khỏi danh sách tab đang mở, quên open-session cũ.
            // Lần mở lại kế tiếp sẽ có SessionId mới để Thống kê tách đúng từng lần mở Chrome.
            var openNames = profiles
                .Select(x => x.Profile)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in _commentCheckManagerOpenSessions.Keys.Where(x => !openNames.Contains(x)).ToList())
                _commentCheckManagerOpenSessions.Remove(profile);

            // FULL SNAPSHOT mỗi 1 giây: kể cả Profiles=[] vẫn phải phát để Monitor biết
            // toàn bộ PRF đã đóng. Cách này tự hồi phục khi Monitor mở trễ hoặc UDP mất
            // một vài gói đơn lẻ, tránh danh sách Check CMT lệch với Manager lâu dài.
            var snapshotSeq = Interlocked.Increment(ref _commentCheckManagerTelemetrySeq);
            var snapshotPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Type = "MANAGER_OPEN_SNAPSHOT",
                ManagerInstanceId = _commentCheckManagerTelemetryInstanceId,
                ManagerSeq = snapshotSeq,
                SentAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Profiles = profiles
            });
            _commentCheckManagerTelemetryUdp.Send(snapshotPayload, snapshotPayload.Length, endpoint);

            // Giữ MANAGER_STATE cho tương thích với Comment Check cũ. Chỉ phát cho profile
            // thật sự đang mở; Monitor mới sẽ dùng FULL SNAPSHOT làm nguồn sự thật chính.
            foreach (var item in profiles)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Type = "MANAGER_STATE",
                    Profile = item.Profile,
                    RunState = item.RunState,
                    ManagerPresent = true,
                    ManagerTerminal = item.ManagerTerminal,
                    ManagerReason = item.ManagerReason,
                    ManagerInstanceId = _commentCheckManagerTelemetryInstanceId,
                    ManagerSeq = Interlocked.Increment(ref _commentCheckManagerTelemetrySeq),
                    SentAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                _commentCheckManagerTelemetryUdp.Send(payload, payload.Length, endpoint);
            }
        }
        catch
        {
            // Read-only telemetry cho Comment Check: fail-open, không được làm ảnh hưởng Manager.
        }
    }
}
