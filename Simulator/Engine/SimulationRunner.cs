using Agent.Abstractions;
using ExecutionManager;
using TradingJournal;
using Agent.Models;
using TradingCore.Pipeline;
using RiskManager.Safety;
using TradingCore.MarketData;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using Simulator.Abstractions;
using Simulator.Broker;
using Simulator.Models;

namespace Simulator.Engine;

/// <summary>
/// Deterministic historical runner. It processes existing orders before exposing the
/// current candle close to the Agent, so newly submitted orders can only fill later.
/// Data quality and safety controls are evaluated before every strategy decision.
/// </summary>
public sealed class SimulationRunner
{
    private readonly IHistoricalCandleSource _source;
    private readonly IAdjustableSimulationClock _clock;
    private readonly MultiTimeframeAggregator _aggregator;
    private readonly IChartAnnotator _annotator;
    private readonly ITradingAgent _agent;
    private readonly SafeTradingPipeline _pipeline;
    private readonly ITradingSafetyController _safety;
    private readonly ITradeJournal _journal;
    private readonly SimulatedBrokerClient _broker;
    private readonly Dictionary<BarInterval, AnalysisSnapshot> _latestAnalysis = [];
    private long _lastProcessedLedgerSequence;
    private AgentDecision? _pendingEntryDecision;
    private string? _pendingEntryBrokerOrderId;
    private SimulatedTradeRecord? _activeTrade;
    private string? _pendingExitReason;
    private readonly List<SimulatedTradeRecord> _trades = [];

    public SimulationRunner(
        IHistoricalCandleSource source,
        IAdjustableSimulationClock clock,
        MultiTimeframeAggregator aggregator,
        IChartAnnotator annotator,
        ITradingAgent agent,
        IExecutionCoordinator execution,
        SimulatedBrokerClient broker,
        IMarketDataQualityGate? dataQuality = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _annotator = annotator ?? throw new ArgumentNullException(nameof(annotator));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        ArgumentNullException.ThrowIfNull(execution);
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _safety = safety ?? new TradingSafetyController();
        _journal = journal ?? NullTradeJournal.Instance;
        _pipeline = new SafeTradingPipeline(
            _agent,
            execution,
            dataQuality,
            _safety,
            _journal);

        if (_agent.RequiredIntervals.Count == 0)
        {
            throw new ArgumentException(
                "The Agent must require at least one analysis interval.",
                nameof(agent));
        }

        if (!_agent.RequiredIntervals.Contains(_agent.TriggerInterval))
        {
            throw new ArgumentException(
                "The Agent trigger interval must also be one of its required intervals.",
                nameof(agent));
        }

        foreach (BarInterval required in _agent.RequiredIntervals)
        {
            if (!_aggregator.Intervals.Contains(required))
            {
                throw new ArgumentException(
                    $"The aggregator does not provide the Agent's required interval {required}.",
                    nameof(aggregator));
            }
        }
    }

    public async Task<SimulationResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        Candle? lastBaseCandle = null;

        await foreach (Candle baseCandle in _source.ReadAsync(cancellationToken))
        {
            lastBaseCandle = baseCandle;
            DateTimeOffset eventTime = baseCandle.CloseTime ?? baseCandle.OpenTime;
            startedAt ??= baseCandle.OpenTime;
            endedAt = eventTime;
            _clock.AdvanceTo(eventTime);

            // Existing orders react first. Orders created from this candle's analysis
            // are stamped with the new market sequence and cannot fill here.
            BrokerPosition? positionBefore = (await _broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == baseCandle.Instrument);
            if (_activeTrade is not null)
                UpdateExcursions(baseCandle);
            await _broker.Runtime.ProcessExecutionCandleAsync(baseCandle, cancellationToken)
                .ConfigureAwait(false);
            BrokerPosition? positionAfter = (await _broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == baseCandle.Instrument);
            CapturePositionTransition(baseCandle, positionBefore, positionAfter);
            if (positionBefore is null && positionAfter is not null && _activeTrade is not null)
                UpdateExcursions(baseCandle);
            RecordNewClosedTrades();

            IReadOnlyList<CandleClosedEvent> closedEvents = _aggregator.Apply(baseCandle);
            bool triggerClosed = false;

            foreach (CandleClosedEvent candleEvent in closedEvents)
            {
                if (candleEvent.Interval != baseCandle.Interval ||
                    candleEvent.Candle.OpenTime != baseCandle.OpenTime)
                {
                    _broker.Runtime.RecordAnalyticalCandle(candleEvent.Candle);
                }

                AnalysisSnapshot snapshot = await _annotator
                    .ProcessAsync(candleEvent, runtimeContext: null, cancellationToken)
                    .ConfigureAwait(false);
                _latestAnalysis[candleEvent.Interval] = snapshot;
                triggerClosed |= candleEvent.Interval == _agent.TriggerInterval;
            }

            if (!triggerClosed || !HasRequiredSnapshots(eventTime))
            {
                continue;
            }

            AccountSnapshot account = (await _broker.Accounts
                .GetAccountsAsync(cancellationToken)
                .ConfigureAwait(false)).Single();
            IReadOnlyList<BrokerPosition> positions = await _broker.Positions
                .GetOpenPositionsAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> openOrders = await _broker.Orders
                .GetOpenOrdersAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            InstrumentKey instrument = baseCandle.Instrument;
            var analysis = new MultiTimeframeAnalysis(
                instrument,
                eventTime,
                _agent.RequiredIntervals.ToDictionary(
                    interval => interval,
                    interval => _latestAnalysis[interval]));

            AgentMarketContext context = new()
            {
                Instrument = instrument,
                Timestamp = eventTime,
                Analysis = analysis,
                Account = account,
                Positions = positions,
                OpenOrders = openOrders
            };

            TradingPipelineResult pipelineResult = await _pipeline
                .ProcessAsync(context, _broker, cancellationToken)
                .ConfigureAwait(false);
            CaptureDecision(pipelineResult);
        }

        if (startedAt is null || endedAt is null)
        {
            throw new InvalidOperationException("The historical source did not provide any candles.");
        }

        if (_broker.Options.CloseOpenPositionsAtEnd && lastBaseCandle is not null)
        {
            BrokerPosition? positionBefore = (await _broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(position => position.Instrument == lastBaseCandle.Instrument);
            if (positionBefore is not null)
            {
                _pendingExitReason = "End of simulation liquidation.";
                await _broker.Runtime.LiquidateAtMarketCloseAsync(lastBaseCandle, cancellationToken)
                    .ConfigureAwait(false);
                BrokerPosition? positionAfter = (await _broker.Positions
                    .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(position => position.Instrument == lastBaseCandle.Instrument);
                CapturePositionTransition(lastBaseCandle, positionBefore, positionAfter);
            }
        }

        RecordNewClosedTrades();
        SimulationResult result = _broker.State.BuildResult(startedAt.Value, endedAt.Value);
        if (_activeTrade is not null)
        {
            _trades.Add(_activeTrade with
            {
                ClosedAt = endedAt,
                ExitReason = SimulatedTradeExitReason.EndOfSimulation,
                ExitReasonText = "Position remained open at the end of the requested history."
            });
            _activeTrade = null;
        }
        return result with { Trades = _trades.ToArray() };
    }

    private void CaptureDecision(TradingPipelineResult result)
    {
        AgentDecision? decision = result.Decision;
        if (decision?.Action == AgentAction.Cancel)
        {
            _pendingEntryDecision = null;
            _pendingEntryBrokerOrderId = null;
            return;
        }
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
            decimal entryCommission = -_broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    (_pendingEntryBrokerOrderId is null || entry.OrderId == _pendingEntryBrokerOrderId) &&
                    entry.Timestamp == timestamp)
                .Sum(entry => entry.Amount);
            _activeTrade = new SimulatedTradeRecord
            {
                StrategyId = decision.StrategyId ?? decision.StrategyName ?? "unknown",
                PlaybookId = decision.PlaybookId ?? "unknown",
                StrategyName = decision.StrategyName ?? _agent.Name,
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
            LedgerEntry? realised = _broker.State.GetLedger()
                .Where(entry => entry.Type == LedgerEntryType.RealisedProfitLoss && entry.Timestamp == timestamp)
                .OrderByDescending(entry => entry.Sequence)
                .FirstOrDefault();
            decimal gross = realised?.Amount ?? 0m;
            string? exitOrderId = realised?.OrderId;
            decimal exitCommission = -_broker.State.GetLedger()
                .Where(entry =>
                    entry.Type == LedgerEntryType.Commission &&
                    entry.Timestamp == timestamp &&
                    (exitOrderId is null || entry.OrderId == exitOrderId))
                .Sum(entry => entry.Amount);
            decimal totalCommission = _activeTrade.Commission + exitCommission;
            decimal entryPrice = _activeTrade.EntryPrice ?? before.AveragePrice ?? candle.Prices.Open;
            decimal quoteToBaseRate = _broker.State.GetQuoteToBaseCurrencyRate(before.Instrument);
            decimal quoteProfitLoss = gross / quoteToBaseRate;
            decimal exitPrice = before.Side == OrderSide.Buy
                ? entryPrice + quoteProfitLoss / Math.Max(before.Quantity, 0.00000001m)
                : entryPrice - quoteProfitLoss / Math.Max(before.Quantity, 0.00000001m);
            BrokerOrder? exitOrder = exitOrderId is null ? null : _broker.State.GetOrder(exitOrderId);
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

    private void UpdateExcursions(Candle candle)
    {
        if (_activeTrade?.EntryPrice is not decimal entry || _activeTrade.Quantity <= 0m)
            return;

        decimal favourablePrice = _activeTrade.Side == OrderSide.Buy
            ? candle.Prices.High
            : candle.Prices.Low;
        decimal adversePrice = _activeTrade.Side == OrderSide.Buy
            ? candle.Prices.Low
            : candle.Prices.High;
        decimal direction = _activeTrade.Side == OrderSide.Buy ? 1m : -1m;
        decimal quoteToBase = _broker.State.GetQuoteToBaseCurrencyRate(_activeTrade.Instrument);
        decimal favourableAmount =
            (favourablePrice - entry) * direction * _activeTrade.Quantity * quoteToBase;
        decimal adverseAmount =
            (adversePrice - entry) * direction * _activeTrade.Quantity * quoteToBase;
        decimal? initialRisk = _activeTrade.StopLossPrice is decimal stop
            ? Math.Abs(entry - stop) * _activeTrade.Quantity * quoteToBase
            : null;
        DateTimeOffset timestamp = candle.CloseTime ?? candle.Interval.AddTo(candle.OpenTime);

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

        _activeTrade = updated;
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
            {
                return SimulatedTradeExitReason.EndOfSimulation;
            }

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
        foreach (LedgerEntry entry in _broker.State.GetLedger()
                     .Where(entry =>
                         entry.Sequence > _lastProcessedLedgerSequence &&
                         entry.Type == LedgerEntryType.RealisedProfitLoss)
                     .OrderBy(entry => entry.Sequence))
        {
            _lastProcessedLedgerSequence = entry.Sequence;
            // Safety limits should use the complete trade result, including both
            // entry and exit commissions, rather than the gross realised ledger item.
            SimulatedTradeRecord? matchingTrade = _trades.LastOrDefault(trade =>
                trade.ClosedAt == entry.Timestamp);
            decimal netClosedTrade = matchingTrade?.NetProfitLoss ?? entry.Amount;
            TradingSafetySnapshot snapshot = _safety.RecordClosedTrade(netClosedTrade, entry.Timestamp);
            _journal.Append(new TradeJournalEntry
            {
                Sequence = 0,
                Timestamp = entry.Timestamp,
                Type = TradeJournalEventType.ClosedTradeRecorded,
                Value = netClosedTrade,
                Message = $"Recorded net closed-trade P/L {netClosedTrade:F2}. " +
                    $"Safety state is {snapshot.State}."
            });

            if (!snapshot.CanOpenNewTrades)
            {
                _journal.Append(new TradeJournalEntry
                {
                    Sequence = 0,
                    Timestamp = entry.Timestamp,
                    Type = TradeJournalEventType.SafetyStateChanged,
                    Value = entry.Amount,
                    Message = snapshot.Message ?? snapshot.Reason.ToString()
                });
            }
        }

        long latestSequence = _broker.State.GetLedger()
            .Select(entry => entry.Sequence)
            .DefaultIfEmpty(_lastProcessedLedgerSequence)
            .Max();
        _lastProcessedLedgerSequence = Math.Max(_lastProcessedLedgerSequence, latestSequence);
    }

    private bool HasRequiredSnapshots(DateTimeOffset timestamp) =>
        _agent.RequiredIntervals.All(interval =>
            _latestAnalysis.TryGetValue(interval, out AnalysisSnapshot? snapshot) &&
            snapshot.AvailableAt <= timestamp);
}
