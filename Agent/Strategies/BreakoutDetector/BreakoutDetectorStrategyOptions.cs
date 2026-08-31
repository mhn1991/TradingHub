using Brokers.Models;

namespace Agent.Strategies.BreakoutDetector;

/// <summary>
/// Configuration for <see cref="BreakoutDetectorAgent"/>. Every property has a default so
/// <c>new BreakoutDetectorStrategyOptions()</c> is a valid definition (matching
/// <c>StructuralConfluenceStrategyOptions</c>), which keeps catalogue/round-trip tests cheap.
/// <para>
/// Scaffold note: the detection parameters below are placeholders chosen to be plausible, not
/// calibrated. They exist so the options shape, validation, and JSON round-trip are wired end to
/// end; adjust or replace them as the detection logic in <see cref="BreakoutDetectorAgent"/> takes
/// shape, and record any calibrated values in PROJECT_STATE.md rather than leaving them implied.
/// </para>
/// </summary>
public sealed record BreakoutDetectorStrategyOptions
{
    /// <summary>Highest timeframe; qualifies the breakout's direction and context.</summary>
    public BarInterval ContextInterval { get; init; } = BarInterval.Minutes(15);

    /// <summary>
    /// Where the consolidation range and the breaking candle are measured. Not the finest
    /// timeframe any more - see <see cref="ConfirmationInterval"/>.
    /// </summary>
    public BarInterval TriggerInterval { get; init; } = BarInterval.Minutes(5);

    /// <summary>
    /// Finest timeframe, evaluated last: the breakout detected on <see cref="TriggerInterval"/>
    /// is only taken if this timeframe agrees. It is also the agent's evaluation cadence
    /// (<c>ITradingAgent.TriggerInterval</c>), because a confirmation that is only consulted on
    /// 5m closes would not be a 1m confirmation at all.
    /// </summary>
    public BarInterval ConfirmationInterval { get; init; } = BarInterval.Minutes(1);

    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// How many completed <see cref="TriggerInterval"/> candles define the range/consolidation a
    /// breakout must clear.
    /// </summary>
    public int RangeLookbackCandles { get; init; } = 20;

    /// <summary>
    /// Minimum distance beyond the range boundary, in <see cref="TriggerInterval"/> ATR multiples,
    /// before a close counts as a break rather than a wick through the level.
    /// </summary>
    public decimal BreakoutBufferAtr { get; init; } = 0.25m;

    /// <summary>Protective stop distance in <see cref="TriggerInterval"/> ATR multiples.</summary>
    public decimal StopAtrMultiple { get; init; } = 1.5m;

    /// <summary>Bracket target distance in <see cref="TriggerInterval"/> ATR multiples.</summary>
    public decimal TargetAtrMultiple { get; init; } = 3.0m;

    /// <summary>
    /// Reward/risk floor applied to the resolved bracket. A candidate whose stop and target imply
    /// less than this is not taken, so a wide stop cannot be rescued by an optimistic target.
    /// </summary>
    public decimal MinimumRewardRisk { get; init; } = 1.5m;

    /// <summary>
    /// Trigger candles to wait after a breakout entry before another one may fire, bounding
    /// repeated re-entries into the same move.
    /// </summary>
    public int CooldownCandles { get; init; } = 4;

    /// <summary>Every timeframe this agent evaluates.</summary>
    public IReadOnlySet<BarInterval> RequiredIntervals =>
        new HashSet<BarInterval>([ConfirmationInterval, TriggerInterval, ContextInterval]);

    public void Validate()
    {
        if (!TriggerInterval.IsValid || !ContextInterval.IsValid || !ConfirmationInterval.IsValid)
            throw new ArgumentException("Breakout-detector intervals must be valid.");
        if (BarIntervalParser.CompareDuration(TriggerInterval, ContextInterval) > 0)
        {
            throw new ArgumentException(
                "Breakout-detector intervals must satisfy TriggerInterval <= ContextInterval.");
        }
        if (BarIntervalParser.CompareDuration(ConfirmationInterval, TriggerInterval) > 0)
        {
            throw new ArgumentException(
                "Breakout-detector intervals must satisfy ConfirmationInterval <= TriggerInterval.");
        }
        // Distinctness is what makes three ROLES meaningful. Collapsing two of them onto one
        // interval silently turns a three-timeframe agent into a two-timeframe one whose
        // "confirmation" is the same candle that triggered it - self-confirming, and invisible
        // in results.
        if (RequiredIntervals.Count != 3)
        {
            throw new ArgumentException(
                "Breakout-detector context, trigger and confirmation intervals must be distinct.");
        }
        if (Quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (RangeLookbackCandles < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RangeLookbackCandles),
                "A range needs at least two candles to have boundaries.");
        }
        if (BreakoutBufferAtr < 0m)
            throw new ArgumentOutOfRangeException(nameof(BreakoutBufferAtr));
        if (StopAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(StopAtrMultiple));
        if (TargetAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(TargetAtrMultiple));
        if (MinimumRewardRisk <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumRewardRisk));
        if (CooldownCandles < 0)
            throw new ArgumentOutOfRangeException(nameof(CooldownCandles));
    }
}
