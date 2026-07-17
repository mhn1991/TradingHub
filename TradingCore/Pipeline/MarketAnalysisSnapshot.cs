using Brokers.Models;
using ChartAnnotator.Models;
using TradingCore.MarketData;

namespace TradingCore.Pipeline;

/// <summary>
/// One immutable, versioned bundle of computed analysis for an instrument under a specific
/// <see cref="AnalysisProfileKey"/>, published once per closed candle by the analysis-profile
/// registry (Simulator: <c>Simulator.Engine.AnalysisProfileRegistry</c>; Live:
/// <c>LiveTrading.Agents.LiveAnalysisProfileRegistry</c>) and shared, unmutated, by every Agent
/// runtime assigned to that profile. This is a thin wrapper over already-immutable per-timeframe
/// types (<see cref="AnalysisSnapshot"/> already carries its own <c>AvailableAt</c>/<c>Version</c>
/// for lookahead-leakage protection) - it does not replace <c>Simulator.Models.AnalysisSnapshotSet</c>
/// or <c>Agent.Models.MultiTimeframeAnalysis</c>, both of which keep serving their existing
/// internal roles unchanged. An <c>IAgentRuntime</c> adapts this into whichever of those two
/// shapes its own pipeline expects immediately before evaluation.
/// </summary>
public sealed record MarketAnalysisSnapshot
{
    public required InstrumentKey Instrument { get; init; }
    public required AnalysisProfileKey Profile { get; init; }
    public required long SnapshotVersion { get; init; }
    public required long DecisionEpoch { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    /// <summary>
    /// Always a fresh defensive copy at publish time (mirrors the existing
    /// <c>new Dictionary&lt;BarInterval, AnalysisSnapshot&gt;(pipeline.LatestSnapshots)</c> pattern
    /// in <c>Simulator.Engine.StreamingComparativeEngine</c>) - never a live reference into a
    /// mutable working dictionary.
    /// </summary>
    public required IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Timeframes { get; init; }

    public required CurrencyStrengthSnapshot? CrossMarket { get; init; }
    public required DataQualityResult DataQuality { get; init; }
}
