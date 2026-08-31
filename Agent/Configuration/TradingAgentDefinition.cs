using Agent.Strategies;
using Agent.Strategies.Alfonso;
using Agent.Strategies.BreakoutDetector;
using Agent.Strategies.TrendTactical;
using Agent.Strategies.DivergenceReversal;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.TradingClassification;
using Brokers.Models;

namespace Agent.Configuration;

public sealed record TradingAgentDefinition
{
    public required TradingAgentKind Kind { get; init; }
    public ProgressiveStrategyOptions? Progressive { get; init; }
    public StructuralConfluenceStrategyOptions? StructuralConfluence { get; init; }
    public DivergenceReversalStrategyOptions? DivergenceReversal { get; init; }
    public BreakoutDetectorStrategyOptions? BreakoutDetector { get; init; }

    public TrendTacticalStrategyOptions? TrendTactical { get; init; }
    public TradingClassificationStrategyOptions? TradingClassification { get; init; }
    public AlfonsoStrategyOptions? Alfonso { get; init; }

    /// <summary>Every timeframe this agent's own configuration evaluates, regardless of kind.</summary>
    public IReadOnlySet<BarInterval> RequiredIntervals => Kind switch
    {
        TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive =>
            new HashSet<BarInterval>(Progressive!.AllRequiredIntervals),
        TradingAgentKind.StructuralConfluence => StructuralConfluence!.RequiredIntervals,
        TradingAgentKind.DivergenceReversal => new HashSet<BarInterval>(DivergenceReversal!.MonitoredIntervals),
        TradingAgentKind.BreakoutDetector => BreakoutDetector!.RequiredIntervals,
        TradingAgentKind.TrendTactical => TrendTactical!.RequiredIntervals,
        TradingAgentKind.TradingClassification => TradingClassification!.RequiredIntervals,
        TradingAgentKind.Alfonso => Alfonso!.RequiredIntervals,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    /// <summary>This agent's own finest (entry/trigger) evaluated timeframe.</summary>
    public BarInterval TriggerInterval => Kind switch
    {
        TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive => Progressive!.EntryInterval,
        TradingAgentKind.StructuralConfluence => StructuralConfluence!.TriggerInterval,
        TradingAgentKind.DivergenceReversal => DivergenceReversal!.MonitoredIntervals
            .OrderBy(interval => interval, Comparer<BarInterval>.Create(BarIntervalParser.CompareDuration))
            .First(),
        // NOT BreakoutDetector.TriggerInterval: since the three-timeframe change that option names
        // the DETECTION timeframe (5m), while the finest evaluated one - and the cadence
        // BreakoutDetectorAgent.TriggerInterval reports - is the 1m confirmation. Using the
        // similarly-named option here would schedule the agent on 5m and silently reduce the 1m
        // confirmation to whatever happened to be true on the 5m close.
        TradingAgentKind.BreakoutDetector => BreakoutDetector!.ConfirmationInterval,
        // The tactical trigger, not the 2H trend interval: the trend layer only gates,
        // it is the trigger timeframe that drives evaluation.
        TradingAgentKind.TrendTactical => TrendTactical!.TriggerInterval,
        // Single-timeframe by construction: the model was trained on one interval's features and
        // cannot be asked about another.
        TradingAgentKind.TradingClassification => TradingClassification!.SignalInterval,
        // The execution timeframe. The two above it only gate; this one drives evaluation.
        TradingAgentKind.Alfonso => Alfonso!.LowerInterval,
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
                if (Progressive is null || StructuralConfluence is not null || DivergenceReversal is not null ||
                    BreakoutDetector is not null || TradingClassification is not null ||
                    TrendTactical is not null)
                {
                    throw new ArgumentException(
                        $"{Kind} requires Progressive options and forbids StructuralConfluence/" +
                        "DivergenceReversal/BreakoutDetector/TradingClassification/TrendTactical options.");
                }
                Progressive.Validate();
                break;
            case TradingAgentKind.StructuralConfluence:
                if (StructuralConfluence is null || Progressive is not null || DivergenceReversal is not null ||
                    BreakoutDetector is not null || TradingClassification is not null ||
                    TrendTactical is not null)
                {
                    throw new ArgumentException(
                        "StructuralConfluence requires structural options and forbids Progressive/" +
                        "DivergenceReversal/BreakoutDetector/TradingClassification/TrendTactical options.");
                }
                StructuralConfluence.Validate();
                break;
            case TradingAgentKind.DivergenceReversal:
                if (DivergenceReversal is null || Progressive is not null || StructuralConfluence is not null ||
                    BreakoutDetector is not null || TradingClassification is not null ||
                    TrendTactical is not null)
                {
                    throw new ArgumentException(
                        "DivergenceReversal requires divergence-reversal options and forbids Progressive/" +
                        "StructuralConfluence/BreakoutDetector/TradingClassification/TrendTactical options.");
                }
                DivergenceReversal.Validate();
                break;
            case TradingAgentKind.BreakoutDetector:
                if (BreakoutDetector is null || Progressive is not null || StructuralConfluence is not null ||
                    DivergenceReversal is not null || TradingClassification is not null ||
                    TrendTactical is not null)
                {
                    throw new ArgumentException(
                        "BreakoutDetector requires breakout-detector options and forbids Progressive/" +
                        "StructuralConfluence/DivergenceReversal/TradingClassification/TrendTactical options.");
                }
                BreakoutDetector.Validate();
                break;
            case TradingAgentKind.TradingClassification:
                if (TradingClassification is null || Progressive is not null || StructuralConfluence is not null ||
                    DivergenceReversal is not null || BreakoutDetector is not null ||
                    TrendTactical is not null)
                {
                    throw new ArgumentException(
                        "TradingClassification requires trading-classification options and forbids Progressive/" +
                        "StructuralConfluence/DivergenceReversal/BreakoutDetector/TrendTactical options.");
                }
                TradingClassification.Validate();
                break;
            case TradingAgentKind.TrendTactical:
                if (TrendTactical is null || Progressive is not null || StructuralConfluence is not null ||
                    DivergenceReversal is not null || BreakoutDetector is not null ||
                    TradingClassification is not null)
                {
                    throw new ArgumentException(
                        "TrendTactical requires trend-tactical options and forbids Progressive/" +
                        "StructuralConfluence/DivergenceReversal/BreakoutDetector/TradingClassification options.");
                }
                TrendTactical.Validate();
                break;
            case TradingAgentKind.Alfonso:
                if (Alfonso is null || Progressive is not null || StructuralConfluence is not null ||
                    DivergenceReversal is not null || BreakoutDetector is not null ||
                    TradingClassification is not null || TrendTactical is not null)
                {
                    throw new ArgumentException(
                        "Alfonso requires alfonso options and forbids Progressive/StructuralConfluence/" +
                        "DivergenceReversal/BreakoutDetector/TradingClassification/TrendTactical options.");
                }
                Alfonso.Validate();
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
        TradingAgentKind.DivergenceReversal => AgentDefinition.FromDivergenceReversal(DivergenceReversal!),
        TradingAgentKind.BreakoutDetector => AgentDefinition.FromBreakoutDetector(BreakoutDetector!),
        TradingAgentKind.TradingClassification => AgentDefinition.FromTradingClassification(TradingClassification!),
        TradingAgentKind.TrendTactical => AgentDefinition.FromTrendTactical(TrendTactical!),
        TradingAgentKind.Alfonso => AgentDefinition.FromAlfonso(Alfonso!),
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
            TradingAgentTypeIds.DivergenceReversal => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.DivergenceReversal,
                DivergenceReversal = definition.ReadDivergenceReversalOptions()
            },
            TradingAgentTypeIds.BreakoutDetector => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.BreakoutDetector,
                BreakoutDetector = definition.ReadBreakoutDetectorOptions()
            },
            TradingAgentTypeIds.TradingClassification => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.TradingClassification,
                TradingClassification = definition.ReadTradingClassificationOptions()
            },
            TradingAgentTypeIds.TrendTactical => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.TrendTactical,
                TrendTactical = definition.ReadTrendTacticalOptions()
            },
            _ => throw new ArgumentException($"Unknown Agent type '{definition.AgentTypeId}'.", nameof(definition))
        };
    }
}
