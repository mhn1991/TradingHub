using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using PortfolioManager.CrossMarket;
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
    private readonly IReadOnlyList<(string Id, ITradingAgent Agent, InstrumentKey Instrument)> _strategies;

    public StreamingComparativeEngine(
        IHistoricalCandleStream stream,
        IReadOnlyList<(string Id, ITradingAgent Agent, InstrumentKey Instrument)> strategies)
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
        bool isSharedPortfolio = options.Runtime.AccountMode == SimulationAccountMode.SharedPortfolioAccount;
        SimulationTimeframeOptions timeframes = options.Runtime.ToTimeframeOptions();
        IReadOnlyList<BarInterval> effectiveAnalysis = timeframes.EffectiveAnalysisIntervals(
            _strategies.SelectMany(s => s.Agent.RequiredIntervals)
                .Concat(_strategies.SelectMany(s => options.Runtime.ResolveManagementIntervals(s.Id)))
                .Concat(isSharedPortfolio ? [options.Runtime.CorrelationRisk.Interval] : []));

        var stopwatch = Stopwatch.StartNew();
        await RaiseStatusAsync(options, SimulationJobStatus.PreparingData).ConfigureAwait(false);

        // Per-instrument candle sourcing/aggregation/quality state is constructed further
        // down, once the traded-instrument set and the wrapped candle stream are known
        // (see InstrumentPipelineState). The annotator stays a single shared instance
        // across instruments - it is already partitioned internally by
        // ChartKey(instrument, interval).
        var sharedAnnotator = new ChartAnnotationEngine(options.AnnotationOptions);

        var sessions = new List<StrategySimulationSession>(_strategies.Count);
        SharedPortfolioRuntime? sharedPortfolio = isSharedPortfolio
            ? new SharedPortfolioRuntime(
                options.SimulationOptions,
                options.Runtime.PortfolioRisk,
                options.Runtime.PositionSizing,
                options.Runtime.AdaptiveRisk,
                options.Runtime.SafetyOptions,
                options.Runtime.CorrelationRisk)
            : null;
        CrossMarketAnalysisCoordinator? crossMarket = options.Runtime.CurrencyStrength.Enabled
            ? new CrossMarketAnalysisCoordinator(options.Runtime.CurrencyStrength)
            : null;
        foreach ((string id, ITradingAgent agent, InstrumentKey _) in _strategies)
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
                executionDecorator: sharedPortfolio is null ? null : sharedPortfolio.Decorate,
                crossMarket: crossMarket,
                detailedExcursionTracking: options.Runtime.DetailedExcursionTracking,
                featurePolicy: new TradingCore.Pipeline.RuntimeFeaturePolicy
                {
                    AnnotationOptions = options.AnnotationOptions ?? new(),
                    MarketRegimeRouting = options.Runtime.MarketRegimeRouting,
                    ValueLocationEvidence = options.Runtime.ValueLocationEvidence,
                    CurrencyStrengthEvidence = options.Runtime.CurrencyStrengthEvidence,
                    RsiBollingerSignals = options.Runtime.RsiBollingerSignals,
                    DmiConfirmationEnabled = options.Runtime.DmiConfirmationEnabled,
                    CurrencyStrength = options.Runtime.CurrencyStrength,
                    SetupCalibration = options.Runtime.SetupCalibration
                },
                strategyVersion: id);
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

        // sessions/workerHosts are index-correlated 1:1 with _strategies (built via the
        // same-order loops above), so this groups each by its assigned instrument once,
        // up front, for routing frames only to the strategies that trade that instrument
        // (see D3 in the §7 multi-instrument-clock plan) without re-deriving the mapping
        // on every batch.
        var sessionsByInstrument = new Dictionary<InstrumentKey, List<StrategySimulationSession>>();
        var hostsByInstrument = new Dictionary<InstrumentKey, List<StrategyWorkerHost>>();
        for (int strategyIndex = 0; strategyIndex < _strategies.Count; strategyIndex++)
        {
            InstrumentKey instrument = _strategies[strategyIndex].Instrument;
            if (!sessionsByInstrument.TryGetValue(instrument, out List<StrategySimulationSession>? sessionList))
                sessionsByInstrument[instrument] = sessionList = [];
            sessionList.Add(sessions[strategyIndex]);
            if (workerHosts.Count > strategyIndex)
            {
                if (!hostsByInstrument.TryGetValue(instrument, out List<StrategyWorkerHost>? hostList))
                    hostsByInstrument[instrument] = hostList = [];
                hostList.Add(workerHosts[strategyIndex]);
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
            Instruments = _strategies.Select(item => item.Instrument).Distinct()
                .Select(instrument => instrument.Value).ToArray(),
            StrategyInstruments = _strategies.ToDictionary(item => item.Id, item => item.Instrument.Value),
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

        // Side-channel basket fetch: cross-market currency-strength analysis needs
        // instruments beyond whatever this run is trading. Pre-fetched up front (not
        // incrementally streamed) since baskets are read-only analysis inputs, never
        // traded - this does not require the multi-instrument portfolio clock.
        var basketCandles = new Dictionary<InstrumentKey, Candle[]>();
        var basketCursors = new Dictionary<InstrumentKey, int>();
        if (crossMarket is not null)
        {
            foreach (InstrumentKey basketInstrument in options.Runtime.CurrencyStrength.AllBasketInstruments())
            {
                var candles = new List<Candle>();
                await foreach (MarketCandle basketCandle in _stream.StreamAsync(
                    new HistoricalCandleRequest(
                        basketInstrument,
                        options.Runtime.CurrencyStrength.Interval,
                        options.StreamFrom,
                        options.EvaluationTo),
                    cancellationToken).ConfigureAwait(false))
                {
                    if (basketCandle.Mid.IsComplete)
                        candles.Add(basketCandle.Mid);
                }
                basketCandles[basketInstrument] = candles.OrderBy(candle => candle.OpenTime).ToArray();
                basketCursors[basketInstrument] = 0;
            }
        }

        long sequence = 0;
        long processed = 0;
        DateTimeOffset? currentMarketTime = null;
        DateTimeOffset lastProgressPublish = DateTimeOffset.MinValue;
        var strategyResults = new List<StrategySimulationResult>();
        bool enteredEvaluationGlobally = false;
        IReadOnlySet<BarInterval> emptyClosed = new HashSet<BarInterval>();
        var pendingExecutionBatch = new List<MarketFrame>();
        InstrumentKey? pendingBatchInstrument = null;

        IReadOnlyList<InstrumentKey> tradedInstruments = _strategies
            .Select(item => item.Instrument)
            .Distinct()
            .ToArray();
        var pipelines = new Dictionary<InstrumentKey, InstrumentPipelineState>();

        try
        {
            foreach (InstrumentKey instrument in tradedInstruments)
            {
                pipelines[instrument] = new InstrumentPipelineState(
                    instrument,
                    stream,
                    candleRequest with { Instrument = instrument },
                    timeframes.ExecutionInterval,
                    timeframes.AnalysisBaseInterval,
                    effectiveAnalysis,
                    options.Runtime.BaseCandleGapPolicy,
                    options.Runtime.CandleCapacity,
                    cancellationToken);
            }

            // Chronological k-way merge across every traded instrument's own candle
            // stream/lookahead buffer (see InstrumentPipelineState). Ties break on
            // instrument value for determinism. For a single traded instrument this
            // degenerates to exactly the old single-stream lookahead pattern - there is
            // only ever one candidate to pick.
            async Task<(InstrumentPipelineState Pipeline, MarketCandle Candle, bool IsLast)?> TakeNextMergedAsync()
            {
                foreach (InstrumentPipelineState candidatePipeline in pipelines.Values)
                    await candidatePipeline.MoveNextBufferedAsync().ConfigureAwait(false);

                InstrumentPipelineState? winner = null;
                MarketCandle? winnerCandle = null;
                foreach (InstrumentPipelineState candidatePipeline in pipelines.Values)
                {
                    MarketCandle? candidate = candidatePipeline.PeekBuffered();
                    if (candidate is null)
                        continue;
                    if (winner is null ||
                        candidate.AvailableAt < winnerCandle!.AvailableAt ||
                        (candidate.AvailableAt == winnerCandle.AvailableAt &&
                         string.CompareOrdinal(candidatePipeline.Instrument.Value, winner.Instrument.Value) < 0))
                    {
                        winner = candidatePipeline;
                        winnerCandle = candidate;
                    }
                }

                if (winner is null)
                    return null;

                (MarketCandle candle, bool isLast) = await winner.TakeBufferedAsync().ConfigureAwait(false);
                return (winner, candle, isLast);
            }

            // One worker enqueue/barrier per pending batch, gated to the strategies that
            // trade pendingBatchInstrument only (D3). An execution batch never spans an
            // instrument switch (D4): SharedPortfolioRuntime must observe strictly
            // increasing (Sequence, AvailableAt) across the whole run, and flushing
            // whenever the merge hands us a different instrument's candle is what keeps
            // that true. For a single traded instrument this is called at exactly the
            // same points, in the same order, as the original single-stream code.
            async Task FlushPendingBatchAsync()
            {
                if (pendingExecutionBatch.Count == 0 || pendingBatchInstrument is not InstrumentKey batchInstrument)
                    return;

                MarketFrame[] executionBatch = pendingExecutionBatch.ToArray();
                pendingExecutionBatch.Clear();
                pendingBatchInstrument = null;

                // Only active (non-failed) strategies trading this batch's instrument
                // participate in the barrier.
                List<StrategyWorkerHost> activeHosts = hostsByInstrument
                    .GetValueOrDefault(batchInstrument, [])
                    .Where(host => !host.Session.IsFailed)
                    .ToList();
                List<StrategySimulationSession> activeSessions = sessionsByInstrument
                    .GetValueOrDefault(batchInstrument, [])
                    .Where(session => !session.IsFailed)
                    .ToList();

                if (activeSessions.Count == 0)
                {
                    if (sessions.All(s => s.IsFailed))
                    {
                        throw new InvalidOperationException(
                            "All strategies have failed; stopping comparison.");
                    }

                    // Every strategy trading THIS instrument has failed, but other
                    // instruments' strategies may still be healthy - skip this batch
                    // rather than aborting the whole run (D9 in the §7 plan). The global
                    // StopEntireComparison check below the main loop still catches a
                    // genuine cross-instrument failure once it actually happens.
                    return;
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
                    return;
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
            }

            await RaiseStatusAsync(options, SimulationJobStatus.WarmingUp).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Pause only between committed frames.
                if (options.PauseGate is not null)
                    await options.PauseGate.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

                (InstrumentPipelineState Pipeline, MarketCandle Candle, bool IsLast)? merged =
                    await TakeNextMergedAsync().ConfigureAwait(false);
                if (merged is null)
                    break;
                InstrumentPipelineState pipeline = merged.Value.Pipeline;
                MarketCandle marketCandle = merged.Value.Candle;
                bool isLast = merged.Value.IsLast;

                Candle baseCandle = marketCandle.Mid;
                pipeline.Quality.Observe(baseCandle);
                IReadOnlyList<string> dataQualityIssueCodes = pipeline.Quality.DrainPendingIssueCodes();
                var runtimeContext = new AnalysisRuntimeContext
                {
                    ExecutableSpread = baseCandle.Prices.Close *
                        options.SimulationOptions.SpreadBasisPoints / 10_000m,
                    DataQualityOk = dataQualityIssueCodes.Count == 0,
                    DataQualityIssueCodes = dataQualityIssueCodes
                };

                if (crossMarket is not null)
                {
                    var ready = new List<(InstrumentKey Instrument, DateTimeOffset CloseTime, decimal Close)>();
                    foreach (InstrumentKey basketInstrument in basketCandles.Keys)
                    {
                        Candle[] candles = basketCandles[basketInstrument];
                        int index = basketCursors[basketInstrument];
                        while (index < candles.Length)
                        {
                            Candle basketCandle = candles[index];
                            DateTimeOffset closeTime = basketCandle.CloseTime ?? basketCandle.OpenTime;
                            if (closeTime > marketCandle.AvailableAt) break;
                            ready.Add((basketInstrument, closeTime, basketCandle.Prices.Close));
                            index++;
                        }
                        basketCursors[basketInstrument] = index;
                    }
                    foreach (var group in ready.GroupBy(item => item.CloseTime).OrderBy(group => group.Key))
                        crossMarket.Update(group.Key, group.ToDictionary(item => item.Instrument, item => item.Close));
                }

                sequence++;
                processed++;
                currentMarketTime = marketCandle.AvailableAt;
                bool isWarmup = baseCandle.OpenTime < options.EvaluationFrom;

                if (!isWarmup && !pipeline.EnteredEvaluation)
                {
                    pipeline.EnteredEvaluation = true;
                    // Freezing is idempotent - PriceActionAnalyzer/MarketRegimeCalibration
                    // snapshot-and-lock current state, and an already-frozen state stops
                    // accumulating new calibration data, so re-freezing it just reproduces
                    // the same frozen snapshot. Triggering per-instrument-first-crossing
                    // (rather than once globally) is required for multi-instrument runs:
                    // ChartAnnotationEngine creates chart state lazily on first candle, so
                    // an instrument whose first candle arrives after another instrument's
                    // crossing would otherwise never get frozen at all.
                    if (sharedAnnotator is ICalibratableChartAnnotator sharedCalibration)
                        sharedCalibration.FreezeCalibration(options.EvaluationFrom);
                    foreach (StrategySimulationSession session in sessionsByInstrument.GetValueOrDefault(pipeline.Instrument, []))
                    {
                        if (session.IndependentAnnotator is ICalibratableChartAnnotator independentCalibration)
                            independentCalibration.FreezeCalibration(options.EvaluationFrom);
                    }
                    if (!enteredEvaluationGlobally)
                    {
                        enteredEvaluationGlobally = true;
                        await RaiseStatusAsync(options, SimulationJobStatus.Running).ConfigureAwait(false);
                    }
                }
                else if (isWarmup && processed == 1)
                {
                    await RaiseStatusAsync(options, SimulationJobStatus.WarmingUp).ConfigureAwait(false);
                }

                // Two-stage aggregation:
                //   execution candle → analysis-base (e.g. 1m)
                //   analysis-base → higher analysis intervals (3m/5m/15m/…)
                // ChartAnnotator only sees completed analysis candles, never raw 1s/5s.
                long incompleteBefore = pipeline.AnalysisBaseAggregator.IncompleteAggregateCount;
                IReadOnlyList<Candle> analysisBaseClosed =
                    pipeline.AnalysisBaseAggregator.ApplyExecutionCandle(baseCandle);
                pipeline.Quality.RecordIncompleteAggregate(
                    pipeline.AnalysisBaseAggregator.IncompleteAggregateCount - incompleteBefore);

                var closed = new HashSet<BarInterval>();
                foreach (Candle analysisBaseCandle in analysisBaseClosed)
                {
                    incompleteBefore = pipeline.MultiTimeframeAggregator.IncompleteAggregateCount;
                    IReadOnlyList<CandleClosedEvent> closedEvents = pipeline.MultiTimeframeAggregator.Apply(analysisBaseCandle);
                    pipeline.Quality.RecordIncompleteAggregate(
                        pipeline.MultiTimeframeAggregator.IncompleteAggregateCount - incompleteBefore);
                    foreach (CandleClosedEvent closedEvent in closedEvents)
                    {
                        closed.Add(closedEvent.Interval);
                        AnalysisSnapshot snapshot = await sharedAnnotator
                            .ProcessAsync(closedEvent, runtimeContext, cancellationToken)
                            .ConfigureAwait(false);
                        if (options.Runtime.AnalysisSharingMode == AnalysisSharingMode.IndependentPerStrategy)
                        {
                            foreach (StrategySimulationSession session in sessionsByInstrument.GetValueOrDefault(pipeline.Instrument, []))
                            {
                                if (session.IndependentAnnotator is not null)
                                {
                                    await session.IndependentAnnotator
                                        .ProcessAsync(closedEvent, runtimeContext, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }

                        pipeline.LatestSnapshots[closedEvent.Interval] = snapshot;

                        if (sharedPortfolio is not null && closedEvent.Interval == options.Runtime.CorrelationRisk.Interval)
                        {
                            decimal close = closedEvent.Candle.Prices.Close;
                            if (pipeline.LastCorrelationClose is decimal previousClose && previousClose > 0m && close > 0m)
                            {
                                decimal logReturn = (decimal)Math.Log((double)(close / previousClose));
                                sharedPortfolio.ObserveCompletedReturns(
                                    closedEvent.Candle.CloseTime ?? marketCandle.AvailableAt,
                                    new Dictionary<InstrumentKey, decimal> { [closedEvent.Instrument] = logReturn });
                            }
                            pipeline.LastCorrelationClose = close;
                        }
                    }
                }

                IReadOnlySet<BarInterval> closedIntervals = closed.Count == 0
                    ? emptyClosed
                    : closed;
                if (closed.Count > 0)
                {
                    pipeline.SnapshotSet = new AnalysisSnapshotSet
                    {
                        Version = pipeline.SnapshotSet.Version + 1,
                        Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>(pipeline.LatestSnapshots)
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
                    Snapshots = pipeline.SnapshotSet.Snapshots,
                    InputStreamId = options.InputStreamId,
                    IsWarmup = isWarmup,
                    IsLastCandle = isLast
                };

                if (pendingExecutionBatch.Count > 0 && pendingBatchInstrument != pipeline.Instrument)
                    await FlushPendingBatchAsync().ConfigureAwait(false);

                pendingBatchInstrument = pipeline.Instrument;
                pendingExecutionBatch.Add(frame);
                if (frame.AnalysisBaseCandle is null && !frame.IsLastCandle)
                    continue;

                // One worker enqueue/barrier per analysis-base bucket. Each isolated
                // strategy still processes every execution frame in strict order.
                await FlushPendingBatchAsync().ConfigureAwait(false);

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
                    FailureMessage = session.FailureMessage,
                    FeaturePolicyHash = session.FeaturePolicyHash
                });
            }

            MarketDataQualityReport qualityReport = CombineQualityReports(
                pipelines.Values.Select(pipeline => pipeline.Quality.BuildReport()).ToArray());
            await replayWriter.CompleteAsync(strategyResults, qualityReport, cancellationToken)
                .ConfigureAwait(false);

            await replayWriter.WriteManifestAsync(new SimulationManifest
            {
                SimulationId = options.SimulationId,
                SchemaVersion = 2,
                Instrument = options.Instrument.Value,
            Instruments = _strategies.Select(item => item.Instrument).Distinct()
                .Select(instrument => instrument.Value).ToArray(),
            StrategyInstruments = _strategies.ToDictionary(item => item.Id, item => item.Instrument.Value),
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
            foreach (InstrumentPipelineState pipeline in pipelines.Values)
                await pipeline.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Combines per-instrument quality reports into one aggregate for back-compat
    /// consumers of the single top-level DataQuality/InputHash fields. For a single
    /// traded instrument this returns the one report unchanged, byte-for-byte.
    /// </summary>
    private static MarketDataQualityReport CombineQualityReports(IReadOnlyList<MarketDataQualityReport> reports)
    {
        if (reports.Count == 1)
            return reports[0];

        string combinedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join('|', reports.Select(report => report.InputHash).OrderBy(hash => hash, StringComparer.Ordinal)))))
            .ToLowerInvariant();
        return new MarketDataQualityReport
        {
            CandleCount = reports.Sum(report => report.CandleCount),
            DuplicateCount = reports.Sum(report => report.DuplicateCount),
            OutOfOrderCount = reports.Sum(report => report.OutOfOrderCount),
            MissingIntervalCount = reports.Sum(report => report.MissingIntervalCount),
            WeekendGapCount = reports.Sum(report => report.WeekendGapCount),
            SessionGapCount = reports.Sum(report => report.SessionGapCount),
            IncompleteAggregateCount = reports.Sum(report => report.IncompleteAggregateCount),
            FirstCandle = reports.Min(report => report.FirstCandle),
            LastCandle = reports.Max(report => report.LastCandle),
            InputHash = combinedHash
        };
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
