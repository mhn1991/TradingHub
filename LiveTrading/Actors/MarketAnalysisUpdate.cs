using Agent.Models;
using Brokers.Models;
using LiveTrading.MarketData;

namespace LiveTrading.Actors;

public sealed record MarketAnalysisUpdate
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }
    public required MultiTimeframeAnalysis Analysis { get; init; }
    public required MarketDataHealthSnapshot Health { get; init; }
    public required long MarketSequence { get; init; }
}
