using System.Net;
using System.Net.Sockets;
using ToolTikTokV12.Models;

namespace ToolTikTokV12.Services;

/// <summary>
/// V12.5 keeps an already-assigned unique CDP port stable even while Chrome is
/// listening on it. New/missing/duplicate assignments still receive a free port.
/// </summary>
public sealed class CdpPortAllocator
{
    public const int BasePort = 9222;

    public int FindAvailablePort(IEnumerable<int> reservedPorts)
    {
        var reserved = new HashSet<int>(reservedPorts.Where(port => port > 0));
        var port = BasePort;
        while (reserved.Contains(port) || !IsPortAvailable(port)) port++;
        return port;
    }

    public void NormalizeProfilePorts(List<TikTokProfileEntry> profiles, Action<string>? log = null)
    {
        var byPath = new Dictionary<string, TikTokProfileEntry>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<int>();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedPath = Path.GetFullPath(profile.ProfilePath);
            if (byPath.TryGetValue(normalizedPath, out var existing) && !existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"[PROFILE_PATH_CONFLICT] {existing.Name} va {profile.Name} dang dung chung ProfilePath={normalizedPath}");
            byPath[normalizedPath] = profile;

            if (profile.CdpPort <= 0 || reserved.Contains(profile.CdpPort))
            {
                var oldPort = profile.CdpPort;
                profile.CdpPort = FindAvailablePort(reserved);
                if (oldPort > 0 && oldPort != profile.CdpPort)
                    log?.Invoke($"[PROFILE_PORT_CONFLICT] name={profile.Name} oldPort={oldPort} newPort={profile.CdpPort}");
            }
            reserved.Add(profile.CdpPort);
        }
    }

    public bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }
}
