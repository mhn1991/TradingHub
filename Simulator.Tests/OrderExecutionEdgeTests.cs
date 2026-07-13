using Brokers.Models;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class OrderExecutionEdgeTests
{
    [TestCase(OrderSide.Buy, StandardOrderType.Limit, 100, null, 95, 101, 94, 96, 95)]
    [TestCase(OrderSide.Buy, StandardOrderType.Limit, 100, null, 105, 106, 99, 104, 100)]
    [TestCase(OrderSide.Buy, StandardOrderType.Limit, 100, null, 105, 106, 101, 104, null)]
    [TestCase(OrderSide.Sell, StandardOrderType.Limit, 100, null, 105, 106, 104, 105, 105)]
    [TestCase(OrderSide.Sell, StandardOrderType.Limit, 100, null, 95, 101, 94, 96, 100)]
    [TestCase(OrderSide.Sell, StandardOrderType.Limit, 100, null, 95, 99, 94, 96, null)]
    [TestCase(OrderSide.Buy, StandardOrderType.Stop, null, 100, 105, 106, 104, 105, 105)]
    [TestCase(OrderSide.Buy, StandardOrderType.Stop, null, 100, 95, 101, 94, 96, 100)]
    [TestCase(OrderSide.Buy, StandardOrderType.Stop, null, 100, 95, 99, 94, 96, null)]
    [TestCase(OrderSide.Sell, StandardOrderType.Stop, null, 100, 95, 96, 94, 95, 95)]
    [TestCase(OrderSide.Sell, StandardOrderType.Stop, null, 100, 105, 106, 99, 104, 100)]
    [TestCase(OrderSide.Sell, StandardOrderType.Stop, null, 100, 105, 106, 101, 104, null)]
    public async Task ConditionalOrder_UsesExpectedOhlcExecutionRule(
        OrderSide side,
        StandardOrderType type,
        decimal? limit,
        decimal? stop,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal? expectedFillPrice)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(
            side: side,
            type: type,
            limitPrice: limit,
            stopPrice: stop);

        await harness.ProcessAsync(0, open, high, low, close);

        IReadOnlyList<BrokerPosition> positions =
            await harness.Broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> orders = await harness.Broker.Orders.GetOpenOrdersAsync();
        if (expectedFillPrice is null)
        {
            Assert.Multiple(() =>
            {
                Assert.That(positions, Is.Empty);
                Assert.That(orders, Has.Count.EqualTo(1));
                Assert.That(orders[0].NormalizedStatus, Is.EqualTo(OrderStatus.Open));
            });
        }
        else
        {
            BrokerPosition position = positions.Single();
            Assert.Multiple(() =>
            {
                Assert.That(position.Side, Is.EqualTo(side));
                Assert.That(position.AveragePrice, Is.EqualTo(expectedFillPrice));
                Assert.That(orders, Is.Empty);
            });
        }
    }

    [TestCase(OrderSide.Buy, 100.1)]
    [TestCase(OrderSide.Sell, 99.9)]
    public async Task MarketOrder_AppliesAdverseSpreadAndSlippage(
        OrderSide side,
        decimal expectedPrice)
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            CommissionRate = 0m,
            SpreadBasisPoints = 10m,
            SlippageBasisPoints = 5m
        });
        await harness.PlaceAsync(side: side);

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.That(position.AveragePrice, Is.EqualTo(expectedPrice));
    }

    [TestCase(OrderSide.Buy)]
    [TestCase(OrderSide.Sell)]
    public async Task LimitOrder_NeverExecutesWorseThanLimitAfterCosts(OrderSide side)
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            CommissionRate = 0m,
            SpreadBasisPoints = 10m,
            SlippageBasisPoints = 5m
        });
        await harness.PlaceAsync(
            side: side,
            type: StandardOrderType.Limit,
            limitPrice: 100m);

        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.That(position.AveragePrice, Is.EqualTo(100m));
    }

    [Test]
    public async Task StopOrder_AppliesCostsBeyondStopPrice()
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            CommissionRate = 0m,
            SpreadBasisPoints = 10m,
            SlippageBasisPoints = 5m
        });
        await harness.PlaceAsync(type: StandardOrderType.Stop, stopPrice: 100m);

        await harness.ProcessAsync(0, 95m, 101m, 94m, 100m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.That(position.AveragePrice, Is.EqualTo(100.1m));
    }

    [Test]
    public async Task StopLimit_TriggersWithoutFillingUntilLaterCandle()
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(
            type: StandardOrderType.StopLimit,
            stopPrice: 101m,
            limitPrice: 100m);

        await harness.ProcessAsync(0, 100m, 102m, 99m, 101m);

        BrokerOrder triggered = (await harness.Broker.Orders.GetOpenOrdersAsync()).Single();
        IReadOnlyList<OrderEvent> triggerEvents = await harness.ReadEventsAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(triggered.Status, Is.EqualTo("TRIGGERED"));
            Assert.That(triggered.NormalizedStatus, Is.EqualTo(OrderStatus.Open));
            Assert.That(triggerEvents.Select(item => item.Type),
                Is.EqualTo(new[] { OrderEventType.Accepted, OrderEventType.Triggered }));
        });

        await harness.ProcessAsync(1, 100m, 101m, 99m, 100m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.That(position.AveragePrice, Is.EqualTo(100m));
    }

    [Test]
    public async Task NewOrder_CannotFillOnAlreadyProcessedCandle()
    {
        await using var harness = new SimulationTestHarness();
        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);

        await harness.PlaceAsync();

        Assert.That(await harness.Broker.Positions.GetOpenPositionsAsync(), Is.Empty);
        await harness.ProcessAsync(1, 110m, 111m, 109m, 110m);
        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.That(position.AveragePrice, Is.EqualTo(110m));
    }

    [Test]
    public async Task AttachedExits_AreCreatedOnlyAfterEntryFill()
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(stopLoss: 95m, takeProfit: 105m, clientOrderId: "entry");

        IReadOnlyList<BrokerOrder> beforeFill = await harness.Broker.Orders.GetOpenOrdersAsync();
        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);
        IReadOnlyList<BrokerOrder> afterFill = await harness.Broker.Orders.GetOpenOrdersAsync();

        Assert.Multiple(() =>
        {
            Assert.That(beforeFill, Has.Count.EqualTo(1));
            Assert.That(afterFill, Has.Count.EqualTo(2));
            Assert.That(afterFill.Select(order => order.ClientOrderId),
                Is.EquivalentTo(new[] { "entry-SL", "entry-TP" }));
            Assert.That(afterFill.All(order => order.NormalizedStatus == OrderStatus.Open), Is.True);
        });
    }

    [Test]
    public async Task OpenOrderFilter_ReturnsOnlyRequestedInstrument()
    {
        InstrumentKey other = new("FX:EUR/USD");
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(type: StandardOrderType.Limit, limitPrice: 90m);
        await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m,
            instrument: other);

        IReadOnlyList<BrokerOrder> filtered =
            await harness.Broker.Orders.GetOpenOrdersAsync(other);

        Assert.That(filtered.Single().Instrument, Is.EqualTo(other));
    }

    [Test]
    public async Task CancelOrder_EmitsEventAndCannotBeRepeated()
    {
        await using var harness = new SimulationTestHarness();
        OrderSubmission submission = await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m,
            clientOrderId: "cancel-me");

        await harness.Broker.Orders.CancelOrderAsync(submission.BrokerOrderId!);

        IReadOnlyList<OrderEvent> events = await harness.ReadEventsAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(item => item.Type),
                Is.EqualTo(new[] { OrderEventType.Accepted, OrderEventType.Cancelled }));
            Assert.That(events[1].ClientOrderId, Is.EqualTo("cancel-me"));
        });
        Assert.That(
            async () => await harness.Broker.Orders.CancelOrderAsync(submission.BrokerOrderId!),
            Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public async Task CancellationToken_PreventsOrderOperations()
    {
        await using var harness = new SimulationTestHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await harness.Broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
            {
                Instrument = SimulationTestHarness.DefaultInstrument,
                Side = OrderSide.Buy,
                Type = StandardOrderType.Market,
                Quantity = new OrderQuantity(1m, QuantityUnit.Units)
            }, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }
}
