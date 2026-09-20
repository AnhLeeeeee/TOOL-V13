using System.Text;
using System.Text.Json;
using ToolTikTokV12.Models;
using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyCoordinator
{
    public const string ProfileProxyFileName = ".tool_proxy.json";

    readonly object _stateSync = new();
    readonly SemaphoreSlim _operationGate = new(1, 1);
    readonly SemaphoreSlim _sharedProxyGate = new(1, 1);
    static readonly TimeSpan SharedProxyHealthFreshFor = TimeSpan.FromSeconds(30);
    readonly ProxyConfigStore _store;
    readonly ProxyTestService _tester = new();
    readonly ProxyAssignmentService _assigner = new();
    readonly Logger _log;
    ProxyState _state;

    static readonly JsonSerializerOptions ProfileWriteJson = new() { WriteIndented = true };

    public ProxyCoordinator(string baseDir, Logger log)
    {
        _store = new ProxyConfigStore(baseDir);
        _log = log;
        _state = _store.Load();
    }

    public ProxyState GetSnapshot()
    {
        lock (_stateSync)
        {
            // Clone để UI không sửa trực tiếp state đang dùng cho launch.
            var json = JsonSerializer.Serialize(_state);
            return JsonSerializer.Deserialize<ProxyState>(json) ?? new ProxyState();
        }
    }

    public void UpdateSettings(ProxySettings settings)
    {
        lock (_stateSync)
        {
            _state.Settings = NormalizeSettings(settings);
            SaveLocked();
        }
    }

    public (int Added, int Duplicate, int Invalid, List<string> Errors) ImportLines(string text, ProxyProtocol defaultProtocol)
    {
        var added = 0;
        var duplicate = 0;
        var invalid = 0;
        var errors = new List<string>();
        lock (_stateSync)
        {
            var existing = _state.Proxies.Select(x => x.DedupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lineNo = 0;
            foreach (var raw in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (!ProxyLineParser.TryParse(line, defaultProtocol, out var endpoint, out var error))
                {
                    invalid++;
                    if (errors.Count < 8) errors.Add($"Dòng {lineNo}: {error}");
                    continue;
                }
                if (!existing.Add(endpoint.DedupKey))
                {
                    duplicate++;
                    continue;
                }
                _state.Proxies.Add(endpoint);
                added++;
            }
            if (added > 0) SaveLocked();
        }
        return (added, duplicate, invalid, errors);
    }

    public int RemoveProxies(IEnumerable<string> proxyIds)
    {
        var ids = proxyIds.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return 0;
        lock (_stateSync)
        {
            var removed = _state.Proxies.RemoveAll(x => ids.Contains(x.Id));
            if (removed > 0)
            {
                _state.Assignments.RemoveAll(x => ids.Contains(x.ProxyId));
                if (ids.Contains(_state.ActiveManagerProxyId))
                    _state.ActiveManagerProxyId = "";
                SaveLocked();
            }
            return removed;
        }
    }

    public void ClearAssignments(IEnumerable<string>? profileNames = null)
    {
        lock (_stateSync)
        {
            if (profileNames is null)
            {
                _state.Assignments.Clear();
            }
            else
            {
                var names = profileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                _state.Assignments.RemoveAll(x => names.Contains(x.ProfileName));
            }
            SaveLocked();
        }
    }

    public async Task SetMasterEnabledAsync(bool enabled, IReadOnlyList<TikTokProfileEntry> profiles)
    {
        lock (_stateSync)
        {
            _state.Settings.Enabled = enabled;
            SaveLocked();
        }

        // Không restart Chrome. Chỉ ghi marker cho lần launch tiếp theo.
        if (!enabled)
        {
            foreach (var profile in profiles)
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
            _log.Info("[PROXY_MASTER_OFF] module=disabled action=direct_network_on_next_launch");
        }
        else
        {
            await ApplyAssignmentsToProfilesAsync(profiles);
            var mode = GetSnapshot().Settings.DistributionMode;
            _log.Info(mode == ProxyDistributionMode.ManagerShared
                ? "[PROXY_MASTER_ON] module=enabled mode=manager_shared action=use_one_proxy_for_all_profiles_on_next_launch"
                : "[PROXY_MASTER_ON] module=enabled mode=per_profile action=use_saved_assignments_on_next_launch");
        }
    }

    public async Task TestAllAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            List<ProxyEndpoint> endpoints;
            lock (_stateSync)
            {
                endpoints = _state.Proxies.Where(x => x.Enabled).Select(CloneEndpoint).ToList();
                foreach (var endpoint in _state.Proxies.Where(x => x.Enabled))
                    endpoint.Health = ProxyHealthState.Testing;
                SaveLocked();
            }

            var done = 0;
            using var limiter = new SemaphoreSlim(6, 6);
            var tasks = endpoints.Select(async endpoint =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    var result = await _tester.TestAsync(endpoint, cancellationToken);
                    ApplyTestResult(endpoint.Id, result);
                    var current = Interlocked.Increment(ref done);
                    progress?.Report($"Đã test {current}/{endpoints.Count}: {endpoint.MaskedDisplay} → {result.Health}");
                }
                finally
                {
                    limiter.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            ReconcileManagerSharedProxyAfterTests("test_all");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<int> TestAndRebalanceAsync(
        IReadOnlyList<TikTokProfileEntry> profiles,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await TestAllAsync(progress, cancellationToken);
        ProxySettings settings;
        lock (_stateSync) settings = CopySettings(_state.Settings);
        if (settings.DistributionMode == ProxyDistributionMode.ManagerShared)
        {
            var shared = await EnsureManagerSharedProxyAsync(cancellationToken, forceHealthCheck: false, reason: "test_rebalance");
            await ApplyAssignmentsToProfilesAsync(profiles);
            return shared is null ? 0 : profiles.Count(x => x.Enabled);
        }

        int assigned;
        lock (_stateSync)
        {
            if (_state.Settings.AutoReplaceBadProxy)
                _assigner.ReplaceBadAssignments(_state, profiles);
            assigned = _assigner.Rebalance(_state, profiles);
            SaveLocked();
        }
        await ApplyAssignmentsToProfilesAsync(profiles);
        return assigned;
    }

    public async Task<int> ReassignBadAsync(IReadOnlyList<TikTokProfileEntry> profiles)
    {
        await _operationGate.WaitAsync();
        try
        {
            ProxySettings settings;
            string beforeShared;
            lock (_stateSync)
            {
                settings = CopySettings(_state.Settings);
                beforeShared = _state.ActiveManagerProxyId;
            }

            if (settings.DistributionMode == ProxyDistributionMode.ManagerShared)
            {
                var endpoint = await EnsureManagerSharedProxyAsync(CancellationToken.None, forceHealthCheck: true, reason: "replace_bad");
                await ApplyAssignmentsToProfilesAsync(profiles);
                string afterShared;
                lock (_stateSync) afterShared = _state.ActiveManagerProxyId;
                return endpoint is not null && !afterShared.Equals(beforeShared, StringComparison.OrdinalIgnoreCase)
                    ? profiles.Count(x => x.Enabled)
                    : 0;
            }

            int count;
            lock (_stateSync)
            {
                count = _assigner.ReplaceBadAssignments(_state, profiles);
                SaveLocked();
            }
            await ApplyAssignmentsToProfilesAsync(profiles);
            return count;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ApplyAssignmentsToProfilesAsync(IReadOnlyList<TikTokProfileEntry> profiles)
    {
        await Task.Yield();
        ProxyState snapshot = GetSnapshot();
        ProxyEndpoint? sharedEndpoint = null;
        if (snapshot.Settings.Enabled && snapshot.Settings.DistributionMode == ProxyDistributionMode.ManagerShared)
        {
            sharedEndpoint = await EnsureManagerSharedProxyAsync(CancellationToken.None, forceHealthCheck: false, reason: "apply_profiles");
            snapshot = GetSnapshot();
        }

        foreach (var profile in profiles)
        {
            if (!snapshot.Settings.Enabled)
            {
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
                continue;
            }

            if (snapshot.Settings.DistributionMode == ProxyDistributionMode.ManagerShared)
            {
                if (sharedEndpoint is not null && sharedEndpoint.IsHealthy)
                    TryWriteProfileConfig(profile, sharedEndpoint, enabled: true, out _);
                else
                    TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
                continue;
            }

            var endpoint = Resolve(snapshot, profile.Name);
            if (endpoint is not null && endpoint.IsHealthy)
                TryWriteProfileConfig(profile, endpoint, enabled: true, out _);
            else
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
        }
    }

    public async Task<ProxyPrepareResult> PrepareForLaunchAsync(
        TikTokProfileEntry profile,
        IReadOnlyList<TikTokProfileEntry> allProfiles,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ProxySettings settings;
            lock (_stateSync) settings = CopySettings(_state.Settings);
            if (!settings.Enabled)
            {
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
                return new ProxyPrepareResult(false, false, profile.Name, "", "proxy_off");
            }

            ProxyEndpoint? endpoint;
            if (settings.DistributionMode == ProxyDistributionMode.ManagerShared)
            {
                endpoint = await EnsureManagerSharedProxyAsync(cancellationToken, forceHealthCheck: false, reason: $"launch:{profile.Name}");
            }
            else
            {
                bool hadAssignment;
                lock (_stateSync)
                {
                    var mapped = _assigner.Resolve(_state, profile.Name);
                    hadAssignment = mapped is not null;
                    endpoint = mapped is { IsHealthy: true } existing ? CloneEndpoint(existing) : null;
                }

                var mayAssign = hadAssignment ? settings.AutoReplaceBadProxy : settings.AutoAssignNewProfiles;
                if (endpoint is null && mayAssign)
                    endpoint = await EnsureAssignmentWithTestingAsync(profile, allProfiles, cancellationToken);
            }

            if (endpoint is null)
            {
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
                _log.Warn($"[PROXY_FALLBACK_DIRECT] profile={profile.Name} reason=no_healthy_or_available_proxy");
                return new ProxyPrepareResult(true, false, profile.Name, "", "no_healthy_or_available_proxy");
            }

            if (!TryWriteProfileConfig(profile, endpoint, enabled: true, out var writeError))
            {
                TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
                _log.Warn($"[PROXY_FALLBACK_DIRECT] profile={profile.Name} proxy={endpoint.MaskedDisplay} reason=config_write_failed detail={writeError}");
                return new ProxyPrepareResult(true, false, profile.Name, endpoint.MaskedDisplay, "config_write_failed");
            }

            var modeTag = settings.DistributionMode == ProxyDistributionMode.ManagerShared ? "manager_shared" : "per_profile";
            _log.Info($"[PROXY_PREPARED] profile={profile.Name} mode={modeTag} proxy={endpoint.MaskedDisplay} exitIp={endpoint.ExitIp} latencyMs={endpoint.LastLatencyMs}");
            return new ProxyPrepareResult(true, true, profile.Name, endpoint.MaskedDisplay, "ok");
        }
        catch (Exception ex)
        {
            // Fail-open: module Proxy không bao giờ được phép làm luồng Chrome cũ chết theo.
            TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);
            _log.Warn($"[PROXY_FAIL_OPEN] profile={profile.Name} action=direct_network detail={Short(ex.Message)}");
            return new ProxyPrepareResult(true, false, profile.Name, "", "module_error_fail_open");
        }
    }

    public bool TryForceDirectForProfile(TikTokProfileEntry profile)
        => TryWriteProfileConfig(profile, endpoint: null, enabled: false, out _);

    async Task<ProxyEndpoint?> EnsureAssignmentWithTestingAsync(
        TikTokProfileEntry profile,
        IReadOnlyList<TikTokProfileEntry> allProfiles,
        CancellationToken cancellationToken)
    {
        ProxySettings settingsSnapshot;
        lock (_stateSync) settingsSnapshot = CopySettings(_state.Settings);
        var allowed = _assigner.GetTargetProfiles(allProfiles, settingsSnapshot)
            .Any(x => x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (!allowed) return null;

        lock (_stateSync)
        {
            if (_assigner.EnsureAssignment(_state, profile, allProfiles) is { } immediate)
            {
                SaveLocked();
                return CloneEndpoint(immediate);
            }
        }

        List<ProxyEndpoint> candidates;
        lock (_stateSync)
        {
            var now = DateTimeOffset.UtcNow;
            candidates = _state.Proxies
                .Where(x => x.Enabled
                            && !x.IsHealthy
                            && (x.QuarantineUntilUtc is null || x.QuarantineUntilUtc <= now))
                .Select(CloneEndpoint)
                .ToList();
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _tester.TestAsync(candidate, cancellationToken);
            ApplyTestResult(candidate.Id, result);
            if (result.Health is not (ProxyHealthState.Good or ProxyHealthState.Slow)) continue;

            lock (_stateSync)
            {
                if (_assigner.EnsureAssignment(_state, profile, allProfiles) is { } assigned)
                {
                    SaveLocked();
                    return CloneEndpoint(assigned);
                }
            }
        }

        return null;
    }

    async Task<ProxyEndpoint?> EnsureManagerSharedProxyAsync(
        CancellationToken cancellationToken,
        bool forceHealthCheck,
        string reason)
    {
        await _sharedProxyGate.WaitAsync(cancellationToken);
        try
        {
            ProxySettings settings;
            string activeId;
            List<ProxyEndpoint> ordered;
            lock (_stateSync)
            {
                settings = CopySettings(_state.Settings);
                activeId = _state.ActiveManagerProxyId;
                ordered = _state.Proxies.Select(CloneEndpoint).ToList();
            }

            if (!settings.Enabled || settings.DistributionMode != ProxyDistributionMode.ManagerShared)
                return null;

            var now = DateTimeOffset.UtcNow;
            var activeIndex = ordered.FindIndex(x => x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
            if (activeIndex >= 0)
            {
                var active = ordered[activeIndex];
                var fresh = active.LastTestUtc is not null && now - active.LastTestUtc.Value <= SharedProxyHealthFreshFor;
                if (active.IsHealthy && !forceHealthCheck && fresh)
                    return active;

                if (active.Enabled && (active.QuarantineUntilUtc is null || active.QuarantineUntilUtc <= now))
                {
                    var test = await _tester.TestAsync(active, cancellationToken);
                    ApplyTestResult(active.Id, test);
                    var refreshed = GetProxyById(active.Id);
                    if (refreshed is { IsHealthy: true })
                        return refreshed;

                    _log.Warn($"[PROXY_MANAGER_SHARED_FAIL] proxy={active.MaskedDisplay} health={test.Health} reason={Short(test.Error)} action=next_proxy");
                }
            }

            var count = ordered.Count;
            if (count == 0)
            {
                ClearActiveManagerProxy("empty_pool");
                return null;
            }

            var start = activeIndex >= 0 ? activeIndex + 1 : 0;
            for (var offset = 0; offset < count; offset++)
            {
                var index = (start + offset) % count;
                var candidate = ordered[index];
                if (!candidate.Enabled) continue;
                if (candidate.QuarantineUntilUtc is not null && candidate.QuarantineUntilUtc > DateTimeOffset.UtcNow) continue;
                if (activeIndex >= 0 && candidate.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase)) continue;

                var fresh = candidate.LastTestUtc is not null
                            && DateTimeOffset.UtcNow - candidate.LastTestUtc.Value <= SharedProxyHealthFreshFor;
                ProxyEndpoint? healthy = candidate.IsHealthy && fresh ? candidate : null;
                if (healthy is null)
                {
                    var test = await _tester.TestAsync(candidate, cancellationToken);
                    ApplyTestResult(candidate.Id, test);
                    healthy = GetProxyById(candidate.Id);
                    if (healthy is not { IsHealthy: true })
                    {
                        _log.Warn($"[PROXY_MANAGER_SHARED_SKIP] proxy={candidate.MaskedDisplay} health={test.Health} reason={Short(test.Error)}");
                        continue;
                    }
                }

                SetActiveManagerProxy(healthy.Id, activeId, reason);
                return healthy;
            }

            ClearActiveManagerProxy("no_healthy_proxy");
            _log.Warn("[PROXY_MANAGER_SHARED_EMPTY] action=direct_network_on_next_launch reason=no_healthy_proxy_in_pool");
            return null;
        }
        finally
        {
            _sharedProxyGate.Release();
        }
    }

    void ReconcileManagerSharedProxyAfterTests(string reason)
    {
        string fromId = "";
        string toId = "";
        string fromDisplay = "—";
        string toDisplay = "—";
        lock (_stateSync)
        {
            if (!_state.Settings.Enabled || _state.Settings.DistributionMode != ProxyDistributionMode.ManagerShared)
                return;

            var currentIndex = _state.Proxies.FindIndex(x => x.Id.Equals(_state.ActiveManagerProxyId, StringComparison.OrdinalIgnoreCase));
            if (currentIndex >= 0 && _state.Proxies[currentIndex].IsHealthy)
                return;

            fromId = _state.ActiveManagerProxyId;
            if (currentIndex >= 0) fromDisplay = _state.Proxies[currentIndex].MaskedDisplay;
            var count = _state.Proxies.Count;
            var start = currentIndex >= 0 ? currentIndex + 1 : 0;
            for (var offset = 0; offset < count; offset++)
            {
                var index = (start + offset) % count;
                var candidate = _state.Proxies[index];
                if (!candidate.IsHealthy) continue;
                toId = candidate.Id;
                toDisplay = candidate.MaskedDisplay;
                break;
            }
            _state.ActiveManagerProxyId = toId;
            SaveLocked();
        }

        if (!fromId.Equals(toId, StringComparison.OrdinalIgnoreCase))
            _log.Warn($"[PROXY_MANAGER_SHARED_SWITCH] from={fromDisplay} to={toDisplay} reason={reason} action=next_launch_only");
    }

    ProxyEndpoint? GetProxyById(string proxyId)
    {
        lock (_stateSync)
        {
            var endpoint = _state.Proxies.FirstOrDefault(x => x.Id.Equals(proxyId, StringComparison.OrdinalIgnoreCase));
            return endpoint is null ? null : CloneEndpoint(endpoint);
        }
    }

    void SetActiveManagerProxy(string proxyId, string previousId, string reason)
    {
        string fromDisplay;
        string toDisplay;
        lock (_stateSync)
        {
            var from = _state.Proxies.FirstOrDefault(x => x.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase));
            var to = _state.Proxies.FirstOrDefault(x => x.Id.Equals(proxyId, StringComparison.OrdinalIgnoreCase));
            fromDisplay = from?.MaskedDisplay ?? "—";
            toDisplay = to?.MaskedDisplay ?? "—";
            _state.ActiveManagerProxyId = proxyId;
            SaveLocked();
        }
        if (!proxyId.Equals(previousId, StringComparison.OrdinalIgnoreCase))
            _log.Warn($"[PROXY_MANAGER_SHARED_SWITCH] from={fromDisplay} to={toDisplay} reason={reason} action=next_launch_only");
    }

    void ClearActiveManagerProxy(string reason)
    {
        lock (_stateSync)
        {
            if (string.IsNullOrWhiteSpace(_state.ActiveManagerProxyId)) return;
            _state.ActiveManagerProxyId = "";
            SaveLocked();
        }
        _log.Warn($"[PROXY_MANAGER_SHARED_CLEAR] reason={reason} action=direct_network_on_next_launch");
    }

    void ApplyTestResult(string proxyId, ProxyTestResult result)
    {
        lock (_stateSync)
        {
            var endpoint = _state.Proxies.FirstOrDefault(x => x.Id.Equals(proxyId, StringComparison.OrdinalIgnoreCase));
            if (endpoint is null) return;
            endpoint.Health = result.Health;
            endpoint.ExitIp = result.ExitIp;
            endpoint.LastLatencyMs = result.LatencyMs;
            endpoint.LastTestUtc = DateTimeOffset.UtcNow;
            endpoint.LastError = result.Error;
            if (result.Health is ProxyHealthState.Good or ProxyHealthState.Slow)
            {
                endpoint.ConsecutiveFailures = 0;
                endpoint.QuarantineUntilUtc = null;
            }
            else
            {
                endpoint.ConsecutiveFailures++;
                if (endpoint.ConsecutiveFailures >= Math.Max(1, _state.Settings.FailureThreshold))
                    endpoint.QuarantineUntilUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _state.Settings.QuarantineMinutes));
            }
            SaveLocked();
        }
    }

    bool TryWriteProfileConfig(TikTokProfileEntry profile, ProxyEndpoint? endpoint, bool enabled, out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(profile.ProfilePath) || !Directory.Exists(profile.ProfilePath))
            {
                error = "profile_path_missing";
                return false;
            }
            var path = Path.Combine(profile.ProfilePath, ProfileProxyFileName);
            var payload = enabled && endpoint is not null
                ? new WorkerProxyConfig
                {
                    Enabled = true,
                    Protocol = endpoint.Protocol.ToString(),
                    Host = endpoint.Host,
                    Port = endpoint.Port,
                    Username = endpoint.Username,
                    Password = endpoint.Password,
                    ProxyId = endpoint.Id
                }
                : new WorkerProxyConfig { Enabled = false };
            var json = JsonSerializer.Serialize(payload, ProfileWriteJson);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = Short(ex.Message);
            return false;
        }
    }

    void SaveLocked() => _store.Save(_state);

    static ProxySettings NormalizeSettings(ProxySettings source)
        => new()
        {
            Enabled = source.Enabled,
            DistributionMode = source.DistributionMode,
            LimitAssignedProfiles = source.LimitAssignedProfiles,
            AssignedProfileLimit = Math.Clamp(source.AssignedProfileLimit, 1, 9999),
            ProfilesPerProxy = Math.Clamp(source.ProfilesPerProxy, 1, 9999),
            AutoAssignNewProfiles = source.AutoAssignNewProfiles,
            AutoReplaceBadProxy = source.AutoReplaceBadProxy,
            FailureThreshold = Math.Clamp(source.FailureThreshold, 1, 10),
            QuarantineMinutes = Math.Clamp(source.QuarantineMinutes, 1, 24 * 60),
            DefaultProtocol = source.DefaultProtocol
        };

    static ProxySettings CopySettings(ProxySettings source) => NormalizeSettings(source);

    static ProxyEndpoint CloneEndpoint(ProxyEndpoint source)
        => new()
        {
            Id = source.Id,
            Protocol = source.Protocol,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            Enabled = source.Enabled,
            Health = source.Health,
            ExitIp = source.ExitIp,
            LastLatencyMs = source.LastLatencyMs,
            LastTestUtc = source.LastTestUtc,
            ConsecutiveFailures = source.ConsecutiveFailures,
            QuarantineUntilUtc = source.QuarantineUntilUtc,
            LastError = source.LastError
        };

    static ProxyEndpoint? Resolve(ProxyState snapshot, string profileName)
    {
        var assignment = snapshot.Assignments.FirstOrDefault(x => x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (assignment is null) return null;
        return snapshot.Proxies.FirstOrDefault(x => x.Id.Equals(assignment.ProxyId, StringComparison.OrdinalIgnoreCase));
    }

    static string Short(string? message)
    {
        var text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 220 ? text : text[..220];
    }

    sealed class WorkerProxyConfig
    {
        public bool Enabled { get; set; }
        public string Protocol { get; set; } = "Http";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string ProxyId { get; set; } = "";
    }
}
