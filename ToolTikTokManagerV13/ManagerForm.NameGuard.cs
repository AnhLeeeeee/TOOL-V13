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
    }

    sealed record NameGuardResult(bool Allowed, string Message, bool ChangedName = false);

    async Task<string> StartWithNameGuardAsync(
        ProfileContext ctx,
        string command,
        TimeSpan timeout,
        bool suppressStatus = false)
    {
        var guard = await EnsureNameGuardBeforeStartAsync(ctx);
        if (!guard.Allowed)
        {
            if (!suppressStatus)
                SetStatus(ctx, "Không Start: " + guard.Message, Color.Firebrick);
            _log.Warn($"[NAME_GUARD_START_BLOCKED] profile={ctx.Profile.Name} command={command} reason={guard.Message}");
            return "name_guard_blocked";
        }

        var reply = await SendCommandAsync(ctx, command, timeout);
        _log.Info($"[NAME_GUARD_START_ALLOWED] profile={ctx.Profile.Name} command={command} changed={guard.ChangedName} reply={reply}");
        return reply;
    }

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
            return new NameGuardResult(false, "Tên của profile đang được luồng Tên/ảnh khác xử lý.");

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
                await FailNameGuardAndCloseAsync(ctx, username, "Chrome chưa kết nối.");
                return new NameGuardResult(false, "Chrome chưa kết nối.");
            }

            return await ProcessNameGuardOnceAsync(ctx, username, state, names);
        }
        catch (Exception ex)
        {
            await FailNameGuardAndCloseAsync(ctx, username, ex.Message);
            _log.Warn($"[NAME_GUARD_ERROR] profile={ctx.Profile.Name} account={username} {ex.Message}");
            return new NameGuardResult(false, ex.Message);
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
            await FailNameGuardAndCloseAsync(ctx, username, reason);
            return new NameGuardResult(false, reason);
        }

        // 2) Tên trùng BẤT KỲ tên mẫu => coi là đúng, ghi DONE rồi cho chạy.
        if (probe.Matched)
        {
            var done = await MarkIdentityDoneVerifiedAsync(username, ctx.Profile.Name, CancellationToken.None);
            if (!done.Ok)
            {
                var reason = "Tên đúng nhưng không ghi được Tên/ảnh=DONE: " + done.Error;
                await FailNameGuardAndCloseAsync(ctx, username, reason);
                return new NameGuardResult(false, reason);
            }

            _autoIdentityHandledSession.Add(ctx.Profile.Name);
            _autoIdentityHandledSession.Add("account:" + username.ToLowerInvariant());
            _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);
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

        // Fast mode: Save/Confirm thành công là DONE; KHÔNG reload/verify lại tên.
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

        var excelDone = await MarkIdentityDoneVerifiedAsync(username, ctx.Profile.Name, CancellationToken.None);
        if (!excelDone.Ok)
        {
            var reason = "Đổi Tên/ảnh thành công nhưng không ghi được Tên/ảnh=DONE: " + excelDone.Error;
            await FailNameGuardAndCloseAsync(ctx, username, reason);
            return new NameGuardResult(false, reason);
        }

        if (reply.AvatarChanged && !string.IsNullOrWhiteSpace(avatarPath))
        {
            state.LastAvatarByProfile[ctx.Profile.Name] = avatarPath;
            SaveIdentityToolState(state);
        }

        _autoIdentityHandledSession.Add(ctx.Profile.Name);
        _autoIdentityHandledSession.Add("account:" + username.ToLowerInvariant());
        _autoIdentityNextProbeUtc.Remove(ctx.Profile.Name);
        _log.Info($"[NAME_GUARD_UPDATE_DONE_NO_RECHECK] profile={ctx.Profile.Name} account={username} target={targetName} nameChanged={reply.NameChanged} avatarChanged={reply.AvatarChanged}");
        return new NameGuardResult(true, "Đổi Tên/ảnh thành công.", ChangedName: reply.NameChanged);
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
                TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize<NameGuardProbeReply>(
                       raw,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new NameGuardProbeReply { Ok = false, Message = "Worker trả kết quả Name Guard không hợp lệ." };
        }
        catch (Exception ex)
        {
            _log.Warn($"[NAME_GUARD_PROFILE_PROBE_FAILED] profile={ctx.Profile.Name} account={username} {ex.Message}");
            return new NameGuardProbeReply { Ok = false, Message = ex.Message };
        }
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

    async Task FailNameGuardAndCloseAsync(ProfileContext ctx, string username, string reason)
    {
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
        }
        catch (Exception ex)
        {
            // Không gỡ tab nếu Worker chưa được xác minh là đã chết. Như vậy UI vẫn
            // phản ánh đúng trạng thái và người dùng còn có thể xử lý thủ công.
            _log.Error($"[NAME_GUARD_FAIL_WORKER_CLOSE_ERROR] profile={ctx.Profile.Name} error={ex}");
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
