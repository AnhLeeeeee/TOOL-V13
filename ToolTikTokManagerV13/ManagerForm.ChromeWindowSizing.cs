using System.Text;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class ManagerChromeWindowSettingsDocument
    {
        public int Version { get; set; } = 1;
        public string Mode { get; set; } = "Full";
        public int Percent { get; set; } = 70;
        public string Position { get; set; } = "BottomLeft";
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    sealed record ManagerChromeWindowApplyResult(
        int TotalProfiles,
        int ConfiguredProfiles,
        int LiveWorkersApplied,
        int LiveWorkersDeferred,
        string Mode,
        int Percent,
        string Position);

    string ManagerChromeWindowSettingsPath
        => Path.Combine(_baseDir, "manager_chrome_window.json");

    static string NormalizeManagerChromeWindowMode(string? mode)
        => string.Equals((mode ?? "").Trim(), "Percent", StringComparison.OrdinalIgnoreCase)
            ? "Percent"
            : "Full";

    static string NormalizeManagerChromeWindowPosition(string? value)
    {
        value = (value ?? "").Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return value.ToLowerInvariant() switch
        {
            "bottomright" => "BottomRight",
            "topleft" => "TopLeft",
            "topright" => "TopRight",
            _ => "BottomLeft"
        };
    }

    static string ManagerChromeWindowPositionDisplayName(string? value)
        => NormalizeManagerChromeWindowPosition(value) switch
        {
            "BottomRight" => "Góc dưới phải",
            "TopLeft" => "Góc trên trái",
            "TopRight" => "Góc trên phải",
            _ => "Góc dưới trái"
        };

    static int GetManagerChromeAutoZoomPercent(int percent)
        => percent >= 90 ? 100
            : percent >= 75 ? 90
            : percent >= 65 ? 85
            : 80;

    bool TryLoadManagerChromeWindowSettings(out ManagerChromeWindowSettingsDocument settings)
    {
        settings = new ManagerChromeWindowSettingsDocument();
        try
        {
            if (!File.Exists(ManagerChromeWindowSettingsPath))
                return false;

            var json = File.ReadAllText(ManagerChromeWindowSettingsPath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<ManagerChromeWindowSettingsDocument>(json);
            if (loaded is null)
                return false;

            settings = NormalizeManagerChromeWindowSettings(loaded);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"[CHROME_WINDOW_GLOBAL_SETTINGS_READ] {ex.Message}");
            return false;
        }
    }

    ManagerChromeWindowSettingsDocument LoadManagerChromeWindowSettingsForUi()
        => TryLoadManagerChromeWindowSettings(out var settings)
            ? settings
            : new ManagerChromeWindowSettingsDocument();

    static ManagerChromeWindowSettingsDocument NormalizeManagerChromeWindowSettings(
        ManagerChromeWindowSettingsDocument settings)
    {
        settings.Version = 1;
        settings.Mode = NormalizeManagerChromeWindowMode(settings.Mode);
        settings.Percent = Math.Clamp(settings.Percent, 50, 100);
        settings.Position = NormalizeManagerChromeWindowPosition(settings.Position);
        return settings;
    }

    void SaveManagerChromeWindowSettings(
        string mode,
        int percent,
        string position,
        string source)
    {
        var settings = NormalizeManagerChromeWindowSettings(new ManagerChromeWindowSettingsDocument
        {
            Mode = mode,
            Percent = percent,
            Position = position,
            UpdatedAtUtc = DateTime.UtcNow
        });

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temp = ManagerChromeWindowSettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, ManagerChromeWindowSettingsPath, overwrite: true);
        _log.Info(
            $"[CHROME_WINDOW_GLOBAL_SETTINGS_SAVE] mode={settings.Mode} percent={settings.Percent} position={settings.Position} source={source}");
    }

    void ApplyManagerChromeWindowToProfileConfig(
        string dataRoot,
        string profileName,
        string mode,
        int percent,
        string position,
        string source)
    {
        mode = NormalizeManagerChromeWindowMode(mode);
        percent = Math.Clamp(percent, 50, 100);
        position = NormalizeManagerChromeWindowPosition(position);
        var iniPath = Path.Combine(dataRoot, "auto_chrome.ini");
        UpsertIniValue(iniPath, "ChromeWindow", "Mode", mode);
        UpsertIniValue(iniPath, "ChromeWindow", "Percent", percent.ToString());
        UpsertIniValue(iniPath, "ChromeWindow", "Position", position);
        _log.Info(
            $"[CHROME_WINDOW_GLOBAL_PROFILE_CONFIG] profile={profileName} mode={mode} percent={percent} position={position} source={source}");
    }

    void ApplyManagerChromeWindowToProfileConfigIfConfigured(
        string dataRoot,
        string profileName,
        string source)
    {
        if (!TryLoadManagerChromeWindowSettings(out var settings))
            return;

        try
        {
            ApplyManagerChromeWindowToProfileConfig(
                dataRoot,
                profileName,
                settings.Mode,
                settings.Percent,
                settings.Position,
                source);
        }
        catch (Exception ex)
        {
            // Đây là tuỳ chọn giao diện Chrome; tuyệt đối không chặn mở/chạy PRF.
            _log.Warn(
                $"[CHROME_WINDOW_GLOBAL_PROFILE_CONFIG_WARN] profile={profileName} source={source} error={ex.Message}");
        }
    }

    async Task<ManagerChromeWindowApplyResult> ApplyManagerChromeWindowToAllProfilesAsync(
        string requestedMode,
        int requestedPercent,
        string requestedPosition,
        string source)
    {
        var mode = NormalizeManagerChromeWindowMode(requestedMode);
        var percent = Math.Clamp(requestedPercent, 50, 100);
        var position = NormalizeManagerChromeWindowPosition(requestedPosition);
        SaveManagerChromeWindowSettings(mode, percent, position, source);

        var catalog = _profileService.Load();
        var profiles = catalog.Profiles
            .OrderBy(profile => profile.Name, NaturalProfileNameOrder)
            .ToList();

        var configured = 0;
        var liveApplied = 0;
        var liveDeferred = 0;

        foreach (var profile in profiles)
        {
            try
            {
                var dataRoot = _profileService.ResolveDataRoot(profile);
                Directory.CreateDirectory(dataRoot);
                ApplyManagerChromeWindowToProfileConfig(
                    dataRoot,
                    profile.Name,
                    mode,
                    percent,
                    position,
                    source + ":all_profiles");
                configured++;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[CHROME_WINDOW_GLOBAL_PROFILE_CONFIG_WARN] profile={profile.Name} source={source} error={ex.Message}");
            }
        }

        try
        {
            if (File.Exists(ManagerDefaultIniPath))
            {
                UpsertIniValue(ManagerDefaultIniPath, "ChromeWindow", "Mode", mode);
                UpsertIniValue(ManagerDefaultIniPath, "ChromeWindow", "Percent", percent.ToString());
                UpsertIniValue(ManagerDefaultIniPath, "ChromeWindow", "Position", position);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[CHROME_WINDOW_GLOBAL_DEFAULT_CONFIG_WARN] mode={mode} percent={percent} position={position} error={ex.Message}");
        }

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
                    $"apply_chrome_window|{mode}|{percent}|{position}",
                    TimeSpan.FromSeconds(10));

                if (reply.StartsWith("applied", StringComparison.OrdinalIgnoreCase)
                    && !reply.EndsWith("|deferred", StringComparison.OrdinalIgnoreCase))
                {
                    liveApplied++;
                    _log.Info(
                        $"[CHROME_WINDOW_GLOBAL_LIVE_APPLIED] profile={ctx.Profile.Name} mode={mode} percent={percent} position={position} reply={reply}");
                }
                else
                {
                    liveDeferred++;
                    _log.Warn(
                        $"[CHROME_WINDOW_GLOBAL_LIVE_DEFERRED] profile={ctx.Profile.Name} mode={mode} percent={percent} position={position} reply={reply}");
                }
            }
            catch (Exception ex)
            {
                liveDeferred++;
                _log.Warn(
                    $"[CHROME_WINDOW_GLOBAL_LIVE_DEFERRED] profile={ctx.Profile.Name} mode={mode} percent={percent} position={position} error={ex.Message}");
            }
        }

        _log.Info(
            $"[CHROME_WINDOW_GLOBAL_APPLY_ALL_DONE] mode={mode} percent={percent} position={position} total={profiles.Count} configured={configured} liveApplied={liveApplied} liveDeferred={liveDeferred}");

        return new ManagerChromeWindowApplyResult(
            profiles.Count,
            configured,
            liveApplied,
            liveDeferred,
            mode,
            percent,
            position);
    }
}
