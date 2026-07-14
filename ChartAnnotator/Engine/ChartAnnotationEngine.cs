using System.Collections.Concurrent;
using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;
using ChartAnnotator.PriceAction;
using ChartAnnotator.Structure;

namespace ChartAnnotator.Engine;

/// <summary>
/// Maintains bounded, incremental analysis state per instrument/timeframe.
/// For deterministic backtests, call ProcessAsync sequentially for each ChartKey.
/// Different ChartKeys may be processed in parallel by a higher-level coordinator.
/// </summary>
public sealed class ChartAnnotationEngine : IChartAnnotator, ICalibratableChartAnnotator
{
    private readonly ChartAnnotationOptions _options;
    private readonly SupportResistanceDetector _supportResistance;
    private readonly RansacTrendlineDetector _trendlineDetector;
    private readonly ChannelDetector _channelDetector;
    private readonly ConfidenceScorer _confidenceScorer;
    private readonly MarketStructureAnalyzer _marketStructureAnalyzer;
    private readonly ConcurrentDictionary<ChartKey, AnalysisState> _states = [];

    public ChartAnnotationEngine(
        ChartAnnotationOptions? options = null,
        SupportResistanceDetector? supportResistance = null,
        RansacTrendlineDetector? trendlineDetector = null,
        ChannelDetector? channelDetector = null,
        ConfidenceScorer? confidenceScorer = null,
        MarketStructureAnalyzer? marketStructureAnalyzer = null)
    {
        _options = options ?? new ChartAnnotationOptions();
        Validate(_options);
        _supportResistance = supportResistance ?? new SupportResistanceDetector();
        _trendlineDetector = trendlineDetector ?? new RansacTrendlineDetector();
        _channelDetector = channelDetector ?? new ChannelDetector();
        _confidenceScorer = confidenceScorer ?? new ConfidenceScorer();
        _marketStructureAnalyzer = marketStructureAnalyzer ??
            new MarketStructureAnalyzer(_options.StructureDirectionToleranceAtr);
    }

    public ValueTask<AnalysisSnapshot> ProcessAsync(
        CandleClosedEvent candleEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candleEvent);
        if (!candleEvent.Candle.IsComplete)
        {
            throw new ArgumentException("Only completed candles may be annotated.", nameof(candleEvent));
        }

        if (candleEvent.Instrument != candleEvent.Candle.Instrument ||
            candleEvent.Interval != candleEvent.Candle.Interval)
        {
            throw new ArgumentException(
                "The candle event identity does not match the contained candle.",
                nameof(candleEvent));
        }

        if (candleEvent.Candle.CloseTime is not DateTimeOffset closeTime ||
            closeTime <= candleEvent.Candle.OpenTime)
        {
            throw new ArgumentException(
                "A completed candle must have a close time later than its open time.",
                nameof(candleEvent));
        }

        ChartKey key = new(candleEvent.Instrument, candleEvent.Interval);
        AnalysisState state = GetOrCreate(key);
        lock (state.SyncRoot)
        {
            return ValueTask.FromResult(ProcessCore(candleEvent, key, state, closeTime));
        }
    }

    private AnalysisSnapshot ProcessCore(
        CandleClosedEvent candleEvent,
        ChartKey key,
        AnalysisState state,
        DateTimeOffset closeTime)
    {
        if (state.LastCloseTime is not null && closeTime <= state.LastCloseTime)
        {
            throw new InvalidOperationException("Candle events must be processed in chronological order.");
        }

        if (candleEvent.Sequence <= state.LastSequence)
        {
            throw new InvalidOperationException("Candle event sequences must be strictly increasing.");
        }

        state.Candles.Add(candleEvent.Candle);
        state.LastCloseTime = closeTime;
        state.LastSequence = candleEvent.Sequence;
        state.Version++;

        bool atrWasReady = state.Atr.IsReady;
        state.Atr.Update(candleEvent.Candle);
        bool atrBecameReady = !atrWasReady && state.Atr.IsReady;
        state.Rsi.Update(candleEvent.Candle.Prices.Close);
        state.Bollinger.Update(candleEvent.Candle.Prices.Close);
        state.Adx.Update(candleEvent.Candle);
        AtrAnalysisSnapshot atrAnalysis = state.Atr.IsReady
            ? state.AtrAnalysis.Update(state.Atr.Current, candleEvent.Candle.Prices.Close)
            : AtrAnalysisSnapshot.Empty;
        BollingerAnalysisSnapshot bollingerAnalysis = state.BollingerAnalysis.Update(
            candleEvent.Candle.Prices.Close,
            state.Bollinger);

        IReadOnlyList<SwingPoint> confirmed = state.SwingDetector.Update(candleEvent.Candle);
        foreach (SwingPoint swing in confirmed)
        {
            state.Swings.Add(swing);
        }

        RsiAnalysisSnapshot rsiAnalysis = state.RsiAnalysis.Update(
            candleEvent.Candle,
            state.Rsi.IsReady ? state.Rsi.Current : null,
            confirmed,
            state.Atr.IsReady ? state.Atr.Current : null);

        if (confirmed.Count > 0)
        {
            state.SwingSnapshot = state.Swings.Snapshot();
        }

        MarketStructureSnapshot previousStructure = state.MarketStructure;
        MarketStructureSnapshot structure = _marketStructureAnalyzer.Analyze(
            state.SwingSnapshot,
            candleEvent.Candle,
            state.Atr.IsReady ? state.Atr.Current : null,
            previousStructure);
        state.MarketStructure = structure;
        bool structureChanged = structure.DirectionChanged;
        bool breakChanged = structure.Break != previousStructure.Break;
        bool runHeavyAnalysis = confirmed.Count > 0 ||
            atrBecameReady ||
            structureChanged ||
            breakChanged ||
            state.Version == 1 ||
            state.Version % _options.HeavyAnalysisEveryCandles == 0;

        if (state.Atr.IsReady)
        {
            // Use one immutable view of the confirmed swings and one ATR value for
            // the complete structural-analysis pass.
            IReadOnlyList<SwingPoint> swingSnapshot = state.SwingSnapshot;
            decimal currentAtr = state.Atr.Current;

            if (runHeavyAnalysis)
            {
                state.Zones = _supportResistance.Detect(
                    swingSnapshot,
                    currentAtr,
                    candleEvent.Candle.Prices.Close);

                state.Trendlines = _trendlineDetector.Detect(
                    swingSnapshot,
                    currentAtr,
                    state.Version,
                    structure.Direction,
                    closeTime);
            }

            // Channel validation is inexpensive because the trendline collection is
            // small. Run it for every closed candle so a projected channel advances
            // to the current time and is removed immediately when the latest close
            // breaks outside it. Trendline fitting remains on the heavy schedule.
            state.Channels = state.Trendlines.Count == 0
                ? []
                : _channelDetector.Detect(
                    state.Trendlines,
                    swingSnapshot,
                    closeTime,
                    currentAtr,
                    candleEvent.Candle.Prices.Close,
                    MarketStructureDirection.Unknown);
        }

        IndicatorSnapshot indicators = new()
        {
            Atr = state.Atr.IsReady ? state.Atr.Current : null,
            Rsi = state.Rsi.IsReady ? state.Rsi.Current : null,
            BollingerMiddle = state.Bollinger.IsReady ? state.Bollinger.Middle : null,
            BollingerUpper = state.Bollinger.IsReady ? state.Bollinger.Upper : null,
            BollingerLower = state.Bollinger.IsReady ? state.Bollinger.Lower : null,
            AtrAnalysis = atrAnalysis,
            RsiAnalysis = rsiAnalysis,
            BollingerAnalysis = bollingerAnalysis,
            AdxAnalysis = state.Adx.Snapshot()
        };
        PriceActionSnapshot priceAction = state.PriceAction.Update(
            candleEvent.Candle,
            state.Candles.Snapshot(),
            state.SwingSnapshot,
            state.Zones,
            structure,
            previousStructure,
            indicators,
            candleEvent.Sequence);
        ConfidenceScore confidence = _confidenceScorer.Calculate(
            candleEvent.Candle,
            indicators,
            state.Zones,
            state.Trendlines,
            state.Channels,
            structure,
            priceAction);
        AnalysisSnapshot snapshot = new()
        {
            Instrument = key.Instrument,
            Interval = key.Interval,
            AvailableAt = state.LastCloseTime.Value,
            Version = state.Version,
            LatestCandle = candleEvent.Candle,
            Indicators = indicators,
            Swings = state.SwingSnapshot,
            PriceZones = state.Zones,
            Trendlines = state.Trendlines,
            Channels = state.Channels,
            MarketStructure = structure,
            PriceAction = priceAction,
            Confidence = confidence
        };

        state.Latest = snapshot;
        state.IndicatorHistory.Add(new IndicatorPoint(
            snapshot.AvailableAt,
            indicators.Atr,
            indicators.Rsi,
            indicators.BollingerMiddle,
            indicators.BollingerUpper,
            indicators.BollingerLower,
            confidence.Total,
            indicators.AtrAnalysis,
            indicators.RsiAnalysis,
            indicators.BollingerAnalysis));
        return snapshot;
    }

    public AnalysisSnapshot? GetLatest(
        InstrumentKey instrument,
        BarInterval interval)
    {
        if (!_states.TryGetValue(new ChartKey(instrument, interval), out AnalysisState? state))
        {
            return null;
        }

        lock (state.SyncRoot)
        {
            return state.Latest;
        }
    }

    public IReadOnlyList<Candle> GetCandles(
        InstrumentKey instrument,
        BarInterval interval)
    {
        if (!_states.TryGetValue(new ChartKey(instrument, interval), out AnalysisState? state))
        {
            return [];
        }

        lock (state.SyncRoot)
        {
            return state.Candles.Snapshot();
        }
    }

    public IReadOnlyList<IndicatorPoint> GetIndicatorHistory(
        InstrumentKey instrument,
        BarInterval interval)
    {
        if (!_states.TryGetValue(new ChartKey(instrument, interval), out AnalysisState? state))
        {
            return [];
        }

        lock (state.SyncRoot)
        {
            return state.IndicatorHistory.Snapshot();
        }
    }

    private AnalysisState GetOrCreate(ChartKey key) =>
        _states.GetOrAdd(key, _ => new AnalysisState(_options));

    private static void Validate(ChartAnnotationOptions options)
    {
        if (options.CandleCapacity < 1 ||
            options.SwingCapacity < 1 ||
            options.IndicatorCapacity < 1 ||
            options.HeavyAnalysisEveryCandles < 1 ||
            options.AtrPeriod <= 1 ||
            options.AdxPeriod <= 1 ||
            options.AtrAnalysisHistoryPeriod < 2 ||
            options.AtrAnalysisChangeLookback < 1 ||
            options.AtrAnalysisChangeLookback >= options.AtrAnalysisHistoryPeriod ||
            options.AtrAnalysisMinimumSamples < 2 ||
            options.AtrAnalysisMinimumSamples > options.AtrAnalysisHistoryPeriod ||
            options.AtrDirectionThresholdPercent < 0m ||
            options.RsiPeriod <= 1 ||
            options.RsiMomentumLookback < 1 ||
            options.RsiMomentumThreshold < 0m ||
            options.RsiMinimumDivergenceDifference < 0m ||
            options.RsiMinimumPriceDifferenceAtr < 0m ||
            options.RsiSignalLifetimeCandles < 1 ||
            options.BollingerPeriod <= 1 ||
            options.BollingerStandardDeviations <= 0m ||
            options.BollingerWidthHistoryPeriod < 2 ||
            options.BollingerWidthChangeLookback < 1 ||
            options.BollingerWidthChangeLookback >= options.BollingerWidthHistoryPeriod ||
            options.BollingerWidthMinimumSamples < 2 ||
            options.BollingerWidthMinimumSamples > options.BollingerWidthHistoryPeriod ||
            options.BollingerWidthDirectionThresholdPercent < 0m ||
            options.BollingerSqueezePercentile is < 0m or > 100m ||
            options.BollingerWidePercentile is < 0m or > 100m ||
            options.BollingerWidePercentile <= options.BollingerSqueezePercentile ||
            options.SwingLeftBars < 1 ||
            options.SwingRightBars < 1 ||
            options.StructureDirectionToleranceAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        options.PriceAction.Validate();
    }

    public void FreezeCalibration(DateTimeOffset frozenAt)
    {
        foreach (AnalysisState state in _states.Values)
        {
            lock (state.SyncRoot)
            {
                state.PriceAction.FreezeCalibration(frozenAt);
            }
        }
    }

    private sealed class AnalysisState
    {
        public AnalysisState(ChartAnnotationOptions options)
        {
            Candles = new RingBuffer<Candle>(options.CandleCapacity);
            Swings = new RingBuffer<SwingPoint>(options.SwingCapacity);
            IndicatorHistory = new RingBuffer<IndicatorPoint>(options.IndicatorCapacity);
            Atr = new AtrState(options.AtrPeriod);
            Adx = new AdxState(options.AdxPeriod);
            AtrAnalysis = new AtrAnalysisState(
                options.AtrAnalysisHistoryPeriod,
                options.AtrAnalysisChangeLookback,
                options.AtrAnalysisMinimumSamples,
                options.AtrDirectionThresholdPercent);
            Rsi = new RsiState(options.RsiPeriod);
            RsiAnalysis = new RsiAnalysisState(
                Math.Max(options.IndicatorCapacity, options.RsiMomentumLookback + 1),
                Math.Max(options.SwingCapacity, 2),
                options.RsiMomentumLookback,
                options.RsiMomentumThreshold,
                options.RsiMinimumDivergenceDifference,
                options.RsiMinimumPriceDifferenceAtr,
                options.RsiSignalLifetimeCandles);
            Bollinger = new BollingerState(
                options.BollingerPeriod,
                options.BollingerStandardDeviations);
            BollingerAnalysis = new BollingerAnalysisState(
                options.BollingerWidthHistoryPeriod,
                options.BollingerWidthChangeLookback,
                options.BollingerWidthMinimumSamples,
                options.BollingerWidthDirectionThresholdPercent,
                options.BollingerSqueezePercentile,
                options.BollingerWidePercentile);
            SwingDetector = new SwingDetector(
                options.SwingLeftBars,
                options.SwingRightBars);
            PriceAction = new PriceActionAnalyzer(options.PriceAction);
        }

        public object SyncRoot { get; } = new();
        public RingBuffer<Candle> Candles { get; }
        public RingBuffer<SwingPoint> Swings { get; }
        public RingBuffer<IndicatorPoint> IndicatorHistory { get; }
        public IReadOnlyList<SwingPoint> SwingSnapshot { get; set; } = [];
        public AtrState Atr { get; }
        public AdxState Adx { get; }
        public AtrAnalysisState AtrAnalysis { get; }
        public RsiState Rsi { get; }
        public RsiAnalysisState RsiAnalysis { get; }
        public BollingerState Bollinger { get; }
        public BollingerAnalysisState BollingerAnalysis { get; }
        public SwingDetector SwingDetector { get; }
        public PriceActionAnalyzer PriceAction { get; }
        public IReadOnlyList<PriceZone> Zones { get; set; } = [];
        public IReadOnlyList<Trendline> Trendlines { get; set; } = [];
        public IReadOnlyList<PriceChannel> Channels { get; set; } = [];
        public MarketStructureSnapshot MarketStructure { get; set; } = MarketStructureSnapshot.Empty;
        public AnalysisSnapshot? Latest { get; set; }
        public DateTimeOffset? LastCloseTime { get; set; }
        public long LastSequence { get; set; } = -1;
        public long Version { get; set; }
    }
}
