namespace Agent.Strategies.Alfonso.Trend;

/// <summary>
/// Module 5: "An asset can only be in either one of three stages: 1. Uptrend 2. Downtrend 3. Out of
/// alignment stage (OOA)."
/// </summary>
public enum AlfonsoTrend
{
    /// <summary>
    /// Neither trending nor out of alignment, because nothing has established either yet. The
    /// course has no name for this because a chart always has history; a detector started mid-stream
    /// does not.
    /// </summary>
    Unknown,

    Uptrend,

    Downtrend,

    /// <summary>"There are times when a timeframe is neither trending up nor trending down."</summary>
    OutOfAlignment
}

/// <summary>
/// The trend on one timeframe, with the evidence that produced it.
/// <para>
/// The evidence is carried rather than discarded because module 5's central warning - "A trendline
/// that connects two impulses does not necessarily mean there is a trend" - is precisely that the
/// same trendline can accompany a trend or no trend. Only the accomplishment separates them, so the
/// accomplishment has to be inspectable.
/// </para>
/// </summary>
public sealed record AlfonsoTrendSnapshot
{
    public required AlfonsoTrend Trend { get; init; }

    /// <summary>The live trendline, when two qualifying swings exist and it has not been broken.</summary>
    public Trendline? Line { get; init; }

    /// <summary>
    /// Opposing zones eliminated since the trend last reset. Module 5 requires at least one:
    /// "An uptrend requires an accomplishment, not just successive higher highs and higher lows."
    /// </summary>
    public required int OpposingEliminations { get; init; }

    /// <summary>
    /// True while the timeframe has run too far to be traded. Module 5: "Once a certain timeframe is
    /// over-extended, that timeframe can no longer be used to place a trade."
    /// </summary>
    public required bool IsOverExtended { get; init; }

    /// <summary>Why the state is what it is, for logging and for reading a rejected bar back.</summary>
    public required string Reason { get; init; }

    public bool IsTrending => Trend is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend;

    /// <summary>A timeframe can only be traded when it is trending and not over-extended.</summary>
    public bool CanTrade => IsTrending && !IsOverExtended;
}
