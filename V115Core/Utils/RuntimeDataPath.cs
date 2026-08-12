namespace ToolTikTokV11.Utils;

public static class RuntimeDataPath
{
    const string FixedChromeProfilePath = @"D:\TOOL V2\Tool_TikTok_V11_XPath_CSharp\dist\chrome_v11_profile";

    public static string Resolve(string appBaseDirectory)
    {
        var appBase = Path.GetFullPath(appBaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var fixedRoot = ResolveFixedRoot(appBase);
        if (fixedRoot is not null) return fixedRoot;

        return appBase;
    }

    public static string ResolveChromeProfilePath(string appBaseDirectory)
    {
        _ = appBaseDirectory;
        return FixedChromeProfilePath;
    }

    static string? ResolveFixedRoot(string appBase)
    {
        if (IsBuildOutput(appBase))
        {
            var projectRoot = FindProjectRoot(appBase);
            if (projectRoot is not null)
            {
                var dist = Path.Combine(projectRoot, "dist");
                if (Directory.Exists(dist)) return dist;
                return projectRoot;
            }
        }

        var localDist = Path.Combine(appBase, "dist");
        if (Directory.Exists(localDist)) return localDist;
        if (Directory.Exists(Path.Combine(appBase, "chrome_v11_profile"))) return appBase;
        return null;
    }

    static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    static string? FindProjectRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.GetFiles("*.csproj").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
