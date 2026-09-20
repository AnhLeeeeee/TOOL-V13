using System.Text.Json.Serialization;

namespace ToolTikTokManagerV13.Proxy;

public enum ProxyProtocol
{
    Http,
    Https,
    Socks5
}

public enum ProxyDistributionMode
{
    PerProfile = 0,
    ManagerShared = 1
}

public enum ProxyHealthState
{
    Untested,
    Testing,
    Good,
    Slow,
    Timeout,
    AuthError,
    Dead,
    Error
}

public sealed class ProxyEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ProxyHealthState Health { get; set; } = ProxyHealthState.Untested;
    public string ExitIp { get; set; } = "";
    public long LastLatencyMs { get; set; }
    public DateTimeOffset? LastTestUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? QuarantineUntilUtc { get; set; }
    public string LastError { get; set; } = "";

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

    [JsonIgnore]
    public bool IsHealthy
        => Enabled
           && (Health == ProxyHealthState.Good || Health == ProxyHealthState.Slow)
           && (QuarantineUntilUtc is null || QuarantineUntilUtc <= DateTimeOffset.UtcNow);

    [JsonIgnore]
    public string DedupKey
        => $"{Protocol}|{Host.Trim().ToLowerInvariant()}|{Port}|{Username.Trim()}";

    [JsonIgnore]
    public string MaskedDisplay
    {
        get
        {
            var scheme = Protocol switch
            {
                ProxyProtocol.Https => "https",
                ProxyProtocol.Socks5 => "socks5",
                _ => "http"
            };
            return HasCredentials
                ? $"{scheme}://{Username}:***@{Host}:{Port}"
                : $"{scheme}://{Host}:{Port}";
        }
    }
}

public sealed class ProxyAssignment
{
    public string ProfileName { get; set; } = "";
    public string ProxyId { get; set; } = "";
    public DateTimeOffset AssignedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProxySettings
{
    /// <summary>Master switch. Khi false, module Proxy không can thiệp luồng Chrome.</summary>
    public bool Enabled { get; set; }

    /// <summary>PerProfile = logic gán cũ; ManagerShared = toàn bộ PRF dùng chung một Proxy theo thứ tự Pool.</summary>
    public ProxyDistributionMode DistributionMode { get; set; } = ProxyDistributionMode.PerProfile;

    /// <summary>Nếu false, mọi PRF Enabled đều có thể được gán Proxy.</summary>
    public bool LimitAssignedProfiles { get; set; }
    public int AssignedProfileLimit { get; set; } = 10;

    /// <summary>Số PRF tối đa dùng chung một Proxy. Tool lấp đầy Proxy hiện tại rồi mới chuyển Proxy kế tiếp.</summary>
    public int ProfilesPerProxy { get; set; } = 1;

    public bool AutoAssignNewProfiles { get; set; } = true;
    public bool AutoReplaceBadProxy { get; set; } = true;
    public int FailureThreshold { get; set; } = 2;
    public int QuarantineMinutes { get; set; } = 10;
    public ProxyProtocol DefaultProtocol { get; set; } = ProxyProtocol.Http;
}

public sealed class ProxyState
{
    public ProxySettings Settings { get; set; } = new();
    public List<ProxyEndpoint> Proxies { get; set; } = [];
    public List<ProxyAssignment> Assignments { get; set; } = [];

    /// <summary>Proxy chung đang dùng khi DistributionMode=ManagerShared. Danh sách Proxies giữ thứ tự ưu tiên/failover.</summary>
    public string ActiveManagerProxyId { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ProxyTestResult(
    ProxyHealthState Health,
    string ExitIp,
    long LatencyMs,
    string Error);

public sealed record ProxyPrepareResult(
    bool ProxyEnabled,
    bool Applied,
    string ProfileName,
    string ProxyDisplay,
    string Reason);

public static class ProxyLineParser
{
    public static bool TryParse(string? raw, ProxyProtocol defaultProtocol, out ProxyEndpoint endpoint, out string error)
    {
        endpoint = new ProxyEndpoint { Protocol = defaultProtocol };
        error = "";
        var line = (raw ?? "").Trim();
        if (line.Length == 0)
        {
            error = "Dòng trống.";
            return false;
        }

        try
        {
            if (line.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
                {
                    error = "URL proxy không hợp lệ.";
                    return false;
                }

                endpoint.Protocol = ParseProtocol(uri.Scheme, defaultProtocol);
                endpoint.Host = uri.Host;
                endpoint.Port = uri.Port;
                if (!string.IsNullOrWhiteSpace(uri.UserInfo))
                {
                    var userInfo = uri.UserInfo.Split(':', 2);
                    endpoint.Username = Uri.UnescapeDataString(userInfo[0]);
                    endpoint.Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
                }
                return Validate(endpoint, out error);
            }

            string hostPort;
            string username = "";
            string password = "";

            var at = line.LastIndexOf('@');
            if (at > 0 && at < line.Length - 1)
            {
                var auth = line[..at];
                hostPort = line[(at + 1)..];
                var authParts = auth.Split(':', 2);
                username = authParts[0].Trim();
                password = authParts.Length > 1 ? authParts[1] : "";
            }
            else
            {
                var parts = line.Split(':');
                if (parts.Length == 2)
                {
                    hostPort = line;
                }
                else if (parts.Length >= 4)
                {
                    hostPort = parts[0] + ":" + parts[1];
                    username = parts[2].Trim();
                    password = string.Join(":", parts.Skip(3));
                }
                else
                {
                    error = "Định dạng hỗ trợ: ip:port, ip:port:user:pass, user:pass@ip:port hoặc URL có scheme.";
                    return false;
                }
            }

            var hp = hostPort.Split(':', 2);
            if (hp.Length != 2 || !int.TryParse(hp[1], out var port))
            {
                error = "Host/port không hợp lệ.";
                return false;
            }

            endpoint.Host = hp[0].Trim();
            endpoint.Port = port;
            endpoint.Username = username;
            endpoint.Password = password;
            return Validate(endpoint, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static ProxyProtocol ParseProtocol(string? value, ProxyProtocol fallback)
        => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "http" => ProxyProtocol.Http,
            "https" => ProxyProtocol.Https,
            "socks" => ProxyProtocol.Socks5,
            "socks5" => ProxyProtocol.Socks5,
            _ => fallback
        };

    static bool Validate(ProxyEndpoint endpoint, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(endpoint.Host)
            || endpoint.Host.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '/' or '\\'))
        {
            error = "Host proxy không hợp lệ.";
            return false;
        }
        if (endpoint.Port is < 1 or > 65535)
        {
            error = "Port proxy phải từ 1 đến 65535.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(endpoint.Username) && !string.IsNullOrWhiteSpace(endpoint.Password))
        {
            error = "Có password nhưng thiếu username.";
            return false;
        }
        return true;
    }
}
