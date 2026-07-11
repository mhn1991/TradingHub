using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;
using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;
using TradingHub.Simulation.Execution;

namespace TradingHub.Simulation.Broker;

public sealed class SimulatedBroker : IBrokerGateway
{
    private readonly SimulatedBrokerOptions _options;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<string, InstrumentDefinition> _instruments;
    private readonly IExecutionModel _executionModel;
    private readonly Dictionary<string, PendingOrder> _orders = new(StringComparer.Ordinal);
    private readonly SimulatedAccount _account;
    private long _sequence;

    public SimulatedBroker(
        SimulatedBrokerOptions options,
        IClock clock,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments,
        ExecutionModelOptions executionOptions)
    {
        options.EnsureValid();
        _options = options;
        _clock = clock;
        _instruments = instruments;
        _executionModel = new BarExecutionModel(executionOptions);
        _account = new SimulatedAccount(options);
    }

    public string BrokerId => _options.BrokerId;

    public string ProviderName => "Simulation";

    public TradingEnvironment Environment => TradingEnvironment.HistoricalSimulation;

    public Task<OrderSubmissionResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rejection = Validate(request);
        if (rejection is not null)
        {
            return Task.FromResult(rejection);
        }

        var sequence = Interlocked.Increment(ref _sequence);
        var brokerOrderId = $"SIM-{sequence:D12}";
        _orders.Add(request.OrderId, new PendingOrder
        {
            Sequence = sequence,
            BrokerOrderId = brokerOrderId,
            Request = request,
            EligibleAt = _clock.UtcNow + _options.OutboundLatency
        });

        return Task.FromResult(new OrderSubmissionResult
        {
            Outcome = SubmissionOutcome.Accepted,
            Status = OrderStatus.Acknowledged,
            BrokerOrderId = brokerOrderId,
            OccurredAt = _clock.UtcNow
        });
    }

    public Task<OrderCancellationResult> CancelOrderAsync(
        OrderCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = _orders.Values.FirstOrDefault(candidate => candidate.BrokerOrderId == request.BrokerOrderId);
        if (request.AccountId != _options.AccountId || order is null)
        {
            return Task.FromResult(new OrderCancellationResult
            {
                Outcome = SubmissionOutcome.Rejected,
                Status = OrderStatus.Rejected,
                OccurredAt = _clock.UtcNow,
                Reason = "The simulated account or broker order was not found."
            });
        }

        _orders.Remove(order.Request.OrderId);
        return Task.FromResult(new OrderCancellationResult
        {
            Outcome = SubmissionOutcome.Accepted,
            Status = OrderStatus.Cancelled,
            OccurredAt = _clock.UtcNow
        });
    }

    public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accountId != _options.AccountId)
        {
            throw new KeyNotFoundException($"Simulated account '{accountId}' was not found.");
        }

        return Task.FromResult(_account.CreateSnapshot(_clock.UtcNow));
    }

    public IReadOnlyList<ExecutionReport> ProcessBar(PriceBar bar)
    {
        if (!_instruments.TryGetValue(bar.InstrumentId, out var instrument))
        {
            throw new KeyNotFoundException($"Instrument '{bar.InstrumentId}' is not configured.");
        }

        var reports = new List<ExecutionReport>();
        var candidates = _orders.Values
            .Where(order => order.Request.InstrumentId == bar.InstrumentId)
            .OrderBy(order => order.Sequence)
            .ToArray();

        foreach (var order in candidates)
        {
            var decision = _executionModel.TryExecute(order, bar, instrument);
            if (decision is null)
            {
                continue;
            }

            var report = CreateReport(order, decision, bar.CloseTime);
            reports.Add(report);
            order.FilledQuantity += decision.Quantity;
            _account.ApplyFill(report, instrument);

            if (IsTerminal(decision.Status))
            {
                _orders.Remove(order.Request.OrderId);
            }
        }

        return reports;
    }

    private OrderSubmissionResult? Validate(OrderRequest request)
    {
        string? reason = null;
        if (request.AccountId != _options.AccountId)
        {
            reason = "The order targets a different simulated account.";
        }
        else if (!_instruments.TryGetValue(request.InstrumentId, out var instrument) || !instrument.IsEnabled)
        {
            reason = "The instrument is not enabled at the simulated broker.";
        }
        else if (!instrument.IsQuantityValid(request.Quantity))
        {
            reason = "The quantity violates the simulated broker's instrument rules.";
        }

        return reason is null
            ? null
            : new OrderSubmissionResult
            {
                Outcome = SubmissionOutcome.Rejected,
                Status = OrderStatus.Rejected,
                OccurredAt = _clock.UtcNow,
                Reason = reason
            };
    }

    private static ExecutionReport CreateReport(
        PendingOrder order,
        FillDecision decision,
        DateTimeOffset occurredAt)
    {
        return new ExecutionReport
        {
            OrderId = order.Request.OrderId,
            BrokerOrderId = order.BrokerOrderId,
            InstrumentId = order.Request.InstrumentId,
            Side = order.Request.Side,
            Status = decision.Status,
            LastFillQuantity = decision.Quantity,
            LastFillPrice = decision.Price,
            CumulativeFilledQuantity = order.FilledQuantity + decision.Quantity,
            Fee = decision.Fee,
            OccurredAt = occurredAt,
            Reason = decision.Reason
        };
    }

    private static bool IsTerminal(OrderStatus status)
    {
        return status is OrderStatus.Filled
            or OrderStatus.Cancelled
            or OrderStatus.Rejected
            or OrderStatus.Expired;
    }
}
