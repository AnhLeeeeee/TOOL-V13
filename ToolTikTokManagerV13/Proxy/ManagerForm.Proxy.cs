using ToolTikTokManagerV13.Proxy;
using ToolTikTokV12.Models;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    ProxyCoordinator? _proxyCoordinator;

    ProxyCoordinator ProxyModule
        => _proxyCoordinator ??= new ProxyCoordinator(_baseDir, _log);

    IReadOnlyList<TikTokProfileEntry> GetProxyProfilesSnapshot()
        => _contexts.Values
            .Select(x => x.Profile)
            .Where(x => x is not null)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    void ShowProxyManagerDialog()
    {
        try
        {
            using var form = new ProxyManagerForm(ProxyModule, GetProxyProfilesSnapshot);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            // UI Proxy lỗi không được phép ảnh hưởng Manager chính.
            _log.Warn($"[PROXY_UI_FAIL_OPEN] detail={ex.Message}");
            MessageBox.Show(this,
                "Không mở được Quản lý Proxy. Các chức năng cũ của tool vẫn hoạt động bằng mạng hiện có.\n\n" + ex.Message,
                "Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task TryPrepareProxyBeforeChromeLaunchAsync(ProfileContext ctx)
    {
        try
        {
            var result = await ProxyModule.PrepareForLaunchAsync(
                ctx.Profile,
                GetProxyProfilesSnapshot(),
                CancellationToken.None);
            if (!result.ProxyEnabled) return;

            if (result.Applied)
            {
                _log.Info($"[PROXY_LAUNCH_READY] profile={ctx.Profile.Name} proxy={result.ProxyDisplay}");
            }
            else
            {
                // Fail-open có chủ ý: Proxy không sẵn sàng thì giữ đường chạy cũ.
                _log.Warn($"[PROXY_LAUNCH_DIRECT_FALLBACK] profile={ctx.Profile.Name} reason={result.Reason}");
            }
        }
        catch (Exception ex)
        {
            // Kể cả ProxyCoordinator không khởi tạo được, xóa marker Proxy cũ để
            // Worker không vô tình dùng lại cấu hình stale từ phiên trước.
            try { ProxyModule.TryForceDirectForProfile(ctx.Profile); } catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(ctx.Profile.ProfilePath))
                {
                    var stale = Path.Combine(ctx.Profile.ProfilePath, ProxyCoordinator.ProfileProxyFileName);
                    if (File.Exists(stale)) File.Delete(stale);
                }
            }
            catch { }
            _log.Warn($"[PROXY_BRIDGE_FAIL_OPEN] profile={ctx.Profile.Name} action=direct_network detail={ex.Message}");
        }
    }
}
