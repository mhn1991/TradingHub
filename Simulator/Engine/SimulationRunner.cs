using Agent.Abstractions;
using Agent.Execution;
using Agent.Models;
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
/// </summary>
public sealed class SimulationRunner
{
    private readonly IHistoricalCandleSource _source;
    private readonly IAdjustableSimulationClock _clock;
    private readonly MultiTimeframeAggregator _aggregator;
    private readonly IChartAnnotator _annotator;
    private readonly ITradingAgent _agent;
    private readonly IExecutionCoordinator _execution;
    private readonly SimulatedBrokerClient _broker;
    private readonly Dictionary<BarInterval, AnalysisSnapshot> _latestAnalysis = [];

    public SimulationRunner(
        IHistoricalCandleSource source,
        IAdjustableSimulationClock clock,
        MultiTimeframeAggregator aggregator,
        IChartAnnotator annotator,
        ITradingAgent agent,
        IExecutionCoordinator execution,
        SimulatedBrokerClient broker)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _annotator = annotator ?? throw new ArgumentNullException(nameof(annotator));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));

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

        await foreach (Candle baseCandle in _source.ReadAsync(cancellationToken))
        {
            DateTimeOffset eventTime = baseCandle.CloseTime ?? baseCandle.OpenTime;
            startedAt ??= baseCandle.OpenTime;
            endedAt = eventTime;
            _clock.AdvanceTo(eventTime);

            // Existing orders react first. Orders created from this candle's analysis
            // are stamped with the new market sequence and cannot fill here.
            await _broker.Runtime.ProcessExecutionCandleAsync(baseCandle, cancellationToken)
                .ConfigureAwait(false);

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
                    .ProcessAsync(candleEvent, cancellationToken)
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

            AgentDecision decision = await _agent
                .EvaluateAsync(context, cancellationToken)
                .ConfigureAwait(false);
            await _execution
                .ProcessAsync(decision, _broker, cancellationToken)
                .ConfigureAwait(false);
        }

        if (startedAt is null || endedAt is null)
        {
            throw new InvalidOperationException("The historical source did not provide any candles.");
        }

        return _broker.State.BuildResult(startedAt.Value, endedAt.Value);
    }

    private bool HasRequiredSnapshots(DateTimeOffset timestamp) =>
        _agent.RequiredIntervals.All(interval =>
            _latestAnalysis.TryGetValue(interval, out AnalysisSnapshot? snapshot) &&
            snapshot.AvailableAt <= timestamp);
}
