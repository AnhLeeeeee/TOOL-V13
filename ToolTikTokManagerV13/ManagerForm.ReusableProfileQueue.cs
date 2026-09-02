using System.Text;
using System.Text.Json;
using ToolTikTokV12.Models;

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
        public int Version { get; set; } = 2;
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
                var workerAlive = false;

                try
                {
                    workerAlive =
                        ctx.Worker is not null
                        && !ctx.Worker.HasExited;
                }
                catch { }

                var tabOpen =
                    ctx.Tab is not null
                    && !ctx.Tab.IsDisposed
                    && ctx.Tab.Parent == _tabs;

                if (tabOpen || workerAlive || ctx.Opening)
                    busyProfiles.Add(ctx.Profile.Name);
            }

            var accounts = await RunAccountPoolIoAsync(
                () => _accountPoolService.Load(),
                ct);

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
                    8);

            var automaticMaxTotalSeconds =
                TimeSpan.FromHours(
                    automaticMaxHours).TotalSeconds;

            var eligible =
                new List<ReusableProfileQueueEntry>();

            var reasons =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var profile in catalog.Profiles)
            {
                ct.ThrowIfCancellationRequested();

                var profileName =
                    (profile.Name ?? "").Trim();

                if (profileName.Length == 0)
                    continue;

                // Xác định entry thủ công từ queue cũ trước khi đánh giá điều kiện.
                var isManual =
                    previousByProfile.TryGetValue(
                        profileName,
                        out var oldEntry)
                    && oldEntry.IsManual;

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

                // retired và FailedProfiles KHÔNG còn là điều kiện khóa ngầm.
                // Một profile tự quét chỉ cần thỏa đúng các điều kiện nhìn thấy bên dưới.
                if (excludedProfiles.Contains(profileName))
                {
                    reasons[profileName] = "ĐÃ BỎ CHỜ";
                    continue;
                }

                if (busyProfiles.Contains(profileName))
                {
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

                var wasFailed = failedProfiles.Remove(profileName);
                reasons[profileName] =
                    wasFailed
                        ? "ĐỦ ĐIỀU KIỆN · THỬ LẠI"
                        : "ĐỦ ĐIỀU KIỆN";

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
                    Version = 2,
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
                .Where(x => !x.IsManual)
                .OrderByDescending(x => x.TotalRunSeconds)
                .ThenBy(
                    x => x.ProfileName,
                    NaturalProfileNameOrder);

        return manual
            .Concat(automatic)
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

            document.Version = 2;

            _reusableProfileQueueCache = document;
            _reusableProfileQueueLoaded = true;
            SaveReusableProfileQueueUnsafe(document);
        }

        _log.Info(
            $"[REUSE_QUEUE_MANUAL_ADD] profile={profileName} account={username} total={TimeSpan.FromSeconds(totalSeconds):c}");

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

            document.Version = 2;

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

        var workerAlive = false;

        try
        {
            workerAlive =
                ctx.Worker is not null
                && !ctx.Worker.HasExited;
        }
        catch { }

        var tabOpen =
            ctx.Tab is not null
            && !ctx.Tab.IsDisposed
            && ctx.Tab.Parent == _tabs;

        return ctx.Opening
               || workerAlive
               || tabOpen;
    }

    async Task<bool> TryUseReusableProfileQueueAsync(
        AutoReplacementRequest request)
    {
        await RefreshReusableProfileQueueAsync(
            "before_replacement");

        if (_closing)
            return false;

        var catalog = _profileService.Load();
        RefreshContextsFromCatalog(catalog);

        List<ReusableProfileQueueEntry> candidates;

        lock (_reusableProfileQueueLock)
        {
            candidates =
                OrderReusableProfileEntries(
                    EnsureReusableProfileQueueLoadedUnsafe()
                        .Pending)
                    .Select(CloneReusableProfileQueueEntry)
                    .ToList();
        }

        foreach (var candidate in candidates)
        {
            if (_closing)
                return false;

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

            if (!_contexts.TryGetValue(profileName, out var ctx))
            {
                RemoveReusableProfileQueueEntry(
                    profileName,
                    "profile_missing_from_context");
                continue;
            }

            var workerAlive = false;

            try
            {
                workerAlive =
                    ctx.Worker is not null
                    && !ctx.Worker.HasExited;
            }
            catch { }

            if (ctx.Opening
                || workerAlive
                || (ctx.Tab is not null
                    && !ctx.Tab.IsDisposed
                    && ctx.Tab.Parent == _tabs))
            {
                // Queue thủ công được giữ lại nếu profile tạm thời đang mở.
                // Queue tự động thì bỏ để lần scan sau tự đánh giá lại.
                if (!candidate.IsManual)
                {
                    RemoveReusableProfileQueueEntry(
                        profileName,
                        "profile_now_busy");
                }

                continue;
            }

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

                // KHÔNG quét IsProfileInUse() theo từng profile.
                // Chỉ mở đúng candidate đang đứng đầu queue.
                var opened =
                    await OpenProfileAsync(
                        ctx,
                        $"Tự bù cho {request.ClosedProfileName}: dùng lại profile {profileName}...");

                if (!opened
                    && (ctx.Worker is null
                        || ctx.Worker.HasExited))
                {
                    await MarkReusableProfileFailedAsync(
                        candidate,
                        "open_worker");

                    continue;
                }

                var reply =
                    await StartWithNameGuardAsync(
                        ctx,
                        "start_auto",
                        TimeSpan.FromSeconds(100),
                        suppressStatus: true);

                if (!string.Equals(
                        reply,
                        "started",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await MarkReusableProfileFailedAsync(
                        candidate,
                        "start:" + reply);

                    await CloseFailedReplacementRuntimeAsync(ctx);
                    continue;
                }

                var healthy =
                    await WaitForReplacementHealthyRunningAsync(
                        ctx,
                        request,
                        "reuse_queue");

                if (!healthy)
                {
                    await MarkReusableProfileFailedAsync(
                        candidate,
                        "started_but_not_healthy");

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

                return true;
            }
            catch (Exception ex)
            {
                await MarkReusableProfileFailedAsync(
                    candidate,
                    "exception:" + ex.Message);

                _log.Warn(
                    $"[REUSE_QUEUE_OPEN_ERROR] profile={profileName} error={ex.Message}");

                try
                {
                    await CloseFailedReplacementRuntimeAsync(ctx);
                }
                catch { }
            }
            finally
            {
                _autoReplacementClaimedProfiles.Remove(profileName);
            }
        }

        return false;
    }

    async Task MarkReusableProfileFailedAsync(
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
            account: candidate.Username,
            replacementProfile: profileName,
            result: "LỖI",
            detail:
                $"Profile dùng lại thất bại: {reason}. Lượt quét sau sẽ tự đánh giá lại; nếu profile vẫn đủ điều kiện thì được đưa lại vào queue.");
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
            Version = 2,
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
            AddedUtc = source.AddedUtc,
            LastCheckedUtc = source.LastCheckedUtc
        };

    static ReusableProfileQueueDocument
        CloneReusableProfileQueueDocument(
            ReusableProfileQueueDocument source)
        => new()
        {
            Version = Math.Max(2, source.Version),
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
