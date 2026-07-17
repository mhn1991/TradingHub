using Brokers.Models;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using NUnit.Framework;

namespace LiveTrading.Tests.Shadow;

[TestFixture]
public sealed class ShadowBrokerClientTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    [Test]
    public async Task Reads_DelegateToTheWrappedBrokerExactly()
    {
        var inner = new FakeBrokerClient
        {
            Accounts_Value = [new AccountSnapshot { AccountId = "acct-1", Balance = 5_000m }],
            Positions_Value = [new BrokerPosition
            {
                PositionId = "pos-1", Instrument = Instrument, Side = OrderSide.Buy,
                Quantity = 1_000m, AveragePrice = 1.1m
            }],
            OpenOrders_Value = [new BrokerOrder
            {
                BrokerOrderId = "order-1", Instrument = Instrument, Side = OrderSide.Buy,
                Type = "MARKET", Status = "OPEN", NormalizedStatus = OrderStatus.Open, Quantity = 1_000m
            }]
        };
        var shadow = new ShadowBrokerClient(inner);

        IReadOnlyList<AccountSnapshot> accounts = await shadow.Accounts.GetAccountsAsync(CancellationToken.None);
        IReadOnlyList<BrokerPosition> positions = await shadow.Positions.GetOpenPositionsAsync(CancellationToken.None);
        IReadOnlyList<BrokerOrder> orders = await shadow.Orders.GetOpenOrdersAsync(cancellationToken: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(accounts, Is.EqualTo(inner.Accounts_Value));
            Assert.That(positions, Is.EqualTo(inner.Positions_Value));
            Assert.That(orders, Is.EqualTo(inner.OpenOrders_Value));
            Assert.That(shadow.Descriptor, Is.EqualTo(inner.Descriptor));
        });
    }

    [Test]
    public void PlaceOrderAsync_Throws()
    {
        var shadow = new ShadowBrokerClient(new FakeBrokerClient());
        var request = new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1_000m, QuantityUnit.Units)
        };

        Assert.ThrowsAsync<InvalidOperationException>(async () => await shadow.Orders.PlaceOrderAsync(request));
    }

    [Test]
    public void CancelOrderAsync_Throws()
    {
        var shadow = new ShadowBrokerClient(new FakeBrokerClient());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await shadow.Orders.CancelOrderAsync("order-1"));
    }

    [Test]
    public void StreamOrderEventsAsync_Throws()
    {
        var shadow = new ShadowBrokerClient(new FakeBrokerClient());
        Assert.Throws<InvalidOperationException>(() => shadow.Orders.StreamOrderEventsAsync());
    }
}
