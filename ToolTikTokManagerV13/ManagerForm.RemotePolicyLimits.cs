using ToolTikTokV12.Controls;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    static readonly object RemotePolicyCreateHistoryLock = new();

    string RemotePolicyCreateHistoryPath
        => Path.Combine(
            _baseDir,
            "remote_policy_create_history.json");

    int CountRemotePolicyAliveWorkers()
    {
        var count = 0;

        foreach (var ctx in _contexts.Values)
        {
            try
            {
                if (ctx.Worker is not null
                    && !ctx.Worker.HasExited)
                {
                    count++;
                }
            }
            catch
            {
                // Telemetry/limit observation must fail-open.
            }
        }

        return count;
    }

    int CountRemotePolicyActiveProfiles()
    {
        var count = 0;

        foreach (var ctx in _contexts.Values)
        {
            var state = GetEffectiveRuntimeState(ctx);

            // PAUSED vẫn giữ một runtime slot vì có thể Resume ngay.
            if (state is RuntimeStateRunning
                or RuntimeStateRecovering
                or RuntimeStatePaused)
            {
                count++;
            }
        }

        return count;
    }

    bool CheckRemotePolicyLimit(
        string limitName,
        int current,
        int requestedAdditional,
        string source,
        string? profileName = null,
        bool showDialog = false)
    {
        var allowed = RemotePolicyRuntimeGate.IsLimitAllowed(
            limitName,
            current,
            requestedAdditional,
            out var decision);

        _log.Info(
            $"[REMOTE_POLICY_LIMIT_RUNTIME_CHECK] limit={limitName} source={source} " +
            $"profile={profileName ?? "-"} revision={decision.Revision} " +
            $"max={(decision.Max.HasValue ? decision.Max.Value.ToString() : "none")} " +
            $"current={decision.Current} additional={decision.RequestedAdditional} " +
            $"wouldExceed={decision.WouldExceed} enforcement={decision.EnforcementEnabled} " +
            $"allowed={allowed} adminBypass={RemotePolicyRuntimeGate.AdminBypass}");

        if (!allowed)
        {
            _log.Warn(
                $"[REMOTE_POLICY_LIMIT_RUNTIME_BLOCKED] limit={limitName} source={source} " +
                $"profile={profileName ?? "-"} revision={decision.Revision}");

            if (showDialog)
            {
                ModernDialog.ShowMessage(
                    this,
                    $"Thiết bị đã đạt giới hạn {limitName} do QITool policy quy định.",
                    "QITool — giới hạn sử dụng",
                    MessageBoxIcon.Warning);
            }
        }

        return allowed;
    }

    bool CheckRemotePolicyMaxWorkersBeforeSpawn(
        ProfileContext ctx,
        string source)
        => CheckRemotePolicyLimit(
            "max_workers",
            CountRemotePolicyAliveWorkers(),
            requestedAdditional: 1,
            source,
            ctx.Profile.Name);

    bool CheckRemotePolicyMaxRunningBeforeStart(
        ProfileContext ctx,
        string source)
    {
        var state = GetEffectiveRuntimeState(ctx);

        var alreadyOccupiesSlot =
            state is RuntimeStateRunning
                or RuntimeStateRecovering
                or RuntimeStatePaused;

        return CheckRemotePolicyLimit(
            "max_running_profiles",
            CountRemotePolicyActiveProfiles(),
            requestedAdditional: alreadyOccupiesSlot ? 0 : 1,
            source,
            ctx.Profile.Name);
    }

    bool CheckRemotePolicyMaxCreateBeforeCreate(
        string source,
        string profileName)
        => CheckRemotePolicyLimit(
            "max_create_per_hour",
            GetRemotePolicyCreateCountLastHour(),
            requestedAdditional: 1,
            source,
            profileName);

    void RecordRemotePolicyProfileCreate(
        string source,
        string profileName)
    {
        try
        {
            int count;
            lock (RemotePolicyCreateHistoryLock)
            {
                var now = DateTime.UtcNow;
                var list = LoadRemotePolicyCreateHistoryLocked()
                    .Where(x => now - x <= TimeSpan.FromHours(1))
                    .ToList();

                list.Add(now);
                SaveRemotePolicyCreateHistoryLocked(list);
                count = list.Count;
            }

            _log.Info(
                $"[REMOTE_POLICY_CREATE_COUNTER] source={source} profile={profileName} " +
                $"createsLastHour={count}");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REMOTE_POLICY_CREATE_COUNTER_FAIL_OPEN] source={source} " +
                $"profile={profileName} detail={ex.Message}");
        }
    }

    int GetRemotePolicyCreateCountLastHour()
    {
        try
        {
            lock (RemotePolicyCreateHistoryLock)
            {
                var now = DateTime.UtcNow;
                var list = LoadRemotePolicyCreateHistoryLocked()
                    .Where(x => now - x <= TimeSpan.FromHours(1))
                    .ToList();

                SaveRemotePolicyCreateHistoryLocked(list);
                return list.Count;
            }
        }
        catch
        {
            return 0;
        }
    }

    List<DateTime> LoadRemotePolicyCreateHistoryLocked()
    {
        try
        {
            if (!File.Exists(RemotePolicyCreateHistoryPath))
                return new List<DateTime>();

            var json = File.ReadAllText(RemotePolicyCreateHistoryPath);

            return JsonSerializer.Deserialize<List<DateTime>>(json)
                   ?? new List<DateTime>();
        }
        catch
        {
            return new List<DateTime>();
        }
    }

    void SaveRemotePolicyCreateHistoryLocked(
        List<DateTime> timestamps)
    {
        var path = RemotePolicyCreateHistoryPath;
        var temp = path + ".tmp";

        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(timestamps));

        File.Move(
            temp,
            path,
            overwrite: true);
    }

    void ObserveRemotePolicyLimitsSnapshot()
    {
        var workers = CountRemotePolicyAliveWorkers();
        var activeProfiles = CountRemotePolicyActiveProfiles();
        var createsLastHour = GetRemotePolicyCreateCountLastHour();

        RemotePolicyRuntimeGate.UpdateRuntimeCounters(
            workers,
            activeProfiles,
            createsLastHour);

        // additional=0 => snapshot hiện tại có đang vượt limit không.
        RemotePolicyRuntimeGate.IsLimitAllowed(
            "max_workers",
            workers,
            0,
            out _);

        RemotePolicyRuntimeGate.IsLimitAllowed(
            "max_running_profiles",
            activeProfiles,
            0,
            out _);

        RemotePolicyRuntimeGate.IsLimitAllowed(
            "max_create_per_hour",
            createsLastHour,
            0,
            out _);
    }
}
