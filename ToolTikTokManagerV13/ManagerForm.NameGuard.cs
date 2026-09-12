using System.Text;
using System.Text.Json;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class NameGuardProbeReply
    {
        public bool Ok { get; set; }
        public string CurrentName { get; set; } = "";
        public bool Matched { get; set; }
        public string CurrentHandle { get; set; } = "";
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
        public bool Transient { get; set; }
    }

    sealed record NameGuardResult(
        bool Allowed,
        string Message,
        bool ChangedName = false,
        bool Transient = false);

    static readonly TimeSpan NameGuardTransientRetryDelay = TimeSpan.FromSeconds(15);
    static readonly TimeSpan NameGuardManagerProbeTimeout = TimeSpan.FromSeconds(45);
    const int NameGuardTransientStartMaxAttempts = 3;

    // Phân biệt "đã xử lý trong phiên" với "đã xác minh tên đúng".
    // _autoIdentityHandledSession còn được dùng để khóa loop sau FAIL, nên tuyệt đối
    // không thể dùng nó làm bằng chứng tên đã đúng khi Start.
    readonly Dictionary<string, string> _nameGuardVerifiedSessionAccount = new(StringComparer.OrdinalIgnoreCase);

    void MarkNameGuardVerifiedForCurrentChromeSession(ProfileContext ctx, string username)
    {
        username = (username ?? "").Trim();
        if (username.Length > 0)
            _nameGuardVerifiedSessionAccount[ctx.Profile.Name] = username;

        _autoIdentityHandledSession.Add(ctx.Profile.Name);
        if (username.Length > 0)
            _autoIdentityHandledSession.Add("account:" + username.ToLowerInvariant());
        _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);
    }

    NameGuardResult RegisterNameGuardPersistenceWarning(
        ProfileContext ctx,
        string username,
        string operation,
        string error,
        bool changedName = false)
    {
        MarkNameGuardVerifiedForCurrentChromeSession(ctx, username);

        var message = operation + " nhưng không lưu được Tên/ảnh=DONE vào Excel: " + error;
        _log.Warn(
            $"[NAME_GUARD_PERSIST_WARNING] profile={ctx.Profile.Name} account={username} " +
            $"operation={operation} action=ALLOW_KEEP_OPEN error={error}");

        WriteAutoDiagnosticEvent(
            ctx,
            "name_guard_persist",
            "NAME_GUARD_PERSIST_WARNING",
            "ALLOW_KEEP_OPEN",
            $"account={username}; operation={operation}; error={error}");

        // Excel chỉ là persistence/cache trạng thái. Khi tên thực tế đã được xác minh
        // đúng (hoặc TikTok đã Save/Confirm đổi thành công), lỗi ghi Excel không được
        // phép biến một tài khoản tốt thành FAIL hay kích hoạt cleanup.
        return new NameGuardResult(true, message, ChangedName: changedName);
    }

    async Task<string> StartWithNameGuardAsync(
        ProfileContext ctx,
        string command,
        TimeSpan timeout,
        bool suppressStatus = false)
    {
        // Lỗi kỹ thuật tạm thời của Name Guard (DOM/CDP/IPC chậm) không được biến
        // ngay thành một profile bù hỏng. Giữ nguyên Chrome/Worker và thử lại cùng
        // profile tối đa 3 lượt, cách nhau 15 giây. Chỉ lỗi cứng mới BLOCK ngay.
        for (var attempt = 1; attempt <= NameGuardTransientStartMaxAttempts; attempt++)
        {
            var guard = await EnsureNameGuardBeforeStartAsync(ctx);
            if (guard.Allowed)
            {
                var reply = await SendCommandAsync(ctx, command, timeout);
                _log.Info(
                    $"[NAME_GUARD_START_ALLOWED] profile={ctx.Profile.Name} command={command} " +
                    $"attempt={attempt}/{NameGuardTransientStartMaxAttempts} changed={guard.ChangedName} reply={reply}");
                return reply;
            }

            if (!guard.Transient)
            {
                if (!suppressStatus)
                    SetStatus(ctx, "Không Start: " + guard.Message, Color.Firebrick);

                _log.Warn(
                    $"[NAME_GUARD_START_BLOCKED] profile={ctx.Profile.Name} command={command} " +
                    $"attempt={attempt}/{NameGuardTransientStartMaxAttempts} transient=false reason={guard.Message}");
                return "name_guard_blocked";
            }

            if (attempt >= NameGuardTransientStartMaxAttempts)
            {
                if (!suppressStatus)
                    SetStatus(ctx, "Name Guard tạm thời chưa sẵn sàng; sẽ thử lại ở lượt sau.", Color.DarkOrange);

                _log.Warn(
                    $"[NAME_GUARD_START_TRANSIENT_EXHAUSTED] profile={ctx.Profile.Name} command={command} " +
                    $"attempt={attempt}/{NameGuardTransientStartMaxAttempts} reason={guard.Message}");
                return "name_guard_transient";
            }

            if (!suppressStatus)
            {
                SetStatus(
                    ctx,
                    $"Name Guard đang chờ ổn định ({attempt}/{NameGuardTransientStartMaxAttempts}); thử lại sau {NameGuardTransientRetryDelay.TotalSeconds:0}s...",
                    Color.DarkOrange);
            }

            _log.Warn(
                $"[NAME_GUARD_START_TRANSIENT_RETRY] profile={ctx.Profile.Name} command={command} " +
                $"attempt={attempt}/{NameGuardTransientStartMaxAttempts} retryIn={NameGuardTransientRetryDelay.TotalSeconds:0}s " +
                $"workerAlive={IsNameGuardWorkerAlive(ctx)} reason={guard.Message}");

            await Task.Delay(NameGuardTransientRetryDelay);
        }

        return "name_guard_transient";
    }

    static bool IsNameGuardTransientStartReply(string? reply)
        => string.Equals(
            (reply ?? "").Trim(),
            "name_guard_transient",
            StringComparison.OrdinalIgnoreCase);

    async Task<NameGuardResult> EnsureNameGuardBeforeStartAsync(ProfileContext ctx)
    {
        var state = LoadIdentityToolState();
        if (!state.UpdateName)
            return new NameGuardResult(true, "Kiểm tra tên đang tắt trong Tên & ảnh TikTok.");

        var names = SplitIdentityNames(state.NamesText);
        if (names.Count == 0)
            return new NameGuardResult(true, "Danh sách tên cấu hình đang trống.");

        var account = await ResolveNameGuardAccountAsync(ctx);
        var username = account.Username;
        if (username.Length == 0)
            return new NameGuardResult(false, "Không xác định được tài khoản đang gán cho profile.");

        // Nếu chính phiên Chrome hiện tại đã xác minh tên đúng thì Start phải đi tiếp
        // ngay cả khi Excel nguồn đang mất/khóa. Chỉ tin cache này khi Chrome hiện vẫn
        // CONNECTED và đúng cùng username, tránh mang kết quả sang phiên/account khác.
        if (string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase)
            && _nameGuardVerifiedSessionAccount.TryGetValue(ctx.Profile.Name, out var verifiedUsername)
            && verifiedUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"[NAME_GUARD_SKIP_SESSION_VERIFIED] profile={ctx.Profile.Name} account={username}");
            return new NameGuardResult(true, "Tên đã được xác minh đúng trong phiên Chrome hiện tại.");
        }

        // DONE trong Excel là nguồn bỏ qua nhanh: không mở trang Hồ sơ, không kiểm tra tên.
        try
        {
            var alreadyDone = await RunAccountPoolIoAsync(
                () => _accountPoolService.IsIdentityDone(username),
                CancellationToken.None);
            if (alreadyDone)
            {
                _autoIdentityHandledSession.Add(ctx.Profile.Name);
                _autoIdentityHandledSession.Add("account:" + username.ToLowerInvariant());
                _log.Info($"[NAME_GUARD_SKIP_EXCEL_DONE] profile={ctx.Profile.Name} account={username}");
                return new NameGuardResult(true, "Tên/ảnh đã DONE trong Excel, bỏ qua kiểm tra tên.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_EXCEL_READ_WARN] profile={ctx.Profile.Name} account={username} {ex.Message}");
        }

        // Manual Start và Auto Tên/ảnh dùng chung một gate: toàn bộ khâu kiểm tra/đổi
        // chỉ chạy từng PRF một, không có hai Chrome bị điều khiển song song.
        var slot = await AcquireManualIdentitySlotAsync(ctx.Profile.Name, TimeSpan.FromSeconds(45));
        if (!slot)
        {
            return new NameGuardResult(
                false,
                "Tên của profile đang được luồng Tên/ảnh khác xử lý.",
                Transient: true);
        }

        await _autoIdentityQueueGate.WaitAsync();
        try
        {
            // Nếu trong lúc chờ gate một lượt AutoOnReady vừa hoàn tất và ghi DONE,
            // bỏ qua ngay để không kiểm tra lại tên lần thứ hai.
            try
            {
                var doneAfterWait = await RunAccountPoolIoAsync(
                    () => _accountPoolService.IsIdentityDone(username),
                    CancellationToken.None);
                if (doneAfterWait)
                {
                    _autoIdentityHandledSession.Add(ctx.Profile.Name);
                    _autoIdentityHandledSession.Add("account:" + username.ToLowerInvariant());
                    _log.Info($"[NAME_GUARD_SKIP_DONE_AFTER_WAIT] profile={ctx.Profile.Name} account={username}");
                    return new NameGuardResult(true, "Tên/ảnh đã DONE trong lúc chờ, bỏ qua kiểm tra.");
                }
            }
            catch { }

            await OpenProfileAsync(ctx);
            try { await RefreshStatusAsync(ctx); } catch { }

            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                await OpenChromeForProfileAsync(ctx);
                try { await RefreshStatusAsync(ctx); } catch { }
            }

            if (!string.Equals(ctx.LastSnapshot?.Chrome, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                return RegisterNameGuardTransientFailure(
                    ctx,
                    username,
                    "Chrome chưa kết nối.",
                    "chrome_not_connected_before_probe");
            }

            return await ProcessNameGuardOnceAsync(ctx, username, state, names);
        }
        catch (Exception ex)
        {
            return RegisterNameGuardTransientFailure(
                ctx,
                username,
                ex.Message,
                "ensure_before_start_exception",
                ex);
        }
        finally
        {
            _autoIdentityQueueGate.Release();
            _autoIdentityInFlight.Remove(ctx.Profile.Name);
        }
    }

    // Core một-lượt dùng chung cho Start và AutoOnReady. Hàm này giả định Chrome đã
    // CONNECTED và caller đang giữ _autoIdentityQueueGate; không tự retry cả quy trình.
    async Task<NameGuardResult> ProcessNameGuardOnceAsync(
        ProfileContext ctx,
        string username,
        IdentityToolState state,
        IReadOnlyList<string> names)
    {
        // 1) Lấy href Hồ sơ -> điều hướng -> poll tên. Không F5.
        var probe = await ProbeNameGuardFastAsync(ctx, username, names);
        if (!probe.Ok)
        {
            var reason = string.IsNullOrWhiteSpace(probe.Message)
                ? "Không đọc được tên trên trang Hồ sơ TikTok."
                : probe.Message;

            // Probe chỉ là bước ĐỌC. Không đọc được tên, IPC rỗng, JSON lỗi,
            // Worker vừa thoát, CDP/DOM tạm lỗi... đều là lỗi kỹ thuật. Tuyệt đối
            // không được biến lỗi này thành Tên/ảnh=FAIL rồi đóng Chrome/Worker.
            return RegisterNameGuardTransientFailure(
                ctx,
                username,
                reason,
                string.IsNullOrWhiteSpace(probe.Source)
                    ? "profile_name_probe_failed"
                    : "profile_name_probe_failed:" + probe.Source);
        }

        // Nếu lần đầu đọc ra tên SAI, chưa được phép sửa/đóng ngay. Worker đã yêu cầu
        // mismatch ổn định trong cùng một probe; Manager vẫn xác nhận thêm một probe độc lập
        // sau khoảng nghỉ để loại trừ snapshot cũ của TikTok SPA.
        if (!probe.Matched)
        {
            var firstMismatchName = (probe.CurrentName ?? "").Trim();
            await Task.Delay(1800);

            var confirmProbe = await ProbeNameGuardFastAsync(ctx, username, names);
            _log.Info(
                $"[NAME_GUARD_MISMATCH_SECOND_CONFIRM] profile={ctx.Profile.Name} account={username} " +
                $"first='{firstMismatchName}' second='{confirmProbe.CurrentName}' " +
                $"ok={confirmProbe.Ok} matched={confirmProbe.Matched} source={confirmProbe.Source}");

            if (!confirmProbe.Ok)
            {
                var confirmReason = string.IsNullOrWhiteSpace(confirmProbe.Message)
                    ? "Lần xác nhận thứ hai chưa đọc ổn định được tên trên Hồ sơ."
                    : confirmProbe.Message;

                return RegisterNameGuardTransientFailure(
                    ctx,
                    username,
                    confirmReason,
                    "mismatch_second_probe_unstable");
            }

            // Lần hai đã thấy tên đúng => lần đầu là snapshot cũ. Cho chạy và ghi DONE
            // qua nhánh matched phía dưới, tuyệt đối không sửa tên/ảnh.
            if (confirmProbe.Matched)
            {
                _log.Info(
                    $"[NAME_GUARD_FALSE_MISMATCH_RECOVERED] profile={ctx.Profile.Name} account={username} " +
                    $"first='{firstMismatchName}' current='{confirmProbe.CurrentName}'");
                probe = confirmProbe;
            }
            else
            {
                var secondMismatchName = (confirmProbe.CurrentName ?? "").Trim();

                // Hai probe đều báo sai nhưng lại thấy hai giá trị khác nhau => DOM đang
                // chuyển trạng thái, không đủ bằng chứng để sửa/đóng. Giữ Chrome mở và retry.
                if (!firstMismatchName.Equals(secondMismatchName, StringComparison.OrdinalIgnoreCase))
                {
                    return RegisterNameGuardTransientFailure(
                        ctx,
                        username,
                        $"Tên trên Hồ sơ chưa ổn định: lần 1='{firstMismatchName}', lần 2='{secondMismatchName}'.",
                        "mismatch_values_changed_between_probes");
                }

                // Chỉ khi hai probe độc lập cùng xác nhận đúng một tên sai mới cho đi tiếp
                // sang luồng cập nhật Tên/ảnh.
                probe = confirmProbe;
                _log.Info(
                    $"[NAME_GUARD_MISMATCH_CONFIRMED_TWICE] profile={ctx.Profile.Name} account={username} " +
                    $"currentName='{probe.CurrentName}'");
            }
        }

        // 2) Tên trùng BẤT KỲ tên mẫu => bằng chứng thực tế đã đủ để cho chạy.
        // Ghi Excel DONE chỉ là persistence; ghi lỗi KHÔNG được đóng Chrome/Worker.
        if (probe.Matched)
        {
            MarkNameGuardVerifiedForCurrentChromeSession(ctx, username);

            var done = await MarkIdentityDoneVerifiedAsync(username, ctx.Profile.Name, CancellationToken.None);
            if (!done.Ok)
            {
                return RegisterNameGuardPersistenceWarning(
                    ctx,
                    username,
                    "Tên hiện tại đã đúng mẫu",
                    done.Error);
            }

            _log.Info($"[NAME_GUARD_NAME_OK] profile={ctx.Profile.Name} account={username} currentName={probe.CurrentName} source={probe.Source}");
            return new NameGuardResult(true, "Tên hiện tại đúng mẫu.");
        }

        // 3) Tên sai => chạy ĐẦY ĐỦ tên + ảnh (+ bio nếu đang bật).
        await TrySetNameGuardExcelStatusAsync(username, "PROCESSING", ctx.Profile.Name);

        var targetName = ChooseNameGuardTargetName(ctx, names, state.RandomNames);
        var (avatarPath, bio) = ChooseNameGuardExtraIdentity(ctx, state);
        _log.Info($"[NAME_GUARD_NAME_WRONG] profile={ctx.Profile.Name} account={username} currentName={probe.CurrentName} target={targetName} avatar={(string.IsNullOrWhiteSpace(avatarPath) ? "no" : Path.GetFileName(avatarPath))}");

        var reply = await UpdateTikTokIdentityAsync(
            ctx,
            targetName,
            avatarPath,
            bio,
            skipIfNameCooldown: true,
            resumeAutomation: false,
            knownDisplayNames: names,
            verifyExistingState: false,
            workerTimeout: TimeSpan.FromSeconds(75),
            nameGuardFastMode: true);

        // Save/Confirm chỉ xác nhận TikTok đã nhận thao tác, KHÔNG phải bằng chứng
        // tên đã phản ánh trên trang Hồ sơ. Vì TikTok có thể đồng bộ nickname chậm,
        // tuyệt đối không ghi DONE chỉ dựa vào reply.Ok.
        var completed = reply.Ok && !reply.NameCooldown && !reply.Skipped;
        if (!completed)
        {
            var reason = !reply.Ok
                ? (string.IsNullOrWhiteSpace(reply.Error) ? reply.Message : reply.Error)
                : reply.NameCooldown
                    ? "TikTok đang giới hạn thời gian đổi tên."
                    : string.IsNullOrWhiteSpace(reply.Message) ? "Đổi Tên/ảnh không thành công." : reply.Message;
            await FailNameGuardAndCloseAsync(ctx, username, reason);
            return new NameGuardResult(false, reason);
        }

        // 4) Sau Save/Confirm, quay lại Hồ sơ và đọc tên thực tế. Probe hiện có chỉ
        // đọc [data-e2e=user-title], không dùng giá trị trong form Edit profile.
        // Thử tối đa 2 lần ngắn; nếu TikTok chưa kịp đồng bộ thì đưa profile sang
        // lane NAME_SYNC_PENDING để recovery sweep hiện có kiểm tra lại sau.
        NameGuardProbeReply? verifiedProbe = null;
        for (var verifyAttempt = 1; verifyAttempt <= 2; verifyAttempt++)
        {
            if (verifyAttempt > 1)
                await Task.Delay(1200);

            verifiedProbe = await ProbeNameGuardFastAsync(ctx, username, names);
            _log.Info(
                $"[NAME_GUARD_AFTER_SAVE_VERIFY] profile={ctx.Profile.Name} account={username} " +
                $"attempt={verifyAttempt}/2 ok={verifiedProbe.Ok} matched={verifiedProbe.Matched} " +
                $"currentName={verifiedProbe.CurrentName} source={verifiedProbe.Source}");

            if (verifiedProbe.Ok && verifiedProbe.Matched)
                break;
        }

        if (verifiedProbe is null || !verifiedProbe.Ok || !verifiedProbe.Matched)
        {
            var seenName = verifiedProbe?.CurrentName ?? "";
            var detail = verifiedProbe is null || !verifiedProbe.Ok
                ? $"TikTok đã Save/Confirm nhưng chưa đọc/xác minh được tên mới trên Hồ sơ. " +
                  $"source={verifiedProbe?.Source ?? "-"}; message={verifiedProbe?.Message ?? "-"}"
                : $"TikTok đã Save/Confirm nhưng tên trên Hồ sơ vẫn chưa cập nhật. " +
                  $"currentName='{seenName}', target='{targetName}'.";

            await QueueNameGuardNameSyncPendingAndCloseAsync(ctx, username, detail);
            return new NameGuardResult(
                false,
                "Tên đã Save nhưng TikTok chưa đồng bộ trên Hồ sơ; đã đưa profile vào Chờ dùng lại.",
                ChangedName: reply.NameChanged,
                Transient: true);
        }

        // Chỉ tới đây mới có bằng chứng tên thực tế trên Hồ sơ đã đúng.
        if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(avatarPath))
        {
            state.LastAvatarByProfile[ctx.Profile.Name] = avatarPath;
            SaveIdentityToolState(state);
        }

        MarkNameGuardVerifiedForCurrentChromeSession(ctx, username);

        var excelDone = await MarkIdentityDoneVerifiedAsync(username, ctx.Profile.Name, CancellationToken.None);
        if (!excelDone.Ok)
        {
            return RegisterNameGuardPersistenceWarning(
                ctx,
                username,
                "Tên trên Hồ sơ đã được xác minh đúng sau Save/Confirm",
                excelDone.Error,
                changedName: reply.NameChanged);
        }

        _log.Info(
            $"[NAME_GUARD_UPDATE_DONE_VERIFIED] profile={ctx.Profile.Name} account={username} " +
            $"target={targetName} currentName={verifiedProbe.CurrentName} nameChanged={reply.NameChanged} avatarChanged={reply.AvatarChanged}");
        return new NameGuardResult(true, "Đổi Tên/ảnh thành công và đã xác minh tên trên Hồ sơ.", ChangedName: reply.NameChanged);
    }

    async Task<(string Username, string AssignedProfile)> ResolveNameGuardAccountAsync(ProfileContext ctx)
    {
        string authUsername = "";
        try
        {
            var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
            authUsername = (_tiktokAuthService.Load(dataRoot).Username ?? "").Trim();
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_AUTH_READ_WARN] profile={ctx.Profile.Name} {ex.Message}");
        }

        var accounts = await RunAccountPoolIoAsync(
            () => _accountPoolService.Load(),
            CancellationToken.None);

        var account = authUsername.Length > 0
            ? accounts.FirstOrDefault(x => x.Username.Equals(authUsername, StringComparison.OrdinalIgnoreCase))
            : null;
        account ??= accounts.FirstOrDefault(x =>
            (x.AssignedProfile ?? "").Trim().Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase));

        return account is null
            ? (authUsername, "")
            : (account.Username.Trim(), (account.AssignedProfile ?? "").Trim());
    }

    async Task<NameGuardProbeReply> ProbeNameGuardFastAsync(
        ProfileContext ctx,
        string username,
        IReadOnlyList<string> allowedNames)
    {
        try
        {
            var request = JsonSerializer.Serialize(new
            {
                Username = username,
                AllowedDisplayNames = allowedNames
            });
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            var raw = await SendCommandAsync(
                ctx,
                "identity_name_probe|" + payload,
                NameGuardManagerProbeTimeout);

            if (string.IsNullOrWhiteSpace(raw))
            {
                _log.Warn($"[NAME_GUARD_PROFILE_PROBE_EMPTY] profile={ctx.Profile.Name} account={username} workerAlive={IsNameGuardWorkerAlive(ctx)}");
                return new NameGuardProbeReply
                {
                    Ok = false,
                    Transient = true,
                    Source = "ipc_empty_response",
                    Message = "Worker không trả dữ liệu Name Guard (IPC response rỗng)."
                };
            }

            try
            {
                var reply = JsonSerializer.Deserialize<NameGuardProbeReply>(
                    raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (reply is null)
                {
                    return new NameGuardProbeReply
                    {
                        Ok = false,
                        Transient = true,
                        Source = "ipc_null_json",
                        Message = "Worker trả kết quả Name Guard không hợp lệ."
                    };
                }

                // Một response JSON hợp lệ nhưng ok=false ở bước PROBE vẫn chỉ có
                // nghĩa là chưa đọc được tên. Đây không phải bằng chứng tên sai.
                if (!reply.Ok)
                    reply.Transient = true;

                return reply;
            }
            catch (JsonException ex)
            {
                _log.Warn($"[NAME_GUARD_PROFILE_PROBE_JSON_INVALID] profile={ctx.Profile.Name} account={username} rawLength={raw.Length} {ex.Message}");
                return new NameGuardProbeReply
                {
                    Ok = false,
                    Transient = true,
                    Source = "ipc_invalid_json",
                    Message = "Worker trả dữ liệu Name Guard không phải JSON hợp lệ: " + ex.Message
                };
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_PROFILE_PROBE_FAILED] profile={ctx.Profile.Name} account={username} workerAlive={IsNameGuardWorkerAlive(ctx)} {ex.Message}");
            return new NameGuardProbeReply
            {
                Ok = false,
                Transient = true,
                Source = "ipc_or_worker_exception",
                Message = ex.Message
            };
        }
    }

    bool IsNameGuardWorkerAlive(ProfileContext ctx)
    {
        try { return ctx.Worker is not null && !ctx.Worker.HasExited; }
        catch { return false; }
    }

    NameGuardResult RegisterNameGuardTransientFailure(
        ProfileContext ctx,
        string username,
        string reason,
        string stage,
        Exception? exception = null)
    {
        reason = string.IsNullOrWhiteSpace(reason)
            ? "Lỗi kỹ thuật tạm thời khi kiểm tra Tên/ảnh."
            : reason.Trim();

        var retryUtc = DateTime.UtcNow.Add(NameGuardTransientRetryDelay);

        // Không đánh dấu handled vĩnh viễn: AutoOnReady được phép thử lại sau
        // khoảng nghỉ. Đồng thời KHÔNG ghi Excel FAIL và KHÔNG cleanup Chrome/Worker.
        _autoIdentityHandledSession.Remove(ctx.Profile.Name);
        _autoIdentityNextProbeUtc[ctx.Profile.Name] = retryUtc;

        var workerAlive = IsNameGuardWorkerAlive(ctx);
        _log.Warn(
            $"[NAME_GUARD_TRANSIENT_KEEP_OPEN] profile={ctx.Profile.Name} account={username} " +
            $"stage={stage} workerAlive={workerAlive} retryAt={retryUtc:O} reason={reason}" +
            (exception is null ? "" : $" exception={exception.GetType().Name}"));

        WriteAutoDiagnosticEvent(
            ctx,
            "name_guard_transient",
            "NAME_GUARD_TRANSIENT",
            "KEEP_OPEN_RETRY",
            $"account={username}; stage={stage}; workerAlive={workerAlive}; retryAt={retryUtc:O}; reason={reason}");

        return new NameGuardResult(false, reason, Transient: true);
    }

    string ChooseNameGuardTargetName(
        ProfileContext ctx,
        IReadOnlyList<string> names,
        bool randomNames)
    {
        if (names.Count == 1) return names[0];
        if (randomNames) return names[Random.Shared.Next(names.Count)];

        var ordered = _contexts.Values
            .OrderBy(x => x.Profile.Name, NaturalProfileNameOrder)
            .Select(x => x.Profile.Name)
            .ToList();
        var index = ordered.FindIndex(x => x.Equals(ctx.Profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        return names[index % names.Count];
    }

    (string AvatarPath, string Bio) ChooseNameGuardExtraIdentity(ProfileContext ctx, IdentityToolState state)
    {
        var avatarPath = "";
        if (state.UpdateAvatar && Directory.Exists(state.ImageFolder))
        {
            var images = Directory.EnumerateFiles(state.ImageFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(x => new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }
                    .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (images.Count > 0)
            {
                var candidates = images;
                if (state.AvoidLastAvatar
                    && images.Count > 1
                    && state.LastAvatarByProfile.TryGetValue(ctx.Profile.Name, out var previous)
                    && !string.IsNullOrWhiteSpace(previous))
                {
                    try
                    {
                        var previousFull = Path.GetFullPath(previous);
                        var filtered = images.Where(x =>
                        {
                            try { return !string.Equals(Path.GetFullPath(x), previousFull, StringComparison.OrdinalIgnoreCase); }
                            catch { return true; }
                        }).ToList();
                        if (filtered.Count > 0) candidates = filtered;
                    }
                    catch { }
                }
                avatarPath = candidates[Random.Shared.Next(candidates.Count)];
            }
        }

        var bio = state.UpdateBio ? (state.BioText ?? "").Trim() : "";
        return (avatarPath, bio);
    }

    async Task QueueNameGuardNameSyncPendingAndCloseAsync(
        ProfileContext ctx,
        string username,
        string detail)
    {
        _nameGuardVerifiedSessionAccount.Remove(ctx.Profile.Name);

        // Tên chưa được xác minh nên tuyệt đối không để Excel ở DONE. PROCESSING
        // phản ánh đúng trạng thái: TikTok đã nhận Save nhưng còn chờ đồng bộ.
        await TrySetNameGuardExcelStatusAsync(username, "PROCESSING", ctx.Profile.Name);

        try
        {
            var account = await RunAccountPoolIoAsync(
                () => _accountPoolService.Load().FirstOrDefault(x =>
                    x.Username.Equals(username, StringComparison.OrdinalIgnoreCase)),
                CancellationToken.None);

            if (account is not null)
            {
                QueueReusableProfileNameSyncPending(
                    new AutoProfileQueueItem(account, ctx.Profile.Name, ResumeExisting: true),
                    detail);
            }
            else
            {
                _log.Warn(
                    $"[NAME_GUARD_NAME_SYNC_QUEUE_ACCOUNT_MISSING] profile={ctx.Profile.Name} account={username}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[NAME_GUARD_NAME_SYNC_QUEUE_WARN] profile={ctx.Profile.Name} account={username} error={ex.Message}");
        }

        // Khóa AutoOnReady chen vào đúng lúc đang cleanup. Recovery sweep của
        // NAME_SYNC_PENDING sẽ chủ động mở lại profile khi tới lượt.
        _autoIdentityHandledSession.Add(ctx.Profile.Name);
        _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);

        _log.Warn(
            $"[NAME_GUARD_NAME_SYNC_PENDING_CLOSE] profile={ctx.Profile.Name} account={username} detail={detail}");
        WriteAutoDiagnosticEvent(
            ctx,
            "name_guard",
            "NAME_SYNC_PENDING",
            "CLOSE_REQUEST",
            $"account={username}; detail={detail}");

        try
        {
            await CloseChromeForProfileAsync(ctx);
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[NAME_GUARD_NAME_SYNC_CHROME_CLOSE_WARN] profile={ctx.Profile.Name} error={ex.Message}");
        }

        try
        {
            await EnsureAutoCloseWorkerStoppedAsync(ctx);

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            _log.Info(
                $"[NAME_GUARD_NAME_SYNC_PENDING_CLOSED] profile={ctx.Profile.Name} account={username}");
            WriteAutoDiagnosticEvent(
                ctx,
                "name_guard",
                "NAME_SYNC_PENDING",
                "CLOSED",
                $"account={username}; detail={detail}");
        }
        catch (Exception ex)
        {
            _log.Error(
                $"[NAME_GUARD_NAME_SYNC_CLOSE_ERROR] profile={ctx.Profile.Name} error={ex}");
        }
    }

    async Task FailNameGuardAndCloseAsync(ProfileContext ctx, string username, string reason)
    {
        _nameGuardVerifiedSessionAccount.Remove(ctx.Profile.Name);

        WriteAutoDiagnosticEvent(
            ctx,
            "name_guard",
            "NAME_GUARD_FAIL",
            "CLOSE_REQUEST",
            $"account={username}; reason={reason}");

        await TrySetNameGuardExcelStatusAsync(username, "FAIL", ctx.Profile.Name);

        // Một lần mở Chrome chỉ xử lý Name Guard một lượt. Đánh dấu handled trước khi
        // cleanup để AutoOnReady không chen vào trong lúc Chrome/Worker đang đóng.
        _autoIdentityHandledSession.Add(ctx.Profile.Name);
        _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);

        _log.Warn($"[NAME_GUARD_FAIL_CLOSE] profile={ctx.Profile.Name} account={username} reason={reason}");

        // 1) Đóng Chrome bằng đúng cơ chế hiện có của profile.
        try
        {
            await CloseChromeForProfileAsync(ctx);
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_FAIL_CHROME_CLOSE_WARN] profile={ctx.Profile.Name} error={ex.Message}");
        }

        // 2) Dùng lại cleanup Worker sẵn có của AutoClose:
        //    shutdown -> chờ 7s -> force-kill tree -> chờ 3s -> verify exited -> Dispose.
        //    Chỉ gỡ tab khi Worker đã được xác minh thoát thật.
        try
        {
            _log.Info($"[NAME_GUARD_FAIL_WORKER_CLOSE_REQUEST] profile={ctx.Profile.Name}");
            await EnsureAutoCloseWorkerStoppedAsync(ctx);

            if (ctx.Tab is not null && !ctx.Tab.IsDisposed && ctx.Tab.Parent == _tabs)
                RemoveTab(ctx);

            _log.Info($"[NAME_GUARD_FAIL_CLEANUP_DONE] profile={ctx.Profile.Name} chrome=closed worker=closed tab=removed");
            WriteAutoDiagnosticEvent(
                ctx,
                "name_guard",
                "NAME_GUARD_FAIL",
                "CLOSED",
                $"account={username}; reason={reason}");
        }
        catch (Exception ex)
        {
            // Không gỡ tab nếu Worker chưa được xác minh là đã chết. Như vậy UI vẫn
            // phản ánh đúng trạng thái và người dùng còn có thể xử lý thủ công.
            _log.Error($"[NAME_GUARD_FAIL_WORKER_CLOSE_ERROR] profile={ctx.Profile.Name} error={ex}");
            WriteAutoDiagnosticEvent(
                ctx,
                "name_guard",
                "NAME_GUARD_FAIL",
                "CLOSE_ERROR",
                $"account={username}; exception={ex.GetType().Name}; message={ex.Message}");
        }
    }

    async Task TrySetNameGuardExcelStatusAsync(
        string username,
        string status,
        string profileName)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        try
        {
            await RunAccountPoolIoAsync(
                () => _accountPoolService.MarkIdentityResult(username, status),
                CancellationToken.None);
            _log.Info($"[NAME_GUARD_EXCEL_STATUS] profile={profileName} account={username} status={status}");
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_EXCEL_STATUS_WARN] profile={profileName} account={username} status={status} {ex.Message}");
        }
    }
}
