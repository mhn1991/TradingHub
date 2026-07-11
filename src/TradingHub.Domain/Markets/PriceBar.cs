namespace TradingHub.Domain.Markets;

public sealed record PriceBar
{
    public required string InstrumentId { get; init; }

    public required DateTimeOffset OpenTime { get; init; }

    public required TimeSpan Period { get; init; }

    public required Ohlc Bid { get; init; }

    public required Ohlc Ask { get; init; }

    public decimal Volume { get; init; }

    public DateTimeOffset CloseTime => OpenTime + Period;

    public decimal MidClose => (Bid.Close + Ask.Close) / 2m;

    public decimal SpreadBps => (Ask.Close - Bid.Close) / MidClose * 10_000m;

    public void EnsureValid()
    {
        Bid.EnsureValid();
        Ask.EnsureValid();

        if (string.IsNullOrWhiteSpace(InstrumentId) || Period <= TimeSpan.Zero || Volume < 0m)
        {
            throw new InvalidOperationException("Price bar metadata is invalid.");
        }

        if (Ask.Open < Bid.Open || Ask.Close < Bid.Close)
        {
            throw new InvalidOperationException("Ask prices cannot be below bid prices.");
        }
    }
}
