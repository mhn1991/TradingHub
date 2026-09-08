using System.Reflection;
using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoBuyExhaustionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static IndicatorSnapshot Hot => new() { BollingerUpper = 102, Rsi = 70, Cci = 100 };

    [TestCase(102, 70, 100, true)]
    [TestCase(102, 70, 99.99, false)]
    [TestCase(102, 70.01, 0, true)]
    [TestCase(102, 30, 100, true)]
    [TestCase(101.99, 90, 200, false)]
    [TestCase(103, 90, 200, true)]
    public void ExactThresholdsAndOr(double high, double rsi, double cci, bool expected) =>
        Assert.That(AlfonsoBuyExhaustion.Matches((decimal)high,
            Hot with { Rsi = (decimal)rsi, Cci = (decimal)cci }), Is.EqualTo(expected));

    [Test]
    public void MissingIndicatorsCannotSatisfyThresholds()
    {
        Assert.That(AlfonsoBuyExhaustion.Matches(102, Hot with { BollingerUpper = null }), Is.False);
        Assert.That(AlfonsoBuyExhaustion.Matches(102, Hot with { Rsi = null, Cci = null }), Is.False);
        Assert.That(AlfonsoBuyExhaustion.Matches(102, Hot with { Rsi = null }), Is.True);
        Assert.That(AlfonsoBuyExhaustion.Matches(102, Hot with { Rsi = 71, Cci = null }), Is.True);
    }

    [Test]
    public void OnlyCurrentClosedFiveMinuteSnapshotIsAccepted()
    {
        var context = AlfonsoLowerAlignmentTests.Context(Start, Start.AddMinutes(-5), fiveIndicators: Hot);
        context.Analysis.TryGet(BarInterval.Minutes(5), out var snapshot);
        Assert.That(AlfonsoBuyExhaustion.Reason(snapshot, Start), Does.Contain("CCI=100"));
        Assert.That(AlfonsoBuyExhaustion.Reason(snapshot, Start.AddMinutes(-1)), Is.Null);
        Assert.That(AlfonsoBuyExhaustion.Reason(snapshot, Start.AddMinutes(5)), Is.Null);
        Assert.That(AlfonsoBuyExhaustion.Reason(snapshot with { AvailableAt = Start.AddSeconds(1) }, Start), Is.Null);
        Assert.That(AlfonsoBuyExhaustion.Reason(snapshot with { Interval = BarInterval.Minutes(15) }, Start), Is.Null);
    }

    [Test]
    public void ConfigurationIsOptInAndRequiresFiveMinutePolicy()
    {
        Assert.That(new AlfonsoStrategyOptions().BlockExhaustedBuysOnFiveMinute, Is.False);
        Assert.Throws<InvalidOperationException>(() => new AlfonsoStrategyOptions { BlockExhaustedBuysOnFiveMinute = true }.Validate());
        var request = new BacktestRequest { Instrument = new("METAL:XAG/USD"), From = Start, To = Start.AddDays(1),
            AlfonsoEntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned, AlfonsoBlockExhaustedBuysOnFiveMinute = true };
        var options = request.ResolveAgentDefinition("alfonso").Alfonso!;
        Assert.That(options.BlockExhaustedBuysOnFiveMinute, Is.True);
        Assert.DoesNotThrow(options.Validate);
    }

    private static AnalysisSnapshot Snapshot(DateTimeOffset at, IndicatorSnapshot indicators)
    {
        var context = AlfonsoLowerAlignmentTests.Context(at, at.AddMinutes(-5), fiveIndicators: indicators);
        context.Analysis.TryGet(BarInterval.Minutes(5), out var snapshot);
        return snapshot;
    }

    [Test]
    public void PreviousWarningPersistsForExactlyOneConsecutiveCandle()
    {
        var filter = new AlfonsoBuyExhaustion();
        var hot = Snapshot(Start, Hot);
        var reason = filter.Evaluate(hot, Start);
        Assert.That(reason, Is.Not.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(5), new()), Start.AddMinutes(5)), Is.EqualTo(reason));
        // Duplicate evaluation must not rotate history or extend its lifetime.
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(5), new()), Start.AddMinutes(5)), Is.EqualTo(reason));
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(10), new()), Start.AddMinutes(10)), Is.Null);
    }

    [Test]
    public void CurrentCandleCanTriggerAfterQuietPreviousCandle()
    {
        var filter = new AlfonsoBuyExhaustion();
        Assert.That(filter.Evaluate(Snapshot(Start, new()), Start), Is.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(5), Hot), Start.AddMinutes(5)),
            Does.Contain($"candle={Start:O}"));
    }

    [Test]
    public void BandTouchAndOscillatorMustBelongToTheSameCandle()
    {
        var filter = new AlfonsoBuyExhaustion();
        Assert.That(filter.Evaluate(Snapshot(Start, Hot with { Rsi = 50, Cci = 0 }), Start), Is.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(5), Hot with { BollingerUpper = 1000 }),
            Start.AddMinutes(5)), Is.Null);
    }

    [Test]
    public void GapDoesNotCarryPreviousWarning()
    {
        var filter = new AlfonsoBuyExhaustion();
        Assert.That(filter.Evaluate(Snapshot(Start, Hot), Start), Is.Not.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(10), new()), Start.AddMinutes(10)), Is.Null);
    }

    [Test]
    public void InvalidAndOutOfOrderSnapshotsCannotPolluteHistory()
    {
        var filter = new AlfonsoBuyExhaustion();
        var hot = Snapshot(Start, Hot);
        Assert.That(filter.Evaluate(hot with { AvailableAt = Start.AddSeconds(1) }, Start), Is.Null);
        Assert.That(filter.Evaluate(hot, Start.AddMinutes(-1)), Is.Null);
        Assert.That(filter.Evaluate(hot with { Interval = BarInterval.Minutes(15) }, Start), Is.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(5), new()), Start.AddMinutes(5)), Is.Null);
        Assert.That(filter.Evaluate(hot, Start), Is.Null);
        Assert.That(filter.Evaluate(Snapshot(Start.AddMinutes(10), new()), Start.AddMinutes(10)), Is.Null);
    }

    [TestCase(true, true, false, OrderSide.Buy, OrderStatus.Open, AgentAction.Cancel)]
    [TestCase(true, true, false, OrderSide.Buy, OrderStatus.Pending, AgentAction.Cancel)]
    [TestCase(false, true, false, OrderSide.Buy, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, false, false, OrderSide.Buy, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, true, true, OrderSide.Buy, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, true, false, OrderSide.Buy, OrderStatus.PartiallyFilled, AgentAction.Observe)]
    [TestCase(true, true, false, OrderSide.Buy, OrderStatus.Filled, AgentAction.Observe)]
    [TestCase(true, true, false, OrderSide.Sell, OrderStatus.Open, AgentAction.Observe)]
    [TestCase(true, true, false, OrderSide.Buy, OrderStatus.Open, AgentAction.Cancel, true)]
    public async Task CancelsOnlyOwnedUnfilledBuysBetweenEntryCloses(
        bool enabled, bool owns, bool filled, OrderSide side, OrderStatus status, AgentAction expected,
        bool previousOnly = false)
    {
        var agent = new AlfonsoAgent(new() { EntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned,
            BlockExhaustedBuysOnFiveMinute = enabled });
        var context = AlfonsoLowerAlignmentTests.Context(Start, Start.AddMinutes(-5),
            fiveIndicators: previousOnly ? Hot : null);
        await agent.EvaluateAsync(context);
        if (owns)
        {
            var states = typeof(AlfonsoAgent).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent)!;
            var state = states.GetType().GetProperty("Item")!.GetValue(states, [context.Instrument])!;
            var keyType = typeof(AlfonsoAgent).GetNestedType("ZoneOrderKey", BindingFlags.NonPublic)!;
            var key = Activator.CreateInstance(keyType, SequenceRole.Lower, Start,
                side == OrderSide.Buy ? ImbalanceKind.Demand : ImbalanceKind.Supply);
            state.GetType().GetProperty("PendingOrder")!.SetValue(state, key);
        }
        var next = AlfonsoLowerAlignmentTests.Context(Start.AddMinutes(5), Start, fiveIndicators: previousOnly ? null : Hot) with
        {
            OpenOrders = [new BrokerOrder { BrokerOrderId = "pending", Instrument = context.Instrument,
                Side = side, Type = "Limit", Status = status.ToString(), NormalizedStatus = status }],
            Positions = filled ? [new BrokerPosition { PositionId = "filled", Instrument = context.Instrument,
                Side = side, Quantity = 1 }] : []
        };
        var decision = await agent.EvaluateAsync(next);
        Assert.That(decision.Action, Is.EqualTo(expected));
        if (expected == AgentAction.Cancel)
        {
            Assert.That(decision.BrokerOrderId, Is.EqualTo("pending"));
            Assert.That(decision.Reason, Does.StartWith("5m buy exhaustion:"));
        }
    }
}
