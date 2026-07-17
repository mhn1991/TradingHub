using Brokers.Abstractions;
using Brokers.Models;

namespace LiveTrading.Tests.Fakes;

/// <summary>Minimal, fully in-memory <see cref="IBrokerClient"/> test double. Read members return
/// whatever was last assigned; write-capable members throw by default so a test can assert "never
/// called" simply by never overriding them - matching the same fail-safe philosophy as <see
/// cref="LiveTrading.Shadow.ShadowBrokerClient"/> itself.</summary>
public sealed class FakeBrokerClient : IBrokerClient
{
    public BrokerDescriptor Descriptor { get; init; } = new(BrokerKind.Oanda, BrokerEnvironment.Demo, "test-account");

    public IReadOnlyList<AccountSnapshot> Accounts_Value { get; set; } =
        [new AccountSnapshot { AccountId = "test-account", Balance = 10_000m, Available = 10_000m }];
    public IReadOnlyList<BrokerPosition> Positions_Value { get; set; } = [];
    public IReadOnlyList<BrokerOrder> OpenOrders_Value { get; set; } = [];

    public int PlaceOrderCallCount { get; private set; }
    public int CancelOrderCallCount { get; private set; }

    public IMarketDataClient MarketData { get; } = new ThrowingMarketDataClient();
    public IAccountClient Accounts { get; }
    public IOrderClient Orders { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; } = new FakeCostClient();

    public FakeBrokerClient()
    {
        Accounts = new FakeAccountClient(this);
        Orders = new FakeOrderClient(this);
        Positions = new FakePositionClient(this);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FakeAccountClient(FakeBrokerClient owner) : IAccountClient
    {
        public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.Accounts_Value);
    }

    private sealed class FakePositionClient(FakeBrokerClient owner) : IPositionClient
    {
        public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.Positions_Value);
    }

    private sealed class FakeOrderClient(FakeBrokerClient owner) : IOrderClient
    {
        public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
            InstrumentKey? instrument = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(instrument is null
                ? owner.OpenOrders_Value
                : (IReadOnlyList<BrokerOrder>)owner.OpenOrders_Value.Where(o => o.Instrument == instrument).ToList());
    }

    private sealed class FakeCostClient : ICostClient
    {
        public Task<CommissionSchedule> GetCommissionAsync(
            InstrumentKey instrument, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommissionSchedule { Instrument = instrument });
    }

    private sealed class ThrowingMarketDataClient : IMarketDataClient
    {
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(
            CandleQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("FakeBrokerClient.MarketData is not configured for this unit test.");
    }
}
