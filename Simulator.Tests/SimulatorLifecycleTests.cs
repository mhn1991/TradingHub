using Brokers.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorLifecycleTests
{
    [Test]
    public async Task DisposingBroker_CompletesWaitingOrderEventStream()
    {
        var harness = new SimulationTestHarness();
        await using IAsyncEnumerator<OrderEvent> events = harness.Broker.Orders
            .StreamOrderEventsAsync()
            .GetAsyncEnumerator();
        Task<bool> pendingRead = events.MoveNextAsync().AsTask();

        await harness.DisposeAsync();

        Assert.That(await pendingRead, Is.False);
    }

    [Test]
    public async Task BrokerDisposal_IsIdempotent()
    {
        var harness = new SimulationTestHarness();

        await harness.DisposeAsync();

        Assert.That(async () => await harness.DisposeAsync(), Throws.Nothing);
    }

    [Test]
    public async Task PublicClientsRejectOperationsAfterBrokerDisposal()
    {
        var harness = new SimulationTestHarness();
        await harness.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await harness.Broker.Accounts.GetAccountsAsync(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                async () => await harness.Broker.Positions.GetOpenPositionsAsync(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                async () => await harness.Broker.Orders.GetOpenOrdersAsync(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                async () => await harness.Broker.Costs.GetCommissionAsync(
                    SimulationTestHarness.DefaultInstrument),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                async () => await harness.Broker.MarketData.GetCandlesAsync(new CandleQuery(
                    SimulationTestHarness.DefaultInstrument,
                    SimulationTestHarness.DefaultInterval)),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public async Task RuntimeRejectsProcessingAfterBrokerDisposal()
    {
        var harness = new SimulationTestHarness();
        await harness.DisposeAsync();
        Candle candle = TestCandles.Create(
            SimulationTestHarness.DefaultInstrument,
            SimulationTestHarness.DefaultStart,
            SimulationTestHarness.DefaultInterval,
            100m,
            101m,
            99m,
            100m);

        Assert.That(
            async () => await harness.Broker.Runtime.ProcessExecutionCandleAsync(candle),
            Throws.TypeOf<ObjectDisposedException>());
    }
}
