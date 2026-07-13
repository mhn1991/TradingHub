using System.Collections.Concurrent;
using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;
using ChartAnnotator.Structure;

namespace ChartAnnotator.Engine;

/// <summary>
/// Maintains bounded, incremental analysis state per instrument/timeframe.
/// For deterministic backtests, call ProcessAsync sequentially for each ChartKey.
/// Different ChartKeys may be processed in parallel by a higher-level coordinator.
/// </summary>
public sealed class ChartAnnotationEngine : IChartAnnotator
{
    private readonly ChartAnnotationOptions _options;
    private readonly SupportResistanceDetector _supportResistance;
    private readonly RansacTrendlineDetector _trendlineDetector;
    private readonly ChannelDetector _channelDetector;
    private readonly ConfidenceScorer _confidenceScorer;
    private readonly ConcurrentDictionary<ChartKey, AnalysisState> _states = [];

    public ChartAnnotationEngine(
        ChartAnnotationOptions? options = null,
        SupportResistanceDetector? supportResistance = null,
        RansacTrendlineDetector? trendlineDetector = null,
        ChannelDetector? channelDetector = null,
        ConfidenceScorer? confidenceScorer = null)
    {
        _options = options ?? new ChartAnnotationOptions();
        Validate(_options);
        _supportResistance = supportResistance ?? new SupportResistanceDetector();
        _trendlineDetector = trendlineDetector ?? new RansacTrendlineDetector();
        _channelDetector = channelDetector ?? new ChannelDetector();
        _confidenceScorer = confidenceScorer ?? new ConfidenceScorer();
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

        state.Atr.Update(candleEvent.Candle);
        state.Rsi.Update(candleEvent.Candle.Prices.Close);
        state.Bollinger.Update(candleEvent.Candle.Prices.Close);

        IReadOnlyList<SwingPoint> confirmed = state.SwingDetector.Update(candleEvent.Candle);
        foreach (SwingPoint swing in confirmed)
        {
            state.Swings.Add(swing);
        }

        if (confirmed.Count > 0)
        {
            state.SwingSnapshot = state.Swings.Snapshot();
        }

        bool runHeavyAnalysis = confirmed.Count > 0 ||
            state.Version == 1 ||
            state.Version % _options.HeavyAnalysisEveryCandles == 0;

        if (runHeavyAnalysis && state.Atr.IsReady)
        {
            IReadOnlyList<SwingPoint> swingSnapshot = state.SwingSnapshot;
            state.Zones = _supportResistance.Detect(swingSnapshot, state.Atr.Current);
            state.Trendlines = _trendlineDetector.Detect(
                swingSnapshot,
                state.Atr.Current,
                state.Version);
            state.Channels = _channelDetector.Detect(
                state.Trendlines,
                state.LastCloseTime.Value,
                state.Atr.Current);
        }

        IndicatorSnapshot indicators = new()
        {
            Atr = state.Atr.IsReady ? state.Atr.Current : null,
            Rsi = state.Rsi.IsReady ? state.Rsi.Current : null,
            BollingerMiddle = state.Bollinger.IsReady ? state.Bollinger.Middle : null,
            BollingerUpper = state.Bollinger.IsReady ? state.Bollinger.Upper : null,
            BollingerLower = state.Bollinger.IsReady ? state.Bollinger.Lower : null
        };

        ConfidenceScore confidence = _confidenceScorer.Calculate(
            candleEvent.Candle,
            indicators,
            state.Zones,
            state.Trendlines,
            state.Channels);

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
            confidence.Total));
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

    private AnalysisState GetOrCreate(ChartKey key)
    {
        return _states.GetOrAdd(key, _ => new AnalysisState(_options));
    }

    private static void Validate(ChartAnnotationOptions options)
    {
        if (options.CandleCapacity < 1 ||
            options.SwingCapacity < 1 ||
            options.IndicatorCapacity < 1 ||
            options.HeavyAnalysisEveryCandles < 1 ||
            options.AtrPeriod <= 1 ||
            options.RsiPeriod <= 1 ||
            options.BollingerPeriod <= 1 ||
            options.BollingerStandardDeviations <= 0m ||
            options.SwingLeftBars < 1 ||
            options.SwingRightBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
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
            Rsi = new RsiState(options.RsiPeriod);
            Bollinger = new BollingerState(
                options.BollingerPeriod,
                options.BollingerStandardDeviations);
            SwingDetector = new SwingDetector(
                options.SwingLeftBars,
                options.SwingRightBars);
        }

        public object SyncRoot { get; } = new();
        public RingBuffer<Candle> Candles { get; }
        public RingBuffer<SwingPoint> Swings { get; }
        public RingBuffer<IndicatorPoint> IndicatorHistory { get; }
        public IReadOnlyList<SwingPoint> SwingSnapshot { get; set; } = [];
        public AtrState Atr { get; }
        public RsiState Rsi { get; }
        public BollingerState Bollinger { get; }
        public SwingDetector SwingDetector { get; }
        public IReadOnlyList<PriceZone> Zones { get; set; } = [];
        public IReadOnlyList<Trendline> Trendlines { get; set; } = [];
        public IReadOnlyList<PriceChannel> Channels { get; set; } = [];
        public AnalysisSnapshot? Latest { get; set; }
        public DateTimeOffset? LastCloseTime { get; set; }
        public long LastSequence { get; set; }
        public long Version { get; set; }
    }
}
