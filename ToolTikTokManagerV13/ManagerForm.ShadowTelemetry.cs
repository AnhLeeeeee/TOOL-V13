namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    ShadowHeartbeatSnapshot? _shadowHeartbeatSnapshot;

    void InitializeShadowTelemetry()
    {
        UpdateShadowTelemetrySnapshot();
        _refreshTimer.Tick += (_, _) => UpdateShadowTelemetrySnapshot();
    }

    internal ShadowHeartbeatSnapshot? GetShadowHeartbeatSnapshot()
        => Volatile.Read(ref _shadowHeartbeatSnapshot);

    void UpdateShadowTelemetrySnapshot()
    {
        try
        {
            // This method runs on the WinForms UI timer, so reading _contexts is
            // kept on the same UI thread that normally owns it.
            var contexts = _contexts.Values.ToArray();

            var workerAttached = 0;
            var workerAlive = 0;
            var running = 0;
            var paused = 0;
            var recovering = 0;
            var stopped = 0;
            var unknown = 0;

            foreach (var ctx in contexts)
            {
                var worker = ctx.Worker;
                if (worker is not null)
                {
                    workerAttached++;
                    try
                    {
                        if (!worker.HasExited) workerAlive++;
                    }
                    catch
                    {
                        // A transient Process access failure is telemetry-only.
                    }
                }

                switch (GetEffectiveRuntimeState(ctx))
                {
                    case RuntimeStateRunning: running++; break;
                    case RuntimeStatePaused: paused++; break;
                    case RuntimeStateRecovering: recovering++; break;
                    case RuntimeStateStopped: stopped++; break;
                    default: unknown++; break;
                }
            }

            int autoReplacementTarget;
            bool autoReplacementTargetInitialized;
            bool startAllInProgress;
            lock (_autoReplacementFixedSlotLock)
            {
                autoReplacementTarget = _autoReplacementTargetSlots;
                autoReplacementTargetInitialized = _autoReplacementTargetInitialized;
                startAllInProgress = _autoReplacementStartAllInProgress;
            }

            bool runStrategyActive;
            int runStrategyTarget;
            bool dailyReplaceAll;
            int dailyReplaceAllTarget;
            bool autoEnsureTarget;
            lock (_runStrategyLock)
            {
                runStrategyActive = _runStrategySessionActive;
                runStrategyTarget = _runStrategyTargetSlots;
                dailyReplaceAll = _runStrategySettings.DailyReplaceAll;
                dailyReplaceAllTarget = _runStrategySettings.DailyReplaceAllTargetSlots;
                autoEnsureTarget = _runStrategySettings.AutoEnsureTarget;
            }

            var telemetry = new
            {
                schema = 1,
                workers = new
                {
                    contexts = contexts.Length,
                    attached = workerAttached,
                    alive = workerAlive,
                    running,
                    paused,
                    recovering,
                    stopped,
                    unknown
                },
                profiles = new
                {
                    openContexts = contexts.Length,
                    running
                },
                runtime = new
                {
                    managerClosing = _closing,
                    autoReplacementTarget,
                    autoReplacementTargetInitialized,
                    startAllInProgress,
                    runStrategyActive,
                    runStrategyTarget,
                    autoEnsureTarget,
                    dailyReplaceAll,
                    dailyReplaceAllTarget
                }
            };

            Volatile.Write(
                ref _shadowHeartbeatSnapshot,
                new ShadowHeartbeatSnapshot(contexts.Length, telemetry));

            // 3C.6 FINAL SHADOW:
            // cập nhật counters mỗi refresh tick để Worker direct-start nhìn thấy
            // max_running_profiles gần realtime, không chờ heartbeat 60 giây.
            ObserveRemotePolicyLimitsSnapshot();
        }
        catch (Exception ex)
        {
            // Observation must never affect the operational flow.
            try { _log.Warn($"[SHADOW_TELEMETRY_SNAPSHOT_ERROR] detail={ex.Message}"); } catch { }
        }
    }
}
