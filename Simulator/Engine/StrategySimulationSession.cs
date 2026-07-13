using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using Brokers.Safety;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ExecutionManager;
using RiskManager;
using RiskManager.Safety;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;
using TradingCore.MarketData;
using TradingCore.Pipeline;
using TradingJournal;

namespace Simulator.Engine;

/// <summary>
/// Fully isolated per-strategy mutable session. Never share account/order/position/risk/journal state.
/// </summary>
public sealed class StrategySimulationSession : IAsyncDisposable
{
    private long _lastProcessedLedgerSequence;
    private AgentDecision? _pendingEntryDecision;
    private string? _pendingEntryBrokerOrderId;
    private SimulatedTradeRecord? _activeTrade;
    private string? _pendingExitReason;
    private readonly List<SimulatedTradeRecord> _trades = [];
    private readonly List<TimeSpan> _frameDurations = [];
    private TimeSpan _totalProcessing;
    private TimeSpan _maxProcessing;
    private TimeSpan _barrierWait;
    private int _peakChannelOccupancy;
    private long _processedFrames;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private bool _failed;
    private string? _failureMessage;
    private long? _failedSequence;

    public StrategySimulationSession(
        string strategyId,
        ITradingAgent strategy,
        SimulatedBrokerClient broker,
        HistoricalSimulationClock clock,
        IExecutionCoordinator execution,
        ITradingSafetyController safety,
        IMarketDataQualityGate dataQuality,
        ITradeJournal journal,
        IChartAnnotator? independentAnnotator = null)
    {
        StrategyId = strategyId ?? throw new ArgumentNullException(nameof(strategyId));
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        Safety = safety ?? throw new ArgumentNullException(nameof(safety));
        Journal = journal ?? throw new ArgumentNullException(nameof(journal));
        IndependentAnnotator = independentAnnotator;
        Pipeline = new SafeTradingPipeline(strategy, execution, dataQuality, safety, journal);
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
    public HistoricalSimulationClock Clock { get; }
    public IReadOnlyList<SimulatedTradeRecord> Trades => _trades;
    public bool IsFailed => _failed;
    public string? FailureMessage => _failureMessage;
    public long? FailedSequence => _failedSequence;

    public static StrategySimulationSession Create(
        string strategyId,
        ITradingAgent agent,
        SimulationOptions simulationOptions,
        TradingSafetyOptions? safetyOptions = null,
        MarketDataQualityOptions? dataQualityOptions = null,
        AnalysisSharingMode analysisSharing = AnalysisSharingMode.SharedImmutableSnapshots,
        ChartAnnotationOptions? annotationOptions = null)
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
        var journal = new InMemoryTradeJournal(options.LedgerCapacity);
        var dataQuality = new MarketDataQualityGate(dataQualityOptions ?? new MarketDataQualityOptions
        {
            RequireIndicatorsReady = true,
            RejectGaps = false
        });

        PreTradeRiskOptions riskOptions = agent.ExitManagementMode switch
        {
            AgentExitManagementMode.ProtectiveStopAndStrategyExit => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = false,
                MinimumRewardRiskRatio = null,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = 0.5m,
                AllowPyramiding = false
            },
            AgentExitManagementMode.Bracket => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = true,
                MinimumRewardRiskRatio = PreTradeRiskOptions.PhaseOneSafeDefaults.MinimumRewardRiskRatio,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = 0.5m,
                AllowPyramiding = false
            },
            _ => PreTradeRiskOptions.PhaseOneSafeDefaults
        };

        var execution = new ExecutionCoordinator(
            null,
            new PreTradeRiskManager(riskOptions),
            new BrokerExecutionSafety(),
            safety,
            journal);

        IChartAnnotator? independent = analysisSharing == AnalysisSharingMode.IndependentPerStrategy
            ? new ChartAnnotationEngine(annotationOptions)
            : null;

        return new StrategySimulationSession(
            strategyId,
            agent,
            broker,
            clock,
            execution,
            safety,
            dataQuality,
            journal,
            independent);
    }

    public async Task<StrategyFrameResult> ProcessFrameAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_failed)
            {
                return BuildResult(frame, sw.Elapsed);
            }

            Candle executionCandle = frame.ExecutionCandle.Mid;
            DateTimeOffset eventTime = frame.AvailableAt;
            _startedAt ??= executionCandle.OpenTime;
            _endedAt = eventTime;
            Clock.AdvanceTo(eventTime);

            BrokerPosition? positionBefore = (await Broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);

            await Broker.Runtime.ProcessExecutionCandleAsync(executionCandle, cancellationToken)
                .ConfigureAwait(false);

            BrokerPosition? positionAfter = (await Broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == executionCandle.Instrument);

            CapturePositionTransition(executionCandle, positionBefore, positionAfter);
            RecordNewClosedTrades();
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
                    OpenOrders = openOrders
                };

                TradingPipelineResult pipelineResult = await Pipeline
                    .ProcessAsync(context, Broker, cancellationToken)
                    .ConfigureAwait(false);
                CaptureDecision(pipelineResult);
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
            sw.Stop();
            RecordTiming(sw.Elapsed);
            throw;
        }
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
            PeakChannelOccupancy = _peakChannelOccupancy
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
                ExitReasonText = "Position remained open at the end of the requested history."
            });
            _activeTrade = null;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result with { Trades = _trades.ToArray() };
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
            LastError = _failureMessage
        };
    }

    public ValueTask DisposeAsync() => Broker.DisposeAsync();

    private Dictionary<BarInterval, AnalysisSnapshot> FilterSnapshots(MarketFrame frame)
    {
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
                frame.Snapshots.TryGetValue(interval, out snapshot);

            if (snapshot is not null)
                filtered[interval] = snapshot;
        }

        return filtered;
    }

    private bool HasRequiredSnapshots(MarketFrame frame)
    {
        foreach (BarInterval interval in Strategy.RequiredIntervals)
        {
            AnalysisSnapshot? snapshot = null;
            if (IndependentAnnotator is not null)
                snapshot = IndependentAnnotator.GetLatest(frame.ExecutionCandle.Instrument, interval);
            if (snapshot is null)
                frame.Snapshots.TryGetValue(interval, out snapshot);
            if (snapshot is null || snapshot.AvailableAt > frame.AvailableAt)
                return false;
        }

        return true;
    }

    private void CaptureDecision(TradingPipelineResult result)
    {
        AgentDecision? decision = result.Decision;
        if (decision is null || result.Submission is null ||
            result.Submission.Status == SubmissionStatus.Rejected)
        {
            return;
        }

        if (decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            _pendingEntryDecision = decision;
            _pendingEntryBrokerOrderId = result.Submission.BrokerOrderId;
        }
        else if (decision.Action == AgentAction.Close)
        {
            _pendingExitReason = decision.Reason;
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
            decimal entryCommission = -Broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    (_pendingEntryBrokerOrderId is null || entry.OrderId == _pendingEntryBrokerOrderId) &&
                    entry.Timestamp == timestamp)
                .Sum(entry => entry.Amount);
            _activeTrade = new SimulatedTradeRecord
            {
                StrategyName = decision.StrategyName ?? Strategy.Name,
                SetupId = decision.SetupId ?? decision.DecisionId ?? $"setup:{timestamp:O}",
                Instrument = decision.Instrument,
                Side = after.Side,
                SetupStartedAt = decision.SetupStartedAt ?? decision.CreatedAt,
                ConfirmationAt = decision.ConfirmationAt,
                SignalCreatedAt = decision.CreatedAt,
                OpenedAt = timestamp,
                SignalPrice = decision.ReferencePrice,
                EntryPrice = after.AveragePrice,
                Quantity = after.Quantity,
                StopLossPrice = decision.StopLossPrice,
                TakeProfitPrice = decision.TakeProfitPrice,
                ExpectedRewardRisk = decision.ExpectedRewardRisk,
                Commission = entryCommission,
                StopSource = decision.StopSource,
                TargetSource = decision.TargetSource,
                SetupReason = decision.Reason,
                ExitReason = SimulatedTradeExitReason.Unknown
            };
            _pendingEntryDecision = null;
            _pendingEntryBrokerOrderId = null;
            return;
        }

        if (before is not null && after is null && _activeTrade is not null)
        {
            LedgerEntry? realised = Broker.State.GetLedger()
                .Where(entry => entry.Type == LedgerEntryType.RealisedProfitLoss && entry.Timestamp == timestamp)
                .OrderByDescending(entry => entry.Sequence)
                .FirstOrDefault();
            decimal gross = realised?.Amount ?? 0m;
            string? exitOrderId = realised?.OrderId;
            decimal exitCommission = -Broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    entry.Timestamp == timestamp &&
                    (exitOrderId is null || entry.OrderId == exitOrderId))
                .Sum(entry => entry.Amount);
            decimal totalCommission = _activeTrade.Commission + exitCommission;
            decimal entryPrice = _activeTrade.EntryPrice ?? before.AveragePrice ?? candle.Prices.Open;
            decimal quoteToBaseRate = Broker.State.GetQuoteToBaseCurrencyRate(before.Instrument);
            decimal quoteProfitLoss = gross / quoteToBaseRate;
            decimal exitPrice = before.Side == OrderSide.Buy
                ? entryPrice + quoteProfitLoss / Math.Max(before.Quantity, 0.00000001m)
                : entryPrice - quoteProfitLoss / Math.Max(before.Quantity, 0.00000001m);
            BrokerOrder? exitOrder = exitOrderId is null ? null : Broker.State.GetOrder(exitOrderId);
            SimulatedTradeExitReason exitReason = DetermineExitReason(
                candle,
                _activeTrade,
                _pendingExitReason,
                exitOrder);
            decimal? initialRisk = _activeTrade.StopLossPrice is decimal stop
                ? Math.Abs(entryPrice - stop) * before.Quantity * quoteToBaseRate
                : null;
            _trades.Add(_activeTrade with
            {
                ClosedAt = timestamp,
                ExitPrice = exitPrice,
                GrossProfitLoss = gross,
                Commission = totalCommission,
                NetProfitLoss = gross - totalCommission,
                RMultiple = initialRisk is > 0m ? (gross - totalCommission) / initialRisk.Value : null,
                ExitReason = exitReason,
                ExitReasonText = _pendingExitReason ?? exitReason.ToString()
            });
            _activeTrade = null;
            _pendingExitReason = null;
        }
    }

    private static SimulatedTradeExitReason DetermineExitReason(
        Candle candle,
        SimulatedTradeRecord trade,
        string? strategyReason,
        BrokerOrder? exitOrder)
    {
        if (!string.IsNullOrWhiteSpace(strategyReason))
        {
            if (strategyReason.Contains("End of simulation", StringComparison.OrdinalIgnoreCase))
                return SimulatedTradeExitReason.EndOfSimulation;
            return strategyReason.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                ? SimulatedTradeExitReason.StructuralInvalidation
                : SimulatedTradeExitReason.StrategyClose;
        }

        if (string.Equals(exitOrder?.Type, StandardOrderType.Stop.ToString(), StringComparison.Ordinal))
            return SimulatedTradeExitReason.StopLoss;
        if (string.Equals(exitOrder?.Type, StandardOrderType.Limit.ToString(), StringComparison.Ordinal))
            return SimulatedTradeExitReason.TakeProfit;
        if (trade.StopLossPrice is decimal stop && candle.Prices.Low <= stop && candle.Prices.High >= stop)
            return SimulatedTradeExitReason.StopLoss;
        if (trade.TakeProfitPrice is decimal target && candle.Prices.Low <= target && candle.Prices.High >= target)
            return SimulatedTradeExitReason.TakeProfit;
        return SimulatedTradeExitReason.Unknown;
    }

    private void RecordNewClosedTrades()
    {
        foreach (LedgerEntry entry in Broker.State.GetLedger()
                     .Where(entry =>
                         entry.Sequence > _lastProcessedLedgerSequence &&
                         entry.Type == LedgerEntryType.RealisedProfitLoss)
                     .OrderBy(entry => entry.Sequence))
        {
            _lastProcessedLedgerSequence = entry.Sequence;
            SimulatedTradeRecord? matchingTrade = _trades.LastOrDefault(trade =>
                trade.ClosedAt == entry.Timestamp);
            decimal netClosedTrade = matchingTrade?.NetProfitLoss ?? entry.Amount;
            TradingSafetySnapshot snapshot = Safety.RecordClosedTrade(netClosedTrade, entry.Timestamp);
            Journal.Append(new TradeJournalEntry
            {
                Sequence = 0,
                Timestamp = entry.Timestamp,
                Type = TradeJournalEventType.ClosedTradeRecorded,
                Value = netClosedTrade,
                Message = $"Recorded net closed-trade P/L {netClosedTrade:F2}. Safety state is {snapshot.State}."
            });
        }

        long latestSequence = Broker.State.GetLedger()
            .Select(entry => entry.Sequence)
            .DefaultIfEmpty(_lastProcessedLedgerSequence)
            .Max();
        _lastProcessedLedgerSequence = Math.Max(_lastProcessedLedgerSequence, latestSequence);
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
            ProcessingTime = processingTime
        };
    }
}
