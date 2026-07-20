using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;

namespace Agent.Configuration;

public sealed record TradingAgentDefinition
{
    public required TradingAgentKind Kind { get; init; }
    public ProgressiveStrategyOptions? Progressive { get; init; }
    public StructuralConfluenceStrategyOptions? StructuralConfluence { get; init; }

    /// <summary>Every timeframe this agent's own configuration evaluates, regardless of kind.</summary>
    public IReadOnlySet<BarInterval> RequiredIntervals => Kind switch
    {
        TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive =>
            new HashSet<BarInterval>(Progressive!.AllRequiredIntervals),
        TradingAgentKind.StructuralConfluence => StructuralConfluence!.RequiredIntervals,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    /// <summary>This agent's own finest (entry/trigger) evaluated timeframe.</summary>
    public BarInterval TriggerInterval => Kind switch
    {
        TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive => Progressive!.EntryInterval,
        TradingAgentKind.StructuralConfluence => StructuralConfluence!.TriggerInterval,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(Kind));

        switch (Kind)
        {
            case TradingAgentKind.LegacyProgressive:
            case TradingAgentKind.ImprovedProgressive:
                if (Progressive is null || StructuralConfluence is not null)
                    throw new ArgumentException(
                        $"{Kind} requires Progressive options and forbids StructuralConfluence options.");
                Progressive.Validate();
                break;
            case TradingAgentKind.StructuralConfluence:
                if (StructuralConfluence is null || Progressive is not null)
                    throw new ArgumentException(
                        "StructuralConfluence requires structural options and forbids Progressive options.");
                StructuralConfluence.Validate();
                break;
        }
    }

    public AgentDefinition ToAgentDefinition() => Kind switch
    {
        TradingAgentKind.LegacyProgressive => AgentDefinition.FromProgressive(
            ProgressiveAgentKind.Legacy, Progressive!),
        TradingAgentKind.ImprovedProgressive => AgentDefinition.FromProgressive(
            ProgressiveAgentKind.Improved, Progressive!),
        TradingAgentKind.StructuralConfluence => AgentDefinition.FromStructuralConfluence(StructuralConfluence!),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    public static TradingAgentDefinition FromAgentDefinition(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return definition.AgentTypeId switch
        {
            TradingAgentTypeIds.LegacyProgressive => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.LegacyProgressive,
                Progressive = definition.ReadProgressiveOptions()
            },
            TradingAgentTypeIds.ImprovedProgressive => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.ImprovedProgressive,
                Progressive = definition.ReadProgressiveOptions()
            },
            TradingAgentTypeIds.StructuralConfluence => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = definition.ReadStructuralOptions()
            },
            _ => throw new ArgumentException($"Unknown Agent type '{definition.AgentTypeId}'.", nameof(definition))
        };
    }
}
