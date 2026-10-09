namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    // Intent chỉ sống trong phiên Chrome hiện tại: user bấm "Mở Chrome" để kiểm tra
    // thì Manager vẫn cho Worker launch + toàn bộ login flow hoạt động, nhưng scheduler
    // AutoOnReady không được tự chạy Tên/ảnh -> VIDEO -> Start. Intent chỉ kết thúc khi
    // user/automation thật sự yêu cầu Start/Resume. Không xóa theo một nhịp CDP
    // DISCONNECTED tạm thời để tránh reconnect xong tự chạy ngoài ý muốn.
    // Không persist xuống đĩa để không làm thay đổi lifecycle tự động sau restart Manager.
    readonly object _manualInspectOpenLock = new();
    readonly HashSet<string> _manualInspectOpenProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    void ArmManualInspectOpen(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var added = false;
        lock (_manualInspectOpenLock)
            added = _manualInspectOpenProfiles.Add(profileName);

        if (added)
        {
            _log.Info(
                $"[MANUAL_INSPECT_OPEN_ARMED] profile={profileName} source={source} " +
                "mode=LOGIN_ONLY_NO_AUTO_IDENTITY_NO_VIDEO_NO_START");
        }
    }

    bool IsManualInspectOpen(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return false;

        lock (_manualInspectOpenLock)
            return _manualInspectOpenProfiles.Contains(profileName);
    }

    void ClearManualInspectOpen(string profileName, string source)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        var removed = false;
        lock (_manualInspectOpenLock)
            removed = _manualInspectOpenProfiles.Remove(profileName);

        if (removed)
        {
            _log.Info(
                $"[MANUAL_INSPECT_OPEN_CLEARED] profile={profileName} source={source}");
        }
    }
}
