using Brokers.Models;

namespace Brokers.Abstractions;

public interface ITradingBrokerClient : IBrokerClient
{
    new ITradingOrderClient Orders { get; }
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
