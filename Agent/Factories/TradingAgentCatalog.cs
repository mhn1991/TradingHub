using Agent.Abstractions;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.BreakoutDetector;
using Agent.Strategies.DivergenceReversal;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.TradingClassification;
using Agent.Strategies.Alfonso;
using Agent.Strategies.TrendTactical;
using TradingClassifier.Features;
using TradingClassifier.Models;
using Brokers.Models;

namespace Agent.Factories;

public enum AgentDeploymentMode
{
    ObserveOnly,
    Shadow,
    ManualApproval,
    Automatic
}

public sealed record AgentDescriptor
{
    public required string AgentTypeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required int DefinitionSchemaVersion { get; init; }
    public required IReadOnlySet<string> RequiredFeatureCapabilities { get; init; }
    public required AgentExitManagementMode ExitManagementMode { get; init; }
    public required IReadOnlySet<AgentDeploymentMode> SupportedDeploymentModes { get; init; }
    public required bool SupportsSetupCalibration { get; init; }
    public required bool SupportsMetaModel { get; init; }
    public required bool SupportsManagementCalibration { get; init; }
    public required IReadOnlySet<BarInterval> DefaultIntervals { get; init; }
    public required bool AutomaticDemoCertified { get; init; }
    public required bool AutomaticLiveCertified { get; init; }
}

public interface ITradingAgentBuilder
{
    string AgentTypeId { get; }
    AgentDescriptor Describe();
    ITradingAgent Build(AgentDefinition definition);
}

public interface ITradingAgentCatalog
{
    IReadOnlyList<AgentDescriptor> List();
    AgentDescriptor Get(string agentTypeId);
    ITradingAgent Create(AgentDefinition definition);
}

public sealed class TradingAgentCatalog : ITradingAgentCatalog
{
    private readonly IReadOnlyDictionary<string, ITradingAgentBuilder> _builders;
    private readonly IReadOnlyList<AgentDescriptor> _descriptors;

    public TradingAgentCatalog(IEnumerable<ITradingAgentBuilder> builders)
    {
        ArgumentNullException.ThrowIfNull(builders);
        ITradingAgentBuilder[] registered = builders.ToArray();
        IGrouping<string, ITradingAgentBuilder>? duplicate = registered
            .GroupBy(item => item.AgentTypeId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate Agent builder '{duplicate.Key}'.");

        _builders = registered.ToDictionary(item => item.AgentTypeId, StringComparer.Ordinal);
        _descriptors = registered
            .Select(item => item.Describe())
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public static TradingAgentCatalog CreateDefault() => new(
        [
            new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Legacy),
            new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Improved),
            new StructuralConfluenceTradingAgentBuilder(),
            new DivergenceReversalTradingAgentBuilder(),
            new BreakoutDetectorTradingAgentBuilder(),
            new TradingClassificationTradingAgentBuilder(),
            new AlfonsoTradingAgentBuilder(),
            new TrendTacticalTradingAgentBuilder()
        ]);

    public IReadOnlyList<AgentDescriptor> List() => _descriptors;

    public AgentDescriptor Get(string agentTypeId)
    {
        string canonical = TradingAgentTypeIds.Normalize(agentTypeId);
        return GetBuilder(canonical).Describe();
    }

    public ITradingAgent Create(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return GetBuilder(definition.AgentTypeId).Build(definition);
    }

    private ITradingAgentBuilder GetBuilder(string canonicalId) =>
        _builders.TryGetValue(canonicalId, out ITradingAgentBuilder? builder)
            ? builder
            : throw new KeyNotFoundException($"Unknown Agent type '{canonicalId}'.");
}

public sealed class ProgressiveTradingAgentBuilder(ProgressiveAgentKind kind) : ITradingAgentBuilder
{
    public string AgentTypeId => kind == ProgressiveAgentKind.Legacy
        ? TradingAgentTypeIds.LegacyProgressive
        : TradingAgentTypeIds.ImprovedProgressive;

    public AgentDescriptor Describe()
    {
        ProgressiveStrategyOptions defaults = new();
        bool improved = kind == ProgressiveAgentKind.Improved;
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = improved ? "Improved Progressive" : "Legacy Progressive",
            Description = improved
                ? "Progressive multi-timeframe strategy with bracket exits."
                : "Compatibility progressive strategy with protective-stop exits.",
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles", "chart-annotations"]),
            ExitManagementMode = improved
                ? AgentExitManagementMode.Bracket
                : AgentExitManagementMode.ProtectiveStopAndStrategyExit,
            SupportedDeploymentModes = AllModes(),
            SupportsSetupCalibration = true,
            SupportsMetaModel = true,
            SupportsManagementCalibration = true,
            DefaultIntervals = defaults.AllRequiredIntervals.ToHashSet(),
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));
        return ProgressiveAgentFactory.Create(kind, definition.ReadProgressiveOptions());
    }

    private static IReadOnlySet<AgentDeploymentMode> AllModes() => new HashSet<AgentDeploymentMode>(
        [AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow, AgentDeploymentMode.ManualApproval,
            AgentDeploymentMode.Automatic]);
}

public sealed class StructuralConfluenceTradingAgentBuilder : ITradingAgentBuilder
{
    public string AgentTypeId => TradingAgentTypeIds.StructuralConfluence;

    public AgentDescriptor Describe()
    {
        StructuralConfluenceStrategyOptions defaults = new();
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Structural Confluence",
            Description = "Stateful liquidity, supply/demand and break/retest confluence playbooks.",
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(
                ["completed-candles", "chart-annotations", "liquidity", "supply-demand"]),
            ExitManagementMode = AgentExitManagementMode.Bracket,
            SupportedDeploymentModes = new HashSet<AgentDeploymentMode>(
                [AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow, AgentDeploymentMode.ManualApproval,
                    AgentDeploymentMode.Automatic]),
            SupportsSetupCalibration = true,
            SupportsMetaModel = true,
            SupportsManagementCalibration = true,
            DefaultIntervals = defaults.RequiredIntervals,
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));
        return new StructuralConfluenceAgent(definition.ReadStructuralOptions());
    }
}

public sealed class DivergenceReversalTradingAgentBuilder : ITradingAgentBuilder
{
    public string AgentTypeId => TradingAgentTypeIds.DivergenceReversal;

    public AgentDescriptor Describe()
    {
        DivergenceReversalStrategyOptions defaults = new()
        {
            MonitoredIntervals = [BarInterval.Minutes(30), BarInterval.Minutes(15)],
            ConfirmationIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(1)]
        };
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Divergence Reversal",
            Description = "Bollinger/RSI/StochRSI extreme readings, classified as reversal or breakout " +
                "via StochRSI-fast divergence, with lower-timeframe confirmation of partial readings.",
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles", "chart-annotations"]),
            ExitManagementMode = AgentExitManagementMode.ProtectiveStopAndStrategyExit,
            SupportedDeploymentModes = new HashSet<AgentDeploymentMode>(
                [AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow]),
            SupportsSetupCalibration = false,
            SupportsMetaModel = false,
            SupportsManagementCalibration = false,
            DefaultIntervals = new HashSet<BarInterval>(
                [.. defaults.MonitoredIntervals, .. defaults.ConfirmationIntervals]),
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));
        return new DivergenceReversalAgent(definition.ReadDivergenceReversalOptions());
    }
}

public sealed class BreakoutDetectorTradingAgentBuilder : ITradingAgentBuilder
{
    public string AgentTypeId => TradingAgentTypeIds.BreakoutDetector;

    public AgentDescriptor Describe()
    {
        BreakoutDetectorStrategyOptions defaults = new();
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Breakout Detector",
            Description = "Range/consolidation breakout on the trigger timeframe, qualified by a " +
                "higher-timeframe context, entered with a bracket. Detection logic is not " +
                "implemented yet - this agent currently observes only.",
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles", "chart-annotations"]),
            ExitManagementMode = AgentExitManagementMode.Bracket,
            // Deliberately no ManualApproval/Automatic while the strategy is a scaffold: an agent
            // that cannot yet produce an entry has nothing to approve or automate.
            SupportedDeploymentModes = new HashSet<AgentDeploymentMode>(
                [AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow]),
            SupportsSetupCalibration = false,
            SupportsMetaModel = false,
            SupportsManagementCalibration = false,
            DefaultIntervals = defaults.RequiredIntervals,
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));
        return new BreakoutDetectorAgent(definition.ReadBreakoutDetectorOptions());
    }
}

/// <summary>
/// Builds <see cref="TradingClassificationAgent"/> from a saved definition.
/// <para>
/// The agent needs a trained <see cref="ITradingModel"/>, and models are produced by
/// <c>TradingClassifier.ML</c> - a Microsoft.ML dependency that must not enter this assembly's
/// AOT-published graph. So the model arrives through <paramref name="modelResolver"/>, supplied by
/// whichever host has already taken that dependency. With no resolver the builder falls back to
/// <see cref="NoTradeModel"/>: the agent still resolves, validates and warms up, but never
/// signals, and <see cref="Describe"/> restricts it to observe-only deployment to say so.
/// </para>
/// </summary>
public sealed class TradingClassificationTradingAgentBuilder(
    Func<TradingClassificationStrategyOptions, ITradingModel>? modelResolver = null) : ITradingAgentBuilder
{
    private readonly Func<TradingClassificationStrategyOptions, ITradingModel> _modelResolver =
        modelResolver ?? (options => new NoTradeModel(FeatureSchema.Create(options.Classifier).Names));

    public string AgentTypeId => TradingAgentTypeIds.TradingClassification;

    public AgentDescriptor Describe()
    {
        TradingClassificationStrategyOptions defaults = new();
        bool trained = modelResolver is not null;
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Trading Classification",
            Description = "LightGBM three-class (SELL/NO_TRADE/BUY) classifier over OHLC-derived " +
                "and indicator features, entered with an ATR bracket when the winning class " +
                "clears its probability threshold. " +
                (trained
                    ? "A trained model is wired in."
                    : "No trained model is wired in, so this instance observes only."),
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles", "chart-annotations"]),
            ExitManagementMode = AgentExitManagementMode.Bracket,
            // Without a model there is nothing to approve or automate; with one, the blueprint's
            // section 36 bar (stable walk-forward performance after costs) still has to be cleared
            // before anything beyond shadow deployment is justified.
            SupportedDeploymentModes = trained
                ? new HashSet<AgentDeploymentMode>(
                    [AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow, AgentDeploymentMode.ManualApproval])
                : new HashSet<AgentDeploymentMode>([AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow]),
            SupportsSetupCalibration = false,
            SupportsMetaModel = true,
            SupportsManagementCalibration = false,
            DefaultIntervals = defaults.RequiredIntervals,
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));

        TradingClassificationStrategyOptions options = definition.ReadTradingClassificationOptions();
        return new TradingClassificationAgent(options, _modelResolver(options));
    }
}


/// <summary>
/// Builds <see cref="TrendTacticalAgent"/> — the 2H trend gate composed with the classifier for
/// entry timing (<c>Books/Design — Trend-Gated Tactical Agent.md</c>).
/// <para>
/// Same model-resolver arrangement as
/// <see cref="TradingClassificationTradingAgentBuilder"/>: Microsoft.ML must not enter this
/// assembly's AOT graph, so a trained <see cref="ITradingModel"/> arrives from the host. Without one
/// the agent falls back to <see cref="NoTradeModel"/> — it still resolves, validates and replays the
/// trend detector, but never signals.
/// </para>
/// </summary>
public sealed class TrendTacticalTradingAgentBuilder(
    Func<TrendTacticalStrategyOptions, ITradingModel>? modelResolver = null,
    Func<TrendTacticalStrategyOptions, IStopPlacementModel?>? stopModelResolver = null)
    : ITradingAgentBuilder
{
    private readonly Func<TrendTacticalStrategyOptions, ITradingModel> _modelResolver =
        modelResolver ?? (options => new NoTradeModel(FeatureSchema.Create(options.Classifier).Names));

    // Null resolver means no stop model: the agent then keeps its structural stop rather than
    // silently behaving as though a prediction were available.
    private readonly Func<TrendTacticalStrategyOptions, IStopPlacementModel?> _stopModelResolver =
        stopModelResolver ?? (_ => null);

    public string AgentTypeId => TradingAgentTypeIds.TrendTactical;

    public AgentDescriptor Describe()
    {
        TrendTacticalStrategyOptions defaults = new();
        bool trained = modelResolver is not null;
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Trend-Gated Tactical",
            Description = "2H TrendStatistics supplies direction, phase and progress; the classifier " +
                "only times entries in the trend's own direction, with the confidence bar rising as " +
                "the trend matures. Protective stop from structure, exit driven by trend state. " +
                (trained
                    ? "A trained model is wired in."
                    : "No trained model is wired in, so this instance observes only."),
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles", "chart-annotations"]),
            // NOT Bracket. §3.19 measured a fixed ATR target as reachable by under 10% of trades, and
            // declaring Bracket also makes PreTradeRiskManager hard-floor reward:risk at 1.5 and
            // silently reject everything below it.
            ExitManagementMode = AgentExitManagementMode.ProtectiveStopAndStrategyExit,
            // Deliberately observe-only until the §5 validation ladder in the design document has
            // run: rung A must first show that §2.18's PF 1.694 survives leaving the research
            // harness, and rung C must show the model beats a simple trend-alignment rule.
            SupportedDeploymentModes =
                new HashSet<AgentDeploymentMode>([AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow]),
            SupportsSetupCalibration = false,
            SupportsMetaModel = true,
            SupportsManagementCalibration = false,
            DefaultIntervals = defaults.RequiredIntervals,
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));

        TrendTacticalStrategyOptions options = definition.ReadTrendTacticalOptions();
        return new TrendTacticalAgent(options, _modelResolver(options), _stopModelResolver(options));
    }
}

/// <summary>
/// Builds <see cref="AlfonsoAgent"/> - the Set and Forget supply and demand method from
/// <c>Books/alfonso</c>, implemented rule for rule.
/// <para>
/// No model resolver, unlike the classifier-backed builders: this agent has no learned component at
/// all. Every threshold is stated in the course and lives in <see cref="AlfonsoStrategyOptions"/>,
/// which makes it directly comparable with rung A - the other purely rule-based strategy in this
/// repo - rather than with anything whose behaviour depends on a trained artifact being present.
/// </para>
/// </summary>
public sealed class AlfonsoTradingAgentBuilder : ITradingAgentBuilder
{
    public string AgentTypeId => TradingAgentTypeIds.Alfonso;

    public AgentDescriptor Describe()
    {
        AlfonsoStrategyOptions defaults = new();
        return new AgentDescriptor
        {
            AgentTypeId = AgentTypeId,
            DisplayName = "Set and Forget supply/demand",
            Description = "Three timeframes decide direction and permission; entries rest at a " +
                "supply or demand zone's proximal line with protection beyond its distal and a " +
                "fixed 3:1 target. Purely rule-based, with no learned component.",
            DefinitionSchemaVersion = AgentDefinition.CurrentSchemaVersion,
            RequiredFeatureCapabilities = new HashSet<string>(["completed-candles"]),
            // The method IS a bracket: entry, protection and target are decided together and never
            // revised. Module 11 forbids the alternative outright - "Do not move the stop loss to
            // breakeven. It's either a win or a loss." The fixed 3:1 also clears the platform's 1.5
            // reward:risk floor comfortably, which is what makes Bracket safe to declare here.
            ExitManagementMode = AgentExitManagementMode.Bracket,
            // Observe-only until the ladder has run against rung A. Nothing about this agent has
            // been measured on out-of-sample data yet.
            SupportedDeploymentModes =
                new HashSet<AgentDeploymentMode>([AgentDeploymentMode.ObserveOnly, AgentDeploymentMode.Shadow]),
            SupportsSetupCalibration = false,
            SupportsMetaModel = false,
            SupportsManagementCalibration = false,
            DefaultIntervals = defaults.RequiredIntervals,
            AutomaticDemoCertified = false,
            AutomaticLiveCertified = false
        };
    }

    public ITradingAgent Build(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.AgentTypeId, AgentTypeId, StringComparison.Ordinal))
            throw new ArgumentException("Agent definition was routed to the wrong builder.", nameof(definition));

        AlfonsoStrategyOptions alfonsoOptions = definition.ReadAlfonsoOptions();
        return new AlfonsoAgent(
            alfonsoOptions,
            string.IsNullOrWhiteSpace(alfonsoOptions.CandidateLogPath)
                ? null
                : AlfonsoCandidateLog.ToFile(alfonsoOptions.CandidateLogPath));
    }
}
