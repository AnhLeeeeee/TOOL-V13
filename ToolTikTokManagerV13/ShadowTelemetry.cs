using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ToolTikTokManagerV13;

internal sealed record ShadowHeartbeatSnapshot(
    int ProfileCount,
    object Telemetry);

internal static class ShadowTelemetryIdentity
{
    static readonly object Sync = new();
    static string? _installId;
    static string? _exeSha256;
    static string? _buildId;

    public static string GetOrCreateInstallId()
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_installId)) return _installId;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ToolTikTok",
                "shadow_telemetry");
            var path = Path.Combine(root, "install_id.txt");

            try
            {
                Directory.CreateDirectory(root);
                if (File.Exists(path))
                {
                    var existing = (File.ReadAllText(path) ?? "").Trim();
                    if (Guid.TryParseExact(existing, "N", out _))
                        return _installId = existing.ToLowerInvariant();
                }

                var created = Guid.NewGuid().ToString("N");
                var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, created);
                File.Move(temp, path, true);
                return _installId = created;
            }
            catch
            {
                // Shadow telemetry must never stop the Tool from starting.
                return _installId = Guid.NewGuid().ToString("N");
            }
        }
    }


    public static string TryGetBuildId()
    {
        lock (Sync)
        {
            if (_buildId is not null) return _buildId;

            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "build_identity.json");
                if (!File.Exists(path))
                    return _buildId = "";

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("buildId", out var buildIdElement)
                    || buildIdElement.ValueKind != JsonValueKind.String)
                {
                    return _buildId = "";
                }

                var buildId = (buildIdElement.GetString() ?? "").Trim();
                if (buildId.Length is < 1 or > 150)
                    return _buildId = "";

                foreach (var ch in buildId)
                {
                    if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))
                        return _buildId = "";
                }

                return _buildId = buildId;
            }
            catch
            {
                // Build identity is observation-only during Shadow Mode.
                return _buildId = "";
            }
        }
    }

    public static string TryGetExecutableSha256()
    {
        lock (Sync)
        {
            if (_exeSha256 is not null) return _exeSha256;
            try
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return _exeSha256 = "";

                using var stream = File.OpenRead(path);
                return _exeSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch
            {
                return _exeSha256 = "";
            }
        }
    }
}
