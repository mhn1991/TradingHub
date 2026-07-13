using System.Diagnostics;
using Agent.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using Simulator.Abstractions;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Replay;

namespace Simulator.Engine;

public sealed class StreamingComparativeEngineOptions
{
    public required Guid SimulationId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset EvaluationFrom { get; init; }
    public required DateTimeOffset EvaluationTo { get; init; }
    public required DateTimeOffset StreamFrom { get; init; }
    public required IReadOnlyList<BarInterval> AnalysisIntervals { get; init; }
    public required BacktestRuntimeOptions Runtime { get; init; }
    public required SimulationOptions SimulationOptions { get; init; }
    public required string OutputDirectory { get; init; }
    public required string InputStreamId { get; init; }
    public ChartAnnotationOptions? AnnotationOptions { get; init; }
    public IProgress<BacktestProgress>? Progress { get; init; }
    public Func<SimulationJobStatus, Task>? StatusChanged { get; init; }
    public Simulator.Jobs.IAsyncPauseGate? PauseGate { get; init; }
}

/// <summary>
/// One-minute canonical clock with shared analysis, isolated strategy sessions,
/// sequential or parallel workers, and a per-frame barrier.
/// </summary>
public sealed class StreamingComparativeEngine
{
    private readonly IHistoricalCandleStream _stream;
    private readonly IReadOnlyList<(string Id, ITradingAgent Agent)> _strategies;

    public StreamingComparativeEngine(
        IHistoricalCandleStream stream,
        IReadOnlyList<(string Id, ITradingAgent Agent)> strategies)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.", nameof(strategies));
    }

    public async Task<ComparativeSimulationResult> RunAsync(
        StreamingComparativeEngineOptions options,
        HistoricalCandleRequest candleRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(candleRequest);
        // Capability + timeframe validation (rejects OANDA+1s, misaligned intervals, etc.).
        options.Runtime.Validate(_strategies.Count);
        SimulationTimeframeOptions timeframes = options.Runtime.ToTimeframeOptions();
        IReadOnlyList<BarInterval> effectiveAnalysis = timeframes.EffectiveAnalysisIntervals(
            _strategies.SelectMany(s => s.Agent.RequiredIntervals));

        var stopwatch = Stopwatch.StartNew();
        await RaiseStatusAsync(options, SimulationJobStatus.PreparingData).ConfigureAwait(false);

        var quality = new MarketDataQualityTracker(options.Runtime.ExecutionInterval);
        // Stage 1: execution → analysis base (e.g. 5s → 1m). Stage 2: multi-TF from analysis base.
        var analysisBaseAggregator = new AnalysisBaseAggregator(
            options.Instrument,
            timeframes.ExecutionInterval,
            timeframes.AnalysisBaseInterval,
            options.Runtime.BaseCandleGapPolicy,
            options.Runtime.CandleCapacity);
        var aggregator = new MultiTimeframeAggregator(
            options.Instrument,
            effectiveAnalysis,
            options.Runtime.CandleCapacity,
            options.Runtime.BaseCandleGapPolicy);
        var sharedAnnotator = new ChartAnnotationEngine(options.AnnotationOptions);
        var latestSnapshots = new Dictionary<BarInterval, AnalysisSnapshot>();

        var sessions = new List<StrategySimulationSession>(_strategies.Count);
        foreach ((string id, ITradingAgent agent) in _strategies)
        {
            sessions.Add(StrategySimulationSession.Create(
                id,
                agent,
                options.SimulationOptions,
                analysisSharing: options.Runtime.AnalysisSharingMode,
                annotationOptions: options.AnnotationOptions));
        }

        // Persistent workers: one task/thread per strategy for the whole simulation.
        var workerHosts = new List<StrategyWorkerHost>();
        if (options.Runtime.StrategyExecutionMode == StrategyExecutionMode.ParallelWorkers)
        {
            foreach (StrategySimulationSession session in sessions)
            {
                workerHosts.Add(new StrategyWorkerHost(
                    session,
                    options.Runtime.StrategyChannelCapacity,
                    options.Runtime.StrategyWorkerMode));
            }
        }

        await using var replayWriter = new ChunkedReplayWriter(
            options.OutputDirectory,
            options.Runtime.ReplayChunkSize);

        await replayWriter.WriteManifestAsync(new SimulationManifest
        {
            SimulationId = options.SimulationId,
            SchemaVersion = 1,
            Instrument = options.Instrument.Value,
            From = options.EvaluationFrom,
            To = options.EvaluationTo,
            WarmupFrom = options.StreamFrom < options.EvaluationFrom ? options.StreamFrom : null,
            BaseInterval = StreamingCandleCache.FormatInterval(options.Runtime.ExecutionInterval),
            AnalysisIntervals = effectiveAnalysis
                .Select(StreamingCandleCache.FormatInterval)
                .ToArray(),
            Strategies = _strategies.Select(item => item.Id).ToArray(),
            InputStreamId = options.InputStreamId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "Running"
        }, cancellationToken).ConfigureAwait(false);

        IHistoricalCandleStream stream = CreatePrefetchStream(_stream, options.Runtime);

        long sequence = 0;
        long processed = 0;
        DateTimeOffset? currentMarketTime = null;
        DateTimeOffset lastProgressPublish = DateTimeOffset.MinValue;
        var strategyResults = new List<StrategySimulationResult>();
        bool enteredEvaluation = false;
        AnalysisSnapshotSet snapshotSet = new()
        {
            Version = 0,
            Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>()
        };
        IReadOnlySet<BarInterval> emptyClosed = new HashSet<BarInterval>();

        try
        {
            // Peek buffer so we can mark the final candle.
            var lookAhead = new Queue<MarketCandle>();
            await using IAsyncEnumerator<MarketCandle> enumerator =
                stream.StreamAsync(candleRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);

            async Task<bool> MoveNextBufferedAsync()
            {
                if (lookAhead.Count > 0)
                    return true;
                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    lookAhead.Enqueue(enumerator.Current);
                    return true;
                }

                return false;
            }

            async Task<MarketCandle?> TakeNextAsync()
            {
                if (!await MoveNextBufferedAsync().ConfigureAwait(false))
                    return null;
                MarketCandle current = lookAhead.Dequeue();
                // Prefetch one more to know if current is last.
                _ = await MoveNextBufferedAsync().ConfigureAwait(false);
                return current;
            }

            await RaiseStatusAsync(options, SimulationJobStatus.WarmingUp).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Pause only between committed frames.
                if (options.PauseGate is not null)
                    await options.PauseGate.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

                MarketCandle? marketCandle = await TakeNextAsync().ConfigureAwait(false);
                if (marketCandle is null)
                    break;

                bool isLast = lookAhead.Count == 0;
                Candle baseCandle = marketCandle.Mid;
                quality.Observe(baseCandle);
                sequence++;
                processed++;
                currentMarketTime = marketCandle.AvailableAt;
                bool isWarmup = baseCandle.OpenTime < options.EvaluationFrom;

                if (!isWarmup && !enteredEvaluation)
                {
                    enteredEvaluation = true;
                    await RaiseStatusAsync(options, SimulationJobStatus.Running).ConfigureAwait(false);
                }
                else if (isWarmup && processed == 1)
                {
                    await RaiseStatusAsync(options, SimulationJobStatus.WarmingUp).ConfigureAwait(false);
                }

                // Two-stage aggregation:
                //   execution candle → analysis-base (e.g. 1m)
                //   analysis-base → higher analysis intervals (3m/5m/15m/…)
                // ChartAnnotator only sees completed analysis candles, never raw 1s/5s.
                IReadOnlyList<Candle> analysisBaseClosed =
                    analysisBaseAggregator.ApplyExecutionCandle(baseCandle);

                var closed = new HashSet<BarInterval>();
                foreach (Candle analysisBaseCandle in analysisBaseClosed)
                {
                    IReadOnlyList<CandleClosedEvent> closedEvents = aggregator.Apply(analysisBaseCandle);
                    foreach (CandleClosedEvent closedEvent in closedEvents)
                    {
                        closed.Add(closedEvent.Interval);
                        AnalysisSnapshot snapshot = await sharedAnnotator
                            .ProcessAsync(closedEvent, cancellationToken)
                            .ConfigureAwait(false);
                        if (options.Runtime.AnalysisSharingMode == AnalysisSharingMode.IndependentPerStrategy)
                        {
                            foreach (StrategySimulationSession session in sessions)
                            {
                                if (session.IndependentAnnotator is not null)
                                {
                                    await session.IndependentAnnotator
                                        .ProcessAsync(closedEvent, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }

                        latestSnapshots[closedEvent.Interval] = snapshot;
                    }
                }

                IReadOnlySet<BarInterval> closedIntervals = closed.Count == 0
                    ? emptyClosed
                    : closed;
                if (closed.Count > 0)
                {
                    snapshotSet = new AnalysisSnapshotSet
                    {
                        Version = snapshotSet.Version + 1,
                        Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>(latestSnapshots)
                    };
                }

                // Frame is always on the execution clock for fills; strategy evaluates only
                // when its trigger interval is in ClosedIntervals (after analysis-base close).
                var frame = new MarketFrame
                {
                    Sequence = sequence,
                    AvailableAt = marketCandle.AvailableAt,
                    ExecutionCandle = marketCandle,
                    ClosedIntervals = closedIntervals,
                    Snapshots = snapshotSet.Snapshots,
                    InputStreamId = options.InputStreamId,
                    IsWarmup = isWarmup,
                    IsLastCandle = isLast
                };

                // Only active (non-failed) strategies participate in the barrier.
                List<StrategyWorkerHost> activeHosts = workerHosts
                    .Where(host => !host.Session.IsFailed)
                    .ToList();
                List<StrategySimulationSession> activeSessions = sessions
                    .Where(session => !session.IsFailed)
                    .ToList();

                if (activeSessions.Count == 0)
                {
                    throw new InvalidOperationException(
                        "All strategies have failed; stopping comparison.");
                }

                StrategyFrameResult[] frameResults;
                try
                {
                    frameResults = options.Runtime.StrategyExecutionMode switch
                    {
                        StrategyExecutionMode.ParallelWorkers =>
                            await ProcessParallelWorkersAsync(activeHosts, frame, cancellationToken)
                                .ConfigureAwait(false),
                        _ => await ProcessSequentialAsync(activeSessions, frame, cancellationToken)
                            .ConfigureAwait(false)
                    };
                }
                catch (Exception exception) when (
                    options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopFailedStrategyOnly)
                {
                    // Sequential path: mark the first non-failed session that threw.
                    StrategySimulationSession? culprit = activeSessions.FirstOrDefault(s => s.IsFailed)
                        ?? activeSessions.FirstOrDefault();
                    culprit?.MarkFailed(frame.Sequence, exception.ToString());
                    if (sessions.All(s => s.IsFailed))
                        throw;
                    continue;
                }

                foreach (StrategyFrameResult result in frameResults)
                {
                    if (result.Sequence != frame.Sequence)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected sequence from {result.StrategyName}: {result.Sequence} != {frame.Sequence}.");
                    }
                }

                await replayWriter.CommitFrameAsync(frame, frameResults, cancellationToken)
                    .ConfigureAwait(false);

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if ((now - lastProgressPublish).TotalMilliseconds >=
                    options.Runtime.ProgressPublishIntervalMilliseconds)
                {
                    lastProgressPublish = now;
                    PublishProgress(options, processed, currentMarketTime.Value, sessions, isWarmup);
                }

                StrategySimulationSession? failed = sessions.FirstOrDefault(session => session.IsFailed);
                if (failed is not null &&
                    options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopEntireComparison)
                {
                    throw new InvalidOperationException(
                        $"Strategy '{failed.StrategyName}' failed at sequence {failed.FailedSequence}: {failed.FailureMessage}");
                }
            }

            foreach (StrategyWorkerHost host in workerHosts)
                host.Complete();
            if (workerHosts.Count > 0)
                await Task.WhenAll(workerHosts.Select(host => host.Completion)).ConfigureAwait(false);

            await RaiseStatusAsync(options, SimulationJobStatus.Exporting).ConfigureAwait(false);

            for (int i = 0; i < sessions.Count; i++)
            {
                StrategySimulationSession session = sessions[i];
                SimulationResult simulationResult = await session
                    .BuildSimulationResultAsync(cancellationToken)
                    .ConfigureAwait(false);
                StrategyWorkerMetrics metrics = workerHosts.Count > i
                    ? workerHosts[i].SnapshotMetrics()
                    : session.BuildMetrics();
                strategyResults.Add(new StrategySimulationResult
                {
                    StrategyId = session.StrategyId,
                    StrategyName = session.StrategyName,
                    Result = simulationResult,
                    Metrics = metrics,
                    IsComplete = !session.IsFailed,
                    FailedAtSequence = session.FailedSequence,
                    FailureMessage = session.FailureMessage
                });
            }

            MarketDataQualityReport qualityReport = quality.BuildReport();
            await replayWriter.CompleteAsync(strategyResults, qualityReport, cancellationToken)
                .ConfigureAwait(false);

            await replayWriter.WriteManifestAsync(new SimulationManifest
            {
                SimulationId = options.SimulationId,
                SchemaVersion = 1,
                Instrument = options.Instrument.Value,
                From = options.EvaluationFrom,
                To = options.EvaluationTo,
                WarmupFrom = options.StreamFrom < options.EvaluationFrom ? options.StreamFrom : null,
                BaseInterval = StreamingCandleCache.FormatInterval(options.Runtime.ExecutionInterval),
                AnalysisIntervals = effectiveAnalysis
                    .Select(StreamingCandleCache.FormatInterval)
                    .ToArray(),
                Strategies = _strategies.Select(item => item.Id).ToArray(),
                InputStreamId = options.InputStreamId,
                InputHash = qualityReport.InputHash,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = "Completed"
            }, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            await RaiseStatusAsync(options, SimulationJobStatus.Completed).ConfigureAwait(false);
            PublishProgress(options, processed, currentMarketTime ?? options.EvaluationTo, sessions, false);

            return new ComparativeSimulationResult
            {
                SimulationId = options.SimulationId,
                InputStreamId = options.InputStreamId,
                InputHash = qualityReport.InputHash,
                DataQuality = qualityReport,
                Strategies = strategyResults,
                OutputDirectory = options.OutputDirectory,
                TotalDuration = stopwatch.Elapsed,
                ProcessedBaseCandles = processed,
                FillModel = options.Runtime.UseHistoricalBidAsk
                    ? FillModel.MidpointPlusConfiguredSpread
                    : FillModel.SyntheticSpreadModel
            };
        }
        finally
        {
            foreach (StrategyWorkerHost host in workerHosts)
                await host.DisposeAsync().ConfigureAwait(false);
            if (workerHosts.Count == 0)
            {
                foreach (StrategySimulationSession session in sessions)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IHistoricalCandleStream CreatePrefetchStream(
        IHistoricalCandleStream stream,
        BacktestRuntimeOptions runtime)
    {
        // Sources with full StreamAsync semantics (OANDA cache/progress, incremental write)
        // must not be reduced to bare ReadPageAsync — that bypasses caching and progress.
        if (stream is IHistoricalCandleStreamWithProgress)
        {
            return new PrefetchingCandleStream(
                stream,
                runtime.PrefetchCapacity,
                runtime.PrefetchLowWatermark);
        }

        // Pure paged fakes/tests use low-watermark page acquisition.
        if (stream is IPagedHistoricalCandleSource paged)
        {
            return new LowWatermarkPrefetchStream(
                paged,
                runtime.PrefetchCapacity,
                runtime.PrefetchLowWatermark);
        }

        if (runtime.PrefetchCapacity > runtime.SourcePageSize)
        {
            return new PrefetchingCandleStream(
                stream,
                runtime.PrefetchCapacity,
                runtime.PrefetchLowWatermark);
        }

        return stream;
    }

    private static async Task<StrategyFrameResult[]> ProcessSequentialAsync(
        IReadOnlyList<StrategySimulationSession> sessions,
        MarketFrame frame,
        CancellationToken cancellationToken)
    {
        var results = new List<StrategyFrameResult>(sessions.Count);
        foreach (StrategySimulationSession session in sessions)
        {
            if (session.IsFailed)
                continue;
            try
            {
                results.Add(await session.ProcessFrameAsync(frame, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                session.MarkFailed(frame.Sequence, exception.ToString());
                throw;
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// Publishes one frame to each persistent worker and awaits the barrier.
    /// Per-worker failures set session.IsFailed without aborting Task.WhenAll aggregation.
    /// </summary>
    private static async Task<StrategyFrameResult[]> ProcessParallelWorkersAsync(
        IReadOnlyList<StrategyWorkerHost> workers,
        MarketFrame frame,
        CancellationToken cancellationToken)
    {
        if (workers.Count == 0)
            return [];

        var resultTasks = new Task<StrategyFrameResult>[workers.Count];
        for (int i = 0; i < workers.Count; i++)
        {
            resultTasks[i] = await workers[i].EnqueueAsync(frame, cancellationToken)
                .ConfigureAwait(false);
        }

        StrategyFrameResult[] results = new StrategyFrameResult[workers.Count];
        for (int i = 0; i < resultTasks.Length; i++)
        {
            try
            {
                results[i] = await resultTasks[i].ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Worker already marked session failed; synthesise a barrier placeholder.
                StrategySimulationSession session = workers[i].Session;
                if (!session.IsFailed)
                    session.MarkFailed(frame.Sequence, exception.ToString());
                results[i] = new StrategyFrameResult
                {
                    StrategyId = session.StrategyId,
                    StrategyName = session.StrategyName,
                    Sequence = frame.Sequence,
                    Balance = 0m,
                    Equity = 0m,
                    UnrealizedProfitLoss = 0m,
                    OpenPositions = 0,
                    CompletedTrades = session.Trades.Count,
                    ActiveSetups = 0,
                    Status = "Failed"
                };
            }
        }

        return results;
    }

    private static void PublishProgress(
        StreamingComparativeEngineOptions options,
        long processed,
        DateTimeOffset marketTime,
        IReadOnlyList<StrategySimulationSession> sessions,
        bool isWarmup)
    {
        if (options.Progress is null)
            return;

        long? estimated = EstimateCandleCount(
            options.StreamFrom,
            options.EvaluationTo,
            options.Runtime.ExecutionInterval);
        decimal percent = estimated is > 0
            ? Math.Round(100m * processed / estimated.Value, 2)
            : 0m;
        decimal cps = 0m;
        // Rough candles/sec from processed and wall time is computed by the service layer.

        options.Progress.Report(new BacktestProgress
        {
            SimulationId = options.SimulationId,
            Status = isWarmup ? SimulationJobStatus.WarmingUp : SimulationJobStatus.Running,
            CurrentMarketTime = marketTime,
            EvaluationStart = options.EvaluationFrom,
            EvaluationEnd = options.EvaluationTo,
            ProcessedBaseCandles = processed,
            EstimatedTotalBaseCandles = estimated,
            ProgressPercent = Math.Min(100m, percent),
            CandlesPerSecond = cps,
            Strategies = sessions.Select(session => session.ToProgressSnapshot()).ToArray()
        });
    }

    private static async Task RaiseStatusAsync(
        StreamingComparativeEngineOptions options,
        SimulationJobStatus status)
    {
        if (options.StatusChanged is not null)
            await options.StatusChanged(status).ConfigureAwait(false);
    }

    public static long? EstimateCandleCount(
        DateTimeOffset from,
        DateTimeOffset to,
        BarInterval interval)
    {
        TimeSpan span = to - from;
        double minutes = span.TotalMinutes;
        double stepMinutes = interval.Unit switch
        {
            BarUnit.Second => interval.Value / 60.0,
            BarUnit.Minute => interval.Value,
            BarUnit.Hour => interval.Value * 60.0,
            BarUnit.Day => interval.Value * 60.0 * 24.0,
            _ => 1.0
        };
        if (stepMinutes <= 0)
            return null;
        // Rough weekday approximation for FX (~5/7).
        return (long)(minutes / stepMinutes * 5.0 / 7.0);
    }
}
