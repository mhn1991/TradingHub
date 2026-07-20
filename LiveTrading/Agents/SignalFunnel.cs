using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using RiskManager.Calibration;
using RiskManager.Conditions;
using TradingCore.Pipeline;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    public AgentInstanceKey? AgentInstance { get; init; }
    public string AnalysisProfileHash { get; init; } = string.Empty;
    public Guid PolicyBundleId { get; init; }
    public int PolicyRevision { get; init; }
    public required AgentAction Action { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required long DecisionEpoch { get; init; }
    /// <summary>Multi-agent architecture Phase 6: the <c>MarketAnalysisUpdate.MarketSequence</c>
    /// this candidate was computed under - <c>LiveDecisionEpochCoordinator.SubmitCandidate</c>
    /// rejects a candidate whose sequence is stale relative to the instrument's latest known
    /// sequence, the live-side substitute for <c>MarketAnalysisSnapshot.SnapshotVersion</c> (Live
    /// does not yet route agent dispatch through <c>MarketAnalysisSnapshot</c> - see
    /// <c>AgentSupervisor</c>'s remarks).</summary>
    public required long MarketSequence { get; init; }
    public decimal? SuggestedQuantity { get; init; }
    public string? PlaybookId { get; init; }
    public string? PlaybookVersion { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public required decimal RawConfidence { get; init; }
    public required decimal MultiTimeframeAlignment { get; init; }
    public required MarketRegime EntryRegime { get; init; }
    public required SetupCalibrationAudit SetupCalibration { get; init; }
    public required MetaLabelAudit MetaLabel { get; init; }
    public required TradingConditionDecision? TradingCondition { get; init; }
    public decimal NeoWaveRiskMultiplier { get; init; } = 1m;
    public decimal StructuralEvidenceRiskMultiplier { get; init; } = 1m;
    public string? NeoWaveHypothesisId { get; init; }
    public NeoWavePatternType? NeoWavePatternType { get; init; }
    public NeoWaveDirection? NeoWaveDirection { get; init; }
    public decimal? NeoWaveStructuralScore { get; init; }
    public decimal? NeoWaveConflictScore { get; init; }
    public decimal? NeoWaveInvalidationPrice { get; init; }
    public Guid? EntrySupplyDemandZoneId { get; init; }
    public decimal? EntrySupplyDemandZoneLowerPrice { get; init; }
    public decimal? EntrySupplyDemandZoneUpperPrice { get; init; }
    public SupplyDemandZoneState? EntrySupplyDemandZoneState { get; init; }
    public string? EntrySupplyDemandProfileHash { get; init; }
    public Guid? OriginatingLiquidityPoolId { get; init; }
    public Guid? OriginatingLiquiditySweepId { get; init; }
    public string? OriginatingLiquidityProfileHash { get; init; }
    public Guid? TargetLiquidityPoolId { get; init; }
    public string? TargetLiquidityProfileHash { get; init; }
    public decimal? StructuralInvalidationReference { get; init; }
    public bool EntrySupplyDemandManagementEnabled { get; init; }
    public bool EntryLiquidityManagementEnabled { get; init; }
    public string? StructuralManagementPolicyRevision { get; init; }
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
        long decisionEpoch,
        long marketSequence,
        AgentInstanceKey? agentInstance = null,
        AnalysisProfileKey? analysisProfile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        AgentDecision decision = result.Decision ?? throw new InvalidOperationException(
            "BuildCandidate requires a pipeline result that produced a decision.");

        decimal alignment = MetaLabelFeatureFactory.ComputeMultiTimeframeAlignment(decision.Action, context.Analysis);
        MarketRegime entryRegime = decision.RegimeLabel ?? ShortestSnapshot(context.Analysis).MarketRegime.Regime;
        string identity = agentInstance is null
            ? $"legacy|{context.Instrument.Value}|{strategyId}"
            : $"{agentInstance.DeploymentId}|{agentInstance.Instrument.Value}|{agentInstance.StrategyId}|" +
              $"{agentInstance.PolicyBundleId:N}|{agentInstance.Revision}";
        string decisionId = decision.DecisionId ?? StableId(
            "decision",
            identity,
            context.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            decisionEpoch.ToString(CultureInfo.InvariantCulture),
            marketSequence.ToString(CultureInfo.InvariantCulture),
            decision.Action.ToString(),
            Invariant(decision.ReferencePrice),
            Invariant(decision.StopLossPrice),
            Invariant(decision.TakeProfitPrice));

        return new LiveTradeCandidate
        {
            CandidateId = StableId(
                "candidate",
                identity,
                analysisProfile?.ProfileHash ?? string.Empty,
                decisionId,
                decisionEpoch.ToString(CultureInfo.InvariantCulture),
                marketSequence.ToString(CultureInfo.InvariantCulture)),
            DecisionId = decisionId,
            SetupId = decision.SetupId,
            StrategyId = strategyId,
            Instrument = context.Instrument,
            AgentInstance = agentInstance,
            AnalysisProfileHash = analysisProfile?.ProfileHash ?? string.Empty,
            PolicyBundleId = agentInstance?.PolicyBundleId ?? Guid.Empty,
            PolicyRevision = agentInstance?.Revision ?? 0,
            Action = decision.Action,
            DecisionTime = context.Timestamp,
            DecisionEpoch = decisionEpoch,
            MarketSequence = marketSequence,
            SuggestedQuantity = decision.SuggestedQuantity,
            PlaybookId = decision.PlaybookId,
            PlaybookVersion = decision.PlaybookVersion,
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
            TradingCondition = result.TradingCondition,
            NeoWaveRiskMultiplier = Math.Clamp(decision.NeoWaveRiskMultiplier ?? 1m, 0m, 1m),
            NeoWaveHypothesisId = decision.NeoWaveHypothesisId,
            NeoWavePatternType = decision.NeoWavePatternType,
            NeoWaveDirection = decision.NeoWaveDirection,
            NeoWaveStructuralScore = decision.NeoWaveStructuralScore,
            NeoWaveConflictScore = decision.NeoWaveConflictScore,
            NeoWaveInvalidationPrice = decision.NeoWaveInvalidationPrice,
            StructuralEvidenceRiskMultiplier = Math.Clamp(
                decision.StructuralEvidenceRiskMultiplier ?? 1m, 0m, 1m),
            EntrySupplyDemandZoneId = decision.EntrySupplyDemandZoneId,
            EntrySupplyDemandZoneLowerPrice = decision.EntrySupplyDemandZoneLowerPrice,
            EntrySupplyDemandZoneUpperPrice = decision.EntrySupplyDemandZoneUpperPrice,
            EntrySupplyDemandZoneState = decision.EntrySupplyDemandZoneState,
            EntrySupplyDemandProfileHash = decision.EntrySupplyDemandProfileHash,
            OriginatingLiquidityPoolId = decision.OriginatingLiquidityPoolId,
            OriginatingLiquiditySweepId = decision.OriginatingLiquiditySweepId,
            OriginatingLiquidityProfileHash = decision.OriginatingLiquidityProfileHash,
            TargetLiquidityPoolId = decision.TargetLiquidityPoolId,
            TargetLiquidityProfileHash = decision.TargetLiquidityProfileHash,
            StructuralInvalidationReference = decision.StructuralInvalidationReference,
            EntrySupplyDemandManagementEnabled = decision.EntrySupplyDemandManagementEnabled,
            EntryLiquidityManagementEnabled = decision.EntryLiquidityManagementEnabled,
            StructuralManagementPolicyRevision = decision.StructuralManagementPolicyRevision
        };
    }

    /// <summary>Reimplementation of <c>SafeTradingPipeline.ShortestSnapshot</c> - that method is
    /// private, so the same "shortest analysis interval" selection is duplicated here rather than
    /// exposed for reuse.</summary>
    private static AnalysisSnapshot ShortestSnapshot(MultiTimeframeAnalysis analysis) =>
        analysis.Timeframes.OrderBy(kv => BarIntervalParser.ApproximateSeconds(kv.Key)).First().Value;

    private static string StableId(params string[] components)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', components)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Invariant(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
