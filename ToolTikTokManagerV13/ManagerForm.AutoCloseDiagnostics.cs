using System.Text;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    readonly Dictionary<string, string> _autoDiagLastOpenTraceByProfile =
        new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, DateTime> _autoDiagLastOpenUtcByProfile =
        new(StringComparer.OrdinalIgnoreCase);

    string BeginAutoDiagnosticOpenChrome(ProfileContext ctx, string source)
    {
        var trace = $"OPEN-{ctx.Profile.Name}-{DateTime.Now:HHmmssfff}";
        _autoDiagLastOpenTraceByProfile[ctx.Profile.Name] = trace;
        _autoDiagLastOpenUtcByProfile[ctx.Profile.Name] = DateTime.UtcNow;

        WriteAutoDiagnosticEvent(
            ctx,
            source,
            "MANUAL_OPEN_CHROME",
            "BẮT ĐẦU",
            $"trace={trace}");

        return trace;
    }

    void FinishAutoDiagnosticOpenChrome(
        ProfileContext ctx,
        string source,
        string trace,
        string result,
        string extra = "")
    {
        WriteAutoDiagnosticEvent(
            ctx,
            source,
            "MANUAL_OPEN_CHROME",
            result,
            $"trace={trace}; {extra}");
    }

    void WriteAutoDiagnosticEvent(
        ProfileContext ctx,
        string source,
        string reason,
        string result,
        string extra = "")
    {
        try
        {
            var state = GetEffectiveRuntimeState(ctx);
            var nowUtc = DateTime.UtcNow;
            var profileName = ctx.Profile.Name;

            var workerAlive = false;
            var workerPid = 0;
            try
            {
                workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
                if (workerAlive && ctx.Worker is not null)
                    workerPid = ctx.Worker.Id;
            }
            catch { }

            var tabOpen =
                ctx.Tab is not null
                && !ctx.Tab.IsDisposed
                && ctx.Tab.Parent == _tabs;

            var chrome = ctx.LastSnapshot?.Chrome ?? "";
            var rounds = ctx.LastSnapshot?.Rounds ?? 0;
            var step = ctx.LastSnapshot?.Step ?? 0;
            var snapshotDetail = CompactAutoActivityText(ctx.LastSnapshot?.Detail, 160);

            var expectedSet = _autoCloseExpectedRunningProfiles.Contains(profileName);
            var runtimeStatsRunning = RuntimeStatsFileSaysRunning(ctx);
            var inProgress = _autoCloseInProgressProfiles.Contains(profileName);
            var replacementClaimed = _autoReplacementClaimedProfiles.Contains(profileName);

            var faultSince = "none";
            var faultAge = "none";
            if (_autoCloseNotRunningSinceUtc.TryGetValue(profileName, out var faultUtc))
            {
                faultSince = faultUtc.ToLocalTime().ToString("HH:mm:ss");
                faultAge = Math.Max(0, (nowUtc - faultUtc).TotalSeconds).ToString("0") + "s";
            }

            var cleanupRetry = "none";
            if (_autoCloseCleanupRetryUtc.TryGetValue(profileName, out var retryUtc))
            {
                var remaining = Math.Max(0, (retryUtc - nowUtc).TotalSeconds);
                cleanupRetry = $"{retryUtc.ToLocalTime():HH:mm:ss}({remaining:0}s)";
            }

            var lastOpenTrace = _autoDiagLastOpenTraceByProfile.TryGetValue(profileName, out var openTrace)
                ? openTrace
                : "none";

            var lastOpenAge = "none";
            if (_autoDiagLastOpenUtcByProfile.TryGetValue(profileName, out var openUtc))
                lastOpenAge = Math.Max(0, (nowUtc - openUtc).TotalSeconds).ToString("0") + "s";

            var total = TimeSpan.Zero;
            try { total = ReadStatisticsRuntime(ctx).Total; } catch { }

            var statusAge = ctx.LastStatusRefreshUtc == DateTime.MinValue
                ? "never"
                : Math.Max(0, (nowUtc - ctx.LastStatusRefreshUtc).TotalSeconds).ToString("0") + "s";

            var detail = new StringBuilder();
            detail.Append($"source={source}; state={state}; workerAlive={workerAlive}; workerPid={workerPid}; ");
            detail.Append($"chrome={chrome}; tabOpen={tabOpen}; opening={ctx.Opening}; expectedSet={expectedSet}; ");
            detail.Append($"runtimeStatsRunning={runtimeStatsRunning}; faultSince={faultSince}; faultAge={faultAge}; ");
            detail.Append($"cleanupRetry={cleanupRetry}; autoCloseInProgress={inProgress}; replacementClaimed={replacementClaimed}; ");
            detail.Append($"total={total:c}; statusAge={statusAge}; rounds={rounds}; step={step}; ");
            detail.Append($"lastOpenTrace={lastOpenTrace}; lastOpenAge={lastOpenAge}");

            if (!string.IsNullOrWhiteSpace(snapshotDetail))
                detail.Append($"; snapshotDetail={snapshotDetail}");

            if (!string.IsNullOrWhiteSpace(extra))
                detail.Append($"; {CompactAutoActivityText(extra, 240)}");

            var compact = CompactAutoActivityText(detail.ToString(), 900);

            _log.Info(
                $"[AUTO_DIAG] profile={profileName} reason={reason} result={result} {compact}");

            WriteAutoActivityLog(
                action: "CHẨN ĐOÁN",
                profile: profileName,
                account: ResolveAutoActivityAccount(profileName),
                reason: reason,
                result: result,
                detail: compact);
        }
        catch (Exception ex)
        {
            try { _log.Warn($"[AUTO_DIAG_WRITE_WARN] profile={ctx.Profile.Name} error={ex.Message}"); } catch { }
        }
    }
}
