using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using RiskManager.Calibration;
using RiskManager.Conditions;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>Projection of <see cref="MetaLabelDecision"/> into an audited candidate field, with a
/// null-safe default for when the meta-label feature is disabled entirely (no decision produced).
/// <see cref="BucketId"/>/<see cref="Samples"/>/<see cref="ExpectedR"/>/<see cref="BrierScore"/>
/// stay null when the calibrated meta-model's Evaluate doesn't return which
/// calibration bucket matched, and extending <see cref="MetaLabelDecision"/> with bucket
/// provenance requires a future RiskManager contract extension.</summary>
public sealed record MetaLabelAudit
{
    public required bool Trade { get; init; }
    public required decimal Probability { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string ModelVersion { get; init; }
    public string? BucketId { get; init; }
    public int? Samples { get; init; }
    public decimal? ExpectedR { get; init; }
    public decimal? BrierScore { get; init; }

    public static MetaLabelAudit Disabled { get; } = new()
    {
        Trade = true,
        Probability = 1m,
        RiskMultiplier = 1m,
        ReasonCode = "MetaLabelDisabled",
        ModelVersion = "none"
    };

    public static MetaLabelAudit FromDecision(MetaLabelDecision decision) => new()
    {
        Trade = decision.Trade,
        Probability = decision.Probability,
        RiskMultiplier = decision.RiskMultiplier,
        ReasonCode = decision.ReasonCode,
        ModelVersion = decision.ModelVersion
    };
}

/// <summary>Projection of <see cref="SetupCalibrationDecision"/>, with the same disabled-default
/// convention as <see cref="MetaLabelAudit"/>.</summary>
public sealed record SetupCalibrationAudit
{
    public required bool Trade { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public string? CalibrationId { get; init; }
    public int Samples { get; init; }
    public decimal? ExpectedR { get; init; }

    public static SetupCalibrationAudit Disabled { get; } = new()
    {
        Trade = true,
        RiskMultiplier = 1m,
        ReasonCode = "SetupCalibrationDisabled",
        Explanation = "Setup calibration is not enabled for this policy bundle."
    };

    public static SetupCalibrationAudit FromDecision(SetupCalibrationDecision decision) => new()
    {
        Trade = decision.Trade,
        RiskMultiplier = decision.RiskMultiplier,
        ReasonCode = decision.ReasonCode,
        Explanation = decision.Explanation,
        CalibrationId = decision.CalibrationId,
        Samples = decision.Samples,
        ExpectedR = decision.ExpectedR
    };
}

/// <summary>A fully-audited, never-executed trade candidate produced by running a live
/// <see cref="Agent.Abstractions.ITradingAgent"/> through the same shared pipeline the simulator
/// uses (see <see cref="IStrategyDecisionPipelineFactory"/>).</summary>
public sealed record LiveTradeCandidate
{
    public required string CandidateId { get; init; }
    public required string DecisionId { get; init; }
    public string? SetupId { get; init; }
    public required string StrategyId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required AgentAction Action { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required long DecisionEpoch { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public required decimal RawConfidence { get; init; }
    public required decimal MultiTimeframeAlignment { get; init; }
    public required MarketRegime EntryRegime { get; init; }
    public required SetupCalibrationAudit SetupCalibration { get; init; }
    public required MetaLabelAudit MetaLabel { get; init; }
    public required TradingConditionDecision? TradingCondition { get; init; }
}

/// <summary>Turns one <see cref="TradingPipelineResult"/> that survived the pipeline as a
/// Buy/Sell decision into a fully-audited <see cref="LiveTradeCandidate"/>. Never executed -
/// The account-wide live runtime decides whether the resulting candidate remains shadow-only or
/// proceeds through manual/autonomous admission.</summary>
public static class SignalFunnel
{
    public static LiveTradeCandidate BuildCandidate(
        string strategyId,
        AgentMarketContext context,
        TradingPipelineResult result,
        long decisionEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        AgentDecision decision = result.Decision ?? throw new InvalidOperationException(
            "BuildCandidate requires a pipeline result that produced a decision.");

        decimal alignment = MetaLabelFeatureFactory.ComputeMultiTimeframeAlignment(decision.Action, context.Analysis);
        MarketRegime entryRegime = decision.RegimeLabel ?? ShortestSnapshot(context.Analysis).MarketRegime.Regime;

        return new LiveTradeCandidate
        {
            CandidateId = Guid.NewGuid().ToString("N"),
            DecisionId = decision.DecisionId ?? Guid.NewGuid().ToString("N"),
            SetupId = decision.SetupId,
            StrategyId = strategyId,
            Instrument = context.Instrument,
            Action = decision.Action,
            DecisionTime = context.Timestamp,
            DecisionEpoch = decisionEpoch,
            ReferencePrice = decision.ReferencePrice,
            StopLossPrice = decision.StopLossPrice,
            TakeProfitPrice = decision.TakeProfitPrice,
            RawConfidence = decision.Confidence,
            MultiTimeframeAlignment = alignment,
            EntryRegime = entryRegime,
            SetupCalibration = result.SetupCalibration is { } setupDecision
                ? SetupCalibrationAudit.FromDecision(setupDecision)
                : SetupCalibrationAudit.Disabled,
            MetaLabel = result.MetaLabel is { } metaDecision
                ? MetaLabelAudit.FromDecision(metaDecision)
                : MetaLabelAudit.Disabled,
            TradingCondition = result.TradingCondition
        };
    }

    /// <summary>Reimplementation of <c>SafeTradingPipeline.ShortestSnapshot</c> - that method is
    /// private, so the same "shortest analysis interval" selection is duplicated here rather than
    /// exposed for reuse.</summary>
    private static AnalysisSnapshot ShortestSnapshot(MultiTimeframeAnalysis analysis) =>
        analysis.Timeframes.OrderBy(kv => BarIntervalParser.ApproximateSeconds(kv.Key)).First().Value;
}
