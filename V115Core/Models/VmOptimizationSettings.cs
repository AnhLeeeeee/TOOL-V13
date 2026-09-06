namespace ToolTikTokV11.Models;

public enum VmOptimizationMode
{
    Normal = 0,
    VmSafe = 1,
    VmMax = 2
}

/// <summary>
/// V13.4: cấu hình tiết kiệm tài nguyên dành cho VM.
/// Các mode chỉ thay cách Chrome/UI/log sử dụng tài nguyên; không thay workflow,
/// XPath, viewer threshold, delay nghiệp vụ hay flow chuyển LIVE.
/// </summary>
public sealed class VmOptimizationSettings
{
    public VmOptimizationMode Mode { get; set; } = VmOptimizationMode.VmSafe;

    public bool Enabled => Mode != VmOptimizationMode.Normal;
    public bool PauseVideo => Mode != VmOptimizationMode.Normal;
    public bool SuppressDetailedPerfLogs => Mode != VmOptimizationMode.Normal;
    public bool DisableCssAnimations => Mode == VmOptimizationMode.VmMax;
    // V13.8.4 hotfix: vẫn chặn luồng video phổ biến để giảm CPU, nhưng KHÔNG cho
    // Chrome background throttle. Automation phụ thuộc timer/renderer của tab TikTok
    // tiếp tục hoạt động ngay cả khi cửa sổ nằm nền/minimize; nếu throttle thì Manager
    // có thể vẫn thấy RUNNING trong khi vòng automation bị đứng hoặc tăng rất chậm.
    public bool BlockCommonMedia => Mode != VmOptimizationMode.Normal;
    public bool AllowChromeBackgroundThrottling => false;

    public int WorkerUiRefreshMs => Mode switch
    {
        VmOptimizationMode.VmSafe => 2000,
        VmOptimizationMode.VmMax => 5000,
        _ => 1000
    };

    public int WorkerLogUiRefreshMs => Mode switch
    {
        VmOptimizationMode.VmSafe => 250,
        VmOptimizationMode.VmMax => 750,
        _ => 100
    };

    public int WorkerLogUiMaxChars => Mode switch
    {
        VmOptimizationMode.VmSafe => 80000,
        VmOptimizationMode.VmMax => 40000,
        _ => 200000
    };
}
