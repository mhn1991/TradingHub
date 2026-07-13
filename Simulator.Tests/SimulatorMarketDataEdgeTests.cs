using Brokers.Abstractions;
using Brokers.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorMarketDataEdgeTests
{
    [Test]
    public async Task InitialAccountAndDescriptorReflectOptions()
    {
        await using var harness = new SimulationTestHarness(new Simulator.Models.SimulationOptions
        {
            ModelledBroker = BrokerKind.Ig,
            AccountId = "backtest-account",
            BaseCurrency = "USD",
            StartingBalance = 12_345m
        });

        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Broker.Descriptor.Kind, Is.EqualTo(BrokerKind.Ig));
            Assert.That(harness.Broker.Descriptor.Environment, Is.EqualTo(BrokerEnvironment.Demo));
            Assert.That(harness.Broker.Descriptor.AccountId, Is.EqualTo("backtest-account"));
            Assert.That(account.AccountId, Is.EqualTo("backtest-account"));
            Assert.That(account.Balance, Is.EqualTo(12_345m));
            Assert.That(account.Available, Is.EqualTo(12_345m));
            Assert.That(account.CanTrade, Is.True);
        });
    }

    [Test]
    public async Task MarketData_QueryAppliesInstrumentIntervalRangeAndLimit()
    {
        await using var harness = new SimulationTestHarness();
        for (int index = 0; index < 4; index++)
        {
            await harness.ProcessAsync(index, 100m + index, 101m + index, 99m + index, 100m + index);
        }

        DateTimeOffset start = SimulationTestHarness.DefaultStart;
        IReadOnlyList<Candle> result = await harness.Broker.MarketData.GetCandlesAsync(new CandleQuery(
            SimulationTestHarness.DefaultInstrument,
            SimulationTestHarness.DefaultInterval,
            Limit: 2,
            From: start.AddMinutes(5),
            To: start.AddMinutes(15)));

        Assert.That(
            result.Select(candle => candle.OpenTime),
            Is.EqualTo(new[] { start.AddMinutes(10), start.AddMinutes(15) }));
    }

    [Test]
    public async Task MarketData_UnknownChartReturnsEmpty()
    {
        await using var harness = new SimulationTestHarness();

        IReadOnlyList<Candle> result = await harness.Broker.MarketData.GetCandlesAsync(
            new CandleQuery("FX:EUR/USD", SimulationTestHarness.DefaultInterval));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task MarketData_InvalidQueryIsRejected()
    {
        await using var harness = new SimulationTestHarness(new Simulator.Models.SimulationOptions
        {
            CandleCapacity = 2
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await harness.Broker.MarketData.GetCandlesAsync(new CandleQuery(
                    default,
                    SimulationTestHarness.DefaultInterval)),
                Throws.ArgumentException);
            Assert.That(
                async () => await harness.Broker.MarketData.GetCandlesAsync(new CandleQuery(
                    SimulationTestHarness.DefaultInstrument,
                    SimulationTestHarness.DefaultInterval,
                    Limit: 3)),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public async Task CostScheduleReflectsConfiguredCommission()
    {
        await using var harness = new SimulationTestHarness(new Simulator.Models.SimulationOptions
        {
            CommissionRate = 0.0025m
        });

        CommissionSchedule result = await harness.Broker.Costs.GetCommissionAsync(
            SimulationTestHarness.DefaultInstrument);

        Assert.Multiple(() =>
        {
            Assert.That(result.Maker, Is.EqualTo(0.0025m));
            Assert.That(result.Taker, Is.EqualTo(0.0025m));
            Assert.That(result.RateUnit, Is.EqualTo(CommissionRateUnit.Fraction));
        });
    }

    [TestCase(0, 100, 99, 100)]
    [TestCase(100, 99, 100, 100)]
    [TestCase(100, 101, 102, 100)]
    [TestCase(100, 101, 99, 0)]
    public async Task InvalidExecutionOhlc_IsRejected(
        decimal open,
        decimal high,
        decimal low,
        decimal close)
    {
        await using var harness = new SimulationTestHarness();

        Assert.That(
            async () => await harness.ProcessAsync(0, open, high, low, close),
            Throws.ArgumentException.With.Message.Contains("OHLC"));
    }

    [Test]
    public async Task CancelledExecution_DoesNotAdvanceMarketOrStoreCandle()
    {
        await using var harness = new SimulationTestHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Candle candle = TestCandles.Create(
            SimulationTestHarness.DefaultInstrument,
            SimulationTestHarness.DefaultStart,
            SimulationTestHarness.DefaultInterval,
            100m,
            101m,
            99m,
            100m);

        Assert.That(
            async () => await harness.Broker.Runtime.ProcessExecutionCandleAsync(
                candle,
                cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.Multiple(() =>
        {
            Assert.That(harness.Broker.State.MarketSequence, Is.Zero);
            Assert.That(harness.Broker.State.GetCandles(new CandleQuery(
                SimulationTestHarness.DefaultInstrument,
                SimulationTestHarness.DefaultInterval)), Is.Empty);
        });
    }
}
