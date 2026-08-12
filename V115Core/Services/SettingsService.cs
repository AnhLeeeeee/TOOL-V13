using System.IO.Compression;
using ToolTikTokV11.Models;
using ToolTikTokV11.Utils;
using System.Text;

namespace ToolTikTokV11.Services;

public sealed class SettingsService
{
    readonly string _baseDir;
    public string BaseDir => _baseDir;
    public string IniPath => Path.Combine(_baseDir, "auto_chrome.ini");
    public string ContentPath => Path.Combine(_baseDir, "auto_chrome_noidung.txt");

    public SettingsService(string baseDir) => _baseDir = baseDir;

    public AppSettings Load()
    {
        var ini = new IniFile(IniPath);
        var fixedProfileDir = RuntimeDataPath.ResolveChromeProfilePath(_baseDir);
        var s = new AppSettings
        {
            XPathPoint1 = ini.Get("XPath", "Point1"),
            XPathPoint2 = ini.Get("XPath", "Point2"),
            XPathPeriodicAction = ini.Get("XPath", "PeriodicAction"),
            XPathHoverArea = ini.Get("XPath", "HoverArea"),
            SwitchNeedsHover = ini.GetBool("XPath", "SwitchNeedsHover", false),
            UseArrowDownForLiveSwitch = ini.GetBool("V11", "UseArrowDownForLiveSwitch", true),
            HoverDelayMs = Math.Clamp(ini.GetInt("XPath", "HoverDelayMs", 350), 0, 3000),
            DelayMinMs = ini.GetInt("ThoiGian", "DelayMin", 700),
            DelayMaxMs = ini.GetInt("ThoiGian", "DelayMax", 1200),
            LoopMinMs = ini.GetInt("ThoiGian", "LoopMin", 700),
            LoopMaxMs = ini.GetInt("ThoiGian", "LoopMax", 1200),
            PeriodicF5Minutes = ini.GetInt("F5DinhKy", "Phut", 0),
            TimerStopMinutes = ini.GetInt("HenGio", "Phut", 0),
            ChromePort = ini.GetInt("V11", "ChromePort", 9222),
            ChromeProfileDir = fixedProfileDir,
            StrictXPathOnly = ini.GetBool("V11", "StrictXPathOnly", true),
            ChromeMode = ini.Get("V11", "ChromeMode", "visible")
        };

        // Runtime luôn dùng một profile cố định đã có từ trước; không phụ thuộc AppContext/BaseDirectory
        // và không đọc profile runtime từ auto_chrome.ini để tránh dotnet run tạo profile riêng.
        s.ChromeProfileDir = fixedProfileDir;

        s.InputGuard = new InputGuardSettings
        {
            Enabled = ini.GetBool("InputGuard", "Enabled", true),
            NormalPlaceholderText = ini.Get("InputGuard", "NormalPlaceholderText", "Nhập"),
            ConfirmReads = Math.Clamp(ini.GetInt("InputGuard", "ConfirmReads", 2), 1, 5),
            ConfirmDelayMs = Math.Clamp(ini.GetInt("InputGuard", "ConfirmDelayMs", 150), 0, 1000),
            ConsecutiveMax = Math.Clamp(ini.GetInt("InputGuard", "ConsecutiveMax", 3), 1, 4)
        };

        s.VmOptimization = new VmOptimizationSettings
        {
            Mode = ParseVmOptimizationMode(ini.Get("VM", "Mode", "VmSafe"))
        };

        s.Viewer = new ViewerSettings
        {
            Enabled = ini.GetBool("NguoiXem", "Enabled"),
            XPath = ini.Get("NguoiXem", "XPath"),
            Threshold = ini.GetInt("NguoiXem", "Threshold", 100),
            ConfirmLow = ini.GetInt("NguoiXem", "ConfirmLow", 2),
            IntervalSec = ini.GetInt("NguoiXem", "IntervalSec", 120),
            WaitAfterF5Sec = ini.GetInt("NguoiXem", "WaitAfterF5Sec", 2),
            MaxF5 = ini.GetInt("NguoiXem", "MaxF5", 100)
        };

        s.OldLive = new OldLiveSettings
        {
            Enabled = ini.GetBool("LiveCu", "Enabled"),
            IdentityXPath = ini.Get("LiveCu", "IdentityXPath"),
            ActionXPath = ini.Get("LiveCu", "ActionXPath", s.XPathPeriodicAction),
            KeepMinutes = ini.GetInt("LiveCu", "KeepMin", 10)
        };

        return s;
    }

    public void Save(AppSettings s)
    {
        var ini = new IniFile(IniPath);
        ini.Set("XPath", "Point1", s.XPathPoint1); ini.Set("XPath", "Point2", s.XPathPoint2); ini.Set("XPath", "PeriodicAction", s.XPathPeriodicAction);
        ini.Set("XPath", "HoverArea", s.XPathHoverArea); ini.Set("XPath", "SwitchNeedsHover", s.SwitchNeedsHover ? 1 : 0); ini.Set("XPath", "HoverDelayMs", s.HoverDelayMs);
        ini.Set("ThoiGian", "DelayMin", s.DelayMinMs); ini.Set("ThoiGian", "DelayMax", s.DelayMaxMs);
        ini.Set("ThoiGian", "LoopMin", s.LoopMinMs); ini.Set("ThoiGian", "LoopMax", s.LoopMaxMs);
        ini.Set("F5DinhKy", "Phut", s.PeriodicF5Minutes); ini.Set("HenGio", "Phut", s.TimerStopMinutes);
        ini.Set("V11", "ChromePort", s.ChromePort);
        ini.Set("V11", "StrictXPathOnly", s.StrictXPathOnly ? 1 : 0); ini.Set("V11", "ChromeMode", s.ChromeMode); ini.Set("V11", "UseArrowDownForLiveSwitch", s.UseArrowDownForLiveSwitch ? 1 : 0);
        ini.Set("InputGuard", "Enabled", s.InputGuard.Enabled ? 1 : 0);
        ini.Set("InputGuard", "NormalPlaceholderText", s.InputGuard.NormalPlaceholderText);
        ini.Set("InputGuard", "ConfirmReads", Math.Clamp(s.InputGuard.ConfirmReads, 1, 5));
        ini.Set("InputGuard", "ConfirmDelayMs", Math.Clamp(s.InputGuard.ConfirmDelayMs, 0, 1000));
        ini.Set("InputGuard", "ConsecutiveMax", Math.Clamp(s.InputGuard.ConsecutiveMax, 1, 4));
        ini.Set("VM", "Mode", s.VmOptimization.Mode.ToString());
        ini.Set("NguoiXem", "Enabled", s.Viewer.Enabled ? 1 : 0); ini.Set("NguoiXem", "XPath", s.Viewer.XPath);
        ini.Set("NguoiXem", "Threshold", s.Viewer.Threshold); ini.Set("NguoiXem", "ConfirmLow", s.Viewer.ConfirmLow);
        ini.Set("NguoiXem", "IntervalSec", s.Viewer.IntervalSec); ini.Set("NguoiXem", "WaitAfterF5Sec", s.Viewer.WaitAfterF5Sec);
        ini.Set("NguoiXem", "MaxF5", s.Viewer.MaxF5);
        // V13.4.1 XPath-only: xóa hoàn toàn cấu hình OCR/toạ độ viewer legacy.
        foreach (var key in new[] { "OcrRetries", "RX1", "RY1", "RX2", "RY2", "X1", "Y1", "X2", "Y2" }) ini.Remove("NguoiXem", key);
        ini.Set("LiveCu", "Enabled", s.OldLive.Enabled ? 1 : 0); ini.Set("LiveCu", "IdentityXPath", s.OldLive.IdentityXPath);
        ini.Set("LiveCu", "ActionXPath", s.OldLive.ActionXPath); ini.Set("LiveCu", "KeepMin", s.OldLive.KeepMinutes);
        // V13.4.1: dọn các section/key image-scan V12.5 không còn được runtime sử dụng.
        ini.RemoveSection("VungQuet");
        ini.RemoveSectionsStartingWith("VungQuet_");
        foreach (var section in new[] { "ToaDo", "CuaSo", "HoSo", "ThongBaoTrangThai" }) ini.RemoveSection(section);
        foreach (var key in new[] { "AfterClickScanEnabled", "AfterClickScanMs", "AfterEnterScanEnabled", "AfterEnterScanMs" }) ini.Remove("ThoiGian", key);
        foreach (var key in new[] { "XPath", "Variation", "RX1", "RY1", "RX2", "RY2", "X1", "Y1", "X2", "Y2" }) ini.Remove("LiveCu", key);
        ini.Save();
    }

    static VmOptimizationMode ParseVmOptimizationMode(string value)
    {
        if (Enum.TryParse<VmOptimizationMode>(value, ignoreCase: true, out var parsed)) return parsed;
        return value.Trim().ToLowerInvariant() switch
        {
            "safe" or "vmsafe" or "vm_safe" => VmOptimizationMode.VmSafe,
            "max" or "vmmax" or "vm_max" => VmOptimizationMode.VmMax,
            _ => VmOptimizationMode.Normal
        };
    }

    public List<string> LoadContents()
    {
        if (!File.Exists(ContentPath)) return [];
        var lines = File.ReadAllLines(ContentPath, Encoding.UTF8);
        return ContentLineHelper.GetDisplayLinesFromRawLines(lines);
    }

    public void SaveContents(string text)
    {
        var lines = ContentLineHelper.GetValidLinesForSave(text);
        File.WriteAllLines(ContentPath, lines, new UTF8Encoding(false));
    }

    public void ExportPackage(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddFile(zip, IniPath, "auto_chrome.ini");
        AddFile(zip, ContentPath, "auto_chrome_noidung.txt");

        // V13.4.1 không còn runtime image-scan vùng lỗi/Live cũ, vì vậy gói cấu hình
        // chỉ chứa dữ liệu còn được dùng. Không mang theo ảnh legacy để tránh ZIP/profile phình trên VM.
        var manifest = zip.CreateEntry("CONFIG_V13_INFO.txt", CompressionLevel.Fastest);
        using var w = new StreamWriter(manifest.Open(), new System.Text.UTF8Encoding(true));
        w.WriteLine("Tool TikTok V13.4.1 XPath-only configuration package");
        w.WriteLine("Created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        w.WriteLine("Includes=auto_chrome.ini, content text");
        w.WriteLine("Legacy image templates and Chrome profile/cookies are intentionally NOT exported.");
    }

    public string ImportPackage(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Không tìm thấy file cấu hình ZIP.", zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry("auto_chrome.ini") is null) throw new InvalidDataException("ZIP không có auto_chrome.ini nên không phải gói cấu hình V13 hợp lệ.");

        var backupDir = Path.Combine(_baseDir, "config_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);
        if (File.Exists(IniPath)) File.Copy(IniPath, Path.Combine(backupDir, "auto_chrome.ini"), true);
        if (File.Exists(ContentPath)) File.Copy(ContentPath, Path.Combine(backupDir, "auto_chrome_noidung.txt"), true);

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var name = entry.FullName.Replace('\\', '/');
            bool allowed = name.Equals("auto_chrome.ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("auto_chrome_noidung.txt", StringComparison.OrdinalIgnoreCase);
            if (!allowed) continue;

            var dest = Path.GetFullPath(Path.Combine(_baseDir, name.Replace('/', Path.DirectorySeparatorChar)));
            var baseFull = Path.GetFullPath(_baseDir) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) && !dest.Equals(Path.GetFullPath(_baseDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP chứa đường dẫn không an toàn: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
        CleanupConfigBackups(Path.Combine(_baseDir, "config_backups"), keepNewest: 3);
        return backupDir;
    }

    static void CleanupConfigBackups(string root, int keepNewest)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var dirs = new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(Math.Max(1, keepNewest))
                .ToList();
            foreach (var dir in dirs)
            {
                try { dir.Delete(recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void AddFile(ZipArchive zip, string file, string entryName)
    {
        if (!File.Exists(file)) return;
        zip.CreateEntryFromFile(file, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
    }
}
