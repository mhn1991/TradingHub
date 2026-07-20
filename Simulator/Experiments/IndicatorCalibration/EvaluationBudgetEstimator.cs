using Brokers.Models;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Computes the expected evaluation count *before* any backtest runs (blueprint §11). Every
/// formula below is a documented, deliberately simple estimate - not an exact count, since the
/// real count depends on runtime sensitivity-screening/convergence outcomes that are not known
/// ahead of time. The point of the preview is transparency and a hard ceiling, not precision.
/// </summary>
public static class EvaluationBudgetEstimator
{
    /// <summary>Fixed by blueprint §9.2: current/default, conservative, permissive.</summary>
    public const int StartingPointCount = 3;

    /// <summary>Local-refinement grid size per winning parameter (blueprint §9.5's own "1.0, 1.5, 2.0, 2.5, 3.0" example uses 5).</summary>
    public const int RefinementGridSize = 5;

    /// <summary>Rough self-similarity discount: sensitivity/descent/interaction stages revisit the same starting-point defaults repeatedly.</summary>
    private const decimal AssumedCacheHitFraction = 0.10m;

    /// <summary>
    /// Measured 2026-07-20 against the real <c>BacktestApplicationService</c> engine (not a
    /// synthetic evaluator): 3 real evaluations over a ~24-day, 1-minute-candle
    /// structural-confluence window averaged ~108 seconds each (~34,560 candles per evaluation),
    /// i.e. roughly 3.1 seconds per 1,000 candles. The two constants below bracket that measurement
    /// with margin - they replace earlier placeholder values (0.02/0.10) that were never validated
    /// against a real backtest and undercounted duration by roughly two orders of magnitude. See
    /// <c>Simulator.Tests.IndicatorConfluenceCalibrationEndToEndTests</c> for the measurement itself.
    /// Revisit if/when the engine's own per-candle throughput materially changes.
    /// </summary>
    private const decimal LowSecondsPerThousandCandles = 1.5m;
    private const decimal HighSecondsPerThousandCandles = 3.5m;

    public static CalibrationBudgetPreview Estimate<TOptions>(
        IndicatorCalibrationRequest request,
        IIndicatorCalibrationManifest<TOptions> manifest,
        int maximumCoordinatePasses)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        request.Validate();
        manifest.Validate();
        if (maximumCoordinatePasses is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximumCoordinatePasses), "Blueprint §9.3 recommends 2-4 passes.");

        int foldCount = request.InternalFoldCount;
        int startingPoints = StartingPointCount;

        int baseline = foldCount + 1; // one baseline per fold's validation window, plus the external holdout baseline (§9.1).
        int sensitivity = manifest.Parameters.Sum(parameter => parameter.CoarseGrid.Count) * foldCount; // §9.2, once per fold.
        int startingPointEvaluations = startingPoints * foldCount; // one evaluation to seed each starting point's own baseline score.

        int interaction = EstimateInteractionEvaluations(manifest, request.Budget.MaximumInteractionCombinationsPerGroup)
            * startingPoints * foldCount;
        int refinement = manifest.Parameters.Count * RefinementGridSize * startingPoints * foldCount; // §9.5.
        int externalHoldout = 2; // baseline + final aggregated candidate, evaluated exactly once (§9.8).

        int totalPlanned = baseline + sensitivity + startingPointEvaluations + interaction + refinement + externalHoldout;
        int estimatedCacheHits = (int)Math.Round(totalPlanned * AssumedCacheHitFraction, MidpointRounding.AwayFromZero);
        int expectedUncached = Math.Max(0, totalPlanned - estimatedCacheHits);

        long candlesPerEvaluation = EstimateCandlesPerEvaluation(request);
        long estimatedCandleEvaluations = candlesPerEvaluation * expectedUncached;

        TimeSpan durationLow = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * LowSecondsPerThousandCandles));
        TimeSpan durationHigh = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * HighSecondsPerThousandCandles));

        var preview = new CalibrationBudgetPreview
        {
            BaselineEvaluations = baseline,
            SensitivityEvaluations = sensitivity,
            StartingPointEvaluations = startingPointEvaluations,
            MaximumCoordinatePasses = maximumCoordinatePasses,
            InteractionEvaluations = interaction,
            RefinementEvaluations = refinement,
            InternalFoldMultiplier = foldCount,
            ExternalHoldoutEvaluations = externalHoldout,
            EstimatedCacheHits = estimatedCacheHits,
            ExpectedUncachedBacktests = expectedUncached,
            EstimatedCandleEvaluations = estimatedCandleEvaluations,
            EstimatedDurationLow = durationLow,
            EstimatedDurationHigh = durationHigh,
            HardRuntimeLimit = request.Budget.HardRuntimeLimit,
            ExceedsBudget = false,
            AppliedOverflowPolicy = null,
            OverflowAdjustments = []
        };

        bool exceeds = preview.TotalPlannedEvaluations > request.Budget.MaximumEvaluationCount ||
            durationHigh > request.Budget.HardRuntimeLimit;
        if (!exceeds)
            return preview;

        return ApplyOverflowPolicy(preview, request, manifest, maximumCoordinatePasses);
    }

    /// <summary>
    /// Deterministically applies exactly one adjustment matching <see cref="CalibrationEvaluationBudget.OverflowPolicy"/>
    /// and recomputes once. If the result still exceeds budget, <see cref="CalibrationBudgetPreview.ExceedsBudget"/>
    /// stays true and the caller (not this estimator) decides whether to reject the run - this
    /// method never loops indefinitely searching for a satisfying configuration (blueprint §11:
    /// "must never silently truncate the search" - an explicit, reported, single adjustment is not silent truncation).
    /// </summary>
    private static CalibrationBudgetPreview ApplyOverflowPolicy<TOptions>(
        CalibrationBudgetPreview original,
        IndicatorCalibrationRequest request,
        IIndicatorCalibrationManifest<TOptions> manifest,
        int maximumCoordinatePasses)
    {
        switch (request.Budget.OverflowPolicy)
        {
            case CalibrationBudgetOverflowPolicy.Reject:
                return original with
                {
                    ExceedsBudget = true,
                    AppliedOverflowPolicy = CalibrationBudgetOverflowPolicy.Reject,
                    OverflowAdjustments = ["Plan exceeds budget; OverflowPolicy=Reject means the run must not start."]
                };

            case CalibrationBudgetOverflowPolicy.ReduceStartingPoints:
            {
                CalibrationBudgetPreview reduced = EstimateWithStartingPoints(request, manifest, maximumCoordinatePasses, startingPoints: 1);
                return reduced with
                {
                    ExceedsBudget = reduced.TotalPlannedEvaluations > request.Budget.MaximumEvaluationCount ||
                        reduced.EstimatedDurationHigh > request.Budget.HardRuntimeLimit,
                    AppliedOverflowPolicy = CalibrationBudgetOverflowPolicy.ReduceStartingPoints,
                    OverflowAdjustments = ["Reduced starting points from 3 (default/conservative/permissive) to 1 (default only)."]
                };
            }

            case CalibrationBudgetOverflowPolicy.ReduceRefinement:
            {
                var adjusted = original with
                {
                    RefinementEvaluations = 0,
                    AppliedOverflowPolicy = CalibrationBudgetOverflowPolicy.ReduceRefinement,
                    OverflowAdjustments = ["Disabled local refinement (Phase 4 §9.5) for this run."]
                };
                int total = adjusted.BaselineEvaluations + adjusted.SensitivityEvaluations +
                    adjusted.StartingPointEvaluations + adjusted.InteractionEvaluations + adjusted.ExternalHoldoutEvaluations;
                var recomputed = RecomputeDuration(request, total);
                return adjusted with
                {
                    EstimatedCacheHits = recomputed.CacheHits,
                    ExpectedUncachedBacktests = recomputed.ExpectedUncached,
                    EstimatedCandleEvaluations = recomputed.CandleEvaluations,
                    EstimatedDurationLow = recomputed.Low,
                    EstimatedDurationHigh = recomputed.High,
                    ExceedsBudget = total > request.Budget.MaximumEvaluationCount ||
                        recomputed.High > request.Budget.HardRuntimeLimit
                };
            }

            case CalibrationBudgetOverflowPolicy.SkipLowerPriorityInteractions:
            {
                var adjusted = original with
                {
                    InteractionEvaluations = 0,
                    AppliedOverflowPolicy = CalibrationBudgetOverflowPolicy.SkipLowerPriorityInteractions,
                    OverflowAdjustments = ["Skipped all declared interaction-group searches for this run."]
                };
                int total = adjusted.BaselineEvaluations + adjusted.SensitivityEvaluations +
                    adjusted.StartingPointEvaluations + adjusted.RefinementEvaluations + adjusted.ExternalHoldoutEvaluations;
                var recomputed = RecomputeDuration(request, total);
                return adjusted with
                {
                    EstimatedCacheHits = recomputed.CacheHits,
                    ExpectedUncachedBacktests = recomputed.ExpectedUncached,
                    EstimatedCandleEvaluations = recomputed.CandleEvaluations,
                    EstimatedDurationLow = recomputed.Low,
                    EstimatedDurationHigh = recomputed.High,
                    ExceedsBudget = total > request.Budget.MaximumEvaluationCount ||
                        recomputed.High > request.Budget.HardRuntimeLimit
                };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Budget.OverflowPolicy, "Unsupported overflow policy.");
        }
    }

    private static CalibrationBudgetPreview EstimateWithStartingPoints<TOptions>(
        IndicatorCalibrationRequest request,
        IIndicatorCalibrationManifest<TOptions> manifest,
        int maximumCoordinatePasses,
        int startingPoints)
    {
        int foldCount = request.InternalFoldCount;
        int baseline = foldCount + 1;
        int sensitivity = manifest.Parameters.Sum(parameter => parameter.CoarseGrid.Count) * foldCount;
        int startingPointEvaluations = startingPoints * foldCount;
        int interaction = EstimateInteractionEvaluations(manifest, request.Budget.MaximumInteractionCombinationsPerGroup)
            * startingPoints * foldCount;
        int refinement = manifest.Parameters.Count * RefinementGridSize * startingPoints * foldCount;
        int externalHoldout = 2;
        int totalPlanned = baseline + sensitivity + startingPointEvaluations + interaction + refinement + externalHoldout;
        int estimatedCacheHits = (int)Math.Round(totalPlanned * AssumedCacheHitFraction, MidpointRounding.AwayFromZero);
        int expectedUncached = Math.Max(0, totalPlanned - estimatedCacheHits);
        long candlesPerEvaluation = EstimateCandlesPerEvaluation(request);
        long estimatedCandleEvaluations = candlesPerEvaluation * expectedUncached;

        return new CalibrationBudgetPreview
        {
            BaselineEvaluations = baseline,
            SensitivityEvaluations = sensitivity,
            StartingPointEvaluations = startingPointEvaluations,
            MaximumCoordinatePasses = maximumCoordinatePasses,
            InteractionEvaluations = interaction,
            RefinementEvaluations = refinement,
            InternalFoldMultiplier = foldCount,
            ExternalHoldoutEvaluations = externalHoldout,
            EstimatedCacheHits = estimatedCacheHits,
            ExpectedUncachedBacktests = expectedUncached,
            EstimatedCandleEvaluations = estimatedCandleEvaluations,
            EstimatedDurationLow = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * LowSecondsPerThousandCandles)),
            EstimatedDurationHigh = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * HighSecondsPerThousandCandles)),
            HardRuntimeLimit = request.Budget.HardRuntimeLimit,
            ExceedsBudget = false,
            AppliedOverflowPolicy = null,
            OverflowAdjustments = []
        };
    }

    /// <summary>
    /// Re-derives cache-hit/candle/duration figures for a post-overflow-adjustment evaluation
    /// total, using the same cache-hit-fraction and per-candle constants as the initial estimate
    /// (rather than a naive proportional scale of the original figures), so a reduced plan's
    /// reported numbers stay internally consistent with <see cref="Estimate{TOptions}"/>'s own math.
    /// </summary>
    private static (int CacheHits, int ExpectedUncached, long CandleEvaluations, TimeSpan Low, TimeSpan High) RecomputeDuration(
        IndicatorCalibrationRequest request, int totalPlanned)
    {
        int estimatedCacheHits = (int)Math.Round(totalPlanned * AssumedCacheHitFraction, MidpointRounding.AwayFromZero);
        int expectedUncached = Math.Max(0, totalPlanned - estimatedCacheHits);
        long candlesPerEvaluation = EstimateCandlesPerEvaluation(request);
        long estimatedCandleEvaluations = candlesPerEvaluation * expectedUncached;

        TimeSpan low = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * LowSecondsPerThousandCandles));
        TimeSpan high = TimeSpan.FromSeconds((double)(estimatedCandleEvaluations / 1000m * HighSecondsPerThousandCandles));
        return (estimatedCacheHits, expectedUncached, estimatedCandleEvaluations, low, high);
    }

    private static int EstimateInteractionEvaluations<TOptions>(
        IIndicatorCalibrationManifest<TOptions> manifest, int maximumCombinationsPerGroup)
    {
        int total = 0;
        foreach (CalibrationInteractionGroup group in manifest.InteractionGroups)
        {
            int combinations = group.ParameterIds
                .Select(id => GridSizeFor(manifest, id))
                .Aggregate(1, (accumulator, size) => accumulator * size);
            total += Math.Min(combinations, maximumCombinationsPerGroup);
        }
        return total;
    }

    private static int GridSizeFor<TOptions>(IIndicatorCalibrationManifest<TOptions> manifest, string parameterId)
    {
        ICalibrationParameterDescriptor<TOptions>? parameter = manifest.Parameters
            .FirstOrDefault(item => string.Equals(item.ParameterId, parameterId, StringComparison.Ordinal));
        if (parameter is not null)
            return parameter.CoarseGrid.Count;
        // An interaction-group member that is an ablation flag contributes exactly 2 (on/off) - blueprint §9.4: "boolean ablation may form one dimension."
        bool isAblation = manifest.Ablations.Any(item => string.Equals(item.ParameterId, parameterId, StringComparison.Ordinal));
        return isAblation ? 2 : 1;
    }

    private static long EstimateCandlesPerEvaluation(IndicatorCalibrationRequest request)
    {
        BarInterval interval = request.TimeframeTopology.ExecutionInterval;
        double approximateSecondsPerCandle = ApproximateSeconds(interval);
        TimeSpan foldTrainingSpan = (request.Timeline.LearningTo - request.Timeline.LearningFrom) / Math.Max(1, request.InternalFoldCount);
        double candles = foldTrainingSpan.TotalSeconds / Math.Max(1d, approximateSecondsPerCandle);
        return Math.Max(1, (long)candles);
    }

    private static double ApproximateSeconds(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => interval.Value,
        BarUnit.Minute => interval.Value * 60,
        BarUnit.Hour => interval.Value * 3_600,
        BarUnit.Day => interval.Value * 86_400,
        BarUnit.Week => interval.Value * 604_800,
        BarUnit.Month => interval.Value * 2_678_400,
        _ => 60
    };
}
