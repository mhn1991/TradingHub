using Agent.Configuration;
using Agent.Factories;
using Agent.Strategies;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradingCore.Pipeline;
using TradingPolicies;

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
        ResolvedAgentPackage package,
        IStrategyDecisionPipelineFactory pipelineFactory)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Create(
            package.StrategyId,
            TradingAgentDefinition.FromAgentDefinition(package.AgentDefinition),
            LiveTradingPolicyBundle.FromResolvedPackage(package),
            package.SetupCalibration,
            package.MetaModel,
            pipelineFactory);
    }

    public static StrategyDecisionRuntime Create(
        string strategyId,
        TradingAgentDefinition agentDefinition,
        LiveTradingPolicyBundle policyBundle,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        IStrategyDecisionPipelineFactory pipelineFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(agentDefinition);
        ArgumentNullException.ThrowIfNull(policyBundle);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        agentDefinition.Validate();
        ProgressiveStrategyOptions? progressive = agentDefinition.Progressive;
        if (progressive is not null && progressive.NeoWaveEvidence != policyBundle.FeaturePolicy.NeoWaveEvidence)
        {
            throw new ArgumentException(
                "Agent NEoWave evidence must match the promoted feature policy.",
                nameof(agentDefinition));
        }
        if (progressive?.NeoWaveEvidence.Enabled == true && !policyBundle.FeaturePolicy.AnnotationOptions.NeoWave.Enabled)
        {
            throw new ArgumentException(
                "NEoWave evidence requires the promoted analysis policy to enable NEoWave.",
                nameof(agentDefinition));
        }

        var definition = new StrategyRuntimeDefinition
        {
            StrategyId = strategyId,
            StrategyVersion = policyBundle.StrategyVersion,
            Agent = TradingAgentFactory.Create(agentDefinition)
        };

        return pipelineFactory.Create(
            definition, policyBundle.FeaturePolicy, setupCalibration, metaModel, policyBundle.AccountSafety);
    }

    public static StrategyDecisionRuntime Create(
        string strategyId,
        ProgressiveAgentKind agentKind,
        ProgressiveStrategyOptions agentOptions,
        LiveTradingPolicyBundle policyBundle,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        IStrategyDecisionPipelineFactory pipelineFactory) => Create(
            strategyId,
            new TradingAgentDefinition
            {
                Kind = agentKind == ProgressiveAgentKind.Legacy
                    ? TradingAgentKind.LegacyProgressive
                    : TradingAgentKind.ImprovedProgressive,
                Progressive = agentOptions
            },
            policyBundle,
            setupCalibration,
            metaModel,
            pipelineFactory);
}
