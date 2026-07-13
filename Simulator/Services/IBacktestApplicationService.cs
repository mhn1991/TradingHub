using Simulator.Models;

namespace Simulator.Services;

public interface IBacktestApplicationService
{
    Task<SimulationJobHandle> StartAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default);

    Task<SimulationJobSnapshot?> GetAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default);

    Task PauseAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a job to completion on the calling thread (used by CLI). Still persists
    /// the same job snapshots as the dashboard path.
    /// </summary>
    Task<ComparativeSimulationResult> RunToCompletionAsync(
        BacktestRequest request,
        IProgress<BacktestProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
