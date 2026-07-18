using Agent.Models;
using Brokers.Models;
using LiveTrading.MarketData;
using TradingCore.Pipeline;

namespace LiveTrading.Actors;

public sealed record MarketAnalysisUpdate
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }
    public required MultiTimeframeAnalysis Analysis { get; init; }
    /// <summary>Immutable, profile-keyed publish boundary shared by every assigned agent.</summary>
    public IReadOnlyDictionary<AnalysisProfileKey, MarketAnalysisSnapshot> SnapshotsByProfile { get; init; }
        = new Dictionary<AnalysisProfileKey, MarketAnalysisSnapshot>();
    public required MarketDataHealthSnapshot Health { get; init; }
    public required long MarketSequence { get; init; }

    public bool TryGetSnapshot(AnalysisProfileKey profile, out MarketAnalysisSnapshot snapshot) =>
        SnapshotsByProfile.TryGetValue(profile, out snapshot!);
}
