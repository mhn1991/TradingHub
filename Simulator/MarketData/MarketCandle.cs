using Brokers.Models;

namespace Simulator.MarketData;

/// <summary>Optional bid/ask envelope around the midpoint execution candle.</summary>
public sealed record MarketCandle
{
    public required Candle Mid { get; init; }
    public Candle? Bid { get; init; }
    public Candle? Ask { get; init; }

    public InstrumentKey Instrument => Mid.Instrument;
    public BarInterval Interval => Mid.Interval;
    public DateTimeOffset OpenTime => Mid.OpenTime;
    public DateTimeOffset AvailableAt => Mid.CloseTime ?? Mid.Interval.AddTo(Mid.OpenTime);

    public static MarketCandle FromMid(Candle mid) => new() { Mid = mid };
}
