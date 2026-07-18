using System.Threading.Channels;
using System.Diagnostics;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using Microsoft.Extensions.Logging;
using TradingCore.MarketData;
using TradingCore.Pipeline;
using System.Collections.Frozen;

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
    int warmupCandles = 250,
    IReadOnlyDictionary<AnalysisProfileKey, IChartAnnotator>? profileAnnotators = null,
    IReadOnlyDictionary<AnalysisProfileKey, IMarketDataQualityGate>? profileQualityGates = null,
    IReadOnlyDictionary<AnalysisProfileKey, int>? profileDependentAgentCounts = null)
{
    private readonly Dictionary<BarInterval, AnalysisSnapshot> _latestByInterval = [];
    private readonly Dictionary<AnalysisProfileKey, Dictionary<BarInterval, AnalysisSnapshot>>
        _latestByProfile = (profileAnnotators ?? new Dictionary<AnalysisProfileKey, IChartAnnotator>())
            .ToDictionary(item => item.Key, _ => new Dictionary<BarInterval, AnalysisSnapshot>());
    private DateTimeOffset? _lastAppliedOpenTime;
    private DateTimeOffset _lastConfirmedClose = DateTimeOffset.MinValue;
    private long _marketSequence;
    private long _processedCandleCount;
    private long _duplicateCandleCount;
    private long _gapDetectedCount;
    private IReadOnlyList<DataQualityIssue> _recentIssues = [];
    private readonly Dictionary<AnalysisProfileKey, ProfileCounters> _profileCounters =
        (profileAnnotators ?? new Dictionary<AnalysisProfileKey, IChartAnnotator>())
        .ToDictionary(item => item.Key, _ => new ProfileCounters());

    public LiveMarketState State { get; private set; } = LiveMarketState.Disabled;
    public LiveAnalysisReadiness? Readiness { get; private set; }
    public LiveQuoteSnapshot? LatestQuote { get; private set; }
    public InstrumentKey Instrument => market.Instrument;
    public IReadOnlyList<AnalysisProfileActorStatus> ProfileStatuses => _profileCounters
        .OrderBy(item => item.Key.ProfileHash, StringComparer.Ordinal)
        .Select(item => new AnalysisProfileActorStatus
        {
            Profile = item.Key,
            Instrument = market.Instrument,
            LastSnapshotVersion = item.Value.LastSnapshotVersion,
            LastAvailableAt = item.Value.LastAvailableAt,
            LastBuildDuration = item.Value.LastBuildDuration,
            CacheHits = item.Value.CacheHits,
            CacheMisses = item.Value.CacheMisses,
            FailureCount = item.Value.FailureCount,
            DependentAgentCount = profileDependentAgentCounts?.GetValueOrDefault(item.Key) ?? 0
        }).ToArray();

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

        var closedThisTick = closed.Select(item => item.Interval).ToHashSet();
        var failedProfiles = new HashSet<AnalysisProfileKey>();
        if (profileAnnotators is { Count: > 0 })
        {
            foreach ((AnalysisProfileKey profile, IChartAnnotator profileAnnotator) in
                     profileAnnotators.OrderBy(item => item.Key.ProfileHash, StringComparer.Ordinal))
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    Dictionary<BarInterval, AnalysisSnapshot> latest = _latestByProfile[profile];
                    foreach (CandleClosedEvent candleEvent in closed)
                    {
                        AnalysisSnapshot snapshot = await profileAnnotator
                            .ProcessAsync(candleEvent, runtimeContext: null, cancellationToken)
                            .ConfigureAwait(false);
                        latest[candleEvent.Interval] = snapshot;
                    }
                    _profileCounters[profile].LastBuildDuration = Stopwatch.GetElapsedTime(started);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedProfiles.Add(profile);
                    _profileCounters[profile].FailureCount++;
                    logger.LogError(
                        ex,
                        "Analysis profile {ProfileHash} failed for {Instrument}; dependent agents are skipped.",
                        profile.ProfileHash,
                        market.Instrument);
                }
            }
        }
        else
        {
            foreach (CandleClosedEvent candleEvent in closed)
            {
                AnalysisSnapshot snapshot = await annotator
                    .ProcessAsync(candleEvent, runtimeContext: null, cancellationToken)
                    .ConfigureAwait(false);
                _latestByInterval[candleEvent.Interval] = snapshot;
            }
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

        long sequence = _marketSequence + 1;
        DateTimeOffset availableAt = _lastConfirmedClose;
        IReadOnlyDictionary<AnalysisProfileKey, MarketAnalysisSnapshot> snapshotsByProfile;
        MultiTimeframeAnalysis analysis;
        DataQualityResult quality;

        if (profileAnnotators is { Count: > 0 })
        {
            var published = new Dictionary<AnalysisProfileKey, MarketAnalysisSnapshot>();
            var qualities = new List<DataQualityResult>(profileAnnotators.Count);
            foreach (AnalysisProfileKey profile in profileAnnotators.Keys
                         .OrderBy(item => item.ProfileHash, StringComparer.Ordinal))
            {
                if (failedProfiles.Contains(profile) || !IsProfileReady(profile, timeProvider.GetUtcNow()))
                    continue;
                try
                {
                    Dictionary<BarInterval, AnalysisSnapshot> latest = _latestByProfile[profile];
                    var profileAnalysis = new MultiTimeframeAnalysis(
                        market.Instrument,
                        availableAt,
                        profile.RequiredIntervals.ToDictionary(interval => interval, interval => latest[interval]));
                    IMarketDataQualityGate gate = profileQualityGates is not null &&
                                                  profileQualityGates.TryGetValue(profile, out IMarketDataQualityGate? configured)
                        ? configured
                        : qualityGate;
                    DataQualityResult profileQuality = gate.Evaluate(profileAnalysis);
                    qualities.Add(profileQuality);
                    published.Add(profile, MarketAnalysisSnapshot.Create(
                        market.Instrument,
                        profile,
                        sequence,
                        sequence,
                        availableAt,
                        profileAnalysis.Timeframes,
                        crossMarket: null,
                        profileQuality));
                    ProfileCounters counters = _profileCounters[profile];
                    counters.LastSnapshotVersion = sequence;
                    counters.LastAvailableAt = availableAt;
                    counters.CacheMisses++;
                    counters.CacheHits += Math.Max(
                        0,
                        (profileDependentAgentCounts?.GetValueOrDefault(profile) ?? 0) - 1);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedProfiles.Add(profile);
                    _profileCounters[profile].FailureCount++;
                    logger.LogError(
                        ex,
                        "Analysis profile {ProfileHash} could not publish for {Instrument}; dependent agents are skipped.",
                        profile.ProfileHash,
                        market.Instrument);
                }
            }

            if (published.Count == 0)
            {
                State = LiveMarketState.Degraded;
                return;
            }

            snapshotsByProfile = published.ToFrozenDictionary();
            MarketAnalysisSnapshot primary = published
                .OrderBy(item => item.Key.ProfileHash, StringComparer.Ordinal)
                .First().Value;
            analysis = new MultiTimeframeAnalysis(primary.Instrument, primary.AvailableAt, primary.Timeframes);
            quality = CombineQuality(qualities);
        }
        else
        {
            analysis = new MultiTimeframeAnalysis(
                market.Instrument,
                availableAt,
                market.AnalysisIntervals.ToDictionary(interval => interval, interval => _latestByInterval[interval]));
            quality = qualityGate.Evaluate(analysis);
            snapshotsByProfile = new Dictionary<AnalysisProfileKey, MarketAnalysisSnapshot>();
        }

        _recentIssues = quality.Issues;
        State = quality.ShouldTrip || failedProfiles.Count > 0
            ? LiveMarketState.Degraded
            : LiveMarketState.Ready;
        _marketSequence = sequence;

        await output.WriteAsync(
            new MarketAnalysisUpdate
            {
                Instrument = market.Instrument,
                AvailableAt = analysis.Timestamp,
                ClosedIntervals = closedThisTick,
                Analysis = analysis,
                SnapshotsByProfile = snapshotsByProfile,
                Health = BuildHealthSnapshot(quality),
                MarketSequence = sequence
            },
            cancellationToken).ConfigureAwait(false);
    }

    private LiveAnalysisReadiness BuildReadiness()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var components = new List<ComponentReadiness>(market.AnalysisIntervals.Count);
        bool ready = true;

        IEnumerable<(string Component, BarInterval Interval, AnalysisSnapshot? Snapshot)> required =
            profileAnnotators is { Count: > 0 }
                ? _latestByProfile.SelectMany(profile => profile.Key.RequiredIntervals.Select(interval =>
                    ($"Analysis:{profile.Key.ProfileHash[..Math.Min(12, profile.Key.ProfileHash.Length)]}",
                     interval,
                     profile.Value.GetValueOrDefault(interval))))
                : market.AnalysisIntervals.Select(interval =>
                    ("Analysis", interval, _latestByInterval.GetValueOrDefault(interval)));

        foreach ((string component, BarInterval interval, AnalysisSnapshot? snapshot) in required)
        {
            bool hasSnapshot = snapshot is not null;
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
                Component = component,
                Interval = interval,
                RequiredSamples = 1,
                AvailableSamples = indicatorsReady ? 1 : 0,
                Status = indicatorsReady ? "Ready" : "WarmingUp"
            });
        }

        if (profileAnnotators is { Count: > 0 })
            ready = profileAnnotators.Keys.Any(profile => IsProfileReady(profile, now));

        return new LiveAnalysisReadiness
        {
            Instrument = market.Instrument,
            Ready = ready,
            Components = components
        };
    }

    private bool IsProfileReady(AnalysisProfileKey profile, DateTimeOffset now) =>
        profile.RequiredIntervals.All(interval =>
            _latestByProfile[profile].TryGetValue(interval, out AnalysisSnapshot? snapshot) &&
            snapshot.AvailableAt <= now &&
            snapshot.Indicators.Atr is not null &&
            snapshot.Indicators.Rsi is not null &&
            snapshot.Indicators.BollingerMiddle is not null);

    private static DataQualityResult CombineQuality(IReadOnlyCollection<DataQualityResult> results)
    {
        if (results.All(item => item.Issues.Count == 0))
            return DataQualityResult.Valid;

        return new DataQualityResult
        {
            IsValid = results.All(item => item.IsValid),
            ShouldTrip = results.Any(item => item.ShouldTrip),
            Issues = results.SelectMany(item => item.Issues).ToArray()
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

    private sealed class ProfileCounters
    {
        public long LastSnapshotVersion { get; set; }
        public DateTimeOffset? LastAvailableAt { get; set; }
        public TimeSpan LastBuildDuration { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long FailureCount { get; set; }
    }
}

public sealed record AnalysisProfileActorStatus
{
    public required AnalysisProfileKey Profile { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required long LastSnapshotVersion { get; init; }
    public DateTimeOffset? LastAvailableAt { get; init; }
    public required TimeSpan LastBuildDuration { get; init; }
    public required long CacheHits { get; init; }
    public required long CacheMisses { get; init; }
    public required long FailureCount { get; init; }
    public required int DependentAgentCount { get; init; }
}
