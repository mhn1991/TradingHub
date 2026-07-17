using Brokers.Models;

namespace LiveTrading.MarketData;

public sealed record LiveQuoteSnapshot
{
    public required InstrumentKey Instrument { get; init; }
    public required decimal Bid { get; init; }
    public required decimal Ask { get; init; }
    public decimal Mid => (Bid + Ask) / 2m;
    public decimal Spread => Ask - Bid;
    public required DateTimeOffset BrokerTime { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required bool IsTradeable { get; init; }
    public required bool IsStale { get; init; }
}
