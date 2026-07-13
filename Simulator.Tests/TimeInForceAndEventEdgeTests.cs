using Brokers.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class TimeInForceAndEventEdgeTests
{
    [TestCase(StandardTimeInForce.ImmediateOrCancel)]
    [TestCase(StandardTimeInForce.FillOrKill)]
    public async Task ImmediateTimeInForce_FillsWhenPriceIsAvailable(
        StandardTimeInForce timeInForce)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 100m,
            timeInForce: timeInForce);

        await harness.ProcessAsync(0, 101m, 102m, 99m, 100m);

        IReadOnlyList<BrokerPosition> positions =
            await harness.Broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> orders = await harness.Broker.Orders.GetOpenOrdersAsync();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.Count.EqualTo(1));
            Assert.That(orders, Is.Empty);
        });
    }

    [TestCase(StandardTimeInForce.ImmediateOrCancel)]
    [TestCase(StandardTimeInForce.FillOrKill)]
    public async Task ImmediateTimeInForce_ExpiresAfterFirstMiss(
        StandardTimeInForce timeInForce)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m,
            timeInForce: timeInForce);

        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);

        IReadOnlyList<OrderEvent> events = await harness.ReadEventsAsync(2);
        Assert.That(events[^1].Type, Is.EqualTo(OrderEventType.Expired));
    }

    [Test]
    public async Task DayOrder_RemainsDuringSubmissionDayAndExpiresNextUtcDay()
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m,
            timeInForce: StandardTimeInForce.Day);
        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);
        Assert.That(await harness.Broker.Orders.GetOpenOrdersAsync(), Has.Count.EqualTo(1));

        DateTimeOffset nextDay = SimulationTestHarness.DefaultStart.AddDays(1);
        Candle candle = TestCandles.Create(
            SimulationTestHarness.DefaultInstrument,
            nextDay,
            SimulationTestHarness.DefaultInterval,
            100m,
            101m,
            99m,
            100m);
        harness.Clock.AdvanceTo(candle.CloseTime!.Value);
        await harness.Broker.Runtime.ProcessExecutionCandleAsync(candle);

        Assert.That(await harness.Broker.Orders.GetOpenOrdersAsync(), Is.Empty);
    }

    [TestCase(StandardTimeInForce.GoodTillDate)]
    [TestCase(StandardTimeInForce.GoodTillCancelled)]
    public async Task ExplicitExpiry_ExpiresAtExactTimestamp(StandardTimeInForce timeInForce)
    {
        await using var harness = new SimulationTestHarness();
        DateTimeOffset expiry = SimulationTestHarness.DefaultStart.AddMinutes(10);
        await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m,
            timeInForce: timeInForce,
            expireAt: expiry);

        await harness.ProcessAsync(0, 100m, 101m, 99m, 100m);
        Assert.That(await harness.Broker.Orders.GetOpenOrdersAsync(), Has.Count.EqualTo(1));
        await harness.ProcessAsync(1, 100m, 101m, 99m, 100m);

        Assert.That(await harness.Broker.Orders.GetOpenOrdersAsync(), Is.Empty);
    }

    [Test]
    public async Task FilledEvent_ContainsExecutionDetails()
    {
        await using var harness = new SimulationTestHarness(new Simulator.Models.SimulationOptions
        {
            StartingBalance = 1_000m,
            CommissionRate = 0.01m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        });
        await harness.PlaceAsync(quantity: 2m, clientOrderId: "details");

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        OrderEvent filled = (await harness.ReadEventsAsync(2)).Last();
        Assert.Multiple(() =>
        {
            Assert.That(filled.Type, Is.EqualTo(OrderEventType.Filled));
            Assert.That(filled.ClientOrderId, Is.EqualTo("details"));
            Assert.That(filled.FillPrice, Is.EqualTo(100m));
            Assert.That(filled.FillQuantity, Is.EqualTo(2m));
            Assert.That(filled.RemainingQuantity, Is.Zero);
            Assert.That(filled.Fee, Is.EqualTo(2m));
            Assert.That(filled.Timestamp, Is.EqualTo(harness.Clock.UtcNow));
        });
    }

    [Test]
    public async Task MarginRejectedFill_EmitsAcceptedThenRejected()
    {
        await using var harness = new SimulationTestHarness(
            SimulationTestHarness.ZeroCostOptions(startingBalance: 100m, leverage: 1m));
        await harness.PlaceAsync(quantity: 2m, clientOrderId: "too-large");

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        IReadOnlyList<OrderEvent> events = await harness.ReadEventsAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(item => item.Type),
                Is.EqualTo(new[] { OrderEventType.Accepted, OrderEventType.Rejected }));
            Assert.That(events[1].Message, Does.Contain("Insufficient"));
            Assert.That(events[1].RemainingQuantity, Is.EqualTo(2m));
        });
    }
}
