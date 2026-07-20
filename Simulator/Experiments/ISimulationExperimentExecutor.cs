using Simulator.Experiments.Models;

namespace Simulator.Experiments;

public sealed record SimulationDatasetPreparationResult
{
    public required string Status { get; init; }
    public string? DatasetId { get; init; }
    public string? DatasetHash { get; init; }
}

public sealed record SimulationProfileLearningResult
{
    public Guid? ChildJobId { get; init; }
    public IReadOnlyList<SimulationArtifactReference> Artifacts { get; init; } = [];
    public string? Detail { get; init; }
}

public sealed record SimulationProfileEvaluationResult
{
    public required Guid ChildJobId { get; init; }
    public required SimulationExperimentComparisonRow Comparison { get; init; }
    public string? Detail { get; init; }
}

public sealed record SimulationExperimentExecutionContext
{
    public required Guid ExperimentId { get; init; }
    public required SimulationExperimentManifest Manifest { get; init; }
    public required Func<ValueTask> WaitIfPausedAsync { get; init; }
    public required Action<Guid, decimal, string?, Guid?> ReportProfileProgress { get; init; }
}

public interface ISimulationExperimentExecutor
{
    Task<SimulationDatasetPreparationResult> PrepareDatasetsAsync(
        SimulationExperimentExecutionContext context,
        CancellationToken cancellationToken);

    Task<SimulationProfileLearningResult> LearnAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        CancellationToken cancellationToken);

    Task<SimulationProfileEvaluationResult> EvaluateAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        IReadOnlyList<SimulationArtifactReference> frozenArtifacts,
        CancellationToken cancellationToken);

    Task PauseChildrenAsync(Guid experimentId, CancellationToken cancellationToken);
    Task ResumeChildrenAsync(Guid experimentId, CancellationToken cancellationToken);
    Task CancelChildrenAsync(Guid experimentId, CancellationToken cancellationToken);
}
