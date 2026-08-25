namespace Agent.Strategies.DivergenceReversal;

/// <summary>
/// Per-timeframe counts of how far each candle got through
/// <see cref="DivergenceReversalAgent"/>'s entry condition, so "why did this agent only trade N
/// times?" is answerable from data rather than inference. Every counter is a strict funnel stage:
/// a candle counted at one stage was counted at all the stages above it.
/// </summary>
public sealed record DivergenceReversalFunnelStage
{
    public required string Interval { get; init; }

    /// <summary>"trigger" (a monitored interval) or "confirmation".</summary>
    public required string Role { get; init; }

    /// <summary>Closed candles of this interval fed through the agent.</summary>
    public long CandlesProcessed { get; init; }

    /// <summary>Candles reading Partial - touches the extreme condition without qualifying.</summary>
    public long PartialExtremes { get; init; }

    /// <summary>Candles reading Full - band + RSI + StochRSI-fast all at their extreme.</summary>
    public long FullExtremes { get; init; }

    /// <summary>Full readings with no StochRSI-fast relationship tracked at all yet.</summary>
    public long NoRelationship { get; init; }

    /// <summary>Full readings whose relationship confirmed longer ago than
    /// <see cref="DivergenceReversalStrategyOptions.SignalFreshnessCandles"/>.</summary>
    public long StaleRelationship { get; init; }

    /// <summary>Full readings whose relationship already produced a decision earlier.</summary>
    public long AlreadyConsumed { get; init; }

    /// <summary>Full readings with a fresh, unconsumed relationship pointing the other way.</summary>
    public long DirectionMismatch { get; init; }

    /// <summary>Full readings that cleared every gate and produced a signal.</summary>
    public long SignalsBuilt { get; init; }
}

/// <summary>
/// Whole-run funnel for one <see cref="DivergenceReversalAgent"/> instance. Diagnostics only -
/// nothing here feeds a decision.
/// </summary>
public sealed record DivergenceReversalFunnelSnapshot
{
    public required IReadOnlyList<DivergenceReversalFunnelStage> Stages { get; init; }

    /// <summary><c>EvaluateAsync</c> calls.</summary>
    public long Evaluations { get; init; }

    public long Entries { get; init; }
    public long Closes { get; init; }

    /// <summary>Entries opened as the second half of a close-then-flip.</summary>
    public long Flips { get; init; }

    /// <summary>Signals dropped because the position was already on that side.</summary>
    public long SuppressedAlreadyPositioned { get; init; }

    /// <summary>Breakout/continuation signals that opposed an open position and so were held
    /// through rather than acted on - by design, only a reversal closes.</summary>
    public long BreakoutHeldAgainstPosition { get; init; }
}
