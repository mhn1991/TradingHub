using Agent.Strategies;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>
/// Builds one live strategy runtime the exact same way <c>StrategySimulationSession.Create</c>
/// does: same <see cref="ProgressiveAgentFactory"/>, same <see cref="IStrategyDecisionPipelineFactory"/>.
/// Deliberately takes an already-resolved <see cref="LiveTradingPolicyBundle"/> plus its resolved
/// calibration artifact/meta-model (not the raw config DTOs those were built from) - this keeps
/// LiveTrading decoupled from LiveTradingHost's configuration layer, which owns config binding and
/// artifact resolution (see <c>LiveTradingHost.Configuration.LivePolicyBundleFactory</c>).
/// </summary>
public static class LiveAgentFactory
{
    public static StrategyDecisionRuntime Create(
        string strategyId,
        ProgressiveAgentKind agentKind,
        ProgressiveStrategyOptions agentOptions,
        LiveTradingPolicyBundle policyBundle,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        IStrategyDecisionPipelineFactory pipelineFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(agentOptions);
        ArgumentNullException.ThrowIfNull(policyBundle);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        agentOptions.Validate();
        if (agentOptions.NeoWaveEvidence != policyBundle.FeaturePolicy.NeoWaveEvidence)
        {
            throw new ArgumentException(
                "Agent NEoWave evidence must match the promoted feature policy.",
                nameof(agentOptions));
        }
        if (agentOptions.NeoWaveEvidence.Enabled && !policyBundle.FeaturePolicy.AnnotationOptions.NeoWave.Enabled)
        {
            throw new ArgumentException(
                "NEoWave evidence requires the promoted analysis policy to enable NEoWave.",
                nameof(agentOptions));
        }

        var definition = new StrategyRuntimeDefinition
        {
            StrategyId = strategyId,
            StrategyVersion = policyBundle.StrategyVersion,
            Agent = ProgressiveAgentFactory.Create(agentKind, agentOptions)
        };

        return pipelineFactory.Create(
            definition, policyBundle.FeaturePolicy, setupCalibration, metaModel, policyBundle.AccountSafety);
    }
}
