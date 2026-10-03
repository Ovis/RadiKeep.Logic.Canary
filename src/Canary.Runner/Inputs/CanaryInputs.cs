using System.Globalization;

namespace Canary.Runner;

/// <summary>
/// 入力値と日本時間の扱いをまとめる。
/// </summary>
internal static class CanaryInputs
{
    internal static TimeZoneInfo ResolveJapanTimeZone()
    {
        var candidates = new[] { "Asia/Tokyo", "Tokyo Standard Time" };
        foreach (var id in candidates)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // try next
            }
        }

        return TimeZoneInfo.Utc;
    }

    internal static string NormalizeRadiruAreaKey(string areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return areaId;
        }

        var trimmed = areaId.Trim();
        if (trimmed.StartsWith("JP", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[2..];
            if (int.TryParse(suffix, out var numeric))
            {
                return (numeric * 10).ToString("000", CultureInfo.InvariantCulture);
            }
        }

        return trimmed;
    }
}
