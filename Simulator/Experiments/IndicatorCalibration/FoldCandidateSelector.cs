using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Outcome states for one internal fold (blueprint §9.6/§13.3). <see cref="Unstable"/>,
/// <see cref="BudgetRejected"/>, and <see cref="Cancelled"/> are assigned by the orchestrator
/// (Phase 5), not by <see cref="FoldCandidateSelector"/> itself - they reflect conditions
/// (cross-fold instability, a budget rejection before the fold ran, or cancellation) that this
/// type, working from a single fold's own validation result, cannot determine on its own.
/// </summary>
public enum FoldResultState
{
    Improved,
    NoImprovement,
    InsufficientEvidence,
    Unstable,
    FailedAbsoluteQualityGate,
    BudgetRejected,
    FailedDataQuality,
    Cancelled
}

public sealed record FoldCandidateSelectionResult
{
    public required int FoldId { get; init; }
    public required CalibrationCandidate Candidate { get; init; }
    public required string SelectedStartingPointId { get; init; }
    public required FoldResultState State { get; init; }
    public required string Reason { get; init; }
    public required decimal? TrainingScore { get; init; }
    public required BacktestEvaluationResult? ValidationResult { get; init; }
    public required decimal? ValidationScore { get; init; }
    public required BacktestEvaluationResult? BaselineValidationResult { get; init; }
    public required decimal? BaselineValidationScore { get; init; }
}

/// <summary>One starting point's finalist candidate (post sensitivity/descent/interaction/refinement) and its own training-phase score.</summary>
public sealed record StartingPointOutcome
{
    public required string StartingPointId { get; init; }
    public required CalibrationCandidate Candidate { get; init; }
    public required decimal? TrainingScore { get; init; }
}

/// <summary>
/// Phase 6 of the search algorithm (blueprint §8 step 6, §9.6). Two sub-steps: (1) pick one
/// candidate among the starting points' training-phase results (training data only - this is
/// itself "fold candidate selection"), then (2) evaluate that single candidate exactly once on
/// the fold's held-out validation window and classify the outcome. The validation result never
/// feeds back into which starting point was picked in step 1 - that selection already happened
/// using only <see cref="StartingPointOutcome.TrainingScore"/>.
/// </summary>
public static class FoldCandidateSelector
{
    public static StartingPointOutcome SelectAmongStartingPoints(IReadOnlyList<StartingPointOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
            throw new ArgumentException("At least one starting-point outcome is required.", nameof(outcomes));

        // Deterministic tie-break (blueprint §10): prefer the higher training score; on a tie,
        // prefer the "default" starting point (the existing/default configuration) over
        // conservative/permissive, then fall back to ordinal starting-point id.
        return outcomes
            .OrderByDescending(outcome => outcome.TrainingScore ?? decimal.MinValue)
            .ThenBy(outcome => outcome.StartingPointId == StartingPointIds.Default ? 0 : 1)
            .ThenBy(outcome => outcome.StartingPointId, StringComparer.Ordinal)
            .First();
    }

    public static async Task<FoldCandidateSelectionResult> EvaluateOnHeldOutFoldAsync(
        int foldId,
        StartingPointOutcome selected,
        CalibrationCandidate baselineCandidate,
        ICandidateEvaluator validationEvaluator,
        CalibrationScoringPolicy scoringPolicy,
        CalibrationAcceptancePolicy acceptancePolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(baselineCandidate);
        ArgumentNullException.ThrowIfNull(validationEvaluator);
        ArgumentNullException.ThrowIfNull(scoringPolicy);
        ArgumentNullException.ThrowIfNull(acceptancePolicy);

        // Baseline is evaluated on every fold's validation window too (blueprint §9.1) - the
        // candidate competes against the real configured baseline, not a manifest default.
        BacktestEvaluationResult baselineResult = await validationEvaluator
            .EvaluateAsync(baselineCandidate, cancellationToken).ConfigureAwait(false);
        CandidateScoreResult baselineScored = CandidateScorer.Score(baselineResult, scoringPolicy);

        BacktestEvaluationResult validationResult = await validationEvaluator
            .EvaluateAsync(selected.Candidate, cancellationToken).ConfigureAwait(false);

        if (!validationResult.DataQualityValid)
        {
            return Build(foldId, selected, FoldResultState.FailedDataQuality,
                "Held-out fold evaluation failed the data-quality gate.",
                validationResult, null, baselineResult, baselineScored.Score);
        }
        if (validationResult.TradeCount < scoringPolicy.MinimumTradesPerFold)
        {
            return Build(foldId, selected, FoldResultState.InsufficientEvidence,
                $"Held-out trade count {validationResult.TradeCount} is below the minimum {scoringPolicy.MinimumTradesPerFold}.",
                validationResult, null, baselineResult, baselineScored.Score);
        }
        if (validationResult.MaximumDrawdownR > acceptancePolicy.MaximumExternalHoldoutDrawdownR ||
            validationResult.MaximumDrawdownR > scoringPolicy.MaximumDrawdownR)
        {
            return Build(foldId, selected, FoldResultState.FailedAbsoluteQualityGate,
                $"Held-out drawdown {validationResult.MaximumDrawdownR:F2}R exceeds the absolute quality floor.",
                validationResult, null, baselineResult, baselineScored.Score);
        }

        CandidateScoreResult validationScored = CandidateScorer.Score(validationResult, scoringPolicy);
        if (!validationScored.PassedEligibilityGates)
        {
            return Build(foldId, selected, FoldResultState.FailedAbsoluteQualityGate,
                validationScored.EligibilityFailureReason ?? "Held-out evaluation failed an eligibility gate.",
                validationResult, null, baselineResult, baselineScored.Score);
        }

        decimal validationScore = validationScored.Score!.Value;
        decimal baselineScore = baselineScored.PassedEligibilityGates ? baselineScored.Score!.Value : decimal.MinValue;
        bool improved = validationScore - baselineScore >= acceptancePolicy.MinimumImprovementOverBaseline;

        return Build(
            foldId, selected,
            improved ? FoldResultState.Improved : FoldResultState.NoImprovement,
            improved
                ? $"Held-out score {validationScore:F4} improved on baseline {baselineScore:F4} by at least {acceptancePolicy.MinimumImprovementOverBaseline:F4}."
                : $"Held-out score {validationScore:F4} did not improve on baseline {baselineScore:F4} by the required margin.",
            validationResult, validationScore, baselineResult, baselineScored.Score);
    }

    private static FoldCandidateSelectionResult Build(
        int foldId,
        StartingPointOutcome selected,
        FoldResultState state,
        string reason,
        BacktestEvaluationResult? validationResult,
        decimal? validationScore,
        BacktestEvaluationResult? baselineResult,
        decimal? baselineScore) => new()
    {
        FoldId = foldId,
        Candidate = selected.Candidate,
        SelectedStartingPointId = selected.StartingPointId,
        State = state,
        Reason = reason,
        TrainingScore = selected.TrainingScore,
        ValidationResult = validationResult,
        ValidationScore = validationScore,
        BaselineValidationResult = baselineResult,
        BaselineValidationScore = baselineScore
    };
}

public static class StartingPointIds
{
    public const string Default = "default";
    public const string Conservative = "conservative";
    public const string Permissive = "permissive";
}
