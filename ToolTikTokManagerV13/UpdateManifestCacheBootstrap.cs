using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolTikTokManagerV13;

/// <summary>
/// Bổ sung cache-buster cho manifest update trên raw.githubusercontent.com.
/// Chạy ngay khi Manager load để các bản sau không phải yêu cầu từng máy khách
/// sửa tay URL version.json / versions.json khi CDN còn giữ manifest cũ.
/// </summary>
internal static class UpdateManifestCacheBootstrap
{
    const string SettingsFileName = "manager_update.json";
    const string DefaultManifestUrl = "https://raw.githubusercontent.com/AnhLeeeeee/TOOL-V13/main/version.json";
    const string DefaultVersionsManifestUrl = "https://raw.githubusercontent.com/AnhLeeeeee/TOOL-V13/main/versions.json";
    const string CacheBustKey = "_cb";

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var settingsPath = Path.Combine(baseDir, SettingsFileName);

            JsonObject root;
            if (File.Exists(settingsPath))
            {
                var text = File.ReadAllText(settingsPath);
                root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject
                {
                    ["ManifestUrl"] = DefaultManifestUrl,
                    ["VersionsManifestUrl"] = DefaultVersionsManifestUrl,
                    ["Channel"] = "stable",
                    ["AutoCheck"] = true,
                    ["PinnedVersion"] = ""
                };
            }

            var manifestUrl = ReadString(root, "ManifestUrl");
            var versionsUrl = ReadString(root, "VersionsManifestUrl");

            if (string.IsNullOrWhiteSpace(manifestUrl))
                manifestUrl = DefaultManifestUrl;
            if (string.IsNullOrWhiteSpace(versionsUrl))
                versionsUrl = DeriveVersionsUrl(manifestUrl) ?? DefaultVersionsManifestUrl;

            // Mỗi lần mở Manager dùng token mới. URL hiển thị/cấu hình vẫn giữ nguyên nguồn,
            // chỉ thay _cb để CDN coi đây là request mới.
            var token = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            root["ManifestUrl"] = AddOrReplaceCacheBust(manifestUrl, token);
            root["VersionsManifestUrl"] = AddOrReplaceCacheBust(versionsUrl, token);

            var tempPath = settingsPath + ".cachebust.tmp";
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, settingsPath, true);
        }
        catch
        {
            // Updater vẫn hoạt động theo cơ chế cũ nếu file cấu hình không thể ghi.
        }
    }

    static string ReadString(JsonObject root, string propertyName)
    {
        try { return root[propertyName]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    static string? DeriveVersionsUrl(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri)) return null;
        try
        {
            var builder = new UriBuilder(uri);
            var path = builder.Path;
            var slash = path.LastIndexOf('/');
            builder.Path = slash >= 0 ? path[..(slash + 1)] + "versions.json" : "/versions.json";
            builder.Query = RemoveQueryKey(builder.Query, CacheBustKey);
            return builder.Uri.ToString();
        }
        catch { return null; }
    }

    static string AddOrReplaceCacheBust(string url, string token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        // Chỉ can thiệp nguồn raw GitHub đang dùng cho manifest; URL tùy chỉnh khác được giữ nguyên.
        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            var builder = new UriBuilder(uri);
            var query = RemoveQueryKey(builder.Query, CacheBustKey);
            builder.Query = string.IsNullOrEmpty(query)
                ? $"{CacheBustKey}={Uri.EscapeDataString(token)}"
                : $"{query}&{CacheBustKey}={Uri.EscapeDataString(token)}";
            return builder.Uri.ToString();
        }
        catch { return url; }
    }

    static string RemoveQueryKey(string query, string key)
    {
        var raw = (query ?? "").TrimStart('?');
        if (raw.Length == 0) return "";

        return string.Join("&", raw
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                           && !part.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }
}
