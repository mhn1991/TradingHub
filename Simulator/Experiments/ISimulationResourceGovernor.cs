using Simulator.Experiments.Models;

namespace Simulator.Experiments;

public interface ISimulationResourceGovernor
{
    ValueTask<IAsyncDisposable> AcquireExperimentAsync(CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireProfileGroupAsync(
        int strategyWorkers,
        long estimatedMemoryBytes = 0,
        CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireHistoricalDownloadAsync(
        string brokerIdentity,
        CancellationToken cancellationToken = default);
    SimulationResourceUsageSnapshot GetUsage();
}

public sealed record SimulationResourceGovernorOptions
{
    public int MaxConcurrentExperiments { get; init; } = 1;
    public int MaxConcurrentProfileGroups { get; init; } = 2;
    public int MaxHistoricalDownloadsPerBroker { get; init; } = 1;
    public int MaxTotalStrategyWorkers { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public long? EstimatedMemoryBudgetBytes { get; init; }

    public void Validate()
    {
        if (MaxConcurrentExperiments < 1 || MaxConcurrentProfileGroups < 1 ||
            MaxHistoricalDownloadsPerBroker < 1 || MaxTotalStrategyWorkers < 1)
            throw new ArgumentException("Resource-governor concurrency limits must be positive.");
        if (EstimatedMemoryBudgetBytes is <= 0)
            throw new ArgumentException("The optional resource-governor memory budget must be positive.");
    }
}
