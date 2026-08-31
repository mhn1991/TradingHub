namespace TrendStatistics.Detection;

/// <summary>
/// Immutable state visible after processing one completed candle.
/// </summary>
public sealed record TrendState
{
    public required TrendPhase Phase { get; init; }

    public TrendDirection? Direction { get; init; }

    /// <summary>The detector symbol. Present on every non-neutral state.</summary>
    public string? Symbol { get; init; }

    public DateTimeOffset? StructuralStartTime { get; init; }

    public decimal? StructuralStartPrice { get; init; }

    public DateTimeOffset? ConfirmationTime { get; init; }

    public decimal? ConfirmationPrice { get; init; }

    /// <summary>ATR/price at confirmation, in percent; fixed for the life of the trend.</summary>
    public decimal VolatilityPctAtConfirmation { get; init; }

    public decimal CurrentMovePct { get; init; }

    /// <summary>The running favorable high/low used to calculate <see cref="CurrentMovePct"/>.</summary>
    public decimal? FavorableExtremePrice { get; init; }

    public int DurationBars { get; init; }

    public decimal DurationHours { get; init; }

    public static TrendState Neutral { get; } = new()
    {
        Phase = TrendPhase.Neutral
    };
}
