using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using Brokers.Safety;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ExecutionManager;
using RiskManager;
using RiskManager.Safety;
using RiskManager.Conditions;
using RiskManager.Calibration;
using Simulator.Broker;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Time;
using TradingCore.MarketData;
using TradingCore.Pipeline;
using TradingJournal;
using TradeManager;

namespace Simulator.Engine;

/// <summary>
/// Fully isolated per-strategy mutable session. Never share account/order/position/risk/journal state.
/// </summary>
public sealed class StrategySimulationSession : IAsyncDisposable
{
    /// <summary>Minimum trade-journal capacity regardless of SimulationOptions.LedgerCapacity -
    /// see the comment where this is used in Create().</summary>
    private const int TradeJournalCapacity = 200_000;

    private long _lastProcessedLedgerSequence;
    private long _lastProcessedOrderEventSequence;
    private AgentDecision? _pendingEntryDecision;
    private decimal? _pendingEntryMultiTimeframeAlignment;
    private string? _pendingEntryBrokerOrderId;
    private SimulatedTradeRecord? _activeTrade;
    private string? _pendingExitReason;
    private SimulatedTradeExitReason? _pendingExitReasonKind;
    private PendingPartialExit? _pendingPartialExit;
    private readonly HashSet<string> _completedReductionStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SimulatedTradeRecord> _trades = [];
    private readonly PositionManagementOptions _positionManagementOptions;
    private readonly decimal _estimatedRoundTripCostBasisPoints;
    private readonly IStructureBasedTradeManager _tradeManager;
    private readonly BarInterval _managementInterval;
    private readonly BarInterval _fastStructureInterval;
    private readonly BarInterval _mainStructureInterval;
    private readonly BarInterval _thesisInterval;
    private readonly List<StrategyReplayEvent> _frameEvents = [];
    private long? _lastAmendmentSnapshotVersion;
    private long? _lastReductionSnapshotVersion;
    private readonly Dictionary<TradeManagementEvaluationScope, long>
        _lastManagementEvaluationVersions = [];
    private int _analysisBarsSinceLastAmendment = int.MaxValue;
    private int _analysisBarsSinceLastReduction = int.MaxValue;
    private int _analysisBarsWithoutNewMfe;
    private decimal _lastObservedManagementMfeR;
    private bool _stagnationReductionCompleted;
    private int _structuralDeteriorationReductionCount;
    private int _momentumDecayReductionCount;
    private int _volatilityExhaustionReductionCount;
    private int _regimeDegradationReductionCount;
    private bool _volatilityExpansionSeenSinceEntry;
    private bool _riskWindowReductionCompleted;
    private bool _executionCostStressReductionCompleted;
    private string? _lastManagementAction;
    private string? _lastManagementReason;
    private decimal? _lastExecutablePrice;
    private DateTimeOffset? _nextManagementIntervalClose;
    private long _currentFrameSequence;
    private readonly List<TimeSpan> _frameDurations = [];
    private TimeSpan _totalProcessing;
    private TimeSpan _maxProcessing;
    private TimeSpan _barrierWait;
    private int _peakChannelOccupancy;
    private long _processedFrames;
    private long _managementEvaluations;
    private long _stopAmendmentRequests;
    private long _acceptedStopAmendments;
    private long _rejectedStopAmendments;
    private long _positionReductionRequests;
    private long _acceptedPositionReductions;
    private long _rejectedPositionReductions;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private bool _failed;
    private string? _failureMessage;
    private long? _failedSequence;
    private StrategyFailureRecord? _failureRecord;
    private EquityProtectionDirective? _sharedEquityProtectionDirective;
    private PortfolioRiskStatusSnapshot? _sharedPortfolioRiskStatus;
    private readonly PortfolioManager.CrossMarket.CrossMarketAnalysisCoordinator? _crossMarket;
    private readonly bool _detailedExcursionTracking;
    private List<SimulatedTradePathPoint>? _excursionPath;
    private int _excursionPathBars;

    public StrategySimulationSession(
        string strategyId,
        ITradingAgent strategy,
        SimulatedBrokerClient broker,
        HistoricalSimulationClock clock,
        IExecutionCoordinator execution,
        ITradingSafetyController safety,
        ITradeJournal journal,
        SafeTradingPipeline pipeline,
        IChartAnnotator? independentAnnotator = null,
        PositionManagementOptions? positionManagementOptions = null,
        IStructureBasedTradeManager? tradeManager = null,
        IReadOnlyDictionary<string, PositionManagementOptions>? playbookManagementOverrides = null,
        BarInterval? managementInterval = null,
        RegimeManagementOptions? regimeManagement = null,
        TradeManagementCalibrationOptions? managementCalibrationOptions = null,
        TradeManagementCalibration? managementCalibrationArtifact = null,
        PortfolioManager.CrossMarket.CrossMarketAnalysisCoordinator? crossMarket = null,
        bool detailedExcursionTracking = false,
        string? featurePolicyHash = null,
        AnalysisProfileKey? analysisProfile = null,
        PositionSizingOptions? positionSizingOptions = null)
    {
        // Position sizing already shrinks quantity for this same estimate (see PositionSizer's
        // risk-based branch), so a stop-out that also pays typical costs stays within the
        // intended risk budget. The planned-risk figure recorded on the trade must use the same
        // cost-inclusive distance, or R-multiple reporting keeps showing the pre-fix overshoot
        // even though the real dollar risk is already correctly bounded.
        _estimatedRoundTripCostBasisPoints = positionSizingOptions?.EstimatedRoundTripCostBasisPoints ?? 0m;
        FeaturePolicyHash = featurePolicyHash;
        AnalysisProfile = analysisProfile;
        _crossMarket = crossMarket;
        _detailedExcursionTracking = detailedExcursionTracking;
        StrategyId = strategyId ?? throw new ArgumentNullException(nameof(strategyId));
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        Safety = safety ?? throw new ArgumentNullException(nameof(safety));
        Journal = journal ?? throw new ArgumentNullException(nameof(journal));
        IndependentAnnotator = independentAnnotator;
        _positionManagementOptions = positionManagementOptions ?? new PositionManagementOptions();
        _positionManagementOptions.Validate();
        // Fast/thesis default to the agent's own entry-side timeframe cascade (finest and
        // coarsest of its RequiredIntervals) rather than an independently chosen constant, so
        // trade management stays aligned with whatever timeframes actually justified the entry -
        // a mismatch here (e.g. thesis checks run on a coarser/finer read than the trend timeframe
        // that formed the trade's thesis) is silent and easy to introduce by hand. An explicit
        // PositionManagementOptions value always wins over this derivation. Main has no safe
        // agent-agnostic "second tier" to derive (not every ITradingAgent's TriggerInterval is the
        // ladder's finest member - basket/cross-market agents in particular can violate that), so
        // it keeps its original fallback to the fast interval; SimulationStrategyProfile.Validate's
        // ValidateManagementTimeframeAlignment is what actually catches a stale/mismatched explicit
        // override for the real (non-synthetic-test) profiles this is meant to protect.
        _fastStructureInterval = _positionManagementOptions.FastStructureInterval ??
            strategy.TriggerInterval;
        _mainStructureInterval = _positionManagementOptions.MainStructureInterval ??
            managementInterval ?? _positionManagementOptions.ManagementInterval ?? strategy.TriggerInterval;
        _thesisInterval = _positionManagementOptions.ThesisInterval ??
            strategy.RequiredIntervals
                .OrderByDescending(BarIntervalParser.ApproximateSeconds)
                .First();
        _managementInterval = _mainStructureInterval;
        if (!_fastStructureInterval.IsValid || !_mainStructureInterval.IsValid || !_thesisInterval.IsValid)
            throw new ArgumentException("All management intervals must be valid.", nameof(managementInterval));
        if (BarIntervalParser.CompareDuration(_fastStructureInterval, _mainStructureInterval) > 0 ||
            BarIntervalParser.CompareDuration(_mainStructureInterval, _thesisInterval) > 0)
        {
            throw new ArgumentException(
                "Management intervals must be ordered fast <= main <= thesis.",
                nameof(positionManagementOptions));
        }
        IStructureBasedTradeManager resolvedTradeManager = tradeManager ?? (managementCalibrationOptions is { Enabled: true }
            ? new CalibratedStructureBasedTradeManager(
                _positionManagementOptions,
                managementCalibrationArtifact ?? throw new ArgumentException(
                    "Enabled management calibration requires an artifact.", nameof(managementCalibrationArtifact)),
                managementCalibrationOptions,
                regimeManagement)
            : regimeManagement is { Enabled: true }
                ? new RegimeAwareStructureBasedTradeManager(_positionManagementOptions, regimeManagement)
                : new StructureBasedTradeManager(_positionManagementOptions));
        // Wraps rather than replaces resolvedTradeManager, so calibration/regime-awareness above
        // is unaffected for every playbook without its own entry here - see
        // PlaybookAwareTradeManager for why this exists (IndicatorConfluencePlaybook's ATR-only
        // trades need a different profile than StructuralDefaults' zone-anchored one).
        _tradeManager = playbookManagementOverrides is { Count: > 0 }
            ? new PlaybookAwareTradeManager(resolvedTradeManager, playbookManagementOverrides)
            : resolvedTradeManager;
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public string StrategyId { get; }
    public string StrategyName => Strategy.Name;
    public ITradingAgent Strategy { get; }
    public SimulatedBrokerClient Broker { get; }
    public IExecutionCoordinator Execution { get; }
    public ITradingSafetyController Safety { get; }
    public ITradeJournal Journal { get; }
    public IChartAnnotator? IndependentAnnotator { get; }
    public SafeTradingPipeline Pipeline { get; }
    /// <summary>Content hash of the <c>RuntimeFeaturePolicy</c> this session's pipeline was built
    /// from - populated only when <see cref="Create"/> constructs the session (never set by
    /// direct construction), so its presence proves the pipeline factory was actually exercised
    /// rather than left dead.</summary>
    public string? FeaturePolicyHash { get; }
    /// <summary>The analysis profile this session's frames were resolved against - set only
    /// when <see cref="Create"/> constructs the session. Null means "use <c>frame.Snapshots</c>
    /// directly," the safe fallback for direct-construction callers/tests.</summary>
    public AnalysisProfileKey? AnalysisProfile { get; }
    public HistoricalSimulationClock Clock { get; }
    public IReadOnlyList<SimulatedTradeRecord> Trades => _trades;
    public SimulatedTradeRecord? ActiveTrade => _activeTrade;
    internal decimal? LastExecutablePrice => _lastExecutablePrice;
    public bool IsFailed => _failed;
    public string? FailureMessage => _failureMessage;
    public long? FailedSequence => _failedSequence;
    /// <summary>Richer context captured at the moment of failure - null when the session was
    /// marked failed via <see cref="MarkFailed"/> instead of catching its own exception (e.g. a
    /// StopFailedStrategyOnly barrier placeholder), since there is no frame to capture from
    /// there.</summary>
    public StrategyFailureRecord? FailureRecord => _failureRecord;

    internal void SetSharedEquityProtectionDirective(EquityHighWatermarkSnapshot snapshot)
    {
        _sharedEquityProtectionDirective = snapshot.PendingPositionAction is
                RiskManager.Safety.EquityProtectionAction action &&
            snapshot.PendingPositionTierId is string tierId
            ? new EquityProtectionDirective
            {
                TierId = $"account:{tierId}",
                Action = action == RiskManager.Safety.EquityProtectionAction.FlattenAllPositions
                    ? EquityProtectionPositionAction.FlattenAllPositions
                    : EquityProtectionPositionAction.ReduceOpenPositions,
                ReductionFraction = snapshot.PendingPositionReductionFraction
            }
            : null;
    }

    internal void SetSharedPortfolioRiskStatus(PortfolioRiskStatusSnapshot snapshot) =>
        _sharedPortfolioRiskStatus = snapshot;

    public static StrategySimulationSession Create(
        string strategyId,
        ITradingAgent agent,
        SimulationOptions simulationOptions,
        TradingSafetyOptions? safetyOptions = null,
        MarketDataQualityOptions? dataQualityOptions = null,
        AnalysisSharingMode analysisSharing = AnalysisSharingMode.SharedImmutableSnapshots,
        ChartAnnotationOptions? annotationOptions = null,
        PositionManagementOptions? positionManagementOptions = null,
        IReadOnlyDictionary<string, PositionManagementOptions>? playbookManagementOverrides = null,
        BarInterval? managementInterval = null,
        PositionSizingOptions? positionSizingOptions = null,
        AdaptiveRiskOptions? adaptiveRiskOptions = null,
        TradingConditionOptions? tradingConditionOptions = null,
        IEconomicEventProvider? economicEventProvider = null,
        RegimeManagementOptions? regimeManagementOptions = null,
        SetupCalibrationPolicyOptions? setupCalibrationOptions = null,
        SetupCalibrationArtifact? setupCalibrationArtifact = null,
        ISetupMetaModel? metaModel = null,
        TradeManagementCalibrationOptions? managementCalibrationOptions = null,
        TradeManagementCalibration? managementCalibrationArtifact = null,
        Func<string, IExecutionCoordinator, IExecutionCoordinator>? executionDecorator = null,
        PortfolioManager.CrossMarket.CrossMarketAnalysisCoordinator? crossMarket = null,
        bool detailedExcursionTracking = false,
        RuntimeFeaturePolicy? featurePolicy = null,
        string? strategyVersion = null,
        AnalysisProfileKey? analysisProfile = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        SimulationOptions options = simulationOptions;
        var clock = new HistoricalSimulationClock();
        var broker = new SimulatedBrokerClient(options, clock);

        TradingSafetyOptions resolvedSafety = safetyOptions ?? new TradingSafetyOptions
        {
            TripOnCriticalDataQualityIssue = true
        };
        var safety = new TradingSafetyController(resolvedSafety);
        // Deliberately not options.LedgerCapacity: the journal records once per signal
        // evaluation (every trigger-interval close), a far higher-frequency stream than the
        // financial ledger it was previously sized after (deposits/commissions/PnL, roughly
        // once per trade). Reusing that number meant a multi-month backtest could silently
        // overflow and lose its early decision history well before the run finished - by the
        // time WriteJournalAsync exports it, only the tail would remain.
        var journal = new InMemoryTradeJournal(Math.Max(options.LedgerCapacity, TradeJournalCapacity));
        var dataQuality = new MarketDataQualityGate(dataQualityOptions ?? new MarketDataQualityOptions
        {
            RequireIndicatorsReady = true,
            RejectGaps = false
        });

        // Risk budget is primarily owned by PositionSizer (fixed-fractional / cash risk).
        // A second hard 0.5%-of-balance clamp / portfolio-heat gate here rejected many
        // otherwise-valid entries when sizing fell back to fixed quantity or conversion
        // rates were approximate.
        PreTradeRiskOptions riskOptions = agent.ExitManagementMode switch
        {
            AgentExitManagementMode.ProtectiveStopAndStrategyExit => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = false,
                MinimumRewardRiskRatio = null,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null,
                AllowPyramiding = false
            },
            AgentExitManagementMode.Bracket => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = true,
                MinimumRewardRiskRatio = PreTradeRiskOptions.PhaseOneSafeDefaults.MinimumRewardRiskRatio,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null,
                AllowPyramiding = false
            },
            _ => PreTradeRiskOptions.PhaseOneSafeDefaults with
            {
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null
            }
        };

        var execution = new ExecutionCoordinator(
            null,
            new PreTradeRiskManager(riskOptions),
            new BrokerExecutionSafety(),
            safety,
            journal,
            new PositionSizer(positionSizingOptions),
            new RiskBudgetPolicy(adaptiveRiskOptions));
        IExecutionCoordinator sessionExecution = executionDecorator is null
            ? execution
            : executionDecorator(strategyId, execution);

        IChartAnnotator? independent = analysisSharing == AnalysisSharingMode.IndependentPerStrategy
            ? new ChartAnnotationEngine(annotationOptions)
            : null;
        ITradingConditionFilter? tradingConditions = tradingConditionOptions is null
            ? null
            : new TradingConditionFilter(tradingConditionOptions, economicEventProvider);

        RuntimeFeaturePolicy resolvedFeaturePolicy = featurePolicy ?? new RuntimeFeaturePolicy
        {
            AnnotationOptions = annotationOptions ?? new(),
            SetupCalibration = setupCalibrationOptions ?? new(),
            MarketRegimeRouting = new(),
            ValueLocationEvidence = new(),
            CurrencyStrengthEvidence = new(),
            RsiBollingerSignals = new(),
            DmiConfirmationEnabled = true,
            CurrencyStrength = new()
        };

        var pipelineFactory = new StrategyDecisionPipelineFactory(
            sessionExecution, dataQuality, journal, tradingConditions, sharedSafetyController: safety);

        StrategyDecisionRuntime decisionRuntime = pipelineFactory.Create(
            new StrategyRuntimeDefinition
            {
                StrategyId = strategyId,
                StrategyVersion = strategyVersion ?? strategyId,
                Agent = agent
            },
            resolvedFeaturePolicy,
            setupCalibrationArtifact,
            metaModel,
            resolvedSafety);

        return new StrategySimulationSession(
            strategyId,
            agent,
            broker,
            clock,
            sessionExecution,
            safety,
            journal,
            decisionRuntime.Pipeline,
            independent,
            positionManagementOptions,
            playbookManagementOverrides: playbookManagementOverrides,
            managementInterval: managementInterval,
            regimeManagement: regimeManagementOptions,
            managementCalibrationOptions: managementCalibrationOptions,
            managementCalibrationArtifact: managementCalibrationArtifact,
            crossMarket: crossMarket,
            detailedExcursionTracking: detailedExcursionTracking,
            featurePolicyHash: decisionRuntime.FeaturePolicyHash,
            analysisProfile: analysisProfile,
            positionSizingOptions: positionSizingOptions);
    }

    public async Task<StrategyFrameResult> ProcessFrameAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _frameEvents.Clear();
            _currentFrameSequence = frame.Sequence;
            if (_failed)
            {
                return BuildResult(frame, sw.Elapsed);
            }

            Candle executionCandle = frame.ExecutionCandle.Mid;
            DateTimeOffset eventTime = frame.AvailableAt;
            _lastExecutablePrice = ResolveExecutablePrice(frame.ExecutionCandle, _activeTrade?.Side);
            _startedAt ??= executionCandle.OpenTime;
            _endedAt = eventTime;
            Clock.AdvanceTo(eventTime);

            BrokerPosition? positionBefore = (await Broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);

            if (_activeTrade is not null)
                UpdateExcursions(frame.ExecutionCandle);

            await Broker.Runtime.ProcessExecutionCandleAsync(executionCandle, cancellationToken)
                .ConfigureAwait(false);
            CaptureExecutionEvents();
            // Financing is posted before fills on the execution frame. Attribute it
            // while the strategy-owned trade is still active, including a rollover
            // frame that also closes the position.
            RecordNewClosedTrades();

            BrokerPosition? positionAfter = (await Broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);

            CapturePositionTransition(executionCandle, positionBefore, positionAfter);
            if (positionAfter is not null && _activeTrade is not null)
                await SynchronizeProtectiveStopAsync(positionAfter, cancellationToken).ConfigureAwait(false);
            if (positionBefore is null && positionAfter is not null && _activeTrade is not null)
                UpdateExcursions(frame.ExecutionCandle);
            if (!frame.IsWarmup)
            {
                TradingSafetySnapshot previousSafety = Safety.Snapshot;
                AccountSnapshot safetyAccount = Broker.State.GetAccount();
                decimal equity = (safetyAccount.Balance ?? 0m) +
                    (safetyAccount.UnrealizedProfitLoss ?? 0m);
                TradingSafetySnapshot currentSafety = Safety.ObserveEquity(equity, eventTime);
                EquityHighWatermarkSnapshot previousWatermark = previousSafety.EquityProtection;
                EquityHighWatermarkSnapshot currentWatermark = currentSafety.EquityProtection;
                if (currentWatermark.PeakEquity > previousWatermark.PeakEquity ||
                    currentWatermark.CurrentRiskMultiplier != previousWatermark.CurrentRiskMultiplier)
                {
                    AddFrameEvent(
                        StrategyReplayEventType.StrategyHighWatermarkUpdated,
                        eventTime,
                        _activeTrade?.SetupId,
                        _activeTrade?.PositionId,
                        reason: $"Strategy equity={currentWatermark.CurrentEquity:F2}; peak={currentWatermark.PeakEquity:F2}; drawdown={currentWatermark.DrawdownPercent:F3}%.",
                        reasonCode: "StrategyHighWatermarkUpdated",
                        previousValue: previousWatermark.PeakEquity,
                        newValue: currentWatermark.PeakEquity,
                        optionOrModelVersion: "strategy-equity-protection-v1");
                }
                HashSet<string> previousTiers = previousWatermark.ActivatedTierIds
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string tier in currentWatermark.ActivatedTierIds
                             .Where(tier => !previousTiers.Contains(tier))
                             .OrderBy(tier => tier, StringComparer.Ordinal))
                {
                    AddFrameEvent(
                        StrategyReplayEventType.EquityProtectionTierActivated,
                        eventTime,
                        _activeTrade?.SetupId,
                        _activeTrade?.PositionId,
                        reason: $"Strategy equity-protection tier '{tier}' activated.",
                        reasonCode: tier,
                        optionOrModelVersion: "strategy-equity-protection-v1");
                }
                if (previousWatermark.ActivatedTierIds.Count > 0 &&
                    currentWatermark.ActivatedTierIds.Count == 0)
                {
                    AddFrameEvent(
                        StrategyReplayEventType.EquityProtectionRecovered,
                        eventTime,
                        _activeTrade?.SetupId,
                        _activeTrade?.PositionId,
                        reason: "Strategy equity protection recovered after the configured confirmation period.",
                        reasonCode: "StrategyEquityProtectionRecovered",
                        optionOrModelVersion: "strategy-equity-protection-v1");
                }
                if (currentSafety.State != previousSafety.State ||
                    currentSafety.Reason != previousSafety.Reason)
                {
                    AddFrameEvent(
                        StrategyReplayEventType.SafetyStateChanged,
                        eventTime,
                        _activeTrade?.SetupId,
                        _activeTrade?.PositionId,
                        reason: currentSafety.Message,
                        reasonCode: currentSafety.Reason.ToString());
                }
            }

            SimulatedTradeRecord? newlyCompleted = null;
            if (_trades.Count > 0)
            {
                SimulatedTradeRecord last = _trades[^1];
                if (last.ClosedAt == eventTime)
                    newlyCompleted = last;
            }

            if (!frame.IsWarmup &&
                frame.ClosedIntervals.Contains(Strategy.TriggerInterval) &&
                HasRequiredSnapshots(frame))
            {
                AccountSnapshot account = (await Broker.Accounts
                    .GetAccountsAsync(cancellationToken)
                    .ConfigureAwait(false)).Single();
                IReadOnlyList<BrokerPosition> positions = await Broker.Positions
                    .GetOpenPositionsAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<BrokerOrder> openOrders = await Broker.Orders
                    .GetOpenOrdersAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots =
                    FilterSnapshots(frame);

                var analysis = new MultiTimeframeAnalysis(
                    executionCandle.Instrument,
                    eventTime,
                    snapshots);

                AgentMarketContext context = new()
                {
                    Instrument = executionCandle.Instrument,
                    Timestamp = eventTime,
                    Analysis = analysis,
                    Account = account,
                    Positions = positions,
                    OpenOrders = openOrders,
                    ExecutableSpread = executionCandle.Prices.Close * Broker.Options.SpreadBasisPoints / 10_000m,
                    // Full round trip: one spread (paid via half-spread on each leg, same as
                    // ExecutableSpread above) plus slippage and commission on both the entry and
                    // exit fill.
                    RoundTripCostEstimate =
                        executionCandle.Prices.Close * Broker.Options.SpreadBasisPoints / 10_000m +
                        executionCandle.Prices.Close * 2m * Broker.Options.SlippageBasisPoints / 10_000m +
                        executionCandle.Prices.Close * 2m * Broker.Options.CommissionRate,
                    MarketDataAvailableAt = frame.AvailableAt,
                    StrategyId = StrategyId,
                    CurrencyStrength = _crossMarket?.GetSnapshot(executionCandle.Instrument)
                };

                TradingPipelineResult pipelineResult = await Pipeline
                    .ProcessAsync(context, Broker, cancellationToken)
                    .ConfigureAwait(false);
                CaptureDecision(pipelineResult, analysis);
                CaptureExecutionEvents();
            }

            if (!frame.IsWarmup && _activeTrade is not null)
            {
                bool managementActionTaken = false;
                managementActionTaken = await EvaluateClosedManagementLayerAsync(
                        frame,
                        _thesisInterval,
                        TradeManagementEvaluationScope.Thesis,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!managementActionTaken)
                {
                    managementActionTaken = await EvaluateClosedManagementLayerAsync(
                            frame,
                            _mainStructureInterval,
                            TradeManagementEvaluationScope.MainStructure,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!managementActionTaken)
                {
                    managementActionTaken = await EvaluateClosedManagementLayerAsync(
                            frame,
                            _fastStructureInterval,
                            TradeManagementEvaluationScope.FastStructure,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!managementActionTaken &&
                    _positionManagementOptions.EvaluateMechanicalProtectionOnEveryExecutionFrame &&
                    ResolveMechanicalSnapshot(frame) is AnalysisSnapshot mechanicalSnapshot)
                {
                    AnalysisSnapshot executionFrameSnapshot = mechanicalSnapshot with
                    {
                        AvailableAt = frame.AvailableAt,
                        Version = -Math.Max(1L, frame.Sequence),
                        LatestCandle = frame.ExecutionCandle.Mid
                    };
                    await EvaluatePositionManagementAsync(
                            frame,
                            executionFrameSnapshot,
                            TradeManagementEvaluationScope.Mechanical,
                            frame.ExecutionCandle.Mid.Interval,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (frame.IsLastCandle &&
                Broker.Options.CloseOpenPositionsAtEnd)
            {
                BrokerPosition? open = (await Broker.Positions
                    .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);
                if (open is not null)
                {
                    _pendingExitReason = "End of simulation liquidation.";
                    _pendingExitReasonKind = SimulatedTradeExitReason.EndOfSimulation;
                    await Broker.Runtime.LiquidateAtMarketCloseAsync(executionCandle, cancellationToken)
                        .ConfigureAwait(false);
                    BrokerPosition? afterLiquidation = (await Broker.Positions
                        .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);
                    CapturePositionTransition(executionCandle, open, afterLiquidation);
                    RecordNewClosedTrades();
                    if (_trades.Count > 0 && _trades[^1].ClosedAt == eventTime)
                        newlyCompleted = _trades[^1];
                }
            }

            _processedFrames++;
            sw.Stop();
            RecordTiming(sw.Elapsed);
            StrategyFrameResult result = BuildResult(frame, sw.Elapsed);
            return newlyCompleted is null ? result : result with { NewlyCompletedTrade = newlyCompleted };
        }
        catch (Exception exception)
        {
            _failed = true;
            _failureMessage = exception.ToString();
            _failedSequence = frame.Sequence;
            _failureRecord = await BuildFailureRecordSafeAsync(frame, exception).ConfigureAwait(false);
            sw.Stop();
            RecordTiming(sw.Elapsed);
            throw;
        }
    }

    /// <summary>Captures broker/setup context at the moment of failure for post-mortem
    /// debugging (previously only the bare exception was kept, discarding what the strategy
    /// was actually doing when it broke). SimulationId is filled in by the caller, which is the
    /// only place that knows it. Broker queries are best-effort: a failure querying broker
    /// state here must never mask or replace the original exception already being handled.</summary>
    private async Task<StrategyFailureRecord> BuildFailureRecordSafeAsync(MarketFrame frame, Exception exception)
    {
        string? openPosition = null;
        int pendingOrders = 0;
        try
        {
            BrokerPosition? position = (await Broker.Positions
                .GetOpenPositionsAsync(CancellationToken.None).ConfigureAwait(false))
                .FirstOrDefault(item => item.Instrument == frame.ExecutionCandle.Mid.Instrument);
            if (position is not null)
            {
                openPosition = $"{position.Side} {position.Quantity:F8} @ {position.AveragePrice:F8}, " +
                    $"unrealized={position.UnrealizedProfitLoss:F2}";
            }

            pendingOrders = (await Broker.Orders
                .GetOpenOrdersAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false)).Count;
        }
        catch
        {
            // Best-effort context capture only - the original exception is what matters and
            // must propagate untouched regardless of whether this secondary query succeeds.
        }

        SimulatedTradeRecord? lastTrade = _trades.Count > 0 ? _trades[^1] : null;
        return new StrategyFailureRecord
        {
            SimulationId = Guid.Empty,
            StrategyName = StrategyName,
            Sequence = frame.Sequence,
            MarketTime = frame.AvailableAt,
            InputStreamId = frame.InputStreamId,
            CurrentSetup = _activeTrade?.SetupId,
            OpenPosition = openPosition,
            PendingOrders = pendingOrders,
            LastCompletedTrade = lastTrade is null
                ? null
                : $"{lastTrade.PositionId} {lastTrade.Side} entry={lastTrade.EntryPrice:F8} " +
                  $"exit={lastTrade.ExitPrice:F8} pnl={lastTrade.NetProfitLoss:F2} closedAt={lastTrade.ClosedAt:O}",
            Exception = exception.ToString()
        };
    }

    public void RecordBarrierWait(TimeSpan wait) => _barrierWait += wait;

    public void ObserveChannelOccupancy(int occupancy) =>
        _peakChannelOccupancy = Math.Max(_peakChannelOccupancy, occupancy);

    public void MarkFailed(long sequence, string message)
    {
        _failed = true;
        _failedSequence = sequence;
        _failureMessage = message;
    }

    public StrategyWorkerMetrics BuildMetrics()
    {
        TimeSpan average = _processedFrames == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(_totalProcessing.Ticks / _processedFrames);
        return new StrategyWorkerMetrics
        {
            StrategyName = StrategyName,
            ProcessedFrames = _processedFrames,
            TotalProcessingTime = _totalProcessing,
            MaximumFrameProcessingTime = _maxProcessing,
            AverageFrameProcessingTime = average,
            BarrierWaitTime = _barrierWait,
            PeakChannelOccupancy = _peakChannelOccupancy,
            ManagementEvaluations = _managementEvaluations,
            StopAmendmentRequests = _stopAmendmentRequests,
            AcceptedStopAmendments = _acceptedStopAmendments,
            RejectedStopAmendments = _rejectedStopAmendments,
            PositionReductionRequests = _positionReductionRequests,
            AcceptedPositionReductions = _acceptedPositionReductions,
            RejectedPositionReductions = _rejectedPositionReductions
        };
    }

    public async Task<SimulationResult> BuildSimulationResultAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = _startedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset ended = _endedAt ?? started;
        SimulationResult result = Broker.State.BuildResult(started, ended);
        if (_activeTrade is not null)
        {
            _trades.Add(_activeTrade with
            {
                ClosedAt = ended,
                ExitReason = SimulatedTradeExitReason.EndOfSimulation,
                ExitReasonText = "Position remained open at the end of the requested history.",
                FinalStopLossPrice = _activeTrade.CurrentStopLossPrice ?? _activeTrade.InitialStopLossPrice,
                ExcursionPath = _excursionPath?.ToArray()
            });
            _activeTrade = null;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        EquityHighWatermarkSnapshot equityProtection = Safety.Snapshot.EquityProtection;
        return result with
        {
            Trades = _trades.ToArray(),
            EquityProtectionPeakEquity = equityProtection.PeakEquity,
            EquityProtectionActivationCount = equityProtection.TotalActivatedTierCount
        };
    }

    public StrategyProgressSnapshot ToProgressSnapshot()
    {
        decimal balance = Broker.Options.StartingBalance;
        decimal equity = balance;
        decimal unrealized = 0m;
        int openPositions = 0;
        try
        {
            SimulationResult interim = Broker.State.BuildResult(
                _startedAt ?? DateTimeOffset.UtcNow,
                _endedAt ?? DateTimeOffset.UtcNow);
            balance = interim.FinalBalance;
            equity = interim.FinalEquity;
            unrealized = interim.UnrealizedProfitLoss;
            openPositions = interim.OpenPositions.Count;
        }
        catch
        {
            // Session may not have processed any candles yet.
        }

        OpenPositionManagementSnapshot? management = null;
        if (_activeTrade?.EntryPrice is decimal entry &&
            (_activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice) is decimal initialStop &&
            (_activeTrade.CurrentStopLossPrice ?? initialStop) is decimal currentStop)
        {
            decimal initialRisk = Math.Abs(entry - initialStop);
            decimal currentPrice = _lastExecutablePrice ?? entry;
            decimal openR = initialRisk <= 0m
                ? 0m
                : _activeTrade.Side == OrderSide.Buy
                    ? (currentPrice - entry) / initialRisk
                    : (entry - currentPrice) / initialRisk;
            decimal lockedR = initialRisk <= 0m
                ? 0m
                : _activeTrade.Side == OrderSide.Buy
                    ? (currentStop - entry) / initialRisk
                    : (entry - currentStop) / initialRisk;
            management = new OpenPositionManagementSnapshot
            {
                SetupId = _activeTrade.SetupId,
                Side = _activeTrade.Side.ToString(),
                EntryPrice = entry,
                InitialQuantity = _activeTrade.InitialQuantity > 0m
                    ? _activeTrade.InitialQuantity
                    : _activeTrade.Quantity,
                RemainingQuantity = _activeTrade.RemainingQuantity,
                PositionReductionCount = _activeTrade.PositionReductionCount,
                ReductionPending = _pendingPartialExit is not null,
                InitialStop = initialStop,
                CurrentStop = currentStop,
                Target = _activeTrade.TakeProfitPrice,
                CurrentOpenR = openR,
                MaximumOpenR = _activeTrade.MaximumFavourableExcursionR ?? 0m,
                LockedInR = lockedR,
                TrailingMode = _positionManagementOptions.Mode.ToString(),
                LastManagementAction = _lastManagementAction,
                LastManagementReason = _lastManagementReason,
                NextManagementIntervalClose = _nextManagementIntervalClose,
                EntryRegime = _activeTrade.EntryRegime.ToString(),
                CurrentRegime = _activeTrade.CurrentRegime.ToString(),
                RegimeConfidence = _activeTrade.EntryRegimeConfidence,
                BaseRiskBudget = _activeTrade.FinalRiskBudgetMultiplier is > 0m &&
                    _activeTrade.PlannedStopRiskAccountCurrency is decimal plannedRisk
                    ? plannedRisk / _activeTrade.FinalRiskBudgetMultiplier.Value
                    : _activeTrade.PlannedStopRiskAccountCurrency,
                FinalRiskBudget = _activeTrade.PlannedStopRiskAccountCurrency,
                FinalRiskMultiplier = _activeTrade.FinalRiskBudgetMultiplier,
                RiskMultipliers = RiskMultipliers(_activeTrade),
                RawQuantity = _activeTrade.BaseRequestedQuantity,
                AllocatedQuantity = _activeTrade.AllocatedQuantity,
                PlannedStopRisk = _activeTrade.PlannedStopRiskAccountCurrency,
                PortfolioReservationId = _activeTrade.PortfolioReservationId,
                CorrelationClusterId = _activeTrade.CorrelationClusterId
            };
        }

        EquityHighWatermarkSnapshot equityProtectionState = Safety.Snapshot.EquityProtection;
        EquityProtectionStatusSnapshot? equityProtectionSnapshot = equityProtectionState.PeakEquity > 0m
            ? new EquityProtectionStatusSnapshot
            {
                PeakEquity = equityProtectionState.PeakEquity,
                DrawdownPercent = equityProtectionState.DrawdownPercent,
                CurrentRiskMultiplier = equityProtectionState.CurrentRiskMultiplier,
                ActivatedTierIds = equityProtectionState.ActivatedTierIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                NewEntriesPaused = !Safety.CanOpenNewTrades &&
                    Safety.Snapshot.Reason == SafetyTripReason.EquityProtectionTier
            }
            : null;

        return new StrategyProgressSnapshot
        {
            StrategyName = StrategyName,
            StrategyId = StrategyId,
            Balance = balance,
            Equity = equity,
            UnrealizedProfitLoss = unrealized,
            OpenPositions = openPositions,
            CompletedTrades = _trades.Count(trade => trade.ClosedAt is not null),
            ActiveSetups = _activeTrade is null ? 0 : 1,
            NetProfit = equity - Broker.Options.StartingBalance,
            Status = _failed ? "Failed" : "Running",
            LastError = _failureMessage,
            Performance = StrategyPerformanceSnapshot.FromTrades(_trades),
            OpenPositionManagement = management,
            EquityProtection = equityProtectionSnapshot,
            PortfolioRisk = _sharedPortfolioRiskStatus
        };
    }

    public ValueTask DisposeAsync() => Broker.DisposeAsync();

    internal void ApplyPortfolioAdmission(AgentDecision decision, OrderSubmission submission)
    {
        _pendingEntryDecision = decision;
        _pendingEntryBrokerOrderId = submission.BrokerOrderId;
    }

    internal void RejectPortfolioAdmission(AgentDecision decision, string? reason)
    {
        if (_pendingEntryDecision?.DecisionId == decision.DecisionId)
        {
            _pendingEntryDecision = null;
            _pendingEntryBrokerOrderId = null;
        }
        _lastManagementReason = reason;
    }

    /// <summary>Resolves the snapshot dictionary this session should read from
    /// <paramref name="frame"/>: its own <see cref="AnalysisProfile"/> entry in
    /// <c>frame.SnapshotsByProfile</c> when both are present, else <c>frame.Snapshots</c> - the
    /// safe fallback for direct-construction callers/tests and for the
    /// <see cref="AnalysisSharingMode.IndependentPerStrategy"/> path, which never populates a
    /// profile (it reads <see cref="IndependentAnnotator"/> directly instead).</summary>
    private IReadOnlyDictionary<BarInterval, AnalysisSnapshot> ResolveSnapshots(MarketFrame frame)
    {
        if (AnalysisProfile is AnalysisProfileKey profile &&
            frame.SnapshotsByProfile is not null &&
            frame.SnapshotsByProfile.TryGetValue(profile, out IReadOnlyDictionary<BarInterval, AnalysisSnapshot>? profileSnapshots))
        {
            return profileSnapshots;
        }
        return frame.Snapshots;
    }

    private Dictionary<BarInterval, AnalysisSnapshot> FilterSnapshots(MarketFrame frame)
    {
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots = ResolveSnapshots(frame);
        var filtered = new Dictionary<BarInterval, AnalysisSnapshot>();
        foreach (BarInterval interval in Strategy.RequiredIntervals)
        {
            AnalysisSnapshot? snapshot = null;
            // Independent analysis: prefer this session's private annotator state.
            if (IndependentAnnotator is not null)
            {
                snapshot = IndependentAnnotator.GetLatest(frame.ExecutionCandle.Instrument, interval);
            }

            if (snapshot is null)
                snapshots.TryGetValue(interval, out snapshot);

            if (snapshot is not null)
                filtered[interval] = snapshot;
        }

        return filtered;
    }

    private bool HasRequiredSnapshots(MarketFrame frame)
    {
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots = ResolveSnapshots(frame);
        foreach (BarInterval interval in Strategy.RequiredIntervals)
        {
            AnalysisSnapshot? snapshot = null;
            if (IndependentAnnotator is not null)
                snapshot = IndependentAnnotator.GetLatest(frame.ExecutionCandle.Instrument, interval);
            if (snapshot is null)
                snapshots.TryGetValue(interval, out snapshot);
            if (snapshot is null || snapshot.AvailableAt > frame.AvailableAt)
                return false;
        }

        return true;
    }

    private void CaptureDecision(TradingPipelineResult result, MultiTimeframeAnalysis analysis)
    {
        AgentDecision? decision = result.Decision;
        if (decision is null)
        {
            return;
        }

        foreach (StructuralPlaybookDiagnostic diagnostic in decision.StructuralPlaybookDiagnostics)
        {
            AddFrameEvent(
                StrategyReplayEventType.StructuralPlaybookEvaluated,
                decision.CreatedAt,
                reason: diagnostic.EvaluationReasonCode,
                reasonCode: diagnostic.OutcomeReasonCode,
                decisionId: decision.DecisionId,
                optionOrModelVersion: diagnostic.PlaybookVersion,
                evaluationSetupId: diagnostic.SetupId,
                playbookId: diagnostic.PlaybookId,
                structuralDirection: diagnostic.Direction,
                structuralLifecycle: diagnostic.Lifecycle,
                isEntryEligible: diagnostic.IsEntryEligible,
                isReady: diagnostic.IsReady,
                isSelected: diagnostic.IsSelected,
                playbookOutcome: diagnostic.Outcome,
                primaryBlockingReasonCode: diagnostic.PrimaryBlockingReasonCode,
                failedGateReasonCodes: diagnostic.FailedGateReasonCodes,
                supportingEvidence: diagnostic.SupportingEvidence,
                conflictingEvidence: diagnostic.ConflictingEvidence,
                structuralConfidence: diagnostic.Confidence,
                mandatoryQualityFloor: diagnostic.MandatoryQualityFloor);
        }

        if (result.TradingCondition is TradingConditionDecision tradingCondition)
        {
            AddFrameEvent(
                tradingCondition.Action is TradingConditionAction.DelayEntry or TradingConditionAction.RejectEntry
                    ? StrategyReplayEventType.TradingConditionRejected
                    : StrategyReplayEventType.TradingConditionEvaluated,
                decision.CreatedAt,
                decision.SetupId,
                reason: tradingCondition.Explanation,
                reasonCode: tradingCondition.ReasonCode);
        }

        if (result.SetupCalibration is SetupCalibrationDecision calibration)
        {
            AddFrameEvent(
                calibration.Trade
                    ? StrategyReplayEventType.SetupCalibrationEvaluated
                    : StrategyReplayEventType.SetupCalibrationRejected,
                decision.CreatedAt,
                decision.SetupId,
                reason: calibration.Explanation,
                reasonCode: calibration.ReasonCode,
                decisionId: decision.DecisionId,
                newValue: calibration.RiskMultiplier,
                optionOrModelVersion: calibration.CalibrationId);
        }

        if (result.MetaLabel is MetaLabelDecision metaLabel)
        {
            AddFrameEvent(
                metaLabel.Trade
                    ? StrategyReplayEventType.MetaLabelEvaluated
                    : StrategyReplayEventType.MetaLabelRejected,
                decision.CreatedAt,
                decision.SetupId,
                reason: $"probability={metaLabel.Probability:F4}; risk={metaLabel.RiskMultiplier:F3}",
                reasonCode: metaLabel.ReasonCode,
                decisionId: decision.DecisionId,
                newValue: metaLabel.Probability,
                optionOrModelVersion: metaLabel.ModelVersion);
        }

        if (decision.Action == AgentAction.Observe &&
            (!string.IsNullOrWhiteSpace(decision.ReasonCode) ||
             decision.Reason.Contains("price-action", StringComparison.OrdinalIgnoreCase)))
        {
            AddFrameEvent(
                StrategyReplayEventType.PriceActionEvaluated,
                decision.CreatedAt,
                decision.SetupId,
                reason: decision.Reason,
                reasonCode: decision.ReasonCode,
                priceActionTrigger: decision.PriceActionTrigger,
                priceActionConfidence: decision.PriceActionConfidence);
        }

        if (decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            if (decision.PriceActionTrigger is not null)
            {
                AddFrameEvent(
                    StrategyReplayEventType.PriceActionConfirmed,
                    decision.CreatedAt,
                    decision.SetupId,
                    reason: decision.Reason,
                    reasonCode: decision.ReasonCode,
                    priceActionTrigger: decision.PriceActionTrigger,
                    priceActionConfidence: decision.PriceActionConfidence);
            }
            AddFrameEvent(
                StrategyReplayEventType.SignalCreated,
                decision.CreatedAt,
                decision.SetupId,
                reason: decision.Reason,
                reasonCode: decision.ReasonCode,
                priceActionTrigger: decision.PriceActionTrigger,
                priceActionConfidence: decision.PriceActionConfidence);
        }
        if (result.Submission is null)
            return;
        if (result.Submission.Status == SubmissionStatus.Rejected)
        {
            AddFrameEvent(
                StrategyReplayEventType.OrderRejected,
                decision.CreatedAt,
                decision.SetupId,
                reason: result.Submission.RejectionReason);
            return;
        }

        if (decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            _pendingEntryDecision = decision;
            _pendingEntryMultiTimeframeAlignment = MetaLabelFeatureFactory.ComputeMultiTimeframeAlignment(
                decision.Action, analysis);
            _pendingEntryBrokerOrderId = result.Submission.BrokerOrderId;
            AddFrameEvent(
                StrategyReplayEventType.OrderSubmitted,
                decision.CreatedAt,
                decision.SetupId,
                reason: decision.Reason);
        }
        else if (decision.Action == AgentAction.Close)
        {
            _pendingExitReason = decision.Reason;
            _pendingExitReasonKind = decision.Reason.Contains(
                "Opposite trend",
                StringComparison.OrdinalIgnoreCase)
                ? SimulatedTradeExitReason.ReverseStrategyClose
                : SimulatedTradeExitReason.StructuralInvalidation;
            AddFrameEvent(
                StrategyReplayEventType.StrategyCloseRequested,
                decision.CreatedAt,
                _activeTrade?.SetupId,
                _activeTrade?.PositionId,
                reason: decision.Reason);
        }
    }

    private void CapturePositionTransition(
        Candle candle,
        BrokerPosition? before,
        BrokerPosition? after)
    {
        DateTimeOffset timestamp = candle.CloseTime ?? candle.OpenTime;
        if (before is null && after is not null && _pendingEntryDecision is not null)
        {
            AgentDecision decision = _pendingEntryDecision;
            decimal conversion = Broker.TryGetQuoteToAccountCurrencyRate(
                decision.Instrument,
                out decimal resolvedConversion)
                ? resolvedConversion
                : 0m;
            // Cost-inclusive, matching the distance PositionSizer already sized this quantity
            // against (see _estimatedRoundTripCostBasisPoints above) - otherwise a clean stop
            // fill that costs exactly what sizing already budgeted for still reports as worse
            // than -1.0R, even though the real dollar risk was never exceeded.
            decimal? plannedStopRisk = after.AveragePrice is decimal filledEntry &&
                decision.StopLossPrice is decimal plannedStop && conversion > 0m
                ? (Math.Abs(filledEntry - plannedStop) +
                    filledEntry * _estimatedRoundTripCostBasisPoints / 10_000m) * after.Quantity * conversion
                : null;
            decimal finalRiskMultiplier = CombinedRiskMultiplier(decision);
            decimal entryCommission = -Broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    (_pendingEntryBrokerOrderId is null || entry.OrderId == _pendingEntryBrokerOrderId) &&
                    entry.Timestamp == timestamp)
                .Sum(entry => entry.Amount);
            _activeTrade = new SimulatedTradeRecord
            {
                StrategyId = StrategyId,
                PlaybookId = decision.PlaybookId ?? "unknown",
                StrategyName = decision.StrategyName ?? Strategy.Name,
                SetupId = decision.SetupId ?? decision.DecisionId ?? $"setup:{timestamp:O}",
                PositionId = after.PositionId,
                Instrument = decision.Instrument,
                Side = after.Side,
                SetupStartedAt = decision.SetupStartedAt ?? decision.CreatedAt,
                ConfirmationAt = decision.ConfirmationAt,
                SignalCreatedAt = decision.CreatedAt,
                OpenedAt = timestamp,
                SignalPrice = decision.ReferencePrice,
                EntryPrice = after.AveragePrice,
                Quantity = after.Quantity,
                InitialQuantity = after.Quantity,
                RemainingQuantity = after.Quantity,
                StopLossPrice = decision.StopLossPrice,
                InitialStopLossPrice = decision.StopLossPrice,
                CurrentStopLossPrice = decision.StopLossPrice,
                TakeProfitPrice = decision.TakeProfitPrice,
                ExpectedRewardRisk = decision.ExpectedRewardRisk,
                EntryCommission = entryCommission,
                Commission = entryCommission,
                NetProfitLoss = -entryCommission,
                StopSource = decision.StopSource,
                TargetSource = decision.TargetSource,
                ExitPolicy = decision.ExitPolicy,
                TargetPlan = decision.TargetPlan,
                SetupReason = decision.Reason,
                ExitReason = SimulatedTradeExitReason.Unknown,
                EntryRegime = decision.RegimeLabel ?? ChartAnnotator.Regime.MarketRegime.Unknown,
                CurrentRegime = decision.RegimeLabel ?? ChartAnnotator.Regime.MarketRegime.Unknown,
                EntryManagementProfileId = decision.RegimeManagementProfileId ?? "default",
                CurrentManagementProfileId = decision.RegimeManagementProfileId ?? "default",
                EntryConfidence = decision.Confidence,
                EntrySetupType = decision.PriceActionSetupType?.ToString() ?? decision.ReasonCode ?? "Unknown",
                EntryPlaybookId = decision.PlaybookId,
                EntrySession = SessionName(timestamp),
                EntryVolatilityBucket = VolatilityBucket(decision.AtrPercentile),
                EntryRegimeConfidence = decision.RegimeConfidence,
                BaseRequestedQuantity = decision.PortfolioOriginalQuantity ?? decision.SuggestedQuantity,
                AllocatedQuantity = decision.PortfolioAllocatedQuantity ?? after.Quantity,
                PlannedStopRiskAccountCurrency = plannedStopRisk,
                PortfolioReservationId = decision.PortfolioReservationId,
                CorrelationClusterId = decision.RiskClusterId,
                RegimeRiskMultiplier = decision.RegimeRiskMultiplier,
                TradingConditionRiskMultiplier = decision.TradingConditionRiskMultiplier,
                CorrelationRiskMultiplier = decision.CorrelationRiskMultiplier,
                StrategyAllocationRiskMultiplier = decision.StrategyAllocationRiskMultiplier,
                EquityProtectionRiskMultiplier = decision.EquityProtectionRiskMultiplier,
                SetupCalibrationRiskMultiplier = decision.SetupCalibrationRiskMultiplier,
                MetaLabelRiskMultiplier = decision.MetaLabelRiskMultiplier,
                NeoWaveRiskMultiplier = decision.NeoWaveRiskMultiplier,
                StructuralEvidenceRiskMultiplier = decision.StructuralEvidenceRiskMultiplier,
                EntrySupplyDemandZoneId = decision.EntrySupplyDemandZoneId,
                EntrySupplyDemandZoneLowerPrice = decision.EntrySupplyDemandZoneLowerPrice,
                EntrySupplyDemandZoneUpperPrice = decision.EntrySupplyDemandZoneUpperPrice,
                EntrySupplyDemandZoneState = decision.EntrySupplyDemandZoneState?.ToString(),
                EntrySupplyDemandProfileHash = decision.EntrySupplyDemandProfileHash,
                TargetLiquidityPoolId = decision.TargetLiquidityPoolId,
                TargetLiquidityProfileHash = decision.TargetLiquidityProfileHash,
                StructuralInvalidationReference = decision.StructuralInvalidationReference,
                EntrySupplyDemandManagementEnabled = decision.EntrySupplyDemandManagementEnabled,
                EntryLiquidityManagementEnabled = decision.EntryLiquidityManagementEnabled,
                StructuralManagementPolicyRevision = decision.StructuralManagementPolicyRevision,
                EntryNeoWaveHypothesisId = decision.NeoWaveHypothesisId,
                EntryNeoWaveInvalidationPrice = decision.NeoWaveInvalidationPrice,
                EntryNeoWavePatternType = decision.NeoWavePatternType?.ToString(),
                EntryNeoWaveStructuralScore = decision.NeoWaveStructuralScore,
                EntryNeoWaveConflictScore = decision.NeoWaveConflictScore,
                FinalRiskBudgetMultiplier = finalRiskMultiplier,
                EntryMultiTimeframeAlignment = _pendingEntryMultiTimeframeAlignment
            };
            if (_detailedExcursionTracking)
            {
                _excursionPath = [];
                _excursionPathBars = 0;
            }
            _pendingEntryDecision = null;
            _pendingEntryMultiTimeframeAlignment = null;
            _pendingEntryBrokerOrderId = null;
            _lastAmendmentSnapshotVersion = null;
            _lastReductionSnapshotVersion = null;
            _lastManagementEvaluationVersions.Clear();
            _analysisBarsSinceLastAmendment = int.MaxValue;
            _analysisBarsSinceLastReduction = int.MaxValue;
            _analysisBarsWithoutNewMfe = 0;
            _lastObservedManagementMfeR = 0m;
            _stagnationReductionCompleted = false;
            _structuralDeteriorationReductionCount = 0;
            _momentumDecayReductionCount = 0;
            _volatilityExhaustionReductionCount = 0;
            _regimeDegradationReductionCount = 0;
            _volatilityExpansionSeenSinceEntry = false;
            _riskWindowReductionCompleted = false;
            _executionCostStressReductionCompleted = false;
            _pendingPartialExit = null;
            _completedReductionStages.Clear();
            AddFrameEvent(
                StrategyReplayEventType.OrderFilled,
                timestamp,
                _activeTrade.SetupId,
                after.PositionId,
                reason: "Entry order filled.");
            AddFrameEvent(
                StrategyReplayEventType.PositionOpened,
                timestamp,
                _activeTrade.SetupId,
                after.PositionId,
                reason: "Position opened with its initial protective stop.");
            return;
        }

        string? capturedPartialOrderId = null;
        if (before is not null && _activeTrade is not null && _pendingPartialExit is not null)
        {
            TryCapturePendingPartialExit(
                timestamp,
                after,
                out capturedPartialOrderId);
        }

        if (before is not null && after is not null && _activeTrade is not null)
        {
            _activeTrade = _activeTrade with { RemainingQuantity = after.Quantity };
            return;
        }

        if (before is not null && after is null && _activeTrade is not null)
        {
            LedgerEntry? realised = Broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.RealisedProfitLoss &&
                    entry.Timestamp == timestamp &&
                    (capturedPartialOrderId is null || entry.OrderId != capturedPartialOrderId))
                .OrderByDescending(entry => entry.Sequence)
                .FirstOrDefault();
            decimal finalGross = realised?.Amount ?? 0m;
            string? exitOrderId = realised?.OrderId;
            decimal exitCommission = -Broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    entry.Timestamp == timestamp &&
                    (exitOrderId is null || entry.OrderId == exitOrderId))
                .Sum(entry => entry.Amount);
            decimal entryPrice = _activeTrade.EntryPrice ?? before.AveragePrice ?? candle.Prices.Open;
            decimal finalQuantity = _activeTrade.RemainingQuantity > 0m
                ? _activeTrade.RemainingQuantity
                : before.Quantity;
            decimal initialQuantity = _activeTrade.InitialQuantity > 0m
                ? _activeTrade.InitialQuantity
                : _activeTrade.Quantity;
            decimal quoteToBaseRate = Broker.State.GetQuoteToBaseCurrencyRate(before.Instrument);
            decimal quoteProfitLoss = finalGross / quoteToBaseRate;
            decimal exitPrice = before.Side == OrderSide.Buy
                ? entryPrice + quoteProfitLoss / Math.Max(finalQuantity, 0.00000001m)
                : entryPrice - quoteProfitLoss / Math.Max(finalQuantity, 0.00000001m);
            BrokerOrder? exitOrder = exitOrderId is null ? null : Broker.State.GetOrder(exitOrderId);
            SimulatedTradeExitReason exitReason = DetermineExitReason(
                candle,
                _activeTrade,
                _pendingExitReason,
                _pendingExitReasonKind,
                exitOrder);
            decimal grossTotal = _activeTrade.RealizedPartialGrossProfitLoss + finalGross;
            decimal totalCommission = _activeTrade.Commission + exitCommission;
            decimal netTotal = grossTotal - totalCommission + _activeTrade.TotalFinancing;
            // Cost-inclusive: matches what PositionSizer already sized this quantity against (see
            // _estimatedRoundTripCostBasisPoints), so RMultiple/RealizedR reflect the risk this
            // trade was actually planned against, not just the raw stop distance.
            decimal? initialRisk = (_activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice) is decimal stop
                ? (Math.Abs(entryPrice - stop) + entryPrice * _estimatedRoundTripCostBasisPoints / 10_000m) *
                    initialQuantity * quoteToBaseRate
                : null;
            decimal exitedNotionalPrice = _activeTrade.PartialExits.Sum(partial =>
                partial.ExitPrice * partial.QuantityClosed) + exitPrice * finalQuantity;
            decimal averageExitPrice = initialQuantity > 0m
                ? exitedNotionalPrice / initialQuantity
                : exitPrice;
            SimulatedTradeRecord closedTrade = _activeTrade with
            {
                ClosedAt = timestamp,
                ExitPrice = exitPrice,
                AverageExitPrice = averageExitPrice,
                RemainingQuantity = 0m,
                GrossProfitLoss = grossTotal,
                Commission = totalCommission,
                NetProfitLoss = netTotal,
                NetProfitAfterFinancing = netTotal,
                RMultiple = initialRisk is > 0m ? netTotal / initialRisk.Value : null,
                ExitReason = exitReason,
                ExitReasonText = _pendingExitReason ?? exitReason.ToString(),
                FinalStopLossPrice = _activeTrade.CurrentStopLossPrice ?? _activeTrade.InitialStopLossPrice,
                ExcursionPath = _excursionPath?.ToArray(),
                // Adaptive target management reporting (plan §3.7/§5.5): only populated for a v2
                // managed trade (ExitPolicy/TargetPlan set at entry - see the copy into
                // SimulatedTradeRecord above). PlannedR is the plan's own weighted forecast;
                // RealizedR is the same net-P&L-over-initial-risk math as the legacy RMultiple
                // above, exposed under the plan's field name for v2-aware consumers.
                PlannedR = _activeTrade.TargetPlan?.PlannedR,
                RealizedR = _activeTrade.ExitPolicy is not null && initialRisk is > 0m
                    ? netTotal / initialRisk.Value
                    : null,
                InitialRiskCash = _activeTrade.ExitPolicy is not null ? initialRisk : null
            };
            _trades.Add(closedTrade);
            RecordCompletedTradeForSafety(closedTrade, timestamp);
            StrategyReplayEventType exitEvent = exitReason switch
            {
                SimulatedTradeExitReason.TakeProfit => StrategyReplayEventType.TargetHit,
                SimulatedTradeExitReason.InitialStopLoss or
                    SimulatedTradeExitReason.BreakEvenStop or
                    SimulatedTradeExitReason.TrailedStructureStop or
                    SimulatedTradeExitReason.ProfitFloorStop or
                    SimulatedTradeExitReason.MfeGivebackStop or
                    SimulatedTradeExitReason.StopLoss => StrategyReplayEventType.StopHit,
                _ => StrategyReplayEventType.PositionClosed
            };
            AddFrameEvent(
                exitEvent,
                timestamp,
                closedTrade.SetupId,
                closedTrade.PositionId,
                reason: closedTrade.ExitReasonText);
            AddFrameEvent(
                StrategyReplayEventType.PositionClosed,
                timestamp,
                closedTrade.SetupId,
                closedTrade.PositionId,
                reason: closedTrade.ExitReasonText);
            AddFrameEvent(
                StrategyReplayEventType.TradeCompleted,
                timestamp,
                closedTrade.SetupId,
                closedTrade.PositionId,
                reason: closedTrade.ExitReasonText);
            _activeTrade = null;
            _pendingPartialExit = null;
            _pendingExitReason = null;
            _pendingExitReasonKind = null;
        }
    }

    private bool TryCapturePendingPartialExit(
        DateTimeOffset timestamp,
        BrokerPosition? positionAfter,
        out string? capturedOrderId)
    {
        capturedOrderId = null;
        if (_activeTrade is null || _pendingPartialExit is null)
            return false;

        PendingPartialExit pending = _pendingPartialExit;
        LedgerEntry? realised = Broker.State.GetLedger()
            .Where(entry =>
                entry.Type == LedgerEntryType.RealisedProfitLoss &&
                entry.Timestamp == timestamp &&
                entry.OrderId == pending.BrokerOrderId)
            .OrderByDescending(entry => entry.Sequence)
            .FirstOrDefault();
        if (realised is null)
        {
            BrokerOrder? pendingOrder = Broker.State.GetOrder(pending.BrokerOrderId);
            if (pendingOrder is not null && pendingOrder.NormalizedStatus is
                OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Expired)
            {
                _rejectedPositionReductions++;
                AddFrameEvent(
                    StrategyReplayEventType.PartialExitRejected,
                    timestamp,
                    _activeTrade.SetupId,
                    _activeTrade.PositionId,
                    reason: $"The partial-close order ended with status {pendingOrder.NormalizedStatus}.",
                    reasonCode: "PartialExitNotFilled",
                    quantityBefore: _activeTrade.RemainingQuantity,
                    quantityChanged: pending.Recommendation.QuantityToClose,
                    quantityRemaining: _activeTrade.RemainingQuantity,
                    reductionStageId: pending.Recommendation.StageId,
                    positionReductionReason: pending.Recommendation.Reason);
                _pendingPartialExit = null;
            }
            return false;
        }

        decimal quantityBefore = _activeTrade.RemainingQuantity > 0m
            ? _activeTrade.RemainingQuantity
            : _activeTrade.InitialQuantity;
        decimal quantityClosed = Math.Min(pending.Recommendation.QuantityToClose, quantityBefore);
        if (quantityClosed <= 0m)
            return false;

        decimal quantityRemaining = positionAfter?.Quantity ?? Math.Max(0m, quantityBefore - quantityClosed);
        decimal exitCommission = -Broker.State.GetLedger()
            .Where(entry =>
                entry.Type == LedgerEntryType.Commission &&
                entry.Timestamp == timestamp &&
                entry.OrderId == pending.BrokerOrderId)
            .Sum(entry => entry.Amount);
        decimal quoteToBaseRate = Broker.State.GetQuoteToBaseCurrencyRate(_activeTrade.Instrument);
        decimal quoteProfitLoss = realised.Amount / quoteToBaseRate;
        decimal entryPrice = _activeTrade.EntryPrice!.Value;
        decimal exitPrice = _activeTrade.Side == OrderSide.Buy
            ? entryPrice + quoteProfitLoss / quantityClosed
            : entryPrice - quoteProfitLoss / quantityClosed;
        decimal initialQuantity = _activeTrade.InitialQuantity > 0m
            ? _activeTrade.InitialQuantity
            : _activeTrade.Quantity;
        decimal allocatedEntryCommission = initialQuantity > 0m
            ? _activeTrade.EntryCommission * quantityClosed / initialQuantity
            : 0m;
        decimal net = realised.Amount - exitCommission - allocatedEntryCommission;
        decimal initialRiskAmount = Math.Abs(
                entryPrice - (_activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice)!.Value) *
            initialQuantity * quoteToBaseRate;
        decimal realizedR = initialRiskAmount > 0m ? net / initialRiskAmount : 0m;
        var partial = new PartialExitRecord
        {
            ExitId = $"{StrategyId}:{_activeTrade.SetupId}:{pending.Recommendation.StageId}:{timestamp:O}",
            StageId = pending.Recommendation.StageId,
            RequestedSequence = pending.RequestedSequence,
            ExecutionSequence = _currentFrameSequence,
            RequestedAt = pending.RequestedAt,
            ExecutedAt = timestamp,
            QuantityBefore = quantityBefore,
            QuantityClosed = quantityClosed,
            QuantityRemaining = quantityRemaining,
            ExitPrice = exitPrice,
            GrossProfitLoss = realised.Amount,
            AllocatedEntryCommission = allocatedEntryCommission,
            ExitCommission = exitCommission,
            NetProfitLoss = net,
            RealizedR = realizedR,
            OpenProfitRBeforeExit = pending.OpenProfitR,
            Reason = MapPartialExitReason(pending.Recommendation.Reason),
            StructureSource = pending.Recommendation.StructureSource,
            StructuralLevel = pending.Recommendation.StructuralLevel,
            Explanation = pending.Recommendation.Explanation,
            BrokerOrderId = pending.BrokerOrderId
        };

        _completedReductionStages.Add(pending.Recommendation.StageId);
        bool runnerActivated = quantityRemaining <=
            initialQuantity * _positionManagementOptions.MinimumRunnerFraction + 0.00000001m;
        _activeTrade = _activeTrade with
        {
            RemainingQuantity = quantityRemaining,
            GrossProfitLoss = _activeTrade.RealizedPartialGrossProfitLoss + realised.Amount,
            Commission = _activeTrade.Commission + exitCommission,
            NetProfitLoss = (_activeTrade.RealizedPartialGrossProfitLoss + realised.Amount) -
                (_activeTrade.Commission + exitCommission),
            RealizedPartialGrossProfitLoss = _activeTrade.RealizedPartialGrossProfitLoss + realised.Amount,
            RealizedPartialCommission = _activeTrade.RealizedPartialCommission + exitCommission,
            RealizedPartialNetProfitLoss = _activeTrade.RealizedPartialNetProfitLoss + net,
            PositionReductionCount = _activeTrade.PositionReductionCount + 1,
            RunnerActivatedAt = runnerActivated
                ? _activeTrade.RunnerActivatedAt ?? timestamp
                : _activeTrade.RunnerActivatedAt,
            CompletedReductionStageIds = _completedReductionStages.OrderBy(value => value).ToArray(),
            PartialExits = _activeTrade.PartialExits.Append(partial).ToArray()
        };
        _acceptedPositionReductions++;
        _lastReductionSnapshotVersion = pending.SnapshotVersion;
        _analysisBarsSinceLastReduction = 0;
        if (pending.Recommendation.Reason == PositionReductionReason.Stagnation)
            _stagnationReductionCompleted = true;
        if (pending.Recommendation.Reason == PositionReductionReason.StructuralDeterioration)
            _structuralDeteriorationReductionCount++;
        if (pending.Recommendation.Reason == PositionReductionReason.MomentumDecay)
            _momentumDecayReductionCount++;
        if (pending.Recommendation.Reason == PositionReductionReason.VolatilityExhaustion)
            _volatilityExhaustionReductionCount++;
        if (pending.Recommendation.Reason == PositionReductionReason.RegimeDegradation)
            _regimeDegradationReductionCount++;
        if (pending.Recommendation.Reason == PositionReductionReason.SessionRisk)
            _riskWindowReductionCompleted = true;
        if (pending.Recommendation.Reason == PositionReductionReason.ExecutionCostStress)
            _executionCostStressReductionCompleted = true;

        AddFrameEvent(
            StrategyReplayEventType.PartialExitAccepted,
            timestamp,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: pending.Recommendation.Explanation,
            reasonCode: pending.Recommendation.Reason.ToString(),
            quantityBefore: quantityBefore,
            quantityChanged: quantityClosed,
            quantityRemaining: quantityRemaining,
            realizedProfitLoss: net,
            realizedR: realizedR,
            reductionStageId: pending.Recommendation.StageId);
        AddFrameEvent(
            StrategyReplayEventType.ProtectiveQuantityUpdated,
            timestamp,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: $"Protective orders reconciled to remaining quantity {quantityRemaining}.",
            quantityRemaining: quantityRemaining,
            reductionStageId: pending.Recommendation.StageId);
        if (runnerActivated && _activeTrade.RunnerActivatedAt == timestamp)
        {
            AddFrameEvent(
                StrategyReplayEventType.RunnerActivated,
                timestamp,
                _activeTrade.SetupId,
                _activeTrade.PositionId,
                reason: $"Runner quantity {quantityRemaining} is now protected by dynamic management.",
                quantityRemaining: quantityRemaining);
        }

        capturedOrderId = pending.BrokerOrderId;
        _pendingPartialExit = null;
        return true;
    }

    private void RecordCompletedTradeForSafety(
        SimulatedTradeRecord trade,
        DateTimeOffset timestamp)
    {
        TradingSafetySnapshot snapshot = Safety.RecordClosedTrade(trade.NetProfitLoss, timestamp);
        Journal.Append(new TradeJournalEntry
        {
            Sequence = 0,
            Timestamp = timestamp,
            Type = TradeJournalEventType.ClosedTradeRecorded,
            Value = trade.NetProfitLoss,
            Message = $"Recorded net completed-trade P/L {trade.NetProfitLoss:F2}. Safety state is {snapshot.State}."
        });
    }

    private static PartialExitReason MapPartialExitReason(PositionReductionReason reason) => reason switch
    {
        PositionReductionReason.ScaleOutProfit => PartialExitReason.ScaleOutProfit,
        PositionReductionReason.OpposingStructure => PartialExitReason.OpposingStructure,
        PositionReductionReason.Stagnation => PartialExitReason.Stagnation,
        PositionReductionReason.StructuralDeterioration => PartialExitReason.StructuralDeterioration,
        PositionReductionReason.MomentumDecay => PartialExitReason.MomentumDecay,
        PositionReductionReason.VolatilityExhaustion => PartialExitReason.VolatilityExhaustion,
        PositionReductionReason.SessionRisk => PartialExitReason.SessionRisk,
        PositionReductionReason.ExecutionCostStress => PartialExitReason.ExecutionCostStress,
        PositionReductionReason.RiskReduction => PartialExitReason.RiskReduction,
        PositionReductionReason.RegimeDegradation => PartialExitReason.RegimeDegradation,
        PositionReductionReason.AdaptiveTargetCheckpoint => PartialExitReason.AdaptiveTargetCheckpoint,
        _ => PartialExitReason.Unknown
    };

    private void UpdateExcursions(MarketCandle marketCandle)
    {
        if (_activeTrade?.EntryPrice is not decimal entry || _activeTrade.Quantity <= 0m)
            return;

        Candle executable = _activeTrade.Side == OrderSide.Buy
            ? marketCandle.Bid ?? marketCandle.Mid
            : marketCandle.Ask ?? marketCandle.Mid;
        decimal favourablePrice = _activeTrade.Side == OrderSide.Buy
            ? executable.Prices.High
            : executable.Prices.Low;
        decimal adversePrice = _activeTrade.Side == OrderSide.Buy
            ? executable.Prices.Low
            : executable.Prices.High;
        decimal direction = _activeTrade.Side == OrderSide.Buy ? 1m : -1m;
        decimal quoteToBase = Broker.State.GetQuoteToBaseCurrencyRate(_activeTrade.Instrument);
        decimal favourableAmount =
            (favourablePrice - entry) * direction * _activeTrade.Quantity * quoteToBase;
        decimal adverseAmount =
            (adversePrice - entry) * direction * _activeTrade.Quantity * quoteToBase;
        // Same cost-inclusive risk unit as the final RMultiple calculation, so MFE_R/MAE_R stay
        // consistent with it rather than reporting excursions in a different "R" than the trade's
        // own realized R-multiple.
        decimal? initialRisk = (_activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice) is decimal stop
            ? (Math.Abs(entry - stop) + entry * _estimatedRoundTripCostBasisPoints / 10_000m) *
                _activeTrade.Quantity * quoteToBase
            : null;
        DateTimeOffset timestamp = executable.CloseTime ?? executable.Interval.AddTo(executable.OpenTime);

        SimulatedTradeRecord updated = _activeTrade;
        if (updated.MaximumFavourableExcursionAt is null ||
            favourableAmount > updated.MaximumFavourableExcursionAmount)
        {
            updated = updated with
            {
                MaximumFavourableExcursionPrice = favourablePrice,
                MaximumFavourableExcursionAmount = favourableAmount,
                MaximumFavourableExcursionR = initialRisk is > 0m
                    ? favourableAmount / initialRisk.Value
                    : null,
                MaximumFavourableExcursionAt = timestamp
            };
        }

        if (updated.MaximumAdverseExcursionAt is null ||
            adverseAmount < updated.MaximumAdverseExcursionAmount)
        {
            updated = updated with
            {
                MaximumAdverseExcursionPrice = adversePrice,
                MaximumAdverseExcursionAmount = adverseAmount,
                MaximumAdverseExcursionR = initialRisk is > 0m
                    ? adverseAmount / initialRisk.Value
                    : null,
                MaximumAdverseExcursionAt = timestamp
            };
        }

        if (_detailedExcursionTracking && _excursionPath is not null)
        {
            _excursionPath.Add(new SimulatedTradePathPoint
            {
                BarsAfterEntry = _excursionPathBars++,
                MfeR = updated.MaximumFavourableExcursionR ?? 0m,
                MaeR = updated.MaximumAdverseExcursionR ?? 0m
            });
        }

        _activeTrade = updated;
    }

    private static SimulatedTradeExitReason DetermineExitReason(
        Candle candle,
        SimulatedTradeRecord trade,
        string? strategyReason,
        SimulatedTradeExitReason? strategyReasonKind,
        BrokerOrder? exitOrder)
    {
        if (!string.IsNullOrWhiteSpace(strategyReason))
        {
            return strategyReasonKind ??
                (strategyReason.Contains("End of simulation", StringComparison.OrdinalIgnoreCase)
                    ? SimulatedTradeExitReason.EndOfSimulation
                    : strategyReason.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                        ? SimulatedTradeExitReason.StructuralInvalidation
                        : SimulatedTradeExitReason.StrategyClose);
        }

        if (string.Equals(exitOrder?.Type, StandardOrderType.Stop.ToString(), StringComparison.Ordinal))
        {
            StopAmendmentRecord? accepted = trade.StopAmendments.LastOrDefault(amendment =>
                amendment.Status is ProtectiveStopAmendmentStatus.Accepted or
                    ProtectiveStopAmendmentStatus.Replaced);
            return accepted?.Reason switch
            {
                StopAmendmentReason.BreakEven => SimulatedTradeExitReason.BreakEvenStop,
                StopAmendmentReason.StructureSwing or StopAmendmentReason.StructureZone or
                    StopAmendmentReason.StructureChannel or StopAmendmentReason.AtrFallback =>
                    SimulatedTradeExitReason.TrailedStructureStop,
                StopAmendmentReason.ProfitFloor => SimulatedTradeExitReason.ProfitFloorStop,
                StopAmendmentReason.MfeGiveback => SimulatedTradeExitReason.MfeGivebackStop,
                _ => SimulatedTradeExitReason.InitialStopLoss
            };
        }
        if (string.Equals(exitOrder?.Type, StandardOrderType.Limit.ToString(), StringComparison.Ordinal))
            return SimulatedTradeExitReason.TakeProfit;
        if ((trade.CurrentStopLossPrice ?? trade.InitialStopLossPrice ?? trade.StopLossPrice) is decimal stop &&
            candle.Prices.Low <= stop && candle.Prices.High >= stop)
        {
            StopAmendmentRecord? accepted = trade.StopAmendments.LastOrDefault(amendment =>
                amendment.Status is ProtectiveStopAmendmentStatus.Accepted or
                    ProtectiveStopAmendmentStatus.Replaced);
            return accepted?.Reason switch
            {
                StopAmendmentReason.BreakEven => SimulatedTradeExitReason.BreakEvenStop,
                StopAmendmentReason.ProfitFloor => SimulatedTradeExitReason.ProfitFloorStop,
                StopAmendmentReason.MfeGiveback => SimulatedTradeExitReason.MfeGivebackStop,
                StopAmendmentReason.StructureSwing or StopAmendmentReason.StructureZone or
                    StopAmendmentReason.StructureChannel or StopAmendmentReason.AtrFallback =>
                    SimulatedTradeExitReason.TrailedStructureStop,
                _ => SimulatedTradeExitReason.InitialStopLoss
            };
        }
        if (trade.TakeProfitPrice is decimal target && candle.Prices.Low <= target && candle.Prices.High >= target)
            return SimulatedTradeExitReason.TakeProfit;
        return SimulatedTradeExitReason.Unknown;
    }

    private void RecordNewClosedTrades()
    {
        // Realised P/L entries can represent partial reductions. Safety accounting is
        // updated exactly once when the complete trade closes in CapturePositionTransition.
        // Here we only advance the ledger cursor so partial fills are not misclassified as
        // separate closed trades or consecutive wins/losses.
        LedgerEntry[] newLedger = Broker.State.GetLedger()
            .Where(entry => entry.Sequence > _lastProcessedLedgerSequence)
            .OrderBy(entry => entry.Sequence)
            .ToArray();
        if (_activeTrade is not null)
        {
            decimal financing = newLedger
                .Where(entry => entry.Type == LedgerEntryType.Financing)
                .Sum(entry => entry.Amount);
            if (financing != 0m)
            {
                _activeTrade = _activeTrade with
                {
                    TotalFinancing = _activeTrade.TotalFinancing + financing,
                    NetProfitLoss = _activeTrade.NetProfitLoss + financing,
                    NetProfitAfterFinancing = _activeTrade.NetProfitAfterFinancing + financing
                };
                foreach (LedgerEntry entry in newLedger.Where(item => item.Type == LedgerEntryType.Financing))
                {
                    AddFrameEvent(
                        StrategyReplayEventType.FinancingCharged,
                        entry.Timestamp,
                        _activeTrade.SetupId,
                        _activeTrade.PositionId,
                        reason: entry.Description,
                        reasonCode: "SyntheticFinancingPosted",
                        orderId: entry.OrderId,
                        newValue: entry.Amount,
                        optionOrModelVersion: Broker.Options.Financing.ModelVersion);
                }
            }
        }
        long latestSequence = newLedger
            .Select(entry => entry.Sequence)
            .DefaultIfEmpty(_lastProcessedLedgerSequence)
            .Max();
        _lastProcessedLedgerSequence = Math.Max(_lastProcessedLedgerSequence, latestSequence);
    }

    private void CaptureExecutionEvents()
    {
        foreach ((long sequence, OrderEvent item) in Broker.GetOrderEventsAfter(_lastProcessedOrderEventSequence))
        {
            _lastProcessedOrderEventSequence = sequence;
            if (item.AppliedSpread is decimal spread && spread != 0m)
            {
                AddFrameEvent(
                    StrategyReplayEventType.ExecutionSpreadAdjusted,
                    item.Timestamp,
                    _activeTrade?.SetupId ?? _pendingEntryDecision?.SetupId,
                    _activeTrade?.PositionId,
                    reason: item.Message,
                    reasonCode: "ExecutionSpreadApplied",
                    orderId: item.BrokerOrderId,
                    newValue: spread,
                    optionOrModelVersion: item.ExecutionModelVersion);
            }
            if (item.AppliedSlippage is decimal slippage && slippage != 0m)
            {
                AddFrameEvent(
                    StrategyReplayEventType.ExecutionSlippageApplied,
                    item.Timestamp,
                    _activeTrade?.SetupId ?? _pendingEntryDecision?.SetupId,
                    _activeTrade?.PositionId,
                    reason: item.Message,
                    reasonCode: "AdverseSlippageApplied",
                    orderId: item.BrokerOrderId,
                    newValue: slippage,
                    optionOrModelVersion: item.ExecutionModelVersion);
            }
            if (item.Type == OrderEventType.PartiallyFilled)
            {
                AddFrameEvent(
                    StrategyReplayEventType.PartialFill,
                    item.Timestamp,
                    _activeTrade?.SetupId ?? _pendingEntryDecision?.SetupId,
                    _activeTrade?.PositionId,
                    reason: item.Message,
                    reasonCode: "SyntheticCapacityPartialFill",
                    orderId: item.BrokerOrderId,
                    quantityChanged: item.FillQuantity,
                    quantityRemaining: item.RemainingQuantity,
                    optionOrModelVersion: item.ExecutionModelVersion);
            }
            if (item.Message?.Contains("GapThroughStop", StringComparison.Ordinal) == true)
            {
                AddFrameEvent(
                    StrategyReplayEventType.GapThroughStop,
                    item.Timestamp,
                    _activeTrade?.SetupId,
                    _activeTrade?.PositionId,
                    reason: item.Message,
                    reasonCode: "AdverseGapFill",
                    orderId: item.BrokerOrderId,
                    newValue: item.FillPrice,
                    optionOrModelVersion: item.ExecutionModelVersion);
            }
            if (item.Message?.Contains("OperationFaultInjected", StringComparison.Ordinal) == true)
            {
                AddFrameEvent(
                    StrategyReplayEventType.OperationFaultInjected,
                    item.Timestamp,
                    _activeTrade?.SetupId ?? _pendingEntryDecision?.SetupId,
                    _activeTrade?.PositionId,
                    reason: item.Message,
                    reasonCode: "DeterministicExecutionFault",
                    orderId: item.BrokerOrderId,
                    optionOrModelVersion: item.ExecutionModelVersion);
            }
        }
    }

    private async Task SynchronizeProtectiveStopAsync(
        BrokerPosition position,
        CancellationToken cancellationToken)
    {
        if (_activeTrade is null)
            return;
        IReadOnlyList<BrokerOrder> orders = await Broker.Orders
            .GetOpenOrdersAsync(position.Instrument, cancellationToken)
            .ConfigureAwait(false);
        decimal? expectedStop = _activeTrade.CurrentStopLossPrice ??
            _activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice;
        BrokerOrder? stop = orders
            .Where(order =>
                order.NormalizedStatus is OrderStatus.Open or OrderStatus.Pending or OrderStatus.PartiallyFilled &&
                string.Equals(order.Type, StandardOrderType.Stop.ToString(), StringComparison.OrdinalIgnoreCase) &&
                order.Side != position.Side &&
                order.Quantity == position.Quantity)
            .OrderBy(order => expectedStop is decimal expected && order.Price is decimal actual
                ? Math.Abs(actual - expected)
                : decimal.MaxValue)
            .FirstOrDefault();
        if (stop is null)
            return;

        _activeTrade = _activeTrade with
        {
            InitialStopOrderId = _activeTrade.InitialStopOrderId ?? stop.BrokerOrderId,
            CurrentStopOrderId = stop.BrokerOrderId,
            CurrentStopLossPrice = stop.Price ?? expectedStop
        };
    }

    private async Task<bool> EvaluatePositionManagementAsync(
        MarketFrame frame,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        BarInterval evaluationInterval,
        CancellationToken cancellationToken)
    {
        if (_activeTrade?.EntryPrice is not decimal entryPrice ||
            (_activeTrade.InitialStopLossPrice ?? _activeTrade.StopLossPrice) is not decimal initialStop ||
            _activeTrade.PositionId is null ||
            _lastManagementEvaluationVersions.GetValueOrDefault(scope) == analysis.Version)
        {
            return false;
        }

        BrokerPosition? openPosition = (await Broker.Positions
            .GetOpenPositionsAsync(cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault(item => item.PositionId == _activeTrade.PositionId);
        if (openPosition is null)
            return false;

        _lastManagementEvaluationVersions[scope] = analysis.Version;
        if (scope == TradeManagementEvaluationScope.MainStructure)
        {
            _analysisBarsSinceLastAmendment = IncrementSaturating(_analysisBarsSinceLastAmendment);
            _analysisBarsSinceLastReduction = IncrementSaturating(_analysisBarsSinceLastReduction);
            _nextManagementIntervalClose = _mainStructureInterval.AddTo(analysis.AvailableAt);
        }
        await SynchronizeProtectiveStopAsync(openPosition, cancellationToken).ConfigureAwait(false);

        decimal currentStop = _activeTrade.CurrentStopLossPrice ?? initialStop;
        decimal executablePrice = ResolveExecutablePrice(frame.ExecutionCandle, _activeTrade.Side);
        decimal increment = ResolveMinimumPriceIncrement(_activeTrade.Instrument);
        decimal commissionPrice = entryPrice * Broker.Options.CommissionRate;
        decimal spreadPrice = entryPrice * Broker.Options.SpreadBasisPoints / 10_000m;
        decimal slippagePrice = entryPrice * Broker.Options.SlippageBasisPoints / 10_000m;
        decimal maximumFavourableR = Math.Max(0m, _activeTrade.MaximumFavourableExcursionR ?? 0m);
        if (scope == TradeManagementEvaluationScope.MainStructure &&
            maximumFavourableR >=
            _lastObservedManagementMfeR + _positionManagementOptions.StagnationMinimumMfeAdvanceR)
        {
            _lastObservedManagementMfeR = maximumFavourableR;
            _analysisBarsWithoutNewMfe = 0;
        }
        else if (scope == TradeManagementEvaluationScope.MainStructure)
        {
            _analysisBarsWithoutNewMfe = IncrementSaturating(_analysisBarsWithoutNewMfe);
        }

        if (scope == TradeManagementEvaluationScope.MainStructure &&
            (analysis.Indicators.BollingerAnalysis.IsExpansion ||
            analysis.Indicators.BollingerAnalysis.WidthRegime is
                BollingerWidthRegime.Expansion or BollingerWidthRegime.Wide))
        {
            _volatilityExpansionSeenSinceEntry = true;
        }

        decimal initialQuantity = _activeTrade.InitialQuantity > 0m
            ? _activeTrade.InitialQuantity
            : _activeTrade.Quantity;
        string selectedManagementProfile = _tradeManager is IManagementProfileResolver profileResolver
            ? profileResolver.ResolveProfileId(analysis.MarketRegime.Regime)
            : _activeTrade.CurrentManagementProfileId;
        if (_activeTrade.CurrentRegime != analysis.MarketRegime.Regime ||
            !string.Equals(_activeTrade.CurrentManagementProfileId, selectedManagementProfile, StringComparison.Ordinal))
        {
            _activeTrade = _activeTrade with
            {
                CurrentRegime = analysis.MarketRegime.Regime,
                CurrentManagementProfileId = selectedManagementProfile,
                ManagementProfileSwitchReason =
                    $"Regime changed to {analysis.MarketRegime.Regime} ({analysis.MarketRegime.ReasonCode})."
            };
        }
        _managementEvaluations++;
        TradeManager.EquityProtectionDirective? equityProtectionDirective = null;
        EquityHighWatermarkSnapshot equityProtectionSnapshot = Safety.Snapshot.EquityProtection;
        if (equityProtectionSnapshot.PendingPositionAction is
            RiskManager.Safety.EquityProtectionAction pendingAction &&
            equityProtectionSnapshot.PendingPositionTierId is string pendingTierId)
        {
            equityProtectionDirective = new TradeManager.EquityProtectionDirective
            {
                TierId = pendingTierId,
                Action = pendingAction == RiskManager.Safety.EquityProtectionAction.FlattenAllPositions
                    ? TradeManager.EquityProtectionPositionAction.FlattenAllPositions
                    : TradeManager.EquityProtectionPositionAction.ReduceOpenPositions,
                ReductionFraction = equityProtectionSnapshot.PendingPositionReductionFraction
            };
        }
        equityProtectionDirective = MergeEquityProtectionDirectives(
            equityProtectionDirective,
            _sharedEquityProtectionDirective);

        TradeManagementRecommendation recommendation = _tradeManager.Evaluate(
            new ManagedTradeState
            {
                StrategyId = _activeTrade.StrategyId,
                InstrumentGroup = InstrumentGroup(_activeTrade.Instrument),
                SetupType = _activeTrade.EntrySetupType,
                PlaybookId = _activeTrade.EntryPlaybookId,
                EntrySession = _activeTrade.EntrySession,
                EntryVolatilityBucket = _activeTrade.EntryVolatilityBucket,
                EntryConfidence = _activeTrade.EntryConfidence,
                Instrument = _activeTrade.Instrument,
                Side = _activeTrade.Side,
                EntryPrice = entryPrice,
                InitialStopPrice = initialStop,
                CurrentStopPrice = currentStop,
                CurrentPrice = executablePrice,
                TakeProfitPrice = _activeTrade.TakeProfitPrice ?? 0m,
                ExitPolicy = _activeTrade.ExitPolicy,
                TargetPlan = _activeTrade.TargetPlan,
                InitialQuantity = initialQuantity,
                CurrentQuantity = openPosition.Quantity,
                MinimumQuantityIncrement = ResolveMinimumQuantityIncrement(_activeTrade.Instrument),
                MaximumFavourableExcursionR = maximumFavourableR,
                CompletedReductionStageIds = _completedReductionStages,
                HasPendingReduction = _pendingPartialExit is not null,
                LastReductionSnapshotVersion = null,
                AnalysisBarsSinceLastReduction = _analysisBarsSinceLastReduction,
                AnalysisBarsWithoutNewMfe = _analysisBarsWithoutNewMfe,
                StagnationReductionCompleted = _stagnationReductionCompleted,
                StructuralDeteriorationReductionCount = _structuralDeteriorationReductionCount,
                MomentumDecayReductionCount = _momentumDecayReductionCount,
                VolatilityExhaustionReductionCount = _volatilityExhaustionReductionCount,
                RegimeDegradationReductionCount = _regimeDegradationReductionCount,
                VolatilityExpansionSeenSinceEntry = _volatilityExpansionSeenSinceEntry,
                RiskWindowReductionCompleted = _riskWindowReductionCompleted,
                ExecutionCostStressReductionCompleted = _executionCostStressReductionCompleted,
                EvaluatedAt = frame.AvailableAt,
                MinimumPriceIncrement = increment,
                EntryCommissionPrice = commissionPrice,
                ExpectedExitCommissionPrice = commissionPrice,
                SpreadPrice = spreadPrice,
                SlippagePrice = slippagePrice,
                LastAmendmentSnapshotVersion = null,
                AnalysisBarsSinceLastAmendment = scope == TradeManagementEvaluationScope.Mechanical
                    ? int.MaxValue
                    : _analysisBarsSinceLastAmendment,
                EntryRegime = _activeTrade.EntryRegime,
                EntryManagementProfileId = _activeTrade.EntryManagementProfileId,
                EntryNeoWaveHypothesisId = _activeTrade.EntryNeoWaveHypothesisId,
                EntryNeoWaveInvalidationPrice = _activeTrade.EntryNeoWaveInvalidationPrice,
                EntrySupplyDemandZoneId = _activeTrade.EntrySupplyDemandZoneId,
                TargetLiquidityPoolId = _activeTrade.TargetLiquidityPoolId,
                EntrySupplyDemandManagementEnabled = _activeTrade.EntrySupplyDemandManagementEnabled,
                EntryLiquidityManagementEnabled = _activeTrade.EntryLiquidityManagementEnabled,
                StructuralManagementPolicyRevision = _activeTrade.StructuralManagementPolicyRevision
            },
            analysis,
            scope,
            equityProtectionDirective);

        _lastManagementAction = recommendation.Action.ToString();
        _lastManagementReason = recommendation.Reason;
        AddFrameEvent(
            StrategyReplayEventType.TradeManagementEvaluated,
            frame.AvailableAt,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: $"[{scope} @ {BarIntervalParser.Format(evaluationInterval)}] {recommendation.Reason}",
            reasonCode: $"{scope}:{recommendation.ReasonCode}",
            quantityBefore: openPosition.Quantity,
            quantityChanged: recommendation.PositionReduction?.QuantityToClose,
            quantityRemaining: recommendation.PositionReduction?.QuantityRemainingAfterReduction,
            reductionStageId: recommendation.PositionReduction?.StageId,
            positionReductionReason: recommendation.PositionReduction?.Reason,
            profitFloorR: recommendation.ProfitFloorR,
            maximumGivebackFloorR: recommendation.MaximumGivebackFloorR);

        if (_pendingExitReason is not null)
            return true;

        if (recommendation.Action == TradeManagementAction.Exit)
        {
            await SubmitTradeManagerExitAsync(
                    frame,
                    analysis,
                    openPosition,
                    executablePrice,
                    recommendation,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (recommendation.PositionReduction is not null)
        {
            await SubmitPositionReductionAsync(
                    frame,
                    analysis,
                    openPosition,
                    executablePrice,
                    recommendation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (recommendation.ProposedStopPrice is decimal proposedStop)
        {
            // The amendment is applied to the current full position. If a partial reduction was
            // also submitted, both become eligible on the next execution candle; the simulated
            // broker executes the market reduction first and atomically reconciles the amended
            // stop/target quantities to the remaining position.
            await ApplyStopRecommendationAsync(
                    frame,
                    analysis,
                    openPosition,
                    entryPrice,
                    initialStop,
                    currentStop,
                    executablePrice,
                    increment,
                    proposedStop,
                    recommendation,
                    evaluationInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return recommendation.Action != TradeManagementAction.Hold;
    }

    private async Task<bool> EvaluateClosedManagementLayerAsync(
        MarketFrame frame,
        BarInterval interval,
        TradeManagementEvaluationScope scope,
        CancellationToken cancellationToken)
    {
        if (!frame.ClosedIntervals.Contains(interval) ||
            !ResolveSnapshots(frame).TryGetValue(interval, out AnalysisSnapshot? snapshot) ||
            snapshot.AvailableAt > frame.AvailableAt)
        {
            return false;
        }

        return await EvaluatePositionManagementAsync(
                frame,
                snapshot,
                scope,
                interval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private AnalysisSnapshot? ResolveMechanicalSnapshot(MarketFrame frame)
    {
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots = ResolveSnapshots(frame);
        if (snapshots.TryGetValue(Strategy.TriggerInterval, out AnalysisSnapshot? trigger) &&
            trigger.AvailableAt <= frame.AvailableAt)
        {
            return trigger;
        }
        if (snapshots.TryGetValue(_fastStructureInterval, out AnalysisSnapshot? fast) &&
            fast.AvailableAt <= frame.AvailableAt)
        {
            return fast;
        }
        return snapshots.Values
            .Where(snapshot => snapshot.AvailableAt <= frame.AvailableAt)
            .OrderByDescending(snapshot => snapshot.AvailableAt)
            .ThenBy(snapshot => BarIntervalParser.ApproximateSeconds(snapshot.Interval))
            .FirstOrDefault();
    }

    private async Task SubmitTradeManagerExitAsync(
        MarketFrame frame,
        AnalysisSnapshot analysis,
        BrokerPosition position,
        decimal executablePrice,
        TradeManagementRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        var close = new AgentDecision
        {
            DecisionId = $"{StrategyId}:{_activeTrade!.SetupId}:trade-manager-close:{analysis.Version}",
            SetupId = _activeTrade.SetupId,
            StrategyName = StrategyName,
            Action = AgentAction.Close,
            Instrument = _activeTrade.Instrument,
            SuggestedQuantity = position.Quantity,
            QuantityUnit = QuantityUnit.Units,
            ReferencePrice = executablePrice,
            Confidence = 100m,
            CreatedAt = frame.AvailableAt,
            Reason = recommendation.Reason,
            ReasonCode = recommendation.ReasonCode
        };
        OrderSubmission? submission = await Execution
            .ProcessAsync(close, Broker, cancellationToken)
            .ConfigureAwait(false);
        if (submission is null || submission.Status == SubmissionStatus.Rejected)
            return;

        _pendingExitReason = recommendation.Reason;
        _pendingExitReasonKind = recommendation.ExitReason switch
        {
            TradeManagementExitReason.AdverseStructure =>
                SimulatedTradeExitReason.TradeManagerStructureExit,
            TradeManagementExitReason.NeoWaveInvalidation =>
                SimulatedTradeExitReason.NeoWaveInvalidationExit,
            TradeManagementExitReason.ProfitFloorBreached =>
                SimulatedTradeExitReason.ProfitFloorExit,
            TradeManagementExitReason.MaximumGivebackBreached =>
                SimulatedTradeExitReason.MaximumGivebackExit,
            _ => SimulatedTradeExitReason.StrategyClose
        };
        AddFrameEvent(
            StrategyReplayEventType.StrategyCloseRequested,
            frame.AvailableAt,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: recommendation.Reason,
            reasonCode: recommendation.ReasonCode,
            profitFloorR: recommendation.ProfitFloorR,
            maximumGivebackFloorR: recommendation.MaximumGivebackFloorR);
    }

    private async Task SubmitPositionReductionAsync(
        MarketFrame frame,
        AnalysisSnapshot analysis,
        BrokerPosition position,
        decimal executablePrice,
        TradeManagementRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        if (_activeTrade is null || _pendingPartialExit is not null ||
            recommendation.PositionReduction is not PositionReductionRecommendation reduction)
        {
            return;
        }

        decimal quantity = Math.Min(reduction.QuantityToClose, position.Quantity);
        if (quantity <= 0m || quantity >= position.Quantity)
        {
            _rejectedPositionReductions++;
            AddFrameEvent(
                StrategyReplayEventType.PartialExitRejected,
                frame.AvailableAt,
                _activeTrade.SetupId,
                _activeTrade.PositionId,
                reason: "The recommended reduction would close no quantity or the complete position.",
                reasonCode: "InvalidReductionQuantity",
                quantityBefore: position.Quantity,
                quantityChanged: quantity,
                quantityRemaining: position.Quantity,
                reductionStageId: reduction.StageId,
                positionReductionReason: reduction.Reason);
            return;
        }

        AddFrameEvent(
            StrategyReplayEventType.PartialExitRecommended,
            frame.AvailableAt,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: reduction.Explanation,
            reasonCode: reduction.Reason.ToString(),
            quantityBefore: position.Quantity,
            quantityChanged: quantity,
            quantityRemaining: position.Quantity - quantity,
            reductionStageId: reduction.StageId,
            positionReductionReason: reduction.Reason);

        var close = new AgentDecision
        {
            DecisionId = $"{StrategyId}:{_activeTrade.SetupId}:partial:{reduction.StageId}:{analysis.Version}",
            SetupId = _activeTrade.SetupId,
            StrategyName = StrategyName,
            Action = AgentAction.Close,
            Instrument = _activeTrade.Instrument,
            SuggestedQuantity = quantity,
            QuantityUnit = QuantityUnit.Units,
            ReferencePrice = executablePrice,
            Confidence = 100m,
            CreatedAt = frame.AvailableAt,
            Reason = reduction.Explanation,
            ReasonCode = reduction.Reason.ToString()
        };
        _positionReductionRequests++;
        OrderSubmission? submission = await Execution
            .ProcessAsync(close, Broker, cancellationToken)
            .ConfigureAwait(false);
        if (submission is null || submission.Status == SubmissionStatus.Rejected ||
            string.IsNullOrWhiteSpace(submission.BrokerOrderId))
        {
            _rejectedPositionReductions++;
            AddFrameEvent(
                StrategyReplayEventType.PartialExitRejected,
                frame.AvailableAt,
                _activeTrade.SetupId,
                _activeTrade.PositionId,
                reason: submission?.RejectionReason ?? "The partial-close order was not accepted.",
                reasonCode: "PartialExitSubmissionRejected",
                quantityBefore: position.Quantity,
                quantityChanged: quantity,
                quantityRemaining: position.Quantity,
                reductionStageId: reduction.StageId,
                positionReductionReason: reduction.Reason);
            return;
        }

        _pendingPartialExit = new PendingPartialExit(
            submission.BrokerOrderId,
            reduction,
            analysis.Version,
            frame.Sequence,
            frame.AvailableAt,
            recommendation.OpenProfitR);
        AddFrameEvent(
            StrategyReplayEventType.PartialExitSubmitted,
            frame.AvailableAt,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: reduction.Explanation,
            reasonCode: reduction.Reason.ToString(),
            quantityBefore: position.Quantity,
            quantityChanged: quantity,
            quantityRemaining: position.Quantity - quantity,
            reductionStageId: reduction.StageId,
            positionReductionReason: reduction.Reason);
    }

    private async Task ApplyStopRecommendationAsync(
        MarketFrame frame,
        AnalysisSnapshot analysis,
        BrokerPosition openPosition,
        decimal entryPrice,
        decimal initialStop,
        decimal currentStop,
        decimal executablePrice,
        decimal increment,
        decimal proposedStop,
        TradeManagementRecommendation recommendation,
        BarInterval evaluationInterval,
        CancellationToken cancellationToken)
    {
        if (_activeTrade is null || _pendingExitReason is not null)
            return;

        var command = new ProtectiveStopAmendmentCommand
        {
            Instrument = _activeTrade.Instrument,
            StrategyId = StrategyId,
            SetupId = _activeTrade.SetupId,
            PositionId = openPosition.PositionId,
            ExistingStopOrderId = _activeTrade.CurrentStopOrderId,
            PositionSide = openPosition.Side,
            PositionQuantity = openPosition.Quantity,
            EntryPrice = entryPrice,
            InitialStopPrice = initialStop,
            CurrentStopPrice = currentStop,
            ProposedStopPrice = proposedStop,
            CurrentExecutablePrice = executablePrice,
            MinimumPriceIncrement = increment,
            OpenProfitR = recommendation.OpenProfitR,
            AmendmentReason = recommendation.AmendmentReason,
            Reason = recommendation.Reason,
            CreatedAt = frame.AvailableAt,
            RequestedSequence = frame.Sequence,
            EffectiveFromExecutionSequence = frame.Sequence + 1
        };
        AddAmendmentEvent(
            StrategyReplayEventType.StopAmendmentRequested,
            command,
            analysis,
            proposedStop,
            null,
            recommendation,
            evaluationInterval);

        _stopAmendmentRequests++;
        ProtectiveStopAmendmentResult result = await Execution
            .AmendProtectiveStopAsync(command, Broker, cancellationToken)
            .ConfigureAwait(false);
        decimal lockedR = result.AcceptedStopPrice is decimal accepted
            ? openPosition.Side == OrderSide.Buy
                ? (accepted - entryPrice) / Math.Abs(entryPrice - initialStop)
                : (entryPrice - accepted) / Math.Abs(entryPrice - initialStop)
            : recommendation.LockedProfitR ?? 0m;
        var amendment = new StopAmendmentRecord
        {
            RequestedSequence = frame.Sequence,
            EffectiveSequence = result.EffectiveFromExecutionSequence,
            RequestedAt = frame.AvailableAt,
            AcceptedAt = result.AcceptedAt,
            PreviousStopPrice = currentStop,
            ProposedStopPrice = proposedStop,
            AcceptedStopPrice = result.AcceptedStopPrice,
            OpenProfitR = recommendation.OpenProfitR,
            LockedProfitR = lockedR,
            Reason = recommendation.AmendmentReason,
            Explanation = recommendation.Reason,
            Status = result.Status,
            PreviousStopOrderId = result.PreviousStopOrderId,
            CurrentStopOrderId = result.CurrentStopOrderId,
            RejectionReason = result.RejectionReason,
            AnalysisInterval = BarIntervalParser.Format(evaluationInterval),
            AnalysisSnapshotVersion = analysis.Version,
            Atr = recommendation.Atr,
            StructuralLevel = recommendation.StructuralLevel,
            StructureSource = recommendation.StructureSource,
            RawEntryPrice = recommendation.RawEntryPrice,
            CostAdjustedBreakEvenPrice = recommendation.CostAdjustedBreakEvenPrice,
            AtrBufferPrice = recommendation.AtrBufferPrice
        };
        StopAmendmentRecord[] history = _activeTrade.StopAmendments.Append(amendment).ToArray();
        bool acceptedAmendment = result.Status is
            ProtectiveStopAmendmentStatus.Accepted or ProtectiveStopAmendmentStatus.Replaced;
        if (acceptedAmendment)
            _acceptedStopAmendments++;
        else
            _rejectedStopAmendments++;

        if (acceptedAmendment && result.AcceptedStopPrice is decimal acceptedStop)
        {
            DateTimeOffset? breakEvenAt = recommendation.AmendmentReason == StopAmendmentReason.BreakEven
                ? _activeTrade.BreakEvenActivatedAt ?? frame.AvailableAt
                : _activeTrade.BreakEvenActivatedAt;
            bool structural = recommendation.AmendmentReason is
                StopAmendmentReason.StructureSwing or StopAmendmentReason.StructureZone or
                StopAmendmentReason.StructureChannel or StopAmendmentReason.AtrFallback;
            _activeTrade = _activeTrade with
            {
                CurrentStopLossPrice = acceptedStop,
                CurrentStopOrderId = result.CurrentStopOrderId,
                StopAmendmentCount = _activeTrade.StopAmendmentCount + 1,
                BreakEvenActivatedAt = breakEvenAt,
                StructureTrailingActivatedAt = structural
                    ? _activeTrade.StructureTrailingActivatedAt ?? frame.AvailableAt
                    : _activeTrade.StructureTrailingActivatedAt,
                ProfitFloorActivatedAt = recommendation.AmendmentReason == StopAmendmentReason.ProfitFloor
                    ? _activeTrade.ProfitFloorActivatedAt ?? frame.AvailableAt
                    : _activeTrade.ProfitFloorActivatedAt,
                MaximumGivebackProtectionActivatedAt =
                    recommendation.AmendmentReason == StopAmendmentReason.MfeGiveback
                        ? _activeTrade.MaximumGivebackProtectionActivatedAt ?? frame.AvailableAt
                        : _activeTrade.MaximumGivebackProtectionActivatedAt,
                MaximumLockedInR = Math.Max(_activeTrade.MaximumLockedInR, lockedR),
                StopAmendments = history
            };
            _lastAmendmentSnapshotVersion = analysis.Version;
            _analysisBarsSinceLastAmendment = 0;
        }
        else
        {
            _activeTrade = _activeTrade with { StopAmendments = history };
        }

        StrategyReplayEventType outcome = result.Status switch
        {
            ProtectiveStopAmendmentStatus.Accepted or ProtectiveStopAmendmentStatus.Replaced =>
                StrategyReplayEventType.StopAmendmentAccepted,
            ProtectiveStopAmendmentStatus.Unsupported =>
                StrategyReplayEventType.StopAmendmentUnsupported,
            _ => StrategyReplayEventType.StopAmendmentRejected
        };
        AddAmendmentEvent(
            outcome,
            command,
            analysis,
            proposedStop,
            result.AcceptedStopPrice,
            recommendation,
            evaluationInterval,
            result.RejectionReason);
        if (!acceptedAmendment)
            return;

        StrategyReplayEventType activationEvent = recommendation.AmendmentReason switch
        {
            StopAmendmentReason.BreakEven => StrategyReplayEventType.BreakEvenActivated,
            StopAmendmentReason.ProfitFloor => StrategyReplayEventType.ProfitFloorActivated,
            StopAmendmentReason.MfeGiveback =>
                StrategyReplayEventType.MaximumGivebackProtectionActivated,
            _ => StrategyReplayEventType.StructureTrailActivated
        };
        AddFrameEvent(
            activationEvent,
            frame.AvailableAt,
            _activeTrade.SetupId,
            _activeTrade.PositionId,
            reason: recommendation.Reason,
            reasonCode: recommendation.ReasonCode,
            profitFloorR: recommendation.ProfitFloorR,
            maximumGivebackFloorR: recommendation.MaximumGivebackFloorR);
    }

    private static EquityProtectionDirective? MergeEquityProtectionDirectives(
        EquityProtectionDirective? strategy,
        EquityProtectionDirective? account)
    {
        if (strategy is null) return account;
        if (account is null) return strategy;
        EquityProtectionPositionAction action = strategy.Action == EquityProtectionPositionAction.FlattenAllPositions ||
            account.Action == EquityProtectionPositionAction.FlattenAllPositions
            ? EquityProtectionPositionAction.FlattenAllPositions
            : EquityProtectionPositionAction.ReduceOpenPositions;
        return new EquityProtectionDirective
        {
            TierId = $"{strategy.TierId}+{account.TierId}",
            Action = action,
            ReductionFraction = Math.Max(strategy.ReductionFraction, account.ReductionFraction)
        };
    }

    private static decimal CombinedRiskMultiplier(AgentDecision decision) => Math.Clamp(
        (decision.RegimeRiskMultiplier ?? 1m) *
        (decision.TradingConditionRiskMultiplier ?? 1m) *
        (decision.CorrelationRiskMultiplier ?? 1m) *
        (decision.StrategyAllocationRiskMultiplier ?? 1m) *
        (decision.EquityProtectionRiskMultiplier ?? 1m) *
        (decision.SetupCalibrationRiskMultiplier ?? 1m) *
        (decision.MetaLabelRiskMultiplier ?? 1m) *
        (decision.NeoWaveRiskMultiplier ?? 1m) *
        (decision.StructuralEvidenceRiskMultiplier ?? 1m),
        0m,
        1m);

    private static IReadOnlyDictionary<string, decimal> RiskMultipliers(SimulatedTradeRecord trade) =>
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["regime"] = trade.RegimeRiskMultiplier ?? 1m,
            ["tradingCondition"] = trade.TradingConditionRiskMultiplier ?? 1m,
            ["correlation"] = trade.CorrelationRiskMultiplier ?? 1m,
            ["strategyAllocation"] = trade.StrategyAllocationRiskMultiplier ?? 1m,
            ["equityProtection"] = trade.EquityProtectionRiskMultiplier ?? 1m,
            ["setupCalibration"] = trade.SetupCalibrationRiskMultiplier ?? 1m,
            ["metaLabel"] = trade.MetaLabelRiskMultiplier ?? 1m,
            ["neoWave"] = trade.NeoWaveRiskMultiplier ?? 1m,
            ["structuralEvidence"] = trade.StructuralEvidenceRiskMultiplier ?? 1m
        };

    private static int IncrementSaturating(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static decimal ResolveMinimumQuantityIncrement(InstrumentKey instrument) =>
        instrument.Value.StartsWith("FX:", StringComparison.OrdinalIgnoreCase) ? 1m : 0.00000001m;

    private static string InstrumentGroup(InstrumentKey instrument) =>
        InstrumentGroupResolver.Resolve(instrument);

    private static string SessionName(DateTimeOffset timestamp) => timestamp.UtcDateTime.Hour switch
    {
        >= 7 and < 12 => "London",
        >= 12 and < 16 => "LondonNewYorkOverlap",
        >= 16 and < 21 => "NewYork",
        >= 21 and < 23 => "Rollover",
        _ => "Asia"
    };

    private static string VolatilityBucket(decimal? percentile) => percentile switch
    {
        null => "Unknown",
        < 20m => "VeryLow",
        < 40m => "Low",
        < 70m => "Normal",
        < 90m => "High",
        _ => "VeryHigh"
    };

    private void AddAmendmentEvent(
        StrategyReplayEventType type,
        ProtectiveStopAmendmentCommand command,
        AnalysisSnapshot analysis,
        decimal proposedStop,
        decimal? acceptedStop,
        TradeManagementRecommendation recommendation,
        BarInterval evaluationInterval,
        string? overrideReason = null) =>
        _frameEvents.Add(new StrategyReplayEvent
        {
            Type = type,
            StrategyId = StrategyId,
            SetupId = command.SetupId,
            PositionId = command.PositionId,
            Sequence = command.RequestedSequence,
            EventTime = command.CreatedAt,
            PreviousStop = command.CurrentStopPrice,
            ProposedStop = proposedStop,
            AcceptedStop = acceptedStop,
            AmendmentReason = command.AmendmentReason,
            OpenProfitR = recommendation.OpenProfitR,
            LockedProfitR = recommendation.LockedProfitR,
            AnalysisInterval = BarIntervalParser.Format(evaluationInterval),
            AnalysisSnapshotVersion = analysis.Version,
            Reason = overrideReason ?? recommendation.Reason
        });

    private void AddFrameEvent(
        StrategyReplayEventType type,
        DateTimeOffset eventTime,
        string? setupId = null,
        string? positionId = null,
        string? reason = null,
        string? reasonCode = null,
        PriceActionEventType? priceActionTrigger = null,
        decimal? priceActionConfidence = null,
        decimal? quantityBefore = null,
        decimal? quantityChanged = null,
        decimal? quantityRemaining = null,
        decimal? realizedProfitLoss = null,
        decimal? realizedR = null,
        string? reductionStageId = null,
        PositionReductionReason? positionReductionReason = null,
        decimal? profitFloorR = null,
        decimal? maximumGivebackFloorR = null,
        string? decisionId = null,
        string? orderId = null,
        string? reservationId = null,
        decimal? previousValue = null,
        decimal? newValue = null,
        string? optionOrModelVersion = null,
        string? evaluationSetupId = null,
        string? playbookId = null,
        PriceActionDirection? structuralDirection = null,
        StructuralSetupLifecycle? structuralLifecycle = null,
        bool? isEntryEligible = null,
        bool? isReady = null,
        bool? isSelected = null,
        StructuralPlaybookOutcome? playbookOutcome = null,
        string? primaryBlockingReasonCode = null,
        IReadOnlyList<string>? failedGateReasonCodes = null,
        IReadOnlyList<string>? supportingEvidence = null,
        IReadOnlyList<string>? conflictingEvidence = null,
        decimal? structuralConfidence = null,
        decimal? mandatoryQualityFloor = null) =>
        _frameEvents.Add(new StrategyReplayEvent
        {
            Type = type,
            StrategyId = StrategyId,
            SetupId = setupId,
            PositionId = positionId,
            DecisionId = decisionId,
            OrderId = orderId,
            ReservationId = reservationId,
            Sequence = _currentFrameSequence,
            EventTime = eventTime,
            Reason = reason,
            ReasonCode = reasonCode,
            PreviousValue = previousValue,
            NewValue = newValue,
            OptionOrModelVersion = optionOrModelVersion,
            EvaluationSetupId = evaluationSetupId,
            PlaybookId = playbookId,
            StructuralDirection = structuralDirection,
            StructuralLifecycle = structuralLifecycle,
            IsEntryEligible = isEntryEligible,
            IsReady = isReady,
            IsSelected = isSelected,
            PlaybookOutcome = playbookOutcome,
            PrimaryBlockingReasonCode = primaryBlockingReasonCode,
            FailedGateReasonCodes = failedGateReasonCodes ?? [],
            SupportingEvidence = supportingEvidence ?? [],
            ConflictingEvidence = conflictingEvidence ?? [],
            StructuralConfidence = structuralConfidence,
            MandatoryQualityFloor = mandatoryQualityFloor,
            PriceActionTrigger = priceActionTrigger,
            PriceActionConfidence = priceActionConfidence,
            QuantityBefore = quantityBefore,
            QuantityChanged = quantityChanged,
            QuantityRemaining = quantityRemaining,
            RealizedProfitLoss = realizedProfitLoss,
            RealizedR = realizedR,
            ReductionStageId = reductionStageId,
            PositionReductionReason = positionReductionReason,
            ProfitFloorR = profitFloorR,
            MaximumGivebackFloorR = maximumGivebackFloorR
        });

    private decimal ResolveExecutablePrice(MarketCandle candle, OrderSide? side)
    {
        if (side == OrderSide.Buy && candle.Bid is not null)
            return candle.Bid.Prices.Close;
        if (side == OrderSide.Sell && candle.Ask is not null)
            return candle.Ask.Prices.Close;

        decimal halfSpread = Broker.Options.SpreadBasisPoints / 2m / 10_000m;
        return side == OrderSide.Buy
            ? candle.Mid.Prices.Close * (1m - halfSpread)
            : side == OrderSide.Sell
                ? candle.Mid.Prices.Close * (1m + halfSpread)
                : candle.Mid.Prices.Close;
    }

    private static decimal ResolveMinimumPriceIncrement(InstrumentKey instrument)
    {
        string value = instrument.Value;
        return value.EndsWith("/JPY", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("_JPY", StringComparison.OrdinalIgnoreCase)
            ? 0.001m
            : value.StartsWith("FX:", StringComparison.OrdinalIgnoreCase)
                ? 0.00001m
                : 0.00000001m;
    }

    private void RecordTiming(TimeSpan elapsed)
    {
        _totalProcessing += elapsed;
        if (elapsed > _maxProcessing)
            _maxProcessing = elapsed;
        _frameDurations.Add(elapsed);
    }

    private StrategyFrameResult BuildResult(MarketFrame frame, TimeSpan processingTime)
    {
        StrategyProgressSnapshot progress = ToProgressSnapshot();
        return new StrategyFrameResult
        {
            StrategyId = StrategyId,
            StrategyName = StrategyName,
            Sequence = frame.Sequence,
            Balance = progress.Balance,
            Equity = progress.Equity,
            UnrealizedProfitLoss = progress.UnrealizedProfitLoss,
            OpenPositions = progress.OpenPositions,
            CompletedTrades = progress.CompletedTrades,
            ActiveSetups = progress.ActiveSetups,
            Status = progress.Status,
            ProcessingTime = processingTime,
            Events = _frameEvents.ToArray(),
            ExecutionDetailSetupId =
                _activeTrade?.SetupId ?? _pendingEntryDecision?.SetupId ??
                _trades.LastOrDefault(trade => trade.ClosedAt == frame.AvailableAt)?.SetupId,
            CaptureExecutionDetail = _activeTrade is not null || _pendingEntryDecision is not null
        };
    }
    private sealed record PendingPartialExit(
        string BrokerOrderId,
        PositionReductionRecommendation Recommendation,
        long SnapshotVersion,
        long RequestedSequence,
        DateTimeOffset RequestedAt,
        decimal OpenProfitR);

}
