using Agent.Configuration;
using Agent.Strategies;
using TradingCore.Pipeline;
using TradingPolicies;

namespace Simulator.Models;

/// <summary>
/// Promotes the exact environment-neutral policy used by a validated simulator strategy into a
/// portable profile. Historical source, simulated fill model, financing model, replay settings,
/// cache settings, and worker settings are deliberately excluded.
/// </summary>
public static class TradingPolicyPromotion
{
    public static TradingPolicyProfile CreateProfile(
        BacktestRuntimeOptions runtime,
        string strategyId,
        string strategyVersion,
        ProgressiveAgentKind agentKind,
        ProgressiveStrategyOptions resolvedAgentOptions,
        Guid profileId,
        int revision,
        DateTimeOffset createdAt,
        TradingPolicyProfileStatus status = TradingPolicyProfileStatus.Reviewed,
        Guid? setupCalibrationArtifactId = null,
        Guid? managementCalibrationArtifactId = null,
        Guid? metaModelArtifactId = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion);
        ArgumentNullException.ThrowIfNull(resolvedAgentOptions);
        runtime.Validate();
        resolvedAgentOptions.Validate();

        RuntimeFeaturePolicy featurePolicy = CreateFeaturePolicy(runtime, setupCalibrationArtifactId);

        return TradingPolicyProfile.Create(
            profileId,
            revision,
            strategyId,
            strategyVersion,
            agentKind,
            resolvedAgentOptions,
            status,
            featurePolicy,
            runtime.PositionSizing,
            runtime.AdaptiveRisk,
            runtime.PortfolioRisk,
            runtime.CorrelationRisk,
            runtime.TradingConditions,
            runtime.SafetyOptions,
            runtime.LegacyPositionManagement,
            runtime.ImprovedPositionManagement,
            runtime.StructuralPositionManagement,
            runtime.RegimeManagement,
            // AGENT-02: Enabled must always agree with whether this specific profile actually
            // has a management-calibration artifact attached, not with runtime.ManagementCalibration's
            // own unrelated consumption toggle - same rationale as the MetaModel reconciliation below.
            runtime.ManagementCalibration with { Enabled = managementCalibrationArtifactId.HasValue },
            // AGENT-02: Enabled must always agree with whether this specific profile actually
            // has a meta-model artifact attached, not with runtime.MetaModel's own unrelated
            // consumption toggle (e.g. the auto-train pipeline reuses the *source* backtest's
            // Runtime, whose MetaModel.Enabled reflects that unrelated run, not the artifact
            // this promotion just attached) - mirrors the CLI's own established
            // `MetaModelArtifact is null ? MetaModel : MetaModel with { Enabled = true }` pattern.
            runtime.MetaModel with { Enabled = metaModelArtifactId.HasValue },
            createdAt,
            setupCalibrationArtifactId,
            managementCalibrationArtifactId,
            metaModelArtifactId,
            description);
    }

    public static TradingPolicyProfile CreateProfile(
        BacktestRuntimeOptions runtime,
        string strategyId,
        string strategyVersion,
        TradingAgentDefinition agentDefinition,
        Guid profileId,
        int revision,
        DateTimeOffset createdAt,
        TradingPolicyProfileStatus status = TradingPolicyProfileStatus.Reviewed,
        Guid? setupCalibrationArtifactId = null,
        Guid? managementCalibrationArtifactId = null,
        Guid? metaModelArtifactId = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion);
        ArgumentNullException.ThrowIfNull(agentDefinition);
        runtime.Validate();
        agentDefinition.Validate();

        return TradingPolicyProfile.Create(
            profileId,
            revision,
            strategyId,
            strategyVersion,
            agentDefinition,
            status,
            CreateFeaturePolicy(runtime, setupCalibrationArtifactId),
            runtime.PositionSizing,
            runtime.AdaptiveRisk,
            runtime.PortfolioRisk,
            runtime.CorrelationRisk,
            runtime.TradingConditions,
            runtime.SafetyOptions,
            runtime.LegacyPositionManagement,
            runtime.ImprovedPositionManagement,
            runtime.StructuralPositionManagement,
            runtime.RegimeManagement,
            // AGENT-02: see the other CreateProfile overload for the rationale.
            runtime.ManagementCalibration with { Enabled = managementCalibrationArtifactId.HasValue },
            runtime.MetaModel with { Enabled = metaModelArtifactId.HasValue },
            createdAt,
            setupCalibrationArtifactId,
            managementCalibrationArtifactId,
            metaModelArtifactId,
            description);
    }

    // AGENT-02: SetupCalibration.Enabled must always agree with whether this specific promotion
    // actually attached a setup-calibration artifact, not with runtime.SetupCalibration's own
    // unrelated consumption toggle - same rationale as the ManagementCalibration/MetaModel
    // reconciliation in both CreateProfile overloads above.
    private static RuntimeFeaturePolicy CreateFeaturePolicy(
        BacktestRuntimeOptions runtime,
        Guid? setupCalibrationArtifactId) => new()
    {
        AnnotationOptions = runtime.AnnotationOptions,
        MarketRegimeRouting = runtime.MarketRegimeRouting,
        ValueLocationEvidence = runtime.ValueLocationEvidence,
        CurrencyStrengthEvidence = runtime.CurrencyStrengthEvidence,
        RsiBollingerSignals = runtime.RsiBollingerSignals,
        DmiConfirmationEnabled = runtime.DmiConfirmationEnabled,
        CurrencyStrength = runtime.CurrencyStrength,
        SetupCalibration = runtime.SetupCalibration with { Enabled = setupCalibrationArtifactId.HasValue },
        NeoWaveEvidence = runtime.NeoWaveEvidence
    };
}
