using System.Text.Json;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const string ManagedRemotePolicyRuntimeFileName =
        "manager_remote_policy_runtime.json";

    bool CheckManagedLiveRuntimePolicy(
        string source,
        bool suppressDialogs)
    {
        // Chỉ áp dụng cho Worker do Manager quản lý.
        if (!_managedMode)
            return true;

        var path = Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            ManagedRemotePolicyRuntimeFileName);

        try
        {
            if (!File.Exists(path))
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LIVE_FAIL_OPEN] source={source} reason=file_missing");
                return true;
            }

            using var doc = JsonDocument.Parse(
                File.ReadAllText(path));

            var root = doc.RootElement;

            var revision =
                root.TryGetProperty("revision", out var revisionEl)
                && revisionEl.TryGetInt32(out var revisionValue)
                    ? Math.Max(0, revisionValue)
                    : 0;

            var enforcement =
                root.TryGetProperty("enforcementEnabled", out var enforcementEl)
                && enforcementEl.ValueKind == JsonValueKind.True;

            var adminBypass =
                root.TryGetProperty("adminBypass", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.True;

            var fresh =
                TryReadManagedPolicySeenAtUtc(
                    root,
                    out var policySeenAtUtc)
                && DateTime.UtcNow - policySeenAtUtc <= TimeSpan.FromMinutes(3)
                && policySeenAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(1);

            var mode = "allow";

            if (root.TryGetProperty("policy", out var policyEl)
                && policyEl.ValueKind == JsonValueKind.Object
                && policyEl.TryGetProperty("live", out var liveEl)
                && liveEl.ValueKind == JsonValueKind.String)
            {
                var rawMode =
                    (liveEl.GetString() ?? "")
                    .Trim()
                    .ToLowerInvariant();

                if (rawMode is "allow" or "monitor" or "block")
                    mode = rawMode;
            }

            var wouldBlock =
                !adminBypass
                && string.Equals(
                    mode,
                    "block",
                    StringComparison.OrdinalIgnoreCase);

            // 3C.7:
            // policy fresh + enforcement + block => chặn thật.
            // stale/missing/invalid => fail-open.
            var actuallyBlocked =
                fresh
                && enforcement
                && wouldBlock;

            var allowed =
                !actuallyBlocked;

            _log.Info(
                $"[REMOTE_POLICY_WORKER_LIVE_CHECK] source={source} " +
                $"revision={revision} mode={mode} wouldBlock={wouldBlock} " +
                $"enforcement={enforcement} adminBypass={adminBypass} " +
                $"fresh={fresh} allowed={allowed}");

            if (!fresh)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LIVE_FAIL_OPEN] source={source} " +
                    $"reason=shadow_stale revision={revision}");
            }

            if (!allowed)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LIVE_BLOCKED] source={source} " +
                    $"revision={revision} mode={mode}");

                if (!suppressDialogs)
                {
                    MessageBox.Show(
                        "Chức năng LIVE đang bị tắt trên QITool cho thiết bị này.",
                        "QITool — LIVE bị khóa",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            return allowed;
        }
        catch (Exception ex)
        {
            // Không được làm Worker lỗi chỉ vì shadow file hỏng/đang thay.
            _log.Warn(
                $"[REMOTE_POLICY_WORKER_LIVE_FAIL_OPEN] source={source} " +
                $"reason=parse_error detail={ShortText(ex.Message, 180)}");

            return true;
        }
    }

    bool CheckManagedLoginRuntimePolicy(
        string source,
        bool suppressDialogs)
    {
        // Chỉ áp dụng cho Worker do Manager quản lý.
        if (!_managedMode)
            return true;

        var path = Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            ManagedRemotePolicyRuntimeFileName);

        try
        {
            if (!File.Exists(path))
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LOGIN_FAIL_OPEN] source={source} reason=file_missing");
                return true;
            }

            using var doc = JsonDocument.Parse(
                File.ReadAllText(path));

            var root = doc.RootElement;

            var revision =
                root.TryGetProperty("revision", out var revisionEl)
                && revisionEl.TryGetInt32(out var revisionValue)
                    ? Math.Max(0, revisionValue)
                    : 0;

            var enforcement =
                root.TryGetProperty("enforcementEnabled", out var enforcementEl)
                && enforcementEl.ValueKind == JsonValueKind.True;

            var adminBypass =
                root.TryGetProperty("adminBypass", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.True;

            var fresh =
                TryReadManagedPolicySeenAtUtc(
                    root,
                    out var policySeenAtUtc)
                && DateTime.UtcNow - policySeenAtUtc <= TimeSpan.FromMinutes(3)
                && policySeenAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(1);

            var mode = "allow";

            if (root.TryGetProperty("policy", out var policyEl)
                && policyEl.ValueKind == JsonValueKind.Object
                && policyEl.TryGetProperty("login", out var loginEl)
                && loginEl.ValueKind == JsonValueKind.String)
            {
                var rawMode =
                    (loginEl.GetString() ?? "")
                    .Trim()
                    .ToLowerInvariant();

                if (rawMode is "allow" or "monitor" or "block")
                    mode = rawMode;
            }

            var wouldBlock =
                !adminBypass
                && string.Equals(
                    mode,
                    "block",
                    StringComparison.OrdinalIgnoreCase);

            var actuallyBlocked =
                fresh
                && enforcement
                && wouldBlock;

            var allowed = !actuallyBlocked;

            _log.Info(
                $"[REMOTE_POLICY_WORKER_LOGIN_CHECK] source={source} " +
                $"revision={revision} mode={mode} wouldBlock={wouldBlock} " +
                $"enforcement={enforcement} adminBypass={adminBypass} " +
                $"fresh={fresh} allowed={allowed}");

            if (!fresh)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LOGIN_FAIL_OPEN] source={source} " +
                    $"reason=shadow_stale revision={revision}");
            }

            if (!allowed)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LOGIN_BLOCKED] source={source} " +
                    $"revision={revision} mode={mode}");

                if (!suppressDialogs)
                {
                    MessageBox.Show(
                        "Chức năng đăng nhập TikTok đang bị tắt trên QITool cho thiết bị này.",
                        "QITool — LOGIN bị khóa",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            return allowed;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REMOTE_POLICY_WORKER_LOGIN_FAIL_OPEN] source={source} " +
                $"reason=parse_error detail={ShortText(ex.Message, 180)}");

            return true;
        }
    }


    bool CheckManagedRuntimePolicyFeature(
        string feature,
        string source,
        bool suppressDialogs)
    {
        if (!_managedMode)
            return true;

        var normalizedFeature =
            (feature ?? "")
            .Trim()
            .ToLowerInvariant();

        if (normalizedFeature.Length == 0)
            return true;

        var path = Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            ManagedRemotePolicyRuntimeFileName);

        try
        {
            if (!File.Exists(path))
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_FEATURE_FAIL_OPEN] feature={normalizedFeature} source={source} reason=file_missing");
                return true;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var revision =
                root.TryGetProperty("revision", out var revisionEl)
                && revisionEl.TryGetInt32(out var revisionValue)
                    ? Math.Max(0, revisionValue)
                    : 0;

            var enforcement =
                root.TryGetProperty("enforcementEnabled", out var enforcementEl)
                && enforcementEl.ValueKind == JsonValueKind.True;

            var adminBypass =
                root.TryGetProperty("adminBypass", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.True;

            var fresh =
                TryReadManagedPolicySeenAtUtc(
                    root,
                    out var policySeenAtUtc)
                && DateTime.UtcNow - policySeenAtUtc <= TimeSpan.FromMinutes(3)
                && policySeenAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(1);

            var mode = "allow";

            // build_identity shadow JSON dùng camelCase giống RemotePolicySnapshot.
            // Runtime feature ID phía code dùng snake_case, nên phải map chính xác.
            var policyJsonKey = normalizedFeature switch
            {
                "video_delete" => "videoDelete",
                "video_upload" => "videoUpload",
                "comment_send" => "commentSend",
                "comment_check" => "commentCheck",
                "ban_check" => "banCheck",
                "create_profile" => "createProfile",
                "auto_replace" => "autoReplace",
                "daily_replace_all" => "dailyReplaceAll",
                "start_worker" => "startWorker",
                "name_image" => "nameImage",
                "tool_access" => "toolAccess",
                _ => normalizedFeature
            };

            if (root.TryGetProperty("policy", out var policyEl)
                && policyEl.ValueKind == JsonValueKind.Object
                && policyEl.TryGetProperty(policyJsonKey, out var featureEl)
                && featureEl.ValueKind == JsonValueKind.String)
            {
                var raw =
                    (featureEl.GetString() ?? "")
                    .Trim()
                    .ToLowerInvariant();

                if (raw is "allow" or "monitor" or "block")
                    mode = raw;
            }

            var wouldBlock =
                !adminBypass
                && string.Equals(
                    mode,
                    "block",
                    StringComparison.OrdinalIgnoreCase);

            var actuallyBlocked =
                fresh
                && enforcement
                && wouldBlock;

            var allowed = !actuallyBlocked;

            _log.Info(
                $"[REMOTE_POLICY_WORKER_FEATURE_CHECK] feature={normalizedFeature} source={source} " +
                $"revision={revision} mode={mode} wouldBlock={wouldBlock} " +
                $"enforcement={enforcement} adminBypass={adminBypass} fresh={fresh} allowed={allowed}");

            if (!fresh)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_FEATURE_FAIL_OPEN] feature={normalizedFeature} source={source} " +
                    $"reason=shadow_stale revision={revision}");
            }

            if (!allowed)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_FEATURE_BLOCKED] feature={normalizedFeature} source={source} " +
                    $"revision={revision} mode={mode}");

                if (!suppressDialogs)
                {
                    MessageBox.Show(
                        $"Chức năng {normalizedFeature} đang bị tắt trên QITool cho thiết bị này.",
                        "QITool — chức năng bị khóa",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            return allowed;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REMOTE_POLICY_WORKER_FEATURE_FAIL_OPEN] feature={normalizedFeature} source={source} " +
                $"reason=parse_error detail={ShortText(ex.Message, 180)}");
            return true;
        }
    }


    bool CheckManagedRuntimePolicyLimit(
        string limitJsonKey,
        int requestedAdditional,
        string source,
        bool suppressDialogs)
    {
        if (!_managedMode)
            return true;

        var normalized =
            (limitJsonKey ?? "")
            .Trim();

        if (normalized.Length == 0)
            return true;

        var path = Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            ManagedRemotePolicyRuntimeFileName);

        try
        {
            if (!File.Exists(path))
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LIMIT_FAIL_OPEN] limit={normalized} source={source} reason=file_missing");
                return true;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var revision =
                root.TryGetProperty("revision", out var revisionEl)
                && revisionEl.TryGetInt32(out var revisionValue)
                    ? Math.Max(0, revisionValue)
                    : 0;

            var enforcement =
                root.TryGetProperty("enforcementEnabled", out var enforcementEl)
                && enforcementEl.ValueKind == JsonValueKind.True;

            var adminBypass =
                root.TryGetProperty("adminBypass", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.True;

            var fresh =
                TryReadManagedPolicySeenAtUtc(
                    root,
                    out var policySeenAtUtc)
                && DateTime.UtcNow - policySeenAtUtc <= TimeSpan.FromMinutes(3)
                && policySeenAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(1);

            int? max = null;
            if (root.TryGetProperty("policy", out var policyEl)
                && policyEl.ValueKind == JsonValueKind.Object
                && policyEl.TryGetProperty(normalized, out var maxEl)
                && maxEl.ValueKind == JsonValueKind.Number
                && maxEl.TryGetInt32(out var maxValue)
                && maxValue >= 0)
            {
                max = maxValue;
            }

            var activeProfiles = 0;
            if (root.TryGetProperty("runtime", out var runtimeEl)
                && runtimeEl.ValueKind == JsonValueKind.Object
                && runtimeEl.TryGetProperty("activeProfiles", out var activeEl)
                && activeEl.ValueKind == JsonValueKind.Number
                && activeEl.TryGetInt32(out var activeValue))
            {
                activeProfiles = Math.Max(0, activeValue);
            }

            var rawExceed =
                max.HasValue
                && activeProfiles + Math.Max(0, requestedAdditional) > max.Value;

            var wouldBlock =
                !adminBypass
                && rawExceed;

            var allowed =
                !(fresh && enforcement && wouldBlock);

            _log.Info(
                $"[REMOTE_POLICY_WORKER_LIMIT_CHECK] limit={normalized} source={source} " +
                $"revision={revision} max={(max.HasValue ? max.Value.ToString() : "none")} " +
                $"current={activeProfiles} additional={Math.Max(0, requestedAdditional)} " +
                $"wouldExceed={wouldBlock} enforcement={enforcement} adminBypass={adminBypass} " +
                $"fresh={fresh} allowed={allowed}");

            if (!fresh)
            {
                _log.Warn(
                    $"[REMOTE_POLICY_WORKER_LIMIT_FAIL_OPEN] limit={normalized} source={source} " +
                    $"reason=shadow_stale revision={revision}");
            }

            if (!allowed && !suppressDialogs)
            {
                MessageBox.Show(
                    "QITool policy đã đạt giới hạn số profile đang chạy.",
                    "QITool — giới hạn sử dụng",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return allowed;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REMOTE_POLICY_WORKER_LIMIT_FAIL_OPEN] limit={normalized} source={source} " +
                $"reason=parse_error detail={ShortText(ex.Message, 180)}");
            return true;
        }
    }


    static bool TryReadManagedPolicySeenAtUtc(
        JsonElement root,
        out DateTime seenAtUtc)
    {
        seenAtUtc = default;

        // Schema 3C.7 hardening: freshness phải dựa trên heartbeat policy thật,
        // không dựa trên thời điểm Manager vừa rewrite file để cập nhật counters.
        if (TryReadUtc(
                root,
                "policySeenAtUtc",
                out seenAtUtc))
        {
            return true;
        }

        // Backward compatibility với shadow schema cũ.
        return TryReadUtc(
            root,
            "updatedAtUtc",
            out seenAtUtc);
    }

    static bool TryReadUtc(
        JsonElement root,
        string propertyName,
        out DateTime value)
    {
        value = default;

        if (!root.TryGetProperty(
                propertyName,
                out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!DateTime.TryParse(
                element.GetString(),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

}
