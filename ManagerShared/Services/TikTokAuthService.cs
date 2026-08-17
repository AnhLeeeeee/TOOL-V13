using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV12.Services;

public sealed class TikTokAuthSettings
{
    public string Username { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string TotpSecretProtected { get; set; } = "";
    public bool AutoLogin { get; set; } = true;
}

public sealed record TikTokAuthMaterial(string Username, string Password, string TotpSecret, bool AutoLogin)
{
    public bool HasPasswordLogin => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
    public bool HasTotp => !string.IsNullOrWhiteSpace(TotpSecret);
}

public sealed class TikTokAuthService
{
    const string FileName = "tiktok_auth.json";
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ToolTikTok-V13.5-TikTokAuth-v1");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetPath(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), FileName);

    public void Save(string dataRoot, string username, string password, string totpSecret, bool autoLogin = true)
    {
        Directory.CreateDirectory(dataRoot);
        username = (username ?? "").Trim();
        totpSecret = NormalizeTotpSecret(totpSecret);

        var settings = new TikTokAuthSettings
        {
            Username = username,
            PasswordProtected = Protect(password ?? ""),
            TotpSecretProtected = Protect(totpSecret),
            AutoLogin = autoLogin
        };
        AtomicWrite(GetPath(dataRoot), JsonSerializer.Serialize(settings, JsonOptions));
    }

    public TikTokAuthMaterial Load(string dataRoot)
    {
        var path = GetPath(dataRoot);
        if (!File.Exists(path)) return new TikTokAuthMaterial("", "", "", true);
        try
        {
            var settings = JsonSerializer.Deserialize<TikTokAuthSettings>(File.ReadAllText(path)) ?? new TikTokAuthSettings();
            return new TikTokAuthMaterial(
                (settings.Username ?? "").Trim(),
                Unprotect(settings.PasswordProtected),
                NormalizeTotpSecret(Unprotect(settings.TotpSecretProtected)),
                settings.AutoLogin);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Không đọc được thông tin đăng nhập TikTok đã mã hóa của profile này.", ex);
        }
    }

    public void Delete(string dataRoot)
    {
        var path = GetPath(dataRoot);
        if (File.Exists(path)) File.Delete(path);
    }

    public string GenerateTotp(string secret, DateTimeOffset? now = null, int digits = 6, int periodSeconds = 30)
    {
        var normalized = NormalizeTotpSecret(secret);
        if (normalized.Length == 0) throw new InvalidOperationException("Secret 2FA/TOTP đang trống.");
        var key = DecodeBase32(normalized);
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var counter = timestamp / Math.Max(1, periodSeconds);
        Span<byte> counterBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xff);
            counter >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);
        var mod = (int)Math.Pow(10, Math.Clamp(digits, 1, 9));
        return (binary % mod).ToString(new string('0', Math.Clamp(digits, 1, 9)));
    }

    public int GetTotpSecondsRemaining(DateTimeOffset? now = null, int periodSeconds = 30)
    {
        var unix = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var remainder = (int)(unix % periodSeconds);
        return remainder == 0 ? periodSeconds : periodSeconds - remainder;
    }

    static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var raw = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var raw = Convert.FromBase64String(value);
        var plain = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static string NormalizeTotpSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim();
        var secretMarker = "secret=";
        var markerIndex = s.IndexOf(secretMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            s = s[(markerIndex + secretMarker.Length)..];
            var amp = s.IndexOf('&');
            if (amp >= 0) s = s[..amp];
            s = Uri.UnescapeDataString(s);
        }
        return new string(s.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToUpperInvariant();
    }

    static byte[] DecodeBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Secret 2FA không đúng định dạng Base32.");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }
        if (output.Count == 0) throw new FormatException("Secret 2FA không hợp lệ.");
        return output.ToArray();
    }

    static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
