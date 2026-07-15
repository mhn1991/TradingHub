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
using RiskManager.Calibration;

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
    public string? SimulationConfigurationId { get; init; }
    public ChartAnnotationOptions? AnnotationOptions { get; init; }
    public IProgress<BacktestProgress>? Progress { get; init; }
    public Func<SimulationJobStatus, Task>? StatusChanged { get; init; }
    public Func<string, SimulatedTradeRecord, Task>? TradeCompleted { get; init; }
    public ISetupMetaModel? MetaLabelModel { get; init; }
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
            _strategies.SelectMany(s => s.Agent.RequiredIntervals)
                .Concat(_strategies.SelectMany(s => options.Runtime.ResolveManagementIntervals(s.Id))));

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
        SharedPortfolioRuntime? sharedPortfolio = options.Runtime.AccountMode == SimulationAccountMode.SharedPortfolioAccount
            ? new SharedPortfolioRuntime(
                options.SimulationOptions,
                options.Runtime.PortfolioRisk,
                options.Runtime.PositionSizing,
                options.Runtime.AdaptiveRisk,
                options.Runtime.SafetyOptions)
            : null;
        foreach ((string id, ITradingAgent agent) in _strategies)
        {
            StrategySimulationSession session = StrategySimulationSession.Create(
                id,
                agent,
                options.SimulationOptions,
                safetyOptions: options.Runtime.SafetyOptions,
                analysisSharing: options.Runtime.AnalysisSharingMode,
                annotationOptions: options.AnnotationOptions,
                positionManagementOptions: options.Runtime.GetPositionManagement(id),
                managementInterval: options.Runtime.ResolveManagementInterval(id),
                positionSizingOptions: options.Runtime.PositionSizing,
                adaptiveRiskOptions: options.Runtime.AdaptiveRisk,
                tradingConditionOptions: options.Runtime.TradingConditions,
                regimeManagementOptions: options.Runtime.RegimeManagement,
                setupCalibrationOptions: options.Runtime.SetupCalibration,
                setupCalibrationArtifact: options.Runtime.SetupCalibrationArtifact,
                metaModel: options.MetaLabelModel,
                managementCalibrationOptions: options.Runtime.ManagementCalibration,
                managementCalibrationArtifact: options.Runtime.ManagementCalibrationArtifact,
                executionDecorator: sharedPortfolio is null ? null : sharedPortfolio.Decorate);
            sessions.Add(session);
            sharedPortfolio?.Register(session);
        }

        // Persistent workers: one task/thread per strategy for the whole simulation.
        var workerHosts = new List<StrategyWorkerHost>();
        if (options.Runtime.StrategyExecutionMode == StrategyExecutionMode.ParallelWorkers)
        {
            foreach (StrategySimulationSession session in sessions)
            {
                workerHosts.Add(new StrategyWorkerHost(
                    session,
                    options.Runtime.StrategyChannelCapacity));
            }
        }

        await using var replayWriter = new ChunkedReplayWriter(
            options.OutputDirectory,
            options.Runtime.ReplayChunkSize,
            options.Runtime.ExecutionDetailPreEntryFrames,
            options.Runtime.ExecutionDetailPostExitFrames);

        await replayWriter.WriteManifestAsync(new SimulationManifest
        {
            SimulationId = options.SimulationId,
            SchemaVersion = 2,
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
            SimulationConfigurationId = options.SimulationConfigurationId,
            SourceKind = options.Runtime.SourceKind.ToString(),
            PrecisionMode = options.Runtime.PrecisionMode.ToString(),
            AnalysisBaseInterval = StreamingCandleCache.FormatInterval(options.Runtime.AnalysisBaseInterval),
            TrendInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.TrendInterval),
            SecondaryTrendIntervals = options.Runtime.StrategyTimeframes.SecondaryTrendIntervals
                .Select(StreamingCandleCache.FormatInterval).ToArray(),
            SetupIntervals = options.Runtime.StrategyTimeframes.SetupIntervals
                .Select(StreamingCandleCache.FormatInterval).ToArray(),
            ConfirmationInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.ConfirmationInterval),
            AdditionalConfirmationIntervals = options.Runtime.StrategyTimeframes.AdditionalConfirmationIntervals
                .Select(StreamingCandleCache.FormatInterval).ToArray(),
            EntryInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.EntryInterval),
            MinimumSecondaryTrendAlignments = options.Runtime.StrategyTimeframes.MinimumSecondaryTrendAlignments,
            MinimumSetupAlignments = options.Runtime.StrategyTimeframes.MinimumSetupAlignments,
            MinimumConfirmationAlignments = options.Runtime.StrategyTimeframes.MinimumConfirmationAlignments,
            StrongOppositionVeto = options.Runtime.StrategyTimeframes.StrongOppositionVeto,
            PositionSizing = options.Runtime.PositionSizing,
            LegacyPositionManagement = options.Runtime.LegacyPositionManagement,
            ImprovedPositionManagement = options.Runtime.ImprovedPositionManagement,
            SafetyOptions = options.Runtime.SafetyOptions,
            SpreadBasisPoints = options.SimulationOptions.SpreadBasisPoints,
            SlippageBasisPoints = options.SimulationOptions.SlippageBasisPoints,
            CommissionRate = options.SimulationOptions.CommissionRate,
            AmbiguityPolicy = options.Runtime.AmbiguousIntrabarPolicy.ToString(),
            FillModel = options.Runtime.Execution.FillModel.ToString(),
            AccountMode = options.Runtime.AccountMode.ToString(),
            RuntimeOptions = options.Runtime,
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
        var pendingExecutionBatch = new List<MarketFrame>();

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
                    if (sharedAnnotator is ICalibratableChartAnnotator sharedCalibration)
                        sharedCalibration.FreezeCalibration(options.EvaluationFrom);
                    foreach (StrategySimulationSession session in sessions)
                    {
                        if (session.IndependentAnnotator is ICalibratableChartAnnotator independentCalibration)
                            independentCalibration.FreezeCalibration(options.EvaluationFrom);
                    }
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
                long incompleteBefore = analysisBaseAggregator.IncompleteAggregateCount;
                IReadOnlyList<Candle> analysisBaseClosed =
                    analysisBaseAggregator.ApplyExecutionCandle(baseCandle);
                quality.RecordIncompleteAggregate(
                    analysisBaseAggregator.IncompleteAggregateCount - incompleteBefore);

                var closed = new HashSet<BarInterval>();
                foreach (Candle analysisBaseCandle in analysisBaseClosed)
                {
                    incompleteBefore = aggregator.IncompleteAggregateCount;
                    IReadOnlyList<CandleClosedEvent> closedEvents = aggregator.Apply(analysisBaseCandle);
                    quality.RecordIncompleteAggregate(
                        aggregator.IncompleteAggregateCount - incompleteBefore);
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
                    AnalysisBaseCandle = analysisBaseClosed.LastOrDefault(),
                    ClosedIntervals = closedIntervals,
                    Snapshots = snapshotSet.Snapshots,
                    InputStreamId = options.InputStreamId,
                    IsWarmup = isWarmup,
                    IsLastCandle = isLast
                };

                pendingExecutionBatch.Add(frame);
                if (frame.AnalysisBaseCandle is null && !frame.IsLastCandle)
                    continue;

                // One worker enqueue/barrier per analysis-base bucket. Each isolated
                // strategy still processes every execution frame in strict order.
                MarketFrame[] executionBatch = pendingExecutionBatch.ToArray();
                pendingExecutionBatch.Clear();

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

                StrategyFrameResult[][] batchResults;
                try
                {
                    if (sharedPortfolio is null)
                    {
                        batchResults = options.Runtime.StrategyExecutionMode switch
                        {
                            StrategyExecutionMode.ParallelWorkers =>
                                await ProcessParallelWorkersBatchAsync(activeHosts, executionBatch, cancellationToken)
                                    .ConfigureAwait(false),
                            _ => await ProcessSequentialBatchAsync(activeSessions, executionBatch, cancellationToken)
                                .ConfigureAwait(false)
                        };
                    }
                    else
                    {
                        // Shared admission is a per-execution-frame barrier. Processing
                        // a whole analysis-base bucket before flushing would let later
                        // candles run before orders from the first candle were ranked.
                        batchResults = new StrategyFrameResult[executionBatch.Length][];
                        for (int batchIndex = 0; batchIndex < executionBatch.Length; batchIndex++)
                        {
                            MarketFrame admissionFrame = executionBatch[batchIndex];
                            StrategyFrameResult[][] oneFrameResults = options.Runtime.StrategyExecutionMode switch
                            {
                                StrategyExecutionMode.ParallelWorkers =>
                                    await ProcessParallelWorkersBatchAsync(activeHosts, [admissionFrame], cancellationToken)
                                        .ConfigureAwait(false),
                                _ => await ProcessSequentialBatchAsync(activeSessions, [admissionFrame], cancellationToken)
                                    .ConfigureAwait(false)
                            };
                            StrategyFrameResult[] frameResults = oneFrameResults[0];
                            IReadOnlyList<StrategyReplayEvent> portfolioEvents = await sharedPortfolio
                                .FlushAsync(admissionFrame.Sequence, admissionFrame.AvailableAt, cancellationToken)
                                .ConfigureAwait(false);
                            for (int strategyIndex = 0; strategyIndex < frameResults.Length; strategyIndex++)
                            {
                                StrategyFrameResult result = frameResults[strategyIndex];
                                StrategyReplayEvent[] additions = portfolioEvents
                                    .Where(item => item.StrategyId == result.StrategyId)
                                    .ToArray();
                                if (additions.Length > 0)
                                {
                                    frameResults[strategyIndex] = result with
                                    {
                                        Events = result.Events.Concat(additions).ToArray()
                                    };
                                }
                            }
                            batchResults[batchIndex] = frameResults;
                        }
                    }
                }
                catch (Exception exception) when (
                    options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopFailedStrategyOnly)
                {
                    // Sequential path: mark the first non-failed session that threw.
                    StrategySimulationSession? culprit = activeSessions.FirstOrDefault(s => s.IsFailed)
                        ?? activeSessions.FirstOrDefault();
                    culprit?.MarkFailed(executionBatch[^1].Sequence, exception.ToString());
                    if (sessions.All(s => s.IsFailed))
                        throw;
                    continue;
                }

                for (int batchIndex = 0; batchIndex < executionBatch.Length; batchIndex++)
                {
                    MarketFrame committedFrame = executionBatch[batchIndex];
                    StrategyFrameResult[] frameResults = batchResults[batchIndex];
                    foreach (StrategyFrameResult result in frameResults)
                    {
                        if (result.Sequence != committedFrame.Sequence)
                        {
                            throw new InvalidOperationException(
                                $"Unexpected sequence from {result.StrategyName}: " +
                                $"{result.Sequence} != {committedFrame.Sequence}.");
                        }
                    }

                    await replayWriter.CommitFrameAsync(committedFrame, frameResults, cancellationToken)
                        .ConfigureAwait(false);
                    if (options.TradeCompleted is not null)
                    {
                        foreach (StrategyFrameResult result in frameResults)
                        {
                            if (result.NewlyCompletedTrade is SimulatedTradeRecord trade)
                            {
                                await options.TradeCompleted(result.StrategyId, trade)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }

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
                SchemaVersion = 2,
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
                SimulationConfigurationId = options.SimulationConfigurationId,
                InputHash = qualityReport.InputHash,
                SourceKind = options.Runtime.SourceKind.ToString(),
                PrecisionMode = options.Runtime.PrecisionMode.ToString(),
                AnalysisBaseInterval = StreamingCandleCache.FormatInterval(options.Runtime.AnalysisBaseInterval),
                TrendInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.TrendInterval),
                SecondaryTrendIntervals = options.Runtime.StrategyTimeframes.SecondaryTrendIntervals
                    .Select(StreamingCandleCache.FormatInterval).ToArray(),
                SetupIntervals = options.Runtime.StrategyTimeframes.SetupIntervals
                    .Select(StreamingCandleCache.FormatInterval).ToArray(),
                ConfirmationInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.ConfirmationInterval),
                AdditionalConfirmationIntervals = options.Runtime.StrategyTimeframes.AdditionalConfirmationIntervals
                    .Select(StreamingCandleCache.FormatInterval).ToArray(),
                EntryInterval = StreamingCandleCache.FormatInterval(options.Runtime.StrategyTimeframes.EntryInterval),
                MinimumSecondaryTrendAlignments = options.Runtime.StrategyTimeframes.MinimumSecondaryTrendAlignments,
                MinimumSetupAlignments = options.Runtime.StrategyTimeframes.MinimumSetupAlignments,
                MinimumConfirmationAlignments = options.Runtime.StrategyTimeframes.MinimumConfirmationAlignments,
                StrongOppositionVeto = options.Runtime.StrategyTimeframes.StrongOppositionVeto,
                PositionSizing = options.Runtime.PositionSizing,
                LegacyPositionManagement = options.Runtime.LegacyPositionManagement,
                ImprovedPositionManagement = options.Runtime.ImprovedPositionManagement,
                SafetyOptions = options.Runtime.SafetyOptions,
                SpreadBasisPoints = options.SimulationOptions.SpreadBasisPoints,
                SlippageBasisPoints = options.SimulationOptions.SlippageBasisPoints,
                CommissionRate = options.SimulationOptions.CommissionRate,
                AmbiguityPolicy = options.Runtime.AmbiguousIntrabarPolicy.ToString(),
                FillModel = options.Runtime.Execution.FillModel.ToString(),
                AccountMode = options.Runtime.AccountMode.ToString(),
                RuntimeOptions = options.Runtime,
                PortfolioPerformance = sharedPortfolio?.Performance,
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
                PortfolioPerformance = sharedPortfolio?.Performance,
                FillModel = options.Runtime.Execution.FillModel switch
                {
                    Simulator.Execution.SimulationFillModel.VariableSyntheticSpread => FillModel.VariableSyntheticSpread,
                    Simulator.Execution.SimulationFillModel.StressExecution => FillModel.StressExecution,
                    Simulator.Execution.SimulationFillModel.HistoricalBidAsk => FillModel.HistoricalBidAsk,
                    _ => FillModel.MidpointPlusConfiguredSpread
                }
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

    private static async Task<StrategyFrameResult[][]> ProcessSequentialBatchAsync(
        IReadOnlyList<StrategySimulationSession> sessions,
        IReadOnlyList<MarketFrame> frames,
        CancellationToken cancellationToken)
    {
        var results = Enumerable.Range(0, frames.Count)
            .Select(_ => new List<StrategyFrameResult>(sessions.Count))
            .ToArray();
        foreach (StrategySimulationSession session in sessions)
        {
            if (session.IsFailed)
                continue;
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                MarketFrame frame = frames[frameIndex];
                try
                {
                    results[frameIndex].Add(await session.ProcessFrameAsync(frame, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    session.MarkFailed(frame.Sequence, exception.ToString());
                    throw;
                }
            }
        }

        return results.Select(frameResults => frameResults.ToArray()).ToArray();
    }

    /// <summary>
    /// Publishes one frame to each persistent worker and awaits the barrier.
    /// Per-worker failures set session.IsFailed without aborting Task.WhenAll aggregation.
    /// </summary>
    private static async Task<StrategyFrameResult[][]> ProcessParallelWorkersBatchAsync(
        IReadOnlyList<StrategyWorkerHost> workers,
        IReadOnlyList<MarketFrame> frames,
        CancellationToken cancellationToken)
    {
        if (workers.Count == 0)
            return frames.Select(_ => Array.Empty<StrategyFrameResult>()).ToArray();

        var resultTasks = new Task<StrategyFrameResult[]>[workers.Count];
        for (int i = 0; i < workers.Count; i++)
        {
            resultTasks[i] = await workers[i].EnqueueBatchAsync(frames, cancellationToken)
                .ConfigureAwait(false);
        }

        var workerResults = new StrategyFrameResult[workers.Count][];
        for (int i = 0; i < resultTasks.Length; i++)
        {
            try
            {
                workerResults[i] = await resultTasks[i].ConfigureAwait(false);
                if (workerResults[i].Length != frames.Count)
                    throw new InvalidOperationException("Strategy worker returned an incomplete execution batch.");
            }
            catch (Exception exception)
            {
                // Worker already marked session failed; synthesise a barrier placeholder.
                StrategySimulationSession session = workers[i].Session;
                if (!session.IsFailed)
                    session.MarkFailed(frames[^1].Sequence, exception.ToString());
                workerResults[i] = frames.Select(frame => new StrategyFrameResult
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
                    })
                    .ToArray();
            }
        }

        var results = new StrategyFrameResult[frames.Count][];
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            results[frameIndex] = workerResults
                .Select(strategyResults => strategyResults[frameIndex])
                .ToArray();
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
