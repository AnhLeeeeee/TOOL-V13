using System.Text.Json;

namespace ToolTikTokV11.Services;

internal sealed class ProxyLaunchProfile
{
    public bool Enabled { get; set; }
    public string Protocol { get; set; } = "Http";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ProxyId { get; set; } = "";
    public string DiagnosticsDirectory { get; set; } = "";

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

    public static ProxyLaunchProfile? TryLoad(string profileDir, out string error)
    {
        error = "";
        try
        {
            var path = Path.Combine(profileDir, ".tool_proxy.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var config = JsonSerializer.Deserialize<ProxyLaunchProfile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (config is null || !config.Enabled) return null;
            if (!IsSafeHost(config.Host) || config.Port is < 1 or > 65535)
            {
                error = "proxy_config_invalid_host_or_port";
                return null;
            }
            if (string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(config.Password))
            {
                error = "proxy_config_password_without_username";
                return null;
            }
            return config;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public string Scheme
        => (Protocol ?? "").Trim().ToLowerInvariant() switch
        {
            "https" => "https",
            "socks5" => "socks5",
            "socks" => "socks5",
            _ => "http"
        };

    public string MaskedDisplay
        => HasCredentials
            ? $"{Scheme}://{Username}:***@{Host}:{Port}"
            : $"{Scheme}://{Host}:{Port}";

    static bool IsSafeHost(string? value)
    {
        var host = (value ?? "").Trim();
        return host.Length > 0
               && !host.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '/' or '\\');
    }
}
