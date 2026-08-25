using ExecutionManager;
using RiskManager.Calibration;
using RiskManager.Conditions;
using RiskManager.Safety;
using TradingCore.MarketData;
using TradingJournal;

namespace TradingCore.Pipeline;

/// <summary>
/// Default <see cref="IStrategyDecisionPipelineFactory"/>. The interface's fixed method
/// signature can't carry every <see cref="SafeTradingPipeline"/> collaborator (execution,
/// data-quality gate, journal, trading-condition filter, and - critically - a pre-built,
/// shareable <see cref="ITradingSafetyController"/>), so those become constructor parameters on
/// this concrete class instead. Sharing the safety controller matters: a caller that already
/// owns a controller instance (e.g. a simulation session tracking its own equity-protection/
/// daily-loss state) must get that exact instance back inside the pipeline, or the two would
/// silently desynchronize.
/// </summary>
public sealed class StrategyDecisionPipelineFactory(
    IExecutionCoordinator execution,
    IMarketDataQualityGate? dataQuality = null,
    ITradeJournal? journal = null,
    ITradingConditionFilter? tradingConditions = null,
    ITradingSafetyController? sharedSafetyController = null) : IStrategyDecisionPipelineFactory
{
    public StrategyDecisionRuntime Create(
        StrategyRuntimeDefinition strategy,
        RuntimeFeaturePolicy featurePolicy,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        TradingSafetyOptions safetyOptions,
        ITradingConditionFilter? tradingConditionsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(featurePolicy);
        ArgumentNullException.ThrowIfNull(safetyOptions);

        // AGENT-05: requiredFeatureSchemaHash is deliberately MetaLabelFeatureFactory.SchemaVersion
        // (a fixed constant verifying the meta-label FEATURE VECTOR SCHEMA is compatible), not
        // featurePolicy.ComputeHash() (a content hash of the OPTION VALUES - regime routing,
        // RSI/Bollinger settings, etc. - see RuntimeFeaturePolicy's own doc comment: those are
        // audit/provenance only and not yet enforced here). An artifact trained under a
        // materially different RuntimeFeaturePolicy configuration will still pass this check as
        // long as the feature schema itself hasn't changed - this is a known, accepted gap, not
        // an oversight.
        ISetupCalibrationPolicy? calibrationPolicy = featurePolicy.SetupCalibration is { Enabled: true }
            ? new SetupCalibrationPolicy(
                setupCalibration,
                featurePolicy.SetupCalibration,
                requiredFeatureSchemaHash: MetaLabelFeatureFactory.SchemaVersion)
            : null;

        ITradingSafetyController safety = sharedSafetyController ?? new TradingSafetyController(safetyOptions);
        ITradingConditionFilter? effectiveTradingConditions = tradingConditionsOverride ?? tradingConditions;

        var pipeline = new SafeTradingPipeline(
            strategy.Agent,
            execution,
            dataQuality,
            safety,
            journal,
            effectiveTradingConditions,
            calibrationPolicy,
            metaModel);

        return new StrategyDecisionRuntime
        {
            Agent = strategy.Agent,
            Pipeline = pipeline,
            StrategyVersion = strategy.StrategyVersion,
            FeaturePolicyHash = featurePolicy.ComputeHash(),
            SetupCalibrationId = setupCalibration?.CalibrationId,
            MetaModelVersion = metaModel?.ModelVersion
        };
    }
}
