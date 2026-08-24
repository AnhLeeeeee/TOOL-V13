using System.Diagnostics;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    const string RuntimeStateRunning = "RUNNING";
    const string RuntimeStatePaused = "PAUSED";
    const string RuntimeStateStopped = "STOPPED";
    const string RuntimeStateRecovering = "RECOVERING";
    const string RuntimeStateUnknown = "UNKNOWN";

    static bool IsWorkerReportedRuntimeState(string? state)
    {
        return NormalizeRuntimeState(state) is RuntimeStateRunning
            or RuntimeStatePaused
            or RuntimeStateStopped
            or RuntimeStateRecovering;
    }

    static string NormalizeRuntimeState(string? state)
    {
        return state?.Trim().ToUpperInvariant() switch
        {
            RuntimeStateRunning => RuntimeStateRunning,
            RuntimeStatePaused => RuntimeStatePaused,
            RuntimeStateStopped => RuntimeStateStopped,
            RuntimeStateRecovering => RuntimeStateRecovering,
            _ => RuntimeStateUnknown
        };
    }

    static bool SnapshotIndicatesRecovery(WorkerSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        if (NormalizeRuntimeState(snapshot.RunState) == RuntimeStateRecovering) return true;

        var detail = snapshot.Detail ?? "";
        return detail.Contains("recover", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("oom", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("crash", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsWorkerProcessExited(ProfileContext ctx)
    {
        var worker = ctx.Worker;
        if (worker is null) return false;
        try { return worker.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    string GetLastConfirmedRuntimeState(ProfileContext ctx)
    {
        var state = NormalizeRuntimeState(ctx.LastConfirmedRuntimeState);
        return state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateStopped
            ? state
            : RuntimeStateUnknown;
    }

    string GetEffectiveRuntimeState(ProfileContext ctx)
    {
        // HasExited is itself an allowed, positive STOPPED confirmation. The
        // event/poll path also persists this through ConfirmRuntimeState.
        if (IsWorkerProcessExited(ctx)) return RuntimeStateStopped;
        if (ctx.RuntimeRecoveryInProgress || SnapshotIndicatesRecovery(ctx.LastSnapshot))
            return RuntimeStateRecovering;
        return GetLastConfirmedRuntimeState(ctx);
    }

    static Color GetRuntimeStateColor(string state)
    {
        return state switch
        {
            RuntimeStateRunning => Color.DarkGreen,
            RuntimeStatePaused => Color.DarkOrange,
            RuntimeStateRecovering => Color.Goldenrod,
            RuntimeStateStopped => Color.Firebrick,
            _ => Color.DimGray
        };
    }

    void ConfirmRuntimeState(ProfileContext ctx, string state, string source, bool clearRecovery = true)
    {
        var normalized = NormalizeRuntimeState(state);
        if (normalized is not (RuntimeStateRunning or RuntimeStatePaused or RuntimeStateStopped)) return;

        var previous = GetLastConfirmedRuntimeState(ctx);
        ctx.LastConfirmedRuntimeState = normalized;
        ctx.LastConfirmedRuntimeStateUtc = DateTime.UtcNow;
        ctx.ConsecutiveStatusPollFailures = 0;
        ctx.LastStatusPollFailure = "";
        if (clearRecovery) ctx.RuntimeRecoveryInProgress = false;

        if (!string.Equals(previous, normalized, StringComparison.Ordinal))
            _log.Info($"[RUNTIME_STATE_CONFIRMED] profile={ctx.Profile.Name} previous={previous} state={normalized} source={source}");
    }

    void ApplyWorkerSnapshotRuntimeState(ProfileContext ctx, WorkerSnapshot snapshot)
    {
        var reported = NormalizeRuntimeState(snapshot.RunState);
        if (reported == RuntimeStateRecovering)
        {
            ctx.RuntimeRecoveryInProgress = true;
            return;
        }

        ConfirmRuntimeState(ctx, reported, "worker_status", clearRecovery: false);
        ctx.RuntimeRecoveryInProgress = SnapshotIndicatesRecovery(snapshot);
    }

    void ApplyCommandRuntimeConfirmation(ProfileContext ctx, string command, string response)
    {
        var normalizedCommand = command.Trim().ToLowerInvariant();
        var normalizedResponse = response.Trim().ToLowerInvariant();
        var confirmedState = (normalizedCommand, normalizedResponse) switch
        {
            ("start", "started") => RuntimeStateRunning,
            ("start_auto", "started") => RuntimeStateRunning,
            ("resume", "running") => RuntimeStateRunning,
            ("pause", "paused") => RuntimeStatePaused,
            ("stop", "stopped") => RuntimeStateStopped,
            _ => ""
        };

        if (confirmedState.Length > 0)
        {
            ConfirmRuntimeState(ctx, confirmedState, $"worker_command:{normalizedCommand}");
            NotifyAutoCloseRuntimeCommand(ctx, normalizedCommand, confirmedState);
        }
    }

    void HandleStatusPollFailure(ProfileContext ctx, Exception exception, string method)
    {
        if (IsWorkerProcessExited(ctx))
        {
            ConfirmRuntimeState(ctx, RuntimeStateStopped, "worker_process_exited");
            SetStatus(ctx, "Worker đã thoát | STOPPED", Color.Firebrick);
            return;
        }

        ctx.ConsecutiveStatusPollFailures++;
        ctx.LastStatusPollFailure = exception.Message;

        // Preserve a recovery signal already reported by the Worker. Ordinary
        // IPC timeouts and malformed/missing snapshots keep the last confirmed
        // RUNNING/PAUSED/STOPPED state instead of manufacturing STOPPED.
        var recoverySignal = exception.Message.Contains("recover", StringComparison.OrdinalIgnoreCase)
                             || exception.Message.Contains("reconnect", StringComparison.OrdinalIgnoreCase);
        if (recoverySignal) ctx.RuntimeRecoveryInProgress = true;

        var keptState = GetEffectiveRuntimeState(ctx);
        SetStatus(ctx, $"Status tạm thời lỗi | giữ {keptState}", GetRuntimeStateColor(keptState));

        if (ctx.ConsecutiveStatusPollFailures == 1 || ctx.ConsecutiveStatusPollFailures % 5 == 0)
        {
            _log.Warn($"[STATUS_POLL_TRANSIENT] profile={ctx.Profile.Name} method={method} failures={ctx.ConsecutiveStatusPollFailures} keep={keptState} error={exception.Message}");
        }
    }

    void OnWorkerProcessExited(ProfileContext ctx, Process process)
    {
        void ApplyExit()
        {
            // Ignore an Exited callback belonging to a Worker instance that was
            // intentionally replaced by a newer process.
            if (!ReferenceEquals(ctx.Worker, process)) return;

            var exitCode = "?";
            try { exitCode = process.ExitCode.ToString(); } catch { }

            // Exit code 0 là đường thoát sạch (shutdown/đóng Worker bình thường).
            // Đây là thao tác kết thúc chủ động, không được giữ expected-running
            // rồi 10 phút sau sinh FAULT_10M + suất bù.
            if (exitCode == "0")
            {
                ClearAutoCloseExpectedRunning(
                    ctx.Profile.Name,
                    "worker_clean_exit");
            }

            ConfirmRuntimeState(ctx, RuntimeStateStopped, $"worker_process_exited:{exitCode}");
            SetStatus(ctx, $"Worker đã thoát ({exitCode}) | STOPPED", Color.Firebrick);
        }

        try
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke((Action)ApplyExit);
            else ApplyExit();
        }
        catch (InvalidOperationException) { }
    }
}
