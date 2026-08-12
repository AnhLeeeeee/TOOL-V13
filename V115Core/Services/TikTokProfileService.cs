using System.Text.Json;
using ToolTikTokV11.Models;

namespace ToolTikTokV11.Services;

public sealed class TikTokProfileService
{
    public const string ProfilesRoot = @"D:\TOOL V2\TikTokProfiles";
    public const string LegacyImportedProfilePath = @"D:\TOOL V2\Tool_TikTok_V11_XPath_CSharp\dist\chrome_v11_profile";
    public const string LegacyImportedProfileName = "TikTok cu";

    readonly string _catalogPath;

    public string CatalogPath => _catalogPath;

    public TikTokProfileService(string baseDir)
    {
        _catalogPath = Path.Combine(baseDir, "profiles.json");
    }

    public TikTokProfileCatalog Load()
    {
        var catalog = LoadCatalogFile();
        var discovered = DiscoverManagedProfiles();
        foreach (var entry in discovered)
            MergeOrAdd(catalog.Profiles, entry);

        if (Directory.Exists(LegacyImportedProfilePath))
            MergeOrAdd(catalog.Profiles, new TikTokProfileEntry
            {
                Name = LegacyImportedProfileName,
                ProfilePath = Path.GetFullPath(LegacyImportedProfilePath),
                Managed = false
            });

        catalog.Profiles = catalog.Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.ProfilePath))
            .Select(NormalizeEntry)
            .Where(p => Directory.Exists(p.ProfilePath))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(catalog.SelectedProfile) || !catalog.Profiles.Any(p => p.Name.Equals(catalog.SelectedProfile, StringComparison.OrdinalIgnoreCase)))
            catalog.SelectedProfile = catalog.Profiles.FirstOrDefault()?.Name ?? "";

        return catalog;
    }

    TikTokProfileCatalog LoadCatalogFile()
    {
        if (!File.Exists(_catalogPath)) return new TikTokProfileCatalog();
        var json = File.ReadAllText(_catalogPath);
        if (string.IsNullOrWhiteSpace(json)) return new TikTokProfileCatalog();

        try
        {
            var catalog = JsonSerializer.Deserialize<TikTokProfileCatalog>(json);
            if (catalog is not null) return catalog;
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var catalog = new TikTokProfileCatalog();
            if (root.TryGetProperty("SelectedProfile", out var sp)) catalog.SelectedProfile = sp.GetString() ?? "";
            if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in profiles.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(name))
                            catalog.Profiles.Add(new TikTokProfileEntry { Name = name, ProfilePath = GetProfilePath(name), Managed = true });
                    }
                }
            }
            return catalog;
        }
        catch
        {
            return new TikTokProfileCatalog();
        }
    }

    public void Save(TikTokProfileCatalog catalog)
    {
        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_catalogPath, json);
    }

    public string NormalizeName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new InvalidOperationException("Tên profile đang trống.");
        if (trimmed is "." or "..") throw new InvalidOperationException("Tên profile không hợp lệ.");
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Tên profile chứa ký tự không hợp lệ.");
        return trimmed;
    }

    public string GetProfileContainerPath(string profileName) => Path.Combine(ProfilesRoot, profileName);

    public string GetProfilePath(string profileName) => Path.Combine(GetProfileContainerPath(profileName), "chrome_profile");

    public TikTokProfileEntry CreateManagedProfile(string profileName)
    {
        var name = NormalizeName(profileName);
        var path = GetProfilePath(name);
        if (Directory.Exists(path)) throw new InvalidOperationException("Profile đã tồn tại: " + name);
        Directory.CreateDirectory(path);
        return new TikTokProfileEntry { Name = name, ProfilePath = path, Managed = true };
    }

    public TikTokProfileEntry ImportExistingProfile(string displayName, string profilePath)
    {
        var name = NormalizeName(displayName);
        var fullPath = Path.GetFullPath((profilePath ?? "").Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Không tìm thấy thư mục profile: " + fullPath);
        return new TikTokProfileEntry { Name = name, ProfilePath = fullPath, Managed = false };
    }

    public void DeleteProfile(TikTokProfileEntry entry)
    {
        if (entry.Managed)
        {
            var dir = GetProfileContainerPath(entry.Name);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Không tìm thấy profile: " + entry.Name);
            Directory.Delete(dir, true);
        }
    }

    TikTokProfileEntry NormalizeEntry(TikTokProfileEntry entry)
        => new()
        {
            Name = NormalizeName(entry.Name),
            ProfilePath = Path.GetFullPath(entry.ProfilePath.Trim()),
            Managed = entry.Managed
        };

    static void MergeOrAdd(List<TikTokProfileEntry> list, TikTokProfileEntry entry)
    {
        var existing = list.FirstOrDefault(x => x.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) list.Add(entry);
        else
        {
            existing.ProfilePath = Path.GetFullPath(entry.ProfilePath);
            existing.Managed = entry.Managed;
        }
    }

    List<TikTokProfileEntry> DiscoverManagedProfiles()
    {
        if (!Directory.Exists(ProfilesRoot)) return [];
        return Directory.EnumerateDirectories(ProfilesRoot)
            .Where(dir => Directory.Exists(Path.Combine(dir, "chrome_profile")))
            .Select(dir => new TikTokProfileEntry
            {
                Name = Path.GetFileName(dir),
                ProfilePath = Path.Combine(dir, "chrome_profile"),
                Managed = true
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
