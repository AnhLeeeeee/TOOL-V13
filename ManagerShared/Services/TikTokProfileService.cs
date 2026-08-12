using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ToolTikTokV12.Models;

namespace ToolTikTokV12.Services;

public sealed class TikTokProfileService
{
    public static string ProfilesRoot => Path.Combine(AppContext.BaseDirectory, "TikTokProfiles");
    public const string LegacyImportedProfilePath = @"D:\TOOL V2\Tool_TikTok_V11_XPath_CSharp\dist\chrome_v11_profile";
    public const string LegacyImportedProfileName = "TikTok cu";
    const int DefaultStartPort = 9222;
    static readonly JsonSerializerOptions CatalogReadJson = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions CatalogWriteJson = new() { WriteIndented = true };

    readonly string _catalogPath;
    readonly CdpPortAllocator _portAllocator = new();

    public string CatalogPath => _catalogPath;
    public string CatalogBackupPath => _catalogPath + ".bak";
    public string RuntimeProfilesRoot => Path.Combine(Path.GetDirectoryName(_catalogPath)!, "profiles");
    public IReadOnlyList<string> LastLoadWarnings { get; private set; } = Array.Empty<string>();

    public TikTokProfileService(string baseDir)
    {
        _catalogPath = Path.Combine(baseDir, "profiles.json");
    }

    public string GetDefaultDataRoot(string profileName)
        => Path.Combine(RuntimeProfilesRoot, NormalizeName(profileName));

    public string ResolveDataRoot(TikTokProfileEntry entry)
        => string.IsNullOrWhiteSpace(entry.DataRoot)
            ? GetDefaultDataRoot(entry.Name)
            : RequireCanonicalPath(entry.DataRoot, "DataRoot");

    /// <summary>
    /// Normalizes a path only after making an empty legacy value explicit.
    /// This keeps callers from leaking ArgumentOutOfRangeException from
    /// Path.GetFullPath when a catalog written by an older version omitted a
    /// path field.
    /// </summary>
    public static string RequireCanonicalPath(string? path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(fieldName + " đang thiếu.");
        return Path.GetFullPath(path.Trim());
    }

    public TikTokProfileCatalog Load()
    {
        var catalog = LoadCatalogFile();
        var warnings = new List<string>();
        catalog.Profiles = DeduplicateCatalogEntries(
            catalog.Profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.ProfilePath))
                .Select(NormalizeEntry)
                .Where(p => Directory.Exists(p.ProfilePath)),
            catalog.SelectedProfile,
            warnings);

        var discovered = DiscoverManagedProfiles();
        foreach (var entry in discovered)
            AddDiscoveredProfile(catalog.Profiles, entry, warnings);

        if (Directory.Exists(LegacyImportedProfilePath))
        {
            AddDiscoveredProfile(catalog.Profiles, new TikTokProfileEntry
            {
                Name = LegacyImportedProfileName,
                ProfilePath = Path.GetFullPath(LegacyImportedProfilePath),
                DataRoot = GetDefaultDataRoot(LegacyImportedProfileName),
                Managed = false,
                Enabled = true
            }, warnings);
        }

        catalog.Profiles = DeduplicateCatalogEntries(catalog.Profiles, catalog.SelectedProfile, warnings)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EnsurePorts(catalog.Profiles);

        if (string.IsNullOrWhiteSpace(catalog.SelectedProfile) || !catalog.Profiles.Any(p => p.Name.Equals(catalog.SelectedProfile, StringComparison.OrdinalIgnoreCase)))
            catalog.SelectedProfile = catalog.Profiles.FirstOrDefault(x => x.Enabled)?.Name ?? catalog.Profiles.FirstOrDefault()?.Name ?? "";

        LastLoadWarnings = warnings.ToArray();
        return catalog;
    }

    /// <summary>
    /// Reads only the persisted catalog file.  Unlike <see cref="Load"/>, this
    /// does not perform profile discovery or mutate the result, so callers can
    /// verify that a catalog transaction reached profiles.json exactly as saved.
    /// </summary>
    public TikTokProfileCatalog LoadPersistedCatalog() => LoadCatalogFile();

    TikTokProfileCatalog LoadCatalogFile()
    {
        if (!File.Exists(_catalogPath)) return new TikTokProfileCatalog();
        var json = File.ReadAllText(_catalogPath);
        if (string.IsNullOrWhiteSpace(json)) return new TikTokProfileCatalog();

        try
        {
            // profiles.json is intentionally written with lower-case entry
            // fields (name/profilePath/dataRoot/cdpPort/...).  The CLR model
            // uses PascalCase properties.  Default System.Text.Json matching
            // is case-sensitive, which can return a catalog with the right
            // number of Profiles but every entry left with empty/default
            // fields.  That made Rename report that the selected persisted
            // entry did not exist.  Read catalog files case-insensitively so
            // both current and legacy casing round-trip correctly.
            var catalog = JsonSerializer.Deserialize<TikTokProfileCatalog>(json, CatalogReadJson);
            if (catalog is not null && catalog.Profiles.Count > 0) return catalog;
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseLegacyShape(doc.RootElement);
        }
        catch
        {
            return new TikTokProfileCatalog();
        }
    }

    TikTokProfileCatalog ParseLegacyShape(JsonElement root)
    {
        var catalog = new TikTokProfileCatalog();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryParseProfileEntry(item, out var entry))
                    catalog.Profiles.Add(entry);
            }
            catalog.SelectedProfile = catalog.Profiles.FirstOrDefault()?.Name ?? "";
            return catalog;
        }

        if (root.TryGetProperty("SelectedProfile", out var selectedProfile))
            catalog.SelectedProfile = selectedProfile.GetString() ?? "";

        if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in profiles.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        catalog.Profiles.Add(new TikTokProfileEntry
                        {
                            Name = name,
                            ProfilePath = GetProfilePath(name),
                            DataRoot = GetDefaultDataRoot(name),
                            Managed = true,
                            Enabled = true
                        });
                    }
                }
                else if (TryParseProfileEntry(item, out var entry))
                {
                    catalog.Profiles.Add(entry);
                }
            }
        }

        return catalog;
    }

    static bool TryParseProfileEntry(JsonElement item, out TikTokProfileEntry entry)
    {
        entry = new TikTokProfileEntry();
        if (item.ValueKind != JsonValueKind.Object) return false;

        entry.Name = item.TryGetProperty("name", out var nameLower)
            ? nameLower.GetString() ?? ""
            : item.TryGetProperty("Name", out var nameUpper) ? nameUpper.GetString() ?? "" : "";

        entry.ProfilePath = item.TryGetProperty("profilePath", out var pathLower)
            ? pathLower.GetString() ?? ""
            : item.TryGetProperty("ProfilePath", out var pathUpper) ? pathUpper.GetString() ?? "" : "";

        entry.DataRoot = item.TryGetProperty("dataRoot", out var dataRootLower)
            ? dataRootLower.GetString() ?? ""
            : item.TryGetProperty("DataRoot", out var dataRootUpper) ? dataRootUpper.GetString() ?? "" : "";

        entry.CdpPort = item.TryGetProperty("cdpPort", out var portLower) && portLower.TryGetInt32(out var lowerPort)
            ? lowerPort
            : item.TryGetProperty("CdpPort", out var portUpper) && portUpper.TryGetInt32(out var upperPort) ? upperPort : 0;

        entry.Enabled = item.TryGetProperty("enabled", out var enabledLower)
            ? ReadBool(enabledLower, true)
            : item.TryGetProperty("Enabled", out var enabledUpper) ? ReadBool(enabledUpper, true) : true;

        entry.Managed = item.TryGetProperty("managed", out var managedLower)
            ? ReadBool(managedLower, true)
            : item.TryGetProperty("Managed", out var managedUpper) ? ReadBool(managedUpper, true) : true;

        return !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.ProfilePath);
    }

    static bool ReadBool(JsonElement value, bool fallback)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    public void Save(TikTokProfileCatalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        var payload = new
        {
            catalog.SelectedProfile,
            Profiles = catalog.Profiles
                .Select(NormalizeEntry)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    name = x.Name,
                    profilePath = x.ProfilePath,
                    dataRoot = x.DataRoot,
                    cdpPort = x.CdpPort,
                    enabled = x.Enabled,
                    managed = x.Managed
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(payload, CatalogWriteJson);
        var tempPath = _catalogPath + ".tmp";
        using (JsonDocument.Parse(json)) { }
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, _catalogPath, true);
        File.Delete(tempPath);
    }

    public void BackupCatalogIfExists()
    {
        if (!File.Exists(_catalogPath)) return;
        File.Copy(_catalogPath, CatalogBackupPath, true);
    }

    public void SaveWithBackup(TikTokProfileCatalog catalog)
    {
        var previous = File.Exists(_catalogPath) ? File.ReadAllText(_catalogPath) : null;
        BackupCatalogIfExists();
        try
        {
            catalog.Profiles = catalog.Profiles.Select(NormalizeEntry).ToList();
            EnsurePorts(catalog.Profiles);
            Save(catalog);
        }
        catch
        {
            if (previous is null)
            {
                if (File.Exists(_catalogPath)) File.Delete(_catalogPath);
            }
            else
            {
                File.WriteAllText(_catalogPath, previous);
            }
            throw;
        }
    }

    /// <summary>
    /// Saves a catalog change that must retain every existing CDP allocation.
    /// A display-name rename never adds a profile, so running the general port
    /// allocator here would be an unrelated mutation of that profile's
    /// identity.  The previous file is restored if the write fails.
    /// </summary>
    public void SaveWithBackupPreservingPorts(TikTokProfileCatalog catalog)
    {
        var previous = File.Exists(_catalogPath) ? File.ReadAllText(_catalogPath) : null;
        BackupCatalogIfExists();
        try
        {
            catalog.Profiles = catalog.Profiles.Select(NormalizeEntry).ToList();
            Save(catalog);
        }
        catch
        {
            if (previous is null)
            {
                if (File.Exists(_catalogPath)) File.Delete(_catalogPath);
            }
            else
            {
                File.WriteAllText(_catalogPath, previous);
            }
            throw;
        }
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
        return new TikTokProfileEntry
        {
            Name = name,
            ProfilePath = path,
            DataRoot = GetDefaultDataRoot(name),
            Managed = true,
            Enabled = true
        };
    }

    public TikTokProfileEntry ImportExistingProfile(string displayName, string profilePath)
    {
        var name = NormalizeName(displayName);
        var fullPath = RequireCanonicalPath(profilePath, "ProfilePath");
        ValidateChromeProfilePath(fullPath);
        return new TikTokProfileEntry
        {
            Name = name,
            ProfilePath = fullPath,
            DataRoot = GetDefaultDataRoot(name),
            Managed = false,
            Enabled = true
        };
    }

    public TikTokProfileEntry RenameProfile(TikTokProfileEntry entry, string newName)
    {
        var to = NormalizeName(newName);
        // The catalog name is a display/identity label.  Moving a Chrome
        // user-data directory for a cosmetic rename can make a stale worker or
        // discovery recreate the old directory as a blank profile.  Keep both
        // storage identities stable and change only the catalog name.
        return new TikTokProfileEntry
        {
            Name = to,
            ProfilePath = RequireCanonicalPath(entry.ProfilePath, "ProfilePath"),
            DataRoot = ResolveDataRoot(entry),
            Managed = entry.Managed,
            Enabled = entry.Enabled,
            CdpPort = entry.CdpPort
        };
    }

    public void DeleteProfile(TikTokProfileEntry entry)
    {
        if (!entry.Managed) return;
        var dir = GetProfileContainerPath(entry.Name);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Không tìm thấy profile: " + entry.Name);
        Directory.Delete(dir, true);
    }

    public void RemoveFromCatalog(TikTokProfileCatalog catalog, string profileName)
    {
        catalog.Profiles.RemoveAll(x => x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (catalog.SelectedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            catalog.SelectedProfile = catalog.Profiles.FirstOrDefault(x => x.Enabled)?.Name ?? catalog.Profiles.FirstOrDefault()?.Name ?? "";
    }

    public void ValidateChromeProfilePath(string profilePath)
    {
        var fullPath = RequireCanonicalPath(profilePath, "ProfilePath");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Không tìm thấy thư mục profile: " + fullPath);

        var defaultDir = Path.Combine(fullPath, "Default");
        var localState = Path.Combine(fullPath, "Local State");
        var preferences = Path.Combine(defaultDir, "Preferences");
        if (!Directory.Exists(defaultDir) && !File.Exists(localState) && !File.Exists(preferences))
            throw new InvalidOperationException("Thư mục không có cấu trúc Chrome profile hợp lệ (cần có Default hoặc Local State).");
    }

    public void EnsurePorts(List<TikTokProfileEntry> profiles)
    {
        _portAllocator.NormalizeProfilePorts(profiles);
    }

    public int FindAvailablePort(IEnumerable<int> reservedPorts)
        => _portAllocator.FindAvailablePort(reservedPorts);

    public bool IsPortAvailable(int port)
        => _portAllocator.IsPortAvailable(port);

    TikTokProfileEntry NormalizeEntry(TikTokProfileEntry entry)
        => new()
        {
            Name = NormalizeName(entry.Name),
            ProfilePath = RequireCanonicalPath(entry.ProfilePath, "ProfilePath"),
            DataRoot = ResolveDataRoot(entry),
            CdpPort = entry.CdpPort,
            Enabled = entry.Enabled,
            Managed = entry.Managed
        };

    void AddDiscoveredProfile(List<TikTokProfileEntry> list, TikTokProfileEntry entry, List<string> warnings)
    {
        var normalized = NormalizeEntry(entry);
        var sameStorage = list.FirstOrDefault(existing => SharesStorageIdentity(existing, normalized));
        if (sameStorage is not null)
        {
            if (!sameStorage.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"[PROFILE_DISCOVERY_ALIAS] Bỏ qua thư mục phát hiện “{normalized.Name}” vì ProfilePath/DataRoot đã thuộc profile catalog “{sameStorage.Name}”. profilePath={normalized.ProfilePath}");
            }
            return;
        }

        var sameName = list.FirstOrDefault(existing => existing.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
        {
            warnings.Add($"[PROFILE_DISCOVERY_NAME_CONFLICT] Bỏ qua thư mục phát hiện “{normalized.Name}” vì tên này đã thuộc profile catalog với ProfilePath khác. catalogPath={sameName.ProfilePath}; discoveredPath={normalized.ProfilePath}");
            return;
        }

        list.Add(normalized);
    }

    List<TikTokProfileEntry> DeduplicateCatalogEntries(IEnumerable<TikTokProfileEntry> entries, string selectedProfile, List<string> warnings)
    {
        var result = new List<TikTokProfileEntry>();
        foreach (var raw in entries)
        {
            var entry = NormalizeEntry(raw);
            var conflictIndex = result.FindIndex(existing =>
                existing.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                || SharesStorageIdentity(existing, entry));
            if (conflictIndex < 0)
            {
                result.Add(entry);
                continue;
            }

            var existing = result[conflictIndex];
            var keepIncoming = entry.Name.Equals(selectedProfile, StringComparison.OrdinalIgnoreCase)
                && !existing.Name.Equals(selectedProfile, StringComparison.OrdinalIgnoreCase);
            var kept = keepIncoming ? entry : existing;
            var ignored = keepIncoming ? existing : entry;
            if (keepIncoming) result[conflictIndex] = entry;
            warnings.Add($"[PROFILE_CATALOG_DEDUP] Giữ “{kept.Name}”, bỏ entry catalog trùng “{ignored.Name}”; ProfilePath/DataRoot trùng hoặc tên trùng. Không xóa dữ liệu trên ổ đĩa.");
        }
        return result;
    }

    public bool SharesStorageIdentity(TikTokProfileEntry left, TikTokProfileEntry right)
    {
        if (!TryCanonicalPath(left.ProfilePath, out var leftProfilePath)
            || !TryCanonicalPath(right.ProfilePath, out var rightProfilePath))
            return false;

        if (leftProfilePath.Equals(rightProfilePath, StringComparison.OrdinalIgnoreCase)) return true;
        return TryCanonicalPath(ResolveDataRoot(left), out var leftDataRoot)
            && TryCanonicalPath(ResolveDataRoot(right), out var rightDataRoot)
            && leftDataRoot.Equals(rightDataRoot, StringComparison.OrdinalIgnoreCase);
    }

    static bool TryCanonicalPath(string? path, out string canonicalPath)
    {
        canonicalPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            canonicalPath = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
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
                DataRoot = GetDefaultDataRoot(Path.GetFileName(dir)),
                Managed = true,
                Enabled = true
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
