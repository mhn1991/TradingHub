using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Models;

public enum AgentAction
{
    Observe,
    Buy,
    Sell,
    Close,
    Cancel
}

public sealed record AgentDecision
{
    public string? DecisionId { get; init; }
    public string? SetupId { get; init; }
    public string? StrategyName { get; init; }
    public DateTimeOffset? SetupStartedAt { get; init; }
    public DateTimeOffset? ConfirmationAt { get; init; }
    public BarInterval? SignalInterval { get; init; }
    public required AgentAction Action { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public decimal? SuggestedQuantity { get; init; }
    public QuantityUnit QuantityUnit { get; init; } = QuantityUnit.Units;
    public StandardOrderType OrderType { get; init; } = StandardOrderType.Market;
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public string? StopSource { get; init; }
    public string? TargetSource { get; init; }
    public decimal? ExpectedRewardRisk { get; init; }
    public required decimal Confidence { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Reason { get; init; }
}

public sealed class MultiTimeframeAnalysis
{
    private readonly IReadOnlyDictionary<BarInterval, AnalysisSnapshot> _timeframes;

    public MultiTimeframeAnalysis(
        InstrumentKey instrument,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> timeframes)
    {
        Instrument = instrument;
        Timestamp = timestamp;
        _timeframes = timeframes ?? throw new ArgumentNullException(nameof(timeframes));
    }

    public InstrumentKey Instrument { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Timeframes => _timeframes;

    public AnalysisSnapshot Get(BarInterval interval) =>
        _timeframes.TryGetValue(interval, out AnalysisSnapshot? snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"No analysis is available for {interval}.");

    public bool TryGet(BarInterval interval, out AnalysisSnapshot snapshot) =>
        _timeframes.TryGetValue(interval, out snapshot!);
}

public sealed record AgentMarketContext
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required MultiTimeframeAnalysis Analysis { get; init; }
    public required AccountSnapshot Account { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    public required IReadOnlyList<BrokerOrder> OpenOrders { get; init; }
}
