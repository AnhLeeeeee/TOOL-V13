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
    public bool BlockCommonMedia => Mode == VmOptimizationMode.VmMax;
    public bool AllowChromeBackgroundThrottling => Mode == VmOptimizationMode.VmMax;

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
