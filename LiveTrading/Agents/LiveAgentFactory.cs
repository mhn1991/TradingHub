using Agent.Configuration;
using Agent.Factories;
using Agent.Strategies;
using RiskManager.Calibration;
using RiskManager.Conditions;
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

        // LIVE-01: the host's IStrategyDecisionPipelineFactory is a single DI singleton shared
        // across every live strategy (see LiveTradingHost/Program.cs), so it can't carry a
        // per-strategy trading-condition filter via its constructor the way the simulator (one
        // factory per session) does. policyBundle.TradingConditions is the promoted, per-strategy
        // config that was validated in simulation - pass it as the per-call override so live
        // decisions apply the same session/spread/rollover gates the simulator enforced, instead
        // of silently skipping them. No live IEconomicEventProvider exists yet, so a promoted
        // policy with EconomicEventFilterEnabled=true correctly fails closed here (throws) rather
        // than silently ignoring the setting - see TradingConditionFilter's constructor.
        var tradingConditions = new TradingConditionFilter(policyBundle.TradingConditions, events: null);

        return pipelineFactory.Create(
            definition,
            policyBundle.FeaturePolicy,
            setupCalibration,
            metaModel,
            policyBundle.AccountSafety,
            tradingConditionsOverride: tradingConditions);
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
