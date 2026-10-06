using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolTikTokManagerV13;

/// <summary>
/// Kết nối Manager với QITool License Server (Supabase Edge Functions).
///
/// Kết nối QITool theo chế độ hybrid an toàn:
/// - register thiết bị khi Manager khởi động;
/// - heartbeat định kỳ khi Manager đang mở;
/// - remote device lock vẫn yêu cầu explicit status=blocked + allowed=false và xác nhận 2 lần;
/// - 3C.7: tool_access=block cũng yêu cầu heartbeat xác nhận lần 2 trước khi đóng Manager;
/// - lỗi mạng/xác nhận thất bại đều fail-open.
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
    readonly string _installId;
    readonly string _buildId;
    readonly string _exeSha256;
    readonly int _managerPid;
    readonly DateTime _processStartedAtUtc;
    RemotePolicySnapshot _remotePolicy = RemotePolicySnapshot.AllowAll;

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
        _installId = ShadowTelemetryIdentity.GetOrCreateInstallId();
        _buildId = ShadowTelemetryIdentity.TryGetBuildId();
        _exeSha256 = ShadowTelemetryIdentity.TryGetExecutableSha256();
        _managerPid = Environment.ProcessId;
        try
        {
            _processStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            _processStartedAtUtc = DateTime.UtcNow;
        }

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

    /// <summary>
    /// Policy mới nhất nhận từ device-heartbeat.
    /// 3C.7: RuntimeGate dùng snapshot này để enforcement trên máy thường.
    /// </summary>
    public RemotePolicySnapshot CurrentRemotePolicy
        => Volatile.Read(ref _remotePolicy);

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

    public async Task<LicenseServerDecision> CheckAdminAsync(
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId = _deviceId,
            deviceHash = _deviceHash,
            version = _version
        };

        var result = await PostAsync(
            "device-admin-check",
            payload,
            cancellationToken).ConfigureAwait(false);

        LogDecision("ADMIN_CHECK", result);
        return result;
    }

    public async Task<LicenseServerDecision> CheckUpdatePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId = _deviceId,
            deviceHash = _deviceHash,
            version = _version
        };

        var result = await PostAsync(
            "device-admin-check",
            payload,
            cancellationToken).ConfigureAwait(false);

        LogDecision("UPDATE_POLICY", result);
        return result;
    }

    public static bool IsExplicitUpdateBlocked(LicenseServerDecision? decision)
    {
        if (decision is null || !decision.Reachable || !decision.Ok)
            return false;

        // Chỉ chặn update khi QITool xác nhận ĐÚNG thiết bị (Device ID + fingerprint),
        // cờ updateBlocked=true và máy không phải ADMIN.
        // Mọi lỗi mạng/HTTP/response thiếu field/identity mismatch đều fail-open.
        return decision.IdentityMatched is true
               && decision.UpdateBlocked is true
               && decision.IsAdmin is not true;
    }

    public Task<LicenseServerDecision> HeartbeatAsync(
        int profileCount = 0,
        CancellationToken cancellationToken = default)
        => HeartbeatAsync(profileCount, null, cancellationToken);

    async Task<LicenseServerDecision> HeartbeatAsync(
        int profileCount,
        ShadowHeartbeatSnapshot? shadowSnapshot,
        CancellationToken cancellationToken)
    {
        var uptime = Math.Max(0L, (long)(DateTime.UtcNow - _processStartedAtUtc).TotalSeconds);
        var effectiveProfileCount = shadowSnapshot?.ProfileCount ?? Math.Max(0, profileCount);

        var payload = new
        {
            deviceId = _deviceId,
            deviceHash = _deviceHash,
            sessionId = _sessionId,
            version = _version,
            profileCount = Math.Max(0, effectiveProfileCount),

            // Shadow telemetry: server accepts these as optional, so old/new
            // clients remain compatible during observation rollout.
            installId = _installId,
            buildId = _buildId,
            exeSha256 = _exeSha256,
            managerPid = _managerPid,
            processStartedAt = _processStartedAtUtc.ToString("O"),
            toolUptimeSeconds = uptime,
            telemetry = shadowSnapshot?.Telemetry,

            // 3C.4: chỉ báo server biết revision client hiện đang giữ trong RAM.
            // Server hiện có thể bỏ qua field này; tuyệt đối chưa dùng để enforcement.
            policyRevisionSeen = CurrentRemotePolicy.Revision
        };

        var result = await PostAsync(
            "device-heartbeat",
            payload,
            cancellationToken).ConfigureAwait(false);

        ObserveRemotePolicy(result);
        LogDecision("HEARTBEAT", result);
        return result;
    }

    void ObserveRemotePolicy(LicenseServerDecision decision)
    {
        var next = decision.Policy;
        if (next is null)
            return;

        var previous = Volatile.Read(ref _remotePolicy);

        // Revision phải đơn điệu tăng.
        // Không cho response cũ/replay hạ policy đang giữ trong RAM.
        if (previous.Revision > 0
            && next.Revision < previous.Revision)
        {
            Log(
                $"[REMOTE_POLICY_REVISION_REGRESSION_IGNORED] " +
                $"currentRevision={previous.Revision} receivedRevision={next.Revision}");
            return;
        }

        // Cùng revision nhưng payload khác là trạng thái không nhất quán.
        // Không nhận payload đó và cũng KHÔNG gia hạn freshness cho policy hiện tại.
        if (previous.Revision > 0
            && next.Revision == previous.Revision
            && previous != next)
        {
            Log(
                $"[REMOTE_POLICY_SAME_REVISION_CONFLICT_IGNORED] revision={next.Revision}");
            return;
        }

        Volatile.Write(ref _remotePolicy, next);

        // Chỉ đúng heartbeat có policy hợp lệ mới refresh policySeenAtUtc.
        // Runtime/UI counter không được tự làm policy cũ trở thành fresh.
        RemotePolicyRuntimeGate.MarkPolicyHeartbeat(next);

        // Không spam log mỗi heartbeat. Chỉ log khi revision hoặc nội dung policy đổi.
        if (previous == next)
            return;

        Log(
            $"[REMOTE_POLICY_SHADOW_RECEIVED] revision={next.Revision} " +
            $"toolAccess={next.ToolAccess} live={next.Live} createProfile={next.CreateProfile} " +
            $"autoReplace={next.AutoReplace} dailyReplaceAll={next.DailyReplaceAll} " +
            $"videoDelete={next.VideoDelete} videoUpload={next.VideoUpload} " +
            $"commentCheck={next.CommentCheck} banCheck={next.BanCheck} proxy={next.Proxy} " +
            $"maxWorkers={RemotePolicySnapshot.FormatLimit(next.MaxWorkers)} " +
            $"maxRunningProfiles={RemotePolicySnapshot.FormatLimit(next.MaxRunningProfiles)} " +
            $"maxCreatePerHour={RemotePolicySnapshot.FormatLimit(next.MaxCreatePerHour)} " +
            $"enforcement={RemotePolicyRuntimeGate.EnforcementEnabled} " +
            $"adminBypass={RemotePolicyRuntimeGate.AdminBypass} " +
            "action=runtime_policy_loaded");

        // Quan sát/log các policy cấp toàn cục.
        // tool_access sẽ được xác nhận 2 lần trong heartbeat loop trước khi đóng Manager.
        RemotePolicyRuntimeGate.Observe("tool_access");
        RemotePolicyRuntimeGate.Observe("update");

        // Giữ self-test LIVE đã có.
        RemotePolicyRuntimeGate.Observe("live");
    }

    public static bool IsExplicitBlocked(LicenseServerDecision? decision)
    {
        if (decision is null || !decision.Reachable)
            return false;

        // Fail-open tuyệt đối với mọi trạng thái khác. Chỉ coi là khóa khi server
        // trả về ĐỒNG THỜI status=blocked và allowed=false.
        // pending/expired/unreachable/http lỗi/response thiếu field KHÔNG được khóa Tool.
        return decision.Allowed is false
               && string.Equals(
                   (decision.Status ?? string.Empty).Trim(),
                   "blocked",
                   StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> ConfirmExplicitBlockAsync(
        LicenseServerDecision firstDecision,
        CancellationToken cancellationToken)
    {
        if (!IsExplicitBlocked(firstDecision))
            return false;

        Log(
            $"[LICENSE_SERVER_BLOCK_SIGNAL] phase=first status={OneLine(firstDecision.Status)} " +
            $"allowed={FormatBool(firstDecision.Allowed)} action=confirm_again_before_lock");

        // Xác nhận lại bằng request độc lập để tránh một response tạm/stale làm khóa nhầm.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken)
                .ConfigureAwait(false);

            var secondDecision = await HeartbeatAsync(0, cancellationToken)
                .ConfigureAwait(false);

            var confirmed = IsExplicitBlocked(secondDecision);
            Log(
                $"[LICENSE_SERVER_BLOCK_CONFIRM] confirmed={confirmed} " +
                $"status={OneLine(secondDecision.Status)} allowed={FormatBool(secondDecision.Allowed)} " +
                $"reachable={secondDecision.Reachable} http={secondDecision.HttpStatus}");

            return confirmed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Không xác nhận được lần 2 => KHÔNG khóa.
            Log($"[LICENSE_SERVER_BLOCK_CONFIRM_FAIL_OPEN] detail={OneLine(ex.Message)}");
            return false;
        }
    }

    async Task<LicenseServerDecision?> ConfirmPolicyToolAccessBlockAsync(
        LicenseServerDecision firstDecision,
        ShadowHeartbeatSnapshot? shadowSnapshot,
        CancellationToken cancellationToken)
    {
        // Chỉ bắt đầu quy trình đóng Tool khi CHÍNH heartbeat vừa nhận
        // mang một policy hợp lệ tool_access=block.
        if (!firstDecision.Reachable
            || !firstDecision.Ok
            || firstDecision.Policy is null
            || !string.Equals(
                firstDecision.Policy.ToolAccess,
                "block",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (RemotePolicyRuntimeGate.IsAllowed(
                "tool_access",
                out var firstPolicyDecision))
        {
            return null;
        }

        Log(
            $"[REMOTE_POLICY_TOOL_ACCESS_BLOCK_SIGNAL] phase=first " +
            $"revision={firstPolicyDecision.Revision} mode={firstPolicyDecision.Mode} " +
            $"enforcement={firstPolicyDecision.EnforcementEnabled} action=confirm_again_before_close");

        try
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(1200),
                    cancellationToken)
                .ConfigureAwait(false);

            var secondDecision = await HeartbeatAsync(
                    shadowSnapshot?.ProfileCount ?? 0,
                    shadowSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            var secondHasExplicitBlock =
                secondDecision.Reachable
                && secondDecision.Ok
                && secondDecision.Policy is not null
                && secondDecision.Policy.Revision >= firstDecision.Policy.Revision
                && string.Equals(
                    secondDecision.Policy.ToolAccess,
                    "block",
                    StringComparison.OrdinalIgnoreCase);

            var runtimeStillBlocked =
                !RemotePolicyRuntimeGate.IsAllowed(
                    "tool_access",
                    out var secondPolicyDecision);

            var confirmed =
                secondHasExplicitBlock
                && runtimeStillBlocked;

            Log(
                $"[REMOTE_POLICY_TOOL_ACCESS_BLOCK_CONFIRM] confirmed={confirmed} " +
                $"explicitSecondBlock={secondHasExplicitBlock} " +
                $"firstRevision={firstDecision.Policy.Revision} " +
                $"secondRevision={(secondDecision.Policy is null ? "none" : secondDecision.Policy.Revision.ToString())} " +
                $"runtimeRevision={secondPolicyDecision.Revision} mode={secondPolicyDecision.Mode} " +
                $"enforcement={secondPolicyDecision.EnforcementEnabled}");

            if (!confirmed)
                return null;

            return secondDecision with
            {
                Allowed = false,
                Status = "policy_tool_access_blocked",
                Reason = "Remote Policy tool_access=block đã được xác nhận 2 lần."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Không xác nhận được lần 2 => fail-open, không đóng Tool.
            Log(
                $"[REMOTE_POLICY_TOOL_ACCESS_BLOCK_CONFIRM_FAIL_OPEN] " +
                $"detail={OneLine(ex.Message)}");
            return null;
        }
    }

    public async Task RunHeartbeatLoopAsync(
        CancellationToken cancellationToken,
        Func<LicenseServerDecision, Task>? onConfirmedExplicitBlock = null,
        Func<ShadowHeartbeatSnapshot?>? shadowSnapshotProvider = null)
    {
        async Task<bool> HandleHeartbeatDecisionAsync(
            LicenseServerDecision decision,
            ShadowHeartbeatSnapshot? shadowSnapshot)
        {
            if (onConfirmedExplicitBlock is null)
                return false;

            // Lớp khóa thiết bị cũ: chỉ status=blocked + allowed=false, xác nhận 2 lần.
            if (IsExplicitBlocked(decision))
            {
                if (!await ConfirmExplicitBlockAsync(
                        decision,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return false;
                }

                Log(
                    $"[LICENSE_SERVER_REMOTE_LOCK_CONFIRMED] status={OneLine(decision.Status)} " +
                    $"allowed={FormatBool(decision.Allowed)} action=notify_manager");

                await onConfirmedExplicitBlock(decision).ConfigureAwait(false);
                return true;
            }

            // 3C.7: tool_access=block cũng đóng Manager, nhưng vẫn xác nhận heartbeat lần 2.
            // ADMIN không vào đây vì RuntimeGate.AdminBypass làm IsAllowed() luôn true.
            var policyBlock = await ConfirmPolicyToolAccessBlockAsync(
                    decision,
                    shadowSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            if (policyBlock is null)
                return false;

            Log(
                $"[REMOTE_POLICY_TOOL_ACCESS_RUNTIME_CONFIRMED] " +
                $"status={OneLine(policyBlock.Status)} action=notify_manager_close");

            await onConfirmedExplicitBlock(policyBlock).ConfigureAwait(false);
            return true;
        }

        // Gửi ngay 1 heartbeat khi Manager vừa mở để web lên Online nhanh.
        try
        {
            ShadowHeartbeatSnapshot? initialShadow = null;
            try { initialShadow = shadowSnapshotProvider?.Invoke(); } catch { }
            var initial = await HeartbeatAsync(initialShadow?.ProfileCount ?? 0, initialShadow, cancellationToken).ConfigureAwait(false);
            if (await HandleHeartbeatDecisionAsync(
                    initial,
                    initialShadow)
                .ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Mạng/Supabase lỗi không được làm Manager chết.
            Log($"[LICENSE_SERVER_HEARTBEAT_ERROR] phase=initial detail={OneLine(ex.Message)}");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    ShadowHeartbeatSnapshot? shadow = null;
                    try { shadow = shadowSnapshotProvider?.Invoke(); } catch { }
                    var decision = await HeartbeatAsync(shadow?.ProfileCount ?? 0, shadow, cancellationToken).ConfigureAwait(false);
                    if (await HandleHeartbeatDecisionAsync(
                            decision,
                            shadow)
                        .ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Mạng/Supabase lỗi không được làm Manager chết.
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

            var remotePolicy = RemotePolicySnapshot.TryParse(
                parsed?.PolicyRevision,
                parsed?.Policy);

            return new LicenseServerDecision(
                Reachable: true,
                HttpStatus: (int)response.StatusCode,
                Ok: parsed?.Ok ?? response.IsSuccessStatusCode,
                Allowed: parsed?.Allowed,
                Status: (parsed?.Status ?? "").Trim(),
                IsNew: parsed?.IsNew,
                IsAdmin: parsed?.IsAdmin,
                IdentityMatched: parsed?.IdentityMatched,
                UpdateBlocked: parsed?.UpdateBlocked,
                Reason: (parsed?.Reason ?? parsed?.Error ?? "").Trim(),
                Raw: OneLine(responseText, 500),
                PolicyRevision: remotePolicy?.Revision,
                Policy: remotePolicy);
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
                IsAdmin: null,
                IdentityMatched: null,
                UpdateBlocked: null,
                Reason: ex.Message,
                Raw: "",
                PolicyRevision: null,
                Policy: null);
        }
    }

    static void LogDecision(string phase, LicenseServerDecision decision)
    {
        Log(
            $"[LICENSE_SERVER_{phase}] mode=hybrid_safe_lock reachable={decision.Reachable} " +
            $"http={decision.HttpStatus} ok={decision.Ok} allowed={FormatBool(decision.Allowed)} " +
            $"status={OneLine(decision.Status)} isNew={FormatBool(decision.IsNew)} " +
            $"isAdmin={FormatBool(decision.IsAdmin)} identityMatched={FormatBool(decision.IdentityMatched)} " +
            $"updateBlocked={FormatBool(decision.UpdateBlocked)} " +
            $"policyRevision={(decision.PolicyRevision is null ? "none" : decision.PolicyRevision.Value.ToString())} " +
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

        [JsonPropertyName("isAdmin")]
        public bool? IsAdmin { get; set; }

        [JsonPropertyName("identityMatched")]
        public bool? IdentityMatched { get; set; }

        [JsonPropertyName("updateBlocked")]
        public bool? UpdateBlocked { get; set; }

        [JsonPropertyName("policyRevision")]
        public int? PolicyRevision { get; set; }

        [JsonPropertyName("policy")]
        public JsonElement? Policy { get; set; }

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
    bool? IsAdmin,
    bool? IdentityMatched,
    bool? UpdateBlocked,
    string Reason,
    string Raw,
    int? PolicyRevision = null,
    RemotePolicySnapshot? Policy = null);
