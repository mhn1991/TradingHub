using Brokers.Models;

namespace Brokers.Abstractions;

public interface ITradingBrokerClient : IBrokerClient
{
    new ITradingOrderClient Orders { get; }
}

/// <summary>
/// Trading broker that explicitly exposes protective-order mutation. Brokers that
/// cannot perform a safe atomic amendment should advertise unsupported and retain
/// the existing stop.
/// </summary>
public interface IProtectiveOrderBrokerClient : ITradingBrokerClient
{
    TradingBrokerCapabilities TradingCapabilities { get; }

    IProtectiveOrderClient ProtectiveOrders { get; }
}

public interface IProtectiveOrderClient
{
    Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        AmendProtectiveStopRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITradingOrderClient : IOrderClient
{
    Task<OrderSubmission> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default);

    Task CancelOrderAsync(
        string brokerOrderId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Trading broker that supports an explicit reduce-only close of an owned trade.</summary>
public interface IPositionReductionBrokerClient : ITradingBrokerClient
{
    IPositionReductionClient PositionReductions { get; }
}

public interface IPositionReductionClient
{
    Task<PositionReductionResult> ReducePositionAsync(
        ReduceBrokerPositionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Trading broker that can replay normalized account events after a durable cursor.</summary>
public interface ITransactionHistoryBrokerClient : ITradingBrokerClient
{
    ITransactionHistoryClient TransactionHistory { get; }
}

public interface ITransactionHistoryClient
{
    Task<BrokerEventHistoryBatch> GetOrderEventsSinceAsync(
        string lastTransactionId,
        CancellationToken cancellationToken = default);
}
