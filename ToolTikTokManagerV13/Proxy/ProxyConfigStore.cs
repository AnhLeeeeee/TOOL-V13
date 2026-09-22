using System.Text;
using System.Text.Json;

namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyConfigStore
{
    readonly string _root;
    readonly string _statePath;
    static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true
    };

    public ProxyConfigStore(string baseDir)
    {
        _root = Path.Combine(Path.GetFullPath(baseDir), "proxy_data");
        _statePath = Path.Combine(_root, "proxy_state.json");
    }

    public string StatePath => _statePath;

    public ProxyState Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return Normalize(new ProxyState());
            var json = File.ReadAllText(_statePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return Normalize(new ProxyState());
            var state = JsonSerializer.Deserialize<ProxyState>(json, ReadJson) ?? new ProxyState();
            return Normalize(state);
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(_root);
                if (File.Exists(_statePath))
                {
                    var bad = Path.Combine(_root, $"proxy_state.bad_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                    File.Copy(_statePath, bad, overwrite: true);
                }
            }
            catch { }
            return Normalize(new ProxyState());
        }
    }

    public void Save(ProxyState state)
    {
        Directory.CreateDirectory(_root);
        state = Normalize(state);
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state, WriteJson);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(_statePath))
        {
            var backup = _statePath + ".bak";
            try { File.Copy(_statePath, backup, overwrite: true); } catch { }
            File.Move(temp, _statePath, overwrite: true);
        }
        else
        {
            File.Move(temp, _statePath);
        }
    }

    static ProxyState Normalize(ProxyState state)
    {
        state.Settings ??= new ProxySettings();
        state.Proxies ??= [];
        state.Assignments ??= [];
        state.Settings.AssignedProfileLimit = Math.Clamp(state.Settings.AssignedProfileLimit, 1, 9999);
        state.Settings.ProfilesPerProxy = Math.Clamp(state.Settings.ProfilesPerProxy, 1, 9999);
        state.Settings.FailureThreshold = Math.Clamp(state.Settings.FailureThreshold, 1, 10);
        state.Settings.QuarantineMinutes = Math.Clamp(state.Settings.QuarantineMinutes, 1, 24 * 60);

        var uniqueProxyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedProxies = new List<ProxyEndpoint>();
        foreach (var proxy in state.Proxies)
        {
            if (proxy is null || string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535) continue;
            if (proxy.AddedAtUtc == default)
                proxy.AddedAtUtc = state.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : state.UpdatedAtUtc;
            if (string.IsNullOrWhiteSpace(proxy.Id)) proxy.Id = Guid.NewGuid().ToString("N");
            if (!uniqueProxyIds.Add(proxy.Id)) proxy.Id = Guid.NewGuid().ToString("N");
            if (!uniqueKeys.Add(proxy.DedupKey)) continue;
            normalizedProxies.Add(proxy);
        }
        state.Proxies = normalizedProxies;

        var validProxyIds = state.Proxies.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(state.ActiveManagerProxyId)
            && !validProxyIds.Contains(state.ActiveManagerProxyId))
            state.ActiveManagerProxyId = "";

        state.Assignments = state.Assignments
            .Where(x => x is not null
                        && !string.IsNullOrWhiteSpace(x.ProfileName)
                        && validProxyIds.Contains(x.ProxyId))
            .GroupBy(x => x.ProfileName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.AssignedAtUtc).First())
            .ToList();
        return state;
    }
}
