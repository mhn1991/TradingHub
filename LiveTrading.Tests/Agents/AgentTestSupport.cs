using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using LiveTrading.Actors;
using LiveTrading.MarketData;

namespace LiveTrading.Tests.Agents;

/// <summary>Shared minimal builders for <see cref="AnalysisSnapshot"/>/<see
/// cref="MultiTimeframeAnalysis"/>/<see cref="MarketAnalysisUpdate"/> across the Phase 2 Agents
/// test fixtures - every field an <see cref="IMarketDataQualityGate"/>/<see
/// cref="Agent.Abstractions.ITradingAgent"/> pipeline evaluation actually reads is populated;
/// everything else uses each record's own permissive defaults.</summary>
internal static class AgentTestSupport
{
    public static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    public static readonly BarInterval M1 = BarInterval.Minutes(1);
    public static readonly BarInterval M5 = BarInterval.Minutes(5);

    public static Candle Candle(DateTimeOffset openTime, BarInterval interval, decimal close = 1.1002m) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        OpenTime = openTime,
        CloseTime = interval.AddTo(openTime),
        Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, close),
        Volume = new MarketVolume(100m, VolumeKind.Unknown),
        IsComplete = true
    };

    public static AnalysisSnapshot Snapshot(BarInterval interval, DateTimeOffset availableAt) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = availableAt,
        Version = 1,
        LatestCandle = Candle(availableAt.AddSeconds(-BarIntervalParser.ApproximateSeconds(interval)), interval),
        Indicators = new IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 0.5m, Contributions = [] }
    };

    public static MultiTimeframeAnalysis Analysis(DateTimeOffset availableAt, params BarInterval[] intervals) =>
        new(Instrument, availableAt, intervals.ToDictionary(interval => interval, interval => Snapshot(interval, availableAt)));

    public static MarketAnalysisUpdate Update(
        MultiTimeframeAnalysis analysis,
        IReadOnlySet<BarInterval> closedIntervals,
        DateTimeOffset availableAt,
        long sequence) => new()
    {
        Instrument = Instrument,
        AvailableAt = availableAt,
        ClosedIntervals = closedIntervals,
        Analysis = analysis,
        Health = new MarketDataHealthSnapshot
        {
            Instrument = Instrument,
            State = LiveMarketState.Ready,
            AsOf = availableAt,
            IsQuoteStale = false,
            ProcessedCandleCount = 1,
            DuplicateCandleCount = 0,
            GapDetectedCount = 0,
            RecentIssues = []
        },
        MarketSequence = sequence
    };
}
