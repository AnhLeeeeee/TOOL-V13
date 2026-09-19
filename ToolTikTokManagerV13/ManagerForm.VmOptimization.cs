using System.Text;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class ManagerVmOptimizationSettingsDocument
    {
        public int Version { get; set; } = 1;
        public string Mode { get; set; } = "VmMax";
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    sealed record ManagerVmOptimizationApplyResult(
        int TotalProfiles,
        int ConfiguredProfiles,
        int LiveWorkersApplied,
        int LiveWorkersDeferred,
        string Mode);

    string ManagerVmOptimizationSettingsPath
        => Path.Combine(_baseDir, "manager_vm_optimization.json");

    static string NormalizeManagerVmOptimizationMode(string? mode)
    {
        mode = (mode ?? "").Trim();
        return mode.ToLowerInvariant() switch
        {
            "normal" or "binhthuong" or "bình thường" => "Normal",
            "vmsafe" or "safe" or "vm_safe" or "vm safe" => "VmSafe",
            "vmmax" or "max" or "vm_max" or "vm max" => "VmMax",
            _ => "VmMax"
        };
    }

    static string ManagerVmOptimizationDisplayName(string? mode)
        => NormalizeManagerVmOptimizationMode(mode) switch
        {
            "Normal" => "Bình thường",
            "VmSafe" => "VM Safe",
            _ => "VM Max"
        };

    bool TryLoadManagerVmOptimizationMode(out string mode)
    {
        mode = "VmMax";
        try
        {
            if (!File.Exists(ManagerVmOptimizationSettingsPath))
                return false;

            var json = File.ReadAllText(ManagerVmOptimizationSettingsPath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<ManagerVmOptimizationSettingsDocument>(json);
            if (document is null)
                return false;

            mode = NormalizeManagerVmOptimizationMode(document.Mode);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"[VM_GLOBAL_SETTINGS_READ] {ex.Message}");
            return false;
        }
    }

    string LoadManagerVmOptimizationModeForUi()
        => TryLoadManagerVmOptimizationMode(out var mode) ? mode : "VmMax";

    void SaveManagerVmOptimizationMode(string mode, string source)
    {
        mode = NormalizeManagerVmOptimizationMode(mode);
        var document = new ManagerVmOptimizationSettingsDocument
        {
            Version = 1,
            Mode = mode,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        var temp = ManagerVmOptimizationSettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, ManagerVmOptimizationSettingsPath, overwrite: true);
        _log.Info($"[VM_GLOBAL_SETTINGS_SAVE] mode={mode} source={source}");
    }

    static void UpsertIniValue(string iniPath, string sectionName, string keyName, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);

        var lines = File.Exists(iniPath)
            ? File.ReadAllLines(iniPath, Encoding.UTF8).ToList()
            : new List<string>();

        var sectionHeader = "[" + sectionName + "]";
        var sectionStart = -1;
        var sectionEnd = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = i;
                sectionEnd = lines.Count;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    var next = lines[j].Trim();
                    if (next.StartsWith("[", StringComparison.Ordinal)
                        && next.EndsWith("]", StringComparison.Ordinal))
                    {
                        sectionEnd = j;
                        break;
                    }
                }
                break;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{keyName}={value}");
        }
        else
        {
            var replaced = false;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                var raw = lines[i];
                var equals = raw.IndexOf('=');
                if (equals <= 0) continue;
                var key = raw[..equals].Trim();
                if (!key.Equals(keyName, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = $"{keyName}={value}";
                replaced = true;
                break;
            }

            if (!replaced)
                lines.Insert(sectionEnd, $"{keyName}={value}");
        }

        var temp = iniPath + ".vm-global.tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        File.Move(temp, iniPath, overwrite: true);
    }

    void ApplyManagerVmOptimizationToProfileConfig(
        string dataRoot,
        string profileName,
        string mode,
        string source)
    {
        mode = NormalizeManagerVmOptimizationMode(mode);
        var iniPath = Path.Combine(dataRoot, "auto_chrome.ini");
        UpsertIniValue(iniPath, "VM", "Mode", mode);
        _log.Info($"[VM_GLOBAL_PROFILE_CONFIG] profile={profileName} mode={mode} source={source}");
    }

    void ApplyManagerVmOptimizationToProfileConfigIfConfigured(
        string dataRoot,
        string profileName,
        string source)
    {
        if (!TryLoadManagerVmOptimizationMode(out var mode))
            return;

        try
        {
            ApplyManagerVmOptimizationToProfileConfig(dataRoot, profileName, mode, source);
        }
        catch (Exception ex)
        {
            // Global VM optimization must never block opening/creating a profile.
            _log.Warn($"[VM_GLOBAL_PROFILE_CONFIG_WARN] profile={profileName} source={source} error={ex.Message}");
        }
    }

    async Task<ManagerVmOptimizationApplyResult> ApplyManagerVmOptimizationToAllProfilesAsync(
        string requestedMode,
        string source)
    {
        var mode = NormalizeManagerVmOptimizationMode(requestedMode);
        SaveManagerVmOptimizationMode(mode, source);

        var catalog = _profileService.Load();
        var profiles = catalog.Profiles
            .OrderBy(profile => profile.Name, NaturalProfileNameOrder)
            .ToList();

        var configured = 0;
        var liveApplied = 0;
        var liveDeferred = 0;

        // First persist the mode to every existing profile. This covers profiles
        // that are not open right now and gives live workers a safe restart fallback.
        foreach (var profile in profiles)
        {
            try
            {
                var dataRoot = _profileService.ResolveDataRoot(profile);
                Directory.CreateDirectory(dataRoot);
                ApplyManagerVmOptimizationToProfileConfig(
                    dataRoot,
                    profile.Name,
                    mode,
                    source + ":all_profiles");
                configured++;
            }
            catch (Exception ex)
            {
                _log.Warn($"[VM_GLOBAL_PROFILE_CONFIG_WARN] profile={profile.Name} source={source} error={ex.Message}");
            }
        }

        // If Manager has a custom default INI, keep its VM mode aligned too.
        // We intentionally do not create a full default config here: global VM mode
        // is enforced before every Worker start by Apply...IfConfigured().
        try
        {
            if (File.Exists(ManagerDefaultIniPath))
                UpsertIniValue(ManagerDefaultIniPath, "VM", "Mode", mode);
        }
        catch (Exception ex)
        {
            _log.Warn($"[VM_GLOBAL_DEFAULT_CONFIG_WARN] mode={mode} error={ex.Message}");
        }

        // Reuse the existing Worker VM engine for every live Worker. No Chrome restart
        // is required: Worker saves its own settings, updates UI/log intervals, then
        // reapplies the existing CDP media/video/CSS policy to the current page.
        foreach (var ctx in _contexts.Values
                     .Where(context => context.Worker is not null)
                     .OrderBy(context => context.Profile.Name, NaturalProfileNameOrder)
                     .ToList())
        {
            bool workerAlive;
            try { workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited; }
            catch { workerAlive = false; }
            if (!workerAlive) continue;

            try
            {
                var reply = await SendPipeAsync(
                    ctx.Profile.Name,
                    "apply_vm_mode|" + mode,
                    TimeSpan.FromSeconds(8));

                if (reply.StartsWith("applied", StringComparison.OrdinalIgnoreCase))
                {
                    liveApplied++;
                    _log.Info($"[VM_GLOBAL_LIVE_APPLIED] profile={ctx.Profile.Name} mode={mode} reply={reply}");
                }
                else
                {
                    liveDeferred++;
                    _log.Warn($"[VM_GLOBAL_LIVE_DEFERRED] profile={ctx.Profile.Name} mode={mode} reply={reply}");
                }
            }
            catch (Exception ex)
            {
                liveDeferred++;
                _log.Warn($"[VM_GLOBAL_LIVE_DEFERRED] profile={ctx.Profile.Name} mode={mode} error={ex.Message}");
            }
        }

        _log.Info(
            $"[VM_GLOBAL_APPLY_ALL_DONE] mode={mode} total={profiles.Count} configured={configured} liveApplied={liveApplied} liveDeferred={liveDeferred}");

        return new ManagerVmOptimizationApplyResult(
            profiles.Count,
            configured,
            liveApplied,
            liveDeferred,
            mode);
    }
}
