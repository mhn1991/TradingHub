using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Configuration for the Set and Forget supply and demand agent.
/// <para>
/// Every timeframe is a parameter. Module 8 lists five sequences and leaves the choice to the
/// trader, so the agent must be able to run any of them without a code change - the rules read
/// roles, never durations.
/// </para>
/// </summary>
public sealed record AlfonsoStrategyOptions
{
    /// <summary>Top timeframe: direction. "We will only trade in the direction of this chart."</summary>
    public BarInterval TopInterval { get; init; } = BarInterval.Hours(4);

    /// <summary>Middle timeframe: intermediate direction.</summary>
    public BarInterval MiddleInterval { get; init; } = BarInterval.Hours(1);

    /// <summary>Lower timeframe: execution. Orders are planned at this timeframe's zones.</summary>
    public BarInterval LowerInterval { get; init; } = BarInterval.Minutes(15);

    public ImbalanceOptions Zones { get; init; } = new();

    public AlfonsoTrendOptions Trend { get; init; } = new();

    public RangeOptions Range { get; init; } = new();

    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// Whether an entry may be planned at a zone that has already been tested once. Module 7 says
    /// no on the set-and-forget path - "We will only trade the first pullback to an imbalance, that
    /// is, only fresh levels" - and a second pullback "require[s] new imbalances to be traded".
    /// </summary>
    public bool FreshLevelsOnly { get; init; } = true;

    public IReadOnlySet<BarInterval> RequiredIntervals =>
        new HashSet<BarInterval> { TopInterval, MiddleInterval, LowerInterval };

    public TimeframeSequence Sequence => new()
    {
        Top = ToTimeSpan(TopInterval),
        Middle = ToTimeSpan(MiddleInterval),
        Lower = ToTimeSpan(LowerInterval)
    };

    /// <summary>
    /// Interval as a span, for the timeframe-agnostic engines. Months are approximated, which is
    /// safe because the span is only ever used to label and order timeframes, never to advance a
    /// clock - candles are advanced by their own open times.
    /// </summary>
    public static TimeSpan ToTimeSpan(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => TimeSpan.FromSeconds(interval.Value),
        BarUnit.Minute => TimeSpan.FromMinutes(interval.Value),
        BarUnit.Hour => TimeSpan.FromHours(interval.Value),
        BarUnit.Day => TimeSpan.FromDays(interval.Value),
        BarUnit.Week => TimeSpan.FromDays(interval.Value * 7),
        BarUnit.Month => TimeSpan.FromDays(interval.Value * 30),
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    public void Validate()
    {
        Zones.Validate();
        Range.Validate();
        Sequence.Validate();

        if (Quantity <= 0m)
            throw new InvalidOperationException("Quantity must be positive.");
    }
}
