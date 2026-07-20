using Simulator.Jobs;
using Simulator.Models;

namespace TradingHub.Persistence.Postgres.Simulation;

internal sealed class DualWriteSimulationJobRepository(
    ISimulationJobRepository postgres,
    ISimulationJobRepository legacy,
    bool postgresReads) : ISimulationJobRepository
{
    public async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken = default)
    {
        await postgres.MarkInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);
        await legacy.MarkInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await postgres.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await legacy.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public Task<SimulationJobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (postgresReads ? postgres : legacy).GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default) =>
        (postgresReads ? postgres : legacy).ListAsync(take, cancellationToken);
}
