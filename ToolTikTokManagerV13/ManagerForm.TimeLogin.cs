using System.Globalization;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    static string FormatTimeLoginForDisplay(string? rawValue)
    {
        var value = (rawValue ?? "").Trim();
        if (value.Length == 0)
            return "";

        // Excel vẫn lưu nguyên HH:mm dd/MM/yyyy để các logic sau này còn đủ dữ liệu.
        // UI chỉ bỏ phần năm, nhưng GIỮ GIỜ: HH:mm dd/MM. Hỗ trợ cả vài format
        // cũ để không làm hỏng dữ liệu đã có từ các phiên bản trước.
        var formatsWithTime = new[]
        {
            "HH:mm dd/MM/yyyy",
            "H:mm dd/MM/yyyy",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy H:mm",
            "HH:mm dd/MM",
            "H:mm dd/MM",
            "dd/MM HH:mm",
            "dd/MM H:mm"
        };

        if (DateTime.TryParseExact(
                value,
                formatsWithTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return parsed.ToString("HH:mm dd/MM", CultureInfo.InvariantCulture);
        }

        // Chỉ dùng parse linh hoạt khi chuỗi thật sự có thành phần giờ.
        // Tránh biến dữ liệu cũ chỉ có dd/MM thành giờ giả 00:00.
        if (value.Contains(':')
            && DateTime.TryParse(
                value,
                CultureInfo.GetCultureInfo("vi-VN"),
                DateTimeStyles.AllowWhiteSpaces,
                out parsed))
        {
            return parsed.ToString("HH:mm dd/MM", CultureInfo.InvariantCulture);
        }

        // Dữ liệu cũ không có giờ hoặc giá trị ghi tay: giữ nguyên thay vì tự bịa giờ.
        return value;
    }

    sealed record RuntimeTimeLoginWriteResult(
        bool Found,
        bool AlreadyExisted,
        string Username,
        string Value,
        string Resolution,
        string Detail);

    /// <summary>
    /// Ghi TIMELOGIN sau khi runtime relogin đã xác nhận session TikTok mở thành công.
    ///
    /// Nguyên tắc an toàn:
    /// - Ưu tiên username thực tế trong tiktok_auth.json của chính PRF vì đây là
    ///   credential mà runtime_relogin_auto vừa dùng để đăng nhập.
    /// - Chỉ ghi khi username đó khớp DUY NHẤT một account trong Kho tài khoản.
    /// - Nếu không đọc được auth username, chỉ fallback khi đúng một account đang
    ///   AssignedProfile vào profile này.
    /// - EnsureTimeLogin giữ nguyên giá trị cũ; không ghi đè lần login đầu tiên.
    /// - Mọi lỗi ở đây đều fail-open: chỉ log WARN, không được làm hỏng recovery,
    ///   không dừng Worker/Chrome/Auto Run.
    /// </summary>
    async Task TryEnsureRuntimeTimeLoginAsync(ProfileContext ctx)
    {
        var profileName = (ctx.Profile.Name ?? "").Trim();

        _log.Info(
            $"[TIMELOGIN_RUNTIME_BEGIN] profile={profileName}");

        try
        {
            var result = await RunAccountPoolIoAsync(
                () =>
                {
                    string authUsername = "";

                    try
                    {
                        var dataRoot = _profileService.ResolveDataRoot(ctx.Profile);
                        authUsername = (_tiktokAuthService.Load(dataRoot).Username ?? "").Trim();
                    }
                    catch (Exception ex)
                    {
                        // Không throw ở bước đọc auth: vẫn còn fallback AssignedProfile.
                        _log.Warn(
                            $"[TIMELOGIN_RUNTIME_AUTH_WARN] profile={profileName} error={ex.Message}");
                    }

                    var accounts = _accountPoolService.Load();

                    TikTokAccountPoolItem? account = null;
                    var resolution = "";

                    if (authUsername.Length > 0)
                    {
                        var usernameMatches = accounts
                            .Where(x => string.Equals(
                                (x.Username ?? "").Trim(),
                                authUsername,
                                StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (usernameMatches.Count == 1)
                        {
                            account = usernameMatches[0];
                            resolution = "AUTH_USERNAME";
                        }
                        else if (usernameMatches.Count > 1)
                        {
                            return new RuntimeTimeLoginWriteResult(
                                Found: false,
                                AlreadyExisted: false,
                                Username: authUsername,
                                Value: "",
                                Resolution: "AUTH_USERNAME_AMBIGUOUS",
                                Detail: $"Có {usernameMatches.Count} dòng cùng username trong Kho tài khoản; không ghi để tránh nhầm dòng.");
                        }
                    }

                    if (account is null)
                    {
                        var assignedMatches = accounts
                            .Where(x => string.Equals(
                                (x.AssignedProfile ?? "").Trim(),
                                profileName,
                                StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (assignedMatches.Count == 1)
                        {
                            account = assignedMatches[0];
                            resolution = authUsername.Length > 0
                                ? "ASSIGNED_PROFILE_FALLBACK_AFTER_AUTH_NOT_FOUND"
                                : "ASSIGNED_PROFILE_FALLBACK";
                        }
                        else
                        {
                            var detail = assignedMatches.Count > 1
                                ? $"Có {assignedMatches.Count} account cùng gán profile={profileName}; không ghi để tránh nhầm."
                                : authUsername.Length > 0
                                    ? $"Username thực tế {authUsername} không có trong Kho tài khoản và profile không có đúng một account được gán."
                                    : $"Không đọc được username thực tế và không có đúng một account gán profile={profileName}.";

                            return new RuntimeTimeLoginWriteResult(
                                Found: false,
                                AlreadyExisted: false,
                                Username: authUsername,
                                Value: "",
                                Resolution: "NO_SAFE_ACCOUNT_MATCH",
                                Detail: detail);
                        }
                    }

                    var username = (account.Username ?? "").Trim();
                    if (username.Length == 0)
                    {
                        return new RuntimeTimeLoginWriteResult(
                            Found: false,
                            AlreadyExisted: false,
                            Username: "",
                            Value: "",
                            Resolution: "EMPTY_USERNAME",
                            Detail: "Account khớp profile nhưng username trống.");
                    }

                    // Nếu fallback theo AssignedProfile trong khi auth username có giá trị
                    // khác, không ghi. Credential vừa dùng để login là nguồn đáng tin cậy
                    // hơn assignment cũ; tránh ghi TIMELOGIN nhầm sang account khác.
                    if (authUsername.Length > 0
                        && !string.Equals(
                            username,
                            authUsername,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new RuntimeTimeLoginWriteResult(
                            Found: false,
                            AlreadyExisted: false,
                            Username: username,
                            Value: "",
                            Resolution: "AUTH_ASSIGNMENT_MISMATCH",
                            Detail: $"Auth username={authUsername} nhưng account gán profile={profileName} là {username}; không ghi để tránh nhầm.");
                    }

                    var existing = _accountPoolService.GetTimeLoginResults();
                    var alreadyExisted =
                        existing.TryGetValue(username, out var oldValue)
                        && !string.IsNullOrWhiteSpace(oldValue);

                    var value = _accountPoolService.EnsureTimeLogin(
                        username,
                        DateTime.Now);

                    return new RuntimeTimeLoginWriteResult(
                        Found: true,
                        AlreadyExisted: alreadyExisted,
                        Username: username,
                        Value: value,
                        Resolution: resolution,
                        Detail: "");
                },
                CancellationToken.None);

            if (!result.Found)
            {
                _log.Warn(
                    $"[TIMELOGIN_RUNTIME_SKIP_NO_ACCOUNT] profile={profileName} username={result.Username} resolution={result.Resolution} detail={result.Detail}");
                return;
            }

            if (result.AlreadyExisted)
            {
                _log.Info(
                    $"[TIMELOGIN_RUNTIME_EXISTING] profile={profileName} account={result.Username} value={result.Value} resolution={result.Resolution}");
                return;
            }

            _log.Info(
                $"[TIMELOGIN_RUNTIME_OK] profile={profileName} account={result.Username} value={result.Value} resolution={result.Resolution}");
        }
        catch (Exception ex)
        {
            // TIMELOGIN chỉ là telemetry theo dõi. Không được phép biến lỗi Excel,
            // lỗi catalog hay lỗi IO thành lỗi đăng nhập/recovery.
            _log.Warn(
                $"[TIMELOGIN_RUNTIME_WARN] profile={profileName} error={ex.Message}");
        }
    }
}
