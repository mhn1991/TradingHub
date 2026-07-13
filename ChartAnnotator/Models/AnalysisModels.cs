using Brokers.Models;

namespace ChartAnnotator.Models;

public readonly record struct ChartKey(
    InstrumentKey Instrument,
    BarInterval Interval);

public sealed record CandleClosedEvent(
    InstrumentKey Instrument,
    BarInterval Interval,
    Candle Candle,
    long Sequence);

public enum SwingType
{
    High,
    Low
}

public sealed record SwingPoint
{
    public required DateTimeOffset PivotTime { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required decimal Price { get; init; }
    public required SwingType Type { get; init; }
    public required int Strength { get; init; }
}

public enum PriceZoneType
{
    Support,
    Resistance,
    Mixed
}

public sealed record PriceZone
{
    public required decimal LowerPrice { get; init; }
    public required decimal UpperPrice { get; init; }
    public required decimal CentrePrice { get; init; }
    public required int TouchCount { get; init; }
    public required decimal Strength { get; init; }
    public required PriceZoneType Type { get; init; }
}

public enum TrendlineType
{
    Support,
    Resistance
}

public sealed record Trendline
{
    public required DateTimeOffset OriginTime { get; init; }
    public required decimal OriginPrice { get; init; }
    public required decimal SlopePerSecond { get; init; }
    public required int InlierCount { get; init; }
    public required decimal MeanAbsoluteError { get; init; }
    public required decimal FitScore { get; init; }
    public required TrendlineType Type { get; init; }

    public decimal PriceAt(DateTimeOffset time) =>
        OriginPrice + SlopePerSecond * (decimal)(time - OriginTime).TotalSeconds;
}

public enum ChannelDirection
{
    Falling,
    Sideways,
    Rising
}

public sealed record PriceChannel
{
    public required Trendline LowerLine { get; init; }
    public required Trendline UpperLine { get; init; }
    public required ChannelDirection Direction { get; init; }
    public required decimal Width { get; init; }
    public required decimal WidthAtr { get; init; }
    public required decimal Confidence { get; init; }
}

public sealed record IndicatorSnapshot
{
    public decimal? Atr { get; init; }
    public decimal? Rsi { get; init; }
    public decimal? BollingerMiddle { get; init; }
    public decimal? BollingerUpper { get; init; }
    public decimal? BollingerLower { get; init; }
}

public sealed record IndicatorPoint(
    DateTimeOffset Timestamp,
    decimal? Atr,
    decimal? Rsi,
    decimal? BollingerMiddle,
    decimal? BollingerUpper,
    decimal? BollingerLower,
    decimal Confidence);

public sealed record ConfidenceContribution(
    string Rule,
    decimal Score,
    string Explanation);

public sealed record ConfidenceScore
{
    public required decimal Total { get; init; }
    public required IReadOnlyList<ConfidenceContribution> Contributions { get; init; }
}

public sealed record AnalysisSnapshot
{
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required long Version { get; init; }
    public required Candle LatestCandle { get; init; }
    public required IndicatorSnapshot Indicators { get; init; }
    public required IReadOnlyList<SwingPoint> Swings { get; init; }
    public required IReadOnlyList<PriceZone> PriceZones { get; init; }
    public required IReadOnlyList<Trendline> Trendlines { get; init; }
    public required IReadOnlyList<PriceChannel> Channels { get; init; }
    public required ConfidenceScore Confidence { get; init; }
}
