using Agent.Abstractions;
using Agent.Execution;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using Simulator.Abstractions;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Engine;

public sealed record SimulationSession(
    SimulationRunner Runner,
    SimulatedBrokerClient Broker,
    IChartAnnotator Annotator,
    IAdjustableSimulationClock Clock) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Broker.DisposeAsync();
}

public static class SimulationFactory
{
    public static SimulationSession CreateHistorical(
        InstrumentKey instrument,
        IEnumerable<Candle> baseCandles,
        IEnumerable<BarInterval> analysisIntervals,
        ITradingAgent agent,
        SimulationOptions? simulationOptions = null,
        ChartAnnotationOptions? annotationOptions = null,
        IExecutionCoordinator? executionCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(baseCandles);
        ArgumentNullException.ThrowIfNull(analysisIntervals);
        ArgumentNullException.ThrowIfNull(agent);

        SimulationOptions options = simulationOptions ?? new SimulationOptions();
        var clock = new HistoricalSimulationClock();
        var broker = new SimulatedBrokerClient(options, clock);
        try
        {
            var annotator = new ChartAnnotationEngine(annotationOptions);
            var aggregator = new MultiTimeframeAggregator(
                instrument,
                analysisIntervals,
                options.CandleCapacity);
            var source = new EnumerableCandleSource(baseCandles);
            var execution = executionCoordinator ?? new ExecutionCoordinator();
            var runner = new SimulationRunner(
                source,
                clock,
                aggregator,
                annotator,
                agent,
                execution,
                broker);

            return new SimulationSession(runner, broker, annotator, clock);
        }
        catch
        {
            broker.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }
    }
}
