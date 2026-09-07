using System.Reflection;
using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoPendingFiveMinuteTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static AlfonsoBar Bar(int n, decimal low, decimal high, decimal close = 100) =>
        new(Start.AddMinutes(n * 5), 100, high, low, close);

    [TestCase(true)]
    [TestCase(false)]
    public void ConfirmedSwingBreakIsCausalAndMirrored(bool buy)
    {
        var structure = new AlfonsoFiveMinuteStructure();
        for (int i = 0; i < 4; i++) structure.Apply(Bar(i, i == 2 ? 95 : 98, i == 2 ? 105 : 102), Start.AddMinutes((i + 1) * 5));
        Assert.That(structure.Low, Is.Null);
        Assert.That(structure.High, Is.Null);
        Assert.That(structure.Apply(Bar(4, 98, 102), Start.AddMinutes(24)), Is.False);
        structure.Apply(Bar(4, 98, 102), Start.AddMinutes(25));
        Assert.That(structure.Low!.Value.OpenTime, Is.EqualTo(Start.AddMinutes(10)));
        Assert.That(structure.Apply(Bar(4, 90, 110), Start.AddMinutes(25)), Is.False);
        var trend = buy ? AlfonsoTrend.Uptrend : AlfonsoTrend.Downtrend;
        structure.Apply(Bar(5, 94, 106, buy ? 95 : 105), Start.AddMinutes(30));
        Assert.That(structure.PendingInvalidation(buy, trend), Is.Null, "Touch/wick alone is not a close break.");
        structure.Apply(Bar(6, 94, 106, buy ? 94 : 106), Start.AddMinutes(35));
        Assert.That(structure.PendingInvalidation(buy, trend), Does.Contain("broke confirmed"));
        Assert.That(structure.PendingInvalidation(buy, AlfonsoTrend.OutOfAlignment), Does.Contain("no longer agrees"));
    }

    [Test]
    public void ConfigurationIsOptInAndRequiresFiveMinutePolicy()
    {
        Assert.That(new AlfonsoStrategyOptions().RevalidatePendingOnFiveMinute, Is.False);
        Assert.Throws<InvalidOperationException>(() => new AlfonsoStrategyOptions { RevalidatePendingOnFiveMinute = true }.Validate());
        var request = new BacktestRequest { Instrument = new("METAL:XAG/USD"), From = Start, To = Start.AddDays(1),
            AlfonsoEntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned, AlfonsoRevalidatePendingOnFiveMinute = true };
        Assert.That(request.ResolveAgentDefinition("alfonso").Alfonso!.RevalidatePendingOnFiveMinute, Is.True);
    }

    [TestCase(true, true, false, OrderStatus.Open, AgentAction.Cancel)]
    [TestCase(false, true, false, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, false, false, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, true, true, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, true, false, OrderStatus.PartiallyFilled, AgentAction.Observe)]
    [TestCase(true, true, false, OrderStatus.Filled, AgentAction.Observe)]
    public async Task GuardRunsBetweenEntryClosesButNeverManagesFilledOrUnownedOrders(
        bool enabled, bool owns, bool filled, OrderStatus status, AgentAction expected)
    {
        var agent = new AlfonsoAgent(new() { EntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned,
            RevalidatePendingOnFiveMinute = enabled });
        var context = AlfonsoLowerAlignmentTests.Context(Start, Start.AddMinutes(-5));
        await agent.EvaluateAsync(context);
        if (owns)
        {
            // Seed the private pending plan, independent of zone construction/trend fixtures.
            var states = typeof(AlfonsoAgent).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent)!;
            var state = states.GetType().GetProperty("Item")!.GetValue(states, [context.Instrument])!;
            var keyType = typeof(AlfonsoAgent).GetNestedType("ZoneOrderKey", BindingFlags.NonPublic)!;
            var key = Activator.CreateInstance(keyType, SequenceRole.Lower, Start, ImbalanceKind.Demand);
            state.GetType().GetProperty("PendingOrder")!.SetValue(state, key);
        }
        var next = AlfonsoLowerAlignmentTests.Context(Start.AddMinutes(5), Start) with
        {
            OpenOrders = [new BrokerOrder { BrokerOrderId = "pending", Instrument = context.Instrument,
                Side = OrderSide.Buy, Type = "Limit", Status = status.ToString(), NormalizedStatus = status }],
            Positions = filled ? [new BrokerPosition { PositionId = "filled", Instrument = context.Instrument,
                Side = OrderSide.Buy, Quantity = 1 }] : []
        };
        var decision = await agent.EvaluateAsync(next);
        Assert.That(decision.Action, Is.EqualTo(expected));
        if (expected == AgentAction.Cancel)
        {
            Assert.That(decision.BrokerOrderId, Is.EqualTo("pending"));
            Assert.That(decision.Reason, Does.StartWith("5m pending guard:"));
        }
    }
}
