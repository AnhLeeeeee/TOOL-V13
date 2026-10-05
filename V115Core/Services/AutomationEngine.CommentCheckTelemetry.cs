using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    const int CommentCheckTelemetryPort = 47771;
    readonly object _commentCheckTelemetryLock = new();
    readonly Dictionary<long, string> _commentCheckSendLiveUrls = new();
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

    void EmitCommentCheckTelemetry(
        string type,
        long sendId = 0,
        int contentIndex = 0,
        string? content = null,
        string? liveUrlOverride = null,
        bool liveUrlVerified = false)
    {
        try
        {
            InitializeCommentCheckTelemetryIdentity();
            var url = liveUrlOverride is not null
                ? liveUrlOverride.Trim()
                : (_chrome.Page?.Url ?? "").Trim();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Type = type,
                Profile = _commentCheckProfileName,
                Username = _commentCheckUsername,
                RunState = !_running ? "STOPPED" : _paused ? "PAUSED" : "RUNNING",
                LiveUrl = url,
                LiveUrlVerified = liveUrlVerified,
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

    async Task<string> ReadFreshCommentCheckLiveUrlAsync(CancellationToken ct)
    {
        // Page.Url là snapshot lúc attach và có thể stale sau khi tab đã chuyển LIVE.
        // Với WILL_SEND phải lấy URL thật ngay trước Enter; nếu không xác minh được thì
        // gửi URL rỗng để Monitor KHÔNG arm/quét nhầm LIVE. Telemetry vẫn fail-open.
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                var r = await _chrome.EvalAsync("(() => String(location.href || ''))()", ct: probeCts.Token);
                if (r.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var href = (value.GetString() ?? "").Trim();
                    if (href.Length > 0) return href;
                }
            }
            catch
            {
                // Runtime.evaluate có thể fail khi target vừa điều hướng; thử refresh metadata cùng target.
            }

            try
            {
                var fresh = await _chrome.RefreshAttachedPageMetadataAsync();
                return (fresh?.Url ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }
        catch
        {
            return "";
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

    async Task<long> EmitCommentCheckWillSendAsync(int contentIndex, string content, CancellationToken ct)
    {
        var sendId = Interlocked.Increment(ref _commentCheckTelemetrySeq);
        // WILL_SEND là gate quyết định Monitor có được arm hay không, nên phải dùng URL thật mới đọc.
        var freshLiveUrl = await ReadFreshCommentCheckLiveUrlAsync(ct);
        if (freshLiveUrl.Length > 0)
        {
            lock (_commentCheckTelemetryLock)
                _commentCheckSendLiveUrls[sendId] = freshLiveUrl;
        }

        // EmitCommentCheckTelemetry cũng tăng Seq độc lập; SendId chỉ cần duy nhất trong Worker/profile.
        EmitCommentCheckTelemetry(
            "WILL_SEND",
            sendId,
            contentIndex,
            content,
            liveUrlOverride: freshLiveUrl,
            liveUrlVerified: freshLiveUrl.Length > 0);
        return sendId;
    }

    void EmitCommentCheckSent(long sendId, int contentIndex, string content)
    {
        string verifiedLiveUrl = "";
        lock (_commentCheckTelemetryLock)
        {
            if (_commentCheckSendLiveUrls.TryGetValue(sendId, out var saved))
            {
                verifiedLiveUrl = saved;
                _commentCheckSendLiveUrls.Remove(sendId);
            }
        }

        EmitCommentCheckTelemetry(
            "SENT",
            sendId,
            contentIndex,
            content,
            liveUrlOverride: verifiedLiveUrl.Length > 0 ? verifiedLiveUrl : null,
            liveUrlVerified: verifiedLiveUrl.Length > 0);
    }
}
