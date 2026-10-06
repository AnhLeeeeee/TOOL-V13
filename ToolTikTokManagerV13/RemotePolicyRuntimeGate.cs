using System.Collections.Concurrent;
using System.Text.Json;

namespace ToolTikTokManagerV13;

/// <summary>
/// Runtime Gate cho Remote Policy.
///
/// 3C.7:
/// - máy ADMIN: bypass toàn bộ enforcement;
/// - máy thường: ALLOW/MONITOR tiếp tục chạy, BLOCK bị chặn thật;
/// - các giới hạn max_* bị chặn thật khi vượt ngưỡng.
/// </summary>
internal static class RemotePolicyRuntimeGate
{
    static Func<RemotePolicySnapshot>? _policyProvider;

    static int _adminBypass;

    static int _enforcementEnabled;

    // Chỉ heartbeat có policy hợp lệ mới được gia hạn freshness.
    // Các refresh UI/runtime counter KHÔNG được làm policy cũ "tươi" lại.
    static long _lastPolicyHeartbeatUtcTicks;

    static readonly TimeSpan PolicyFreshWindow =
        TimeSpan.FromMinutes(3);

    static readonly TimeSpan PolicyFutureTolerance =
        TimeSpan.FromMinutes(1);

    // Chống spam log cùng một feature/mode liên tục.
    static readonly ConcurrentDictionary<string, string> _lastLoggedState =
        new(StringComparer.OrdinalIgnoreCase);

    static readonly ConcurrentDictionary<string, string> _lastLoggedLimitState =
        new(StringComparer.OrdinalIgnoreCase);

    static int _shadowWorkerAlive;
    static int _shadowActiveProfiles;
    static int _shadowCreatesLastHour;

    const string WorkerShadowFileName = "manager_remote_policy_runtime.json";
    static readonly object _workerShadowFileLock = new();
    static string _lastWorkerShadowState = "";

    public static bool EnforcementEnabled
        => Volatile.Read(ref _enforcementEnabled) == 1;

    public static bool AdminBypass
        => Volatile.Read(ref _adminBypass) == 1;

    public static void Bind(
        Func<RemotePolicySnapshot> policyProvider,
        bool adminBypass)
    {
        _policyProvider = policyProvider
            ?? throw new ArgumentNullException(nameof(policyProvider));

        Volatile.Write(
            ref _adminBypass,
            adminBypass ? 1 : 0);

        Interlocked.Exchange(
            ref _lastPolicyHeartbeatUtcTicks,
            0L);

        // 3C.7 - bật enforcement thật cho MÁY THƯỜNG.
        //
        // ADMIN vẫn full bypass để máy quản trị không tự khóa khi test policy.
        // Với máy thường:
        // - mode=allow / monitor => không chặn;
        // - mode=block         => chặn thật tại các RuntimeGate đã nối ở 3C.6;
        // - max_* có giá trị   => chặn thật khi vượt giới hạn.
        //
        // Không cần thêm một "master flag" phía server: chính policy BLOCK/limit
        // của từng thiết bị là công tắc có chủ đích.
        var enforcementEnabled = !adminBypass;

        Volatile.Write(
            ref _enforcementEnabled,
            enforcementEnabled ? 1 : 0);

        Log(
            $"[REMOTE_POLICY_GATE_BOUND] " +
            $"adminBypass={adminBypass} " +
            $"enforcement={enforcementEnabled} " +
            "stage=3C.7");

        // Publish trạng thái ban đầu; heartbeat kế tiếp sẽ refresh policy thật.
        PublishWorkerShadow(CurrentPolicy);
    }

    internal static void MarkPolicyHeartbeat(
        RemotePolicySnapshot policy)
    {
        Interlocked.Exchange(
            ref _lastPolicyHeartbeatUtcTicks,
            DateTime.UtcNow.Ticks);

        PublishWorkerShadow(policy);
    }

    static DateTime? LastPolicyHeartbeatUtc
    {
        get
        {
            var ticks =
                Interlocked.Read(
                    ref _lastPolicyHeartbeatUtcTicks);

            return ticks > 0
                ? new DateTime(
                    ticks,
                    DateTimeKind.Utc)
                : null;
        }
    }

    static bool IsPolicyFresh(
        out double ageSeconds)
    {
        var seenAt = LastPolicyHeartbeatUtc;

        if (!seenAt.HasValue)
        {
            ageSeconds = double.PositiveInfinity;
            return false;
        }

        var age =
            DateTime.UtcNow - seenAt.Value;

        ageSeconds = age.TotalSeconds;

        return age <= PolicyFreshWindow
               && age >= -PolicyFutureTolerance;
    }

    public static RemotePolicySnapshot CurrentPolicy
    {
        get
        {
            try
            {
                return _policyProvider?.Invoke()
                       ?? RemotePolicySnapshot.AllowAll;
            }
            catch (Exception ex)
            {
                // Fail-open.
                Log(
                    $"[REMOTE_POLICY_GATE_PROVIDER_FAIL_OPEN] " +
                    $"detail={OneLine(ex.Message)}");

                return RemotePolicySnapshot.AllowAll;
            }
        }
    }

    /// <summary>
    /// Chỉ đánh giá mode hiện tại.
    /// Không chặn chức năng.
    /// </summary>
    public static RuntimePolicyDecision Observe(
        string feature)
    {
        var policy = CurrentPolicy;

        var mode = GetMode(
            policy,
            feature);

        var wouldBlock =
            !AdminBypass &&
            string.Equals(
                mode,
                "block",
                StringComparison.OrdinalIgnoreCase);

        LogStateChangeOnly(
            feature,
            policy.Revision,
            mode,
            wouldBlock);

        return new RuntimePolicyDecision(
            Feature: feature,
            Revision: policy.Revision,
            Mode: mode,
            WouldBlock: wouldBlock,
            EnforcementEnabled: EnforcementEnabled,
            Allowed: true);
    }

    /// <summary>
    /// API enforcement dùng chung cho các module runtime.
    /// </summary>
    public static bool IsAllowed(
        string feature,
        out RuntimePolicyDecision decision)
    {
        var policy = CurrentPolicy;

        var mode = GetMode(
            policy,
            feature);

        var wouldBlock =
            !AdminBypass &&
            string.Equals(
                mode,
                "block",
                StringComparison.OrdinalIgnoreCase);

        var policyFresh =
            IsPolicyFresh(
                out var policyAgeSeconds);

        var actuallyBlocked =
            EnforcementEnabled &&
            wouldBlock &&
            policyFresh;

        if (EnforcementEnabled
            && wouldBlock
            && !policyFresh)
        {
            Log(
                $"[REMOTE_POLICY_GATE_STALE_FAIL_OPEN] feature={feature} " +
                $"revision={policy.Revision} mode={mode} " +
                $"policyAgeSeconds={(double.IsFinite(policyAgeSeconds) ? Math.Max(0, policyAgeSeconds).ToString("0") : "unknown")} " +
                "action=allow");
        }

        decision =
            new RuntimePolicyDecision(
                Feature: feature,
                Revision: policy.Revision,
                Mode: mode,
                WouldBlock: wouldBlock,
                EnforcementEnabled: EnforcementEnabled,
                Allowed: !actuallyBlocked);

        LogStateChangeOnly(
            feature,
            policy.Revision,
            mode,
            wouldBlock);

        return !actuallyBlocked;
    }

    public static void UpdateRuntimeCounters(
        int workerAlive,
        int activeProfiles,
        int createsLastHour)
    {
        Interlocked.Exchange(
            ref _shadowWorkerAlive,
            Math.Max(0, workerAlive));

        Interlocked.Exchange(
            ref _shadowActiveProfiles,
            Math.Max(0, activeProfiles));

        Interlocked.Exchange(
            ref _shadowCreatesLastHour,
            Math.Max(0, createsLastHour));

        // Refresh shadow file để Worker direct-start nhìn thấy count mới,
        // không phải chờ heartbeat 60 giây.
        PublishWorkerShadow(CurrentPolicy);
    }

    public static bool IsLimitAllowed(
        string limitName,
        int current,
        int requestedAdditional,
        out RuntimePolicyLimitDecision decision)
    {
        var policy = CurrentPolicy;
        var max = GetLimit(policy, limitName);

        var safeCurrent = Math.Max(0, current);
        var safeAdditional = Math.Max(0, requestedAdditional);

        var rawExceed =
            max.HasValue
            && safeCurrent + safeAdditional > max.Value;

        var wouldBlock =
            !AdminBypass
            && rawExceed;

        var policyFresh =
            IsPolicyFresh(
                out var policyAgeSeconds);

        var actuallyBlocked =
            EnforcementEnabled
            && wouldBlock
            && policyFresh;

        if (EnforcementEnabled
            && wouldBlock
            && !policyFresh)
        {
            Log(
                $"[REMOTE_POLICY_LIMIT_STALE_FAIL_OPEN] limit={limitName} " +
                $"revision={policy.Revision} " +
                $"policyAgeSeconds={(double.IsFinite(policyAgeSeconds) ? Math.Max(0, policyAgeSeconds).ToString("0") : "unknown")} " +
                "action=allow");
        }

        decision = new RuntimePolicyLimitDecision(
            Limit: limitName,
            Revision: policy.Revision,
            Max: max,
            Current: safeCurrent,
            RequestedAdditional: safeAdditional,
            WouldExceed: wouldBlock,
            EnforcementEnabled: EnforcementEnabled,
            Allowed: !actuallyBlocked);

        LogLimitStateChangeOnly(decision);

        return !actuallyBlocked;
    }

    /// <summary>
    /// Xuất policy runtime ra file dùng chung để Worker có thể kiểm tra
    /// cả trường hợp người dùng bấm Start/Resume trực tiếp trong Worker UI.
    ///
    /// File được ghi atomic.
    /// updatedAtUtc = thời điểm file được ghi lại.
    /// policySeenAtUtc = lần heartbeat gần nhất thực sự xác nhận policy từ server.
    /// Worker phải dùng policySeenAtUtc để quyết định freshness.
    /// </summary>
    internal static void PublishWorkerShadow(
        RemotePolicySnapshot policy)
    {
        try
        {
            var stateKey =
                $"{policy.Revision}|{EnforcementEnabled}|{AdminBypass}|" +
                $"{policy.ToolAccess}|{policy.StartWorker}|{policy.Live}|{policy.CommentSend}|" +
                $"{policy.CreateProfile}|{policy.AutoReplace}|{policy.DailyReplaceAll}|" +
                $"{policy.Login}|{policy.NameImage}|{policy.VideoDelete}|{policy.VideoUpload}|" +
                $"{policy.CommentCheck}|{policy.BanCheck}|{policy.Proxy}|{policy.MaxWorkers}|" +
                $"{policy.MaxRunningProfiles}|{policy.MaxCreatePerHour}|" +
                $"{Volatile.Read(ref _shadowWorkerAlive)}|{Volatile.Read(ref _shadowActiveProfiles)}|" +
                $"{Volatile.Read(ref _shadowCreatesLastHour)}";

            var payload = new
            {
                schemaVersion = 1,
                revision = policy.Revision,
                updatedAtUtc = DateTime.UtcNow,
                policySeenAtUtc = LastPolicyHeartbeatUtc,
                policyFreshWindowSeconds = (int)PolicyFreshWindow.TotalSeconds,
                enforcementEnabled = EnforcementEnabled,
                adminBypass = AdminBypass,
                policy = new
                {
                    toolAccess = policy.ToolAccess,
                    update = policy.Update,
                    startWorker = policy.StartWorker,
                    live = policy.Live,
                    commentSend = policy.CommentSend,
                    createProfile = policy.CreateProfile,
                    autoReplace = policy.AutoReplace,
                    dailyReplaceAll = policy.DailyReplaceAll,
                    login = policy.Login,
                    nameImage = policy.NameImage,
                    videoDelete = policy.VideoDelete,
                    videoUpload = policy.VideoUpload,
                    commentCheck = policy.CommentCheck,
                    banCheck = policy.BanCheck,
                    proxy = policy.Proxy,
                    maxWorkers = policy.MaxWorkers,
                    maxRunningProfiles = policy.MaxRunningProfiles,
                    maxCreatePerHour = policy.MaxCreatePerHour
                },
                runtime = new
                {
                    workerAlive = Volatile.Read(ref _shadowWorkerAlive),
                    activeProfiles = Volatile.Read(ref _shadowActiveProfiles),
                    createsLastHour = Volatile.Read(ref _shadowCreatesLastHour)
                }
            };

            var path = Path.Combine(
                Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                WorkerShadowFileName);

            var temp = path + ".tmp";

            lock (_workerShadowFileLock)
            {
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(payload));

                File.Move(
                    temp,
                    path,
                    overwrite: true);

                if (!string.Equals(
                        _lastWorkerShadowState,
                        stateKey,
                        StringComparison.Ordinal))
                {
                    _lastWorkerShadowState = stateKey;

                    Log(
                        $"[REMOTE_POLICY_WORKER_SHADOW_PUBLISHED] " +
                        $"revision={policy.Revision} live={policy.Live} " +
                        $"enforcement={EnforcementEnabled} adminBypass={AdminBypass}");
                }
            }
        }
        catch (Exception ex)
        {
            // Shadow file không được phép làm hỏng Manager.
            Log(
                $"[REMOTE_POLICY_WORKER_SHADOW_FAIL_OPEN] " +
                $"detail={OneLine(ex.Message)}");
        }
    }

    static string GetMode(
        RemotePolicySnapshot policy,
        string feature)
    {
        return feature.Trim().ToLowerInvariant() switch
        {
            "tool_access" =>
                policy.ToolAccess,

            "update" =>
                policy.Update,

            "start_worker" =>
                policy.StartWorker,

            "live" =>
                policy.Live,

            "comment_send" =>
                policy.CommentSend,

            "create_profile" =>
                policy.CreateProfile,

            "auto_replace" =>
                policy.AutoReplace,

            "daily_replace_all" =>
                policy.DailyReplaceAll,

            "login" =>
                policy.Login,

            "name_image" =>
                policy.NameImage,

            "video_delete" =>
                policy.VideoDelete,

            "video_upload" =>
                policy.VideoUpload,

            "comment_check" =>
                policy.CommentCheck,

            "ban_check" =>
                policy.BanCheck,

            "proxy" =>
                policy.Proxy,

            // Feature lạ => fail-open.
            _ => "allow"
        };
    }

    static void LogStateChangeOnly(
        string feature,
        int revision,
        string mode,
        bool wouldBlock)
    {
        var key =
            $"{revision}|{mode}|{wouldBlock}|{EnforcementEnabled}|{AdminBypass}";

        if (_lastLoggedState.TryGetValue(
                feature,
                out var previous)
            && string.Equals(
                previous,
                key,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedState[feature] =
            key;

        Log(
            $"[REMOTE_POLICY_GATE] " +
            $"feature={feature} " +
            $"revision={revision} " +
            $"mode={mode} " +
            $"wouldBlock={wouldBlock} " +
            $"enforcement={EnforcementEnabled} " +
            $"adminBypass={AdminBypass} " +
            "action=observe_only");
    }

    static int? GetLimit(
        RemotePolicySnapshot policy,
        string limitName)
    {
        return limitName.Trim().ToLowerInvariant() switch
        {
            "max_workers" =>
                policy.MaxWorkers,

            "max_running_profiles" =>
                policy.MaxRunningProfiles,

            "max_create_per_hour" =>
                policy.MaxCreatePerHour,

            _ => null
        };
    }

    static void LogLimitStateChangeOnly(
        RuntimePolicyLimitDecision decision)
    {
        var key =
            $"{decision.Revision}|{decision.Max}|{decision.Current}|" +
            $"{decision.RequestedAdditional}|{decision.WouldExceed}|" +
            $"{decision.EnforcementEnabled}|{AdminBypass}";

        if (_lastLoggedLimitState.TryGetValue(
                decision.Limit,
                out var previous)
            && string.Equals(
                previous,
                key,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedLimitState[decision.Limit] = key;

        Log(
            $"[REMOTE_POLICY_LIMIT] limit={decision.Limit} " +
            $"revision={decision.Revision} " +
            $"max={(decision.Max.HasValue ? decision.Max.Value.ToString() : "none")} " +
            $"current={decision.Current} " +
            $"additional={decision.RequestedAdditional} " +
            $"wouldExceed={decision.WouldExceed} " +
            $"enforcement={decision.EnforcementEnabled} " +
            $"allowed={decision.Allowed} " +
            $"adminBypass={AdminBypass} " +
            "action=runtime_gate");
    }

    static string OneLine(
        string? value,
        int max = 300)
    {
        var text =
            (value ?? string.Empty)
            .Replace(
                "\r",
                " ",
                StringComparison.Ordinal)
            .Replace(
                "\n",
                " ",
                StringComparison.Ordinal)
            .Trim();

        return text.Length <= max
            ? text
            : text[..max] + "...";
    }

    static void Log(
        string message)
    {
        ManagerProcessDiagnostics.Append(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }
}

internal sealed record RuntimePolicyDecision(
    string Feature,
    int Revision,
    string Mode,
    bool WouldBlock,
    bool EnforcementEnabled,
    bool Allowed);

internal sealed record RuntimePolicyLimitDecision(
    string Limit,
    int Revision,
    int? Max,
    int Current,
    int RequestedAdditional,
    bool WouldExceed,
    bool EnforcementEnabled,
    bool Allowed);
