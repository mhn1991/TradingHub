using Agent.Abstractions;
using RiskManager.Calibration;
using RiskManager.Safety;

namespace TradingCore.Pipeline;

/// <summary>
/// Builds the exact same agent-plus-pipeline pairing for both the simulator and a future live
/// host, so the two can never structurally diverge the way <c>StreamingComparativeEngineOptions
/// .MetaLabelModel</c> once silently did when each caller built its own <see
/// cref="SafeTradingPipeline"/> by hand.
/// </summary>
public interface IStrategyDecisionPipelineFactory
{
    StrategyDecisionRuntime Create(
        StrategyRuntimeDefinition strategy,
        RuntimeFeaturePolicy featurePolicy,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        TradingSafetyOptions safetyOptions);
}

/// <summary>Everything constructing a strategy's pipeline produces, plus the provenance needed
/// to later verify a running agent matches what it was declared to be.</summary>
public sealed record StrategyDecisionRuntime
{
    public required ITradingAgent Agent { get; init; }
    public required SafeTradingPipeline Pipeline { get; init; }
    public required string StrategyVersion { get; init; }
    public required string FeaturePolicyHash { get; init; }
    public required string? SetupCalibrationId { get; init; }
    public required string? MetaModelVersion { get; init; }
}
