using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    // Không xác nhận mất đăng nhập chỉ từ một lần popup. TikTok có thể rơi vào trạng thái
    // tạm thời; chỉ khi popup login quay lại sau nhiều lần Click/Dán/Enter liên tiếp mới
    // giao quyền xử lý cho luồng runtime-login recovery có sẵn của Manager.
    const int RuntimeLoginModalConfirmationsRequired = 2;
    const int RuntimeLoginSuspectTransitionRetryMs = 1000;

    int _runtimeLoginModalStreak;
    bool _runtimeLoginSuspectTransitionPending;
    string _runtimeLoginSuspectContext = "";
    string _runtimeLoginSuspectMarker = "";

    void ResetRuntimeLoginModalState(string reason)
    {
        ResetRuntimeLoginModalConfirmation(reason);
        _runtimeLoginSuspectTransitionPending = false;
        _runtimeLoginSuspectContext = "";
        _runtimeLoginSuspectMarker = "";
    }

    void ResetRuntimeLoginModalConfirmation(string reason)
    {
        if (_runtimeLoginModalStreak > 0)
        {
            _log.Info($"[RUNTIME_LOGIN_MODAL_STREAK_RESET] previous={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} reason={reason}");
        }

        _runtimeLoginModalStreak = 0;
    }

    /// <summary>
    /// Một DOM probe dùng ngay sau Enter.
    /// - LOGIN_MODAL: chỉ khi thấy popup/dialog có tiêu đề “Đăng nhập vào TikTok” / “Log in to TikTok”.
    /// - COMMENT_BANNED: vẫn giữ detector toast cấm bình luận hiện tại.
    /// Không dùng toast “Vui lòng đăng nhập trước” để xác nhận mất login nữa.
    /// </summary>
    async Task<string> DetectPostEnterReactionAsync(CancellationToken ct)
    {
        const string js = """
(() => {
  const norm = (value) => String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/đ/g, 'd')
    .replace(/\s+/g, ' ')
    .trim();

  const visible = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const r = el.getBoundingClientRect?.();
    if (!r || r.width < 20 || r.height < 8 || r.bottom <= 0 || r.right <= 0 || r.top >= innerHeight || r.left >= innerWidth) return false;
    const s = getComputedStyle(el);
    return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity || 1) > 0.05;
  };

  const loginModalText = (text) => {
    const t = norm(text);
    return t.includes('dang nhap vao tiktok') || t.includes('log in to tiktok');
  };

  // 1) Ưu tiên semantic dialog/aria-modal. Popup login có thể chứa rất nhiều nút,
  // vì vậy không giới hạn text <= 180 như detector toast.
  for (const el of document.querySelectorAll('[role="dialog"],[aria-modal="true"]')) {
    if (!visible(el)) continue;
    const text = norm(el.innerText || el.textContent || '');
    if (!loginModalText(text)) continue;
    const r = el.getBoundingClientRect();
    return `LOGIN_MODAL|text=${text.slice(0,140)}|x=${Math.round(r.left)}|y=${Math.round(r.top)}|w=${Math.round(r.width)}|h=${Math.round(r.height)}`;
  }

  // 2) Fallback cho class TikTok obfuscated: tìm đúng heading “Đăng nhập vào TikTok”,
  // rồi yêu cầu một ancestor dạng overlay/dialog/fixed để không nhầm nút Đăng nhập sidebar.
  for (const el of document.querySelectorAll('h1,h2,h3,h4,div,span,p')) {
    if (!visible(el)) continue;
    const own = norm(el.innerText || el.textContent || '');
    if (own.length === 0 || own.length > 80 || !loginModalText(own)) continue;

    let anchor = el;
    for (let i = 0; i < 8 && anchor; i++, anchor = anchor.parentElement) {
      if (!visible(anchor)) continue;
      const r = anchor.getBoundingClientRect();
      const s = getComputedStyle(anchor);
      const role = String(anchor.getAttribute?.('role') || '').toLowerCase();
      const ariaModal = String(anchor.getAttribute?.('aria-modal') || '').toLowerCase();
      const positioned = ['fixed','absolute','sticky'].includes(String(s.position || '').toLowerCase());
      const modalLike = role === 'dialog' || ariaModal === 'true' || positioned;
      const largeEnough = r.width >= 280 && r.height >= 180;
      const nearCenter = (r.left + r.width / 2) >= innerWidth * 0.20
        && (r.left + r.width / 2) <= innerWidth * 0.80;
      if (modalLike && largeEnough && nearCenter) {
        return `LOGIN_MODAL|text=${own.slice(0,80)}|x=${Math.round(r.left)}|y=${Math.round(r.top)}|w=${Math.round(r.width)}|h=${Math.round(r.height)}`;
      }
    }
  }

  // 3) Detector cấm bình luận cũ: chỉ áp dụng cho toast/notice nhỏ.
  const commentPhrase = 'ban hien bi cam binh luan';
  const commentLoose = 'hien bi cam binh luan';
  const classifyComment = (text) => {
    const t = norm(text);
    return t.includes(commentPhrase) || t.includes(commentLoose);
  };

  const body = norm(document.body?.textContent || '');
  if (!body.includes(commentPhrase) && !body.includes(commentLoose)) return '';

  const candidates = new Set();
  for (const el of document.querySelectorAll('[role="alert"],[role="status"],[aria-live]:not([aria-live="off"]),[class*="toast" i],[class*="notice" i],[class*="snackbar" i]')) {
    candidates.add(el);
  }

  for (const el of document.querySelectorAll('div,span,p')) {
    const text = norm(el.innerText || el.textContent || '');
    if (text.length > 0 && text.length <= 180 && classifyComment(text)) candidates.add(el);
  }

  for (const el of candidates) {
    if (!visible(el)) continue;
    const text = norm(el.innerText || el.textContent || '');
    if (!classifyComment(text) || text.length > 180) continue;

    let anchor = el;
    let positioned = false;
    for (let i = 0; i < 5 && anchor; i++, anchor = anchor.parentElement) {
      if (!visible(anchor)) continue;
      const s = getComputedStyle(anchor);
      const pos = (s.position || '').toLowerCase();
      if (pos === 'fixed' || pos === 'absolute' || pos === 'sticky'
          || anchor.getAttribute?.('role') === 'alert'
          || anchor.getAttribute?.('role') === 'status'
          || anchor.hasAttribute?.('aria-live')) {
        positioned = true;
        break;
      }
    }

    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const inToastZone = r.top >= 0 && r.top <= innerHeight * 0.55
      && cx >= innerWidth * 0.12 && cx <= innerWidth * 0.88;
    const compact = r.height <= 180 && r.width <= Math.max(900, innerWidth * 0.75);
    if ((positioned || inToastZone) && compact) {
      return `COMMENT_BANNED|text=${text.slice(0,120)}|x=${Math.round(r.left)}|y=${Math.round(r.top)}`;
    }
  }

  return '';
})()
""";

        var r = await _chrome.EvalAsync(js, ct: ct);
        return r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";
    }

    /// <summary>
    /// Mỗi lần Enter chỉ được cộng đúng 1 xác nhận. Popup biến mất do chuyển LIVE/F5 không
    /// được tính là hồi login và cũng không reset streak. Chỉ một lần Enter kế tiếp chạy qua
    /// hết cửa sổ scan mà KHÔNG thấy LOGIN_MODAL mới reset streak về 0.
    /// </summary>
    void RegisterRuntimeLoginModalAfterEnter(string pointName, int restartStep, string marker)
    {
        _step = restartStep;
        _runtimeLoginModalStreak = Math.Min(
            RuntimeLoginModalConfirmationsRequired,
            _runtimeLoginModalStreak + 1);

        _log.Warn(
            $"[RUNTIME_LOGIN_MODAL_CONFIRM] point={pointName} confirmation={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} marker={marker}");

        if (_runtimeLoginModalStreak >= RuntimeLoginModalConfirmationsRequired)
        {
            _runtimeLoginSuspectTransitionPending = false;
            _runtimeLoginSuspectContext = "";
            _runtimeLoginSuspectMarker = "";

            var confirmedMarker = $"{marker}; repeated={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired}";
            LatchRuntimeLoginLost(pointName, confirmedMarker);
            Stop($"Popup Đăng nhập vào TikTok lặp lại {_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} lần sau Enter; chờ Manager gọi luồng đăng nhập có sẵn.");
            return;
        }

        _runtimeLoginSuspectTransitionPending = true;
        _runtimeLoginSuspectContext = pointName;
        _runtimeLoginSuspectMarker = marker;

        SetStatus(
            "NGHI MẤT ĐĂNG NHẬP",
            $"{pointName}: popup login {_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired}; chuyển LIVE rồi thử gửi lại cùng nội dung.");
    }

    /// <summary>
    /// Sau lần popup 1/2, chuyển LIVE để modal biến mất rồi cho workflow thực hiện
    /// Click/Dán/Enter ở LIVE khác. Streak vẫn được giữ xuyên qua chuyển LIVE/F5.
    /// </summary>
    async Task<bool> HandlePendingRuntimeLoginSuspectTransitionAsync(CancellationToken ct)
    {
        if (!_runtimeLoginSuspectTransitionPending) return false;

        await WaitIfPausedAsync(ct);
        var pointName = string.IsNullOrWhiteSpace(_runtimeLoginSuspectContext)
            ? "sau Enter"
            : _runtimeLoginSuspectContext;
        var source = $"xác minh popup đăng nhập sau Enter {pointName} {_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired}";

        SetStatus(
            "ĐANG XÁC MINH ĐĂNG NHẬP",
            $"{pointName}: chuyển LIVE sau popup {_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired}; chưa gọi login lại.");
        _log.Warn(
            $"[RUNTIME_LOGIN_MODAL_SWITCH] point={pointName} confirmation={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} action={(_s.UseArrowDownForLiveSwitch ? "ArrowDown" : "ClickXPath")}");

        var action = _s.UseArrowDownForLiveSwitch ? TransitionAction.ArrowDown : TransitionAction.ClickXPath;
        var transitioned = await TransitionAsync(
            source,
            action,
            _s.XPathPeriodicAction,
            1,
            scheduledPeriodic: false,
            ct,
            F5WaitMs);

        if (transitioned)
        {
            _runtimeLoginSuspectTransitionPending = false;
            _runtimeLoginSuspectContext = "";
            _runtimeLoginSuspectMarker = "";
            _log.Warn(
                $"[RUNTIME_LOGIN_MODAL_SWITCHED] point={pointName} confirmation={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} result=OK action=RETRY_SAME_CONTENT_AFTER_VIEWER_AND_INPUT_GUARD");
            SetStatus(
                "ĐANG XÁC MINH ĐĂNG NHẬP",
                $"Đã chuyển LIVE; chờ Click/Dán/Enter kế tiếp để xác minh popup {_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired}.");
            return true;
        }

        _log.Warn(
            $"[RUNTIME_LOGIN_MODAL_SWITCH_PENDING] point={pointName} confirmation={_runtimeLoginModalStreak}/{RuntimeLoginModalConfirmationsRequired} result=NOT_CONFIRMED waitMs={RuntimeLoginSuspectTransitionRetryMs} action=KEEP_WORKFLOW_LOCKED");
        await Task.Delay(RuntimeLoginSuspectTransitionRetryMs, ct);
        return true;
    }

    void LatchRuntimeLoginLost(string pointName, string marker)
    {
        _runtimeLoginLostDetail = $"point={pointName}; marker={marker}";
        _runtimeLoginLostConfirmed = true;

        ReportProblem(
            "RUNTIME_LOGIN_LOST_CONFIRMED",
            pointName,
            $"Popup ‘Đăng nhập vào TikTok’ đã lặp lại {RuntimeLoginModalConfirmationsRequired} lần liên tiếp sau Click/Dán/Enter. Dừng Worker automation để Manager đóng sạch, mở lại chính PRF và gọi nguyên logic đăng nhập hiện tại.",
            error: true,
            throttleSeconds: 5);

        SetStatus(
            "MẤT ĐĂNG NHẬP",
            $"{pointName}: popup login lặp lại {RuntimeLoginModalConfirmationsRequired}/{RuntimeLoginModalConfirmationsRequired}; chờ Manager gọi luồng đăng nhập có sẵn.");
    }
}
