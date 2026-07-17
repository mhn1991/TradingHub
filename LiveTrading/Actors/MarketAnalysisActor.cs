using System.Threading.Channels;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using Microsoft.Extensions.Logging;
using TradingCore.MarketData;

namespace LiveTrading.Actors;

/// <summary>
/// One actor per instrument, owning its own <see cref="MultiTimeframeAggregator"/>, <see
/// cref="IChartAnnotator"/>, and <see cref="IMarketDataQualityGate"/> - no shared mutable state
/// across actors, so different actors may run concurrently while each processes its own events
/// strictly sequentially. Never touches the broker beyond the read-only <see
/// cref="ICompletedCandleProvider"/> used for warm-up/catch-up.
/// </summary>
public sealed class MarketAnalysisActor(
    LiveMarketDefinition market,
    MultiTimeframeAggregator aggregator,
    IChartAnnotator annotator,
    IMarketDataQualityGate qualityGate,
    ICompletedCandleProvider warmupProvider,
    TimeProvider timeProvider,
    ILogger<MarketAnalysisActor> logger,
    int warmupCandles = 250)
{
    private readonly Dictionary<BarInterval, AnalysisSnapshot> _latestByInterval = [];
    private DateTimeOffset? _lastAppliedOpenTime;
    private DateTimeOffset _lastConfirmedClose = DateTimeOffset.MinValue;
    private long _marketSequence;
    private long _processedCandleCount;
    private long _duplicateCandleCount;
    private long _gapDetectedCount;
    private IReadOnlyList<DataQualityIssue> _recentIssues = [];

    public LiveMarketState State { get; private set; } = LiveMarketState.Disabled;
    public LiveAnalysisReadiness? Readiness { get; private set; }
    public LiveQuoteSnapshot? LatestQuote { get; private set; }
    public InstrumentKey Instrument => market.Instrument;

    public async Task RunAsync(
        ChannelReader<LiveMarketEvent> input,
        ChannelWriter<MarketAnalysisUpdate> output,
        CancellationToken cancellationToken)
    {
        await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        if (State != LiveMarketState.Degraded)
        {
            State = LiveMarketState.Ready;
        }

        try
        {
            await foreach (LiveMarketEvent evt in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LiveQuoteMarketEvent quote:
                        LatestQuote = quote.Quote;
                        break;

                    case CandleClosedMarketEvent candleEvent:
                        await ApplyCandleAsync(candleEvent.Candle, output, cancellationToken).ConfigureAwait(false);
                        break;

                    case StreamFaultMarketEvent:
                        State = LiveMarketState.Stale;
                        break;

                    case StreamRecoveredMarketEvent:
                        await WarmUpAsync(cancellationToken).ConfigureAwait(false);
                        if (State != LiveMarketState.Degraded)
                        {
                            State = LiveMarketState.Ready;
                        }

                        break;
                }
            }
        }
        finally
        {
            // Always complete the output writer when this actor stops (input closed or
            // cancelled) - otherwise a caller reading via ReadAllAsync() with no cancellation of
            // its own would hang forever waiting for an update that will never arrive.
            output.TryComplete();
        }
    }

    /// <summary>Cold start (no cursor yet) and post-reconnect catch-up share this path: fetch
    /// missing completed candles and replay them through the aggregator/annotator without
    /// publishing intermediate updates - only a live <see cref="ApplyCandleAsync"/> call ever
    /// writes to the output channel.</summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        bool coldStart = _lastConfirmedClose == DateTimeOffset.MinValue;
        State = coldStart ? LiveMarketState.WarmingUp : LiveMarketState.CatchingUp;

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset from = coldStart
            ? now - TimeSpan.FromSeconds(
                BarIntervalParser.ApproximateSeconds(market.AnalysisBaseInterval) * warmupCandles)
            : _lastConfirmedClose;

        IReadOnlyList<Candle> candles;
        try
        {
            candles = await warmupProvider.GetCompletedCandlesAsync(
                market.Instrument, market.AnalysisBaseInterval, from, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Warm-up/catch-up candle fetch failed for {Instrument}.", market.Instrument);
            return;
        }

        foreach (Candle candle in candles.OrderBy(c => c.OpenTime))
        {
            await ApplyCandleAsync(candle, output: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyCandleAsync(
        Candle baseCandle, ChannelWriter<MarketAnalysisUpdate>? output, CancellationToken cancellationToken)
    {
        if (_lastAppliedOpenTime is { } last && baseCandle.OpenTime <= last)
        {
            _duplicateCandleCount++;
            logger.LogWarning(
                "Dropped duplicate/out-of-order M1 candle for {Instrument} at {OpenTime}.",
                market.Instrument, baseCandle.OpenTime);
            return;
        }

        _lastAppliedOpenTime = baseCandle.OpenTime;
        _lastConfirmedClose = baseCandle.CloseTime ?? market.AnalysisBaseInterval.AddTo(baseCandle.OpenTime);
        _processedCandleCount++;

        IReadOnlyList<CandleClosedEvent> closed;
        try
        {
            closed = aggregator.Apply(baseCandle);
        }
        catch (Exception ex)
        {
            _gapDetectedCount++;
            State = LiveMarketState.Degraded;
            logger.LogError(ex, "Chronological gap detected for {Instrument}; marking Degraded.", market.Instrument);
            return;
        }

        var closedThisTick = new HashSet<BarInterval>();
        foreach (CandleClosedEvent candleEvent in closed)
        {
            AnalysisSnapshot snapshot = await annotator
                .ProcessAsync(candleEvent, runtimeContext: null, cancellationToken)
                .ConfigureAwait(false);
            _latestByInterval[candleEvent.Interval] = snapshot;
            closedThisTick.Add(candleEvent.Interval);
        }

        Readiness = BuildReadiness();
        if (!Readiness.Ready)
        {
            if (State != LiveMarketState.Degraded)
            {
                State = LiveMarketState.WarmingUp;
            }

            return;
        }

        if (output is null)
        {
            // Warm-up/catch-up replay: state/cursor are rebuilt, but no update is published for
            // historical candles - only the caller's final live candle (outside this loop) does.
            return;
        }

        var analysis = new MultiTimeframeAnalysis(
            market.Instrument,
            timeProvider.GetUtcNow(),
            market.AnalysisIntervals.ToDictionary(interval => interval, interval => _latestByInterval[interval]));

        DataQualityResult quality = qualityGate.Evaluate(analysis);
        _recentIssues = quality.Issues;
        State = quality.ShouldTrip ? LiveMarketState.Degraded : LiveMarketState.Ready;

        await output.WriteAsync(
            new MarketAnalysisUpdate
            {
                Instrument = market.Instrument,
                AvailableAt = analysis.Timestamp,
                ClosedIntervals = closedThisTick,
                Analysis = analysis,
                Health = BuildHealthSnapshot(quality),
                MarketSequence = ++_marketSequence
            },
            cancellationToken).ConfigureAwait(false);
    }

    private LiveAnalysisReadiness BuildReadiness()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var components = new List<ComponentReadiness>(market.AnalysisIntervals.Count);
        bool ready = true;

        foreach (BarInterval interval in market.AnalysisIntervals)
        {
            bool hasSnapshot = _latestByInterval.TryGetValue(interval, out AnalysisSnapshot? snapshot);
            // Indicator nullability is the public readiness signal (null = not enough samples yet
            // - the same fields TradingCore.MarketData.MarketDataQualityGate's RequireIndicatorsReady
            // check inspects). A numeric per-indicator sample count is not exposed publicly by
            // ChartAnnotationEngine, so RequiredSamples/AvailableSamples are a boolean-shaped 1/0
            // proxy rather than a true running count.
            bool indicatorsReady = hasSnapshot &&
                snapshot!.AvailableAt <= now &&
                snapshot.Indicators.Atr is not null &&
                snapshot.Indicators.Rsi is not null &&
                snapshot.Indicators.BollingerMiddle is not null;

            ready &= indicatorsReady;
            components.Add(new ComponentReadiness
            {
                Component = "Analysis",
                Interval = interval,
                RequiredSamples = 1,
                AvailableSamples = indicatorsReady ? 1 : 0,
                Status = indicatorsReady ? "Ready" : "WarmingUp"
            });
        }

        return new LiveAnalysisReadiness
        {
            Instrument = market.Instrument,
            Ready = ready,
            Components = components
        };
    }

    private MarketDataHealthSnapshot BuildHealthSnapshot(DataQualityResult quality) => new()
    {
        Instrument = market.Instrument,
        State = State,
        AsOf = timeProvider.GetUtcNow(),
        LastQuoteAt = LatestQuote?.BrokerTime,
        LastM1CloseAt = _lastConfirmedClose == DateTimeOffset.MinValue ? null : _lastConfirmedClose,
        IsQuoteStale = LatestQuote?.IsStale ?? true,
        ProcessedCandleCount = _processedCandleCount,
        DuplicateCandleCount = _duplicateCandleCount,
        GapDetectedCount = _gapDetectedCount,
        RecentIssues = quality.Issues.Count > 0 ? quality.Issues : _recentIssues
    };
}
