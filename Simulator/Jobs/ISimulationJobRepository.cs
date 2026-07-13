using Simulator.Models;

namespace Simulator.Jobs;

public interface ISimulationJobRepository
{
    Task SaveAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<SimulationJobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default);
}
