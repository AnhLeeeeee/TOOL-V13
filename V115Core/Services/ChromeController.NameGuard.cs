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
        var hrefDeadline = DateTime.UtcNow.AddSeconds(6);
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
            return new TikTokFastNameProbeResult(false, "", false, username, "href", "Không tìm thấy href trang Hồ sơ TikTok sau 6 giây.");

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

        // 3) TikTok là SPA: URL/DOM có thể xuất hiện trước khi dữ liệu hồ sơ mới đồng bộ.
        // Chờ ngẫu nhiên 3-5 giây trước mỗi lần đọc Name Guard để tránh đọc snapshot cũ
        // ngay sau khi vừa vào/load lại trang Hồ sơ. Ưu tiên chậm nhưng chắc.
        var profileSettleDelayMs = Random.Shared.Next(3000, 5001);
        _log.Info($"[NAME_GUARD_PROFILE_SETTLE_WAIT] delayMs={profileSettleDelayMs} href={profileHref}");
        await Task.Delay(profileSettleDelayMs, ct);

        // 4) Poll tên trên trang Hồ sơ sau khoảng settle ở trên. Không F5.
        // Một lần đọc thấy tên SAI chưa đủ để kết luận: TikTok SPA có thể giữ user-title
        // của snapshot cũ trong lúc hydrate/re-render. Tên ĐÚNG thì có thể accept ngay,
        // còn tên SAI phải xuất hiện ổn định ít nhất 2 lần cách nhau >= 1.2 giây.
        string currentHandle = "";
        string lastSeenName = "";
        string lastSource = "";
        string stableMismatchName = "";
        var stableMismatchCount = 0;
        var nameDeadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < nameDeadline)
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
                    var observedName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                    var observedHandle = root.TryGetProperty("handle", out var hp) ? (hp.GetString() ?? "").Trim().TrimStart('@') : "";
                    var source = root.TryGetProperty("source", out var sp) ? (sp.GetString() ?? "").Trim() : "";

                    if (observedHandle.Length > 0)
                        currentHandle = observedHandle;
                    if (source.Length > 0)
                        lastSource = source;

                    // Nếu trang đang hiện handle khác tài khoản Manager đang kiểm tra thì
                    // tuyệt đối không dùng tên trên trang đó để đổi/đóng. Đây là trạng thái
                    // chuyển account/SPA chưa ổn định -> trả lỗi tạm thời để giữ Chrome mở.
                    if (username.Length > 0 && currentHandle.Length > 0
                        && !currentHandle.Equals(username, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn($"[NAME_GUARD_PROFILE_HANDLE_MISMATCH] expected={username} actual={currentHandle} href={profileHref}");
                        return new TikTokFastNameProbeResult(
                            false,
                            observedName,
                            false,
                            currentHandle,
                            "profile-handle-mismatch",
                            $"Trang Hồ sơ đang hiển thị @{currentHandle}, chưa phải tài khoản @{username}. Chờ đồng bộ rồi kiểm tra lại.");
                    }

                    if (observedName.Length > 0)
                    {
                        lastSeenName = observedName;
                        var normalized = NormalizeNameGuardText(observedName);
                        var matched = allowed.Any(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));

                        if (matched)
                        {
                            _log.Info($"[NAME_GUARD_PROFILE_NAME_CONFIRMED_OK] currentName={observedName} matched=True href={profileHref} source={source}");
                            return new TikTokFastNameProbeResult(true, observedName, true, currentHandle, source, "");
                        }

                        if (stableMismatchName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                            stableMismatchCount++;
                        else
                        {
                            stableMismatchName = normalized;
                            stableMismatchCount = 1;
                        }

                        _log.Info(
                            $"[NAME_GUARD_PROFILE_NAME_MISMATCH_OBSERVED] currentName={observedName} " +
                            $"stableCount={stableMismatchCount}/2 href={profileHref} source={source}");

                        if (stableMismatchCount >= 2)
                        {
                            _log.Info($"[NAME_GUARD_PROFILE_NAME_MISMATCH_CONFIRMED] currentName={observedName} href={profileHref} source={source}");
                            return new TikTokFastNameProbeResult(true, observedName, false, currentHandle, source, "");
                        }

                        // Không đọc lại ngay cùng một frame DOM; cho TikTok đủ thời gian
                        // hydrate/re-render trước quan sát xác nhận thứ hai.
                        await Task.Delay(1200, ct);
                        continue;
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
            lastSeenName,
            false,
            currentHandle,
            string.IsNullOrWhiteSpace(lastSource) ? "profile-dom" : "profile-dom-unstable",
            lastSeenName.Length == 0
                ? "Đã vào trang Hồ sơ nhưng không đọc được tên sau 6 giây."
                : $"Tên trên Hồ sơ chưa ổn định đủ để kết luận sai sau 6 giây (đã thấy '{lastSeenName}').");
    }

    static string NormalizeNameGuardText(string value)
    {
        var normalized = (value ?? "").Normalize().Trim();
        normalized = Regex.Replace(normalized, @"[\u200B-\u200D\uFEFF]", "");
        return Regex.Replace(normalized, @"\s+", " ");
    }
}
