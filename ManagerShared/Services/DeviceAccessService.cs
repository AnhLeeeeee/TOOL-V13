using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV12.Services;

/// <summary>
/// Khóa Tool theo thiết bị mà không phụ thuộc vào thư mục cài đặt.
///
/// Cơ chế chuyển tiếp:
/// - Máy đã có Tool trước mốc GrandfatherCutoffUtc được tự kích hoạt một lần dựa trên
///   dữ liệu runtime cũ hoặc marker do Setup nâng cấp tạo ra.
/// - Sau khi kích hoạt, dấu .device_access_migrated được ghi cạnh EXE. Nếu cả thư mục
///   Tool bị copy sang máy khác, fingerprint không khớp và dấu này ngăn tự kích hoạt lại.
/// - Máy cài mới sau mốc không có bằng chứng cũ => PENDING/BLOCKED.
/// - device_access_policy.json trên GitHub có thể khóa chạy, khóa update hoặc duyệt ngoại lệ.
///
/// Lưu ý: đây là lớp khóa phía client. Người có toàn quyền chỉnh sửa binary/source vẫn có thể
/// gỡ kiểm tra. Mục tiêu là quản lý phát hành/cài đặt bình thường giữa các máy khách.
/// </summary>
public static class DeviceAccessService
{
    // Mốc chuyển tiếp được chốt ngay sau khi nhận bản Tool hiện tại:
    // 01:35 ngày 21/09/2026 (GMT+7) = 18:35 ngày 20/09/2026 UTC.
    // Chỉ cài đặt/dữ liệu có từ trước mốc này mới được coi là "máy đã có Tool".
    public static readonly DateTime GrandfatherCutoffUtc = new(2026, 9, 20, 18, 35, 0, DateTimeKind.Utc);

    public const string DefaultPolicyUrl =
        "https://raw.githubusercontent.com/AnhLeeeeee/TOOL-V13/main/device_access_policy.json";

    const string MigrationConsumedFileName = ".device_access_migrated";
    const string UpgradeMarkerFileName = ".device_access_upgrade_marker";
    const string IdentityFileName = "device.json";
    const string PolicyCacheFileName = "policy-cache.json";
    const string AuditFileName = "device-access.log";

    static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };
    static readonly object Sync = new();

    static DeviceAccessDecision? _lastDecision;

    public sealed class DeviceAccessDecision
    {
        public bool AllowRun { get; init; }
        public bool AllowUpdate { get; init; }
        public string DeviceId { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool Activated { get; init; }
        public bool RemotePolicyApplied { get; init; }
        public string ActivationSource { get; init; } = "";
        public bool VersionControlEnabled { get; init; }
        public string VersionPolicyUrl { get; init; } = "";
        public bool VersionPolicyFailClosedOnDowngrade { get; init; } = true;
    }

    sealed class DeviceIdentity
    {
        public string DeviceId { get; set; } = "";
        public string FingerprintHash { get; set; } = "";
        public bool Activated { get; set; }
        public string ActivationSource { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime? ActivatedUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    sealed class VersionControlBootstrap
    {
        public bool Enabled { get; set; }
        public string PolicyUrl { get; set; } = "";
        public bool FailClosedOnDowngrade { get; set; } = true;
    }

    sealed class DeviceAccessPolicy
    {
        public bool Enabled { get; set; } = true;
        public bool EnforceAllowList { get; set; }
        public List<string> AllowedDeviceIds { get; set; } = new();
        public List<string> BlockedDeviceIds { get; set; } = new();
        public List<string> BlockedUpdateDeviceIds { get; set; } = new();
        public string BlockMessage { get; set; } = "Thiết bị này chưa được cấp quyền sử dụng Tool.";
        public string PolicyVersion { get; set; } = "1";
        public VersionControlBootstrap VersionControl { get; set; } = new();
    }

    sealed class PolicyCacheEnvelope
    {
        public DateTime FetchedUtc { get; set; }
        public DeviceAccessPolicy? Policy { get; set; }
    }

    public static DeviceAccessDecision? LastDecision
    {
        get { lock (Sync) return _lastDecision; }
    }

    public static string AuditLogPath => AuditPath;

    public static string GetDeviceId(string baseDir)
    {
        try
        {
            var identity = EnsureIdentity(baseDir, allowGrandfatherMigration: false, out _);
            return identity.DeviceId;
        }
        catch
        {
            return "TT-UNKNOWN";
        }
    }

    public static DeviceAccessDecision EvaluateLocalAccess(string baseDir, string currentVersion)
    {
        var identity = EnsureIdentity(baseDir, allowGrandfatherMigration: true, out var reason);
        var decision = new DeviceAccessDecision
        {
            AllowRun = identity.Activated,
            AllowUpdate = identity.Activated,
            DeviceId = identity.DeviceId,
            Reason = reason,
            Activated = identity.Activated,
            RemotePolicyApplied = false,
            ActivationSource = identity.ActivationSource,
            VersionControlEnabled = false,
            VersionPolicyUrl = "",
            VersionPolicyFailClosedOnDowngrade = true
        };
        lock (Sync) _lastDecision = decision;
        AppendAudit(baseDir,
            $"[DEVICE_ACCESS_LOCAL] id={identity.DeviceId} version={currentVersion} activated={identity.Activated} " +
            $"source={SafeOneLine(identity.ActivationSource)} allowRun={decision.AllowRun} reason={SafeOneLine(reason)}");
        return decision;
    }

    public static async Task<DeviceAccessDecision> EvaluateStartupAsync(
        string baseDir,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var identity = EnsureIdentity(baseDir, allowGrandfatherMigration: true, out var localReason);
        var localAllowed = identity.Activated;
        var allowRun = localAllowed;
        var allowUpdate = localAllowed;
        var reason = localReason;
        var remoteApplied = false;
        var versionControlEnabled = false;
        var versionPolicyUrl = "";
        var versionPolicyFailClosed = true;

        var policy = await TryLoadRemotePolicyAsync(cancellationToken).ConfigureAwait(false);
        if (policy is not null && policy.Enabled)
        {
            remoteApplied = true;
            versionControlEnabled = policy.VersionControl?.Enabled == true;
            versionPolicyUrl = (policy.VersionControl?.PolicyUrl ?? "").Trim();
            versionPolicyFailClosed = policy.VersionControl?.FailClosedOnDowngrade ?? true;
            var id = identity.DeviceId;

            if (ContainsId(policy.BlockedDeviceIds, id))
            {
                allowRun = false;
                allowUpdate = false;
                reason = string.IsNullOrWhiteSpace(policy.BlockMessage)
                    ? "Thiết bị này đã bị khóa."
                    : policy.BlockMessage.Trim();
            }
            else
            {
                // Máy mới/PENDING chỉ được duyệt từ xa khi ID nằm trong allowedDeviceIds.
                if (!identity.Activated && ContainsId(policy.AllowedDeviceIds, id))
                {
                    identity.Activated = true;
                    identity.ActivationSource = "remote_allowlist";
                    identity.ActivatedUtc = DateTime.UtcNow;
                    identity.LastSeenUtc = DateTime.UtcNow;
                    SaveIdentity(identity);
                    TryWriteMigrationConsumedMarker(baseDir, identity, "remote_allowlist");
                    allowRun = true;
                    allowUpdate = true;
                    reason = "Thiết bị đã được duyệt từ danh sách cho phép.";
                }

                if (policy.EnforceAllowList && !ContainsId(policy.AllowedDeviceIds, id))
                {
                    allowRun = false;
                    allowUpdate = false;
                    reason = string.IsNullOrWhiteSpace(policy.BlockMessage)
                        ? "Thiết bị không nằm trong danh sách cho phép."
                        : policy.BlockMessage.Trim();
                }
                else if (allowRun && ContainsId(policy.BlockedUpdateDeviceIds, id))
                {
                    allowUpdate = false;
                }
            }
        }

        var decision = new DeviceAccessDecision
        {
            AllowRun = allowRun,
            AllowUpdate = allowUpdate,
            DeviceId = identity.DeviceId,
            Reason = reason,
            Activated = identity.Activated,
            RemotePolicyApplied = remoteApplied,
            ActivationSource = identity.ActivationSource,
            VersionControlEnabled = versionControlEnabled,
            VersionPolicyUrl = versionPolicyUrl,
            VersionPolicyFailClosedOnDowngrade = versionPolicyFailClosed
        };

        lock (Sync) _lastDecision = decision;
        AppendAudit(baseDir,
            $"[DEVICE_ACCESS_STARTUP] id={identity.DeviceId} version={currentVersion} activated={identity.Activated} " +
            $"source={SafeOneLine(identity.ActivationSource)} allowRun={allowRun} allowUpdate={allowUpdate} " +
            $"remote={remoteApplied} versionControl={versionControlEnabled} versionPolicyUrl={SafeOneLine(versionPolicyUrl)} " +
            $"reason={SafeOneLine(reason)}");
        return decision;
    }

    /// <summary>
    /// Kiểm tra lại policy trước khi hiển thị/tải bản cập nhật. Nếu policy từ xa tạm lỗi,
    /// dùng cache cuối cùng; nếu chưa từng có cache thì giữ quyết định local hiện tại.
    /// </summary>
    public static async Task<DeviceAccessDecision> RefreshForUpdateAsync(
        string baseDir,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateStartupAsync(baseDir, currentVersion, cancellationToken).ConfigureAwait(false);
    }

    static DeviceIdentity EnsureIdentity(string baseDir, bool allowGrandfatherMigration, out string reason)
    {
        Directory.CreateDirectory(DeviceAccessRoot);
        var fingerprint = ComputeMachineFingerprint();
        var identity = LoadIdentity();

        if (identity is not null && !string.IsNullOrWhiteSpace(identity.DeviceId))
        {
            if (FixedEquals(identity.FingerprintHash, fingerprint))
            {
                identity.LastSeenUtc = DateTime.UtcNow;
                SaveIdentity(identity);
                reason = identity.Activated
                    ? "Thiết bị đã được kích hoạt."
                    : "Thiết bị đang chờ cấp quyền.";
                return identity;
            }

            // device.json bị copy từ máy khác: không cho dùng ID đã kích hoạt của máy cũ.
            TryArchiveMismatchedIdentity(identity);
            identity = CreatePendingIdentity(fingerprint);
            SaveIdentity(identity);
            reason = "Dữ liệu kích hoạt thuộc máy khác; thiết bị hiện tại chưa được cấp quyền.";
            AppendAudit(baseDir, $"[DEVICE_FINGERPRINT_MISMATCH] newId={identity.DeviceId}");
            return identity;
        }

        identity = CreatePendingIdentity(fingerprint);

        if (allowGrandfatherMigration)
        {
            var consumed = File.Exists(MigrationConsumedPath(baseDir));
            var upgradeMarker = File.Exists(UpgradeMarkerPath(baseDir));
            string evidence = string.Empty;
            var legacyEvidence = !consumed && HasGrandfatherEvidence(baseDir, out evidence);

            if (!consumed && (upgradeMarker || legacyEvidence))
            {
                identity.Activated = true;
                identity.ActivationSource = upgradeMarker ? "existing_install_upgrade" : "grandfather_runtime";
                identity.ActivatedUtc = DateTime.UtcNow;
                identity.LastSeenUtc = DateTime.UtcNow;
                SaveIdentity(identity);
                TryWriteMigrationConsumedMarker(baseDir, identity, identity.ActivationSource);
                TryDeleteUpgradeMarker(baseDir);
                reason = upgradeMarker
                    ? "Máy đã có Tool trước khi nâng cấp; đã tự kích hoạt."
                    : $"Máy có dữ liệu Tool cũ ({evidence}); đã tự kích hoạt.";
                AppendAudit(baseDir,
                    $"[DEVICE_GRANDFATHER_ACTIVATED] id={identity.DeviceId} source={identity.ActivationSource} evidence={SafeOneLine(evidence)}");
                return identity;
            }
        }

        SaveIdentity(identity);
        reason = File.Exists(MigrationConsumedPath(baseDir))
            ? "Bản Tool này đã được kích hoạt trên một thiết bị khác hoặc dữ liệu nhận diện máy đã thay đổi."
            : "Đây là thiết bị/cài đặt mới và chưa được cấp quyền.";
        return identity;
    }

    static DeviceIdentity CreatePendingIdentity(string fingerprint)
        => new()
        {
            DeviceId = NewDeviceId(),
            FingerprintHash = fingerprint,
            Activated = false,
            ActivationSource = "pending",
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

    static bool HasGrandfatherEvidence(string baseDir, out string evidence)
    {
        evidence = "";
        try
        {
            // Dấu hiệu mạnh nhất: chính thư mục cài đặt đã tồn tại trước mốc khóa.
            // Copy/cài mới sau mốc sẽ có CreationTime mới và không qua nhánh này.
            if (Directory.Exists(baseDir) && Directory.GetCreationTimeUtc(baseDir) <= GrandfatherCutoffUtc)
            {
                evidence = "install_directory";
                return true;
            }

            var candidates = new[]
            {
                Path.Combine(baseDir, "profiles.json"),
                Path.Combine(baseDir, "manager_update.json"),
                Path.Combine(baseDir, "logs", "manager-v13.log"),
                Path.Combine(baseDir, "manager_default_config_sync.json")
            };
            foreach (var path in candidates)
            {
                if (IsOldFile(path))
                {
                    evidence = Path.GetFileName(path);
                    return true;
                }
            }

            foreach (var path in Directory.EnumerateFiles(baseDir, "worker-profile-*.log", SearchOption.TopDirectoryOnly))
            {
                if (IsOldFile(path))
                {
                    evidence = Path.GetFileName(path);
                    return true;
                }
            }

            var profilesDir = Path.Combine(baseDir, "profiles");
            if (Directory.Exists(profilesDir))
            {
                foreach (var path in Directory.EnumerateFiles(profilesDir, "*", SearchOption.AllDirectories).Take(20))
                {
                    if (IsOldFile(path))
                    {
                        evidence = "profiles/" + Path.GetFileName(path);
                        return true;
                    }
                }
            }

            var chromeProfiles = Path.Combine(baseDir, "TikTokProfiles");
            if (Directory.Exists(chromeProfiles))
            {
                foreach (var dir in Directory.EnumerateDirectories(chromeProfiles).Take(20))
                {
                    if (Directory.GetCreationTimeUtc(dir) <= GrandfatherCutoffUtc)
                    {
                        evidence = "TikTokProfiles/" + Path.GetFileName(dir);
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    static bool IsOldFile(string path)
    {
        try
        {
            return File.Exists(path)
                   && new FileInfo(path).Length > 0
                   && File.GetCreationTimeUtc(path) <= GrandfatherCutoffUtc;
        }
        catch { return false; }
    }

    static async Task<DeviceAccessPolicy?> TryLoadRemotePolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ToolTikTok-DeviceAccess/1.0");
            var uri = AddCacheBuster(DefaultPolicyUrl);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return LoadCachedPolicy(maxAge: TimeSpan.FromDays(14));

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var policy = JsonSerializer.Deserialize<DeviceAccessPolicy>(json, ReadJson);
            if (policy is null) return LoadCachedPolicy(maxAge: TimeSpan.FromDays(14));
            SaveCachedPolicy(policy);
            return policy;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LoadCachedPolicy(maxAge: TimeSpan.FromDays(14));
        }
        catch
        {
            return LoadCachedPolicy(maxAge: TimeSpan.FromDays(14));
        }
    }

    static Uri AddCacheBuster(string raw)
    {
        var builder = new UriBuilder(raw);
        var token = "_da=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = (builder.Query ?? "").TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? token : query + "&" + token;
        return builder.Uri;
    }

    static DeviceAccessPolicy? LoadCachedPolicy(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(PolicyCachePath)) return null;
            var envelope = JsonSerializer.Deserialize<PolicyCacheEnvelope>(File.ReadAllText(PolicyCachePath), ReadJson);
            if (envelope?.Policy is null) return null;
            if (DateTime.UtcNow - envelope.FetchedUtc > maxAge) return null;
            return envelope.Policy;
        }
        catch { return null; }
    }

    static void SaveCachedPolicy(DeviceAccessPolicy policy)
    {
        try
        {
            Directory.CreateDirectory(DeviceAccessRoot);
            AtomicWriteJson(PolicyCachePath, new PolicyCacheEnvelope
            {
                FetchedUtc = DateTime.UtcNow,
                Policy = policy
            });
        }
        catch { }
    }

    static DeviceIdentity? LoadIdentity()
    {
        try
        {
            if (!File.Exists(IdentityPath)) return null;
            return JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(IdentityPath), ReadJson);
        }
        catch { return null; }
    }

    static void SaveIdentity(DeviceIdentity identity)
    {
        Directory.CreateDirectory(DeviceAccessRoot);
        AtomicWriteJson(IdentityPath, identity);
    }

    static void AtomicWriteJson<T>(string path, T value)
    {
        // Manager + nhiều Worker có thể cùng cập nhật LastSeen. Dùng temp riêng theo process/GUID
        // để không đụng chung device.json.tmp giữa các process.
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, WriteJson), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    static void TryArchiveMismatchedIdentity(DeviceIdentity oldIdentity)
    {
        try
        {
            Directory.CreateDirectory(DeviceAccessRoot);
            var archive = Path.Combine(DeviceAccessRoot, $"device-mismatch-{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.Copy(IdentityPath, archive, false);
        }
        catch { }
    }

    static void TryWriteMigrationConsumedMarker(string baseDir, DeviceIdentity identity, string source)
    {
        try
        {
            var payload = new
            {
                deviceId = identity.DeviceId,
                activatedUtc = DateTime.UtcNow,
                source
            };
            File.WriteAllText(
                MigrationConsumedPath(baseDir),
                JsonSerializer.Serialize(payload, WriteJson),
                new UTF8Encoding(false));
        }
        catch { }
    }

    static void TryDeleteUpgradeMarker(string baseDir)
    {
        try
        {
            var path = UpgradeMarkerPath(baseDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    static string ComputeMachineFingerprint()
    {
        var machineGuid = "";
        try
        {
            machineGuid = Convert.ToString(
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "")) ?? "";
        }
        catch { }

        var sid = "";
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { }

        var volume = GetSystemVolumeSerial();
        var basis = string.Join("|", new[]
        {
            machineGuid.Trim(),
            volume,
            Environment.MachineName.Trim(),
            sid.Trim(),
            Environment.Is64BitOperatingSystem ? "x64" : "x86"
        });
        return Sha256Hex(basis);
    }

    static string GetSystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root)) return "";
            if (GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0))
                return serial.ToString("X8");
        }
        catch { }
        return "";
    }

    static string NewDeviceId()
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        return $"TT-{raw[..5]}-{raw.Substring(5, 5)}-{raw.Substring(10, 5)}-{raw.Substring(15, 5)}";
    }

    static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));

    static bool FixedEquals(string? left, string? right)
    {
        left = (left ?? "").Trim().ToUpperInvariant();
        right = (right ?? "").Trim().ToUpperInvariant();
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    static bool ContainsId(IEnumerable<string>? values, string id)
        => values?.Any(v => string.Equals((v ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)) == true;

    static string MigrationConsumedPath(string baseDir) => Path.Combine(baseDir, MigrationConsumedFileName);
    static string UpgradeMarkerPath(string baseDir) => Path.Combine(baseDir, UpgradeMarkerFileName);

    static string DeviceAccessRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolTikTok", "device_access");

    static string IdentityPath => Path.Combine(DeviceAccessRoot, IdentityFileName);
    static string PolicyCachePath => Path.Combine(DeviceAccessRoot, PolicyCacheFileName);
    static string AuditPath => Path.Combine(DeviceAccessRoot, AuditFileName);

    static void AppendAudit(string baseDir, string message)
    {
        try
        {
            Directory.CreateDirectory(DeviceAccessRoot);
            File.AppendAllText(AuditPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { }
    }

    static string SafeOneLine(string? value)
        => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
