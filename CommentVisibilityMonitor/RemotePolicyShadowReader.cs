using System.Text.Json;

namespace CommentVisibilityMonitor;

internal sealed record RemotePolicyDecision(
    int Revision,
    string Feature,
    string Mode,
    bool WouldBlock,
    bool Enforcement,
    bool AdminBypass,
    bool Fresh,
    bool Allowed,
    string SourcePath,
    string Error);

internal static class RemotePolicyShadowReader
{
    const string FileName = "manager_remote_policy_runtime.json";

    public static RemotePolicyDecision Evaluate(string feature)
    {
        var normalized =
            (feature ?? "")
            .Trim()
            .ToLowerInvariant();

        var path = FindPolicyFile();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new RemotePolicyDecision(
                0, normalized, "allow", false, false, false, false, true, "", "file_missing_fail_open");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var revision =
                root.TryGetProperty("revision", out var revisionEl)
                && revisionEl.TryGetInt32(out var revisionValue)
                    ? Math.Max(0, revisionValue)
                    : 0;

            var enforcement =
                root.TryGetProperty("enforcementEnabled", out var enforcementEl)
                && enforcementEl.ValueKind == JsonValueKind.True;

            var adminBypass =
                root.TryGetProperty("adminBypass", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.True;

            var fresh =
                TryReadPolicySeenAtUtc(
                    root,
                    out var policySeenAtUtc)
                && DateTime.UtcNow - policySeenAtUtc <= TimeSpan.FromMinutes(3)
                && policySeenAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(1);

            var mode = "allow";

            var policyJsonKey = normalized switch
            {
                "comment_check" => "commentCheck",
                "ban_check" => "banCheck",
                "video_delete" => "videoDelete",
                "video_upload" => "videoUpload",
                "comment_send" => "commentSend",
                "create_profile" => "createProfile",
                "auto_replace" => "autoReplace",
                "daily_replace_all" => "dailyReplaceAll",
                "start_worker" => "startWorker",
                "name_image" => "nameImage",
                "tool_access" => "toolAccess",
                _ => normalized
            };

            if (root.TryGetProperty("policy", out var policyEl)
                && policyEl.ValueKind == JsonValueKind.Object
                && policyEl.TryGetProperty(policyJsonKey, out var featureEl)
                && featureEl.ValueKind == JsonValueKind.String)
            {
                var raw =
                    (featureEl.GetString() ?? "")
                    .Trim()
                    .ToLowerInvariant();

                if (raw is "allow" or "monitor" or "block")
                    mode = raw;
            }

            var wouldBlock =
                !adminBypass
                && string.Equals(mode, "block", StringComparison.OrdinalIgnoreCase);

            var allowed =
                !(fresh && enforcement && wouldBlock);

            return new RemotePolicyDecision(
                revision,
                normalized,
                mode,
                wouldBlock,
                enforcement,
                adminBypass,
                fresh,
                allowed,
                path,
                fresh ? "" : "shadow_stale_fail_open");
        }
        catch (Exception ex)
        {
            return new RemotePolicyDecision(
                0, normalized, "allow", false, false, false, false, true, path,
                "parse_error_fail_open:" + ex.GetType().Name);
        }
    }

    static bool TryReadPolicySeenAtUtc(
        JsonElement root,
        out DateTime seenAtUtc)
    {
        seenAtUtc = default;

        if (TryReadUtc(
                root,
                "policySeenAtUtc",
                out seenAtUtc))
        {
            return true;
        }

        // Tương thích shadow schema cũ.
        return TryReadUtc(
            root,
            "updatedAtUtc",
            out seenAtUtc);
    }

    static bool TryReadUtc(
        JsonElement root,
        string propertyName,
        out DateTime value)
    {
        value = default;

        if (!root.TryGetProperty(
                propertyName,
                out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!DateTime.TryParse(
                element.GetString(),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    static string? FindPolicyFile()
    {
        var baseDir =
            Path.GetFullPath(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        var parent = Directory.GetParent(baseDir)?.FullName ?? "";
        var grandParent =
            string.IsNullOrWhiteSpace(parent)
                ? ""
                : Directory.GetParent(parent)?.FullName ?? "";

        var candidates = new[]
        {
            Path.Combine(baseDir, FileName),
            string.IsNullOrWhiteSpace(parent) ? "" : Path.Combine(parent, FileName),
            string.IsNullOrWhiteSpace(parent) ? "" : Path.Combine(parent, "dist_v13", FileName),
            string.IsNullOrWhiteSpace(grandParent) ? "" : Path.Combine(grandParent, "dist_v13", FileName)
        };

        return candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }
}
