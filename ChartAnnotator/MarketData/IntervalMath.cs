using Brokers.Models;

namespace ChartAnnotator.MarketData;

internal static class IntervalMath
{
    public static DateTimeOffset BucketStart(DateTimeOffset time, BarInterval interval)
    {
        DateTimeOffset utc = time.ToUniversalTime();

        return interval.Unit switch
        {
            BarUnit.Second => FromUnixSeconds(
                Floor(utc.ToUnixTimeSeconds(), interval.Value)),

            BarUnit.Minute => FromUnixSeconds(
                Floor(utc.ToUnixTimeSeconds(), checked(interval.Value * 60L))),

            BarUnit.Hour => FromUnixSeconds(
                Floor(utc.ToUnixTimeSeconds(), checked(interval.Value * 3_600L))),

            BarUnit.Day => FromUnixSeconds(
                Floor(utc.ToUnixTimeSeconds(), checked(interval.Value * 86_400L))),

            BarUnit.Week => StartOfWeek(utc, interval.Value),
            BarUnit.Month => StartOfMonth(utc, interval.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };
    }

    public static DateTimeOffset BucketEnd(DateTimeOffset start, BarInterval interval) =>
        interval.Unit switch
        {
            BarUnit.Second => start.AddSeconds(interval.Value),
            BarUnit.Minute => start.AddMinutes(interval.Value),
            BarUnit.Hour => start.AddHours(interval.Value),
            BarUnit.Day => start.AddDays(interval.Value),
            BarUnit.Week => start.AddDays(checked(interval.Value * 7)),
            BarUnit.Month => start.AddMonths(interval.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };

    public static TimeSpan ApproximateDuration(BarInterval interval) =>
        interval.Unit switch
        {
            BarUnit.Second => TimeSpan.FromSeconds(interval.Value),
            BarUnit.Minute => TimeSpan.FromMinutes(interval.Value),
            BarUnit.Hour => TimeSpan.FromHours(interval.Value),
            BarUnit.Day => TimeSpan.FromDays(interval.Value),
            BarUnit.Week => TimeSpan.FromDays(interval.Value * 7),
            BarUnit.Month => TimeSpan.FromDays(interval.Value * 30),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };

    private static long Floor(long value, long interval) => value - Mod(value, interval);
    private static long Mod(long value, long divisor) => ((value % divisor) + divisor) % divisor;
    private static DateTimeOffset FromUnixSeconds(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static DateTimeOffset StartOfWeek(DateTimeOffset time, int weekCount)
    {
        DateTimeOffset epochMonday = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);
        long elapsedDays = (long)(time.Date - epochMonday.Date).TotalDays;
        long bucketDays = checked(weekCount * 7L);
        long startDays = Floor(elapsedDays, bucketDays);
        return epochMonday.AddDays(startDays);
    }

    private static DateTimeOffset StartOfMonth(DateTimeOffset time, int monthCount)
    {
        int absoluteMonth = checked(time.Year * 12 + time.Month - 1);
        int bucketMonth = absoluteMonth - Mod(absoluteMonth, monthCount);
        int year = bucketMonth / 12;
        int month = bucketMonth % 12 + 1;
        return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}
