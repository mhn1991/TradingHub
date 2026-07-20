using Simulator.Experiments.Models;

namespace Simulator.Experiments.Persistence;

public interface ISimulationExperimentRepository
{
    Task SaveAsync(SimulationExperimentSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<SimulationExperimentSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationExperimentSnapshot>> ListAsync(int take = 50, CancellationToken cancellationToken = default);
    Task<int> MarkInterruptedAsync(CancellationToken cancellationToken = default);
}
