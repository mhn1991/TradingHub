namespace Brokers.Models;

public sealed record CandleQuery(
    InstrumentKey Instrument,
    BarInterval Interval,
    int Limit = 100,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
{
    public void Validate(int maximumLimit, string brokerName)
    {
        if (Instrument.IsEmpty)
        {
            throw new ArgumentException("A candle query must specify an instrument.", nameof(Instrument));
        }

        if (!Interval.IsValid)
        {
            throw new ArgumentException("A candle query must specify a valid interval.", nameof(Interval));
        }

        if (Limit < 1 || Limit > maximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Limit),
                $"{brokerName} candle count must be between 1 and {maximumLimit}.");
        }

        if (From is not null && To is not null && From > To)
        {
            throw new ArgumentException("Candle query From must not be later than To.", nameof(From));
        }
    }
}

public sealed record Ohlc(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close);

public enum VolumeKind
{
    Unknown,
    TickCount,
    BaseAssetQuantity,
    LastTradedQuantity
}

public sealed record MarketVolume(decimal Value, VolumeKind Kind);

public sealed record Candle
{
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }
    public required DateTimeOffset OpenTime { get; init; }
    public DateTimeOffset? CloseTime { get; init; }
    public required Ohlc Prices { get; init; }
    public MarketVolume? Volume { get; init; }
    public required bool IsComplete { get; init; }
}
