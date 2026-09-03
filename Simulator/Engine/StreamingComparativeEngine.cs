using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
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
using RiskManager;
using RiskManager.Calibration;
using TradingCore.Pipeline;

namespace Simulator.Engine;

/// <summary>
/// One resolved strategy/instrument pairing <see cref="StreamingComparativeEngine"/> consumes. A
/// named record (Phase 5 of the multi-agent architecture), not the original bare 3-tuple, so
/// <see cref="AnalysisOptionsOverride"/>/<see cref="Mode"/> have a home without every call site
/// re-deriving a wider tuple type. <see cref="AnalysisOptionsOverride"/> null (the default) means
/// "use the run's shared <c>StreamingComparativeEngineOptions.AnnotationOptions</c>" - only
/// assignments that explicitly diverge resolve to their own <c>AnalysisProfileKey</c>.
/// </summary>
public sealed record StrategyFactoryEntry(
    string Id,
    ITradingAgent Agent,
    InstrumentKey Instrument,
    ChartAnnotationOptions? AnalysisOptionsOverride = null,
    AgentExecutionMode Mode = AgentExecutionMode.Shadow,
    Guid? PolicyBundleId = null,
    int? PolicyRevision = null);

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
    public decimal MinimumRewardRiskRatio { get; init; } =
        PreTradeRiskOptions.PhaseOneSafeDefaults.MinimumRewardRiskRatio ?? 1.5m;
    public required string OutputDirectory { get; init; }
    public required string InputStreamId { get; init; }
    public string? SimulationConfigurationId { get; init; }
    public ChartAnnotationOptions? AnnotationOptions { get; init; }
    public IProgress<BacktestProgress>? Progress { get; init; }
    public Func<SimulationJobStatus, Task>? StatusChanged { get; init; }
    public Func<string, SimulatedTradeRecord, Task>? TradeCompleted { get; init; }
    public ISetupMetaModel? MetaLabelModel { get; init; }
    public Simulator.Jobs.IAsyncPauseGate? PauseGate { get; init; }
    public bool CaptureMarketReplay { get; init; } = true;
}

/// <summary>
/// One-minute canonical clock with shared analysis, isolated strategy sessions,
/// sequential or parallel workers, and a per-frame barrier.
/// </summary>
public sealed class StreamingComparativeEngine
{
    private readonly IHistoricalCandleStream _stream;
    private readonly IReadOnlyList<StrategyFactoryEntry> _strategies;
    private readonly IReadOnlyDictionary<string, string> _instrumentByStrategyId;

    public StreamingComparativeEngine(
        IHistoricalCandleStream stream,
        IReadOnlyList<StrategyFactoryEntry> strategies)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.", nameof(strategies));
        _instrumentByStrategyId = _strategies.ToDictionary(entry => entry.Id, entry => entry.Instrument.Value);
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
        // (see InstrumentPipelineState). Analysis engines themselves are resolved through
        // the profile registry, de-duplicated by AnalysisProfileKey - today every session
        // resolves to the same options.AnnotationOptions and therefore shares exactly one
        // engine, matching the previous single-sharedAnnotator behaviour byte-for-byte, but
        // a future per-assignment AnalysisOptionsOverride (Phase 5) will transparently split
        // into multiple engines without further changes here.
        var profileRegistry = new AnalysisProfileRegistry(MetaLabelFeatureFactory.SchemaVersion);
        IReadOnlySet<BarInterval> requiredIntervalsSet = new HashSet<BarInterval>(effectiveAnalysis);

        var sessions = new List<StrategySimulationSession>(_strategies.Count);
        var sessionProfiles = new List<AnalysisProfileKey>(_strategies.Count);
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
        foreach (StrategyFactoryEntry entry in _strategies)
        {
            string id = entry.Id;
            ITradingAgent agent = entry.Agent;
            // Per-assignment AnalysisOptionsOverride (Phase 5) falls back to the run's shared
            // options exactly as every session resolved before this phase - only assignments
            // that explicitly diverge resolve to their own AnalysisProfileKey/engine.
            ChartAnnotationOptions? resolvedAnnotationOptions = entry.AnalysisOptionsOverride ?? options.AnnotationOptions;
            AnalysisProfileKey sessionProfile = profileRegistry.GetOrCreateProfile(
                resolvedAnnotationOptions, requiredIntervalsSet);
            sessionProfiles.Add(sessionProfile);
            StrategySimulationSession session = StrategySimulationSession.Create(
                id,
                agent,
                options.SimulationOptions,
                safetyOptions: options.Runtime.SafetyOptions,
                analysisSharing: options.Runtime.AnalysisSharingMode,
                annotationOptions: resolvedAnnotationOptions,
                analysisProfile: sessionProfile,
                positionManagementOptions: options.Runtime.GetPositionManagement(id),
                playbookManagementOverrides: options.Runtime.GetPlaybookManagementOverrides(id),
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
                    AnnotationOptions = resolvedAnnotationOptions ?? new(),
                    MarketRegimeRouting = options.Runtime.MarketRegimeRouting,
                    ValueLocationEvidence = options.Runtime.ValueLocationEvidence,
                    CurrencyStrengthEvidence = options.Runtime.CurrencyStrengthEvidence,
                    RsiBollingerSignals = options.Runtime.RsiBollingerSignals,
                    DmiConfirmationEnabled = options.Runtime.DmiConfirmationEnabled,
                    CurrencyStrength = options.Runtime.CurrencyStrength,
                    SetupCalibration = options.Runtime.SetupCalibration
                },
                strategyVersion: id,
                minimumRewardRiskRatio: options.MinimumRewardRiskRatio);
            sessions.Add(session);
            sharedPortfolio?.Register(session);
        }

        // Persistent workers: one task/thread per strategy for the whole simulation.
        var workerHosts = new List<StrategyWorkerHost>();
        if (options.Runtime.StrategyExecutionMode == StrategyExecutionMode.ParallelWorkers)
        {
            for (int strategyIndex = 0; strategyIndex < sessions.Count; strategyIndex++)
            {
                // PolicyBundleId/Revision default to Guid.Empty/0 placeholders when an assignment
                // doesn't declare real policy-bundle identity (StrategyInstrumentAssignment's
                // PolicyBundleId/PolicyRevision, Phase 5) - not a claim of genuine identity in
                // that case.
                var key = new AgentInstanceKey(
                    AgentInstanceKey.SimulatorDeploymentId,
                    _strategies[strategyIndex].Instrument,
                    sessions[strategyIndex].StrategyId,
                    _strategies[strategyIndex].PolicyBundleId ?? Guid.Empty,
                    _strategies[strategyIndex].PolicyRevision ?? 0);
                workerHosts.Add(new StrategyWorkerHost(
                    sessions[strategyIndex],
                    options.Runtime.StrategyChannelCapacity,
                    key));
            }
        }

        // sessions/workerHosts are index-correlated 1:1 with _strategies (built via the
        // same-order loops above), so this groups each by its assigned instrument once,
        // up front, for routing frames only to the strategies that trade that instrument
        // (see D3 in the §7 multi-instrument-clock plan) without re-deriving the mapping
        // on every batch.
        var sessionsByInstrument = new Dictionary<InstrumentKey, List<StrategySimulationSession>>();
        var hostsByInstrument = new Dictionary<InstrumentKey, List<StrategyWorkerHost>>();
        var profilesByInstrument = new Dictionary<InstrumentKey, List<AnalysisProfileKey>>();
        var profileBySession = new Dictionary<StrategySimulationSession, AnalysisProfileKey>();
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

            profileBySession[sessions[strategyIndex]] = sessionProfiles[strategyIndex];
            if (!profilesByInstrument.TryGetValue(instrument, out List<AnalysisProfileKey>? profileList))
                profilesByInstrument[instrument] = profileList = [];
            if (!profileList.Contains(sessionProfiles[strategyIndex]))
                profileList.Add(sessionProfiles[strategyIndex]);
        }

        await using var replayWriter = new ChunkedReplayWriter(
            options.OutputDirectory,
            options.Runtime.ReplayChunkSize,
            options.Runtime.ExecutionDetailPreEntryFrames,
            options.Runtime.ExecutionDetailPostExitFrames,
            options.CaptureMarketReplay);

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
            BaseCurrency = options.SimulationOptions.BaseCurrency,
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

        // SharedPortfolioRuntime.ObserveCompletedReturns must see every instrument's completed
        // correlation-interval return for a given timestamp in one call (it enforces strictly
        // increasing timestamps - see its own doc comment about "once the multi-instrument
        // portfolio clock lands"). The merged stream below hands us one instrument's candle per
        // iteration, tie-broken by instrument ordinal when timestamps match, so several
        // instruments closing at the same correlation-interval timestamp arrive as consecutive
        // (never interleaved with a different timestamp) iterations - buffer by timestamp and
        // flush on change instead of calling per-instrument, which used to throw
        // "Correlation observations must be chronological" the moment a second instrument's
        // candle closed at a timestamp already observed.
        DateTimeOffset? pendingCorrelationAt = null;
        var pendingCorrelationReturns = new Dictionary<InstrumentKey, decimal>();
        void FlushPendingCorrelation()
        {
            if (pendingCorrelationAt is not DateTimeOffset at || pendingCorrelationReturns.Count == 0)
                return;
            sharedPortfolio!.ObserveCompletedReturns(at, new Dictionary<InstrumentKey, decimal>(pendingCorrelationReturns));
            pendingCorrelationReturns.Clear();
            pendingCorrelationAt = null;
        }

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
                    options.Runtime.AggregationGapToleranceFraction,
                    options.Runtime.CandleCapacity,
                    cancellationToken);
                foreach (AnalysisProfileKey profile in profilesByInstrument.GetValueOrDefault(instrument, []))
                    pipelines[instrument].LatestSnapshotsFor(profile);
            }

            // True cross-instrument concurrency: each traded instrument gets its own
            // dedicated task instead of being funneled one-candle-at-a-time through the
            // single chronological merge below. Only safe when nothing needs one advancing
            // global clock while candles are still being produced: SharedPortfolioRuntime
            // requires strictly increasing (Sequence, AvailableAt) across the whole run
            // (see FlushPendingCorrelation), and cross-market/currency-strength analysis
            // assumes the same. Both are null exactly when accounts are independent and
            // currency-strength is disabled - the common multi-instrument case, and the
            // one this exists for.
            bool runInstrumentsConcurrently =
                pipelines.Count > 1 && sharedPortfolio is null && crossMarket is null;

            // Priming buffer reused every merge step so a multi-instrument run doesn't
            // allocate a new array per candle (this loop runs once per merged candle -
            // potentially millions of times for a multi-month, multi-instrument backtest).
            // Only used by the sequential fallback path below.
            InstrumentPipelineState[] pipelineArray = [.. pipelines.Values];
            Task<bool>[] primeTasks = new Task<bool>[pipelineArray.Length];

            // Chronological k-way merge across every traded instrument's own candle
            // stream/lookahead buffer (see InstrumentPipelineState). Ties break on
            // instrument value for determinism. For a single traded instrument this
            // degenerates to exactly the old single-stream lookahead pattern - there is
            // only ever one candidate to pick.
            //
            // Priming every pipeline concurrently (rather than one at a time) matters
            // because each pipeline's first MoveNextBufferedAsync call starts that
            // instrument's own background download (see PrefetchingCandleStream); a
            // sequential await here would stagger every instrument's download start
            // behind the previous instrument's first page, and would serialize any
            // later moment where more than one instrument simultaneously needs a fresh
            // page (e.g. every instrument starting from the same warm-up date tends to
            // exhaust its prefetch buffer around the same simulated time).
            async Task<(InstrumentPipelineState Pipeline, MarketCandle Candle, bool IsLast)?> TakeNextMergedAsync()
            {
                if (pipelineArray.Length == 1)
                {
                    await pipelineArray[0].MoveNextBufferedAsync().ConfigureAwait(false);
                }
                else
                {
                    for (int i = 0; i < pipelineArray.Length; i++)
                        primeTasks[i] = pipelineArray[i].MoveNextBufferedAsync();
                    await Task.WhenAll(primeTasks).ConfigureAwait(false);
                }

                InstrumentPipelineState? winner = null;
                MarketCandle? winnerCandle = null;
                foreach (InstrumentPipelineState candidatePipeline in pipelineArray)
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
                catch (Exception) when (
                    options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopFailedStrategyOnly)
                {
                    // A per-session catch inside ProcessSequentialBatchAsync/ProcessParallelWorkersBatchAsync
                    // already calls session.MarkFailed before rethrowing, so the genuine culprit is
                    // identifiable here by IsFailed. If nothing is marked failed, this exception did not
                    // originate from a single strategy's evaluation (e.g. sharedPortfolio.FlushAsync itself
                    // threw) - blaming an arbitrary "first active session" would misattribute the failure
                    // and let every other strategy keep trading against a batch/portfolio state that might
                    // itself be corrupted. Rethrow instead of guessing; StopFailedStrategyOnly only isolates
                    // failures it can actually attribute to one strategy.
                    StrategySimulationSession? culprit = activeSessions.FirstOrDefault(s => s.IsFailed);
                    if (culprit is null)
                        throw;
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

            // Runs every traded instrument's fetch/aggregate/annotate/evaluate pipeline on
            // its own concurrent task instead of funneling everything through one
            // chronological merge (see runInstrumentsConcurrently above for why this is
            // only safe without a shared portfolio or cross-market coordinator). Only
            // ChartAnnotationEngine's own state is genuinely per-ChartKey (instrument,
            // interval) and documented as safe for concurrent use across different keys
            // (ChartAnnotator/Engine/ChartAnnotationEngine.cs) - everything else touched
            // per candle (aggregators, snapshot sets, worker hosts/sessions) already lives
            // on InstrumentPipelineState or is 1:1 with one instrument's strategies, so
            // nothing needs new locking there.
            //
            // The one thing that does need a single chronological view is the shared
            // replay writer: ChunkedReplayWriter derives each market chunk's declared
            // FromTime/ToTime (and FromSequence/ToSequence) from the first/last row
            // physically written to it, so committing out of time order would produce
            // chunks with wrong bounds. So: producers evaluate strategies fully
            // concurrently using their own per-instrument-local frame sequence (workers/
            // sessions only require strictly-increasing sequence within their own single
            // instrument's stream - see StrategyWorkerHost's out-of-order check), then
            // hand finished (frame, results) pairs to one single-threaded committer that
            // merges them back into global chronological order - the same k-way merge
            // TakeNextMergedAsync does above for raw candles, just merging
            // already-evaluated results instead, and assigning the real global Sequence
            // at that point.
            async Task RunInstrumentsConcurrentlyAsync()
            {
                using CancellationTokenSource runCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken runToken = runCts.Token;
                Exception? fatal = null;
                var fatalLock = new object();
                int enteredEvaluationFlag = 0;

                Dictionary<InstrumentKey, Channel<(MarketFrame Frame, StrategyFrameResult[] Results)>> channels =
                    tradedInstruments.ToDictionary(
                        instrument => instrument,
                        _ => Channel.CreateBounded<(MarketFrame Frame, StrategyFrameResult[] Results)>(
                            new BoundedChannelOptions(64)
                            {
                                SingleReader = true,
                                SingleWriter = true,
                                FullMode = BoundedChannelFullMode.Wait
                            }));

                async Task ProduceAsync(InstrumentKey instrument)
                {
                    InstrumentPipelineState pipeline = pipelines[instrument];
                    ChannelWriter<(MarketFrame, StrategyFrameResult[])> writer = channels[instrument].Writer;
                    List<StrategyWorkerHost> instrumentHosts = hostsByInstrument.GetValueOrDefault(instrument, []);
                    List<StrategySimulationSession> instrumentSessions = sessionsByInstrument.GetValueOrDefault(instrument, []);
                    IReadOnlyList<AnalysisProfileKey> instrumentProfiles = profilesByInstrument.GetValueOrDefault(instrument, []);
                    long localSequence = 0;
                    var localBatch = new List<MarketFrame>();
                    try
                    {
                        while (true)
                        {
                            runToken.ThrowIfCancellationRequested();
                            if (options.PauseGate is not null)
                                await options.PauseGate.WaitIfPausedAsync(runToken).ConfigureAwait(false);

                            if (!await pipeline.MoveNextBufferedAsync().ConfigureAwait(false))
                                break;
                            (MarketCandle marketCandle, bool isLast) =
                                await pipeline.TakeBufferedAsync().ConfigureAwait(false);

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

                            localSequence++;
                            bool isWarmup = baseCandle.OpenTime < options.EvaluationFrom;

                            if (!isWarmup && !pipeline.EnteredEvaluation)
                            {
                                pipeline.EnteredEvaluation = true;
                                foreach (AnalysisProfileKey profile in instrumentProfiles)
                                {
                                    if (profileRegistry.EngineFor(profile) is ICalibratableChartAnnotator sharedCalibration)
                                        sharedCalibration.FreezeCalibration(options.EvaluationFrom);
                                }
                                foreach (StrategySimulationSession session in instrumentSessions)
                                {
                                    if (session.IndependentAnnotator is ICalibratableChartAnnotator independentCalibration)
                                        independentCalibration.FreezeCalibration(options.EvaluationFrom);
                                }
                                if (Interlocked.CompareExchange(ref enteredEvaluationFlag, 1, 0) == 0)
                                    await RaiseStatusAsync(options, SimulationJobStatus.Running).ConfigureAwait(false);
                            }

                            long incompleteBefore = pipeline.AnalysisBaseAggregator.IncompleteAggregateCount;
                            IReadOnlyList<Candle> analysisBaseClosed =
                                pipeline.AnalysisBaseAggregator.ApplyExecutionCandle(baseCandle);
                            pipeline.Quality.RecordIncompleteAggregate(
                                pipeline.AnalysisBaseAggregator.IncompleteAggregateCount - incompleteBefore);

                            var closed = new HashSet<BarInterval>();
                            foreach (Candle analysisBaseCandle in analysisBaseClosed)
                            {
                                incompleteBefore = pipeline.MultiTimeframeAggregator.IncompleteAggregateCount;
                                IReadOnlyList<CandleClosedEvent> closedEvents =
                                    pipeline.MultiTimeframeAggregator.Apply(analysisBaseCandle);
                                pipeline.Quality.RecordIncompleteAggregate(
                                    pipeline.MultiTimeframeAggregator.IncompleteAggregateCount - incompleteBefore);
                                foreach (CandleClosedEvent closedEvent in closedEvents)
                                {
                                    closed.Add(closedEvent.Interval);
                                    foreach (AnalysisProfileKey profile in instrumentProfiles)
                                    {
                                        AnalysisSnapshot snapshot = await profileRegistry.EngineFor(profile)
                                            .ProcessAsync(closedEvent, runtimeContext, runToken)
                                            .ConfigureAwait(false);
                                        pipeline.LatestSnapshotsFor(profile)[closedEvent.Interval] = snapshot;
                                    }
                                    if (options.Runtime.AnalysisSharingMode == AnalysisSharingMode.IndependentPerStrategy)
                                    {
                                        foreach (StrategySimulationSession session in instrumentSessions)
                                        {
                                            if (session.IndependentAnnotator is not null)
                                            {
                                                await session.IndependentAnnotator
                                                    .ProcessAsync(closedEvent, runtimeContext, runToken)
                                                    .ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    // sharedPortfolio is always null on this path (see
                                    // runInstrumentsConcurrently above), so there is no
                                    // cross-instrument correlation-return bookkeeping to do.
                                }
                            }

                            IReadOnlySet<BarInterval> closedIntervals = closed.Count == 0 ? emptyClosed : closed;
                            if (closed.Count > 0)
                            {
                                foreach (AnalysisProfileKey profile in instrumentProfiles)
                                {
                                    AnalysisSnapshotSet previous = pipeline.SnapshotSetsByProfile[profile];
                                    pipeline.SnapshotSetsByProfile[profile] = new AnalysisSnapshotSet
                                    {
                                        Version = previous.Version + 1,
                                        Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>(pipeline.LatestSnapshotsFor(profile))
                                    };
                                }
                            }

                            var snapshotsByProfile = new Dictionary<AnalysisProfileKey, IReadOnlyDictionary<BarInterval, AnalysisSnapshot>>();
                            foreach (AnalysisProfileKey profile in instrumentProfiles)
                                snapshotsByProfile[profile] = pipeline.SnapshotSetsByProfile[profile].Snapshots;
                            IReadOnlyDictionary<BarInterval, AnalysisSnapshot> primarySnapshots = instrumentProfiles.Count > 0
                                ? snapshotsByProfile[instrumentProfiles[0]]
                                : new Dictionary<BarInterval, AnalysisSnapshot>();

                            var frame = new MarketFrame
                            {
                                Sequence = localSequence,
                                AvailableAt = marketCandle.AvailableAt,
                                ExecutionCandle = marketCandle,
                                AnalysisBaseCandle = analysisBaseClosed.LastOrDefault(),
                                ClosedIntervals = closedIntervals,
                                Snapshots = primarySnapshots,
                                SnapshotsByProfile = snapshotsByProfile,
                                InputStreamId = options.InputStreamId,
                                IsWarmup = isWarmup,
                                IsLastCandle = isLast
                            };

                            localBatch.Add(frame);
                            if (frame.AnalysisBaseCandle is null && !frame.IsLastCandle)
                                continue;

                            MarketFrame[] executionBatch = [.. localBatch];
                            localBatch.Clear();

                            List<StrategyWorkerHost> activeHosts =
                                instrumentHosts.Where(host => !host.Session.IsFailed).ToList();
                            List<StrategySimulationSession> activeSessions =
                                instrumentSessions.Where(session => !session.IsFailed).ToList();

                            if (activeSessions.Count == 0)
                            {
                                if (sessions.All(s => s.IsFailed))
                                    throw new InvalidOperationException("All strategies have failed; stopping comparison.");
                                // Every strategy trading this instrument has failed, but other
                                // instruments' strategies may still be healthy (D9) - keep
                                // draining this instrument's own stream (matches the
                                // sequential path's behaviour) without dispatching anything.
                                continue;
                            }

                            StrategyFrameResult[][] batchResults;
                            try
                            {
                                batchResults = options.Runtime.StrategyExecutionMode switch
                                {
                                    StrategyExecutionMode.ParallelWorkers =>
                                        await ProcessParallelWorkersBatchAsync(activeHosts, executionBatch, runToken)
                                            .ConfigureAwait(false),
                                    _ => await ProcessSequentialBatchAsync(activeSessions, executionBatch, runToken)
                                        .ConfigureAwait(false)
                                };
                            }
                            catch (Exception) when (
                                options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopFailedStrategyOnly)
                            {
                                StrategySimulationSession? culprit = activeSessions.FirstOrDefault(s => s.IsFailed);
                                if (culprit is null)
                                    throw;
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
                                await writer.WriteAsync((committedFrame, frameResults), runToken).ConfigureAwait(false);
                            }

                            if (options.Runtime.StrategyFailurePolicy == StrategyFailurePolicy.StopEntireComparison &&
                                sessions.FirstOrDefault(s => s.IsFailed) is StrategySimulationSession failedSession)
                            {
                                throw new InvalidOperationException(
                                    $"Strategy '{failedSession.StrategyName}' failed at sequence " +
                                    $"{failedSession.FailedSequence}: {failedSession.FailureMessage}");
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        lock (fatalLock) { fatal ??= exception; }
                        runCts.Cancel();
                    }
                    finally
                    {
                        writer.Complete();
                    }
                }

                async Task CommitAsync()
                {
                    Dictionary<InstrumentKey, ChannelReader<(MarketFrame, StrategyFrameResult[])>> readers =
                        channels.ToDictionary(kv => kv.Key, kv => kv.Value.Reader);
                    var buffered = new Dictionary<InstrumentKey, (MarketFrame Frame, StrategyFrameResult[] Results)?>();
                    foreach (InstrumentKey instrument in tradedInstruments)
                        buffered[instrument] = null;

                    async Task<bool> EnsureBufferedAsync(InstrumentKey instrument)
                    {
                        if (buffered[instrument] is not null)
                            return true;
                        ChannelReader<(MarketFrame, StrategyFrameResult[])> reader = readers[instrument];
                        if (await reader.WaitToReadAsync(runToken).ConfigureAwait(false) &&
                            reader.TryRead(out (MarketFrame, StrategyFrameResult[]) item))
                        {
                            buffered[instrument] = item;
                            return true;
                        }
                        return false;
                    }

                    while (true)
                    {
                        if (options.PauseGate is not null)
                            await options.PauseGate.WaitIfPausedAsync(runToken).ConfigureAwait(false);

                        await Task.WhenAll(tradedInstruments.Select(EnsureBufferedAsync)).ConfigureAwait(false);

                        InstrumentKey? winner = null;
                        DateTimeOffset winnerTime = default;
                        foreach (InstrumentKey instrument in tradedInstruments)
                        {
                            if (buffered[instrument] is not (MarketFrame candidateFrame, _))
                                continue;
                            if (winner is null ||
                                candidateFrame.AvailableAt < winnerTime ||
                                (candidateFrame.AvailableAt == winnerTime &&
                                 string.CompareOrdinal(instrument.Value, winner.Value.Value) < 0))
                            {
                                winner = instrument;
                                winnerTime = candidateFrame.AvailableAt;
                            }
                        }

                        if (winner is null)
                            break;

                        (MarketFrame localFrame, StrategyFrameResult[] localResults) = buffered[winner.Value]!.Value;
                        buffered[winner.Value] = null;

                        sequence++;
                        processed++;
                        currentMarketTime = localFrame.AvailableAt;
                        MarketFrame committedFrame = localFrame with { Sequence = sequence };
                        StrategyFrameResult[] committedResults =
                            [.. localResults.Select(result => result with { Sequence = sequence })];

                        await replayWriter.CommitFrameAsync(committedFrame, committedResults, runToken)
                            .ConfigureAwait(false);
                        if (options.TradeCompleted is not null)
                        {
                            foreach (StrategyFrameResult result in committedResults)
                            {
                                if (result.NewlyCompletedTrade is SimulatedTradeRecord trade)
                                    await options.TradeCompleted(result.StrategyId, trade).ConfigureAwait(false);
                            }
                        }

                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        if ((now - lastProgressPublish).TotalMilliseconds >=
                            options.Runtime.ProgressPublishIntervalMilliseconds)
                        {
                            lastProgressPublish = now;
                            PublishProgress(options, processed, currentMarketTime.Value, sessions, committedFrame.IsWarmup);
                        }
                    }
                }

                async Task RunCommitterAsync()
                {
                    try
                    {
                        await CommitAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // A committer-side failure (e.g. a replay-writer I/O error) must also
                        // stop every producer promptly - otherwise they keep fetching/annotating/
                        // evaluating with nothing left to drain their output into.
                        lock (fatalLock) { fatal ??= exception; }
                        runCts.Cancel();
                        throw;
                    }
                }

                Task[] producers = [.. tradedInstruments.Select(ProduceAsync)];
                Task committerTask = RunCommitterAsync();
                try
                {
                    await Task.WhenAll([.. producers, committerTask]).ConfigureAwait(false);
                }
                catch when (fatal is not null)
                {
                    throw fatal;
                }
            }

            await RaiseStatusAsync(options, SimulationJobStatus.WarmingUp).ConfigureAwait(false);

            if (runInstrumentsConcurrently)
            {
                await RunInstrumentsConcurrentlyAsync().ConfigureAwait(false);
            }
            else
            {
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
                    foreach (AnalysisProfileKey profile in profilesByInstrument.GetValueOrDefault(pipeline.Instrument, []))
                    {
                        if (profileRegistry.EngineFor(profile) is ICalibratableChartAnnotator sharedCalibration)
                            sharedCalibration.FreezeCalibration(options.EvaluationFrom);
                    }
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
                        foreach (AnalysisProfileKey profile in profilesByInstrument.GetValueOrDefault(pipeline.Instrument, []))
                        {
                            AnalysisSnapshot snapshot = await profileRegistry.EngineFor(profile)
                                .ProcessAsync(closedEvent, runtimeContext, cancellationToken)
                                .ConfigureAwait(false);
                            pipeline.LatestSnapshotsFor(profile)[closedEvent.Interval] = snapshot;
                        }
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

                        if (sharedPortfolio is not null && closedEvent.Interval == options.Runtime.CorrelationRisk.Interval)
                        {
                            decimal close = closedEvent.Candle.Prices.Close;
                            if (pipeline.LastCorrelationClose is decimal previousClose && previousClose > 0m && close > 0m)
                            {
                                decimal logReturn = (decimal)Math.Log((double)(close / previousClose));
                                DateTimeOffset closeAt = closedEvent.Candle.CloseTime ?? marketCandle.AvailableAt;
                                if (pendingCorrelationAt is DateTimeOffset currentBatch && currentBatch != closeAt)
                                    FlushPendingCorrelation();
                                pendingCorrelationAt = closeAt;
                                pendingCorrelationReturns[closedEvent.Instrument] = logReturn;
                            }
                            pipeline.LastCorrelationClose = close;
                        }
                    }
                }

                IReadOnlySet<BarInterval> closedIntervals = closed.Count == 0
                    ? emptyClosed
                    : closed;
                IReadOnlyList<AnalysisProfileKey> instrumentProfiles =
                    profilesByInstrument.GetValueOrDefault(pipeline.Instrument, []);
                if (closed.Count > 0)
                {
                    foreach (AnalysisProfileKey profile in instrumentProfiles)
                    {
                        AnalysisSnapshotSet previous = pipeline.SnapshotSetsByProfile[profile];
                        pipeline.SnapshotSetsByProfile[profile] = new AnalysisSnapshotSet
                        {
                            Version = previous.Version + 1,
                            Snapshots = new Dictionary<BarInterval, AnalysisSnapshot>(pipeline.LatestSnapshotsFor(profile))
                        };
                    }
                }

                // Every profile trading this instrument gets its own resolved snapshot view;
                // Snapshots (singular) stays populated with the first/primary profile's view
                // for replay/execution-detail consumers that are not yet profile-aware
                // (ChunkedReplayWriter) - today there is exactly one profile per instrument in
                // practice, so this is identical to the previous single-annotator behaviour.
                var snapshotsByProfile = new Dictionary<AnalysisProfileKey, IReadOnlyDictionary<BarInterval, AnalysisSnapshot>>();
                foreach (AnalysisProfileKey profile in instrumentProfiles)
                    snapshotsByProfile[profile] = pipeline.SnapshotSetsByProfile[profile].Snapshots;
                IReadOnlyDictionary<BarInterval, AnalysisSnapshot> primarySnapshots = instrumentProfiles.Count > 0
                    ? snapshotsByProfile[instrumentProfiles[0]]
                    : new Dictionary<BarInterval, AnalysisSnapshot>();

                // Frame is always on the execution clock for fills; strategy evaluates only
                // when its trigger interval is in ClosedIntervals (after analysis-base close).
                var frame = new MarketFrame
                {
                    Sequence = sequence,
                    AvailableAt = marketCandle.AvailableAt,
                    ExecutionCandle = marketCandle,
                    AnalysisBaseCandle = analysisBaseClosed.LastOrDefault(),
                    ClosedIntervals = closedIntervals,
                    Snapshots = primarySnapshots,
                    SnapshotsByProfile = snapshotsByProfile,
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
            }

            FlushPendingCorrelation();

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
                    FailureRecord = session.FailureRecord is StrategyFailureRecord record
                        ? record with { SimulationId = options.SimulationId }
                        : null,
                    FeaturePolicyHash = session.FeaturePolicyHash
                });
                await replayWriter.WriteJournalAsync(
                    session.StrategyId, session.StrategyName, session.Journal.Snapshot(), cancellationToken)
                    .ConfigureAwait(false);
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
                BaseCurrency = options.SimulationOptions.BaseCurrency,
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

    private void PublishProgress(
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

        // EstimateCandleCount is a rough weekday heuristic, not the true candle total, so
        // `processed` can reach or pass it well before the stream actually finishes. Cap the
        // in-flight estimate below 100 so a still-running job can never display "100%" - the
        // real 100% is reported once by the caller when the run actually completes.
        options.Progress.Report(new BacktestProgress
        {
            SimulationId = options.SimulationId,
            Status = isWarmup ? SimulationJobStatus.WarmingUp : SimulationJobStatus.Running,
            CurrentMarketTime = marketTime,
            EvaluationStart = options.EvaluationFrom,
            EvaluationEnd = options.EvaluationTo,
            ProcessedBaseCandles = processed,
            EstimatedTotalBaseCandles = estimated,
            ProgressPercent = Math.Min(99m, percent),
            CandlesPerSecond = cps,
            Strategies = sessions.Select(session => session.ToProgressSnapshot() with
            {
                Instrument = _instrumentByStrategyId.GetValueOrDefault(session.StrategyId)
            }).ToArray()
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
