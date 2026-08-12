using System.Text.Json;
using ToolTikTokV11.Models;
using ToolTikTokV11.Utils;

namespace ToolTikTokV11.Services;

/// <summary>
/// Đọc trạng thái ô nhập trực tiếp từ DOM qua CDP. Không screenshot, không OCR,
/// không image matching. Class này chỉ chịu trách nhiệm phát hiện; AutomationEngine
/// vẫn sở hữu toàn bộ logic chuyển LIVE/recovery để dễ nâng cấp và tránh duplicate flow.
/// </summary>
public sealed class ChatInputGuard
{
    public sealed record Snapshot(
        bool Exists,
        bool Visible,
        bool Editable,
        bool Disabled,
        bool Empty,
        bool HasExpectedPlaceholder,
        string Placeholder,
        string Text,
        string Reason)
    {
        public bool IsNormal => Exists && Visible && Editable && !Disabled && Empty && HasExpectedPlaceholder;
    }

    readonly ChromeController _chrome;
    readonly Logger _log;

    public ChatInputGuard(ChromeController chrome, Logger log)
    {
        _chrome = chrome;
        _log = log;
    }

    public async Task<Snapshot> ProbeAsync(string xpath, string expectedPlaceholder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xpath))
            return new Snapshot(false, false, false, false, true, false, "", "", "XPath ô nhập đang trống");

        var xp = JsonSerializer.Serialize(xpath);
        var expected = JsonSerializer.Serialize((expectedPlaceholder ?? "").Trim());
        var js = $$"""
(() => {
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const expected = normalize({{expected}}).toLocaleLowerCase();
  let root = null;
  try {
    root = document.evaluate({{xp}}, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
  } catch (_) {
    return { exists:false, visible:false, editable:false, disabled:false, empty:true, hasExpectedPlaceholder:false, placeholder:'', text:'', reason:'XPath không hợp lệ' };
  }
  if (!root || root.nodeType !== 1) {
    return { exists:false, visible:false, editable:false, disabled:false, empty:true, hasExpectedPlaceholder:false, placeholder:'', text:'', reason:'Không tìm thấy ô nhập' };
  }

  const editorSelector = 'textarea,input,[contenteditable="true"],[contenteditable=""],[role="textbox"]';
  const editor = root.matches?.(editorSelector) ? root : (root.querySelector?.(editorSelector) || root);
  const rect = editor.getBoundingClientRect?.() || root.getBoundingClientRect();
  const style = getComputedStyle(editor);
  const visible = !!rect && rect.width >= 2 && rect.height >= 2 && style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity || 1) > 0.05;

  const disabled = !!editor.disabled || editor.getAttribute?.('disabled') !== null || editor.getAttribute?.('aria-disabled') === 'true';
  const editable = !disabled && (
    editor.isContentEditable === true ||
    /^(INPUT|TEXTAREA)$/.test(editor.tagName || '') ||
    (editor.getAttribute?.('role') || '').toLowerCase() === 'textbox' ||
    editor.getAttribute?.('contenteditable') === 'true' ||
    editor.getAttribute?.('contenteditable') === ''
  );

  const placeholderCandidates = [];
  const collect = el => {
    if (!el || el.nodeType !== 1) return;
    for (const a of ['placeholder','data-placeholder','aria-placeholder','data-placeholder-text']) {
      const v = normalize(el.getAttribute?.(a));
      if (v) placeholderCandidates.push(v);
    }
    try {
      for (const pseudo of ['::before','::after']) {
        const content = normalize(getComputedStyle(el, pseudo)?.content).replace(/^['\"]|['\"]$/g, '');
        if (content && content !== 'none' && content !== 'normal') placeholderCandidates.push(content);
      }
    } catch (_) {}
  };
  collect(root);
  if (editor !== root) collect(editor);
  for (const el of root.querySelectorAll?.('[placeholder],[data-placeholder],[aria-placeholder],[data-placeholder-text]') || []) collect(el);

  // Một số layout TikTok render placeholder thành node con thay vì attribute/pseudo-element.
  // Chỉ dùng các node nhỏ/hiển thị để tránh quét cả nội dung container.
  if (expected) {
    for (const el of root.querySelectorAll?.('span,div,p') || []) {
      const t = normalize(el.innerText || el.textContent || '');
      if (!t || t.length > 80 || !t.toLocaleLowerCase().includes(expected)) continue;
      const r = el.getBoundingClientRect?.();
      if (!r || r.width < 1 || r.height < 1) continue;
      const s = getComputedStyle(el);
      if (s.display === 'none' || s.visibility === 'hidden' || Number(s.opacity || 1) <= 0.05) continue;
      placeholderCandidates.push(t);
    }
  }

  let text = '';
  if (/^(INPUT|TEXTAREA)$/.test(editor.tagName || '')) text = normalize(editor.value);
  else text = normalize(editor.innerText || editor.textContent || '');

  const hasExpectedPlaceholder = !!expected && placeholderCandidates.some(x => normalize(x).toLocaleLowerCase().includes(expected));
  // Nếu placeholder được render thành text node trong contenteditable thì không coi nó là nội dung người dùng.
  const textLooksLikePlaceholder = !!expected && text.toLocaleLowerCase().includes(expected) && hasExpectedPlaceholder && text.length <= 80;
  const empty = !text || textLooksLikePlaceholder;
  const placeholder = placeholderCandidates.find(x => expected && normalize(x).toLocaleLowerCase().includes(expected)) || placeholderCandidates[0] || '';

  let reason = 'Bình thường';
  if (!visible) reason = 'Ô nhập không hiển thị';
  else if (disabled) reason = 'Ô nhập bị disabled';
  else if (!editable) reason = 'Ô nhập không còn editable';
  else if (!empty) reason = 'Ô nhập có nội dung ngoài workflow';
  else if (!hasExpectedPlaceholder) reason = 'Mất placeholder bình thường';

  return { exists:true, visible, editable, disabled, empty, hasExpectedPlaceholder, placeholder, text, reason };
})()
""";

        var result = await _chrome.EvalAsync(js, ct: ct);
        if (!result.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object)
            return new Snapshot(false, false, false, false, true, false, "", "", "CDP không trả trạng thái ô nhập");

        static bool B(JsonElement o, string name, bool fallback = false)
            => o.TryGetProperty(name, out var x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : fallback;
        static string S(JsonElement o, string name)
            => o.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

        var snapshot = new Snapshot(
            B(v, "exists"),
            B(v, "visible"),
            B(v, "editable"),
            B(v, "disabled"),
            B(v, "empty", true),
            B(v, "hasExpectedPlaceholder"),
            S(v, "placeholder"),
            S(v, "text"),
            S(v, "reason"));

        return snapshot;
    }

    public async Task<(bool normal, Snapshot snapshot)> ConfirmNormalAsync(
        string xpath,
        InputGuardSettings settings,
        CancellationToken ct = default)
    {
        var reads = Math.Clamp(settings.ConfirmReads, 1, 5);
        var delay = Math.Clamp(settings.ConfirmDelayMs, 0, 1000);
        Snapshot? last = null;

        for (var i = 1; i <= reads; i++)
        {
            last = await ProbeAsync(xpath, settings.NormalPlaceholderText, ct);
            if (last.IsNormal) return (true, last);
            if (i < reads && delay > 0) await Task.Delay(delay, ct);
        }

        last ??= new Snapshot(false, false, false, false, true, false, "", "", "Không đọc được trạng thái");
        _log.Warn($"[INPUT_GUARD] abnormal reason={last.Reason} exists={last.Exists} visible={last.Visible} editable={last.Editable} disabled={last.Disabled} empty={last.Empty} placeholder=\"{Trim(last.Placeholder)}\" text=\"{Trim(last.Text)}\"");
        return (false, last);
    }

    static string Trim(string value, int max = 80)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }
}
