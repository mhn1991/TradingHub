using Simulator.Experiments.Models;

namespace Simulator.Experiments;

public interface ISimulationExperimentApplicationService
{
    Task<SimulationExperimentHandle> StartAsync(
        SimulationExperimentRequest request,
        CancellationToken cancellationToken = default);
    Task<SimulationExperimentSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationExperimentSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default);
    Task PauseAsync(Guid id, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid id, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);
}
