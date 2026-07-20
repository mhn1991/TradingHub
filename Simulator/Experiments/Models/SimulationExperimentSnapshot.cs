using Simulator.Experiments;

namespace Simulator.Experiments.Models;

public enum SimulationExperimentStage
{
    Queued,
    ResolvingProfiles,
    PreparingDatasets,
    TrainingWarmup,
    Learning,
    FreezingArtifacts,
    EmbargoReady,
    EvaluationWarmup,
    Evaluating,
    Aggregating,
    Completed,
    Failed,
    Cancelled
}

public enum SimulationExperimentState
{
    Queued,
    Running,
    Paused,
    Interrupted,
    Completed,
    Failed,
    Cancelled
}

public sealed record SimulationArtifactReference
{
    public required Guid ArtifactId { get; init; }
    public required string Kind { get; init; }
    public required string ContentHash { get; init; }
}

public sealed record SimulationProfileRunProgress
{
    public required Guid ProfileRunId { get; init; }
    public required string ProfileName { get; init; }
    public required SimulationExperimentStage Stage { get; init; }
    public decimal ProgressPercent { get; init; }
    public Guid? LearningJobId { get; init; }
    public Guid? EvaluationJobId { get; init; }
    public IReadOnlyList<SimulationArtifactReference> Artifacts { get; init; } = [];
    public IReadOnlyList<SimulationExperimentStage> CompletedStages { get; init; } = [];
    public SimulationExperimentComparisonRow? Result { get; init; }
    public string? StatusDetail { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record SimulationExperimentComparisonRow
{
    public required Guid ProfileRunId { get; init; }
    public required string ProfileName { get; init; }
    public decimal? NetProfit { get; init; }
    public decimal? NetR { get; init; }
    public decimal? Expectancy { get; init; }
    public decimal? MaximumDrawdown { get; init; }
    public decimal? ProfitFactor { get; init; }
    public int TradeCount { get; init; }
    public decimal? DifferenceFromBaseline { get; init; }
    public int? Rank { get; init; }
    public bool IsEligible { get; init; }
    public bool IsShortlisted { get; init; }
    public decimal? StabilityScore { get; init; }
    public IReadOnlyDictionary<string, decimal> ScoreBreakdown { get; init; } =
        new Dictionary<string, decimal>();
    public IReadOnlyList<string> HardGateFailures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record SimulationPromotionCandidate
{
    public required Guid CandidateId { get; init; }
    public required Guid ExperimentId { get; init; }
    public required Guid ProfileRunId { get; init; }
    public required Guid ProfileId { get; init; }
    public required int ProfileRevision { get; init; }
    public required string ProfileContentHash { get; init; }
    public required string AgentDefinitionHash { get; init; }
    public required string DatasetHash { get; init; }
    public required string CandidatePackageHash { get; init; }
    public required SimulationExperimentTimeline Timeline { get; init; }
    public required IReadOnlyList<SimulationArtifactReference> Artifacts { get; init; }
    public required int Rank { get; init; }
    public required decimal StabilityScore { get; init; }
    public string Status { get; init; } = "AwaitingOperatorApproval";
    public bool AutomaticExecutablePermission { get; init; }
}

public sealed record SimulationExperimentComparisonSummary
{
    public Guid? BaselineProfileRunId { get; init; }
    public IReadOnlyList<SimulationExperimentComparisonRow> Profiles { get; init; } = [];
    public IReadOnlyList<SimulationPromotionCandidate> PromotionCandidates { get; init; } = [];
    public string Objective { get; init; } = "RiskAdjustedExpectancy";
}

public sealed record SimulationExperimentManifest
{
    public required SimulationExperimentTimeline Timeline { get; init; }
    public required IReadOnlyList<SimulationExperimentProfileRun> ProfileRuns { get; init; }
    public required IReadOnlyDictionary<Guid, AnalysisWarmupPlan> TrainingWarmups { get; init; }
    public required IReadOnlyDictionary<Guid, AnalysisWarmupPlan> EvaluationWarmups { get; init; }
    public required SimulationExperimentParallelism Parallelism { get; init; }
    public SimulationExperimentRankingPolicy Ranking { get; init; } = new();
    public IReadOnlyDictionary<Guid, string> AnalysisCompatibilityKeys { get; init; } =
        new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> HistoricalDataCompatibilityKeys { get; init; } =
        new Dictionary<Guid, string>();
    public string DatasetStatus { get; init; } = "Pending";
    public string? DatasetId { get; init; }
    public string? DatasetHash { get; init; }
}

public sealed record SimulationResourceUsageSnapshot
{
    public int ParentExperiments { get; init; }
    public int ProfileGroups { get; init; }
    public int StrategyWorkers { get; init; }
    public IReadOnlyDictionary<string, int> HistoricalDownloadsByBroker { get; init; } =
        new Dictionary<string, int>();
    public long EstimatedMemoryBytes { get; init; }
}

public sealed record SimulationExperimentSnapshot
{
    public required Guid Id { get; init; }
    public required int Revision { get; init; }
    public required string Name { get; init; }
    public required SimulationExperimentState State { get; init; }
    public required SimulationExperimentStage Stage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required SimulationExperimentManifest Manifest { get; init; }
    public required IReadOnlyList<SimulationProfileRunProgress> Profiles { get; init; }
    public SimulationResourceUsageSnapshot ResourceUsage { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? FailureReason { get; init; }
    public SimulationExperimentComparisonSummary? Comparison { get; init; }

    public bool IsTerminal => State is SimulationExperimentState.Completed or
        SimulationExperimentState.Failed or SimulationExperimentState.Cancelled;
}

public readonly record struct SimulationExperimentHandle(Guid Id, SimulationExperimentState State);
