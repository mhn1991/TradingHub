using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.Broker;

public sealed class SimulatedBrokerClient : IProtectiveOrderBrokerClient, IAccountCurrencyConversionProvider
{
    private readonly BoundedAsyncEventLog<OrderEvent> _orderEvents;
    private readonly SimulatedBrokerState _state;
    private readonly SimulatedOrderClient _orders;
    private int _disposed;

    public SimulatedBrokerClient(
        SimulationOptions options,
        ISimulationClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        Validate(options);

        Options = options;
        _state = new SimulatedBrokerState(options);
        _orderEvents = new BoundedAsyncEventLog<OrderEvent>(options.OrderEventCapacity);

        _orders = new SimulatedOrderClient(_state, _orderEvents, clock, ThrowIfDisposed);
        Descriptor = new BrokerDescriptor(
            options.ModelledBroker,
            BrokerEnvironment.Demo,
            options.AccountId);
        MarketData = new SimulatedMarketDataClient(_state, ThrowIfDisposed);
        Accounts = new SimulatedAccountClient(_state, ThrowIfDisposed);
        Positions = new SimulatedPositionClient(_state, ThrowIfDisposed);
        Costs = new SimulatedCostClient(options, ThrowIfDisposed);
        Runtime = new SimulatedBrokerRuntime(_state, _orderEvents, options, clock, ThrowIfDisposed);
    }

    public SimulationOptions Options { get; }
    public BrokerDescriptor Descriptor { get; }
    public IMarketDataClient MarketData { get; }
    public IAccountClient Accounts { get; }
    public ITradingOrderClient Orders => _orders;
    IOrderClient IBrokerClient.Orders => _orders;
    public TradingBrokerCapabilities TradingCapabilities { get; } = new()
    {
        SupportsNativeStopAmendment = false,
        SupportsAtomicOrderReplacement = true,
        SupportsDependentOcoAmendment = true
    };
    public IProtectiveOrderClient ProtectiveOrders => _orders;
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }
    public SimulatedBrokerRuntime Runtime { get; }

    public bool TryGetQuoteToAccountCurrencyRate(InstrumentKey instrument, out decimal rate) =>
        _state.TryGetQuoteToBaseCurrencyRate(instrument, out rate, out _);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _orderEvents.Complete();
        }

        return ValueTask.CompletedTask;
    }

    internal SimulatedBrokerState State => _state;
    internal IReadOnlyList<(long Sequence, OrderEvent Item)> GetOrderEventsAfter(long sequence) =>
        _orderEvents.SnapshotAfter(sequence);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private static void Validate(SimulationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AccountId) ||
            string.IsNullOrWhiteSpace(options.BaseCurrency) ||
            options.QuoteToBaseCurrencyRates is null)
        {
            throw new ArgumentException(
                "Simulation account ID and base currency are required.",
                nameof(options));
        }

        if (!Enum.IsDefined(options.ModelledBroker) ||
            !Enum.IsDefined(options.OcoFillPolicy))
        {
            throw new ArgumentException("Simulation enum options must be valid.", nameof(options));
        }

        if (options.QuoteToBaseCurrencyRates.Any(rate =>
                string.IsNullOrWhiteSpace(rate.Key) || rate.Value <= 0m))
        {
            throw new ArgumentException(
                "Quote-currency conversion rates must have a currency and a positive rate.",
                nameof(options));
        }

        if (options.StartingBalance <= 0m ||
            options.Leverage <= 0m ||
            options.CommissionRate is < 0m or > 1m ||
            options.SpreadBasisPoints < 0m ||
            options.SlippageBasisPoints < 0m ||
            options.SpreadBasisPoints / 2m + options.SlippageBasisPoints >= 10_000m ||
            options.CandleCapacity < 1 ||
            options.LedgerCapacity < 1 ||
            options.OrderEventCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        options.ExecutionModel.Validate();
        options.Financing.Validate();
    }
}
