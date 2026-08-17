using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ToolTikTokV12.Services;

/// <summary>
/// Keeps Chrome's visible profile name aligned with the Manager name.  This is
/// deliberately a pre-launch operation: Chromium must not have the profile
/// open while its Preferences file is changed.
/// </summary>
public sealed class ChromeProfileNameSyncService
{
    public sealed record SyncResult(bool Updated, string PreferencesPath, string Detail);
    sealed record ChromeProcessInfo(int ProcessId, string CommandLine);

    public SyncResult SyncBeforeLaunch(string userDataDir, string profileName, string? profileDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            throw new InvalidOperationException("ProfilePath đang thiếu.");
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        var (profilePath, profileKey) = ResolveProfileDirectory(userDataDir, profileDirectory);
        var preferencesPath = Path.Combine(profilePath, "Preferences");
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);

        var root = LoadJsonObject(preferencesPath, "Chrome Preferences");

        var profile = root["profile"] as JsonObject;
        if (profile is null)
        {
            profile = new JsonObject();
            root["profile"] = profile;
        }

        var currentName = profile["name"]?.GetValue<string>();
        var currentDefault = profile["using_default_name"]?.GetValue<bool?>();
        var updated = !string.Equals(currentName, profileName, StringComparison.Ordinal) || currentDefault != false;
        if (updated)
        {
            profile["name"] = profileName;
            profile["using_default_name"] = false;
        }

        // V13 managed profiles should be ready to use in Vietnamese from the
        // very first Chrome launch.  Write both Chromium language preference
        // keys while the profile is guaranteed to be closed.  LaunchAsync also
        // supplies --lang/--accept-lang as a runtime fallback.
        var intl = root["intl"] as JsonObject;
        if (intl is null)
        {
            intl = new JsonObject();
            root["intl"] = intl;
        }

        const string preferredLanguages = "vi-VN,vi,en-US,en";
        var currentAcceptLanguages = intl["accept_languages"]?.GetValue<string>();
        var currentSelectedLanguages = intl["selected_languages"]?.GetValue<string>();
        var languageUpdated = !string.Equals(currentAcceptLanguages, preferredLanguages, StringComparison.Ordinal)
            || !string.Equals(currentSelectedLanguages, preferredLanguages, StringComparison.Ordinal);
        if (languageUpdated)
        {
            intl["accept_languages"] = preferredLanguages;
            intl["selected_languages"] = preferredLanguages;
        }

        if (updated || languageUpdated)
            SaveJsonObject(preferencesPath, root);

        var localStateUpdated = UpdateLocalStateName(userDataDir, profileKey, profileName);
        updated |= localStateUpdated || languageUpdated;
        return new SyncResult(updated, preferencesPath, updated
            ? "Đã đồng bộ tên Chrome và ngôn ngữ mặc định tiếng Việt."
            : "Tên Chrome và ngôn ngữ tiếng Việt đã đồng bộ.");
    }

    public static string ResolvePreferencesPath(string userDataDir, string? profileDirectory)
    {
        var (profilePath, _) = ResolveProfileDirectory(userDataDir, profileDirectory);
        return Path.Combine(profilePath, "Preferences");
    }

    static (string profilePath, string profileKey) ResolveProfileDirectory(string userDataDir, string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            throw new InvalidOperationException("ProfilePath đang thiếu.");
        var root = Path.GetFullPath(userDataDir);
        var directory = string.IsNullOrWhiteSpace(profileDirectory)
            ? (File.Exists(Path.Combine(root, "Preferences")) ? root : Path.Combine(root, "Default"))
            : Path.Combine(root, profileDirectory);
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("--profile-directory nằm ngoài Chrome user-data-dir.");
        var key = directory.Equals(root, StringComparison.OrdinalIgnoreCase) ? "Default" : Path.GetRelativePath(root, directory);
        return (directory, key.Replace(Path.DirectorySeparatorChar, '/'));
    }

    static JsonObject LoadJsonObject(string path, string label)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException($"{label} is not a JSON object.");
    }

    static void SaveJsonObject(string path, JsonObject root)
    {
        var temporaryPath = path + ".tooltiktok.tmp";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    static bool UpdateLocalStateName(string userDataDir, string profileKey, string profileName)
    {
        var localStatePath = Path.Combine(Path.GetFullPath(userDataDir), "Local State");
        if (!File.Exists(localStatePath)) return false;

        var root = LoadJsonObject(localStatePath, "Chrome Local State");
        var profile = root["profile"] as JsonObject ?? new JsonObject();
        root["profile"] = profile;
        var infoCache = profile["info_cache"] as JsonObject ?? new JsonObject();
        profile["info_cache"] = infoCache;
        var info = infoCache[profileKey] as JsonObject ?? new JsonObject();
        infoCache[profileKey] = info;
        var currentName = info["name"]?.GetValue<string>();
        var currentDefault = info["is_using_default_name"]?.GetValue<bool?>();
        if (string.Equals(currentName, profileName, StringComparison.Ordinal) && currentDefault == false) return false;
        info["name"] = profileName;
        info["is_using_default_name"] = false;
        SaveJsonObject(localStatePath, root);
        return true;
    }

    public static bool IsProfileInUse(string userDataDir)
    {
        if (!TryGetCanonicalUserDataDir(userDataDir, out var target)) return false;
        return FindChromeProcessesUsingProfile(target).Count > 0;
    }

    /// <summary>
    /// Stops only Chrome process trees whose --user-data-dir exactly matches the
    /// persisted profile path.  It deliberately never uses a display name to find
    /// a process, so another profile cannot be terminated by mistake.
    /// </summary>
    public static IReadOnlyList<int> StopChromeUsingProfile(string userDataDir)
    {
        if (!TryGetCanonicalUserDataDir(userDataDir, out _)) return [];
        var stopped = new List<int>();
        foreach (var item in FindChromeProcessesUsingProfile(userDataDir))
        {
            try
            {
                using var process = Process.GetProcessById(item.ProcessId);
                if (process.HasExited) continue;
                process.Kill(entireProcessTree: true);
                stopped.Add(item.ProcessId);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return stopped;
    }

    static List<ChromeProcessInfo> FindChromeProcessesUsingProfile(string userDataDir)
    {
        if (!TryGetCanonicalUserDataDir(userDataDir, out var target)) return [];
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$target=$env:TOOL_TIKTOK_PROFILE_TARGET; $items=Get-CimInstance Win32_Process -Filter \\\"Name = 'chrome.exe'\\\" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($target,[System.StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object ProcessId,CommandLine; if($items){$items|ConvertTo-Json -Compress}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.Environment["TOOL_TIKTOK_PROFILE_TARGET"] = target;
        process.Start();
        if (!process.WaitForExit(4000))
        {
            try { process.Kill(true); } catch { }
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        if (string.IsNullOrWhiteSpace(output)) return [];
        try
        {
            using var doc = JsonDocument.Parse(output);
            var result = new List<ChromeProcessInfo>();
            void Read(JsonElement item)
            {
                if (!item.TryGetProperty("ProcessId", out var id) || !id.TryGetInt32(out var pid) || pid <= 0) return;
                var commandLine = item.TryGetProperty("CommandLine", out var command) ? command.GetString() ?? "" : "";
                if (ProfileArgumentMatches(commandLine, target)) result.Add(new ChromeProcessInfo(pid, commandLine));
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in doc.RootElement.EnumerateArray()) Read(item);
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                Read(doc.RootElement);
            return result;
        }
        catch { return []; }
    }

    static bool TryGetCanonicalUserDataDir(string? userDataDir, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(userDataDir)) return false;
        try
        {
            target = Path.GetFullPath(userDataDir.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    static bool ProfileArgumentMatches(string commandLine, string target)
    {
        var expression = "--user-data-dir(?:=|\\s+)(?:\\\"(?<value>[^\\\"]+)\\\"|'(?<single>[^']+)'|(?<bare>[^\\s]+))";
        var match = Regex.Match(commandLine, expression, RegexOptions.IgnoreCase);
        var raw = match.Groups["value"].Success ? match.Groups["value"].Value
            : match.Groups["single"].Success ? match.Groups["single"].Value
            : match.Groups["bare"].Value;
        try
        {
            return !string.IsNullOrWhiteSpace(raw)
                && Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar).Equals(target, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
