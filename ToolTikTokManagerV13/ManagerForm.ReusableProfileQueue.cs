using System.Text;
using System.Text.Json;
using ToolTikTokV12.Models;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class ReusableProfileQueueEntry
    {
        public string ProfileName { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string Username { get; set; } = "";
        public double TotalRunSeconds { get; set; }
        public bool IsManual { get; set; }

        // Profile được tạo để bù nhưng TikTok chưa phản ánh tên mới ngay.
        // Vẫn dùng CHÍNH queue Chờ dùng lại; lane này được vét SAU profile chờ
        // bình thường nhưng TRƯỚC khi tạo profile mới, với min-age + one-attempt/request.
        public bool NameSyncPending { get; set; }
        public DateTime? NameSyncQueuedUtc { get; set; }
        public int NameSyncCheckCount { get; set; }

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
    }

    sealed class ReusableProfileFailureEntry
    {
        public double FailedAtTotalRunSeconds { get; set; }
        public DateTime FailedUtc { get; set; } = DateTime.UtcNow;
        public string Reason { get; set; } = "";
    }

    sealed class ReusableProfileQueueDocument
    {
        public int Version { get; set; } = 3;
        public List<ReusableProfileQueueEntry> Pending { get; set; } = new();
        public List<string> ExcludedProfiles { get; set; } = new();
        public Dictionary<string, ReusableProfileFailureEntry> FailedProfiles { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    sealed record ReusableProfileQueueView(
        int Position,
        string ProfileName,
        string Username,
        TimeSpan TotalRuntime,
        bool IsManual);

    readonly object _reusableProfileQueueLock = new();
    readonly SemaphoreSlim _reusableProfileRefreshGate = new(1, 1);
    ReusableProfileQueueDocument _reusableProfileQueueCache = new();
    Dictionary<string, string> _reusableProfileReasonCache =
        new(StringComparer.OrdinalIgnoreCase);
    bool _reusableProfileQueueLoaded;

    string ReusableProfileQueuePath
        => Path.Combine(_baseDir, "manager_reuse_profile_queue.json");

    async Task RefreshReusableProfileQueueAsync(
        string source,
        CancellationToken ct = default)
    {
        if (_closing || IsDisposed || Disposing)
            return;

        source = string.IsNullOrWhiteSpace(source)
            ? "unknown"
            : source.Trim();

        // Mọi lượt quét được xếp hàng tuần tự. Trước đây WaitAsync(0) làm nút
        // "Quét chờ" có thể thoát ngay nếu quét nền đang chạy nhưng UI vẫn báo
        // "Đã quét xong". Chờ gate giúp lượt quét thủ công luôn thực sự chạy.
        await _reusableProfileRefreshGate.WaitAsync(ct);

        try
        {
            var busyProfiles =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var ctx in _contexts.Values.ToList())
            {
                // Với kho Chờ dùng lại, Worker/tab còn tồn tại KHÔNG đồng nghĩa
                // profile đang bận. Sau Stop/AutoClose, Worker hoặc tab có thể còn sống
                // một nhịp trong khi runtime đã STOPPED; đây chính là cửa sổ race làm
                // PRF chờ tự động bị loại rồi rơi sang tạo PRF mới.
                // IsReusableProfileBusy() chỉ coi RUNNING/PAUSED/RECOVERING hoặc
                // trạng thái chưa xác minh là bận; STOPPED vẫn được phép reuse.
                if (IsReusableProfileBusy(ctx.Profile.Name))
                    busyProfiles.Add(ctx.Profile.Name);
            }

            // Đọc chung account + hai trạng thái Excel một lần cho mỗi lượt quét.
            // Soft-retired được tự quay lại queue khi Tên/ảnh=DONE.
            // +auto không cần DONE nữa; chỉ +auto=FAIL mới tiếp tục chặn để tránh
            // tự hồi sinh profile đã có lỗi cứng/đã được đánh dấu không nên retry.
            var accountPoolSnapshot = await RunAccountPoolIoAsync(
                () => (
                    Accounts: _accountPoolService.Load(),
                    AutoProfileResults: _accountPoolService.LoadAutoProfileResults(),
                    IdentityResults: _accountPoolService.GetIdentityResults()),
                ct);

            var accounts = accountPoolSnapshot.Accounts;
            var autoProfileResults = accountPoolSnapshot.AutoProfileResults;
            var identityResults = accountPoolSnapshot.IdentityResults;

            var catalog = await Task.Run(
                () => _profileService.Load(),
                ct);

            var runtimeByProfile = await Task.Run(
                () =>
                {
                    var result =
                        new Dictionary<string, double>(
                            StringComparer.OrdinalIgnoreCase);

                    foreach (var profile in catalog.Profiles)
                    {
                        ct.ThrowIfCancellationRequested();

                        result[profile.Name] =
                            ReadReusableProfileTotalSeconds(profile);
                    }

                    return result;
                },
                ct);

            ReusableProfileQueueDocument previous;

            lock (_reusableProfileQueueLock)
            {
                previous = CloneReusableProfileQueueDocument(
                    EnsureReusableProfileQueueLoadedUnsafe());
            }

            var previousByProfile =
                previous.Pending
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x.ProfileName))
                    .GroupBy(
                        x => x.ProfileName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => x.First(),
                        StringComparer.OrdinalIgnoreCase);

            var excludedProfiles =
                new HashSet<string>(
                    previous.ExcludedProfiles
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()),
                    StringComparer.OrdinalIgnoreCase);

            var failedProfiles =
                new Dictionary<string, ReusableProfileFailureEntry>(
                    previous.FailedProfiles,
                    StringComparer.OrdinalIgnoreCase);

            var accountsByProfile =
                accounts
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x.AssignedProfile))
                    .GroupBy(
                        x => x.AssignedProfile.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => x.OrderBy(a => a.SourceRow).First(),
                        StringComparer.OrdinalIgnoreCase);

            // Ngưỡng quét tự động dùng CHÍNH cấu hình "Tự động khi Tổng thời gian
            // Automation chạy đủ X giờ". Không còn cố định 1 giờ.
            var automaticMaxHours =
                Math.Clamp(
                    _autoCloseSettings.RunHours,
                    3,
                    24);

            var automaticMaxTotalSeconds =
                TimeSpan.FromHours(
                    automaticMaxHours).TotalSeconds;

            var eligible =
                new List<ReusableProfileQueueEntry>();

            var reasons =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            // Snapshot supply-state một lần cho cả lượt quét. Tránh đọc JSON từ đĩa
            // lặp lại theo từng profile khi VM đang chạy nhiều Chrome.
            ProfileSupplyStateDocument supplyStateSnapshot;
            lock (_profileSupplyStateLock)
            {
                supplyStateSnapshot = LoadProfileSupplyStateDocumentUnsafe();
            }

            var refreshUtc = DateTime.UtcNow;

            foreach (var profile in catalog.Profiles)
            {
                ct.ThrowIfCancellationRequested();

                var profileName =
                    (profile.Name ?? "").Trim();

                if (profileName.Length == 0)
                    continue;

                // Xác định trạng thái đã có trong queue cũ trước khi đánh giá điều kiện.
                var hasOldEntry =
                    previousByProfile.TryGetValue(
                        profileName,
                        out var oldEntry);

                var isManual =
                    hasOldEntry
                    && oldEntry!.IsManual;

                var nameSyncPending =
                    hasOldEntry
                    && oldEntry!.NameSyncPending;

                if (!IsReusableProfileActuallyCreated(profile))
                {
                    reasons[profileName] = "CHƯA KHỞI TẠO CHROME";
                    continue;
                }

                if (!accountsByProfile.TryGetValue(
                        profileName,
                        out var account))
                {
                    reasons[profileName] = "CHƯA GÁN TÀI KHOẢN";
                    continue;
                }

                if (!runtimeByProfile.TryGetValue(
                        profileName,
                        out var totalSeconds))
                {
                    totalSeconds = 0;
                }

                totalSeconds =
                    Math.Max(
                        0,
                        totalSeconds);

                // Lane đặc biệt nhưng vẫn nằm trong CHÍNH queue Chờ dùng lại:
                // profile vừa tạo đã Save tên nhưng TikTok có thể cập nhật chậm.
                // Giữ entry qua mọi lượt refresh, nhưng TryUseReusableProfileQueueAsync
                // sẽ bỏ qua lane này cho tới khi recovery sweep chủ động kiểm tra.
                if (nameSyncPending)
                {
                    var pendingNote = (account.Note ?? "").Trim();
                    if (pendingNote.Equals("ban", StringComparison.OrdinalIgnoreCase))
                    {
                        reasons[profileName] = "GHI CHÚ: ban";
                        continue;
                    }

                    if (excludedProfiles.Contains(profileName))
                    {
                        reasons[profileName] = "ĐÃ BỎ CHỜ";
                        continue;
                    }

                    if (totalSeconds >= automaticMaxTotalSeconds)
                    {
                        reasons[profileName] = $"TỔNG >= {automaticMaxHours}H";
                        continue;
                    }

                    failedProfiles.Remove(profileName);
                    reasons[profileName] =
                        busyProfiles.Contains(profileName)
                            ? "CHỜ ĐỒNG BỘ TÊN · PROFILE ĐANG MỞ"
                            : $"CHỜ ĐỒNG BỘ TÊN · ĐÃ KIỂM TRA {Math.Max(0, oldEntry!.NameSyncCheckCount)} LẦN";

                    eligible.Add(
                        new ReusableProfileQueueEntry
                        {
                            ProfileName = profileName,
                            AccountId = account.Id,
                            Username = account.Username,
                            TotalRunSeconds = totalSeconds,
                            IsManual = false,
                            NameSyncPending = true,
                            NameSyncQueuedUtc = oldEntry!.NameSyncQueuedUtc ?? oldEntry.AddedUtc,
                            NameSyncCheckCount = Math.Max(0, oldEntry.NameSyncCheckCount),
                            AddedUtc = oldEntry.AddedUtc,
                            LastCheckedUtc = oldEntry.LastCheckedUtc
                        });

                    continue;
                }

                if (isManual)
                {
                    // Queue thủ công giữ nguyên ưu tiên. Chỉ Ghi chú=ban là chặn tuyệt đối.
                    if (string.Equals(
                            (account.Note ?? "").Trim(),
                            "ban",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reasons[profileName] = "GHI CHÚ: ban";
                        continue;
                    }

                    failedProfiles.Remove(profileName);
                    reasons[profileName] =
                        busyProfiles.Contains(profileName)
                            ? "ĐANG CHỜ · THỦ CÔNG · PROFILE ĐANG MỞ"
                            : "ĐANG CHỜ · THỦ CÔNG";

                    eligible.Add(
                        new ReusableProfileQueueEntry
                        {
                            ProfileName = profileName,
                            AccountId = account.Id,
                            Username = account.Username,
                            TotalRunSeconds = totalSeconds,
                            IsManual = true,
                            AddedUtc = oldEntry.AddedUtc,
                            LastCheckedUtc = DateTime.UtcNow
                        });

                    // Nếu profile đang mở thì vẫn giữ queue thủ công,
                    // chỉ chưa được lấy ra sử dụng.
                    continue;
                }

                supplyStateSnapshot.Profiles.TryGetValue(profileName, out var supplyState);

                var persistedRetired =
                    supplyState is not null
                    && (supplyState.State ?? "").Trim().Equals(
                        "retired",
                        StringComparison.OrdinalIgnoreCase);

                var retired =
                    _autoReplacementRetiredProfiles.Contains(profileName)
                    || persistedRetired;

                // HARD RETIRED: BAN/LOGIN_BAN/TIME là điểm kết thúc vòng đời.
                // Không tự hồi sinh dù Excel DONE/DONE; chỉ thao tác thủ công mới có thể
                // can thiệp theo chủ đích. Các retired khác (FAULT/lỗi kỹ thuật/legacy)
                // là SOFT RETIRED và có thể tự quay lại queue sau khi xác minh an toàn.
                if (retired && IsHardReusableProfileRetired(supplyState))
                {
                    var hardSource =
                        string.IsNullOrWhiteSpace(supplyState?.Source)
                            ? "hard-retired"
                            : supplyState!.Source.Trim();

                    reasons[profileName] = $"RETIRED CỨNG · {hardSource}";
                    continue;
                }

                if (excludedProfiles.Contains(profileName))
                {
                    reasons[profileName] = "ĐÃ BỎ CHỜ";
                    continue;
                }

                if (busyProfiles.Contains(profileName))
                {
                    // Entry auto đã có từ trước nhưng đúng lúc refresh Worker/tab đang
                    // OPENING hoặc runtime chưa xác minh được thì KHÔNG được làm rơi
                    // khỏi queue. Giữ entry để lượt bù chờ xác minh lại, thay vì coi
                    // như đã hết PRF chờ rồi tạo PRF mới. Profile thực sự RUNNING/PAUSED/
                    // RECOVERING vẫn bị loại như cũ.
                    if (hasOldEntry
                        && !oldEntry!.IsManual
                        && !oldEntry.NameSyncPending
                        && IsReusableProfileTransientBusy(profileName))
                    {
                        reasons[profileName] = "ĐANG CHỜ · TỰ ĐỘNG · ĐANG XÁC MINH TRẠNG THÁI";
                        eligible.Add(
                            new ReusableProfileQueueEntry
                            {
                                ProfileName = profileName,
                                AccountId = account.Id,
                                Username = account.Username,
                                TotalRunSeconds = totalSeconds,
                                IsManual = false,
                                AddedUtc = oldEntry.AddedUtc,
                                LastCheckedUtc = oldEntry.LastCheckedUtc
                            });
                        continue;
                    }

                    reasons[profileName] = "ĐANG MỞ/CHẠY";
                    continue;
                }

                var note = (account.Note ?? "").Trim();
                if (note.Length > 0)
                {
                    reasons[profileName] = $"GHI CHÚ: {note}";
                    continue;
                }

                if (totalSeconds >= automaticMaxTotalSeconds)
                {
                    reasons[profileName] = $"TỔNG >= {automaticMaxHours}H";
                    continue;
                }

                // Profile bù vừa lỗi phải có cooldown thật. Giữ FailedProfiles trong
                // toàn bộ cửa sổ cooldown để restart Manager cũng không làm mất hàng rào.
                if (failedProfiles.TryGetValue(profileName, out var failedEntry))
                {
                    var failedUntilUtc = failedEntry.FailedUtc + AutoReplacementFailedProfileCooldown;
                    if (refreshUtc < failedUntilUtc)
                    {
                        var remain = failedUntilUtc - refreshUtc;
                        reasons[profileName] =
                            $"COOLDOWN SAU LỖI · CÒN {Math.Max(1, (int)Math.Ceiling(remain.TotalMinutes))}P";
                        continue;
                    }

                    failedProfiles.Remove(profileName);
                }

                // Đồng thời kiểm tra cooldown runtime để chặn race giữa hai lượt refresh.
                if (IsReplacementProfileCoolingDown(profileName))
                {
                    reasons[profileName] = "COOLDOWN SAU LỖI";
                    continue;
                }

                if (retired)
                {
                    autoProfileResults.TryGetValue(account.Id, out var autoResult);
                    var autoProfileStatus = (autoResult ?? "").Trim();
                    var autoProfileFailed =
                        string.Equals(
                            autoProfileStatus,
                            "FAIL",
                            StringComparison.OrdinalIgnoreCase);

                    var identityDone =
                        identityResults.TryGetValue(account.Username, out var identityResult)
                        && string.Equals(
                            (identityResult ?? "").Trim(),
                            "DONE",
                            StringComparison.OrdinalIgnoreCase);

                    // Quy tắc reuse mới:
                    // - Tên/ảnh=DONE là điều kiện hoàn tất cần thiết.
                    // - +auto=DONE/PROCESSING/trống đều KHÔNG chặn reuse.
                    // - +auto=FAIL vẫn chặn để không tự lấy lại profile đã bị đánh dấu lỗi cứng.
                    if (!identityDone)
                    {
                        reasons[profileName] =
                            $"SOFT RETIRED · TÊN/ẢNH CHƯA DONE (+auto={(autoProfileStatus.Length == 0 ? "TRỐNG" : autoProfileStatus)})";
                        continue;
                    }

                    if (autoProfileFailed)
                    {
                        reasons[profileName] = "SOFT RETIRED · +auto=FAIL";
                        continue;
                    }

                    // Profile đã từng đóng vì lỗi không kết thúc vòng đời, Tên/ảnh đã DONE
                    // và không có note. Không yêu cầu +auto phải DONE nữa.
                    // PROCESSING hoặc trống vẫn được đưa lại kho dùng lại.
                    _autoReplacementRetiredProfiles.Remove(profileName);

                    var previousSource =
                        string.IsNullOrWhiteSpace(supplyState?.Source)
                            ? "legacy_retired"
                            : supplyState!.Source.Trim();

                    MarkProfileSupplyState(
                        profileName,
                        "used",
                        "auto_reuse_soft_retired_recovered:" + previousSource);

                    var recoveredState = GetProfileSupplyState(profileName);
                    if (recoveredState is not null
                        && (recoveredState.State ?? "").Trim().Equals(
                            "retired",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Ghi state thất bại: giữ hàng rào, không cho candidate lọt qua
                        // pre-open guard bằng một trạng thái chỉ hồi sinh trong RAM.
                        _autoReplacementRetiredProfiles.Add(profileName);
                        reasons[profileName] = "SOFT RETIRED · CHƯA LƯU ĐƯỢC TRẠNG THÁI KHÔI PHỤC";
                        continue;
                    }

                    reasons[profileName] = "ĐỦ ĐIỀU KIỆN · KHÔI PHỤC SOFT RETIRED";
                    _log.Info(
                        $"[REUSE_QUEUE_SOFT_RETIRED_RECOVERED] profile={profileName} account={account.Username} oldSource={previousSource} total={TimeSpan.FromSeconds(totalSeconds):c}");
                }
                else
                {
                    reasons[profileName] = "ĐỦ ĐIỀU KIỆN";
                }

                eligible.Add(
                    new ReusableProfileQueueEntry
                    {
                        ProfileName = profileName,
                        AccountId = account.Id,
                        Username = account.Username,
                        TotalRunSeconds = totalSeconds,
                        IsManual = false,
                        AddedUtc =
                            oldEntry?.AddedUtc
                            ?? DateTime.UtcNow,
                        LastCheckedUtc = DateTime.UtcNow
                    });
            }

            // Tài khoản có AssignedProfile nhưng profile không còn trong catalog cũng cần
            // hiện lý do rõ ràng trên Kho tài khoản, thay vì để ô trống khó đoán.
            var catalogProfileNames =
                new HashSet<string>(
                    catalog.Profiles
                        .Select(x => (x.Name ?? "").Trim())
                        .Where(x => x.Length > 0),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var profileName in accountsByProfile.Keys)
            {
                if (!catalogProfileNames.Contains(profileName)
                    && !reasons.ContainsKey(profileName))
                {
                    reasons[profileName] = "KHÔNG TÌM THẤY PROFILE";
                }
            }

            eligible =
                OrderReusableProfileEntries(eligible);

            var updated =
                new ReusableProfileQueueDocument
                {
                    Version = 3,
                    Pending = eligible,
                    ExcludedProfiles =
                        excludedProfiles
                            .OrderBy(
                                x => x,
                                NaturalProfileNameOrder)
                            .ToList(),
                    FailedProfiles = failedProfiles
                };

            lock (_reusableProfileQueueLock)
            {
                _reusableProfileQueueCache =
                    CloneReusableProfileQueueDocument(
                        updated);

                _reusableProfileQueueLoaded = true;
                _reusableProfileReasonCache =
                    new Dictionary<string, string>(
                        reasons,
                        StringComparer.OrdinalIgnoreCase);
                SaveReusableProfileQueueUnsafe(updated);
            }

            _log.Info(
                $"[REUSE_QUEUE_REFRESH] source={source} eligible={eligible.Count} manual={eligible.Count(x => x.IsManual)} auto={eligible.Count(x => !x.IsManual)} busy={busyProfiles.Count} maxAutoTotal={automaticMaxHours}h");

            // Nếu một suất bù đang chờ timer TẠO MỚI mà queue dùng lại vừa có
            // nguồn, đánh thức suất đó ngay. Timer phút không được giữ PRF đã tồn tại.
            if (eligible.Count > 0)
                WakeAutoReplacementForReusableSupply("reuse_refresh:" + source);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REUSE_QUEUE_REFRESH_WARN] source={source} error={ex.Message}");

            // Với nút Quét chờ, đẩy lỗi ra UI để không báo "Đã quét xong" giả.
            if (source.Equals(
                    "account_pool_manual",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }
        finally
        {
            _reusableProfileRefreshGate.Release();
        }
    }

    static bool IsHardReusableProfileRetired(
        ProfileSupplyStateEntry? supplyState)
    {
        if (supplyState is null
            || !(supplyState.State ?? "").Trim().Equals(
                "retired",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = (supplyState.Source ?? "").Trim();
        if (source.Length == 0)
        {
            // State legacy không có lý do được xem là soft, nhưng chỉ được hồi sinh
            // nếu Excel vẫn DONE/DONE + note trống + profile không bận + chưa hết giờ.
            return false;
        }

        if (source.StartsWith(
                "login_banned:",
                StringComparison.OrdinalIgnoreCase)
            || source.Contains(
                "login_ban",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (source.StartsWith(
                "auto_close:",
                StringComparison.OrdinalIgnoreCase))
        {
            var reason = source["auto_close:".Length..].Trim();

            if (reason.Equals(
                    "BAN",
                    StringComparison.OrdinalIgnoreCase)
                || reason.StartsWith(
                    "BAN_",
                    StringComparison.OrdinalIgnoreCase)
                || reason.StartsWith(
                    "TIME_",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    Dictionary<string, ReusableProfileQueueView>
        GetReusableProfileQueueSnapshot()
    {
        lock (_reusableProfileQueueLock)
        {
            var document =
                EnsureReusableProfileQueueLoadedUnsafe();

            var result =
                new Dictionary<string, ReusableProfileQueueView>(
                    StringComparer.OrdinalIgnoreCase);

            var ordered =
                OrderReusableProfileEntries(
                    document.Pending);

            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];

                if (string.IsNullOrWhiteSpace(entry.ProfileName))
                    continue;

                result[entry.ProfileName] =
                    new ReusableProfileQueueView(
                        i + 1,
                        entry.ProfileName,
                        entry.Username,
                        TimeSpan.FromSeconds(
                            Math.Max(
                                0,
                                entry.TotalRunSeconds)),
                        entry.IsManual);
            }

            return result;
        }
    }

    Dictionary<string, string> GetReusableProfileQueueReasonSnapshot()
    {
        lock (_reusableProfileQueueLock)
        {
            return new Dictionary<string, string>(
                _reusableProfileReasonCache,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    int GetReusableProfileQueueCount()
    {
        lock (_reusableProfileQueueLock)
        {
            return EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Count;
        }
    }

    static List<ReusableProfileQueueEntry>
        OrderReusableProfileEntries(
            IEnumerable<ReusableProfileQueueEntry> source)
    {
        var items = source.ToList();

        var manual =
            items
                .Where(x => x.IsManual)
                .OrderBy(x => x.AddedUtc)
                .ThenBy(
                    x => x.ProfileName,
                    NaturalProfileNameOrder);

        var automatic =
            items
                .Where(x => !x.IsManual && !x.NameSyncPending)
                .OrderByDescending(x => x.TotalRunSeconds)
                .ThenBy(
                    x => x.ProfileName,
                    NaturalProfileNameOrder);

        var nameSyncPending =
            items
                .Where(x => !x.IsManual && x.NameSyncPending)
                .OrderBy(x => x.AddedUtc)
                .ThenBy(
                    x => x.ProfileName,
                    NaturalProfileNameOrder);

        return manual
            .Concat(automatic)
            .Concat(nameSyncPending)
            .ToList();
    }

    bool TryAddReusableProfileManual(
        string accountId,
        string username,
        string profileName,
        string note,
        out string message)
    {
        accountId = (accountId ?? "").Trim();
        username = (username ?? "").Trim();
        profileName = (profileName ?? "").Trim();
        note = (note ?? "").Trim();

        if (profileName.Length == 0)
        {
            message = "Tài khoản chưa được gán Profile.";
            return false;
        }

        if (note.Equals(
                "ban",
                StringComparison.OrdinalIgnoreCase))
        {
            message =
                $"Profile {profileName} có Ghi chú = ban nên không được đưa vào Chờ dùng lại.";
            return false;
        }

        var catalog = _profileService.Load();

        var profile =
            catalog.Profiles.FirstOrDefault(x =>
                x.Name.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            message =
                $"Không tìm thấy Profile {profileName} trong danh sách profile hiện tại.";
            return false;
        }

        if (!IsReusableProfileActuallyCreated(profile))
        {
            message =
                $"Profile {profileName} chưa được Chrome khởi tạo thật (chưa có Local State).";
            return false;
        }

        if (IsReusableProfileBusy(profileName))
        {
            message =
                $"Profile {profileName} đang mở/chạy. Hãy đóng profile trước khi thêm vào Chờ dùng lại.";
            return false;
        }

        // Thêm THỦ CÔNG = người dùng chủ động xác nhận muốn dùng lại.
        // Vì vậy retired không chặn thao tác này. Ta gỡ retired trong RAM và
        // chuyển supply-state sang used để lần quét kế tiếp không làm rớt entry thủ công.
        var retiredInMemory =
            _autoReplacementRetiredProfiles.Remove(profileName);

        var retiredPersisted = false;

        lock (_profileSupplyStateLock)
        {
            var supply =
                LoadProfileSupplyStateDocumentUnsafe();

            retiredPersisted =
                supply.Profiles.TryGetValue(
                    profileName,
                    out var state)
                && (state.State ?? "").Trim().Equals(
                    "retired",
                    StringComparison.OrdinalIgnoreCase);
        }

        if (retiredInMemory || retiredPersisted)
        {
            MarkProfileSupplyState(
                profileName,
                "used",
                "manual_reuse_override_retired");

            _log.Info(
                $"[REUSE_QUEUE_MANUAL_OVERRIDE_RETIRED] profile={profileName} inMemory={retiredInMemory} persisted={retiredPersisted}");
        }

        var totalSeconds =
            ReadReusableProfileTotalSeconds(profile);

        lock (_reusableProfileQueueLock)
        {
            var document =
                CloneReusableProfileQueueDocument(
                    EnsureReusableProfileQueueLoadedUnsafe());

            var existing =
                document.Pending.FirstOrDefault(x =>
                    x.ProfileName.Equals(
                        profileName,
                        StringComparison.OrdinalIgnoreCase));

            document.Pending.RemoveAll(x =>
                x.ProfileName.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

            document.ExcludedProfiles.RemoveAll(x =>
                x.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

            document.FailedProfiles.Remove(profileName);

            document.Pending.Add(
                new ReusableProfileQueueEntry
                {
                    ProfileName = profileName,
                    AccountId = accountId,
                    Username = username,
                    TotalRunSeconds = totalSeconds,
                    IsManual = true,
                    AddedUtc =
                        existing?.IsManual == true
                            ? existing.AddedUtc
                            : DateTime.UtcNow,
                    LastCheckedUtc = DateTime.UtcNow
                });

            document.Pending =
                OrderReusableProfileEntries(
                    document.Pending);

            document.Version = 3;

            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info(
            $"[REUSE_QUEUE_MANUAL_ADD] profile={profileName} account={username} total={TimeSpan.FromSeconds(totalSeconds):c}");

        // PRF thủ công đã được user xác nhận sẵn sàng: nếu Tự bù đang chờ tạo mới,
        // kéo request về xử lý ngay thay vì bắt user chờ hết 2/3/5 phút.
        WakeAutoReplacementForReusableSupply("manual_reuse_add:" + profileName);

        message =
            $"Đã thêm Profile {profileName} ({username}) vào Chờ dùng lại thủ công.";
        return true;
    }

    bool TryRemoveReusableProfileManual(
        string profileName,
        out string message)
    {
        profileName = (profileName ?? "").Trim();

        if (profileName.Length == 0)
        {
            message = "Tài khoản chưa được gán Profile.";
            return false;
        }

        lock (_reusableProfileQueueLock)
        {
            var document =
                CloneReusableProfileQueueDocument(
                    EnsureReusableProfileQueueLoadedUnsafe());

            var removed =
                document.Pending.RemoveAll(x =>
                    x.ProfileName.Equals(
                        profileName,
                        StringComparison.OrdinalIgnoreCase));

            if (removed <= 0)
            {
                message =
                    $"Profile {profileName} hiện không nằm trong Chờ dùng lại.";
                return false;
            }

            if (!document.ExcludedProfiles.Any(x =>
                    x.Equals(
                        profileName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                document.ExcludedProfiles.Add(profileName);
            }

            document.Version = 3;

            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info(
            $"[REUSE_QUEUE_MANUAL_REMOVE] profile={profileName}");

        message =
            $"Đã bỏ Profile {profileName} khỏi Chờ dùng lại. Quét tự động sẽ không tự thêm lại profile này.";
        return true;
    }

    bool IsReusableProfileBusy(
        string profileName)
    {
        profileName = (profileName ?? "").Trim();

        if (profileName.Length == 0)
            return false;

        if (!_contexts.TryGetValue(profileName, out var ctx))
            return false;

        if (ctx.Opening)
            return true;

        var state = GetEffectiveRuntimeState(ctx);
        if (state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering)
            return true;

        // STOPPED là profile có thể dùng lại, kể cả Worker/tab vẫn còn tồn tại.
        // OpenProfileAsync/EnsureWorkerAsync có thể tái sử dụng Worker sống sẵn.
        if (state == RuntimeStateStopped)
            return false;

        // UNKNOWN + còn Worker/tab: chưa đủ bằng chứng để mở chồng lên. Lượt bù sẽ
        // chủ động RefreshStatus một lần và, nếu vẫn UNKNOWN, giữ suất để retry.
        return HasReusableProfilePhysicalRuntime(ctx);
    }

    bool IsReusableProfileTransientBusy(string profileName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0
            || !_contexts.TryGetValue(profileName, out var ctx))
        {
            return false;
        }

        if (ctx.Opening)
            return true;

        var state = GetEffectiveRuntimeState(ctx);
        if (state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering
            || state == RuntimeStateStopped)
        {
            return false;
        }

        return HasReusableProfilePhysicalRuntime(ctx);
    }

    bool HasReusableProfilePhysicalRuntime(ProfileContext ctx)
    {
        var workerAlive = false;
        try
        {
            workerAlive = ctx.Worker is not null && !ctx.Worker.HasExited;
        }
        catch { }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        return workerAlive || tabOpen;
    }

    sealed record ReusableProfileDrainSummary(
        int Checked,
        int Ready,
        int StillWaiting,
        int Skipped,
        int CleanupFailed);

    void PromoteNameSyncPendingProfileToReady(string profileName, string currentName)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0) return;

        lock (_reusableProfileQueueLock)
        {
            var document = CloneReusableProfileQueueDocument(
                EnsureReusableProfileQueueLoadedUnsafe());

            var entry = document.Pending.FirstOrDefault(x =>
                x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            entry.NameSyncPending = false;
            entry.NameSyncQueuedUtc = null;
            entry.LastCheckedUtc = DateTime.UtcNow;
            // Giữ AddedUtc cũ để profile tồn lâu được ưu tiên dùng trước.

            document.Pending = OrderReusableProfileEntries(document.Pending);
            document.Version = 3;
            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            _reusableProfileReasonCache[profileName] =
                string.IsNullOrWhiteSpace(currentName)
                    ? "SẴN SÀNG DÙNG LẠI"
                    : $"SẴN SÀNG · TÊN: {currentName}";
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info($"[REUSE_QUEUE_DRAIN_READY] profile={profileName} currentName={currentName}");
    }

    async Task<ReusableProfileDrainSummary> DrainReusableProfileNameSyncQueueAsync(
        Action<string>? progress,
        CancellationToken ct = default)
    {
        await RefreshReusableProfileQueueAsync("manual_drain_name_sync", ct);

        List<ReusableProfileQueueEntry> candidates;
        lock (_reusableProfileQueueLock)
        {
            candidates = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x => x.NameSyncPending)
                .OrderBy(x => x.AddedUtc)
                .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        var checkedCount = 0;
        var readyCount = 0;
        var waitingCount = 0;
        var skippedCount = 0;
        var cleanupFailed = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var profileName = (candidate.ProfileName ?? "").Trim();
            if (profileName.Length == 0) { skippedCount++; continue; }

            // Không mở lại profile vừa mới kiểm tra: cho TikTok ít nhất 60 giây đồng bộ.
            if ((DateTime.UtcNow - candidate.LastCheckedUtc) < AutoReplacementNameSyncMinRetryAge)
            {
                skippedCount++;
                continue;
            }

            if (IsReusableProfileBusy(profileName)
                || _autoReplacementClaimedProfiles.Contains(profileName))
            {
                skippedCount++;
                continue;
            }

            TikTokAccountPoolItem? account = null;
            try
            {
                account = await RunAccountPoolIoAsync(
                    () =>
                    {
                        if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                            _accountPoolService.ReloadCurrentExcel();
                        return _accountPoolService.Load().FirstOrDefault(x =>
                            x.Id.Equals(candidate.AccountId, StringComparison.OrdinalIgnoreCase)
                            || (x.Username.Equals(candidate.Username, StringComparison.OrdinalIgnoreCase)
                                && (x.AssignedProfile ?? "").Equals(profileName, StringComparison.OrdinalIgnoreCase)));
                    }, ct);
            }
            catch { }

            if (account is null
                || (account.Note ?? "").Trim().Equals("ban", StringComparison.OrdinalIgnoreCase)
                || !string.Equals((account.AssignedProfile ?? "").Trim(), profileName, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            try
            {
                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);
            }
            catch { }

            if (!_contexts.TryGetValue(profileName, out var ctx))
            {
                skippedCount++;
                continue;
            }

            checkedCount++;
            progress?.Invoke($"Đang kiểm tra PRF {profileName} ({checkedCount}/{candidates.Count})...");
            _autoReplacementClaimedProfiles.Add(profileName);

            try
            {
                _ = await OpenProfileAsync(ctx, $"Dọn kho: kiểm tra tên PRF {profileName}...");

                // Dọn kho chỉ cần đủ điều kiện đọc tên, không Start automation. Cho VM chậm
                // tối đa 90 giây để Worker/Chrome/CDP sẵn sàng trước khi kết luận CHỜ tiếp.
                var readyDeadline = DateTime.UtcNow.AddSeconds(90);
                var probeReady = false;
                while (DateTime.UtcNow < readyDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await RefreshStatusAsync(ctx); } catch { }

                    var workerAlive = IsNameGuardWorkerAlive(ctx);
                    var chromeConnected = string.Equals(
                        ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase);

                    if (workerAlive && chromeConnected)
                    {
                        probeReady = true;
                        break;
                    }

                    if (!workerAlive)
                    {
                        try { _ = await OpenProfileAsync(ctx, $"Dọn kho: chờ Worker PRF {profileName}..."); }
                        catch { }
                    }
                    else if (!chromeConnected)
                    {
                        try { await OpenChromeForProfileAsync(ctx); }
                        catch { }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }

                if (!probeReady)
                {
                    waitingCount++;
                    TouchReusableProfileNameSyncPending(profileName, "manual_drain_probe_not_ready");
                    continue;
                }

                var names = SplitIdentityNames(LoadIdentityToolState().NamesText);
                if (names.Count == 0)
                {
                    waitingCount++;
                    TouchReusableProfileNameSyncPending(profileName, "manual_drain_names_empty");
                    continue;
                }

                var probe = await ProbeNameGuardFastAsync(ctx, account.Username, names);
                if (!probe.Ok || !probe.Matched)
                {
                    waitingCount++;
                    TouchReusableProfileNameSyncPending(
                        profileName,
                        probe.Ok ? "manual_drain_name_not_updated" : "manual_drain_probe_transient");
                    continue;
                }

                var identityDone = await MarkIdentityDoneVerifiedAsync(account.Username, profileName, ct);
                if (!identityDone.Ok)
                {
                    waitingCount++;
                    TouchReusableProfileNameSyncPending(profileName, "manual_drain_identity_done_write_failed");
                    continue;
                }

                PromoteNameSyncPendingProfileToReady(profileName, probe.CurrentName);
                readyCount++;
            }
            finally
            {
                try
                {
                    await CleanupCreatedReplacementAttemptAsync(profileName, "manual_reuse_queue_drain");
                }
                catch (Exception ex)
                {
                    cleanupFailed++;
                    _log.Warn($"[REUSE_QUEUE_DRAIN_CLEANUP_WARN] profile={profileName} error={ex.Message}");
                }

                _autoReplacementClaimedProfiles.Remove(profileName);
            }

            // Không mở/đóng liên tục quá nhanh trên VM.
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        await RefreshReusableProfileQueueAsync("manual_drain_name_sync_done", ct);
        return new ReusableProfileDrainSummary(
            checkedCount, readyCount, waitingCount, skippedCount, cleanupFailed);
    }

    int GetNameSyncPendingReusableProfileCount()
    {
        lock (_reusableProfileQueueLock)
        {
            return EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Count(x => x.NameSyncPending);
        }
    }

    void QueueReusableProfileNameSyncPending(
        AutoProfileQueueItem item,
        string detail)
    {
        var profileName = (item.ProfileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        double totalSeconds = 0;
        try
        {
            var profile = _profileService.Load().Profiles.FirstOrDefault(x =>
                x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
                totalSeconds = ReadReusableProfileTotalSeconds(profile);
        }
        catch { }

        var nowUtc = DateTime.UtcNow;

        lock (_reusableProfileQueueLock)
        {
            var document = CloneReusableProfileQueueDocument(
                EnsureReusableProfileQueueLoadedUnsafe());

            var existing = document.Pending.FirstOrDefault(x =>
                x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            document.Pending.RemoveAll(x =>
                x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            // Đây là lỗi đồng bộ tên tạm thời, không phải lỗi profile cứng.
            document.FailedProfiles.Remove(profileName);
            document.ExcludedProfiles.RemoveAll(x =>
                x.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            document.Pending.Add(
                new ReusableProfileQueueEntry
                {
                    ProfileName = profileName,
                    AccountId = item.Account.Id,
                    Username = item.Account.Username,
                    TotalRunSeconds = Math.Max(0, totalSeconds),
                    IsManual = false,
                    NameSyncPending = true,
                    NameSyncQueuedUtc = existing?.NameSyncQueuedUtc ?? nowUtc,
                    NameSyncCheckCount = existing?.NameSyncCheckCount ?? 0,
                    AddedUtc = existing?.AddedUtc ?? nowUtc,
                    LastCheckedUtc = existing?.LastCheckedUtc ?? nowUtc
                });

            document.Pending = OrderReusableProfileEntries(document.Pending);
            document.Version = 3;
            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            _reusableProfileReasonCache[profileName] = "CHỜ ĐỒNG BỘ TÊN";
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Warn(
            $"[REUSE_QUEUE_NAME_SYNC_ADD] profile={profileName} account={item.Account.Username} detail={detail}");

        WriteAutoActivityLog(
            action: "CHỜ DÙNG LẠI",
            profile: profileName,
            account: item.Account.Username,
            result: "CHỜ ĐỒNG BỘ TÊN",
            detail: "TikTok chưa phản ánh tên mới. Đã đóng runtime và giữ profile trong Chờ dùng lại để recovery sweep kiểm tra sau.");
    }

    void TouchReusableProfileNameSyncPending(
        string profileName,
        string reason)
    {
        profileName = (profileName ?? "").Trim();
        if (profileName.Length == 0)
            return;

        lock (_reusableProfileQueueLock)
        {
            var document = CloneReusableProfileQueueDocument(
                EnsureReusableProfileQueueLoadedUnsafe());

            var entry = document.Pending.FirstOrDefault(x =>
                x.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase)
                && x.NameSyncPending);

            if (entry is null)
                return;

            entry.NameSyncCheckCount = Math.Max(0, entry.NameSyncCheckCount) + 1;
            entry.LastCheckedUtc = DateTime.UtcNow;

            // Đưa xuống cuối lane NAME_SYNC_PENDING cho đúng nghĩa thử một lượt.
            entry.AddedUtc = DateTime.UtcNow;

            document.Pending = OrderReusableProfileEntries(document.Pending);
            document.Version = 3;
            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            _reusableProfileReasonCache[profileName] =
                $"CHỜ ĐỒNG BỘ TÊN · ĐÃ KIỂM TRA {entry.NameSyncCheckCount} LẦN";
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info(
            $"[REUSE_QUEUE_NAME_SYNC_ROTATE] profile={profileName} reason={reason}");
    }

    async Task<bool> TryRecoverNameSyncPendingReusableProfilesOnceAsync(
        AutoReplacementRequest request,
        int executionGeneration,
        CancellationToken executionToken)
    {
        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();

        await RefreshReusableProfileQueueAsync("name_sync_recovery_sweep");

        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();

        List<ReusableProfileQueueEntry> candidates;
        lock (_reusableProfileQueueLock)
        {
            candidates = EnsureReusableProfileQueueLoadedUnsafe()
                .Pending
                .Where(x => x.NameSyncPending)
                .OrderBy(x => x.AddedUtc)
                .ThenBy(x => x.ProfileName, NaturalProfileNameOrder)
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        if (candidates.Count == 0)
            return false;

        _log.Info(
            $"[NAME_SYNC_RECOVERY_SWEEP_BEGIN] closed={request.ClosedProfileName} pending={candidates.Count}");

        foreach (var candidate in candidates)
        {
            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return false;

            executionToken.ThrowIfCancellationRequested();

            var profileName = (candidate.ProfileName ?? "").Trim();
            if (profileName.Length == 0
                || profileName.Equals(request.ClosedProfileName, StringComparison.OrdinalIgnoreCase)
                || _autoReplacementClaimedProfiles.Contains(profileName))
            {
                continue;
            }

            if (IsManualCloseSuppressed(profileName))
            {
                _log.Info(
                    $"[NAME_SYNC_RECOVERY_SKIP_MANUAL_CLOSE] id={request.Id} profile={profileName}");
                continue;
            }

            // 1 profile chỉ được thử 1 lần trong CÙNG một suất bù, kể cả request
            // đã RETRY sau 2/3/5 phút. Không mở đi mở lại khi tên TikTok chưa kịp sync.
            if (HasAutoReplacementProfileBeenAttempted(request, profileName))
            {
                _log.Info(
                    $"[NAME_SYNC_RECOVERY_SKIP_ALREADY_ATTEMPTED] id={request.Id} profile={profileName}");
                continue;
            }

            var sinceLastCheck = DateTime.UtcNow - candidate.LastCheckedUtc;
            if (sinceLastCheck < AutoReplacementNameSyncMinRetryAge)
            {
                _log.Info(
                    $"[NAME_SYNC_RECOVERY_SKIP_TOO_FRESH] id={request.Id} profile={profileName} age={sinceLastCheck:c} min={AutoReplacementNameSyncMinRetryAge:c}");
                continue;
            }

            TikTokAccountPoolItem? account = null;
            Exception? accountReadError = null;
            try
            {
                account = await RunAccountPoolIoAsync(
                    () =>
                    {
                        if (!string.IsNullOrWhiteSpace(_accountPoolService.CurrentSourcePath))
                            _accountPoolService.ReloadCurrentExcel();

                        var accounts = _accountPoolService.Load();
                        return accounts.FirstOrDefault(x =>
                            x.Id.Equals(candidate.AccountId, StringComparison.OrdinalIgnoreCase)
                            || (x.Username.Equals(candidate.Username, StringComparison.OrdinalIgnoreCase)
                                && (x.AssignedProfile ?? "").Equals(profileName, StringComparison.OrdinalIgnoreCase)));
                    },
                    executionToken);
            }
            catch (OperationCanceledException)
                when (executionToken.IsCancellationRequested
                      || !IsAutoReplacementExecutionAllowed(executionGeneration))
            {
                _log.Warn(
                    $"[NAME_SYNC_RECOVERY_HARD_STOP_ACCOUNT_READ] profile={profileName} generation={executionGeneration}");
                throw;
            }
            catch (Exception ex)
            {
                accountReadError = ex;
                _log.Warn(
                    $"[NAME_SYNC_RECOVERY_ACCOUNT_READ_WARN] profile={profileName} error={ex.Message}");
            }

            if (accountReadError is not null)
            {
                // Đây là lỗi I/O/Excel tạm thời, KHÔNG phải bằng chứng account đã mất.
                // Không xóa PRF chờ và không cho flow rơi xuống tạo PRF mới.
                throw new InvalidOperationException(
                    $"Chưa xác minh được account của PRF chờ {profileName}; giữ queue và retry, không tạo PRF mới.",
                    accountReadError);
            }

            if (account is null)
            {
                RemoveReusableProfileQueueEntry(profileName, "name_sync_account_missing_verified");
                continue;
            }

            if ((account.Note ?? "").Trim().Equals("ban", StringComparison.OrdinalIgnoreCase)
                || !string.Equals((account.AssignedProfile ?? "").Trim(), profileName, StringComparison.OrdinalIgnoreCase))
            {
                RemoveReusableProfileQueueEntry(profileName, "name_sync_account_invalid_or_remapped");
                continue;
            }

            Exception? catalogRefreshError = null;
            try
            {
                var catalog = _profileService.Load();
                RefreshContextsFromCatalog(catalog);
            }
            catch (Exception ex)
            {
                catalogRefreshError = ex;
                _log.Warn(
                    $"[NAME_SYNC_RECOVERY_CATALOG_REFRESH_WARN] profile={profileName} error={ex.Message}");
            }

            if (!_contexts.TryGetValue(profileName, out var ctx))
            {
                if (catalogRefreshError is not null)
                {
                    // Không thể kết luận PRF đã mất nếu chính lượt load catalog vừa lỗi.
                    // Giữ queue và chặn fallback tạo mới cho đến lượt retry sau.
                    throw new InvalidOperationException(
                        $"Chưa xác minh được catalog/context của PRF chờ {profileName}; giữ queue và retry, không tạo PRF mới.",
                        catalogRefreshError);
                }

                RemoveReusableProfileQueueEntry(profileName, "name_sync_context_missing_verified");
                continue;
            }

            if (IsReusableProfileBusy(profileName))
            {
                TouchReusableProfileNameSyncPending(profileName, "profile_busy");
                continue;
            }

            // Đánh dấu trước khi mở Chrome. Dù probe fail/cleanup/retry request,
            // profile này không được mở lần 2 trong cùng suất bù.
            MarkAutoReplacementProfileAttempted(
                request,
                profileName,
                "name_sync_pending");

            _autoReplacementClaimedProfiles.Add(profileName);

            try
            {
                _log.Info(
                    $"[NAME_SYNC_RECOVERY_OPEN] closed={request.ClosedProfileName} profile={profileName} account={account.Username}");

                WriteAutoActivityLog(
                    action: "KIỂM TRA TÊN CHỜ",
                    profile: request.ClosedProfileName,
                    account: account.Username,
                    reason: request.Reason,
                    replacementProfile: profileName,
                    result: "BẮT ĐẦU",
                    detail: "Mở lại profile trong Chờ dùng lại để chỉ kiểm tra tên đã đồng bộ hay chưa.");

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_HARD_STOP_BEFORE_OPEN] profile={profileName} generation={executionGeneration}");
                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                SetAutoReplacementUiPhase(
                    "MỞ CHỜ TÊN",
                    profileName,
                    request.Id);

                _ = await OpenProfileAsync(
                    ctx,
                    $"Kiểm tra lại tên profile chờ dùng lại {profileName}...");

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            profileName,
                            "name_sync_recovery_hard_stop_after_open");
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Warn(
                            $"[NAME_SYNC_RECOVERY_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                // Worker/Chrome trên VM có thể cần nhiều thời gian để ổn định. Không kết
                // luận lỗi ngay sau pipe timeout 10s; giữ đúng slot này tối đa 10 phút và
                // chỉ recovery CHÍNH profile đang thử.
                SetAutoReplacementUiPhase(
                    "CHỜ TÊN ỔN ĐỊNH",
                    profileName,
                    request.Id);

                var probeReady = await WaitForReplacementProbeReadyAsync(
                    ctx,
                    request,
                    "name_sync_recovery",
                    executionGeneration,
                    executionToken);

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            profileName,
                            "name_sync_recovery_hard_stop_after_probe_grace");
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Warn(
                            $"[NAME_SYNC_RECOVERY_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                if (!probeReady)
                {
                    if (IsManualCloseSuppressed(profileName))
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            profileName,
                            "name_sync_recovery_manual_close_during_probe_grace");
                        _log.Warn(
                            $"[NAME_SYNC_RECOVERY_MANUAL_ABORT] profile={profileName} stage=probe_grace");
                        return false;
                    }

                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_probe_ready_10m_timeout");
                    TouchReusableProfileNameSyncPending(profileName, "probe_ready_10m_timeout");
                    continue;
                }

                var identityState = LoadIdentityToolState();
                var names = SplitIdentityNames(identityState.NamesText);
                if (names.Count == 0)
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_names_empty");
                    TouchReusableProfileNameSyncPending(profileName, "identity_names_empty");
                    continue;
                }

                // Chỉ PROBE tên. Không gọi ProcessNameGuardOnceAsync vì hàm đó sẽ
                // đổi tên lại khi chưa match; recovery sweep theo yêu cầu chỉ kiểm tra.
                var probe = await ProbeNameGuardFastAsync(
                    ctx,
                    account.Username,
                    names);

                if (!probe.Ok)
                {
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_PROBE_WAIT] profile={profileName} source={probe.Source} message={probe.Message}");

                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_probe_transient");
                    TouchReusableProfileNameSyncPending(profileName, "probe_transient");
                    continue;
                }

                if (!probe.Matched)
                {
                    _log.Info(
                        $"[NAME_SYNC_RECOVERY_NOT_YET] profile={profileName} currentName={probe.CurrentName}");

                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_not_updated_yet");
                    TouchReusableProfileNameSyncPending(profileName, "name_not_updated_yet");
                    continue;
                }

                var identityDone = await MarkIdentityDoneVerifiedAsync(
                    account.Username,
                    profileName,
                    executionToken);

                if (!identityDone.Ok)
                {
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_EXCEL_WAIT] profile={profileName} error={identityDone.Error}");

                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_identity_done_write_failed");
                    TouchReusableProfileNameSyncPending(profileName, "identity_done_write_failed");
                    continue;
                }

                MarkNameGuardVerifiedForCurrentChromeSession(ctx, account.Username);

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            profileName,
                            "name_sync_recovery_hard_stop_before_start");
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Warn(
                            $"[NAME_SYNC_RECOVERY_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                var stabilization = await StabilizeReplacementRuntimeAsync(
                    ctx,
                    request,
                    "name_sync_recovery",
                    executionGeneration,
                    executionToken);

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    try
                    {
                        await CleanupCreatedReplacementAttemptAsync(
                            profileName,
                            "name_sync_recovery_hard_stop_after_stabilize");
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Warn(
                            $"[NAME_SYNC_RECOVERY_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                if (IsManualCloseSuppressed(profileName))
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_manual_close_during_stabilize");
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_MANUAL_ABORT] profile={profileName} stage=stabilize");
                    return false;
                }

                // BAN/TIME có thể xuất hiện SAU khi candidate đã được claim/mở.
                // Stabilizer sẽ thoát ngay; không đánh FAIL/cooldown và không cố mở lại.
                // finally bên dưới nhả claim để job Tự xóa được tiếp tục an toàn.
                if (IsProfileRetireDeleteBlockedForOpen(profileName)
                    || (!stabilization.Healthy
                        && (stabilization.Detail ?? "").StartsWith(
                            "RETIRE_DELETE_BLOCKED:",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    ClearAutoCloseExpectedRunning(
                        profileName,
                        "reuse_retire_delete_abort");

                    RemoveReusableProfileQueueEntry(
                        profileName,
                        "retire_delete_during_stabilize");

                    _log.Warn(
                        $"[REUSE_QUEUE_ABORT_RETIRE_DELETE] id={request.Id} profile={profileName} detail={stabilization.Detail}");

                    continue;
                }

                if (stabilization.NameSyncPending)
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_deferred_again");
                    TouchReusableProfileNameSyncPending(profileName, "deferred_again");
                    continue;
                }

                if (!stabilization.Healthy)
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        stabilization.HardFailed
                            ? "name_sync_recovery_hard_failed"
                            : "name_sync_recovery_10m_timeout");
                    TouchReusableProfileNameSyncPending(
                        profileName,
                        stabilization.HardFailed ? "hard_failed" : "stabilize_10m_timeout");
                    continue;
                }

                try
                {
                    await RunAccountPoolIoAsync(
                        () => _accountPoolService.SetAutoProfileResult(account.Id, "DONE"),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_DONE_WRITE_WARN] profile={profileName} account={account.Username} error={ex.Message}");
                }

                RemoveReusableProfileQueueEntry(profileName, "name_sync_recovered_success");
                MarkProfileSupplyState(
                    profileName,
                    "used",
                    "name_sync_recovered_running_confirmed");

                _log.Info(
                    $"[NAME_SYNC_RECOVERY_OK] closed={request.ClosedProfileName} replacement={profileName} account={account.Username} currentName={probe.CurrentName}");

                WriteAutoActivityLog(
                    action: "KIỂM TRA TÊN CHỜ",
                    profile: request.ClosedProfileName,
                    account: account.Username,
                    reason: request.Reason,
                    replacementProfile: profileName,
                    result: "THÀNH CÔNG",
                    detail: $"Tên đã cập nhật thành '{probe.CurrentName}'. Đã xác minh DONE và profile RUNNING khỏe.");

                return true;
            }
            catch (OperationCanceledException)
                when (executionToken.IsCancellationRequested
                      || !IsAutoReplacementExecutionAllowed(executionGeneration))
            {
                _log.Warn(
                    $"[NAME_SYNC_RECOVERY_HARD_STOP] profile={profileName} generation={executionGeneration}");

                try
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_hard_stop");
                }
                catch (Exception cleanupEx)
                {
                    _log.Warn(
                        $"[NAME_SYNC_RECOVERY_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                }

                return false;
            }
            catch (AutoReplacementCleanupBarrierException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[NAME_SYNC_RECOVERY_ERROR] profile={profileName} error={ex.Message}");

                try
                {
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "name_sync_recovery_exception:" + ex.GetType().Name);
                    TouchReusableProfileNameSyncPending(profileName, "exception:" + ex.GetType().Name);
                }
                catch (AutoReplacementCleanupBarrierException)
                {
                    throw;
                }
            }
            finally
            {
                _autoReplacementClaimedProfiles.Remove(profileName);
            }
        }

        _log.Info(
            $"[NAME_SYNC_RECOVERY_SWEEP_END] closed={request.ClosedProfileName} result=NO_MATCH pending={GetNameSyncPendingReusableProfileCount()}");

        return false;
    }

    bool TryFindUntestedEligibleReusableProfile(
        AutoReplacementRequest request,
        out string profileName,
        out string lane,
        out string detail)
    {
        profileName = "";
        lane = "";
        detail = "";

        List<ReusableProfileQueueEntry> snapshot;
        lock (_reusableProfileQueueLock)
        {
            snapshot = OrderReusableProfileEntries(
                    EnsureReusableProfileQueueLoadedUnsafe().Pending)
                .Select(CloneReusableProfileQueueEntry)
                .ToList();
        }

        foreach (var candidate in snapshot)
        {
            var name = (candidate.ProfileName ?? "").Trim();
            if (name.Length == 0
                || name.Equals(request.ClosedProfileName, StringComparison.OrdinalIgnoreCase)
                || _autoReplacementClaimedProfiles.Contains(name)
                || IsProfileRetireDeleteBlockedForOpen(name)
                || IsManualCloseSuppressed(name)
                || HasAutoReplacementProfileBeenAttempted(request, name)
                || IsNightReserveProfileProtected(name))
            {
                continue;
            }

            if (!candidate.IsManual)
            {
                var supplyState = GetProfileSupplyState(name);
                var retired =
                    _autoReplacementRetiredProfiles.Contains(name)
                    || (supplyState is not null
                        && (supplyState.State ?? "").Trim().Equals(
                            "retired",
                            StringComparison.OrdinalIgnoreCase));

                if (retired || IsReplacementProfileCoolingDown(name))
                    continue;
            }

            // NAME_SYNC_PENDING dưới 60s chưa phải candidate dùng được ngay; giữ nguyên
            // logic hiện tại là có thể fallback tạo mới thay vì chờ profile quá fresh.
            if (candidate.NameSyncPending
                && DateTime.UtcNow - candidate.LastCheckedUtc < AutoReplacementNameSyncMinRetryAge)
            {
                continue;
            }

            // Profile thật sự RUNNING/PAUSED/RECOVERING không còn là nguồn chờ.
            // Nhưng OPENING/UNKNOWN của một entry vẫn đang trong queue là trạng thái
            // tạm thời; phải CHẶN fallback tạo PRF mới và để request retry, nếu không
            // kết quả sẽ phụ thuộc timing (lúc reuse, lúc tạo mới).
            if (IsReusableProfileBusy(name))
            {
                if (_contexts.TryGetValue(name, out var busyCtx))
                {
                    var busyState = GetEffectiveRuntimeState(busyCtx);
                    if (busyCtx.Opening || busyState == RuntimeStateUnknown)
                    {
                        profileName = name;
                        lane = candidate.IsManual
                            ? "REUSE_MANUAL_STATE_PENDING"
                            : "REUSE_AUTO_STATE_PENDING";
                        detail = $"profile đang chờ xác minh trạng thái; state={busyState}; opening={busyCtx.Opening}";
                        return true;
                    }
                }

                continue;
            }

            // Context không tồn tại không coi là candidate mở được ngay. Refresh/sweep chịu
            // trách nhiệm xác minh và xóa entry thật sự invalid.
            if (!_contexts.ContainsKey(name))
                continue;

            profileName = name;
            lane = candidate.NameSyncPending ? "NAME_SYNC_PENDING" : "REUSE_READY";
            detail = candidate.NameSyncPending
                ? $"age={DateTime.UtcNow - candidate.LastCheckedUtc:c}; entry vẫn còn trong queue và chưa attempted"
                : "entry vẫn còn trong queue, không busy/cooldown/suppressed và chưa attempted";
            return true;
        }

        return false;
    }

    async Task<bool> TryUseReusableProfileQueueAsync(
        AutoReplacementRequest request,
        int executionGeneration,
        CancellationToken executionToken)
    {
        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();

        await RefreshReusableProfileQueueAsync(
            "before_replacement");

        if (!IsAutoReplacementExecutionAllowed(executionGeneration))
            return false;

        executionToken.ThrowIfCancellationRequested();

        var catalog = _profileService.Load();
        RefreshContextsFromCatalog(catalog);

        List<ReusableProfileQueueEntry> candidates;

        lock (_reusableProfileQueueLock)
        {
            candidates =
                OrderReusableProfileEntries(
                    EnsureReusableProfileQueueLoadedUnsafe()
                        .Pending)
                    // NAME_SYNC_PENDING dùng lane riêng: không trộn vào reuse thường.
                    // Tầng ngoài sẽ vét lane này trước khi tiêu account mới.
                    .Where(x => !x.NameSyncPending)
                    .Select(CloneReusableProfileQueueEntry)
                    .ToList();
        }

        // Dự phòng đêm vẫn dùng CHÍNH queue cũ nhưng luôn xếp sau mọi PRF thường.
        // Mặc định PREPARE/PRIME vẫn bảo vệ reserve. Ngoại lệ duy nhất là planned
        // rotation của Giờ vàng đang thiếu quota FRESH: target chạy chính được quyền
        // mượn reserve như một buffer mềm, sau đó Night Reserve sẽ tự bổ sung lại.
        var allowNightReserveBorrowForFreshQuota =
            (request.Reason ?? "").Contains(
                "allow_night_reserve_borrow=fresh_quota",
                StringComparison.OrdinalIgnoreCase);

        candidates = candidates
            .Select((entry, index) => new { entry, index })
            .OrderBy(x => IsNightReserveProfile(x.entry.ProfileName) ? 1 : 0)
            .ThenBy(x => x.index)
            .Select(x => x.entry)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                return false;

            executionToken.ThrowIfCancellationRequested();

            var profileName =
                (candidate.ProfileName ?? "").Trim();

            if (profileName.Length == 0
                || profileName.Equals(
                    request.ClosedProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_autoReplacementClaimedProfiles.Contains(profileName))
                continue;

            if (IsProfileRetireDeleteBlockedForOpen(profileName))
            {
                _log.Warn(
                    $"[REUSE_QUEUE_SKIP_RETIRE_DELETE] id={request.Id} profile={profileName} action=skip_candidate");
                continue;
            }

            if (IsNightReserveProfileProtected(profileName)
                && !allowNightReserveBorrowForFreshQuota)
            {
                _log.Info(
                    $"[REUSE_QUEUE_SKIP_NIGHT_RESERVE] id={request.Id} profile={profileName} reason={request.Reason}");
                continue;
            }

            if (IsNightReserveProfile(profileName)
                && allowNightReserveBorrowForFreshQuota)
            {
                _log.Info(
                    $"[REUSE_QUEUE_BORROW_NIGHT_RESERVE] id={request.Id} profile={profileName} reason={request.Reason}");
            }

            if (IsManualCloseSuppressed(profileName))
            {
                _log.Info(
                    $"[REUSE_QUEUE_SKIP_MANUAL_CLOSE] id={request.Id} profile={profileName}");
                continue;
            }

            if (HasAutoReplacementProfileBeenAttempted(request, profileName))
            {
                _log.Info(
                    $"[REUSE_QUEUE_SKIP_ALREADY_ATTEMPTED] id={request.Id} closed={request.ClosedProfileName} profile={profileName}");
                continue;
            }

            // Re-check ngay trước khi mở vì candidates là snapshot. Không cho profile
            // auto vừa bị retired/cooldown lọt qua do state thay đổi sau refresh.
            if (!candidate.IsManual)
            {
                var supplyState = GetProfileSupplyState(profileName);
                var retired =
                    _autoReplacementRetiredProfiles.Contains(profileName)
                    || (supplyState is not null
                        && (supplyState.State ?? "").Trim().Equals(
                            "retired",
                            StringComparison.OrdinalIgnoreCase));

                if (retired)
                {
                    RemoveReusableProfileQueueEntry(
                        profileName,
                        "retired_before_open");
                    _log.Info(
                        $"[REUSE_QUEUE_SKIP_RETIRED] closed={request.ClosedProfileName} profile={profileName}");
                    continue;
                }

                if (IsReplacementProfileCoolingDown(profileName))
                {
                    _log.Info(
                        $"[REUSE_QUEUE_SKIP_COOLDOWN] closed={request.ClosedProfileName} profile={profileName}");
                    continue;
                }
            }

            if (!_contexts.TryGetValue(profileName, out var ctx))
            {
                RemoveReusableProfileQueueEntry(
                    profileName,
                    "profile_missing_from_context");
                continue;
            }

            // Candidate auto có thể còn Worker/tab STOPPED từ lần chạy trước.
            // Trường hợp đó KHÔNG phải bận và OpenProfileAsync có thể reuse Worker.
            // Nếu state đang UNKNOWN, poll đúng profile này một lần để tránh quyết định
            // theo snapshot cũ; không quét toàn bộ catalog.
            if (!candidate.IsManual
                && IsReusableProfileBusy(profileName)
                && GetEffectiveRuntimeState(ctx) == RuntimeStateUnknown)
            {
                try
                {
                    await RefreshStatusAsync(ctx);
                }
                catch (Exception ex)
                {
                    _log.Info(
                        $"[REUSE_QUEUE_BUSY_RECHECK_WARN] profile={profileName} error={ex.Message}");
                }
            }

            if (IsReusableProfileBusy(profileName))
            {
                var state = GetEffectiveRuntimeState(ctx);

                if (!candidate.IsManual
                    && state is RuntimeStateRunning or RuntimeStatePaused or RuntimeStateRecovering)
                {
                    // Profile thực sự đang hoạt động thì không còn là nguồn chờ auto.
                    RemoveReusableProfileQueueEntry(
                        profileName,
                        "profile_now_actively_running");
                }
                else
                {
                    // OPENING/UNKNOWN là trạng thái tạm thời: giữ entry, để safety guard
                    // chặn tạo PRF mới và retry sau khi state được xác minh.
                    _log.Info(
                        $"[REUSE_QUEUE_DEFER_TRANSIENT_BUSY] profile={profileName} manual={candidate.IsManual} state={state} opening={ctx.Opening}");
                }

                continue;
            }

            // Một candidate đã bắt đầu được thử thì khóa theo request ngay lập tức.
            // Các RETRY của cùng suất không được mở lại candidate này.
            MarkAutoReplacementProfileAttempted(
                request,
                profileName,
                candidate.IsManual ? "reuse_manual" : "reuse_auto");

            _autoReplacementClaimedProfiles.Add(profileName);

            try
            {
                _log.Info(
                    $"[REUSE_QUEUE_OPEN_BEGIN] closed={request.ClosedProfileName} replacement={profileName} account={candidate.Username} total={TimeSpan.FromSeconds(candidate.TotalRunSeconds):c}");

                WriteAutoActivityLog(
                    action: "MỞ PROFILE BÙ",
                    profile: request.ClosedProfileName,
                    account: candidate.Username,
                    reason: request.Reason,
                    replacementProfile: profileName,
                    result: "BẮT ĐẦU",
                    detail:
                        candidate.IsManual
                            ? $"Dùng lại profile THỦ CÔNG. Tổng chạy={TimeSpan.FromSeconds(candidate.TotalRunSeconds):c}."
                            : $"Dùng lại profile tự quét. Tổng chạy={TimeSpan.FromSeconds(candidate.TotalRunSeconds):c}.");

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[REUSE_QUEUE_HARD_STOP_BEFORE_OPEN] profile={profileName} generation={executionGeneration}");
                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                // KHÔNG quét IsProfileInUse() theo từng profile.
                // Chỉ mở đúng candidate đang đứng đầu queue.
                SetAutoReplacementUiPhase(
                    "MỞ PRF CHỜ",
                    profileName,
                    request.Id);

                _ = await OpenProfileAsync(
                    ctx,
                    $"Tự bù cho {request.ClosedProfileName}: dùng lại profile {profileName}...");

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[REUSE_QUEUE_HARD_STOP_AFTER_OPEN] profile={profileName} generation={executionGeneration} emergency={IsAutomationHalted}");

                    if (!IsAutomationHalted)
                    {
                        try
                        {
                            await CloseFailedReplacementRuntimeAsync(ctx);
                        }
                        catch (Exception cleanupEx)
                        {
                            _log.Warn(
                                $"[REUSE_QUEUE_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                        }
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                if (IsManualCloseSuppressed(profileName))
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    _log.Warn(
                        $"[REUSE_QUEUE_MANUAL_ABORT] profile={profileName} stage=after_open");
                    return false;
                }

                // PRF trong Chờ dùng lại đã tồn tại + đăng nhập sẵn: chỉ xác nhận
                // RUNNING trong cửa sổ ngắn. Grace dài 10 phút chỉ dành cho nhánh
                // tạo mới/login, không áp vào reuse.
                SetAutoReplacementUiPhase(
                    "XÁC NHẬN PRF CHỜ",
                    profileName,
                    request.Id);

                var stabilization = await StabilizeReplacementRuntimeAsync(
                    ctx,
                    request,
                    "reuse_queue",
                    executionGeneration,
                    executionToken);

                if (!IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[REUSE_QUEUE_HARD_STOP_AFTER_STABILIZE] profile={profileName} generation={executionGeneration} emergency={IsAutomationHalted}");

                    if (!IsAutomationHalted)
                    {
                        try
                        {
                            await CloseFailedReplacementRuntimeAsync(ctx);
                        }
                        catch (Exception cleanupEx)
                        {
                            _log.Warn(
                                $"[REUSE_QUEUE_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                        }
                    }

                    return false;
                }

                executionToken.ThrowIfCancellationRequested();

                if (IsManualCloseSuppressed(profileName))
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    _log.Warn(
                        $"[REUSE_QUEUE_MANUAL_ABORT] profile={profileName} stage=stabilize");
                    return false;
                }

                if (IsProfileRetireDeleteBlockedForOpen(profileName)
                    || (!stabilization.Healthy
                        && (stabilization.Detail ?? "").StartsWith(
                            "RETIRE_DELETE_BLOCKED:",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    ClearAutoCloseExpectedRunning(
                        profileName,
                        "reuse_retire_delete_abort");

                    RemoveReusableProfileQueueEntry(
                        profileName,
                        "retire_delete_during_stabilize");

                    _log.Warn(
                        $"[REUSE_QUEUE_ABORT_RETIRE_DELETE] id={request.Id} profile={profileName} detail={stabilization.Detail}");

                    // Không cleanup runtime ở đây: AutoClose/Tự xóa đang là owner của
                    // lifecycle này. finally sẽ nhả claim để deleter tiếp tục.
                    continue;
                }

                if (stabilization.NameSyncPending)
                {
                    // Name Guard đã chủ động đóng vì TikTok chưa sync tên. Đây là DEFER,
                    // không phải FAIL. Xác minh đóng sạch rồi chuyển candidate khác.
                    await CleanupCreatedReplacementAttemptAsync(
                        profileName,
                        "reuse_queue_name_sync_pending");
                    TouchReusableProfileNameSyncPending(
                        profileName,
                        "deferred_same_slot_one_attempt");

                    _log.Info(
                        $"[REUSE_QUEUE_NAME_SYNC_DEFERRED] profile={profileName} id={request.Id} action=NEXT_CANDIDATE");
                    continue;
                }

                if (!stabilization.Healthy)
                {
                    await MarkReusableProfileFailedAsync(
                        request,
                        candidate,
                        stabilization.HardFailed
                            ? "stabilize_hard_failed:" + stabilization.Detail
                            : "reuse_fast_timeout:" + stabilization.Detail);

                    // Đóng + xác minh sạch trước khi giải phóng claim và thử profile khác.
                    await CloseFailedReplacementRuntimeAsync(ctx);
                    continue;
                }

                RemoveReusableProfileQueueEntry(
                    profileName,
                    "consumed_success");

                MarkProfileSupplyState(
                    profileName,
                    "used",
                    "reuse_queue_running_confirmed");

                try
                {
                    await RunAccountPoolIoAsync(
                        () =>
                            _accountPoolService.SetAutoProfileResult(
                                candidate.AccountId,
                                "DONE"),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"[REUSE_QUEUE_DONE_WRITE_WARN] profile={profileName} account={candidate.Username} error={ex.Message}");
                }

                _log.Info(
                    $"[REUSE_QUEUE_OPEN_OK] closed={request.ClosedProfileName} replacement={profileName} account={candidate.Username} confirmed=healthy_running");

                WriteAutoActivityLog(
                    action: "MỞ PROFILE BÙ",
                    profile: request.ClosedProfileName,
                    account: candidate.Username,
                    reason: request.Reason,
                    replacementProfile: profileName,
                    result: "THÀNH CÔNG",
                    detail:
                        $"Đã dùng lại profile {profileName}; RUNNING khỏe {AutoReplacementHealthyStableSeconds}s.");

                MarkNightReserveConsumed(profileName, request.Reason);
                return true;
            }
            catch (OperationCanceledException)
                when (executionToken.IsCancellationRequested
                      || !IsAutoReplacementExecutionAllowed(executionGeneration))
            {
                _log.Warn(
                    $"[REUSE_QUEUE_HARD_STOP] profile={profileName} generation={executionGeneration} emergency={IsAutomationHalted}");

                // Dừng khẩn cấp có nghĩa là đóng băng hiện trạng: không tiếp tục cleanup
                // Chrome/Worker sau thời điểm user bấm dừng. Các hard-stop khác (ví dụ
                // manual close co target) vẫn cleanup candidate đang mở như trước.
                if (!IsAutomationHalted)
                {
                    try
                    {
                        await CloseFailedReplacementRuntimeAsync(ctx);
                    }
                    catch (Exception cleanupEx)
                    {
                        _log.Warn(
                            $"[REUSE_QUEUE_HARD_STOP_CLEANUP_WARN] profile={profileName} error={cleanupEx.Message}");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                // Lỗi cứng của PRF đã được phân loại ở nhánh stabilization.Healthy=false
                // phía trên và chỉ nhánh đó mới được đưa PRF vào Failed/cooldown.
                //
                // Exception rơi tới đây chủ yếu là lỗi kỹ thuật (IPC/CDP/I/O/UI race...).
                // Không được xóa PRF khỏi queue hay ghi +auto=FAIL chỉ vì một lỗi hạ tầng
                // tạm thời; nếu làm vậy lượt hiện tại có thể "vét" sai PRF rồi rơi xuống
                // tạo profile mới dù PRF cũ chưa được thử hợp lệ.
                if (ex is AutoReplacementCleanupBarrierException)
                    throw;

                if (IsAutomationHalted
                    || executionToken.IsCancellationRequested
                    || !IsAutoReplacementExecutionAllowed(executionGeneration))
                {
                    _log.Warn(
                        $"[REUSE_QUEUE_TRANSIENT_ABORTED_BY_STOP] profile={profileName} error={ex.Message}");
                    return false;
                }

                _log.Warn(
                    $"[REUSE_QUEUE_TRANSIENT_ERROR] profile={profileName} action=KEEP_QUEUE_RETRY_NEXT_ROUND error={ex.Message}");

                WriteAutoActivityLog(
                    action: "MỞ PROFILE BÙ",
                    profile: request.ClosedProfileName,
                    account: candidate.Username,
                    reason: request.Reason,
                    replacementProfile: profileName,
                    result: "LỖI TẠM THỜI",
                    detail: $"Giữ PRF trong Chờ dùng lại; không đánh FAIL. {ex.Message}");

                try
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                }
                catch (AutoReplacementCleanupBarrierException cleanupEx)
                {
                    throw new AutoReplacementCleanupBarrierException(
                        profileName,
                        $"PRF chờ {profileName} gặp lỗi kỹ thuật và cleanup chưa hoàn tất; chặn mở profile bù kế tiếp.",
                        cleanupEx);
                }

                // Dừng sweep của lượt hiện tại. Request sẽ retry theo backoff; vì
                // AttemptedProfiles đã ghi profile này, retry cùng suất sẽ chuyển sang
                // PRF chờ tiếp theo thay vì mở lại profile vừa lỗi ngay lập tức.
                throw new InvalidOperationException(
                    $"PRF chờ {profileName} gặp lỗi kỹ thuật tạm thời; đã cleanup và giữ lại queue để retry. Không tạo PRF mới trong lượt này.",
                    ex);
            }
            finally
            {
                _autoReplacementClaimedProfiles.Remove(profileName);
            }
        }

        return false;
    }

    async Task MarkReusableProfileFailedAsync(
        AutoReplacementRequest request,
        ReusableProfileQueueEntry candidate,
        string reason)
    {
        var profileName =
            (candidate.ProfileName ?? "").Trim();

        lock (_reusableProfileQueueLock)
        {
            var document =
                CloneReusableProfileQueueDocument(
                    EnsureReusableProfileQueueLoadedUnsafe());

            document.Pending.RemoveAll(x =>
                x.ProfileName.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase));

            document.FailedProfiles[profileName] =
                new ReusableProfileFailureEntry
                {
                    FailedAtTotalRunSeconds =
                        Math.Max(
                            0,
                            candidate.TotalRunSeconds),
                    FailedUtc = DateTime.UtcNow,
                    Reason = reason
                };

            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            SaveReusableProfileQueueUnsafe(document);
        }

        MarkReplacementProfileFailed(
            profileName,
            "reuse_queue:" + reason);

        try
        {
            await RunAccountPoolIoAsync(
                () =>
                    _accountPoolService.SetAutoProfileResult(
                        candidate.AccountId,
                        "FAIL"),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REUSE_QUEUE_FAIL_WRITE_WARN] profile={profileName} account={candidate.Username} error={ex.Message}");
        }

        WriteAutoActivityLog(
            action: "MỞ PROFILE BÙ",
            profile: request.ClosedProfileName,
            account: candidate.Username,
            reason: request.Reason,
            replacementProfile: profileName,
            result: "LỖI",
            detail:
                $"Profile dùng lại thất bại: {reason}. Đã vào cooldown {AutoReplacementFailedProfileCooldown.TotalMinutes:0} phút; nếu cleanup sạch và hết cooldown thì lượt bù sau mới được đánh giá lại.");
    }

    void RemoveReusableProfileQueueEntry(
        string profileName,
        string reason)
    {
        profileName =
            (profileName ?? "").Trim();

        if (profileName.Length == 0)
            return;

        lock (_reusableProfileQueueLock)
        {
            var document =
                CloneReusableProfileQueueDocument(
                    EnsureReusableProfileQueueLoadedUnsafe());

            var removed =
                document.Pending.RemoveAll(x =>
                    x.ProfileName.Equals(
                        profileName,
                        StringComparison.OrdinalIgnoreCase));

            if (removed <= 0)
                return;

            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info(
            $"[REUSE_QUEUE_REMOVE] profile={profileName} reason={reason}");
    }

    static bool IsReusableProfileActuallyCreated(
        TikTokProfileEntry profile)
    {
        try
        {
            var profilePath =
                Path.GetFullPath(
                    (profile.ProfilePath ?? "").Trim());

            if (!Directory.Exists(profilePath))
                return false;

            // Tool tự tạo Default/Preferences trước lần mở Chrome đầu tiên,
            // nên không dùng các file đó để xác nhận. Local State do Chromium
            // sinh ở root user-data-dir sau khi Chrome thực sự khởi tạo.
            var localStatePath =
                Path.Combine(
                    profilePath,
                    "Local State");

            if (!File.Exists(localStatePath))
                return false;

            // Tránh nhận nhầm file rỗng/hỏng do một lần tạo dở dang.
            var info =
                new FileInfo(localStatePath);

            return info.Length > 2;
        }
        catch
        {
            return false;
        }
    }

    double ReadReusableProfileTotalSeconds(
        TikTokProfileEntry profile)
    {
        try
        {
            var dataRoot =
                _profileService.ResolveDataRoot(profile);

            var path =
                Path.Combine(
                    dataRoot,
                    "runtime_stats.json");

            if (!File.Exists(path))
                return 0;

            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty(
                    "totalRunSeconds",
                    out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var seconds))
            {
                return 0;
            }

            return Math.Max(
                0,
                seconds);
        }
        catch
        {
            return 0;
        }
    }

    ReusableProfileQueueDocument
        EnsureReusableProfileQueueLoadedUnsafe()
    {
        if (_reusableProfileQueueLoaded)
            return _reusableProfileQueueCache;

        _reusableProfileQueueCache =
            LoadReusableProfileQueueUnsafe();

        _reusableProfileQueueLoaded = true;

        return _reusableProfileQueueCache;
    }

    ReusableProfileQueueDocument
        LoadReusableProfileQueueUnsafe()
    {
        try
        {
            if (!File.Exists(ReusableProfileQueuePath))
                return NewReusableProfileQueueDocument();

            var loaded =
                JsonSerializer.Deserialize<ReusableProfileQueueDocument>(
                    File.ReadAllText(
                        ReusableProfileQueuePath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            loaded ??=
                NewReusableProfileQueueDocument();

            loaded.Pending ??=
                new List<ReusableProfileQueueEntry>();

            loaded.ExcludedProfiles ??=
                new List<string>();

            loaded.FailedProfiles ??=
                new Dictionary<string, ReusableProfileFailureEntry>(
                    StringComparer.OrdinalIgnoreCase);

            loaded.FailedProfiles =
                new Dictionary<string, ReusableProfileFailureEntry>(
                    loaded.FailedProfiles,
                    StringComparer.OrdinalIgnoreCase);

            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[REUSE_QUEUE_READ_WARN] {ex.Message}");

            return NewReusableProfileQueueDocument();
        }
    }

    void SaveReusableProfileQueueUnsafe(
        ReusableProfileQueueDocument document)
    {
        var json =
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        var temp =
            ReusableProfileQueuePath
            + ".tmp";

        File.WriteAllText(
            temp,
            json,
            new UTF8Encoding(false));

        File.Move(
            temp,
            ReusableProfileQueuePath,
            overwrite: true);
    }

    static ReusableProfileQueueDocument
        NewReusableProfileQueueDocument()
        => new()
        {
            Version = 3,
            Pending = new List<ReusableProfileQueueEntry>(),
            ExcludedProfiles = new List<string>(),
            FailedProfiles =
                new Dictionary<string, ReusableProfileFailureEntry>(
                    StringComparer.OrdinalIgnoreCase)
        };

    static ReusableProfileQueueEntry
        CloneReusableProfileQueueEntry(
            ReusableProfileQueueEntry source)
        => new()
        {
            ProfileName = source.ProfileName ?? "",
            AccountId = source.AccountId ?? "",
            Username = source.Username ?? "",
            TotalRunSeconds = source.TotalRunSeconds,
            IsManual = source.IsManual,
            NameSyncPending = source.NameSyncPending,
            NameSyncQueuedUtc = source.NameSyncQueuedUtc,
            NameSyncCheckCount = source.NameSyncCheckCount,
            AddedUtc = source.AddedUtc,
            LastCheckedUtc = source.LastCheckedUtc
        };

    static ReusableProfileQueueDocument
        CloneReusableProfileQueueDocument(
            ReusableProfileQueueDocument source)
        => new()
        {
            Version = Math.Max(3, source.Version),
            Pending =
                source.Pending
                    .Select(
                        CloneReusableProfileQueueEntry)
                    .ToList(),
            ExcludedProfiles =
                (source.ExcludedProfiles ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            FailedProfiles =
                source.FailedProfiles.ToDictionary(
                    x => x.Key,
                    x => new ReusableProfileFailureEntry
                    {
                        FailedAtTotalRunSeconds =
                            x.Value.FailedAtTotalRunSeconds,
                        FailedUtc =
                            x.Value.FailedUtc,
                        Reason =
                            x.Value.Reason ?? ""
                    },
                    StringComparer.OrdinalIgnoreCase)
        };

    static string FormatReusableRuntime(
        TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        if (value.TotalHours >= 1)
        {
            return
                $"{(int)value.TotalHours}h {value.Minutes:00}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return
                $"{(int)value.TotalMinutes}m {value.Seconds:00}s";
        }

        return $"{Math.Max(0, value.Seconds)}s";
    }
}
