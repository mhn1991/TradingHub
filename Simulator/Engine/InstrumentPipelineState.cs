using Brokers.Models;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using Simulator.Abstractions;
using Simulator.MarketData;
using Simulator.Models;
using TradingCore.Pipeline;

namespace Simulator.Engine;

/// <summary>
/// Per-instrument candle sourcing and analysis-aggregation state for the multi-instrument
/// portfolio clock. One instance exists per distinct instrument traded in a run. Analysis
/// engines themselves live in the run's shared <see cref="AnalysisProfileRegistry"/> - possibly
/// more than one per instrument, one per distinct <see cref="AnalysisProfileKey"/> needed by any
/// session trading it - but aggregation, quality tracking, and per-interval snapshot state are
/// hard-scoped to one instrument each (both <see cref="AnalysisBaseAggregator"/> and
/// <see cref="MultiTimeframeAggregator"/> throw if fed a candle for a different instrument),
/// so each traded instrument needs its own copy of this state.
/// </summary>
internal sealed class InstrumentPipelineState : IAsyncDisposable
{
    private readonly IAsyncEnumerator<MarketCandle> _enumerator;
    private readonly Queue<MarketCandle> _lookAhead = new();

    public InstrumentPipelineState(
        InstrumentKey instrument,
        IHistoricalCandleStream stream,
        HistoricalCandleRequest candleRequest,
        BarInterval executionInterval,
        BarInterval analysisBaseInterval,
        IReadOnlyList<BarInterval> analysisIntervals,
        BaseCandleGapPolicy gapPolicy,
        double gapToleranceFraction,
        int candleCapacity,
        CancellationToken cancellationToken)
    {
        Instrument = instrument;
        Quality = new MarketDataQualityTracker(executionInterval);
        AnalysisBaseAggregator = new AnalysisBaseAggregator(
            instrument,
            executionInterval,
            analysisBaseInterval,
            gapPolicy,
            candleCapacity);
        MultiTimeframeAggregator = new MultiTimeframeAggregator(
            instrument,
            analysisIntervals,
            candleCapacity,
            gapPolicy,
            gapToleranceFraction);
        _enumerator = stream.StreamAsync(candleRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    public InstrumentKey Instrument { get; }
    public MarketDataQualityTracker Quality { get; }
    public AnalysisBaseAggregator AnalysisBaseAggregator { get; }
    public MultiTimeframeAggregator MultiTimeframeAggregator { get; }

    /// <summary>Working (mutating) latest-snapshot-per-interval state, one dictionary per
    /// distinct analysis profile trading this instrument.</summary>
    private readonly Dictionary<AnalysisProfileKey, Dictionary<BarInterval, AnalysisSnapshot>> _latestSnapshotsByProfile = new();

    /// <summary>Published (immutable, versioned) snapshot sets, one per distinct analysis
    /// profile trading this instrument. A new <see cref="AnalysisSnapshotSet"/> replaces the
    /// prior one wholesale on every candle that closes at least one interval for that profile -
    /// existing frames retain their own captured reference, never mutated after publish.</summary>
    public Dictionary<AnalysisProfileKey, AnalysisSnapshotSet> SnapshotSetsByProfile { get; } = new();

    public Dictionary<BarInterval, AnalysisSnapshot> LatestSnapshotsFor(AnalysisProfileKey profile)
    {
        if (!_latestSnapshotsByProfile.TryGetValue(profile, out Dictionary<BarInterval, AnalysisSnapshot>? latest))
        {
            _latestSnapshotsByProfile[profile] = latest = new Dictionary<BarInterval, AnalysisSnapshot>();
            SnapshotSetsByProfile[profile] = new AnalysisSnapshotSet
            {
                Version = 0,
                Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>()
            };
        }
        return latest;
    }

    public decimal? LastCorrelationClose { get; set; }

    /// <summary>Set once this instrument's own stream has produced at least one non-warmup candle.</summary>
    public bool EnteredEvaluation { get; set; }

    private bool _exhausted;

    /// <summary>Ensures a candle is buffered (a no-op if one already is or the stream is exhausted).</summary>
    public async Task<bool> MoveNextBufferedAsync()
    {
        if (_lookAhead.Count > 0)
            return true;
        if (_exhausted)
            return false;
        if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            _lookAhead.Enqueue(_enumerator.Current);
            return true;
        }

        _exhausted = true;
        return false;
    }

    public MarketCandle? PeekBuffered() => _lookAhead.Count > 0 ? _lookAhead.Peek() : null;

    /// <summary>Dequeues the buffered candle and reports whether it was this instrument's last one.</summary>
    public async Task<(MarketCandle Candle, bool IsLast)> TakeBufferedAsync()
    {
        MarketCandle candle = _lookAhead.Dequeue();
        bool isLast = !await MoveNextBufferedAsync().ConfigureAwait(false);
        return (candle, isLast);
    }

    public ValueTask DisposeAsync() => _enumerator.DisposeAsync();
}
