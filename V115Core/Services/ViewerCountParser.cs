using System.Globalization;
using System.Text.RegularExpressions;
using ToolTikTokV11.Utils;

namespace ToolTikTokV11.Services;

/// <summary>
/// Parser thuần text cho số người xem đọc trực tiếp từ DOM/XPath.
/// Không dùng OCR, screenshot hay Tesseract.
/// Giữ cách hiểu K/M/B và tương thích dữ liệu số thập phân cũ.
/// </summary>
public static partial class ViewerCountParser
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<num>[0-9OIL]*[0-9][0-9OIL]*(?:[\.,][0-9OIL]+)?)[ \t]*(?<unit>[KMB]?)(?![\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex ViewerTokenRegex();

    public static int Parse(string raw, Logger? log = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return -1;

        var text = raw.Trim().ToUpperInvariant().Replace("\r", " ").Replace("\n", " ");
        var matches = ViewerTokenRegex().Matches(text);
        if (matches.Count == 0) return -1;

        (int value, int score, bool inferredK)? best = null;
        foreach (Match m in matches)
        {
            var token = m.Groups["num"].Value;
            var normalized = token.Replace('O', '0').Replace('I', '1').Replace('L', '1').Replace(',', '.');
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;

            var unit = m.Groups["unit"].Value.ToUpperInvariant();
            bool inferredK = false;
            if (unit.Length == 0 && normalized.Contains('.'))
            {
                unit = "K";
                inferredK = true;
            }

            double mul = unit switch
            {
                "K" => 1_000d,
                "M" => 1_000_000d,
                "B" => 1_000_000_000d,
                _ => 1d
            };
            var scaled = v * mul;
            if (scaled < 0 || double.IsNaN(scaled) || double.IsInfinity(scaled)) continue;
            var value = scaled >= int.MaxValue ? int.MaxValue : (int)Math.Round(scaled);

            int score = m.Groups["unit"].Value.Length > 0 ? 300 : inferredK ? 200 : 100;
            score += Math.Min(99, m.Index / 8);

            if (best is null || score >= best.Value.score)
                best = (value, score, inferredK);
        }

        if (best is null) return -1;

        if (best.Value.inferredK)
            log?.Warn($"Dữ liệu người xem không có hậu tố K; tự hiểu “{raw}” là đơn vị nghìn.");

        return best.Value.value;
    }
}
