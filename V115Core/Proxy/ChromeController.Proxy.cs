using System.Text;
using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    string BuildOptionalProxyLaunchArguments(string profileDir)
    {
        try
        {
            var config = ProxyLaunchProfile.TryLoad(profileDir, out var loadError);
            if (config is null)
            {
                if (!string.IsNullOrWhiteSpace(loadError))
                    _log.Warn($"[PROXY_WORKER_FAIL_OPEN] profile={profileDir} reason={loadError} action=direct_network");
                return "";
            }

            if (config.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) && config.HasCredentials)
            {
                _log.Warn($"[PROXY_WORKER_FAIL_OPEN] proxy={config.MaskedDisplay} reason=socks5_auth_not_supported_by_chrome action=direct_network");
                return "";
            }

            var proxyServer = $"{config.Scheme}://{config.Host}:{config.Port}";
            var flags = $"--proxy-server=\"{proxyServer}\" --proxy-bypass-list=\"localhost;127.0.0.1\" ";

            if (config.HasCredentials)
            {
                var extensionDir = EnsureProxyAuthExtension(profileDir, config);
                if (string.IsNullOrWhiteSpace(extensionDir))
                {
                    _log.Warn($"[PROXY_WORKER_FAIL_OPEN] proxy={config.MaskedDisplay} reason=auth_extension_create_failed action=direct_network");
                    return "";
                }
                flags += $"--load-extension=\"{extensionDir}\" ";
            }

            _log.Info($"[PROXY_WORKER_ENABLED] proxy={config.MaskedDisplay} auth={(config.HasCredentials ? "yes" : "no")}");
            return flags;
        }
        catch (Exception ex)
        {
            // Proxy là module tùy chọn. Bất kỳ lỗi nào ở đây phải rơi về launch Chrome cũ.
            _log.Warn($"[PROXY_WORKER_FAIL_OPEN] reason={ShortProxyError(ex.Message)} action=direct_network");
            return "";
        }
    }

    string? EnsureProxyAuthExtension(string profileDir, ProxyLaunchProfile config)
    {
        try
        {
            var root = Path.Combine(profileDir, ".tool_proxy_extension");
            Directory.CreateDirectory(root);
            var manifest = """
{
  "manifest_version": 3,
  "name": "ToolTikTok Proxy Auth",
  "version": "1.0.0",
  "permissions": ["proxy", "storage", "webRequest", "webRequestAuthProvider"],
  "host_permissions": ["<all_urls>"],
  "background": { "service_worker": "background.js" }
}
""";
            File.WriteAllText(Path.Combine(root, "manifest.json"), manifest, new UTF8Encoding(false));

            var scheme = JsonSerializer.Serialize(config.Scheme);
            var host = JsonSerializer.Serialize(config.Host);
            var username = JsonSerializer.Serialize(config.Username);
            var password = JsonSerializer.Serialize(config.Password);
            var script = $$"""
const proxy = {
  scheme: {{scheme}},
  host: {{host}},
  port: {{config.Port}},
  username: {{username}},
  password: {{password}}
};

function applyProxy() {
  chrome.proxy.settings.set({
    value: {
      mode: "fixed_servers",
      rules: {
        singleProxy: { scheme: proxy.scheme, host: proxy.host, port: proxy.port },
        bypassList: ["localhost", "127.0.0.1"]
      }
    },
    scope: "regular"
  });
}

applyProxy();
chrome.runtime.onStartup.addListener(applyProxy);
chrome.runtime.onInstalled.addListener(applyProxy);

const proxyAuthAttempts = new Map();
chrome.webRequest.onAuthRequired.addListener(
  (details, callback) => {
    if (!details.isProxy) {
      callback({});
      return;
    }
    const count = proxyAuthAttempts.get(details.requestId) || 0;
    if (count >= 2) {
      callback({ cancel: true });
      return;
    }
    proxyAuthAttempts.set(details.requestId, count + 1);
    callback({ authCredentials: { username: proxy.username, password: proxy.password } });
  },
  { urls: ["<all_urls>"] },
  ["asyncBlocking"]
);

function clearAuthAttempt(details) { proxyAuthAttempts.delete(details.requestId); }
chrome.webRequest.onCompleted.addListener(clearAuthAttempt, { urls: ["<all_urls>"] });
chrome.webRequest.onErrorOccurred.addListener(clearAuthAttempt, { urls: ["<all_urls>"] });
""";
            File.WriteAllText(Path.Combine(root, "background.js"), script, new UTF8Encoding(false));
            return root;
        }
        catch (Exception ex)
        {
            _log.Warn($"[PROXY_AUTH_EXTENSION_ERROR] proxy={config.MaskedDisplay} detail={ShortProxyError(ex.Message)}");
            return null;
        }
    }

    static string ShortProxyError(string? message)
    {
        var text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 200 ? text : text[..200];
    }
}
