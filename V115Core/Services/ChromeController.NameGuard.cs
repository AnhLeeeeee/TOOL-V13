using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolTikTokV11.Services;

public sealed record TikTokFastNameProbeResult(
    bool Ok,
    string CurrentName,
    bool Matched,
    string CurrentHandle,
    string Source,
    string Message);

public sealed partial class ChromeController
{
    public async Task<TikTokFastNameProbeResult> ProbeCurrentAccountDisplayNameAsync(
        string? username,
        IReadOnlyCollection<string>? allowedDisplayNames,
        CancellationToken ct = default)
    {
        username = (username ?? "").Trim().TrimStart('@');
        var allowed = (allowedDisplayNames ?? Array.Empty<string>())
            .Select(NormalizeNameGuardText)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allowed.Length == 0)
            return new TikTokFastNameProbeResult(false, "", false, username, "", "Danh sách tên cấu hình đang trống.");
        if (!Connected)
            return new TikTokFastNameProbeResult(false, "", false, username, "", "Chrome chưa kết nối CDP.");

        static string ReadString(JsonElement result)
            => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        // 1) Chờ đúng href Hồ sơ trong sidebar. Poll theo điều kiện thay vì sleep cứng.
        string profileHref = "";
        var hrefDeadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < hrefDeadline && string.IsNullOrWhiteSpace(profileHref))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hrefResult = await EvalAsync("""
(() => {
  const candidates = [
    document.querySelector('a[data-e2e="nav-profile"]'),
    document.querySelector('[data-e2e="nav-profile"] a')
  ].filter(Boolean);
  for (const a of candidates) {
    const href = a.href || a.getAttribute?.('href') || '';
    if (href && href.includes('/@')) return href;
  }

  // Fallback: tìm link /@... gần khu điều hướng Hồ sơ/Profile.
  const norm = s => String(s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  for (const a of document.querySelectorAll('a[href*="/@"]')) {
    const href = a.href || a.getAttribute?.('href') || '';
    if (!href) continue;
    const text = norm(`${a.innerText || a.textContent || ''} ${a.getAttribute?.('aria-label') || ''} ${a.getAttribute?.('data-e2e') || ''}`);
    if (text === 'profile' || text === 'hồ sơ' || text.includes('nav-profile') || a.closest?.('[data-e2e="nav-profile"]')) return href;
  }
  return '';
})()
""", ct: ct);
                profileHref = ReadString(hrefResult).Trim();
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }

            if (string.IsNullOrWhiteSpace(profileHref))
                await Task.Delay(250, ct);
        }

        if (string.IsNullOrWhiteSpace(profileHref))
            return new TikTokFastNameProbeResult(false, "", false, username, "href", "Không tìm thấy href trang Hồ sơ TikTok sau 4 giây.");

        // 2) Đi vào chính href Hồ sơ. Nếu đã ở đúng URL thì không điều hướng lại.
        try
        {
            var currentUrl = Page?.Url ?? "";
            if (!string.Equals(currentUrl.TrimEnd('/'), profileHref.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"[NAME_GUARD_PROFILE_HREF_NAV] href={profileHref}");
                await NavigateAndWaitAsync(profileHref, 250, 7000, ct);
            }
        }
        catch (Exception ex)
        {
            return new TikTokFastNameProbeResult(false, "", false, username, "href", "Không vào được trang Hồ sơ: " + ex.Message);
        }

        // 3) Poll tên ngay trên trang Hồ sơ. Không F5. Có tên là trả kết quả ngay.
        string currentName = "";
        string currentHandle = username;
        var nameDeadline = DateTime.UtcNow.AddSeconds(3.5);
        while (DateTime.UtcNow < nameDeadline && string.IsNullOrWhiteSpace(currentName))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await EvalAsync("""
(() => {
  const norm = s => String(s || '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  let handle = '';
  try {
    const m = location.pathname.match(/^\/@([^/?#]+)/i);
    if (m) handle = decodeURIComponent(m[1] || '').replace(/^@/, '');
  } catch {}

  const selectors = [
    '[data-e2e="user-title"]'
  ];
  for (const selector of selectors) {
    const el = document.querySelector(selector);
    if (!visible(el)) continue;
    const text = norm(el.innerText || el.textContent || '');
    if (text) return JSON.stringify({ name: text, handle, source: selector });
  }

  // Không fallback sang h1/h2 khác. TikTok có nhiều heading trên profile và
  // đọc nhầm sẽ khiến Name Guard kết luận sai tên. Chỉ user-title là nguồn tên.
  return JSON.stringify({ name: '', handle, source: '' });
})()
""", ct: ct);

                var json = ReadString(result);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    currentName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                    var h = root.TryGetProperty("handle", out var hp) ? (hp.GetString() ?? "").Trim() : "";
                    if (h.Length > 0) currentHandle = h;
                    var source = root.TryGetProperty("source", out var sp) ? (sp.GetString() ?? "").Trim() : "";
                    if (currentName.Length > 0)
                    {
                        var normalized = NormalizeNameGuardText(currentName);
                        var matched = allowed.Any(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                        _log.Info($"[NAME_GUARD_PROFILE_NAME] currentName={currentName} matched={matched} href={profileHref} source={source}");
                        return new TikTokFastNameProbeResult(true, currentName, matched, currentHandle, source, "");
                    }
                }
            }
            catch (Exception ex) when (IsTransientDocumentContextError(ex)) { }
            catch (Exception ex)
            {
                _log.Warn("[NAME_GUARD_PROFILE_NAME_WARN] " + ex.Message);
            }

            await Task.Delay(250, ct);
        }

        return new TikTokFastNameProbeResult(
            false,
            "",
            false,
            currentHandle,
            "profile-dom",
            "Đã vào trang Hồ sơ nhưng không đọc được tên sau 3.5 giây.");
    }

    static string NormalizeNameGuardText(string value)
        => Regex.Replace((value ?? "").Trim(), @"\s+", " ");
}
