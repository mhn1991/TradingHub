using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Models;
using TradeManager;

namespace Simulator.Experiments.Models;

public enum ExperimentCalibrationMode
{
    Disabled,
    ReuseSpecifiedArtifacts,
    TrainFreshAndUseForHeldOutEvaluation,
    TrainFreshPendingReviewOnly
}

public sealed record CalibrationExperimentPolicy
{
    public ExperimentCalibrationMode Mode { get; init; } =
        ExperimentCalibrationMode.TrainFreshAndUseForHeldOutEvaluation;
    public int InternalFolds { get; init; } = 5;
    public int InternalEmbargoHours { get; init; } = 24;
    public IReadOnlyList<Guid> ArtifactIds { get; init; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || InternalFolds < 3 || InternalEmbargoHours < 0)
            throw new ArgumentException("Calibration experiment policy is invalid.");
        if (Mode == ExperimentCalibrationMode.ReuseSpecifiedArtifacts && ArtifactIds.Count == 0)
            throw new ArgumentException("Artifact reuse requires at least one specified artifact.");
    }
}

public sealed record BacktestRuntimeProfile
{
    public BacktestRuntimeOptions Options { get; init; } = new();
}

public sealed record SimulationStrategyProfile
{
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public required string Name { get; init; }
    public required TradingAgentDefinition Agent { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public ChartAnnotationOptions Analysis { get; init; } = new();
    public BacktestRuntimeProfile Runtime { get; init; } = new();
    public PositionManagementOptions Management { get; init; } = new();
    public CalibrationExperimentPolicy Calibration { get; init; } = new();
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string ContentHash { get; init; }

    public void Validate()
    {
        if (ProfileId == Guid.Empty || Revision < 1 || string.IsNullOrWhiteSpace(Name) || Instrument.IsEmpty)
            throw new ArgumentException("Profile identity, revision, name, and instrument are required.");
        Agent.Validate();
        Analysis.Validate();
        Runtime.Options.Validate();
        Management.Validate();
        Calibration.Validate();
        ValidateManagementTimeframeAlignment(Agent, Management, Runtime.Options.RegimeManagement);
        if (Tags is null || Tags.Any(string.IsNullOrWhiteSpace) ||
            Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tags.Count)
            throw new ArgumentException("Profile tags must be non-empty and unique.");
        string computed = ComputeContentHash(this with { ContentHash = string.Empty });
        if (!string.Equals(ContentHash, computed, StringComparison.Ordinal))
            throw new ArgumentException("ContentHash does not match the resolved profile.");
    }

    /// <summary>
    /// An explicit Fast/Main/Thesis structure-management interval is only accepted if it is
    /// actually one of this agent's own evaluated timeframes. Trade management must be checked on
    /// the same timeframes that justified the entry - a hardcoded value that happens to look
    /// reasonable but silently drifts from the agent's real timeframe stack (e.g. a thesis-level
    /// invalidation check running on 1H when the agent's own trend read is 2H) is exactly the bug
    /// class this guards against. Omitting a field lets StrategySimulationSession derive it
    /// automatically from the agent instead.
    ///
    /// Also recurses into RegimeManagement.Profiles: each regime can carry its own complete
    /// PositionManagementOptions override, which would otherwise bypass this check entirely and
    /// reintroduce the exact same drift risk the moment regime-based management switching is
    /// enabled (dormant today - every current profile has RegimeManagement.Enabled = false - but
    /// nothing before this stopped a future regime profile from carrying a stale value).
    /// </summary>
    private static void ValidateManagementTimeframeAlignment(
        TradingAgentDefinition agent,
        PositionManagementOptions management,
        RegimeManagementOptions regimeManagement)
    {
        ValidateManagementTimeframeAlignment(agent, management, "Management");
        foreach (PositionManagementProfile regimeProfile in regimeManagement.Profiles)
        {
            ValidateManagementTimeframeAlignment(
                agent,
                regimeProfile.Options,
                $"Runtime.Options.RegimeManagement.Profiles['{regimeProfile.ProfileId}'].Options");
        }
    }

    private static void ValidateManagementTimeframeAlignment(
        TradingAgentDefinition agent,
        PositionManagementOptions options,
        string label)
    {
        if (options.FastStructureInterval is null &&
            options.MainStructureInterval is null &&
            options.ThesisInterval is null)
        {
            return;
        }

        List<BarInterval> ladder = agent.RequiredIntervals
            .Distinct()
            .OrderBy(BarIntervalParser.ApproximateSeconds)
            .ToList();

        if (options.FastStructureInterval is BarInterval fast && fast != agent.TriggerInterval)
        {
            throw new ArgumentException(
                $"{label}.FastStructureInterval ({fast}) does not match this agent's own " +
                $"trigger/entry interval ({agent.TriggerInterval}). Omit FastStructureInterval to " +
                "derive it automatically instead of hardcoding a value that can drift.");
        }
        if (options.ThesisInterval is BarInterval thesis && ladder.Count > 0 && thesis != ladder[^1])
        {
            throw new ArgumentException(
                $"{label}.ThesisInterval ({thesis}) does not match this agent's own " +
                $"coarsest/trend interval ({ladder[^1]}). Omit ThesisInterval to derive it " +
                "automatically instead of hardcoding a value that can drift.");
        }
        if (options.MainStructureInterval is BarInterval main && !ladder.Contains(main))
        {
            throw new ArgumentException(
                $"{label}.MainStructureInterval ({main}) is not one of this agent's own " +
                $"evaluated intervals ({string.Join(", ", ladder)}). Omit MainStructureInterval to " +
                "derive it automatically instead of hardcoding a value that can drift.");
        }
    }

    public void ValidateForExecution()
    {
        Validate();
        if (Agent.Kind != TradingAgentKind.StructuralConfluence)
            return;

        StructuralConfluenceStrategyOptions strategy = Agent.StructuralConfluence!;
        bool requiresLiquidity = strategy.LiquiditySweepReversal.Enabled ||
            strategy.LiquidityBreakRetest.Enabled;
        if (requiresLiquidity && !Analysis.Liquidity.Enabled)
        {
            throw new ArgumentException(
                "Liquidity analysis must be enabled when the liquidity-sweep or accepted-break/retest playbook is enabled.");
        }

        bool requiresSupplyDemand = strategy.SupplyDemandPullback.Enabled ||
            (strategy.LiquiditySweepReversal.Enabled &&
             strategy.LiquiditySweepReversal.SupplyDemandConfluence != StructuralConfluenceRequirement.Disabled);
        if (requiresSupplyDemand && !Analysis.SupplyDemand.Enabled)
        {
            throw new ArgumentException(
                "Supply/demand analysis must be enabled for the supply/demand-pullback playbook or liquidity-sweep supply/demand confluence.");
        }
    }

    public static SimulationStrategyProfile Create(SimulationStrategyProfile draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var profile = draft with { ContentHash = ComputeContentHash(draft with { ContentHash = string.Empty }) };
        profile.ValidateForExecution();
        return profile;
    }

    public static string ComputeContentHash(SimulationStrategyProfile profile)
    {
        MarketRegimePolicyOptions routing = profile.Runtime.Options.MarketRegimeRouting;
        MarketRegimePolicyOptions normalizedRouting = routing.Policies is null
            ? routing
            : routing with
            {
                Policies = routing.Policies
                    .OrderBy(entry => entry.Key)
                    .ToDictionary(entry => entry.Key, entry => entry.Value)
            };
        BacktestRuntimeProfile normalizedRuntime = profile.Runtime with
        {
            Options = profile.Runtime.Options with { MarketRegimeRouting = normalizedRouting }
        };
        JsonObject content = JsonSerializer.SerializeToNode(new
        {
            profile.Name,
            profile.Agent,
            profile.Instrument,
            profile.Analysis,
            Runtime = normalizedRuntime,
            profile.Management,
            profile.Calibration,
            Tags = profile.Tags.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
        })!.AsObject();

        StructuralConfluenceStrategyOptions? structural = profile.Agent.StructuralConfluence;
        if (profile.Agent.Kind == TradingAgentKind.StructuralConfluence &&
            structural is
            {
                StrategyVersion: "structural-confluence-v1",
                AdaptiveTargetManagement.Enabled: false
            })
        {
            content[nameof(profile.Agent)]![nameof(TradingAgentDefinition.StructuralConfluence)]!
                .AsObject()
                .Remove(nameof(StructuralConfluenceStrategyOptions.AdaptiveTargetManagement));
        }

        string json = content.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

public sealed record SimulationStrategyProfileSnapshot
{
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public required string ContentHash { get; init; }
    public required SimulationStrategyProfile ResolvedProfile { get; init; }

    public static SimulationStrategyProfileSnapshot From(SimulationStrategyProfile profile)
    {
        profile.ValidateForExecution();
        return new SimulationStrategyProfileSnapshot
        {
            ProfileId = profile.ProfileId,
            Revision = profile.Revision,
            ContentHash = profile.ContentHash,
            ResolvedProfile = profile
        };
    }
}

public sealed record SimulationExperimentProfileRun
{
    public required Guid ProfileRunId { get; init; }
    public required SimulationStrategyProfileSnapshot Profile { get; init; }
    public string? BaselineProfileRunId { get; init; }
}
