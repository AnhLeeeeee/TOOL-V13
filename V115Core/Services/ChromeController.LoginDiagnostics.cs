using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    async Task CaptureLoginPageDiagnosticAsync(string stage, CancellationToken ct)
    {
        stage = CompactLoginDiagnostic(stage, 90);

        try
        {
            var r = await EvalAsync("""
(() => {
  const clean = x => String(x || '').replace(/\s+/g, ' ').trim();
  const norm = x => String(x || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .toLowerCase().replace(/đ/g, 'd').replace(/\s+/g, ' ').trim();
  const visible = e => {
    if (!e) return false;
    try {
      const r = e.getBoundingClientRect();
      const s = getComputedStyle(e);
      return r.width > 1 && r.height > 1
        && s.display !== 'none'
        && s.visibility !== 'hidden'
        && Number(s.opacity || 1) > 0.02;
    } catch { return false; }
  };

  const selectors = [
    '[role="alert"]',
    '[aria-live="assertive"]',
    '[aria-live="polite"]',
    '[data-e2e*="error" i]',
    '[data-testid*="error" i]',
    '[class*="error" i]',
    'form p',
    'form span'
  ];

  const texts = [];
  for (const selector of selectors) {
    for (const el of document.querySelectorAll(selector)) {
      if (!visible(el)) continue;
      const t = clean(el.innerText || el.textContent || '');
      if (!t || t.length > 280) continue;
      if (!texts.includes(t)) texts.push(t);
      if (texts.length >= 8) break;
    }
    if (texts.length >= 8) break;
  }

  const rawInnerText = String(document.body?.innerText || '');
  const rawTextContent = String(document.body?.textContent || '');
  const body = rawInnerText || rawTextContent;
  const bodyInnerNorm = norm(rawInnerText);
  const bodyTextNorm = norm(rawTextContent);

  const interesting = body
    .split(/\r?\n/)
    .map(clean)
    .filter(Boolean)
    .filter(t => /lỗi|loi|error|server|try again|thử lại|thu lai|banned|suspended|đình chỉ|dinh chi|bị cấm|bi cam|không tồn tại|khong ton tai|does not exist|doesn't exist/i.test(t))
    .slice(0, 8);

  // Diagnostic-only: dùng cùng marker với DetectLoginAccountBanAsync, nhưng ghi rõ
  // marker nằm ở element nào, element có đang hiển thị hay không, và match đến từ
  // innerText hay chỉ textContent. KHÔNG thay đổi quyết định BAN.
  const banMarkers = [
    ['VI_BANNED', 'tai khoan cua ban da bi cam'],
    ['VI_SUSPENDED', 'tai khoan cua ban da bi dinh chi'],
    ['EN_BANNED', 'your account has been banned'],
    ['EN_BANNED_WAS', 'your account was banned'],
    ['EN_SUSPENDED', 'your account has been suspended'],
    ['EN_SUSPENDED_WAS', 'your account was suspended'],
    ['VI_USER_NOT_EXIST', 'nguoi dung khong ton tai'],
    ['VI_USER_NOT_EXIST_THIS', 'nguoi dung nay khong ton tai'],
    ['EN_USER_NOT_EXIST', 'user does not exist'],
    ['EN_USER_NOT_EXIST_SHORT', "user doesn't exist"],
    ['EN_ACCOUNT_NOT_EXIST', 'account does not exist'],
    ['EN_ACCOUNT_NOT_EXIST_SHORT', "account doesn't exist"]
  ];

  const allElements = document.body ? Array.from(document.body.querySelectorAll('*')) : [];
  const banMatches = [];

  for (const [marker, phrase] of banMarkers) {
    const inInnerText = bodyInnerNorm.includes(phrase);
    const inTextContent = bodyTextNorm.includes(phrase);
    if (!inInnerText && !inTextContent) continue;

    let best = null;
    for (const el of allElements) {
      const inner = clean(el.innerText || '');
      const content = clean(el.textContent || '');
      const innerNorm = norm(inner);
      const contentNorm = norm(content);
      let source = '';
      let candidateText = '';

      if (innerNorm.includes(phrase)) {
        source = 'innerText';
        candidateText = inner;
      } else if (contentNorm.includes(phrase)) {
        source = 'textContent';
        candidateText = content;
      } else {
        continue;
      }

      // Ưu tiên element gần text nhất thay vì các parent lớn như body/main.
      const score = Math.min(candidateText.length || 999999, 999999);
      if (!best || score < best.score) {
        best = {
          score,
          marker,
          phrase,
          source,
          visible: visible(el),
          tag: String(el.tagName || '').slice(0, 40),
          id: String(el.id || '').slice(0, 100),
          cls: String(el.className || '').slice(0, 180),
          text: candidateText.slice(0, 420),
          html: clean(el.outerHTML || '').slice(0, 650)
        };
      }
    }

    banMatches.push(best || {
      marker,
      phrase,
      source: inInnerText ? 'body.innerText' : 'body.textContent',
      visible: inInnerText,
      tag: 'BODY',
      id: '',
      cls: '',
      text: '',
      html: ''
    });
  }

  const serverPatterns = [
    'loi may chu noi bo',
    'loi may chu',
    'internal server error',
    'server error',
    'please try again later',
    'vui long thu lai sau'
  ];

  const serverErrors = [];
  for (const el of allElements) {
    if (!visible(el)) continue;
    const t = clean(el.innerText || el.textContent || '');
    if (!t || t.length > 320) continue;
    const n = norm(t);
    if (!serverPatterns.some(p => n.includes(p))) continue;
    if (!serverErrors.includes(t)) serverErrors.push(t);
    if (serverErrors.length >= 6) break;
  }

  return {
    url: String(location.href || '').slice(0, 240),
    title: clean(document.title || '').slice(0, 160),
    ready: String(document.readyState || ''),
    alerts: texts,
    hints: interesting,
    banMatches,
    serverErrors
  };
})()
""", ct: ct);

            if (!r.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object)
            {
                _log.Warn($"[TIKTOK_LOGIN_PAGE_DIAG] stage={stage} result=no_object");
                return;
            }

            var url = ReadLoginDiagnosticString(value, "url", 240);
            var title = ReadLoginDiagnosticString(value, "title", 160);
            var ready = ReadLoginDiagnosticString(value, "ready", 30);
            var alerts = ReadLoginDiagnosticArray(value, "alerts", 8, 220);
            var hints = ReadLoginDiagnosticArray(value, "hints", 8, 220);
            var serverErrors = ReadLoginDiagnosticArray(value, "serverErrors", 6, 260);

            _log.Warn(
                $"[TIKTOK_LOGIN_PAGE_DIAG] stage={stage} ready={ready} url={url} title={title} alerts={alerts} hints={hints} serverErrors={serverErrors}");

            if (stage.StartsWith("ban_signal:", StringComparison.OrdinalIgnoreCase)
                && value.TryGetProperty("banMatches", out var banMatches)
                && banMatches.ValueKind == JsonValueKind.Array)
            {
                foreach (var match in banMatches.EnumerateArray().Take(8))
                {
                    if (match.ValueKind != JsonValueKind.Object)
                        continue;

                    var marker = ReadLoginDiagnosticString(match, "marker", 60);
                    var phrase = ReadLoginDiagnosticString(match, "phrase", 120);
                    var source = ReadLoginDiagnosticString(match, "source", 40);
                    var tag = ReadLoginDiagnosticString(match, "tag", 40);
                    var id = ReadLoginDiagnosticString(match, "id", 100);
                    var cls = ReadLoginDiagnosticString(match, "cls", 180);
                    var text = ReadLoginDiagnosticString(match, "text", 420);
                    var html = ReadLoginDiagnosticString(match, "html", 650);
                    var isVisible = match.TryGetProperty("visible", out var visibleValue)
                        && visibleValue.ValueKind == JsonValueKind.True;

                    _log.Warn(
                        $"[TIKTOK_LOGIN_BAN_MATCH_DETAIL] stage={stage} marker={marker} phrase={phrase} source={source} visible={isVisible} tag={tag} id={id} class={cls} text={text} html={html}");
                }

                if (serverErrors != "-")
                {
                    _log.Warn(
                        $"[TIKTOK_LOGIN_BAN_WITH_SERVER_ERROR] stage={stage} visibleServerError={serverErrors}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[TIKTOK_LOGIN_PAGE_DIAG] stage={stage} result=probe_error error={CompactLoginDiagnostic(ex.Message, 180)}");
        }
    }

    static string ReadLoginDiagnosticString(JsonElement root, string property, int max)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "-";
        }

        return CompactLoginDiagnostic(value.GetString(), max);
    }

    static string ReadLoginDiagnosticArray(JsonElement root, string property, int maxItems, int maxEach)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return "-";
        }

        var items = value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => CompactLoginDiagnostic(x.GetString(), maxEach))
            .Where(x => x.Length > 0)
            .Take(maxItems)
            .ToArray();

        return items.Length == 0 ? "-" : string.Join(" || ", items);
    }

    static string CompactLoginDiagnostic(string? text, int max)
    {
        text = (text ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        if (text.Length <= max)
            return text;

        return text[..max] + "...";
    }
}
