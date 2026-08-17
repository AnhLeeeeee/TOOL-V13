using System.Reflection;

namespace ToolTikTokV12.Utils;

/// <summary>
/// Version runtime dùng chung cho Manager và Worker.
/// Giá trị được đóng vào assembly từ VERSION.txt qua Directory.Build.props.
/// </summary>
public static class AppVersionInfo
{
    static readonly Lazy<string> CurrentValue = new(ResolveCurrentVersion);

    public static string Current => CurrentValue.Value;
    public static string Display => "V" + Current;

    static string ResolveCurrentVersion()
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        if (version is null) return "0.0.0";

        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
