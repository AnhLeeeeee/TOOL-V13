namespace ToolTikTokV11;

public sealed class StartupOptions
{
    public string ProfileName { get; init; } = "";
    public string ProfilePath { get; init; } = "";
    public int CdpPort { get; init; } = 9222;
    public string DataRoot { get; init; } = "";
    public string PipeName { get; init; } = "";
    public bool Worker { get; init; }
    public bool Embedded { get; init; }

    public bool ManagedMode => Worker || !string.IsNullOrWhiteSpace(ProfileName) || !string.IsNullOrWhiteSpace(ProfilePath);

    public static StartupOptions Parse(string[] args)
    {
        string profile = "", profilePath = "", dataRoot = "", pipeName = "";
        int port = 9222;
        bool worker = false, embedded = false;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = token[2..].ToLowerInvariant();
            if (key == "worker") { worker = true; continue; }
            if (key == "embedded") { embedded = true; continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) continue;
            var value = args[++i];
            switch (key)
            {
                case "profile": profile = value; break;
                case "profile-path": profilePath = value; break;
                case "cdp-port": if (int.TryParse(value, out var parsed) && parsed > 0) port = parsed; break;
                case "data-root": dataRoot = value; break;
                case "pipe-name": pipeName = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName) && !string.IsNullOrWhiteSpace(profile))
            pipeName = "ToolTikTokV13_" + profile;
        return new StartupOptions
        {
            ProfileName = profile.Trim(),
            ProfilePath = profilePath.Trim(),
            CdpPort = port,
            DataRoot = dataRoot.Trim(),
            PipeName = pipeName.Trim(),
            Worker = worker,
            Embedded = embedded
        };
    }
}
