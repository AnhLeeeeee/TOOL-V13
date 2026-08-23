namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    /// <summary>
    /// TikTok đôi lúc vừa đăng nhập xong nhưng SPA/profile page tạm hiển thị như chưa đăng nhập.
    /// Nếu cookie probe chưa sẵn sàng, F5 tối đa 2 lần trước khi trả về not_logged_in.
    /// </summary>
    public async Task<bool> EnsureTikTokIdentitySessionReadyAsync(
        CancellationToken ct = default)
    {
        if (!Connected)
            return false;

        try
        {
            if (await IsTikTokSessionActiveAsync(ct))
                return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn("[TIKTOK_IDENTITY_SESSION_PROBE_WARN] " + ex.Message);
        }

        var currentUrl = Page?.Url ?? "";
        if (!currentUrl.StartsWith(
                "https://www.tiktok.com/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const int maxReloads = 2;

        for (var attempt = 1; attempt <= maxReloads; attempt++)
        {
            try
            {
                _log.Warn(
                    $"[TIKTOK_IDENTITY_SESSION_RELOAD] url={currentUrl} attempt={attempt}/{maxReloads} reason=session-not-ready");

                await ReloadAndWaitAsync(
                    minWaitMs: 900,
                    timeoutMs: 15000,
                    ct);

                await Task.Delay(450, ct);

                if (await IsTikTokSessionActiveAsync(ct))
                {
                    _log.Info(
                        $"[TIKTOK_IDENTITY_SESSION_RECOVERED] attempt={attempt}/{maxReloads}");
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[TIKTOK_IDENTITY_SESSION_RELOAD_WARN] attempt={attempt}/{maxReloads} error={ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Nếu đang ở trang cá nhân /@username nhưng nút Edit profile chưa render,
    /// không chờ lâu: probe nhanh rồi F5 ngay, tối đa 2 lần.
    /// Chỉ kiểm tra nút, không click; UpdateTikTokProfileIdentityAsync vẫn là nơi click thật.
    /// </summary>
    public async Task<bool> EnsureTikTokEditProfileEntranceReadyAsync(
        CancellationToken ct = default)
    {
        if (!Connected)
            return false;

        var currentUrl = Page?.Url ?? "";
        if (!LooksLikeTikTokProfilePage(currentUrl))
            return true; // Luồng update chính sẽ tự điều hướng tới profile page.

        if (await HasTikTokEditProfileEntranceAsync(1200, ct))
            return true;

        const int maxReloads = 2;

        for (var attempt = 1; attempt <= maxReloads; attempt++)
        {
            try
            {
                _log.Warn(
                    $"[TIKTOK_IDENTITY_EDIT_QUICK_RELOAD] url={currentUrl} attempt={attempt}/{maxReloads} reason=edit-profile-missing");

                await ReloadAndWaitAsync(
                    minWaitMs: 900,
                    timeoutMs: 15000,
                    ct);

                await Task.Delay(450, ct);

                // Sau F5, session có thể vừa hồi lại.
                try
                {
                    if (!await IsTikTokSessionActiveAsync(ct))
                    {
                        _log.Warn(
                            $"[TIKTOK_IDENTITY_EDIT_RELOAD_SESSION_NOT_READY] attempt={attempt}/{maxReloads}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[TIKTOK_IDENTITY_EDIT_RELOAD_SESSION_PROBE_WARN] attempt={attempt}/{maxReloads} error={ex.Message}");
                }

                if (await HasTikTokEditProfileEntranceAsync(1800, ct))
                {
                    _log.Info(
                        $"[TIKTOK_IDENTITY_EDIT_READY_AFTER_QUICK_RELOAD] attempt={attempt}/{maxReloads}");
                    return true;
                }

                currentUrl = Page?.Url ?? currentUrl;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[TIKTOK_IDENTITY_EDIT_QUICK_RELOAD_WARN] attempt={attempt}/{maxReloads} error={ex.Message}");
            }
        }

        return false;
    }

    static bool LooksLikeTikTokProfilePage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.AbsolutePath.StartsWith("/@", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> HasTikTokEditProfileEntranceAsync(
        int timeoutMs,
        CancellationToken ct)
    {
        const string js = """
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();

  const direct = document.querySelector(
    '[data-e2e="edit-profile-entrance"],' +
    'button[data-e2e*="edit-profile"],' +
    '[role="button"][data-e2e*="edit-profile"]'
  );

  if (direct) return true;

  const all = [...document.querySelectorAll('button,[role="button"],a')];

  return all.some(el => {
    const t = norm(
      `${el.innerText || el.textContent || ''} ${el.getAttribute('aria-label') || ''}`
    );

    return t === 'edit profile'
      || t === 'chỉnh sửa hồ sơ'
      || t === 'sửa hồ sơ'
      || t === 'edit';
  });
})()
""";

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(400, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await EvalAsync(js, ct: ct);
                if (result.TryGetProperty("value", out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsTransientDocumentContextError(ex))
            {
                // F5/navigation vừa đổi execution context; chờ DOM ổn định rồi thử lại.
            }
            catch (Exception ex)
            {
                _log.Warn("[TIKTOK_IDENTITY_EDIT_PROBE_WARN] " + ex.Message);
            }

            await Task.Delay(200, ct);
        }

        return false;
    }
}
