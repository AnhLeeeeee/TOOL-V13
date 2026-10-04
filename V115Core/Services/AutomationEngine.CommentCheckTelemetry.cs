using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    const int CommentCheckTelemetryPort = 47771;
    readonly object _commentCheckTelemetryLock = new();
    UdpClient? _commentCheckTelemetryUdp;
    long _commentCheckTelemetrySeq;
    DateTime _commentCheckLastHeartbeatUtc = DateTime.MinValue;
    string _commentCheckLastHeartbeatUrl = "";
    int _commentCheckLastHeartbeatIndex = -1;
    string _commentCheckProfileName = "";
    string _commentCheckUsername = "";


    public void ConfigureCommentCheckTelemetryProfile(string profileName)
    {
        var value = (profileName ?? "").Trim();
        if (value.Length > 0) _commentCheckProfileName = value;
    }

    void InitializeCommentCheckTelemetryIdentity()
    {
        if (string.IsNullOrWhiteSpace(_commentCheckProfileName))
        {
            try
            {
                _commentCheckProfileName = Path.GetFileName(
                    _baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch { _commentCheckProfileName = ""; }
        }

        // Username trong tiktok_auth.json là plaintext; không đụng password/2FA.
        // Chỉ refresh khi Start để account vừa thay đổi cũng được phản ánh đúng.
        try
        {
            var path = Path.Combine(_baseDir, "tiktok_auth.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Username", out var u)
                    && u.ValueKind == JsonValueKind.String)
                {
                    var rawUsername = (u.GetString() ?? "").Trim().TrimStart('@');
                    _commentCheckUsername = rawUsername.Length >= 2 && rawUsername.Length <= 64
                        && rawUsername.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_')
                            ? rawUsername
                            : "";
                }
            }
        }
        catch { }
    }

    void EmitCommentCheckTelemetry(string type, long sendId = 0, int contentIndex = 0, string? content = null)
    {
        try
        {
            InitializeCommentCheckTelemetryIdentity();
            var url = (_chrome.Page?.Url ?? "").Trim();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Type = type,
                Profile = _commentCheckProfileName,
                Username = _commentCheckUsername,
                RunState = !_running ? "STOPPED" : _paused ? "PAUSED" : "RUNNING",
                LiveUrl = url,
                ContentIndex = contentIndex > 0 ? contentIndex : (_contents.Count > 0 ? _contentIndex + 1 : 0),
                ContentTotal = _contents.Count,
                Content = content ?? "",
                SendId = sendId,
                Seq = Interlocked.Increment(ref _commentCheckTelemetrySeq),
                SentAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Pid = Environment.ProcessId
            });

            lock (_commentCheckTelemetryLock)
            {
                _commentCheckTelemetryUdp ??= new UdpClient(AddressFamily.InterNetwork);
                _commentCheckTelemetryUdp.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, CommentCheckTelemetryPort));
            }
        }
        catch
        {
            // Telemetry là read-only/fail-open: monitor tắt/lỗi tuyệt đối không ảnh hưởng Worker.
        }
    }

    void EmitCommentCheckHeartbeat(bool force = false)
    {
        if (!_running || _paused) return;
        var now = DateTime.UtcNow;
        var url = (_chrome.Page?.Url ?? "").Trim();
        var index = _contents.Count > 0 ? _contentIndex + 1 : 0;
        if (!force
            && now - _commentCheckLastHeartbeatUtc < TimeSpan.FromSeconds(1)
            && string.Equals(url, _commentCheckLastHeartbeatUrl, StringComparison.Ordinal)
            && index == _commentCheckLastHeartbeatIndex)
            return;

        _commentCheckLastHeartbeatUtc = now;
        _commentCheckLastHeartbeatUrl = url;
        _commentCheckLastHeartbeatIndex = index;
        EmitCommentCheckTelemetry("HEARTBEAT", contentIndex: index);
    }

    long EmitCommentCheckWillSend(int contentIndex, string content)
    {
        var sendId = Interlocked.Increment(ref _commentCheckTelemetrySeq);
        // EmitCommentCheckTelemetry cũng tăng Seq độc lập; SendId chỉ cần duy nhất trong Worker/profile.
        EmitCommentCheckTelemetry("WILL_SEND", sendId, contentIndex, content);
        return sendId;
    }

    void EmitCommentCheckSent(long sendId, int contentIndex, string content)
        => EmitCommentCheckTelemetry("SENT", sendId, contentIndex, content);
}
