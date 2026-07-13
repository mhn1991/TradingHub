using Brokers.Models;

namespace Simulator.Tests;

internal static class TestCandles
{
    public static Candle Create(
        InstrumentKey instrument,
        DateTimeOffset openTime,
        BarInterval interval,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume = 1m)
    {
        TimeSpan duration = interval.Unit switch
        {
            BarUnit.Second => TimeSpan.FromSeconds(interval.Value),
            BarUnit.Minute => TimeSpan.FromMinutes(interval.Value),
            BarUnit.Hour => TimeSpan.FromHours(interval.Value),
            BarUnit.Day => TimeSpan.FromDays(interval.Value),
            _ => throw new NotSupportedException("The test helper supports fixed-duration intervals only.")
        };

        return new Candle
        {
            Instrument = instrument,
            Interval = interval,
            OpenTime = openTime,
            CloseTime = openTime + duration,
            Prices = new Ohlc(open, high, low, close),
            Volume = new MarketVolume(volume, VolumeKind.TickCount),
            IsComplete = true
        };
    }
}
