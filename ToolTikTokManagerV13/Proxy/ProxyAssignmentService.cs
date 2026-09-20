using ToolTikTokV12.Models;

namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyAssignmentService
{
    public IReadOnlyList<TikTokProfileEntry> GetTargetProfiles(
        IReadOnlyList<TikTokProfileEntry> profiles,
        ProxySettings settings)
    {
        var ordered = profiles
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, NaturalNameComparer.Instance)
            .ToList();
        if (settings.LimitAssignedProfiles)
            ordered = ordered.Take(Math.Max(1, settings.AssignedProfileLimit)).ToList();
        return ordered;
    }

    public int Rebalance(ProxyState state, IReadOnlyList<TikTokProfileEntry> profiles)
    {
        var targetProfiles = GetTargetProfiles(profiles, state.Settings);
        // Gán tự động là thao tác chủ động: dựng lại mapping từ catalog hiện tại để
        // không giữ "slot ma" của PRF đã bị xóa/đổi tên.
        state.Assignments.Clear();

        var usable = HealthyProxies(state);
        var perProxy = Math.Max(1, state.Settings.ProfilesPerProxy);
        var assigned = 0;
        for (var index = 0; index < targetProfiles.Count; index++)
        {
            var proxyIndex = index / perProxy;
            if (proxyIndex >= usable.Count) break;
            state.Assignments.Add(new ProxyAssignment
            {
                ProfileName = targetProfiles[index].Name,
                ProxyId = usable[proxyIndex].Id,
                AssignedAtUtc = DateTimeOffset.UtcNow
            });
            assigned++;
        }

        return assigned;
    }

    public ProxyEndpoint? EnsureAssignment(
        ProxyState state,
        TikTokProfileEntry profile,
        IReadOnlyList<TikTokProfileEntry> allProfiles)
    {
        var catalogNames = allProfiles
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.Assignments.RemoveAll(x => !catalogNames.Contains(x.ProfileName));

        var allowedNames = GetTargetProfiles(allProfiles, state.Settings)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowedNames.Contains(profile.Name))
        {
            state.Assignments.RemoveAll(x => x.ProfileName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        var existing = state.Assignments.FirstOrDefault(x => x.ProfileName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var endpoint = state.Proxies.FirstOrDefault(x => x.Id.Equals(existing.ProxyId, StringComparison.OrdinalIgnoreCase));
            if (endpoint is not null && endpoint.IsHealthy)
                return endpoint;
            state.Assignments.Remove(existing);
        }

        var usable = HealthyProxies(state);
        var perProxy = Math.Max(1, state.Settings.ProfilesPerProxy);
        foreach (var endpoint in usable)
        {
            var used = state.Assignments.Count(x => x.ProxyId.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase));
            if (used >= perProxy) continue;
            state.Assignments.Add(new ProxyAssignment
            {
                ProfileName = profile.Name,
                ProxyId = endpoint.Id,
                AssignedAtUtc = DateTimeOffset.UtcNow
            });
            return endpoint;
        }
        return null;
    }

    public int ReplaceBadAssignments(ProxyState state, IReadOnlyList<TikTokProfileEntry> allProfiles)
    {
        if (!state.Settings.AutoReplaceBadProxy) return 0;
        var profileByName = allProfiles.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var badIds = state.Proxies.Where(x => !x.IsHealthy).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = state.Assignments
            .Where(x => badIds.Contains(x.ProxyId) && profileByName.ContainsKey(x.ProfileName))
            .Select(x => x.ProfileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        state.Assignments.RemoveAll(x => badIds.Contains(x.ProxyId));
        var replaced = 0;
        foreach (var profileName in affected)
        {
            if (!profileByName.TryGetValue(profileName, out var profile)) continue;
            if (EnsureAssignment(state, profile, allProfiles) is not null) replaced++;
        }
        return replaced;
    }

    public ProxyEndpoint? Resolve(ProxyState state, string profileName)
    {
        var assignment = state.Assignments.FirstOrDefault(x => x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (assignment is null) return null;
        return state.Proxies.FirstOrDefault(x => x.Id.Equals(assignment.ProxyId, StringComparison.OrdinalIgnoreCase));
    }

    static List<ProxyEndpoint> HealthyProxies(ProxyState state)
        => state.Proxies.Where(x => x.IsHealthy).ToList();

    sealed class NaturalNameComparer : IComparer<string>
    {
        public static readonly NaturalNameComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    var i0 = i;
                    var j0 = j;
                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;
                    var a = left[i0..i].TrimStart('0');
                    var b = right[j0..j].TrimStart('0');
                    a = a.Length == 0 ? "0" : a;
                    b = b.Length == 0 ? "0" : b;
                    var len = a.Length.CompareTo(b.Length);
                    if (len != 0) return len;
                    var cmp = string.Compare(a, b, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    continue;
                }
                var charCmp = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (charCmp != 0) return charCmp;
                i++;
                j++;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
