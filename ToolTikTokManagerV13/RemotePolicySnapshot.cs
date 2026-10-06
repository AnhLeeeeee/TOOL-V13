using System.Text.Json;

namespace ToolTikTokManagerV13;

/// <summary>
/// Snapshot remote policy nhận từ QITool.
/// Bước 3C.4 chỉ parse + giữ trong RAM + ghi log để quan sát.
/// TUYỆT ĐỐI chưa được dùng để chặn LIVE/CREATE/VIDEO/...
/// </summary>
internal sealed record RemotePolicySnapshot(
    int Revision,
    string ToolAccess,
    string Update,
    string StartWorker,
    string Live,
    string CommentSend,
    string CreateProfile,
    string AutoReplace,
    string DailyReplaceAll,
    string Login,
    string NameImage,
    string VideoDelete,
    string VideoUpload,
    string CommentCheck,
    string BanCheck,
    string Proxy,
    int? MaxWorkers,
    int? MaxRunningProfiles,
    int? MaxCreatePerHour)
{
    public static RemotePolicySnapshot AllowAll { get; } = new(
        Revision: 0,
        ToolAccess: "allow",
        Update: "allow",
        StartWorker: "allow",
        Live: "allow",
        CommentSend: "allow",
        CreateProfile: "allow",
        AutoReplace: "allow",
        DailyReplaceAll: "allow",
        Login: "allow",
        NameImage: "allow",
        VideoDelete: "allow",
        VideoUpload: "allow",
        CommentCheck: "allow",
        BanCheck: "allow",
        Proxy: "allow",
        MaxWorkers: null,
        MaxRunningProfiles: null,
        MaxCreatePerHour: null);

    public static RemotePolicySnapshot? TryParse(
        int? revision,
        JsonElement? policyElement)
    {
        if (policyElement is null
            || policyElement.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var policy = policyElement.Value;
        var safeRevision = Math.Max(0, revision ?? 0);

        // Fail-open ở mọi key thiếu/sai kiểu: mode không hợp lệ => allow, limit lỗi => null.
        return new RemotePolicySnapshot(
            Revision: safeRevision,
            ToolAccess: ReadMode(policy, "tool_access"),
            Update: ReadMode(policy, "update"),
            StartWorker: ReadMode(policy, "start_worker"),
            Live: ReadMode(policy, "live"),
            CommentSend: ReadMode(policy, "comment_send"),
            CreateProfile: ReadMode(policy, "create_profile"),
            AutoReplace: ReadMode(policy, "auto_replace"),
            DailyReplaceAll: ReadMode(policy, "daily_replace_all"),
            Login: ReadMode(policy, "login"),
            NameImage: ReadMode(policy, "name_image"),
            VideoDelete: ReadMode(policy, "video_delete"),
            VideoUpload: ReadMode(policy, "video_upload"),
            CommentCheck: ReadMode(policy, "comment_check"),
            BanCheck: ReadMode(policy, "ban_check"),
            Proxy: ReadMode(policy, "proxy"),
            MaxWorkers: ReadLimit(policy, "max_workers"),
            MaxRunningProfiles: ReadLimit(policy, "max_running_profiles"),
            MaxCreatePerHour: ReadLimit(policy, "max_create_per_hour"));
    }

    static string ReadMode(JsonElement policy, string propertyName)
    {
        if (!policy.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "allow";
        }

        var mode = (value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "allow" or "monitor" or "block"
            ? mode
            : "allow";
    }

    static int? ReadLimit(JsonElement policy, string propertyName)
    {
        if (!policy.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            && number >= 0)
        {
            return number;
        }

        return null;
    }

    public static string FormatLimit(int? value)
        => value is null ? "none" : value.Value.ToString();
}
