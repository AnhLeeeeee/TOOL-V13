using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolTikTokManagerV13;

/// <summary>
/// Kết nối Manager với QITool License Server (Supabase Edge Functions).
///
/// Bản vá này chạy ở SHADOW MODE:
/// - register thiết bị khi Manager khởi động;
/// - heartbeat định kỳ khi Manager đang mở;
/// - chỉ ghi log trạng thái server, KHÔNG thay đổi quyền chạy hiện tại của Tool.
///
/// Nhờ vậy có thể kiểm tra dữ liệu/Online/Offline trên web trước khi bật khóa từ xa.
/// </summary>
internal sealed class LicenseServerClient : IDisposable
{
    const string ConfigFileName = "license_server.json";

    static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static readonly JsonSerializerOptions WriteJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly HttpClient _http;
    readonly LicenseServerConfig _config;
    readonly string _deviceId;
    readonly string _deviceHash;
    readonly string _version;
    readonly string _sessionId;

    LicenseServerClient(
        LicenseServerConfig config,
        string deviceId,
        string deviceHash,
        string version)
    {
        _config = config;
        _deviceId = deviceId;
        _deviceHash = deviceHash;
        _version = version;
        _sessionId = Guid.NewGuid().ToString("N");

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 3, 30))
        };

        // Publishable key là key dành cho client. Tuyệt đối không dùng sb_secret/service_role ở đây.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", config.PublishableKey);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.PublishableKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QITool-License-Client/1.0");
    }

    public string SessionId => _sessionId;

    public static LicenseServerClient? TryCreate(
        string baseDir,
        string deviceId,
        string deviceHash,
        string version)
    {
        try
        {
            var configPath = Path.Combine(baseDir, ConfigFileName);
            if (!File.Exists(configPath))
            {
                Log($"[LICENSE_SERVER_DISABLED] reason=config_missing path={configPath}");
                return null;
            }

            var config = JsonSerializer.Deserialize<LicenseServerConfig>(
                File.ReadAllText(configPath),
                ReadJson) ?? new LicenseServerConfig();

            if (!config.Enabled)
            {
                Log("[LICENSE_SERVER_DISABLED] reason=config_enabled_false");
                return null;
            }

            config.ProjectUrl = (config.ProjectUrl ?? "").Trim().TrimEnd('/');
            config.PublishableKey = (config.PublishableKey ?? "").Trim();

            if (LooksLikePlaceholder(config.ProjectUrl)
                || LooksLikePlaceholder(config.PublishableKey)
                || string.IsNullOrWhiteSpace(config.ProjectUrl)
                || string.IsNullOrWhiteSpace(config.PublishableKey))
            {
                Log("[LICENSE_SERVER_DISABLED] reason=config_incomplete action=fill_project_url_and_publishable_key");
                return null;
            }

            if (!Uri.TryCreate(config.ProjectUrl, UriKind.Absolute, out var projectUri)
                || !string.Equals(projectUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                Log("[LICENSE_SERVER_DISABLED] reason=invalid_project_url_https_required");
                return null;
            }

            if (string.IsNullOrWhiteSpace(deviceId)
                || deviceId.Equals("TT-UNKNOWN", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(deviceHash))
            {
                Log($"[LICENSE_SERVER_DISABLED] reason=device_identity_missing device={OneLine(deviceId)}");
                return null;
            }

            config.HeartbeatSeconds = Math.Clamp(config.HeartbeatSeconds, 30, 600);
            config.RequestTimeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 3, 30);

            Log(
                $"[LICENSE_SERVER_READY] mode=shadow device={OneLine(deviceId)} " +
                $"heartbeatSeconds={config.HeartbeatSeconds} projectHost={projectUri.Host}");

            return new LicenseServerClient(config, deviceId, deviceHash, version);
        }
        catch (Exception ex)
        {
            Log($"[LICENSE_SERVER_DISABLED] reason=config_error detail={OneLine(ex.Message)}");
            return null;
        }
    }

    public async Task<LicenseServerDecision> RegisterAsync(
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId = _deviceId,
            deviceHash = _deviceHash,
            machineName = Environment.MachineName,
            version = _version,
            sessionId = _sessionId
        };

        var result = await PostAsync(
            "device-register",
            payload,
            cancellationToken).ConfigureAwait(false);

        LogDecision("REGISTER", result);
        return result;
    }

    public async Task<LicenseServerDecision> HeartbeatAsync(
        int profileCount = 0,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId = _deviceId,
            deviceHash = _deviceHash,
            sessionId = _sessionId,
            version = _version,
            profileCount = Math.Max(0, profileCount)
        };

        var result = await PostAsync(
            "device-heartbeat",
            payload,
            cancellationToken).ConfigureAwait(false);

        LogDecision("HEARTBEAT", result);
        return result;
    }

    public async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // Gửi ngay 1 heartbeat khi Manager vừa mở để web lên Online nhanh.
        try
        {
            await HeartbeatAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log($"[LICENSE_SERVER_HEARTBEAT_ERROR] phase=initial detail={OneLine(ex.Message)}");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HeartbeatAsync(0, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Mạng/Supabase lỗi không được làm Manager chết trong shadow mode.
                    Log($"[LICENSE_SERVER_HEARTBEAT_ERROR] detail={OneLine(ex.Message)}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    async Task<LicenseServerDecision> PostAsync(
        string functionName,
        object payload,
        CancellationToken cancellationToken)
    {
        var url = $"{_config.ProjectUrl}/functions/v1/{functionName}";
        var json = JsonSerializer.Serialize(payload, WriteJson);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            var responseText = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            LicenseServerResponse? parsed = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(responseText))
                    parsed = JsonSerializer.Deserialize<LicenseServerResponse>(responseText, ReadJson);
            }
            catch
            {
                // Giữ raw response rút gọn ở Decision để chẩn đoán.
            }

            return new LicenseServerDecision(
                Reachable: true,
                HttpStatus: (int)response.StatusCode,
                Ok: parsed?.Ok ?? response.IsSuccessStatusCode,
                Allowed: parsed?.Allowed,
                Status: (parsed?.Status ?? "").Trim(),
                IsNew: parsed?.IsNew,
                Reason: (parsed?.Reason ?? parsed?.Error ?? "").Trim(),
                Raw: OneLine(responseText, 500));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LicenseServerDecision(
                Reachable: false,
                HttpStatus: 0,
                Ok: false,
                Allowed: null,
                Status: "unreachable",
                IsNew: null,
                Reason: ex.Message,
                Raw: "");
        }
    }

    static void LogDecision(string phase, LicenseServerDecision decision)
    {
        Log(
            $"[LICENSE_SERVER_{phase}] mode=shadow reachable={decision.Reachable} " +
            $"http={decision.HttpStatus} ok={decision.Ok} allowed={FormatBool(decision.Allowed)} " +
            $"status={OneLine(decision.Status)} isNew={FormatBool(decision.IsNew)} " +
            $"reason={OneLine(decision.Reason)}");
    }

    static string FormatBool(bool? value)
        => value is null ? "unknown" : value.Value ? "true" : "false";

    static bool LooksLikePlaceholder(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0
               || text.Contains("PASTE_", StringComparison.OrdinalIgnoreCase)
               || text.Contains("YOUR_PROJECT", StringComparison.OrdinalIgnoreCase)
               || text.Contains("xxxxxxxx", StringComparison.OrdinalIgnoreCase);
    }

    static string OneLine(string? value, int max = 300)
    {
        var text = (value ?? "")
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    static void Log(string message)
        => ManagerProcessDiagnostics.Append(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");

    public void Dispose() => _http.Dispose();

    sealed class LicenseServerConfig
    {
        public bool Enabled { get; set; } = true;
        public string ProjectUrl { get; set; } = "";
        public string PublishableKey { get; set; } = "";
        public int HeartbeatSeconds { get; set; } = 60;
        public int RequestTimeoutSeconds { get; set; } = 6;
    }

    sealed class LicenseServerResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("allowed")]
        public bool? Allowed { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("isNew")]
        public bool? IsNew { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

internal sealed record LicenseServerDecision(
    bool Reachable,
    int HttpStatus,
    bool Ok,
    bool? Allowed,
    string Status,
    bool? IsNew,
    string Reason,
    string Raw);
