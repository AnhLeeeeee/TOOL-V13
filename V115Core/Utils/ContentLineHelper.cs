using System.Text;

namespace ToolTikTokV11.Utils;

public static class ContentLineHelper
{
    public static string NormalizeNewLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\u2028", "\n", StringComparison.Ordinal)
            .Replace("\u2029", "\n", StringComparison.Ordinal);

        return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    public static List<string> GetDisplayLinesFromText(string text)
        => SplitNormalizedText(NormalizeNewLines(text), trimAll: false);

    public static List<string> GetDisplayLinesFromRawLines(IEnumerable<string> lines)
    {
        var merged = string.Join("\n", lines ?? Array.Empty<string>());
        return SplitNormalizedText(NormalizeNewLines(merged), trimAll: false);
    }

    public static List<string> GetAutomationLinesFromText(string text)
        => SplitNormalizedText(NormalizeNewLines(text), trimAll: true);

    public static string JoinLinesForTextBox(IEnumerable<string> lines)
        => string.Join(Environment.NewLine, lines ?? Array.Empty<string>());

    public static string[] GetValidLinesForSave(string text)
        => GetDisplayLinesFromText(text).ToArray();

    public static string[] GetValidLinesForSave(IEnumerable<string> lines)
        => GetDisplayLinesFromRawLines(lines).ToArray();

    static List<string> SplitNormalizedText(string normalizedText, bool trimAll)
    {
        if (string.IsNullOrEmpty(normalizedText)) return [];

        return normalizedText
            .Split(Environment.NewLine)
            .Select(line => trimAll ? line.Trim() : line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }
}
