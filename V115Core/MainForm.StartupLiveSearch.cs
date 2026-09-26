using System.Text.Json;
using ToolTikTokV11.Models;
using ToolTikTokV11.Services;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    const string SearchTopTabXPath = "(//button[@data-testid='tux-web-tab-bar'][.//span[@data-testid='tux-web-text' and normalize-space(.)='Top']])[1]";
    const string SearchLiveTabXPath = "(//button[@data-testid='tux-web-tab-bar'][.//span[@data-testid='tux-web-text' and normalize-space(.)='LIVE']])[1]";

    const int SearchOpenStepSec = 8;
    const int SearchTopRenderStepSec = 8;
    const int SearchLiveTabStepSec = 5;
    const int SearchLiveRenderStepSec = 8;
    const int SearchCandidateOpenStepSec = 10;

    readonly CheckBox _startupLiveSearchEnabled = new()
    {
        Text = "Bật tìm LIVE bằng TikTok Search (startup + làm mới nguồn khi đang chạy)",
        AutoSize = true,
        Checked = true
    };
    readonly TextBox _startupLiveSearchKeywords = new() { Width = 460 };
    readonly NumericUpDown _startupLiveSearchKeywordTimeout = Num(10, 120);
    readonly NumericUpDown _startupLiveSearchTotalTimeout = Num(20, 300);
    readonly NumericUpDown _startupLiveSearchMaxCards = Num(3, 50);

    sealed record StartupSearchCandidate(string Href, string ViewerText, int Viewer, string Username);

    Control BuildStartupLiveSearchGroup()
    {
        var group = new GroupBox
        {
            Text = "Nguồn tìm LIVE — TikTok Search",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8)
        };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0)
        };

        panel.Controls.Add(_startupLiveSearchEnabled);

        var keywords = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
        keywords.Controls.Add(new Label { Text = "Từ khóa (; phân cách)", AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        keywords.Controls.Add(_startupLiveSearchKeywords);
        panel.Controls.Add(keywords);

        var limits = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
        AddLabeled(limits, "Timeout / từ khóa (giây)", _startupLiveSearchKeywordTimeout);
        AddLabeled(limits, "Timeout toàn Search (giây)", _startupLiveSearchTotalTimeout);
        AddLabeled(limits, "Card tối đa", _startupLiveSearchMaxCards);
        panel.Controls.Add(limits);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            Margin = new Padding(4, 5, 4, 2),
            Text = "Startup: nếu đang ở /@user/live thì giữ LIVE hiện tại; nếu chưa ở LIVE thì Search theo từ khóa. Khi đang chạy, cứ đủ 2 LIVE thấp sẽ tính 1 lượt đổi nguồn; lượt 1–4 ưu tiên sidebar, sidebar trống thì Search; đủ 5 lượt sẽ bắt buộc Search để lấy đầu vào chuỗi mới. " +
                   "Chỉ badge SVG hình người mới được tính Viewer; badge Like bị bỏ qua. Mỗi bước Search có deadline nên không thể treo vô hạn. Ngưỡng dùng chung Viewer Gate hiện tại."
        });

        group.Controls.Add(panel);
        return group;
    }

    void LoadStartupLiveSearchToUi()
    {
        var cfg = _settings.StartupLiveSearch ?? new StartupLiveSearchSettings();
        _startupLiveSearchEnabled.Checked = cfg.Enabled;
        _startupLiveSearchKeywords.Text = cfg.Keywords ?? "";
        _startupLiveSearchKeywordTimeout.Value = Clamp(cfg.KeywordTimeoutSec, _startupLiveSearchKeywordTimeout);
        _startupLiveSearchTotalTimeout.Value = Clamp(cfg.TotalTimeoutSec, _startupLiveSearchTotalTimeout);
        _startupLiveSearchMaxCards.Value = Clamp(cfg.MaxCards, _startupLiveSearchMaxCards);
    }

    void SaveStartupLiveSearchFromUi()
    {
        _settings.StartupLiveSearch ??= new StartupLiveSearchSettings();
        _settings.StartupLiveSearch.Enabled = _startupLiveSearchEnabled.Checked;
        _settings.StartupLiveSearch.Keywords = _startupLiveSearchKeywords.Text.Trim();
        _settings.StartupLiveSearch.KeywordTimeoutSec = (int)_startupLiveSearchKeywordTimeout.Value;
        _settings.StartupLiveSearch.TotalTimeoutSec = (int)_startupLiveSearchTotalTimeout.Value;
        _settings.StartupLiveSearch.MaxCards = (int)_startupLiveSearchMaxCards.Value;
    }

    string GetTikTokStartupGateDetail()
        => _startupPreparationState switch
        {
            "CAPTCHA_REQUIRED" => "TikTok vẫn đang yêu cầu CAPTCHA sau thời gian chờ. Hãy xử lý CAPTCHA trên Chrome rồi bấm Bắt đầu lại.",
            "TOTP_REQUIRED" => "TikTok đang yêu cầu 2FA nhưng profile chưa có secret TOTP.",
            "LOGIN_REQUIRED" => "Profile chưa đăng nhập và chưa cấu hình tài khoản/mật khẩu tự động.",
            "ACCOUNT_BANNED" => "Tài khoản TikTok đã bị cấm/đình chỉ/không tồn tại.",
            _ => "TikTok chưa sẵn sàng: " + _startupPreparationState
        };

    List<string> GetStartupLiveSearchKeywords()
        => (_settings.StartupLiveSearch?.Keywords ?? "")
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    Task<bool> TryStartupLiveSearchAsync()
        => TryLiveSearchAsync(CancellationToken.None);

    async Task<bool> TryRuntimeLiveSearchAsync(string reason, CancellationToken ct)
    {
        _log.Warn($"[RUNTIME_LIVE_SEARCH_BEGIN] reason={ShortText(reason, 180)}");
        var ok = await TryLiveSearchAsync(ct);
        _log.Warn($"[RUNTIME_LIVE_SEARCH_DONE] reason={ShortText(reason, 180)} result={(ok ? "OPENED" : "MISS")}");
        return ok;
    }

    async Task<bool> TryLiveSearchAsync(CancellationToken parentCt)
    {
        var cfg = _settings.StartupLiveSearch ?? new StartupLiveSearchSettings();
        if (!cfg.Enabled) return false;

        var keywords = GetStartupLiveSearchKeywords();
        if (keywords.Count == 0)
        {
            _log.Warn("[STARTUP_LIVE_SEARCH_SKIP] reason=no-keywords action=OLD_FLOW");
            return false;
        }

        // Mỗi lượt START chỉ xáo thứ tự keyword đúng 1 lần. Bên trong từng keyword vẫn giữ
        // nguyên flow cũ TOP -> LIVE; nếu keyword đầu không đạt thì tiếp tục keyword còn lại.
        var originalKeywordOrder = string.Join("|", keywords);
        for (var i = keywords.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (keywords[i], keywords[j]) = (keywords[j], keywords[i]);
        }
        _log.Info($"[STARTUP_LIVE_SEARCH_KEYWORD_ORDER] original={originalKeywordOrder} randomized={string.Join("|", keywords)}");

        var threshold = Math.Max(0, _settings.Viewer?.Threshold ?? 0);
        var maxCards = Math.Clamp(cfg.MaxCards, 3, 50);
        var totalTimeoutSec = Math.Clamp(cfg.TotalTimeoutSec, 20, 300);
        var keywordTimeoutSec = Math.Clamp(cfg.KeywordTimeoutSec, 10, 120);

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        totalCts.CancelAfter(TimeSpan.FromSeconds(totalTimeoutSec));
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        _log.Info($"[STARTUP_LIVE_SEARCH_BEGIN] keywords={string.Join("|", keywords)} threshold={threshold} keywordTimeoutSec={keywordTimeoutSec} totalTimeoutSec={totalTimeoutSec} maxCards={maxCards}");

        foreach (var keyword in keywords)
        {
            if (totalCts.IsCancellationRequested) break;

            using var keywordCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            keywordCts.CancelAfter(TimeSpan.FromSeconds(keywordTimeoutSec));
            var ct = keywordCts.Token;
            var keywordSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _log.Info($"[STARTUP_LIVE_SEARCH_KEYWORD_BEGIN] keyword={keyword}");
                var topUrl = BuildTikTokSearchUrl(keyword, liveTab: false);

                await RunSearchStepAsync(
                    "OPEN_TOP",
                    SearchOpenStepSec,
                    ct,
                    token => _chrome.NavigateAndWaitAsync(topUrl, 350, SearchOpenStepSec * 1000, token));

                await TryEnsureExactSearchTopTabAsync(keyword, ct);

                var topCandidates = await WaitForSearchCandidatesAsync(
                    "TOP",
                    SearchTopRenderStepSec,
                    maxCards,
                    ct);

                var topGood = topCandidates.FirstOrDefault(x => x.Viewer >= threshold);
                if (topGood is not null)
                {
                    if (await TryOpenStartupSearchCandidateAsync(keyword, "TOP", topGood, ct))
                    {
                        _log.Info($"[STARTUP_LIVE_SEARCH_SUCCESS] keyword={keyword} tab=TOP viewer={topGood.Viewer} href={ShortText(topGood.Href, 160)} elapsedMs={totalSw.ElapsedMilliseconds}");
                        return true;
                    }
                }

                _log.Info($"[STARTUP_LIVE_SEARCH_TOP_MISS] keyword={keyword} cards={topCandidates.Count} threshold={threshold} action=LIVE_TAB");

                var liveTabReady = await TrySwitchSearchLiveTabAsync(keyword, ct);
                if (!liveTabReady)
                {
                    _log.Warn($"[STARTUP_LIVE_SEARCH_LIVE_TAB_CLICK_FAIL] keyword={keyword} action=DIRECT_URL");
                    var liveSearchUrl = BuildTikTokSearchUrl(keyword, liveTab: true);
                    await RunSearchStepAsync(
                        "OPEN_LIVE_URL",
                        SearchLiveRenderStepSec,
                        ct,
                        token => _chrome.NavigateAndWaitAsync(liveSearchUrl, 350, SearchLiveRenderStepSec * 1000, token));
                }

                var liveCandidates = await WaitForSearchCandidatesAsync(
                    "LIVE",
                    SearchLiveRenderStepSec,
                    maxCards,
                    ct);

                var liveGood = liveCandidates.FirstOrDefault(x => x.Viewer >= threshold);
                if (liveGood is not null)
                {
                    if (await TryOpenStartupSearchCandidateAsync(keyword, "LIVE", liveGood, ct))
                    {
                        _log.Info($"[STARTUP_LIVE_SEARCH_SUCCESS] keyword={keyword} tab=LIVE viewer={liveGood.Viewer} href={ShortText(liveGood.Href, 160)} elapsedMs={totalSw.ElapsedMilliseconds}");
                        return true;
                    }
                }

                _log.Warn($"[STARTUP_LIVE_SEARCH_KEYWORD_MISS] keyword={keyword} topCards={topCandidates.Count} liveCards={liveCandidates.Count} elapsedMs={keywordSw.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException) when (keywordCts.IsCancellationRequested && !parentCt.IsCancellationRequested)
            {
                _log.Warn($"[STARTUP_LIVE_SEARCH_KEYWORD_TIMEOUT] keyword={keyword} elapsedMs={keywordSw.ElapsedMilliseconds} totalElapsedMs={totalSw.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[STARTUP_LIVE_SEARCH_KEYWORD_ERROR] keyword={keyword} error={ShortText(ex.Message, 180)} action=NEXT_KEYWORD");
            }
        }

        parentCt.ThrowIfCancellationRequested();
        _log.Warn($"[STARTUP_LIVE_SEARCH_FAIL] elapsedMs={totalSw.ElapsedMilliseconds} totalTimedOut={totalCts.IsCancellationRequested} action=RETURN_FALSE_TO_CALLER");
        return false;
    }

    static string BuildTikTokSearchUrl(string keyword, bool liveTab)
    {
        var q = Uri.EscapeDataString(keyword ?? "");
        return liveTab
            ? $"https://www.tiktok.com/search/live?q={q}"
            : $"https://www.tiktok.com/search?q={q}";
    }

    async Task RunSearchStepAsync(string step, int timeoutSec, CancellationToken parentCt, Func<CancellationToken, Task> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.Info($"[STARTUP_LIVE_SEARCH_STEP_BEGIN] step={step} timeoutSec={timeoutSec}");
        try
        {
            await action(cts.Token);
            _log.Info($"[STARTUP_LIVE_SEARCH_STEP_OK] step={step} elapsedMs={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !parentCt.IsCancellationRequested)
        {
            _log.Warn($"[STARTUP_LIVE_SEARCH_STEP_TIMEOUT] step={step} elapsedMs={sw.ElapsedMilliseconds}");
            throw new TimeoutException($"Search LIVE step {step} quá {timeoutSec}s.");
        }
    }

    async Task<T> RunSearchStepAsync<T>(string step, int timeoutSec, CancellationToken parentCt, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.Info($"[STARTUP_LIVE_SEARCH_STEP_BEGIN] step={step} timeoutSec={timeoutSec}");
        try
        {
            var result = await action(cts.Token);
            _log.Info($"[STARTUP_LIVE_SEARCH_STEP_OK] step={step} elapsedMs={sw.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !parentCt.IsCancellationRequested)
        {
            _log.Warn($"[STARTUP_LIVE_SEARCH_STEP_TIMEOUT] step={step} elapsedMs={sw.ElapsedMilliseconds}");
            throw new TimeoutException($"Search LIVE step {step} quá {timeoutSec}s.");
        }
    }

    async Task<List<StartupSearchCandidate>> WaitForSearchCandidatesAsync(
        string tab,
        int timeoutSec,
        int maxCards,
        CancellationToken parentCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<StartupSearchCandidate> latest = [];
        _log.Info($"[STARTUP_LIVE_SEARCH_STEP_BEGIN] step=SCAN_{tab} timeoutSec={timeoutSec}");

        try
        {
            while (!cts.IsCancellationRequested)
            {
                latest = await ReadSearchLiveCandidatesAsync(maxCards, cts.Token);
                if (latest.Count > 0)
                {
                    _log.Info($"[STARTUP_LIVE_SEARCH_CARDS] tab={tab} count={latest.Count} values={string.Join(",", latest.Take(6).Select(x => x.ViewerText + "=" + x.Viewer))} elapsedMs={sw.ElapsedMilliseconds}");
                    return latest;
                }
                await Task.Delay(250, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !parentCt.IsCancellationRequested)
        {
            // Hết thời gian render/quét của tab là MISS bình thường, KHÔNG làm hỏng cả keyword.
        }

        parentCt.ThrowIfCancellationRequested();
        _log.Info($"[STARTUP_LIVE_SEARCH_STEP_MISS] step=SCAN_{tab} elapsedMs={sw.ElapsedMilliseconds} action=NEXT_STAGE");
        return latest;
    }

    async Task<List<StartupSearchCandidate>> ReadSearchLiveCandidatesAsync(int maxCards, CancellationToken ct)
    {
        // KHÔNG dựa vào class hash động. Viewer và Like dùng cùng LiveTextWrap nhưng SVG path khác nhau.
        // Viewer icon do user cung cấp bắt đầu "M24 3a10 10..."; Like bắt đầu "M26.56 2.78a4.37...".
        // Chỉ nhận icon VIEWER, mọi LIKE/UNKNOWN đều bị loại trước khi parse số.
        var js = $$"""
(() => {
  const maxCards = {{Math.Clamp(maxCards, 3, 50)}};
  const visible = (e) => {
    if (!e) return false;
    const r = e.getBoundingClientRect();
    const s = getComputedStyle(e);
    return r.width > 1 && r.height > 1 && s.display !== 'none' && s.visibility !== 'hidden';
  };
  const normalizePath = (v) => String(v || '').replace(/\s+/g, ' ').trim();
  const result = [];
  const seen = new Set();
  const badges = Array.from(document.querySelectorAll('div[class*="LiveTextWrap"]'));
  for (const badge of badges) {
    if (!visible(badge)) continue;
    const path = normalizePath(badge.querySelector('svg path')?.getAttribute('d'));
    if (!path.startsWith('M24 3a10 10')) continue; // VIEWER only; never LIKE.

    const textNode = badge.querySelector('div[class*="LiveText"]');
    const viewerText = String(textNode?.innerText || textNode?.textContent || '').trim();
    if (!viewerText) continue;

    let node = badge;
    let anchor = null;
    for (let depth = 0; depth < 9 && node; depth++, node = node.parentElement) {
      if (node.matches?.('a[href*="/live"]')) { anchor = node; break; }
      const nested = node.querySelector?.('a[href*="/live"]');
      if (nested) { anchor = nested; break; }
    }
    if (!anchor) continue;
    const href = String(anchor.href || anchor.getAttribute('href') || '').trim();
    if (!href || !/tiktok\.com\/@[^/]+\/live/i.test(href)) continue;
    if (seen.has(href)) continue;
    seen.add(href);

    const m = href.match(/tiktok\.com\/@([^/?#]+)\/live/i);
    result.push({ href, viewerText, username: m ? m[1] : '' });
    if (result.length >= maxCards) break;
  }
  return result;
})()
""";

        var eval = await _chrome.EvalAsync(js, ct: ct);
        if (!eval.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<StartupSearchCandidate>();
        foreach (var item in value.EnumerateArray())
        {
            var href = item.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "";
            var viewerText = item.TryGetProperty("viewerText", out var vt) ? vt.GetString() ?? "" : "";
            var username = item.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(viewerText)) continue;
            var viewer = ViewerCountParser.Parse(viewerText, _log);
            if (viewer < 0) continue;
            result.Add(new StartupSearchCandidate(href, viewerText, viewer, username));
        }
        return result;
    }

    async Task TryEnsureExactSearchTopTabAsync(string keyword, CancellationToken ct)
    {
        try
        {
            // Chỉ chấp nhận đúng tab-bar button chứa span data-testid=tux-web-text = Top.
            // Không fallback sang tìm text Top chung để tránh click nhầm nội dung/card.
            if (!await _chrome.XPathExistsAsync(SearchTopTabXPath, ct))
            {
                _log.Warn($"[STARTUP_LIVE_SEARCH_TOP_TAB_NOT_FOUND] keyword={keyword} action=KEEP_DIRECT_TOP_URL");
                return;
            }

            await _chrome.ClickXPathDomSmartAsync(SearchTopTabXPath, ct: ct);
            _log.Info($"[STARTUP_LIVE_SEARCH_TOP_TAB_OK] keyword={keyword} xpath={SearchTopTabXPath}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[STARTUP_LIVE_SEARCH_TOP_TAB_WARN] keyword={keyword} error={ShortText(ex.Message, 140)} action=KEEP_DIRECT_TOP_URL");
        }
    }

    async Task<bool> TrySwitchSearchLiveTabAsync(string keyword, CancellationToken parentCt)
    {
        try
        {
            await RunSearchStepAsync(
                "CLICK_LIVE_TAB",
                SearchLiveTabStepSec,
                parentCt,
                async token =>
                {
                    // XPath này bám chính xác button data-testid=tux-web-tab-bar và span text=LIVE,
                    // nên không thể nhầm với chữ LIVE trong card hoặc nội dung trang.
                    if (!await _chrome.XPathExistsAsync(SearchLiveTabXPath, token))
                        throw new InvalidOperationException("Không tìm thấy đúng tab LIVE của thanh Search.");
                    await _chrome.ClickXPathDomSmartAsync(SearchLiveTabXPath, ct: token);

                    var deadline = Environment.TickCount64 + SearchLiveTabStepSec * 1000L;
                    while (Environment.TickCount64 < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        var href = await ReadRuntimeHrefAsync(token);
                        if (href.Contains("/search/live", StringComparison.OrdinalIgnoreCase)) return;
                        await Task.Delay(150, token);
                    }
                    throw new TimeoutException("Click tab LIVE nhưng URL chưa chuyển sang /search/live.");
                });
            _log.Info($"[STARTUP_LIVE_SEARCH_LIVE_TAB_OK] keyword={keyword} xpath={SearchLiveTabXPath}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"[STARTUP_LIVE_SEARCH_LIVE_TAB_FAIL] keyword={keyword} error={ShortText(ex.Message, 160)}");
            return false;
        }
    }

    async Task<bool> TryOpenStartupSearchCandidateAsync(
        string keyword,
        string tab,
        StartupSearchCandidate candidate,
        CancellationToken parentCt)
    {
        try
        {
            return await RunSearchStepAsync(
                "OPEN_CANDIDATE",
                SearchCandidateOpenStepSec,
                parentCt,
                async token =>
                {
                    _log.Info($"[STARTUP_LIVE_SEARCH_PICK] keyword={keyword} tab={tab} user={candidate.Username} viewer={candidate.Viewer} raw={candidate.ViewerText} href={ShortText(candidate.Href, 170)}");
                    await _chrome.NavigateAndWaitAsync(candidate.Href, 300, SearchCandidateOpenStepSec * 1000, token);

                    var deadline = Environment.TickCount64 + SearchCandidateOpenStepSec * 1000L;
                    while (Environment.TickCount64 < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        var href = await ReadRuntimeHrefAsync(token);
                        if (LooksLikeTikTokLiveUrl(href))
                        {
                            var probe = await ProbeStartupLivePageReadyAsync();
                            if (probe.Ready)
                            {
                                _startupPreparationState = "READY";
                                SetChromeStatus(
                                    "Trạng thái Chrome: 🟢 Đã sẵn sàng", Color.DarkGreen,
                                    $"TikTok: 🟢 Search LIVE — {candidate.ViewerText} viewer", Color.DarkGreen);
                                return true;
                            }
                        }
                        await Task.Delay(250, token);
                    }
                    return false;
                });
        }
        catch (Exception ex)
        {
            _log.Warn($"[STARTUP_LIVE_SEARCH_CANDIDATE_FAIL] keyword={keyword} tab={tab} href={ShortText(candidate.Href, 140)} error={ShortText(ex.Message, 160)}");
            return false;
        }
    }

    async Task<string> ReadRuntimeHrefAsync(CancellationToken ct)
    {
        var r = await _chrome.EvalAsync("String(location.href || '')", ct: ct);
        return r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }
}
