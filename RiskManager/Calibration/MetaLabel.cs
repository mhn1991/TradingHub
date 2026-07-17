using Agent.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace RiskManager.Calibration;

/// <summary>
/// Causal, decision-time-only inputs for an optional setup meta-model. The deterministic
/// strategy remains responsible for creating the candidate order.
/// </summary>
public sealed record MetaLabelFeatures
{
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required AgentAction Direction { get; init; }
    public required MarketRegime Regime { get; init; }
    public required decimal RegimeConfidence { get; init; }
    public required decimal SetupConfidence { get; init; }
    public required decimal MultiTimeframeAlignment { get; init; }
    public required IReadOnlyList<string> PriceActionEvents { get; init; }
    public decimal? Atr { get; init; }
    public decimal? AtrPercentile { get; init; }
    public decimal? Adx { get; init; }
    public decimal? EfficiencyRatio { get; init; }
    public decimal? Rsi { get; init; }
    public decimal? BollingerPercentB { get; init; }
    public decimal? DonchianWidthAtr { get; init; }
    public decimal? NearestValueReferenceDistanceAtr { get; init; }
    public decimal? SpreadAtr { get; init; }
    public decimal? ExpectedRewardRisk { get; init; }
    public decimal? PlannedCostBasisPoints { get; init; }
    public decimal? PortfolioConcentration { get; init; }
    public decimal? CurrencyStrengthDifferential { get; init; }
    public required string FeatureSchemaVersion { get; init; }
}

public sealed record MetaLabelDecision
{
    public required bool Trade { get; init; }
    public required decimal Probability { get; init; }
    public required string ModelVersion { get; init; }
    public required string ReasonCode { get; init; }
    /// <summary>Conservative scalar only. Values above one are rejected.</summary>
    public decimal RiskMultiplier { get; init; } = 1m;

    public void Validate()
    {
        if (Probability is < 0m or > 1m || RiskMultiplier is < 0m or > 1m ||
            string.IsNullOrWhiteSpace(ModelVersion) || string.IsNullOrWhiteSpace(ReasonCode))
            throw new InvalidOperationException("The meta-label model returned an invalid decision.");
    }
}

public interface ISetupMetaModel
{
    /// <summary>Identifies the model/calibration in use, before any decision has been made.</summary>
    string ModelVersion { get; }

    MetaLabelDecision Evaluate(MetaLabelFeatures features);
}

public static class MetaLabelFeatureFactory
{
    public const string SchemaVersion = "tradinghub-meta-v1";

    public static MetaLabelFeatures Create(
        AgentDecision decision,
        MultiTimeframeAnalysis analysis,
        decimal? spreadAtr = null,
        decimal? plannedCostBasisPoints = null,
        decimal? portfolioConcentration = null,
        decimal? currencyStrengthDifferential = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(analysis);
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
            throw new ArgumentException("Meta-label features require a deterministic buy or sell candidate.", nameof(decision));
        AnalysisSnapshot[] snapshots = analysis.Timeframes.Values
            .OrderBy(item => item.AvailableAt)
            .ThenBy(item => item.Interval.Value)
            .ToArray();
        if (snapshots.Length == 0)
            throw new ArgumentException("Meta-label features require at least one causal analysis snapshot.", nameof(analysis));
        if (snapshots.Any(item => item.AvailableAt > analysis.Timestamp))
            throw new InvalidOperationException("A future analysis snapshot cannot be used by the meta-label model.");

        AnalysisSnapshot trigger = snapshots
            .OrderBy(item => ApproximateSeconds(item.Interval))
            .First();
        decimal alignment = ComputeMultiTimeframeAlignment(decision.Action, analysis);
        string[] events = snapshots
            .SelectMany(item => item.PriceAction.Events)
            .Where(item => item.ConfirmedAt <= analysis.Timestamp)
            .OrderBy(item => item.ConfirmedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .Select(item => item.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new MetaLabelFeatures
        {
            StrategyId = decision.StrategyId ?? decision.StrategyName ?? "unknown",
            Instrument = decision.Instrument.Value,
            AvailableAt = analysis.Timestamp,
            Direction = decision.Action,
            Regime = trigger.MarketRegime.Regime,
            RegimeConfidence = trigger.MarketRegime.Confidence,
            SetupConfidence = decision.Confidence,
            MultiTimeframeAlignment = alignment,
            PriceActionEvents = events,
            Atr = trigger.Indicators.Atr,
            AtrPercentile = trigger.Indicators.AtrAnalysis.Percentile,
            Adx = trigger.Indicators.AdxAnalysis.Adx,
            EfficiencyRatio = trigger.Indicators.EfficiencyRatio,
            Rsi = trigger.Indicators.Rsi,
            BollingerPercentB = trigger.Indicators.BollingerAnalysis.PercentB,
            DonchianWidthAtr = trigger.Indicators.Donchian.WidthAtr,
            NearestValueReferenceDistanceAtr = trigger.ValueReferences
                .Where(item => item.DistanceAtr is not null)
                .Select(item => (decimal?)Math.Abs(item.DistanceAtr!.Value))
                .Min(),
            SpreadAtr = spreadAtr,
            ExpectedRewardRisk = decision.ExpectedRewardRisk,
            PlannedCostBasisPoints = plannedCostBasisPoints,
            PortfolioConcentration = portfolioConcentration,
            CurrencyStrengthDifferential = currencyStrengthDifferential,
            FeatureSchemaVersion = SchemaVersion
        };
    }

    /// <summary>
    /// Fraction of causal analysis snapshots whose regime direction agrees with the decision's
    /// side. Exposed separately from <see cref="Create"/> so callers that only need this one
    /// feature (e.g. recording it on a closed trade for later meta-model calibration) don't
    /// need a full Buy/Sell <see cref="AgentDecision"/> to call it.
    /// </summary>
    public static decimal ComputeMultiTimeframeAlignment(AgentAction direction, MultiTimeframeAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (direction is not (AgentAction.Buy or AgentAction.Sell))
            return 0m;
        AnalysisSnapshot[] snapshots = analysis.Timeframes.Values.ToArray();
        if (snapshots.Length == 0)
            return 0m;
        int directionValue = direction == AgentAction.Buy ? 1 : -1;
        int aligned = snapshots.Count(item => Direction(item.MarketRegime.Regime) == directionValue);
        return aligned / (decimal)snapshots.Length;
    }

    private static int Direction(MarketRegime regime) => regime switch
    {
        MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp => 1,
        MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown => -1,
        _ => 0
    };

    private static double ApproximateSeconds(Brokers.Models.BarInterval interval) => interval.Unit switch
    {
        Brokers.Models.BarUnit.Second => interval.Value,
        Brokers.Models.BarUnit.Minute => interval.Value * 60d,
        Brokers.Models.BarUnit.Hour => interval.Value * 3_600d,
        Brokers.Models.BarUnit.Day => interval.Value * 86_400d,
        _ => double.MaxValue
    };
}
