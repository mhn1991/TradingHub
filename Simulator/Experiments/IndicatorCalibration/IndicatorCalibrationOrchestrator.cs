using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record IndicatorCalibrationOrchestrationResult
{
    public required IReadOnlyList<CalibrationInternalFold> Folds { get; init; }
    public required IReadOnlyList<FoldCandidateSelectionResult> FoldResults { get; init; }
    public required CrossFoldAggregationResult Aggregation { get; init; }
    public required BacktestEvaluationResult ExternalHoldoutBaselineResult { get; init; }
    public required decimal? ExternalHoldoutBaselineScore { get; init; }
    public required BacktestEvaluationResult ExternalHoldoutCandidateResult { get; init; }
    public required decimal? ExternalHoldoutCandidateScore { get; init; }
    public required CalibrationOutcome Outcome { get; init; }
    public required string Reason { get; init; }
    /// <summary>
    /// Count of candidates that actually reached <see cref="ICandidateEvaluator.EvaluateAsync"/>
    /// (i.e. <c>IsValidCandidate</c> was true) across every fold's sensitivity screening, all three
    /// starting points' descent/interaction/refinement steps, every fold's held-out validation, and
    /// the final external holdout - for <see cref="CalibrationEvidenceSummary.TotalCandidatesEvaluated"/>.
    /// A true lower bound: it does not count internal bookkeeping evaluations invisible to the
    /// returned step lists, only what this result can account for.
    /// </summary>
    public required int TotalCandidatesEvaluated { get; init; }
}

/// <summary>
/// Wires Phase 4's search components into the leakage-safe sequence blueprint §8 and §9 describe:
/// for every internal fold, run the complete search (sensitivity → multi-start descent →
/// interaction → refinement → fold candidate selection) *strictly within that fold's training
/// window*, then evaluate the selected candidate exactly once against that fold's own held-out
/// validation window. Only after every fold has produced a result does
/// <see cref="CrossFoldAggregator"/> combine them into one final candidate - which is then, and
/// only then, evaluated against the external holdout window. The external-holdout evaluator is
/// never constructed or invoked until every fold and the aggregation step have already completed
/// (blueprint §9.8: "No further search occurs after viewing the holdout" - here, nothing *sees*
/// the holdout window at all before that point).
/// </summary>
public static class IndicatorCalibrationOrchestrator
{
    public static async Task<IndicatorCalibrationOrchestrationResult> RunAsync<TOptions>(
        IndicatorCalibrationRequest request,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluatorFactory evaluatorFactory,
        int maximumCoordinatePasses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evaluatorFactory);
        request.Validate();
        manifest.Validate();

        IReadOnlyList<CalibrationInternalFold> folds = IndicatorCalibrationFoldPlanner.BuildFolds(
            request.Timeline, request.InternalFoldCount, request.Timeline.EmbargoDays);

        CalibrationCandidate baselineCandidate = CalibrationCandidate.FromEffectiveOptions(baselineOptions, manifest);
        decimal minimumStepImprovement = manifest.ScoringPolicy.MinimumStepImprovement;

        int totalCandidatesEvaluated = 0;
        var foldResults = new List<FoldCandidateSelectionResult>();
        foreach (CalibrationInternalFold fold in folds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Training evaluator: bound only to this fold's training window. Every search step
            // below (sensitivity, descent, interaction, refinement, and starting-point selection)
            // uses only this evaluator - the fold's validation window is never touched here.
            ICandidateEvaluator trainingEvaluator = evaluatorFactory.CreateEvaluator(fold.TrainingFrom, fold.TrainingTo);

            IReadOnlyList<ParameterSensitivityResult> sensitivity = await SensitivityScreener.ScreenAsync(
                baselineCandidate, baselineOptions, manifest, trainingEvaluator, manifest.ScoringPolicy,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            totalCandidatesEvaluated += sensitivity.Sum(item => item.GridResults.Count(point => point.IsValidCandidate));
            IReadOnlyList<string> influentialOrder = SensitivityScreener.OrderByInfluence(sensitivity, manifest);

            var startingPointOutcomes = new List<StartingPointOutcome>();
            foreach ((string startId, CalibrationCandidate startCandidate) in BuildStartingPoints(baselineCandidate, manifest))
            {
                CoordinateDescentResult descent = await CoordinateDescentEngine.RunAsync(
                    startCandidate, influentialOrder, baselineOptions, manifest, trainingEvaluator, manifest.ScoringPolicy,
                    minimumStepImprovement, maximumCoordinatePasses, cancellationToken).ConfigureAwait(false);
                totalCandidatesEvaluated += descent.Steps.Count(step => step.IsValidCandidate);
                InteractionSearchResult interaction = await InteractionSearchEngine.RunAsync(
                    descent.BestCandidate, baselineOptions, manifest, trainingEvaluator, manifest.ScoringPolicy,
                    minimumStepImprovement, request.Budget.MaximumInteractionCombinationsPerGroup, cancellationToken)
                    .ConfigureAwait(false);
                totalCandidatesEvaluated += interaction.GroupResults.Sum(
                    group => group.Steps.Count(step => step.IsValidCandidate));
                LocalRefinementResult refinement = await LocalRefiner.RunAsync(
                    interaction.BestCandidate, influentialOrder, baselineOptions, manifest, trainingEvaluator,
                    manifest.ScoringPolicy, minimumStepImprovement, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                totalCandidatesEvaluated += refinement.Steps.Count(step => step.IsValidCandidate);

                startingPointOutcomes.Add(new StartingPointOutcome
                {
                    StartingPointId = startId,
                    Candidate = refinement.BestCandidate,
                    TrainingScore = refinement.BestScore
                });
            }

            // Fold candidate selection among starting points uses only the training scores just
            // computed above - the validation evaluator is constructed for the first time here,
            // strictly after that selection has already happened.
            StartingPointOutcome selected = FoldCandidateSelector.SelectAmongStartingPoints(startingPointOutcomes);
            ICandidateEvaluator validationEvaluator = evaluatorFactory.CreateEvaluator(fold.ValidationFrom, fold.ValidationTo);
            FoldCandidateSelectionResult foldResult = await FoldCandidateSelector.EvaluateOnHeldOutFoldAsync(
                fold.FoldId, selected, baselineCandidate, validationEvaluator, manifest.ScoringPolicy,
                manifest.AcceptancePolicy, cancellationToken).ConfigureAwait(false);
            totalCandidatesEvaluated += 2; // baseline + selected, both evaluated on this fold's validation window.
            foldResults.Add(foldResult);
        }

        CrossFoldAggregationResult aggregation = CrossFoldAggregator.Aggregate(foldResults, manifest, manifest.AcceptancePolicy);

        // The external holdout evaluator is constructed and invoked here for the first time -
        // after every fold and the cross-fold aggregation have already produced their final,
        // frozen result. Nothing above this line has seen or could have seen the holdout window.
        ICandidateEvaluator holdoutEvaluator = evaluatorFactory.CreateEvaluator(
            request.Timeline.ExternalHoldoutFrom, request.Timeline.ExternalHoldoutTo);
        BacktestEvaluationResult holdoutBaselineResult = await holdoutEvaluator
            .EvaluateAsync(baselineCandidate, cancellationToken).ConfigureAwait(false);
        CandidateScoreResult holdoutBaselineScored = CandidateScorer.Score(holdoutBaselineResult, manifest.ScoringPolicy);
        BacktestEvaluationResult holdoutCandidateResult = await holdoutEvaluator
            .EvaluateAsync(aggregation.AggregatedCandidate, cancellationToken).ConfigureAwait(false);
        CandidateScoreResult holdoutCandidateScored = CandidateScorer.Score(holdoutCandidateResult, manifest.ScoringPolicy);

        totalCandidatesEvaluated += 2; // external holdout: baseline + aggregated candidate.
        (CalibrationOutcome outcome, string reason) = ClassifyOutcome(
            aggregation, holdoutCandidateResult, holdoutCandidateScored, holdoutBaselineScored, manifest.AcceptancePolicy);

        return new IndicatorCalibrationOrchestrationResult
        {
            Folds = folds,
            FoldResults = foldResults,
            Aggregation = aggregation,
            ExternalHoldoutBaselineResult = holdoutBaselineResult,
            ExternalHoldoutBaselineScore = holdoutBaselineScored.PassedEligibilityGates ? holdoutBaselineScored.Score : null,
            ExternalHoldoutCandidateResult = holdoutCandidateResult,
            ExternalHoldoutCandidateScore = holdoutCandidateScored.PassedEligibilityGates ? holdoutCandidateScored.Score : null,
            Outcome = outcome,
            Reason = reason,
            TotalCandidatesEvaluated = totalCandidatesEvaluated
        };
    }

    private static IEnumerable<(string Id, CalibrationCandidate Candidate)> BuildStartingPoints<TOptions>(
        CalibrationCandidate baselineCandidate, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        yield return (StartingPointIds.Default, baselineCandidate);

        var conservative = new Dictionary<string, decimal>(baselineCandidate.NumericValues, StringComparer.Ordinal);
        var permissive = new Dictionary<string, decimal>(baselineCandidate.NumericValues, StringComparer.Ordinal);
        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters)
        {
            conservative[parameter.ParameterId] = parameter.ConservativeStartingValue;
            permissive[parameter.ParameterId] = parameter.PermissiveStartingValue;
        }

        yield return (StartingPointIds.Conservative, new CalibrationCandidate
        {
            NumericValues = conservative,
            AblationValues = baselineCandidate.AblationValues
        });
        yield return (StartingPointIds.Permissive, new CalibrationCandidate
        {
            NumericValues = permissive,
            AblationValues = baselineCandidate.AblationValues
        });
    }

    /// <summary>Blueprint §9.8: the candidate must satisfy *both* internal walk-forward acceptance and external holdout acceptance.</summary>
    private static (CalibrationOutcome Outcome, string Reason) ClassifyOutcome(
        CrossFoldAggregationResult aggregation,
        BacktestEvaluationResult holdoutCandidateResult,
        CandidateScoreResult holdoutCandidateScored,
        CandidateScoreResult holdoutBaselineScored,
        CalibrationAcceptancePolicy acceptancePolicy)
    {
        if (!aggregation.IsStable)
            return (CalibrationOutcome.UnstableAcrossFolds, "One or more parameters did not reach a stable cross-fold plateau.");
        if (aggregation.AcceptableFoldPercent < acceptancePolicy.MinimumAcceptableFoldPercent)
        {
            return (CalibrationOutcome.InsufficientEvidence,
                $"Only {aggregation.AcceptableFoldPercent:F1}% of folds improved, below the required {acceptancePolicy.MinimumAcceptableFoldPercent:F1}%.");
        }
        if (!holdoutCandidateResult.DataQualityValid)
            return (CalibrationOutcome.FailedDataQuality, "External holdout evaluation failed the data-quality gate.");
        if (holdoutCandidateResult.TradeCount < acceptancePolicy.MinimumExternalHoldoutTrades)
        {
            return (CalibrationOutcome.InsufficientEvidence,
                $"External holdout trade count {holdoutCandidateResult.TradeCount} is below the minimum {acceptancePolicy.MinimumExternalHoldoutTrades}.");
        }
        if (holdoutCandidateResult.MaximumDrawdownR > acceptancePolicy.MaximumExternalHoldoutDrawdownR)
        {
            return (CalibrationOutcome.FailedAbsoluteQualityGate,
                $"External holdout drawdown {holdoutCandidateResult.MaximumDrawdownR:F2}R exceeds the maximum {acceptancePolicy.MaximumExternalHoldoutDrawdownR:F2}R.");
        }
        if (holdoutCandidateResult.MedianExpectancyR < acceptancePolicy.MinimumExternalHoldoutExpectancyR)
        {
            return (CalibrationOutcome.FailedExternalHoldout,
                $"External holdout expectancy {holdoutCandidateResult.MedianExpectancyR:F2}R is below the minimum {acceptancePolicy.MinimumExternalHoldoutExpectancyR:F2}R.");
        }
        if (!holdoutCandidateScored.PassedEligibilityGates)
        {
            return (CalibrationOutcome.FailedExternalHoldout,
                holdoutCandidateScored.EligibilityFailureReason ?? "External holdout failed an eligibility gate.");
        }

        decimal candidateScore = holdoutCandidateScored.Score!.Value;
        decimal baselineScore = holdoutBaselineScored.PassedEligibilityGates ? holdoutBaselineScored.Score!.Value : decimal.MinValue;
        if (candidateScore - baselineScore < acceptancePolicy.MinimumImprovementOverBaseline)
        {
            return (CalibrationOutcome.NoImprovement,
                $"External holdout score {candidateScore:F4} did not improve on baseline {baselineScore:F4} by the required margin.");
        }

        return (CalibrationOutcome.Improved,
            $"External holdout score {candidateScore:F4} improved on baseline {baselineScore:F4} and every acceptance gate passed.");
    }
}
