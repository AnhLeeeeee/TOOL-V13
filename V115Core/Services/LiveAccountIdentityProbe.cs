using System.Text.Json;

namespace ToolTikTokV11.Services;

/// <summary>
/// Đọc định danh tài khoản LIVE trực tiếp từ DOM theo XPath do người dùng cấu hình.
/// Ưu tiên username lấy từ href /@username; nếu XPath không nằm trực tiếp trên thẻ a
/// thì thử anchor cha/con gần nhất. Khi không có href, text của node được dùng làm
/// fallback để vẫn hỗ trợ các layout TikTok khác nhau.
/// </summary>
public sealed class LiveAccountIdentityProbe
{
    public sealed record Snapshot(
        bool Exists,
        bool Visible,
        string IdentityKey,
        string Username,
        string Href,
        string Text,
        string Reason)
    {
        public bool IsValid => Exists && !string.IsNullOrWhiteSpace(IdentityKey);
        public string DisplayName => !string.IsNullOrWhiteSpace(Username)
            ? "@" + Username
            : !string.IsNullOrWhiteSpace(Text) ? Text : Href;
    }

    readonly ChromeController _chrome;

    public LiveAccountIdentityProbe(ChromeController chrome) => _chrome = chrome;

    public async Task<Snapshot> ProbeAsync(string xpath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath))
            return new Snapshot(false, false, "", "", "", "", "XPath tài khoản LIVE đang trống.");

        var xpathJson = JsonSerializer.Serialize(xpath.Trim());
        var js = $$"""
(() => {
  try {
    const e = document.evaluate({{xpathJson}}, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
    if (!e) return { exists:false, visible:false, key:'', username:'', href:'', text:'', reason:'Không tìm thấy XPath tài khoản LIVE.' };

    const style = e.nodeType === 1 ? getComputedStyle(e) : null;
    const rect = e.nodeType === 1 && e.getBoundingClientRect ? e.getBoundingClientRect() : null;
    const visible = !!e && (!style || (style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity || 1) !== 0))
      && (!rect || (rect.width > 0 && rect.height > 0));

    const normalizeText = (value) => String(value || '').replace(/\s+/g, ' ').trim();
    const cleanHref = (value) => {
      if (!value) return '';
      try {
        const u = new URL(value, location.href);
        u.hash = '';
        return u.toString();
      } catch { return normalizeText(value); }
    };

    let anchor = null;
    if (e.nodeType === 1) {
      if (e.matches && e.matches('a[href]')) anchor = e;
      if (!anchor && e.closest) anchor = e.closest('a[href]');
      if (!anchor && e.querySelector) anchor = e.querySelector('a[href]');
      if (!anchor && e.parentElement?.querySelector) anchor = e.parentElement.querySelector('a[href*="/@"]');
    }

    const href = cleanHref(anchor?.href || anchor?.getAttribute?.('href') || '');
    let username = '';
    const hrefMatch = href.match(/\/@([^/?#]+)/i);
    if (hrefMatch) username = decodeURIComponent(hrefMatch[1]).trim();

    const text = normalizeText(e.innerText || e.textContent || e.getAttribute?.('aria-label') || e.getAttribute?.('title') || '');
    if (!username) {
      const textMatch = text.match(/@([A-Za-z0-9._]+)/);
      if (textMatch) username = textMatch[1].trim();
    }

    const normalizedUsername = username.toLocaleLowerCase('en-US');
    const normalizedHref = href.toLocaleLowerCase('en-US');
    const normalizedText = text.toLocaleLowerCase('vi-VN');
    const key = normalizedUsername
      ? `user:${normalizedUsername}`
      : normalizedHref ? `href:${normalizedHref}`
      : normalizedText ? `text:${normalizedText}`
      : '';

    return {
      exists:true,
      visible,
      key,
      username,
      href,
      text,
      reason:key ? '' : 'XPath tồn tại nhưng không đọc được username/href/text để tạo định danh LIVE.'
    };
  } catch (err) {
    return { exists:false, visible:false, key:'', username:'', href:'', text:'', reason:String(err?.message || err || 'XPath lỗi') };
  }
})()
""";

        var result = await _chrome.EvalAsync(js, ct: ct);
        if (!result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return new Snapshot(false, false, "", "", "", "", "CDP không trả về dữ liệu định danh LIVE hợp lệ.");

        static string GetString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        static bool GetBool(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var p)
               && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
               && p.GetBoolean();

        return new Snapshot(
            GetBool(value, "exists"),
            GetBool(value, "visible"),
            GetString(value, "key").Trim(),
            GetString(value, "username").Trim(),
            GetString(value, "href").Trim(),
            GetString(value, "text").Trim(),
            GetString(value, "reason").Trim());
    }
}
