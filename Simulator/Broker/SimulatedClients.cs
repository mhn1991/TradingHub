using System.Runtime.CompilerServices;
using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.Broker;

internal sealed class SimulatedMarketDataClient(
    SimulatedBrokerState state,
    Action ensureActive) : IMarketDataClient
{
    public Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetCandles(query));
    }
}

internal sealed class SimulatedAccountClient(
    SimulatedBrokerState state,
    Action ensureActive) : IAccountClient
{
    public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AccountSnapshot>>([state.GetAccount()]);
    }
}

internal sealed class SimulatedPositionClient(
    SimulatedBrokerState state,
    Action ensureActive) : IPositionClient
{
    public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetPositions());
    }
}

internal sealed class SimulatedCostClient(
    SimulationOptions options,
    Action ensureActive) : ICostClient
{
    public Task<CommissionSchedule> GetCommissionAsync(
        InstrumentKey instrument,
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        if (instrument.IsEmpty)
        {
            throw new ArgumentException("An instrument is required.", nameof(instrument));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CommissionSchedule
        {
            Instrument = instrument,
            Maker = options.CommissionRate,
            Taker = options.CommissionRate,
            Buyer = 0m,
            Seller = 0m,
            Source = "Simulator percentage commission"
        });
    }
}

internal sealed class SimulatedOrderClient(
    SimulatedBrokerState state,
    BoundedAsyncEventLog<OrderEvent> events,
    ISimulationClock clock,
    Action ensureActive) : ITradingOrderClient
{
    public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetOpenOrders(instrument));
    }

    public Task<OrderSubmission> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        string? validationError = Validate(request, clock.UtcNow);
        validationError ??= state.GetOrderConfigurationError(request);
        if (validationError is not null)
        {
            state.RejectOrder();
            string rejectedId = request.ClientOrderId ?? $"rejected-{Guid.NewGuid():N}";
            events.Append(new OrderEvent
            {
                BrokerOrderId = rejectedId,
                ClientOrderId = request.ClientOrderId,
                Instrument = request.Instrument,
                Type = OrderEventType.Rejected,
                Timestamp = clock.UtcNow,
                Message = validationError
            });

            return Task.FromResult(new OrderSubmission
            {
                ClientOrderId = rejectedId,
                Status = SubmissionStatus.Rejected,
                Certainty = ExecutionCertainty.Rejected,
                RejectionReason = validationError
            });
        }

        if (!state.TryCreateOrder(
                request,
                clock.UtcNow,
                out SimulatedOrder? order,
                out string? creationError) ||
            order is null)
        {
            state.RejectOrder();
            string rejectedId = request.ClientOrderId ?? $"rejected-{Guid.NewGuid():N}";
            events.Append(new OrderEvent
            {
                BrokerOrderId = rejectedId,
                ClientOrderId = request.ClientOrderId,
                Instrument = request.Instrument,
                Type = OrderEventType.Rejected,
                Timestamp = clock.UtcNow,
                Message = creationError
            });
            return Task.FromResult(new OrderSubmission
            {
                ClientOrderId = rejectedId,
                Status = SubmissionStatus.Rejected,
                Certainty = ExecutionCertainty.Rejected,
                RejectionReason = creationError
            });
        }

        events.Append(ToEvent(order, OrderEventType.Accepted, clock.UtcNow));

        return Task.FromResult(new OrderSubmission
        {
            ClientOrderId = order.ClientOrderId,
            BrokerOrderId = order.BrokerOrderId,
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        });
    }

    public Task CancelOrderAsync(
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        ensureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        if (!state.TryCancel(brokerOrderId, out SimulatedOrder? cancelled) || cancelled is null)
        {
            throw new KeyNotFoundException($"Open simulated order {brokerOrderId} was not found.");
        }

        events.Append(ToEvent(cancelled, OrderEventType.Cancelled, clock.UtcNow));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ensureActive();
        await foreach (OrderEvent orderEvent in events.ReadAllAsync(cancellationToken))
        {
            yield return orderEvent;
        }
    }

    internal static OrderEvent ToEvent(
        SimulatedOrder order,
        OrderEventType eventType,
        DateTimeOffset timestamp,
        decimal? fillPrice = null,
        decimal? fillQuantity = null,
        decimal? fee = null,
        string? message = null) => new()
        {
            BrokerOrderId = order.BrokerOrderId,
            ClientOrderId = order.ClientOrderId,
            Instrument = order.Request.Instrument,
            Type = eventType,
            Timestamp = timestamp,
            FillPrice = fillPrice,
            FillQuantity = fillQuantity,
            RemainingQuantity = Math.Max(0m, order.Request.Quantity.Value - order.FilledQuantity),
            Fee = fee,
            Message = message
        };

    private static string? Validate(PlaceOrderRequest request, DateTimeOffset now)
    {
        if (request.Instrument.IsEmpty)
        {
            return "An instrument is required.";
        }

        if (!Enum.IsDefined(request.Side) || request.Side == OrderSide.Unknown)
        {
            return "Order side must be Buy or Sell.";
        }

        if (!Enum.IsDefined(request.Type))
        {
            return "The order type is invalid.";
        }

        if (!Enum.IsDefined(request.TimeInForce))
        {
            return "The time-in-force value is invalid.";
        }

        if (request.Quantity.Value <= 0m || !Enum.IsDefined(request.Quantity.Unit))
        {
            return "Order quantity must be positive and have a valid unit.";
        }

        if (request.Quantity.Unit is not (QuantityUnit.Units or QuantityUnit.BaseAsset))
        {
            return $"The simulator does not support {request.Quantity.Unit} quantities without " +
                "instrument-specific conversion metadata.";
        }

        if (request.ClientOrderId is not null && string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            return "Client order ID cannot be empty.";
        }

        if (request.StopLoss is { Price: <= 0m })
        {
            return "Stop-loss price must be positive.";
        }

        if (request.TakeProfit is { Price: <= 0m })
        {
            return "Take-profit price must be positive.";
        }

        if (request.TimeInForce == StandardTimeInForce.GoodTillDate && request.ExpireAt is null)
        {
            return "GoodTillDate orders require an expiry time.";
        }

        if (request.ExpireAt is DateTimeOffset expiry && expiry <= now)
        {
            return "Order expiry must be later than the submission time.";
        }

        return request.Type switch
        {
            StandardOrderType.Limit when request.LimitPrice is null or <= 0m =>
                "A positive limit price is required.",
            StandardOrderType.Stop when request.StopPrice is null or <= 0m =>
                "A positive stop price is required.",
            StandardOrderType.StopLimit when request.StopPrice is null or <= 0m =>
                "A positive stop price is required.",
            StandardOrderType.StopLimit when request.LimitPrice is null or <= 0m =>
                "A positive limit price is required.",
            _ => null
        };
    }
}
