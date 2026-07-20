using Agent.Abstractions;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
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
            new StructuralConfluenceTradingAgentBuilder()
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
