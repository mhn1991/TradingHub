using Brokers.Models;

namespace Agent.Strategies.DivergenceReversal;

/// <summary>
/// Formalizes the user's original Bollinger + RSI + StochRSI "extreme reading" strategy
/// (algoTrading/Brokers/Binance/run.py - a proof of concept, not production logic) with the two
/// pieces that proof of concept was missing: a StochRSI-fast-line divergence check that classifies
/// an extreme reading as a genuine reversal versus a breakout continuation, and a protective stop
/// as a backstop (the original had no risk bound at all - see PROJECT_STATE.md for the
/// conversation this came from).
/// </summary>
public sealed record DivergenceReversalStrategyOptions
{
    /// <summary>
    /// The trigger timeframes this agent watches for an extreme reading. Deliberately not a fixed
    /// trend/setup/entry role hierarchy - any configured interval can independently produce a
    /// signal, matching "any timeframe can trigger" from the source conversation. Must contain at
    /// least one interval.
    /// </summary>
    public required IReadOnlyList<BarInterval> MonitoredIntervals { get; init; }

    /// <summary>
    /// Lower timeframes checked, finest first, only when a <see cref="MonitoredIntervals"/> reading
    /// is <i>partial</i> (touches the extreme condition but doesn't fully qualify) - mirrors the
    /// original Python's recursive lower-timeframe confirmation. The first confirmation interval
    /// whose own reading fully qualifies in the same direction supplies the signal (its own
    /// StochRSI-fast divergence classifies reversal vs. breakout, since that's where the fresh
    /// confirmed swing pivot actually is). May be empty - a partial reading with no confirmation
    /// interval configured, or none that confirms, is simply not acted on.
    /// </summary>
    public IReadOnlyList<BarInterval> ConfirmationIntervals { get; init; } = [];

    public decimal Quantity { get; init; } = 1_000m;

    public decimal RsiOverbought { get; init; } = 75m;
    public decimal RsiOversold { get; init; } = 25m;

    /// <summary>StochRSI fast line (%K), not the slow line (%D) - see StochRsiAnalysisState.</summary>
    public decimal StochRsiFastOverbought { get; init; } = 100m;
    public decimal StochRsiFastOversold { get; init; } = 0m;

    /// <summary>
    /// Looser thresholds defining a <i>partial</i> reading (mirrors the Python's "touches the band,
    /// or %K beyond 85/15" condition): a candle whose high/low touches the Bollinger band, or whose
    /// StochRSI-fast crosses this threshold, without meeting the full <see cref="StochRsiFastOverbought"/>/
    /// <see cref="StochRsiFastOversold"/> + RSI condition. Only meaningful on a
    /// <see cref="MonitoredIntervals"/> entry - confirmation intervals are checked against the
    /// *full* condition only, never a further partial.
    /// </summary>
    public decimal PartialStochRsiFastOverbought { get; init; } = 85m;
    public decimal PartialStochRsiFastOversold { get; init; } = 15m;

    /// <summary>
    /// Backstop-only protective stop distance, in ATR multiples of the signal timeframe. The
    /// primary exit is strategy-driven (wait for the next genuine reversal signal, in either
    /// direction); this only bounds the worst case if that never arrives in reasonable time -
    /// closing exactly the "I had to sell but didn't know how deep it would go" gap the original
    /// implementation had no answer for.
    /// </summary>
    public decimal ProtectiveStopAtrMultiple { get; init; } = 3.0m;

    /// <summary>
    /// Per-timeframe StochRSI divergence tracking parameters, passed through to each interval's
    /// <c>StochRsiAnalysisState</c> instance.
    /// </summary>
    public int RelationshipSignalLifetimeCandles { get; init; } = 50;
    public decimal MinimumStochRsiFastDifference { get; init; } = 5m;
    public decimal MinimumPriceDifferenceAtr { get; init; } = 0.05m;

    public void Validate()
    {
        if (MonitoredIntervals is null || MonitoredIntervals.Count == 0 ||
            MonitoredIntervals.Any(interval => !interval.IsValid) ||
            MonitoredIntervals.Distinct().Count() != MonitoredIntervals.Count)
        {
            throw new ArgumentException(
                "At least one distinct, valid monitored interval is required.",
                nameof(MonitoredIntervals));
        }

        if (ConfirmationIntervals is null ||
            ConfirmationIntervals.Any(interval => !interval.IsValid) ||
            ConfirmationIntervals.Distinct().Count() != ConfirmationIntervals.Count ||
            ConfirmationIntervals.Intersect(MonitoredIntervals).Any())
        {
            throw new ArgumentException(
                "Confirmation intervals must be distinct, valid, and disjoint from the monitored intervals.",
                nameof(ConfirmationIntervals));
        }

        if (Quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (RsiOversold is < 0m or > 100m || RsiOverbought is < 0m or > 100m || RsiOverbought <= RsiOversold)
            throw new ArgumentException("RSI thresholds must be within 0-100 with overbought above oversold.");
        if (StochRsiFastOversold is < 0m or > 100m || StochRsiFastOverbought is < 0m or > 100m ||
            StochRsiFastOverbought <= StochRsiFastOversold)
        {
            throw new ArgumentException("StochRSI fast thresholds must be within 0-100 with overbought above oversold.");
        }
        if (PartialStochRsiFastOversold is < 0m or > 100m || PartialStochRsiFastOverbought is < 0m or > 100m ||
            PartialStochRsiFastOverbought <= PartialStochRsiFastOversold ||
            PartialStochRsiFastOverbought > StochRsiFastOverbought ||
            PartialStochRsiFastOversold < StochRsiFastOversold)
        {
            throw new ArgumentException(
                "Partial StochRSI fast thresholds must be within 0-100, overbought above oversold, and " +
                "strictly inside the full overbought/oversold thresholds.");
        }
        if (ProtectiveStopAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(ProtectiveStopAtrMultiple));
        if (RelationshipSignalLifetimeCandles < 1)
            throw new ArgumentOutOfRangeException(nameof(RelationshipSignalLifetimeCandles));
        if (MinimumStochRsiFastDifference < 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumStochRsiFastDifference));
        if (MinimumPriceDifferenceAtr < 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumPriceDifferenceAtr));
    }
}
