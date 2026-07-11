namespace TradingHub.Simulation.Runner.Configuration;

internal sealed record LoadedSimulationConfiguration
{
    public required SimulationConfigurationDto Value { get; init; }

    public required string BaseDirectory { get; init; }
}
