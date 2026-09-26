namespace ToolTikTokV11.Models;

public sealed class AppSettings
{
    public string XPathPoint1 { get; set; } = "";
    public string XPathPoint2 { get; set; } = "";
    public string XPathPeriodicAction { get; set; } = "";
    public string XPathHoverArea { get; set; } = "";
    public bool SwitchNeedsHover { get; set; }
    public bool UseArrowDownForLiveSwitch { get; set; } = true;
    public int HoverDelayMs { get; set; } = 350;
    public int DelayMinMs { get; set; } = 700;
    public int DelayMaxMs { get; set; } = 1200;
    public int LoopMinMs { get; set; } = 700;
    public int LoopMaxMs { get; set; } = 1200;
    public int PeriodicF5Minutes { get; set; }
    public int TimerStopMinutes { get; set; }
    public int ChromePort { get; set; } = 9222;
    public string ChromeProfileDir { get; set; } = "";
    public bool StrictXPathOnly { get; set; } = true;
    public string ChromeMode { get; set; } = "visible"; // visible | background
    // V14.2.9: kích thước cửa sổ Chrome do Manager cấu hình toàn cục.
    // Full giữ nguyên hành vi cũ (--start-maximized). Percent thu theo % WorkingArea
    // và tự scale viewport TikTok để hạn chế responsive layout.
    public ChromeWindowSettings ChromeWindow { get; set; } = new();
    // V13 runtime dùng InputGuard thay cho image-scan vùng lỗi.
    public InputGuardSettings InputGuard { get; set; } = new();
    // V13.4: chế độ tiết kiệm tài nguyên cho máy ảo.
    public VmOptimizationSettings VmOptimization { get; set; } = new();
    public ViewerSettings Viewer { get; set; } = new();
    public StartupLiveSearchSettings StartupLiveSearch { get; set; } = new();
    public OldLiveSettings OldLive { get; set; } = new();
}

public sealed class ViewerSettings
{
    public bool Enabled { get; set; }
    public string XPath { get; set; } = "";
    public int Threshold { get; set; } = 100;
    public int ConfirmLow { get; set; } = 2;
    public int WaitAfterF5Sec { get; set; } = 2;
    public int MaxF5 { get; set; } = 100;
}

public sealed class StartupLiveSearchSettings
{
    public bool Enabled { get; set; } = true;
    public string Keywords { get; set; } = "live; liên quân";
    public int KeywordTimeoutSec { get; set; } = 40;
    public int TotalTimeoutSec { get; set; } = 90;
    public int MaxCards { get; set; } = 12;
}

public sealed class OldLiveSettings
{
    public bool Enabled { get; set; }
    // V13.4: XPath của tên/username tài khoản LIVE dùng để đọc định danh trực tiếp từ DOM.
    public string IdentityXPath { get; set; } = "";
    public string ActionXPath { get; set; } = "";
    public int KeepMinutes { get; set; } = 10;
}

public sealed class ChromeWindowSettings
{
    // Full | Percent
    public string Mode { get; set; } = "Full";
    public int Percent { get; set; } = 70;
    // BottomLeft | BottomRight | TopLeft | TopRight
    public string Position { get; set; } = "BottomLeft";
}
