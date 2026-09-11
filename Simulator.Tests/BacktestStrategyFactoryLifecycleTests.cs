using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using RiskManager.Conditions;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

[TestFixture]
public sealed class BacktestStrategyFactoryLifecycleTests
{
    [Test]
    public async Task CompletionReusesTheExecutedAgentsWithoutCallingTheFactoryAgain()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), "alfonso-factory-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int calls = 0;
        try
        {
            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(Path.Combine(root, "jobs")),
                new BacktestApplicationServiceOptions
                {
                    MaxConcurrentJobs = 1,
                    StrategyFactory = _ =>
                    {
                        Assert.That(++calls, Is.EqualTo(1), "Finalization must not construct another agent.");
                        return [new StrategyFactoryEntry("single-use", new NoTradeAgent(interval), instrument)];
                    }
                });
            var result = await service.RunToCompletionAsync(new BacktestRequest
            {
                Instrument = instrument, From = start, To = start.AddMinutes(10),
                OutputDirectory = Path.Combine(root, "out"), JobsDirectory = Path.Combine(root, "jobs"),
                InlineCandles = Enumerable.Range(0, 10).Select(i => TestCandles.Create(
                    instrument, start.AddMinutes(i), interval, 1.1m, 1.1m, 1.1m, 1.1m)).ToArray(),
                Runtime = new BacktestRuntimeOptions
                {
                    BaseInterval = interval, AnalysisBaseInterval = interval, AnalysisIntervals = [interval],
                    WarmupDays = 0, TradingConditions = new TradingConditionOptions { Enabled = false },
                    AnnotationOptions = new ChartAnnotationOptions { AtrPeriod = 3, RsiPeriod = 3, BollingerPeriod = 3 }
                }
            });
            var snapshot = await service.GetAsync(result.SimulationId);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(snapshot!.Status, Is.EqualTo(SimulationJobStatus.Completed));
            Assert.That(snapshot.Strategies.Single().Instrument, Is.EqualTo(instrument.Value));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NoTradeAgent(BarInterval interval) : ITradingAgent
    {
        public string Name => "Factory lifecycle test";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;
        public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe, Instrument = context.Instrument,
                Confidence = 0m, CreatedAt = context.Timestamp, Reason = "No setup"
            });
    }
}
