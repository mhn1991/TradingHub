using TradingHub.Abstractions.MarketData;
using TradingHub.Simulation.Engine;
using TradingHub.Simulation.Results;

namespace TradingHub.Simulation.Runner;

internal sealed record SimulationRuntime
{
    public required string RunId { get; init; }

    public required string OutputDirectory { get; init; }

    public required SimulationEngine Engine { get; init; }

    public required IMarketDataSource MarketDataSource { get; init; }

    public required SimulationResultWriter ResultWriter { get; init; }

    public async Task<SimulationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var result = await Engine.RunAsync(RunId, MarketDataSource, cancellationToken);
        await ResultWriter.WriteAsync(result, OutputDirectory, cancellationToken);
        return result;
    }
}
