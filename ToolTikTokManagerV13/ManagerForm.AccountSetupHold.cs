using System.Diagnostics;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const string ManagedAccountSetupHoldFileName = "manager_account_setup_hold.json";

    sealed class ManagedAccountSetupHoldDocument
    {
        public int Version { get; set; } = 1;
        public int ManagerPid { get; set; }
        public string ProfileName { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    string ManagedAccountSetupHoldPath(ProfileContext ctx)
    {
        var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
        Directory.CreateDirectory(dataRoot);
        return Path.Combine(dataRoot, ManagedAccountSetupHoldFileName);
    }

    bool ShouldArmManagedAccountSetupHold()
    {
        try
        {
            var state = LoadIdentityToolState();
            if (!state.AutoOnReady) return false;
            if (string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath)) return false;

            // Chỉ cần một trong hai khối Tên/ảnh hoặc VIDEO tham gia flow tự động
            // thì Worker phải giữ Start/LIVE cho tới khi pipeline kết thúc.
            return state.UpdateName
                   || state.UpdateAvatar
                   || state.UpdateBio
                   || state.VideoDeleteEnabled
                   || state.VideoUploadEnabled;
        }
        catch
        {
            // Không tạo hold nếu chưa đọc được cấu hình; tránh khóa Worker ngoài ý muốn.
            return false;
        }
    }

    void ArmManagedAccountSetupHold(ProfileContext ctx, string reason)
    {
        try
        {
            var path = ManagedAccountSetupHoldPath(ctx);
            var temp = path + ".tmp";
            var doc = new ManagedAccountSetupHoldDocument
            {
                ManagerPid = Environment.ProcessId,
                ProfileName = ctx.Profile.Name,
                Reason = reason ?? "",
                CreatedUtc = DateTime.UtcNow
            };

            File.WriteAllText(temp, JsonSerializer.Serialize(doc));
            File.Move(temp, path, overwrite: true);
            _log.Info($"[ACCOUNT_SETUP_HOLD_ON] profile={ctx.Profile.Name} reason={reason}");
        }
        catch (Exception ex)
        {
            // Hold là lớp điều phối bổ sung; lỗi persistence không được phá logic cũ.
            _log.Warn($"[ACCOUNT_SETUP_HOLD_ON_WARN] profile={ctx.Profile.Name} reason={reason} error={ex.Message}");
        }
    }

    void ClearManagedAccountSetupHold(ProfileContext ctx, string reason)
    {
        try
        {
            var path = ManagedAccountSetupHoldPath(ctx);
            if (File.Exists(path)) File.Delete(path);
            try
            {
                var temp = path + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch { }
            _log.Info($"[ACCOUNT_SETUP_HOLD_OFF] profile={ctx.Profile.Name} reason={reason}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[ACCOUNT_SETUP_HOLD_OFF_WARN] profile={ctx.Profile.Name} reason={reason} error={ex.Message}");
        }
    }

    bool IsManagedAccountSetupHoldActive(ProfileContext ctx)
    {
        try
        {
            var path = ManagedAccountSetupHoldPath(ctx);
            if (!File.Exists(path)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ManagerPid", out var pidProp)
                || !pidProp.TryGetInt32(out var managerPid)
                || managerPid <= 0)
                return false;

            if (managerPid == Environment.ProcessId) return true;

            try
            {
                using var owner = Process.GetProcessById(managerPid);
                if (!owner.HasExited) return true;
            }
            catch { }

            // File của Manager cũ/crash: tự dọn để không khóa Worker vĩnh viễn.
            try { File.Delete(path); } catch { }
            return false;
        }
        catch
        {
            return false;
        }
    }

    string GetManagedAccountSetupHoldReason(ProfileContext ctx)
    {
        try
        {
            // IsManagedAccountSetupHoldActive đồng thời tự dọn hold stale của Manager cũ.
            if (!IsManagedAccountSetupHoldActive(ctx)) return "";

            var path = ManagedAccountSetupHoldPath(ctx);
            if (!File.Exists(path)) return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Reason", out var reasonProp))
                return (reasonProp.GetString() ?? "").Trim();
        }
        catch { }

        return "";
    }

    async Task ReleaseManagedAccountSetupHoldAsync(ProfileContext ctx, string reason)
    {
        ClearManagedAccountSetupHold(ctx, reason);

        // Báo Worker ngay để nếu user đã bấm Start trong lúc setup, Worker có thể
        // thực hiện intent đó sau khi Tên/ảnh -> VIDEO đã kết thúc.
        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                var reply = await SendPipeAsync(ctx.Profile.Name, "setup_release", TimeSpan.FromSeconds(5));
                _log.Info($"[ACCOUNT_SETUP_RELEASE_IPC] profile={ctx.Profile.Name} reason={reason} reply={reply}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[ACCOUNT_SETUP_RELEASE_IPC_WARN] profile={ctx.Profile.Name} reason={reason} error={ex.Message}");
        }
    }

    async Task CancelManagedAccountSetupHoldAsync(ProfileContext ctx, string reason)
    {
        // Dùng cho đường lỗi/pause/return không được phép Start: xóa HOLD nhưng KHÔNG
        // phát lại Start intent mà Worker đã defer trong lúc setup.
        ClearManagedAccountSetupHold(ctx, reason);

        try
        {
            if (ctx.Worker is not null && !ctx.Worker.HasExited)
            {
                var reply = await SendPipeAsync(ctx.Profile.Name, "setup_cancel", TimeSpan.FromSeconds(5));
                _log.Info($"[ACCOUNT_SETUP_CANCEL_IPC] profile={ctx.Profile.Name} reason={reason} reply={reply}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[ACCOUNT_SETUP_CANCEL_IPC_WARN] profile={ctx.Profile.Name} reason={reason} error={ex.Message}");
        }
    }

    async Task<bool> WaitForManagedAccountSetupReleaseAsync(
        ProfileContext ctx,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var logged = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsManagedAccountSetupHoldActive(ctx))
            {
                if (logged)
                    _log.Info($"[ACCOUNT_SETUP_WAIT_DONE] profile={ctx.Profile.Name}");
                return true;
            }

            if (!logged)
            {
                logged = true;
                _log.Info($"[ACCOUNT_SETUP_WAIT] profile={ctx.Profile.Name} timeoutSec={timeout.TotalSeconds:0}");
            }
            await Task.Delay(500, ct);
        }

        _log.Warn($"[ACCOUNT_SETUP_WAIT_TIMEOUT] profile={ctx.Profile.Name} timeoutSec={timeout.TotalSeconds:0}");
        return false;
    }
}
