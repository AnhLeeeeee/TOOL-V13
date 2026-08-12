namespace ToolTikTokManagerV13;

/// <summary>
/// V13 ưu tiên kế thừa catalog/config từ V12.5 khi chạy cạnh một bản cũ.
/// Không rewrite ProfilePath/DataRoot/CDP port: storage identity đã ổn định từ V12.5
/// và phải được giữ nguyên để tránh sinh profile trắng/race rename.
/// </summary>
static class LegacyDataMigration
{
    static readonly string[] LegacyDistNames = ["dist_v125", "dist_v12"];

    public static void TryImportLegacyCatalog(string baseDir)
    {
        var target = Path.Combine(baseDir, "profiles.json");
        if (File.Exists(target)) return;
        var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return;

        foreach (var dist in LegacyDistNames)
        {
            var source = Path.Combine(parent, dist, "profiles.json");
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(baseDir);
            File.Copy(source, target, false);
            return;
        }
    }

    public static void TryImportLegacyProfileData(string baseDir, string profileName, string dataRoot)
    {
        var target = Path.GetFullPath(dataRoot);
        var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return;

        foreach (var dist in LegacyDistNames)
        {
            var source = Path.Combine(parent, dist, "profiles", profileName);
            if (!Directory.Exists(source)) continue;
            if (Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase)) return;
            CopyMissing(source, target);
            return;
        }
    }

    static void CopyMissing(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest)) File.Copy(file, dest, false);
        }
    }
}
