using System.Globalization;

namespace ToolTikTokV12.Services;

/// <summary>
/// Hàng rào LOGIN dùng chung cho toàn bộ Worker/PRF trên cùng máy.
///
/// Quy tắc:
/// - Tối đa 1 lần TikTok LOGIN_SUBMIT thực sự mỗi 120 giây.
/// - Mốc thời gian chỉ được ghi sau khi click nút Đăng nhập thành công.
/// - Dùng named Mutex + state trong LocalAppData để các Worker process khác nhau
///   vẫn nhìn thấy cùng một cooldown, kể cả Worker bị restart.
/// - Stop All tăng generation để mọi login flow đã bắt đầu trước Stop All tự hủy
///   ngay trước/đang chờ submit; flow mới sau Stop All lấy generation mới và hoạt động bình thường.
/// </summary>
public static class TikTokGlobalLoginSubmitGate
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    const string SubmitMutexName = @"Local\ToolTikTokV13_GlobalLoginSubmitGate_V1";
    const string StateDirectoryName = "ToolTikTokV13";
    const string LastSubmitFileName = "global_login_submit_utc_ticks_v1.txt";
    const string StopGenerationFileName = "global_login_stop_generation_v1.txt";

    static readonly object StopGenerationSync = new();

    static string StateDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = AppContext.BaseDirectory;
            return Path.Combine(local, StateDirectoryName);
        }
    }

    static string LastSubmitPath => Path.Combine(StateDirectory, LastSubmitFileName);
    static string StopGenerationPath => Path.Combine(StateDirectory, StopGenerationFileName);

    public static Mutex CreateSubmitMutex()
        => new(false, SubmitMutexName);

    public static DateTime ReadLastSubmitUtc()
    {
        try
        {
            var path = LastSubmitPath;
            if (!File.Exists(path))
                return DateTime.MinValue;

            var text = File.ReadAllText(path).Trim();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return DateTime.MinValue;
            if (ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return DateTime.MinValue;

            return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch
        {
            // State hỏng/không đọc được không được khóa login vĩnh viễn.
            return DateTime.MinValue;
        }
    }

    public static void RecordSuccessfulSubmitUtc(DateTime utcNow)
    {
        utcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        Directory.CreateDirectory(StateDirectory);

        var path = LastSubmitPath;
        var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(temp, utcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, path, overwrite: true);
    }

    public static TimeSpan GetRemainingCooldown(DateTime utcNow)
    {
        utcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        var last = ReadLastSubmitUtc();
        if (last == DateTime.MinValue)
            return TimeSpan.Zero;

        var remaining = Cooldown - (utcNow - last);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static long ReadStopGeneration()
    {
        try
        {
            var path = StopGenerationPath;
            if (!File.Exists(path))
                return 0;

            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static long MarkStopAll()
    {
        lock (StopGenerationSync)
        {
            Directory.CreateDirectory(StateDirectory);

            // Ticks đủ để mỗi Stop All có một generation mới. Nếu cùng tick hiếm gặp,
            // tăng thêm 1 so với state cũ để vẫn đảm bảo khác generation.
            var previous = ReadStopGeneration();
            var next = Math.Max(DateTime.UtcNow.Ticks, previous + 1);
            var path = StopGenerationPath;
            var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(temp, next.ToString(CultureInfo.InvariantCulture));
            File.Move(temp, path, overwrite: true);
            return next;
        }
    }
}
