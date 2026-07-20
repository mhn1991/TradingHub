namespace Simulator.Experiments.Models;

public sealed record SimulationProfileRevisionReference
{
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public bool IsBaseline { get; init; }

    public void Validate()
    {
        if (ProfileId == Guid.Empty || Revision < 1)
            throw new ArgumentException("A profile ID and positive revision are required.");
    }
}

public sealed record SimulationExperimentParallelism
{
    // Structural analysis snapshots are memory-heavy, so profile groups run sequentially
    // unless the caller deliberately opts into greater parallelism.
    public int MaxProfileGroups { get; init; } = 1;
    public int MaxTotalStrategyWorkers { get; init; } = 4;
    public int MaxHistoricalDownloadsPerBroker { get; init; } = 1;
    public long? EstimatedMemoryBudgetBytes { get; init; }

    public void Validate()
    {
        if (MaxProfileGroups < 1 || MaxTotalStrategyWorkers < 1 || MaxHistoricalDownloadsPerBroker < 1)
            throw new ArgumentException("Experiment parallelism limits must be positive.");
        if (EstimatedMemoryBudgetBytes is <= 0)
            throw new ArgumentException("The optional memory budget must be positive.");
    }
}

public sealed record SimulationExperimentRankingPolicy
{
    public int MinimumHeldOutTradeCount { get; init; } = 30;
    public decimal MinimumExpectancy { get; init; }
    public decimal MinimumProfitFactor { get; init; } = 1m;
    public decimal? MaximumDrawdown { get; init; }
    public bool RequireWarningFreeEvaluation { get; init; } = true;
    public int ShortlistSize { get; init; } = 3;
    public bool CreatePromotionCandidates { get; init; } = true;
    public decimal ExpectancyWeight { get; init; } = 0.40m;
    public decimal DrawdownAdjustedReturnWeight { get; init; } = 0.35m;
    public decimal ProfitFactorWeight { get; init; } = 0.15m;
    public decimal SampleConfidenceWeight { get; init; } = 0.10m;

    public void Validate()
    {
        if (MinimumHeldOutTradeCount < 1 || MinimumExpectancy < 0m || MinimumProfitFactor < 0m ||
            MaximumDrawdown is <= 0m || ShortlistSize < 1)
            throw new ArgumentException("Experiment ranking limits are invalid.");
        decimal weight = ExpectancyWeight + DrawdownAdjustedReturnWeight +
            ProfitFactorWeight + SampleConfidenceWeight;
        if (ExpectancyWeight < 0m || DrawdownAdjustedReturnWeight < 0m ||
            ProfitFactorWeight < 0m || SampleConfidenceWeight < 0m || weight <= 0m)
            throw new ArgumentException("Experiment ranking weights must be non-negative with a positive total.");
    }
}

public sealed record SimulationExperimentRequest
{
    public required string Name { get; init; }
    public required SimulationExperimentTimeline Timeline { get; init; }
    public required IReadOnlyList<SimulationProfileRevisionReference> Profiles { get; init; }
    public SimulationExperimentParallelism Parallelism { get; init; } = new();
    public SimulationExperimentRankingPolicy Ranking { get; init; } = new();
    public bool AllowInsufficientWarmup { get; init; }
    public DateTimeOffset? AvailableDataFrom { get; init; }
    public string? Description { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Experiment name is required.");
        Timeline.Validate();
        Parallelism.Validate();
        Ranking.Validate();
        if (Profiles is null || Profiles.Count == 0)
            throw new ArgumentException("At least one profile revision is required.");
        foreach (SimulationProfileRevisionReference profile in Profiles)
            profile.Validate();
        if (Profiles.Select(item => (item.ProfileId, item.Revision)).Distinct().Count() != Profiles.Count)
            throw new ArgumentException("A profile revision can only appear once in an experiment.");
        if (Profiles.Count(item => item.IsBaseline) > 1)
            throw new ArgumentException("An experiment can have at most one baseline profile.");
        if (AvailableDataFrom is { } availableDataFrom && availableDataFrom.Offset != TimeSpan.Zero)
            throw new ArgumentException("AvailableDataFrom must be UTC when specified.");
    }
}
