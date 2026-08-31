namespace TrendStatistics.Data;

/// <summary>
/// A completed candle consumed by the statistical trend detector.
/// The detector owns the timeframe so this model remains independent of broker types.
/// </summary>
public sealed record Candle
{
    public required string Symbol { get; init; }

    public required DateTimeOffset OpenTime { get; init; }

    public required decimal Open { get; init; }

    public required decimal High { get; init; }

    public required decimal Low { get; init; }

    public required decimal Close { get; init; }

    public bool IsComplete { get; init; } = true;
}
