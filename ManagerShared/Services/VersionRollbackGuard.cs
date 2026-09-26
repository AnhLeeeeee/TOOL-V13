using Microsoft.Win32;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV12.Services;

/// <summary>
/// Bảo vệ hạ phiên bản theo 2 lớp:
/// 1) local HighestVersionEver để có fallback khi server lỗi;
/// 2) server policy có quyền cho phép toàn bộ bản cũ, chỉ một/vài bản cụ thể, hoặc chặn.
///
/// Server không làm giảm HighestVersionEver local. Quyền rollback chỉ là ngoại lệ chạy/cài,
/// nhờ vậy khi hết quyền rollback máy tự quay về trạng thái chặn bản cũ.
/// </summary>
public static class VersionRollbackGuard
{
    const int RecordSchema = 1;
    const string RegistryPath = @"Software\ToolTikTok\VersionGuard";
    const string RegistryValueName = "HighestVersionRecord";
    const string RecordFileName = "highest-version.json";
    const string AuditFileName = "version-guard.log";

    const string SignatureSalt = "ToolTikTok.VersionRollbackGuard.Local.v1.20260926";

    public const string ModeDeny = "deny";
    public const string ModeAllowAllOld = "allow_all_old";
    public const string ModeAllowSpecific = "allow_specific";

    static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };
    static readonly object Sync = new();

    static ServerVersionPolicy? _lastServerPolicy;
    static DateTime? _lastServerPolicyFetchedUtc;

    public sealed class VersionGuardDecision
    {
        public bool AllowRun { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string HighestVersionEver { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool RecordUpdated { get; init; }
        public bool ServerPolicyApplied { get; init; }
        public string DowngradeMode { get; init; } = ModeDeny;
    }

    public sealed class ServerVersionPolicy
    {
        public bool Enabled { get; set; } = true;
        public string Mode { get; set; } = ModeDeny;
        public List<string> AllowedVersions { get; set; } = new();
        public string HighestVersionEver { get; set; } = "";
        public bool? AllowCurrentVersion { get; set; }
        public string Message { get; set; } = "";
        public string PolicyVersion { get; set; } = "1";
    }

    sealed class VersionPolicyRequest
    {
        public string DeviceId { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string LocalHighestVersion { get; set; } = "";
        public string Action { get; set; } = "startup";
    }

    sealed class VersionRecord
    {
        public int Schema { get; set; } = RecordSchema;
        public string DeviceId { get; set; } = "";
        public string HighestVersion { get; set; } = "";
        public string Signature { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    readonly record struct StoreReadResult(bool Exists, bool Valid, Version? Version, string Source, string Error);

    public static ServerVersionPolicy? LastServerPolicy
    {
        get
        {
            lock (Sync)
            {
                return _lastServerPolicy is null ? null : ClonePolicy(_lastServerPolicy);
            }
        }
    }

    public static DateTime? LastServerPolicyFetchedUtc
    {
        get { lock (Sync) return _lastServerPolicyFetchedUtc; }
    }

    public static async Task<VersionGuardDecision> EvaluateAndRecordAsync(
        string currentVersion,
        string deviceId,
        bool serverControlEnabled,
        string? serverPolicyUrl,
        bool failClosedOnDowngrade = true,
        CancellationToken cancellationToken = default)
    {
        // LOCAL GUARD LUÔN BẬT:
        // - luôn đọc/ghi HighestVersionEver để bản mới đã chạy thì bản thấp hơn bị chặn;
        // - serverControlEnabled chỉ quyết định có gọi API ngoại lệ rollback từ xa hay không.
        // Nhờ vậy nếu Git/Supabase tạm lỗi, mốc phiên bản local vẫn còn hiệu lực.
        if (!serverControlEnabled)
            SetLastServerPolicy(null);

        var current = ParseVersion(currentVersion);
        if (current is null)
        {
            var invalid = new VersionGuardDecision
            {
                AllowRun = false,
                CurrentVersion = currentVersion ?? "",
                Reason = "Không đọc được phiên bản hiện tại của Tool."
            };
            AppendAudit($"[VERSION_GUARD_BLOCK] reason=INVALID_CURRENT current={Safe(currentVersion)} device={Safe(deviceId)}");
            return invalid;
        }

        var normalizedCurrent = FormatVersion(current);
        var local = ReadLocalHighest(deviceId);
        if (!local.Valid && local.Exists)
        {
            AppendAudit($"[VERSION_GUARD_BLOCK] reason=INVALID_RECORD current={normalizedCurrent} device={Safe(deviceId)} detail={Safe(local.Error)}");
            return new VersionGuardDecision
            {
                AllowRun = false,
                CurrentVersion = normalizedCurrent,
                Reason = "Dữ liệu bảo vệ phiên bản trên máy không hợp lệ."
            };
        }

        var localHighest = local.Valid && local.Version is not null ? local.Version : current;
        var normalizedLocalHighest = FormatVersion(localHighest);

        ServerVersionPolicy? serverPolicy = null;
        var serverAttempted = serverControlEnabled && !string.IsNullOrWhiteSpace(serverPolicyUrl);
        if (serverAttempted)
        {
            serverPolicy = await TryFetchServerPolicyAsync(
                serverPolicyUrl!,
                deviceId,
                normalizedCurrent,
                normalizedLocalHighest,
                "startup",
                cancellationToken).ConfigureAwait(false);
        }

        var referenceHighest = localHighest;
        if (serverPolicy is not null)
        {
            var serverHighest = ParseVersion(serverPolicy.HighestVersionEver);
            if (serverHighest is not null && serverHighest.CompareTo(referenceHighest) > 0)
                referenceHighest = serverHighest;
        }

        var normalizedReferenceHighest = FormatVersion(referenceHighest);
        var isDowngrade = current.CompareTo(referenceHighest) < 0;

        if (serverPolicy?.AllowCurrentVersion == false)
        {
            var reason = string.IsNullOrWhiteSpace(serverPolicy.Message)
                ? $"Server không cho phép chạy V{normalizedCurrent} trên máy này."
                : serverPolicy.Message.Trim();
            AppendAudit($"[VERSION_GUARD_BLOCK] reason=SERVER_DENY_CURRENT current={normalizedCurrent} highest={normalizedReferenceHighest} mode={NormalizeMode(serverPolicy.Mode)} device={Safe(deviceId)}");
            return new VersionGuardDecision
            {
                AllowRun = false,
                CurrentVersion = normalizedCurrent,
                HighestVersionEver = normalizedReferenceHighest,
                Reason = reason,
                ServerPolicyApplied = true,
                DowngradeMode = NormalizeMode(serverPolicy.Mode)
            };
        }

        if (isDowngrade)
        {
            if (serverPolicy is not null
                && (serverPolicy.AllowCurrentVersion == true || IsPolicyAllowingVersion(serverPolicy, normalizedCurrent)))
            {
                // Không hạ HighestVersionEver local. Server chỉ cấp quyền ngoại lệ cho bản cũ này.
                if (local.Valid)
                    SaveRecordToStores(deviceId, localHighest);
                else
                    SaveRecordToStores(deviceId, referenceHighest);

                AppendAudit($"[VERSION_GUARD_ALLOW_SERVER_ROLLBACK] current={normalizedCurrent} highest={normalizedReferenceHighest} mode={NormalizeMode(serverPolicy.Mode)} device={Safe(deviceId)}");
                return new VersionGuardDecision
                {
                    AllowRun = true,
                    CurrentVersion = normalizedCurrent,
                    HighestVersionEver = normalizedReferenceHighest,
                    Reason = string.IsNullOrWhiteSpace(serverPolicy.Message)
                        ? "Server cho phép chạy phiên bản cũ này."
                        : serverPolicy.Message.Trim(),
                    ServerPolicyApplied = true,
                    DowngradeMode = NormalizeMode(serverPolicy.Mode)
                };
            }

            var reason = serverAttempted && serverPolicy is null && failClosedOnDowngrade
                ? "Không xác minh được quyền hạ phiên bản từ server nên phiên bản cũ bị chặn."
                : serverPolicy is not null && !string.IsNullOrWhiteSpace(serverPolicy.Message)
                    ? serverPolicy.Message.Trim()
                    : $"Máy này đã từng sử dụng phiên bản {normalizedReferenceHighest}. Phiên bản {normalizedCurrent} thấp hơn nên không được phép chạy.";

            AppendAudit($"[VERSION_GUARD_BLOCK] reason=DOWNGRADE current={normalizedCurrent} highest={normalizedReferenceHighest} serverAttempted={serverAttempted} serverApplied={serverPolicy is not null} mode={Safe(serverPolicy?.Mode)} device={Safe(deviceId)}");
            return new VersionGuardDecision
            {
                AllowRun = false,
                CurrentVersion = normalizedCurrent,
                HighestVersionEver = normalizedReferenceHighest,
                Reason = reason,
                ServerPolicyApplied = serverPolicy is not null,
                DowngradeMode = NormalizeMode(serverPolicy?.Mode)
            };
        }

        // Current >= highest đã biết: cho chạy và nâng mốc local nếu cần.
        var target = current.CompareTo(referenceHighest) > 0 ? current : referenceHighest;
        var updated = !local.Valid || current.CompareTo(localHighest) > 0 || referenceHighest.CompareTo(localHighest) > 0;
        var saved = SaveRecordToStores(deviceId, target);
        if (!saved && !local.Valid)
        {
            AppendAudit($"[VERSION_GUARD_BLOCK] reason=INITIAL_SAVE_FAILED current={normalizedCurrent} device={Safe(deviceId)}");
            return new VersionGuardDecision
            {
                AllowRun = false,
                CurrentVersion = normalizedCurrent,
                HighestVersionEver = FormatVersion(target),
                Reason = "Không thể lưu trạng thái bảo vệ phiên bản trên máy.",
                ServerPolicyApplied = serverPolicy is not null,
                DowngradeMode = NormalizeMode(serverPolicy?.Mode)
            };
        }

        AppendAudit($"[VERSION_GUARD_ALLOW] current={normalizedCurrent} highest={FormatVersion(target)} updated={updated} serverApplied={serverPolicy is not null} mode={Safe(serverPolicy?.Mode)} device={Safe(deviceId)}");
        return new VersionGuardDecision
        {
            AllowRun = true,
            CurrentVersion = normalizedCurrent,
            HighestVersionEver = FormatVersion(target),
            Reason = updated ? "Đã cập nhật phiên bản cao nhất của thiết bị." : "Phiên bản hợp lệ.",
            RecordUpdated = updated,
            ServerPolicyApplied = serverPolicy is not null,
            DowngradeMode = NormalizeMode(serverPolicy?.Mode)
        };
    }

    /// <summary>
    /// Refresh policy trước khi hiển thị/cài bản cũ. Nếu server không trả lời thì xóa policy runtime
    /// để giao diện mặc định ẩn/chặn downgrade; không dùng quyền cũ vô thời hạn.
    /// </summary>
    public static async Task<ServerVersionPolicy?> RefreshServerPolicyAsync(
        string currentVersion,
        string deviceId,
        bool serverControlEnabled,
        string? serverPolicyUrl,
        string action = "update_check",
        CancellationToken cancellationToken = default)
    {
        if (!serverControlEnabled || string.IsNullOrWhiteSpace(serverPolicyUrl))
        {
            SetLastServerPolicy(null);
            return null;
        }

        var current = ParseVersion(currentVersion);
        if (current is null)
        {
            SetLastServerPolicy(null);
            return null;
        }

        var local = ReadLocalHighest(deviceId);
        var localHighest = local.Valid && local.Version is not null ? FormatVersion(local.Version) : FormatVersion(current);
        return await TryFetchServerPolicyAsync(
            serverPolicyUrl,
            deviceId,
            FormatVersion(current),
            localHighest,
            action,
            cancellationToken).ConfigureAwait(false);
    }

    public static bool IsDowngradeInstallAllowed(string targetVersion, string currentVersion, out string reason)
    {
        reason = "";
        var target = ParseVersion(targetVersion);
        var current = ParseVersion(currentVersion);
        if (target is null || current is null)
        {
            reason = "Phiên bản không hợp lệ.";
            return false;
        }

        if (target.CompareTo(current) >= 0)
            return true;

        ServerVersionPolicy? policy;
        lock (Sync) policy = _lastServerPolicy is null ? null : ClonePolicy(_lastServerPolicy);

        if (policy is null)
        {
            reason = "Server chưa cấp quyền cài phiên bản cũ.";
            return false;
        }

        var normalizedTarget = FormatVersion(target);
        if (IsPolicyAllowingVersion(policy, normalizedTarget))
            return true;

        reason = string.IsNullOrWhiteSpace(policy.Message)
            ? policy.Mode switch
            {
                ModeAllowSpecific => $"Server không cho phép cài V{normalizedTarget} trên máy này.",
                _ => "Server đang chặn cài phiên bản cũ trên máy này."
            }
            : policy.Message.Trim();
        return false;
    }

    static async Task<ServerVersionPolicy?> TryFetchServerPolicyAsync(
        string rawUrl,
        string deviceId,
        string currentVersion,
        string localHighestVersion,
        string action,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            AppendAudit($"[VERSION_POLICY_FAILED] reason=INVALID_URL url={Safe(rawUrl)} device={Safe(deviceId)}");
            SetLastServerPolicy(null);
            return null;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ToolTikTok-VersionPolicy/1.0");

            var payload = new VersionPolicyRequest
            {
                DeviceId = (deviceId ?? "").Trim(),
                CurrentVersion = currentVersion,
                LocalHighestVersion = localHighestVersion,
                Action = string.IsNullOrWhiteSpace(action) ? "startup" : action.Trim()
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var policy = JsonSerializer.Deserialize<ServerVersionPolicy>(responseJson, JsonRead);
            if (policy is null || !policy.Enabled)
            {
                AppendAudit($"[VERSION_POLICY_FAILED] reason=EMPTY_OR_DISABLED device={Safe(deviceId)}");
                SetLastServerPolicy(null);
                return null;
            }

            policy.Mode = NormalizeMode(policy.Mode);
            policy.AllowedVersions = NormalizeVersionList(policy.AllowedVersions);
            if (ParseVersion(policy.HighestVersionEver) is Version serverHighest)
                policy.HighestVersionEver = FormatVersion(serverHighest);
            else
                policy.HighestVersionEver = "";

            SetLastServerPolicy(policy);
            AppendAudit($"[VERSION_POLICY_OK] device={Safe(deviceId)} action={Safe(action)} mode={policy.Mode} allowed={string.Join(",", policy.AllowedVersions)} highest={Safe(policy.HighestVersionEver)} policyVersion={Safe(policy.PolicyVersion)}");
            return ClonePolicy(policy);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendAudit($"[VERSION_POLICY_FAILED] reason=TIMEOUT device={Safe(deviceId)} action={Safe(action)}");
        }
        catch (Exception ex)
        {
            AppendAudit($"[VERSION_POLICY_FAILED] reason={ex.GetType().Name} device={Safe(deviceId)} action={Safe(action)} message={Safe(ex.Message)}");
        }

        SetLastServerPolicy(null);
        return null;
    }

    static bool IsPolicyAllowingVersion(ServerVersionPolicy policy, string normalizedVersion)
    {
        var mode = NormalizeMode(policy.Mode);
        if (mode == ModeAllowAllOld)
            return true;
        if (mode != ModeAllowSpecific)
            return false;

        return policy.AllowedVersions.Any(v =>
        {
            var parsed = ParseVersion(v);
            return parsed is not null && string.Equals(FormatVersion(parsed), normalizedVersion, StringComparison.OrdinalIgnoreCase);
        });
    }

    static string NormalizeMode(string? raw)
    {
        var mode = (raw ?? "").Trim().ToLowerInvariant();
        return mode switch
        {
            ModeAllowAllOld => ModeAllowAllOld,
            ModeAllowSpecific => ModeAllowSpecific,
            _ => ModeDeny
        };
    }

    static List<string> NormalizeVersionList(IEnumerable<string>? values)
    {
        var result = new List<string>();
        if (values is null) return result;
        foreach (var value in values)
        {
            var parsed = ParseVersion(value);
            if (parsed is null) continue;
            var normalized = FormatVersion(parsed);
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }
        return result;
    }

    static void SetLastServerPolicy(ServerVersionPolicy? policy)
    {
        lock (Sync)
        {
            _lastServerPolicy = policy is null ? null : ClonePolicy(policy);
            _lastServerPolicyFetchedUtc = policy is null ? null : DateTime.UtcNow;
        }
    }

    static ServerVersionPolicy ClonePolicy(ServerVersionPolicy policy)
        => new()
        {
            Enabled = policy.Enabled,
            Mode = policy.Mode,
            AllowedVersions = policy.AllowedVersions.ToList(),
            HighestVersionEver = policy.HighestVersionEver,
            AllowCurrentVersion = policy.AllowCurrentVersion,
            Message = policy.Message,
            PolicyVersion = policy.PolicyVersion
        };

    static (bool Exists, bool Valid, Version? Version, string Error) ReadLocalHighest(string deviceId)
    {
        lock (Sync)
        {
            var file = ReadFileRecord(deviceId);
            var registry = ReadRegistryRecord(deviceId);
            var existing = new[] { file, registry };
            var valid = existing.Where(x => x.Valid && x.Version is not null).ToArray();
            var anyArtifactExists = existing.Any(x => x.Exists);

            if (valid.Length == 0)
            {
                var errors = string.Join("; ", existing.Where(x => x.Exists && !x.Valid)
                    .Select(x => $"{x.Source}:{x.Error}"));
                return (anyArtifactExists, false, null, errors);
            }

            var highest = valid[0].Version!;
            foreach (var item in valid.Skip(1))
            {
                if (item.Version is not null && item.Version.CompareTo(highest) > 0)
                    highest = item.Version;
            }
            return (true, true, highest, "");
        }
    }

    static StoreReadResult ReadFileRecord(string deviceId)
    {
        try
        {
            if (!File.Exists(RecordFilePath))
                return new StoreReadResult(false, false, null, "file", "missing");

            var json = File.ReadAllText(RecordFilePath, Encoding.UTF8);
            return ValidateRecord(json, deviceId, "file");
        }
        catch (Exception ex)
        {
            return new StoreReadResult(true, false, null, "file", ex.GetType().Name);
        }
    }

    static StoreReadResult ReadRegistryRecord(string deviceId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            if (key is null)
                return new StoreReadResult(false, false, null, "registry", "missing");

            var json = Convert.ToString(key.GetValue(RegistryValueName));
            if (string.IsNullOrWhiteSpace(json))
                return new StoreReadResult(false, false, null, "registry", "missing");

            return ValidateRecord(json, deviceId, "registry");
        }
        catch (Exception ex)
        {
            return new StoreReadResult(true, false, null, "registry", ex.GetType().Name);
        }
    }

    static StoreReadResult ValidateRecord(string json, string deviceId, string source)
    {
        try
        {
            var record = JsonSerializer.Deserialize<VersionRecord>(json, JsonRead);
            if (record is null)
                return new StoreReadResult(true, false, null, source, "empty");
            if (record.Schema != RecordSchema)
                return new StoreReadResult(true, false, null, source, "schema");
            if (!string.Equals((record.DeviceId ?? "").Trim(), (deviceId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return new StoreReadResult(true, false, null, source, "device_mismatch");

            var version = ParseVersion(record.HighestVersion);
            if (version is null)
                return new StoreReadResult(true, false, null, source, "version");

            var expected = ComputeSignature(deviceId, FormatVersion(version));
            if (!FixedEquals(expected, record.Signature))
                return new StoreReadResult(true, false, null, source, "signature");

            return new StoreReadResult(true, true, version, source, "");
        }
        catch (Exception ex)
        {
            return new StoreReadResult(true, false, null, source, ex.GetType().Name);
        }
    }

    static bool SaveRecordToStores(string deviceId, Version version)
    {
        lock (Sync)
        {
            var normalized = FormatVersion(version);
            var record = new VersionRecord
            {
                Schema = RecordSchema,
                DeviceId = (deviceId ?? "").Trim(),
                HighestVersion = normalized,
                UpdatedUtc = DateTime.UtcNow,
                Signature = ComputeSignature(deviceId, normalized)
            };
            var json = JsonSerializer.Serialize(record, JsonWrite);

            var fileOk = TryWriteFile(json);
            var registryOk = TryWriteRegistry(json);
            if (!fileOk || !registryOk)
                AppendAudit($"[VERSION_GUARD_STORE_REPAIR] file={fileOk} registry={registryOk} highest={normalized} device={Safe(deviceId)}");
            return fileOk || registryOk;
        }
    }

    static bool TryWriteFile(string json)
    {
        try
        {
            Directory.CreateDirectory(GuardRoot);
            var temp = RecordFilePath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, RecordFilePath, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    static bool TryWriteRegistry(string json)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null) return false;
            key.SetValue(RegistryValueName, json, RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    static string ComputeSignature(string deviceId, string version)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(SignatureSalt + "|" + (deviceId ?? "").Trim().ToUpperInvariant()));
        using var hmac = new HMACSHA256(key);
        var data = Encoding.UTF8.GetBytes($"{RecordSchema}|{(deviceId ?? "").Trim().ToUpperInvariant()}|{version}");
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    static bool FixedEquals(string? left, string? right)
    {
        left = (left ?? "").Trim().ToUpperInvariant();
        right = (right ?? "").Trim().ToUpperInvariant();
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            text = text[1..];

        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4) return null;

        var values = new List<int>(4);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var n) || n < 0) return null;
            values.Add(n);
        }
        while (values.Count < 3) values.Add(0);

        return values.Count == 4
            ? new Version(values[0], values[1], values[2], values[3])
            : new Version(values[0], values[1], values[2]);
    }

    static string FormatVersion(Version version)
        => version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    static string GuardRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolTikTok", "version_guard");

    static string RecordFilePath => Path.Combine(GuardRoot, RecordFileName);
    static string AuditPath => Path.Combine(GuardRoot, AuditFileName);

    static void AppendAudit(string message)
    {
        try
        {
            Directory.CreateDirectory(GuardRoot);
            File.AppendAllText(AuditPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { }
    }

    static string Safe(string? value)
        => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
}
