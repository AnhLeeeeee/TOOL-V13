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

            foreach (var ctx in _contexts.Values.ToArray())
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

                var effectiveState = GetEffectiveRuntimeState(ctx);
                if (!terminal
                    && effectiveState is not (RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering))
                {
                    continue;
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Type = "MANAGER_STATE",
                    Profile = profile,
                    RunState = effectiveState,
                    ManagerPresent = true,
                    ManagerTerminal = terminal,
                    ManagerReason = reason,
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
