namespace ToolTikTokV11.Models;

public sealed class TikTokProfileCatalog
{
    public string SelectedProfile { get; set; } = "";
    public List<TikTokProfileEntry> Profiles { get; set; } = [];
}

public sealed class TikTokProfileEntry
{
    public string Name { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public bool Managed { get; set; } = true;
}
