using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed partial class AutomationEngine
{
    const int CommentRestrictionPollMs = 100;
    const int CommentRestrictionRetryCooldownMs = 1000;

    bool _postEnterCommentRestrictionPending;
    string _postEnterCommentRestrictionContext = "";

    void ResetPostEnterCommentRestrictionState()
    {
        _postEnterCommentRestrictionPending = false;
        _postEnterCommentRestrictionContext = "";
    }

    /// <summary>
    /// Thay khoảng chờ thụ động 2 giây sau Enter bằng polling DOM nhẹ. Nếu TikTok chỉ hiện
    /// toast “Bạn hiện bị cấm bình luận” nhưng ô nhập vẫn editable/placeholder bình thường,
    /// đánh dấu bắt buộc chuyển LIVE và quay lại đúng điểm hiện tại để không làm mất nội dung.
    /// </summary>
    async Task<bool> WatchPostEnterCommentRestrictionAsync(string pointName, int restartStep, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (_running && !ct.IsCancellationRequested && sw.ElapsedMilliseconds < EnterReactionScanMs)
        {
            await WaitIfPausedAsync(ct);

            string marker;
            try
            {
                marker = await DetectCommentRestrictionToastAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Guard bổ sung không được làm chết workflow nếu DOM thay đổi đúng lúc polling.
                if (!IsLikelyCdpIssue(ex))
                    ReportProblem("COMMENT_RESTRICTION_CHECK_FAILED", pointName,
                        "Không kiểm tra được toast cấm bình luận: " + ex.Message, throttleSeconds: 60);
                marker = "";
            }

            if (!string.IsNullOrWhiteSpace(marker))
            {
                _step = restartStep;
                _postEnterCommentRestrictionPending = true;
                _postEnterCommentRestrictionContext = pointName;

                _log.Warn($"[COMMENT_RESTRICTION_DETECTED] point={pointName} marker={marker} action=SWITCH_LIVE restartStep={restartStep}");
                ReportProblem("COMMENT_RESTRICTION_DETECTED", pointName,
                    "TikTok báo ‘Bạn hiện bị cấm bình luận’. Khóa workflow và chuyển LIVE; nội dung hiện tại sẽ được thử lại ở LIVE mới.",
                    throttleSeconds: 5);
                SetStatus("BỊ CẤM BÌNH LUẬN", $"{pointName}: đã phát hiện toast → chuyển LIVE.");
                return true;
            }

            var remaining = EnterReactionScanMs - (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            if (remaining <= 0) break;
            await Task.Delay(Math.Min(CommentRestrictionPollMs, remaining), ct);
        }

        return false;
    }

    /// <summary>
    /// Chạy ở ranh giới vòng chính trước Viewer/InputGuard. Khi pending=true, tuyệt đối không
    /// cho workflow quay lại Click/Dán/Enter trên LIVE cũ. Nếu chuyển LIVE chưa thành công thì
    /// giữ pending và thử lại ở vòng kế tiếp thay vì fail-open.
    /// </summary>
    async Task<bool> HandlePendingPostEnterCommentRestrictionAsync(CancellationToken ct)
    {
        if (!_postEnterCommentRestrictionPending) return false;

        await WaitIfPausedAsync(ct);
        var pointName = string.IsNullOrWhiteSpace(_postEnterCommentRestrictionContext)
            ? "sau Enter"
            : _postEnterCommentRestrictionContext;
        var source = $"cấm bình luận sau Enter tại {pointName}";

        SetStatus("ĐANG RỜI LIVE BỊ CẤM BÌNH LUẬN", pointName);
        _log.Warn($"[COMMENT_RESTRICTION_SWITCH] point={pointName} action={(_s.UseArrowDownForLiveSwitch ? "ArrowDown" : "ClickXPath")}");

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
            _postEnterCommentRestrictionPending = false;
            _postEnterCommentRestrictionContext = "";
            _log.Warn($"[COMMENT_RESTRICTION_SWITCHED] point={pointName} result=OK action=RECHECK_VIEWER_AND_INPUT_GUARD");
            SetStatus("ĐÃ CHUYỂN LIVE", "Sẽ kiểm tra Viewer Gate + InputGuard trước khi thử lại nội dung.");
            return true;
        }

        // Không cho workflow chạy trên cùng LIVE khi đã biết Enter bị chặn.
        // Giữ pending và thử lại nhẹ ở vòng chính tiếp theo.
        _log.Warn($"[COMMENT_RESTRICTION_SWITCH_PENDING] point={pointName} result=NOT_CONFIRMED waitMs={CommentRestrictionRetryCooldownMs} action=KEEP_WORKFLOW_LOCKED");
        await Task.Delay(CommentRestrictionRetryCooldownMs, ct);
        return true;
    }

    /// <summary>
    /// Phát hiện toast cấm bình luận bằng DOM/CDP, không ảnh/OCR. Ưu tiên role=alert/status,
    /// aria-live và các overlay nhỏ; fallback chỉ chạy khi body thực sự chứa marker. Điều kiện
    /// hình học giúp tránh nhầm câu tương tự trong khung chat.
    /// </summary>
    async Task<string> DetectCommentRestrictionToastAsync(CancellationToken ct)
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

  const phrase = 'ban hien bi cam binh luan';
  const loose = 'hien bi cam binh luan';
  const bodyRaw = document.body?.textContent || '';
  const body = norm(bodyRaw);
  if (!body.includes(phrase) && !body.includes(loose)) return '';

  const visible = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const r = el.getBoundingClientRect?.();
    if (!r || r.width < 20 || r.height < 8 || r.bottom <= 0 || r.right <= 0 || r.top >= innerHeight || r.left >= innerWidth) return false;
    const s = getComputedStyle(el);
    return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity || 1) > 0.05;
  };

  const matchesPhrase = (el) => {
    const text = norm(el?.innerText || el?.textContent || '');
    return text.length > 0 && text.length <= 180 && (text.includes(phrase) || text.includes(loose));
  };

  const candidates = new Set();
  for (const el of document.querySelectorAll('[role="alert"],[role="status"],[aria-live]:not([aria-live="off"]),[class*="toast" i],[class*="notice" i],[class*="snackbar" i]')) {
    candidates.add(el);
  }

  // Fallback cho TikTok class obfuscated: chỉ chạy khi body đã có đúng marker.
  for (const el of document.querySelectorAll('div,span,p')) {
    if (matchesPhrase(el)) candidates.add(el);
  }

  for (const el of candidates) {
    if (!visible(el) || !matchesPhrase(el)) continue;

    const text = norm(el.innerText || el.textContent || '');
    let anchor = el;
    let positioned = false;
    for (let i = 0; i < 5 && anchor; i++, anchor = anchor.parentElement) {
      if (!visible(anchor)) continue;
      const s = getComputedStyle(anchor);
      const pos = (s.position || '').toLowerCase();
      if (pos === 'fixed' || pos === 'absolute' || pos === 'sticky') {
        positioned = true;
        break;
      }
      if (anchor.getAttribute?.('role') === 'alert' || anchor.getAttribute?.('role') === 'status' || anchor.hasAttribute?.('aria-live')) {
        positioned = true;
        break;
      }
    }

    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const inToastZone = r.top >= 0 && r.top <= innerHeight * 0.55 && cx >= innerWidth * 0.12 && cx <= innerWidth * 0.88;
    const compact = r.height <= 180 && r.width <= Math.max(900, innerWidth * 0.75);

    // Toast TikTok thường là overlay/aria-live ở nửa trên màn hình. Không nhận text dài
    // hoặc message nằm sâu ở sidebar chat để giảm false-positive.
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
}
