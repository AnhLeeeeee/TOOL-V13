namespace ToolTikTokV12.Models;

public sealed class TikTokProfileCatalog
{
    public string SelectedProfile { get; set; } = "";
    public List<TikTokProfileEntry> Profiles { get; set; } = [];
}

public sealed class TikTokProfileEntry
{
    public string Name { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    /// <summary>Worker runtime/config/image directory owned by the V12.5 Manager.</summary>
    public string DataRoot { get; set; } = "";
    public int CdpPort { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Managed { get; set; } = true;
}
