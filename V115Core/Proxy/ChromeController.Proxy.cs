namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    LocalProxyBridge? _proxyBridge;

    string BuildOptionalProxyLaunchArguments(string profileDir)
    {
        StopProxyBridge();
        var config = ProxyLaunchProfile.TryLoad(profileDir, out var loadError);
        if (config is null)
        {
            if (!string.IsNullOrWhiteSpace(loadError))
            {
                _log.Warn($"[PROXY_WORKER_CONFIG_ERROR] profile={profileDir} reason={ShortProxyError(loadError)} action=direct_network");
                ProxyWorkerDiagnostics.Write(null, "WORKER_CONFIG_ERROR", $"profile={profileDir} reason={ShortProxyError(loadError)}");
            }
            return "";
        }

        try
        {
            // Proxy không auth vẫn có thể dùng Chromium trực tiếp. Proxy có auth được
            // bọc qua localhost để Chrome không hiện popup username/password.
            if (!config.HasCredentials)
            {
                var proxyServer = $"{config.Scheme}://{config.Host}:{config.Port}";
                var flags = $"--proxy-server=\"{proxyServer}\" --proxy-bypass-list=\"localhost;127.0.0.1\" ";
                _log.Info($"[PROXY_WORKER_ENABLED] proxy={config.MaskedDisplay} auth=no transport=direct_chromium");
                ProxyWorkerDiagnostics.Write(config, "WORKER_ENABLED", $"proxy={config.MaskedDisplay} auth=no transport=direct_chromium");
                return flags;
            }

            _proxyBridge = LocalProxyBridge.Start(config, detail =>
            {
                _log.Info($"[PROXY_BRIDGE] proxy={config.MaskedDisplay} {detail}");
                ProxyWorkerDiagnostics.Write(config, "BRIDGE", $"proxy={config.MaskedDisplay} {detail}");
            });
            var localProxy = _proxyBridge.LocalProxyUrl;
            _log.Info($"[PROXY_WORKER_ENABLED] proxy={config.MaskedDisplay} auth=yes transport=local_bridge local={localProxy}");
            ProxyWorkerDiagnostics.Write(config, "WORKER_ENABLED", $"proxy={config.MaskedDisplay} auth=yes transport=local_bridge local={localProxy}");
            return $"--proxy-server=\"{localProxy}\" --proxy-bypass-list=\"localhost;127.0.0.1\" ";
        }
        catch (Exception ex)
        {
            StopProxyBridge();
            var reason = ShortProxyError(ex.Message);
            _log.Error($"[PROXY_LAUNCH_BLOCKED] proxy={config.MaskedDisplay} reason={reason} action=chrome_not_started");
            ProxyWorkerDiagnostics.Write(config, "LAUNCH_BLOCKED", $"proxy={config.MaskedDisplay} reason={reason} action=chrome_not_started");
            // Khi người dùng đã bật/gán Proxy, không rơi về IP thật nếu lớp auth không
            // khởi động được. Dừng trước khi mở Chrome để tránh popup/treo và tránh leak IP.
            throw new InvalidOperationException("Không khởi động được Proxy xác thực. Chrome chưa được mở. Chi tiết: " + reason, ex);
        }
    }

    void StopProxyBridge()
    {
        var bridge = Interlocked.Exchange(ref _proxyBridge, null);
        if (bridge is null) return;
        try { bridge.Dispose(); } catch { }
    }

    static string ShortProxyError(string? message)
    {
        var text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 200 ? text : text[..200];
    }
}
