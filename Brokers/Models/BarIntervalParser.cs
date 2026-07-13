using System.Globalization;
using System.Text.RegularExpressions;

namespace Brokers.Models;

/// <summary>Shared interval parse/format used by CLI, API, cache keys, and dashboard.</summary>
public static partial class BarIntervalParser
{
    private static readonly Regex Pattern = IntervalRegex();

    public static BarInterval Parse(string value)
    {
        if (!TryParse(value, out BarInterval interval))
        {
            throw new ArgumentException(
                $"Unsupported interval '{value}'. Examples: 1s, 5s, 1m, 3m, 5m, 15m, 30m, 1h, 2h, 1d.");
        }

        return interval;
    }

    public static bool TryParse(string? value, out BarInterval interval)
    {
        interval = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        Match match = Pattern.Match(value.Trim());
        if (!match.Success)
            return false;

        int number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        string unit = match.Groups["unit"].Value.ToLowerInvariant();
        interval = unit switch
        {
            "s" => BarInterval.Seconds(number),
            "m" => BarInterval.Minutes(number),
            "h" => BarInterval.Hours(number),
            "d" => BarInterval.Days(number),
            "w" => BarInterval.Weeks(number),
            "mo" => BarInterval.Months(number),
            _ => default
        };
        return interval.IsValid;
    }

    public static string Format(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => $"{interval.Value}s",
        BarUnit.Minute => $"{interval.Value}m",
        BarUnit.Hour => $"{interval.Value}h",
        BarUnit.Day => $"{interval.Value}d",
        BarUnit.Week => $"{interval.Value}w",
        BarUnit.Month => $"{interval.Value}mo",
        _ => interval.ToString()
    };

    /// <summary>Approximate duration in seconds for fixed-length intervals (not calendar months).</summary>
    public static double ApproximateSeconds(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => interval.Value,
        BarUnit.Minute => interval.Value * 60.0,
        BarUnit.Hour => interval.Value * 3600.0,
        BarUnit.Day => interval.Value * 86400.0,
        BarUnit.Week => interval.Value * 604800.0,
        BarUnit.Month => interval.Value * 2_592_000.0,
        _ => 0
    };

    /// <summary>
    /// True when the larger interval's length is an integer multiple of the smaller's for
    /// fixed second/minute/hour units. Calendar day/week/month are accepted only when equal.
    /// </summary>
    public static bool IsDivisible(BarInterval larger, BarInterval smaller)
    {
        if (!larger.IsValid || !smaller.IsValid)
            return false;
        if (larger == smaller)
            return true;

        // Prefer second-based comparison for s/m/h.
        if (larger.Unit is BarUnit.Second or BarUnit.Minute or BarUnit.Hour &&
            smaller.Unit is BarUnit.Second or BarUnit.Minute or BarUnit.Hour)
        {
            double large = ApproximateSeconds(larger);
            double small = ApproximateSeconds(smaller);
            if (small <= 0 || large < small)
                return false;
            double ratio = large / small;
            return Math.Abs(ratio - Math.Round(ratio)) < 1e-9;
        }

        return false;
    }

    public static int CompareDuration(BarInterval left, BarInterval right)
    {
        double l = ApproximateSeconds(left);
        double r = ApproximateSeconds(right);
        return l.CompareTo(r);
    }

    [GeneratedRegex("^(?<number>[1-9][0-9]*)(?<unit>s|m|h|d|w|mo)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalRegex();
}
