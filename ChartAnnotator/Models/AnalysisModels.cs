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
    /// <summary>The first pivot supporting this fitted segment.</summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>The last pivot supporting this fitted segment.</summary>
    public required DateTimeOffset EndTime { get; init; }

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
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required ChannelDirection Direction { get; init; }
    public required decimal Width { get; init; }
    public required decimal WidthAtr { get; init; }
    public required decimal Confidence { get; init; }
}


public enum MarketStructureDirection
{
    Unknown,
    Rising,
    Falling,
    Sideways
}

public enum MarketStructureBreak
{
    None,
    Bullish,
    Bearish
}

public sealed record MarketStructureSnapshot
{
    public static MarketStructureSnapshot Empty { get; } = new();

    public MarketStructureDirection Direction { get; init; }
    public MarketStructureDirection PreviousDirection { get; init; }
    public MarketStructureBreak Break { get; init; }
    public bool DirectionChanged { get; init; }
    public DateTimeOffset? SegmentStartedAt { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public SwingPoint? LastSwingHigh { get; init; }
    public SwingPoint? LastSwingLow { get; init; }
    public int ConsecutiveHigherHighs { get; init; }
    public int ConsecutiveHigherLows { get; init; }
    public int ConsecutiveLowerHighs { get; init; }
    public int ConsecutiveLowerLows { get; init; }
    public decimal Strength { get; init; }
}

public enum MomentumDirection
{
    Unknown,
    Falling,
    Stable,
    Rising
}

public enum VolatilityDirection
{
    Unknown,
    Contracting,
    Stable,
    Expanding
}

public enum BollingerWidthRegime
{
    Unknown,
    Squeeze,
    Narrow,
    Normal,
    Wide,
    Expansion
}

public sealed record BollingerAnalysisSnapshot
{
    public static BollingerAnalysisSnapshot Empty { get; } = new();

    public decimal? BandwidthPercent { get; init; }
    public decimal? BandwidthChangePercent { get; init; }
    public decimal? PercentB { get; init; }
    public decimal? WidthPercentile { get; init; }
    public VolatilityDirection WidthDirection { get; init; }
    public BollingerWidthRegime WidthRegime { get; init; }
    public bool IsSqueeze { get; init; }
    public bool IsExpansion { get; init; }
    public bool SqueezeReleased { get; init; }
    public int SampleCount { get; init; }
}

public enum AtrVolatilityRegime
{
    Unknown,
    VeryLow,
    Low,
    Normal,
    High,
    VeryHigh
}

public sealed record AtrAnalysisSnapshot
{
    public static AtrAnalysisSnapshot Empty { get; } = new();

    public decimal? NormalizedPercent { get; init; }
    public decimal? ChangePercent { get; init; }
    public decimal? Percentile { get; init; }
    public VolatilityDirection Direction { get; init; }
    public AtrVolatilityRegime Regime { get; init; }
    public int SampleCount { get; init; }
}

public enum RsiZone
{
    Unknown,
    Oversold,
    Bearish,
    Neutral,
    Bullish,
    Overbought
}

public enum RsiRelationshipType
{
    None,
    RegularBullishDivergence,
    RegularBearishDivergence,
    HiddenBullishDivergence,
    HiddenBearishDivergence,
    BullishConvergence,
    BearishConvergence
}

public sealed record RsiRelationshipSnapshot
{
    public required RsiRelationshipType Type { get; init; }
    public required DateTimeOffset FirstPivotTime { get; init; }
    public required DateTimeOffset SecondPivotTime { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required decimal FirstPrice { get; init; }
    public required decimal SecondPrice { get; init; }
    public required decimal FirstRsi { get; init; }
    public required decimal SecondRsi { get; init; }
    public required decimal PriceChange { get; init; }
    public required decimal RsiChange { get; init; }
    public required decimal Strength { get; init; }
    public required int AgeCandles { get; init; }

    public bool IsDivergence => Type is
        RsiRelationshipType.RegularBullishDivergence or
        RsiRelationshipType.RegularBearishDivergence or
        RsiRelationshipType.HiddenBullishDivergence or
        RsiRelationshipType.HiddenBearishDivergence;

    public bool IsConvergence => Type is
        RsiRelationshipType.BullishConvergence or
        RsiRelationshipType.BearishConvergence;
}

public sealed record RsiAnalysisSnapshot
{
    public static RsiAnalysisSnapshot Empty { get; } = new();

    public RsiZone Zone { get; init; }
    public MomentumDirection MomentumDirection { get; init; }
    public decimal? MomentumChange { get; init; }
    public RsiRelationshipSnapshot? LatestRelationship { get; init; }
    public bool IsNewRelationship { get; init; }
    public int SampleCount { get; init; }
}

public sealed record IndicatorSnapshot
{
    public decimal? Atr { get; init; }
    public decimal? Rsi { get; init; }
    public decimal? BollingerMiddle { get; init; }
    public decimal? BollingerUpper { get; init; }
    public decimal? BollingerLower { get; init; }
    public AtrAnalysisSnapshot AtrAnalysis { get; init; } = AtrAnalysisSnapshot.Empty;
    public RsiAnalysisSnapshot RsiAnalysis { get; init; } = RsiAnalysisSnapshot.Empty;
    public BollingerAnalysisSnapshot BollingerAnalysis { get; init; } = BollingerAnalysisSnapshot.Empty;
}

public sealed record IndicatorPoint(
    DateTimeOffset Timestamp,
    decimal? Atr,
    decimal? Rsi,
    decimal? BollingerMiddle,
    decimal? BollingerUpper,
    decimal? BollingerLower,
    decimal Confidence,
    AtrAnalysisSnapshot? AtrAnalysis = null,
    RsiAnalysisSnapshot? RsiAnalysis = null,
    BollingerAnalysisSnapshot? BollingerAnalysis = null);

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
    public MarketStructureSnapshot MarketStructure { get; init; } = MarketStructureSnapshot.Empty;
    public required ConfidenceScore Confidence { get; init; }
}
