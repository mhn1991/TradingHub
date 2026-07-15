namespace ChartAnnotator.Regime;

public enum MarketRegime
{
    Unknown,
    TrendingUp,
    TrendingDown,
    Range,
    Compression,
    BreakoutExpansionUp,
    BreakoutExpansionDown,
    HighVolatilityDisorder,
    IlliquidUnsafe
}

public sealed record RegimeContribution(
    string Rule,
    decimal Score,
    string Explanation);

public sealed record MarketRegimeSnapshot
{
    /// <summary>
    /// The permissive default used when classification is disabled or has not yet
    /// produced a result: never blocks trading (<see cref="IsTradeable"/> is true).
    /// </summary>
    public static MarketRegimeSnapshot Unknown { get; } = new()
    {
        Regime = MarketRegime.Unknown,
        Confidence = 0m,
        ConfirmedAt = DateTimeOffset.MinValue,
        AgeCandles = 0,
        Contributions = [],
        ReasonCode = "NoData",
        IsTradeable = true
    };

    public required MarketRegime Regime { get; init; }
    public required decimal Confidence { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required int AgeCandles { get; init; }
    public required IReadOnlyList<RegimeContribution> Contributions { get; init; }
    public required string ReasonCode { get; init; }
    public bool IsTradeable { get; init; }
}
