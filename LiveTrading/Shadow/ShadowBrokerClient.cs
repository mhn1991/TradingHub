using Brokers.Abstractions;
using Brokers.Models;

namespace LiveTrading.Shadow;

/// <summary>
/// Wraps the real read-only <see cref="IBrokerClient"/> so <see
/// cref="TradingCore.Pipeline.SafeTradingPipeline"/>.ProcessAsync's fixed <see
/// cref="ITradingBrokerClient"/> parameter type is satisfiable without ever granting write
/// capability. Reads delegate to the wrapped client; every write-capable member throws -
/// defense-in-depth on top of <see cref="ShadowExecutionCoordinator"/> never calling them.
/// </summary>
public sealed class ShadowBrokerClient(IBrokerClient inner) : ITradingBrokerClient
{
    public BrokerDescriptor Descriptor => inner.Descriptor;
    public BrokerCapabilities Capabilities => inner.Capabilities;
    public IMarketDataClient MarketData => inner.MarketData;
    public IAccountClient Accounts => inner.Accounts;
    public IPositionClient Positions => inner.Positions;
    public ICostClient Costs => inner.Costs;
    public ITradingOrderClient Orders { get; } = new ShadowOrderClient(inner.Orders);
    IOrderClient IBrokerClient.Orders => Orders;

    /// <summary>Does not own the wrapped broker's lifetime - this wrapper is constructed per
    /// pipeline call and must not dispose the shared broker singleton underneath it.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ShadowOrderClient(IOrderClient inner) : ITradingOrderClient
    {
        public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
            InstrumentKey? instrument = null, CancellationToken cancellationToken = default) =>
            inner.GetOpenOrdersAsync(instrument, cancellationToken);

        public Task<OrderSubmission> PlaceOrderAsync(
            PlaceOrderRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Shadow mode never places orders.");

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Shadow mode never cancels orders.");

        public IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Shadow mode never streams order events.");
    }
}
