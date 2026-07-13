using Agent.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulationFactoryEdgeTests
{
    private static readonly InstrumentKey Instrument = SimulationTestHarness.DefaultInstrument;
    private static readonly BarInterval Five = SimulationTestHarness.DefaultInterval;

    [Test]
    public void FactoryRejectsNullDependencies()
    {
        ITradingAgent agent = new RecordingTradingAgent([Five], Five);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => SimulationFactory.CreateHistorical(Instrument, null!, [Five], agent),
                Throws.TypeOf<ArgumentNullException>());
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    null!,
                    agent),
                Throws.TypeOf<ArgumentNullException>());
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    [Five],
                    null!),
                Throws.TypeOf<ArgumentNullException>());
        });
    }

    [Test]
    public void FactoryPropagatesInvalidSimulationAndAnnotationOptions()
    {
        ITradingAgent agent = new RecordingTradingAgent([Five], Five);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    [Five],
                    agent,
                    new SimulationOptions { StartingBalance = 0m }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    [Five],
                    agent,
                    annotationOptions: new ChartAnnotationOptions { AtrPeriod = 1 }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void AgentMustDeclareRequiredAndTriggerIntervalsConsistently()
    {
        BarInterval fifteen = BarInterval.Minutes(15);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    [Five],
                    new RecordingTradingAgent([], Five)),
                Throws.ArgumentException.With.Message.Contains("at least one"));
            Assert.That(
                () => SimulationFactory.CreateHistorical(
                    Instrument,
                    [Candle()],
                    [Five, fifteen],
                    new RecordingTradingAgent([Five], fifteen)),
                Throws.ArgumentException.With.Message.Contains("trigger interval"));
        });
    }

    [Test]
    public async Task SessionDisposalDisposesBroker()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [Candle()],
            [Five],
            agent);

        await session.DisposeAsync();

        Assert.That(
            async () => await session.Broker.Accounts.GetAccountsAsync(),
            Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task DuplicateAnalysisIntervalsAreCollapsed()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [Candle()],
            [Five, Five, Five],
            agent);

        await session.Runner.RunAsync();

        Assert.That(agent.Contexts, Has.Count.EqualTo(1));
        Assert.That(agent.Contexts[0].Analysis.Timeframes, Has.Count.EqualTo(1));
    }

    private static Candle Candle() => TestCandles.Create(
        Instrument,
        SimulationTestHarness.DefaultStart,
        Five,
        100m,
        101m,
        99m,
        100m);
}
