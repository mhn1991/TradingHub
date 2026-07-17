using Brokers.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.NeoWave;

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

public enum VolumeRegime
{
    Unknown,
    VeryLow,
    Low,
    Normal,
    High,
    Spike
}

public sealed record VolumeAnalysisSnapshot
{
    public static VolumeAnalysisSnapshot Empty { get; } = new();

    public decimal? Value { get; init; }
    public VolumeKind Kind { get; init; }
    public decimal? BaselineMedian { get; init; }
    public decimal? RelativeToBaseline { get; init; }
    public decimal? Percentile { get; init; }
    public VolumeRegime Regime { get; init; }
    public int SampleCount { get; init; }

    /// <summary>
    /// True when the feed identifies the volume semantics. TickCount is a relative
    /// activity proxy, while quantity kinds represent traded/reported quantity.
    /// </summary>
    public bool IsReliable { get; init; }
    public bool IsActivityProxy => Kind == VolumeKind.TickCount;
    public bool IsElevated => Regime is VolumeRegime.High or VolumeRegime.Spike;
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

public enum MarketEfficiencyState
{
    Unknown,
    HighlyChoppy,
    Choppy,
    Transitional,
    Efficient,
    HighlyEfficient
}

public sealed record EfficiencyAnalysisSnapshot
{
    public static EfficiencyAnalysisSnapshot Empty { get; } = new();

    public decimal? Percentile { get; init; }
    public MomentumDirection Direction { get; init; }
    public MarketEfficiencyState State { get; init; }
    public int SampleCount { get; init; }
}

public sealed record DonchianSnapshot
{
    public static DonchianSnapshot Empty { get; } = new();

    public decimal? Upper { get; init; }
    public decimal? Lower { get; init; }
    public decimal? Middle { get; init; }
    public decimal? Width { get; init; }
    public decimal? WidthAtr { get; init; }
    public bool ClosedAbovePreviousUpper { get; init; }
    public bool ClosedBelowPreviousLower { get; init; }
    public int BarsSinceUpperBreak { get; init; } = -1;
    public int BarsSinceLowerBreak { get; init; } = -1;
}

public enum ValueReferenceKind
{
    Unavailable,
    AnchoredTwap,
    BrokerTickVolumeVwap,
    ExchangeVolumeVwap
}

public enum ValueAnchorType
{
    SessionOpen,
    WeekOpen,
    MajorSwing,
    StructureBreak,
    SetupStart
}

/// <summary>
/// A causal anchored value estimate. Tick-count weighting is deliberately labelled
/// separately from traded-volume VWAP so feed semantics remain honest.
/// </summary>
public sealed record AnchoredValueReference
{
    public required string AnchorId { get; init; }
    public required ValueAnchorType AnchorType { get; init; }
    public required DateTimeOffset AnchoredAt { get; init; }
    public required ValueReferenceKind Kind { get; init; }
    public required decimal Value { get; init; }
    public decimal? StandardDeviation { get; init; }
    public decimal? DistanceAtr { get; init; }
    public decimal? DataCoveragePercent { get; init; }
}

public sealed record IndicatorSnapshot
{
    public decimal? Atr { get; init; }
    public decimal? Rsi { get; init; }
    public decimal? BollingerMiddle { get; init; }
    public decimal? BollingerUpper { get; init; }
    public decimal? BollingerLower { get; init; }
    /// <summary>Commodity Channel Index (Lambert), typically period 20.</summary>
    public decimal? Cci { get; init; }
    /// <summary>Simple moving average of close (default period 50).</summary>
    public decimal? Sma50 { get; init; }
    /// <summary>Simple moving average of close (default period 200).</summary>
    public decimal? Sma200 { get; init; }

    /// <summary>
    /// Incremental Kaufman-style Efficiency Ratio for the current bar. Distinct
    /// from the swing-leg-scoped <see cref="PriceLegMetrics.EfficiencyRatio"/>.
    /// </summary>
    public decimal? EfficiencyRatio { get; init; }
    public EfficiencyAnalysisSnapshot EfficiencyAnalysis { get; init; } = EfficiencyAnalysisSnapshot.Empty;
    public DonchianSnapshot Donchian { get; init; } = DonchianSnapshot.Empty;
    public AtrAnalysisSnapshot AtrAnalysis { get; init; } = AtrAnalysisSnapshot.Empty;
    public VolumeAnalysisSnapshot VolumeAnalysis { get; init; } = VolumeAnalysisSnapshot.Empty;
    public RsiAnalysisSnapshot RsiAnalysis { get; init; } = RsiAnalysisSnapshot.Empty;
    public BollingerAnalysisSnapshot BollingerAnalysis { get; init; } = BollingerAnalysisSnapshot.Empty;
    public AdxAnalysisSnapshot AdxAnalysis { get; init; } = AdxAnalysisSnapshot.Empty;
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
    BollingerAnalysisSnapshot? BollingerAnalysis = null,
    VolumeAnalysisSnapshot? VolumeAnalysis = null,
    decimal? Cci = null,
    decimal? Sma50 = null,
    decimal? Sma200 = null);

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
    public PriceActionSnapshot PriceAction { get; init; } = PriceActionSnapshot.Empty;
    public MarketRegimeSnapshot MarketRegime { get; init; } = MarketRegimeSnapshot.Unknown;
    public NeoWaveSnapshot NeoWave { get; init; } = NeoWaveSnapshot.Disabled;
    public IReadOnlyList<AnchoredValueReference> ValueReferences { get; init; } = [];
    public required ConfidenceScore Confidence { get; init; }
}

public enum PriceActionDirection
{
    Neutral,
    Bullish,
    Bearish
}

public enum PriceActionEventType
{
    BullishBreakOfStructure,
    BearishBreakOfStructure,
    BullishChangeOfCharacter,
    BearishChangeOfCharacter,
    BullishRetestHeld,
    BearishRetestHeld,
    BullishRejection,
    BearishRejection,
    BullishDisplacement,
    BearishDisplacement,
    SellSideLiquiditySweep,
    BuySideLiquiditySweep,
    BullishCompressionBreakout,
    BearishCompressionBreakout,
    BullishImpulse,
    BearishImpulse,
    BullishPullback,
    BearishPullback
}

public enum BreakRetestState
{
    None,
    AwaitingRetest,
    RetestInProgress,
    RetestHeld,
    RetestFailed,
    Expired
}

public sealed record PriceActionEvent
{
    public required string EventId { get; init; }
    public required PriceActionEventType Type { get; init; }
    public required PriceActionDirection Direction { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required long ConfirmedSequence { get; init; }
    public decimal? ReferenceLevel { get; init; }
    public decimal? BrokenLevel { get; init; }
    public decimal? RetestLevel { get; init; }
    public decimal? Atr { get; init; }
    public required decimal Strength { get; init; }
    public required decimal Confidence { get; init; }
    public string? SourceSwingKey { get; init; }
    public string? SourceZoneKey { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public sealed record PriceActionDiagnostic
{
    public required string Candidate { get; init; }
    public required bool Accepted { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public sealed record BreakRetestSnapshot
{
    public static BreakRetestSnapshot Empty { get; } = new();

    public string? SetupId { get; init; }
    public PriceActionDirection Direction { get; init; }
    public BreakRetestState State { get; init; }
    public decimal? BrokenLevel { get; init; }
    public DateTimeOffset? BreakConfirmedAt { get; init; }
    public long? BreakSequence { get; init; }
    public int BarsSinceBreak { get; init; }
    public decimal? ClosestRetestDistanceAtr { get; init; }
    public string? InvalidReason { get; init; }
}

public sealed record PriceLegMetrics
{
    public PriceActionDirection Direction { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public decimal Distance { get; init; }
    public decimal? DistanceAtr { get; init; }
    public int BarCount { get; init; }

    /// <summary>
    /// Path efficiency of this specific swing leg (distance/path, clamped 0-1).
    /// Distinct from the bar-by-bar <see cref="IndicatorSnapshot.EfficiencyRatio"/>.
    /// </summary>
    public decimal EfficiencyRatio { get; init; }
    public decimal RetracementPercent { get; init; }
}

public sealed record PriceActionCalibrationSnapshot
{
    public static PriceActionCalibrationSnapshot Empty { get; } = new();

    public int SampleCount { get; init; }
    public bool IsReady { get; init; }
    public bool IsFrozen { get; init; }
    public DateTimeOffset? FrozenAt { get; init; }
    public decimal MedianBodyAtr { get; init; }
    public decimal MedianRangeAtr { get; init; }
    public decimal MedianWickToBodyRatio { get; init; }
    public decimal BodyAtr70 { get; init; }
    public decimal RangeAtr70 { get; init; }
    public decimal RangeAtr90 { get; init; }
}

public enum PriceActionSetupType
{
    BullishBreakRetestHold,
    BearishBreakRetestHold,
    BullishChoChRetestHold,
    BearishChoChRetestHold,
    BullishSweepDisplacement,
    BearishSweepDisplacement,
    BullishSweepChoCh,
    BearishSweepChoCh
}

public enum PriceActionSetupPhase
{
    Armed,
    Triggered,
    Invalidated,
    Expired
}

public sealed record PriceActionSetup
{
    public required string SetupId { get; init; }
    public required PriceActionSetupType Type { get; init; }
    public required PriceActionDirection Direction { get; init; }
    public required PriceActionSetupPhase Phase { get; init; }
    public required DateTimeOffset ArmedAt { get; init; }
    public DateTimeOffset? TriggeredAt { get; init; }
    public required long ArmedSequence { get; init; }
    public long? TriggeredSequence { get; init; }
    public required decimal Confidence { get; init; }
    public decimal? ReferenceLevel { get; init; }
    public decimal? EntryReference { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public IReadOnlyList<string> SourceEventIds { get; init; } = [];
}

public sealed record PriceActionSnapshot
{
    public static PriceActionSnapshot Empty { get; } = new();

    public PriceActionDirection Bias { get; init; }
    public decimal BullishScore { get; init; }
    public decimal BearishScore { get; init; }
    public IReadOnlyList<PriceActionEvent> Events { get; init; } = [];
    public IReadOnlyList<PriceActionDiagnostic> Diagnostics { get; init; } = [];
    public BreakRetestSnapshot ActiveRetest { get; init; } = BreakRetestSnapshot.Empty;
    public PriceLegMetrics? LatestLeg { get; init; }
    public PriceActionCalibrationSnapshot Calibration { get; init; } = PriceActionCalibrationSnapshot.Empty;
    /// <summary>
    /// Composite setups derived from atomic events (same timeframe). Terminal
    /// Triggered/Invalidated/Expired rows for this bar are included alongside any
    /// still-Armed setups.
    /// </summary>
    public IReadOnlyList<PriceActionSetup> Setups { get; init; } = [];

    public bool HasConfirmedTrigger(PriceActionDirection direction, decimal minimumConfidence = 50m) =>
        Events.Any(item =>
            item.Direction == direction &&
            item.Confidence >= minimumConfidence &&
            (item.Type is PriceActionEventType.BullishRetestHeld or
                PriceActionEventType.BearishRetestHeld or
                PriceActionEventType.BullishRejection or
                PriceActionEventType.BearishRejection or
                PriceActionEventType.BullishDisplacement or
                PriceActionEventType.BearishDisplacement or
                PriceActionEventType.BullishCompressionBreakout or
                PriceActionEventType.BearishCompressionBreakout or
                PriceActionEventType.SellSideLiquiditySweep or
                PriceActionEventType.BuySideLiquiditySweep or
                PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter or
                PriceActionEventType.BullishPullback or
                PriceActionEventType.BearishPullback));

    public bool HasTriggeredSetup(
        PriceActionDirection direction,
        decimal minimumConfidence = 50m,
        IReadOnlyCollection<PriceActionSetupType>? allowedTypes = null) =>
        Setups.Any(setup =>
            setup.Phase == PriceActionSetupPhase.Triggered &&
            setup.Direction == direction &&
            setup.Confidence >= minimumConfidence &&
            (allowedTypes is null || allowedTypes.Count == 0 || allowedTypes.Contains(setup.Type)));

    public PriceActionSetup? GetBestTriggeredSetup(
        PriceActionDirection direction,
        decimal minimumConfidence = 50m,
        IReadOnlyCollection<PriceActionSetupType>? allowedTypes = null) =>
        Setups
            .Where(setup =>
                setup.Phase == PriceActionSetupPhase.Triggered &&
                setup.Direction == direction &&
                setup.Confidence >= minimumConfidence &&
                (allowedTypes is null || allowedTypes.Count == 0 || allowedTypes.Contains(setup.Type)))
            .OrderByDescending(setup => setup.Confidence)
            .ThenByDescending(setup => setup.TriggeredAt)
            .FirstOrDefault();
}

public sealed record AdxAnalysisSnapshot
{
    public static AdxAnalysisSnapshot Empty { get; } = new();

    public decimal? Adx { get; init; }
    public decimal? PlusDi { get; init; }
    public decimal? MinusDi { get; init; }
    public MomentumDirection StrengthDirection { get; init; }
    public PriceActionDirection DirectionalBias { get; init; }
    public bool IsTrendStrengthening { get; init; }
}
