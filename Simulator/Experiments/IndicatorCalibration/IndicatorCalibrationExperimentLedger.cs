using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>Which deterministic stage of the search a ledger entry/run belongs to (blueprint §8/§9/§19 Phase 5-6).</summary>
public enum IndicatorCalibrationStage
{
    NotStarted,
    BaselineEvaluation,
    SensitivityScreening,
    CoordinateDescent,
    InteractionSearch,
    LocalRefinement,
    FoldCandidateSelection,
    CrossFoldAggregation,
    ExternalHoldout,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// One deterministic, resumable step of a calibration run (blueprint §13.1/§14). Every
/// candidate evaluation - accepted, rejected, or invalid - produces exactly one entry, keyed by a
/// step id of the form <c>fold:{foldId}:stage:{stage}:start:{startId}:parameter:{parameterId}:candidate:{candidateIndex}</c>.
/// </summary>
public sealed record IndicatorCalibrationLedgerEntry
{
    public required string StepId { get; init; }
    public required IndicatorCalibrationStage Stage { get; init; }
    /// <summary>-1 for steps that are not scoped to a specific internal fold (e.g. external holdout).</summary>
    public required int FoldId { get; init; }
    public required string StartingPointId { get; init; }
    public string? ParameterId { get; init; }
    public required int CandidateIndex { get; init; }

    public required IReadOnlyDictionary<string, decimal> CandidateNumericValues { get; init; }
    public required IReadOnlyDictionary<string, bool> CandidateAblationValues { get; init; }

    public required bool WasCacheHit { get; init; }

    public required bool IsValidCandidate { get; init; }
    public string? InvalidCandidateReason { get; init; }

    public required bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }

    public BacktestEvaluationResult? Result { get; init; }
    public decimal? Score { get; init; }

    public required bool Accepted { get; init; }
    public string? RejectionReason { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StepId))
            throw new ArgumentException("StepId is required.");
        if (string.IsNullOrWhiteSpace(StartingPointId))
            throw new ArgumentException("StartingPointId is required.");
        if (FoldId < -1)
            throw new ArgumentOutOfRangeException(nameof(FoldId));
        if (CandidateIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(CandidateIndex));
        ArgumentNullException.ThrowIfNull(CandidateNumericValues);
        ArgumentNullException.ThrowIfNull(CandidateAblationValues);
        if (!IsValidCandidate && string.IsNullOrWhiteSpace(InvalidCandidateReason))
            throw new ArgumentException("An invalid candidate must record InvalidCandidateReason.");
        if (IsValidCandidate && !PassedEligibilityGates && string.IsNullOrWhiteSpace(EligibilityFailureReason))
            throw new ArgumentException("A candidate that failed eligibility gates must record EligibilityFailureReason.");
        if (!Accepted && Score is not null && string.IsNullOrWhiteSpace(RejectionReason) && IsValidCandidate && PassedEligibilityGates)
            throw new ArgumentException("A scored but unaccepted candidate must record RejectionReason.");
    }
}

/// <summary>
/// The full research ledger for one calibration run (blueprint §13.1). Large, never loaded by
/// live trading - <see cref="IndicatorCalibrationArtifact.ExperimentLedgerId"/> is only a
/// reference a human reviewer follows, not something the runtime decision path ever reads.
/// </summary>
public sealed record IndicatorCalibrationExperimentLedger
{
    public required string LedgerId { get; init; }
    /// <summary>Monotonic; the repository must never persist a write with Revision &lt;= the currently-stored value.</summary>
    public required long Revision { get; init; }
    public required IndicatorCalibrationRequest Request { get; init; }
    public required CalibrationBudgetPreview BudgetPreview { get; init; }
    public required IReadOnlyList<IndicatorCalibrationLedgerEntry> Entries { get; init; }
    public required IndicatorCalibrationStage CurrentStage { get; init; }
    public required bool IsCancelled { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LedgerId))
            throw new ArgumentException("LedgerId is required.");
        ArgumentNullException.ThrowIfNull(Request);
        Request.Validate();
        ArgumentNullException.ThrowIfNull(BudgetPreview);
        ArgumentNullException.ThrowIfNull(Entries);
        foreach (IndicatorCalibrationLedgerEntry entry in Entries)
            entry.Validate();
        if (Entries.Select(entry => entry.StepId).Distinct(StringComparer.Ordinal).Count() != Entries.Count)
            throw new ArgumentException("Duplicate StepId values in the ledger - each deterministic step must be recorded exactly once.");
        if (!Enum.IsDefined(CurrentStage))
            throw new ArgumentOutOfRangeException(nameof(CurrentStage));
        if (UpdatedAt < CreatedAt)
            throw new ArgumentException("UpdatedAt cannot precede CreatedAt.");
    }

    /// <summary>Content hash of the ledger, recorded on the compact artifact so a reviewer can detect a ledger that was altered after the fact.</summary>
    public string ComputeChecksum() => IndicatorCalibrationHash.ComputeOfObject(this);

    /// <summary>The set of step ids already recorded - resume uses this to skip completed steps (blueprint §14).</summary>
    public IReadOnlySet<string> CompletedStepIds() =>
        new HashSet<string>(Entries.Select(entry => entry.StepId), StringComparer.Ordinal);
}
