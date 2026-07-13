using Brokers.Models;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class NearestToOpenAndFailurePolicyTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task NearestToOpen_WhenTargetNearer_FillsTarget()
    {
        // Entry ~100; stop 90 (dist 10); target 101 (dist 1). Both touched → target.
        decimal balance = await RunAmbiguousOcoAsync(
            OcoFillPolicy.NearestToOpenFirst,
            stop: 90m,
            target: 101m,
            ambiguousOpen: 100m,
            ambiguousHigh: 110m,
            ambiguousLow: 80m);

        // Target fill at ~101 on long 1 unit from 100 → profit ~1 before costs (0).
        Assert.That(balance, Is.EqualTo(1_001m));
    }

    [Test]
    public async Task NearestToOpen_WhenStopNearer_FillsStop()
    {
        // stop 99 (dist 1); target 110 (dist 10). Both touched → stop.
        decimal balance = await RunAmbiguousOcoAsync(
            OcoFillPolicy.NearestToOpenFirst,
            stop: 99m,
            target: 110m,
            ambiguousOpen: 100m,
            ambiguousHigh: 111m,
            ambiguousLow: 98m);

        Assert.That(balance, Is.EqualTo(999m));
    }

    [Test]
    public async Task NearestToOpen_ExactTie_PrefersStop()
    {
        // stop 95 (dist 5); target 105 (dist 5). Tie → stop-first.
        decimal balance = await RunAmbiguousOcoAsync(
            OcoFillPolicy.NearestToOpenFirst,
            stop: 95m,
            target: 105m,
            ambiguousOpen: 100m,
            ambiguousHigh: 110m,
            ambiguousLow: 90m);

        Assert.That(balance, Is.EqualTo(995m));
    }

    [Test]
    public void Runtime_Maps_NearestToOpen_Policy()
    {
        var runtime = new BacktestRuntimeOptions
        {
            AmbiguousIntrabarPolicy = AmbiguousIntrabarPolicy.NearestToOpenFirst
        };
        Assert.That(runtime.ToOcoFillPolicy(), Is.EqualTo(OcoFillPolicy.NearestToOpenFirst));
    }

    private static async Task<decimal> RunAmbiguousOcoAsync(
        OcoFillPolicy policy,
        decimal stop,
        decimal target,
        decimal ambiguousOpen,
        decimal ambiguousHigh,
        decimal ambiguousLow)
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = new SimulatedBrokerClient(new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            OcoFillPolicy = policy
        }, clock);

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units),
            StopLoss = new StopLossInstruction(stop),
            TakeProfit = new TakeProfitInstruction(target),
            ClientOrderId = "oco-entry"
        });

        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start, Interval, 100m, 101m, 99m, 100m));
        clock.AdvanceTo(Start.AddMinutes(10));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(
                Instrument,
                Start.AddMinutes(5),
                Interval,
                ambiguousOpen,
                ambiguousHigh,
                ambiguousLow,
                ambiguousOpen));

        AccountSnapshot account = (await broker.Accounts.GetAccountsAsync()).Single();
        Assert.That(await broker.Positions.GetOpenPositionsAsync(), Is.Empty);
        return account.Balance ?? 0m;
    }
}
