using Simulator.Experiments.Models;

namespace Simulator.Experiments.Persistence;

public sealed record SimulationProfileDiff
{
    public required string LeftResolvedJson { get; init; }
    public required string RightResolvedJson { get; init; }
    public required IReadOnlyList<string> ChangedPaths { get; init; }
}

public interface ISimulationStrategyProfileStore
{
    Task<SimulationStrategyProfile> CreateAsync(SimulationStrategyProfile draft, CancellationToken cancellationToken = default);
    Task<SimulationStrategyProfile> CloneAsync(Guid sourceId, int sourceRevision, Guid cloneId, string cloneName, CancellationToken cancellationToken = default);
    Task<SimulationStrategyProfile> CreateRevisionAsync(Guid profileId, SimulationStrategyProfile draft, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationStrategyProfile>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<SimulationStrategyProfile?> ReadAsync(Guid profileId, int revision, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<SimulationProfileDiff> DiffAsync(Guid leftId, int leftRevision, Guid rightId, int rightRevision, CancellationToken cancellationToken = default);
}
