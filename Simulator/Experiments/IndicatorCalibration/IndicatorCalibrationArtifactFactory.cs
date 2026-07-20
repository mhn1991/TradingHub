using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Converts an <see cref="IndicatorCalibrationOrchestrationResult"/> into the compact, promotable
/// <see cref="IndicatorCalibrationArtifact"/> (blueprint §13.2, §19 Phase 6 "artifact creation").
/// Every artifact is created with <see cref="CalibrationPromotionStatus.PendingReview"/> regardless
/// of <see cref="CalibrationOutcome"/> - nothing in this factory, or anywhere upstream of it, ever
/// sets <see cref="CalibrationPromotionStatus.Approved"/>. That transition is Phase 7's explicit,
/// human-triggered approval flow alone.
/// </summary>
public static class IndicatorCalibrationArtifactFactory
{
    public static IndicatorCalibrationArtifact BuildArtifact<TOptions>(
        IndicatorCalibrationOrchestrationResult result,
        IIndicatorCalibrationManifest<TOptions> manifest,
        TOptions baselineOptions,
        CalibrationCompatibilityIdentity compatibility,
        string calibrationId,
        string instrument,
        string candleDataIdentityHash,
        string baselineConfigurationHash,
        string experimentLedgerId,
        string experimentLedgerChecksum,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(baselineOptions);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(candleDataIdentityHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineConfigurationHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentLedgerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentLedgerChecksum);

        CalibrationCandidate baselineCandidate = CalibrationCandidate.FromEffectiveOptions(baselineOptions, manifest);
        CalibrationCandidate aggregated = result.Aggregation.AggregatedCandidate;
        TOptions resolvedOptions = aggregated.ApplyTo(baselineOptions, manifest);
        string resolvedCandidateConfigurationHash = IndicatorCalibrationHash.ComputeOfObject(resolvedOptions);

        var overrides = new List<CalibratedParameterOverride>();
        foreach (ParameterAggregationResult parameterResult in result.Aggregation.ParameterResults)
        {
            decimal baselineValue = baselineCandidate.RequireNumericValue(parameterResult.ParameterId);
            if (baselineValue == parameterResult.AggregatedValue)
                continue; // Only differences from the effective baseline are stored (blueprint §13.2).
            overrides.Add(new CalibratedParameterOverride
            {
                ParameterId = parameterResult.ParameterId,
                DefaultValue = baselineValue,
                CalibratedValue = parameterResult.AggregatedValue,
                FoldSupportPercent = parameterResult.FoldSupportPercent,
                PlateauWidth = parameterResult.PlateauWidth,
                SelectionStage = "CrossFoldAggregation"
            });
        }

        var ablationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach ((string parameterId, bool aggregatedValue) in aggregated.AblationValues)
        {
            if (baselineCandidate.AblationValues.TryGetValue(parameterId, out bool baselineValue) &&
                baselineValue != aggregatedValue)
            {
                ablationOverrides[parameterId] = aggregatedValue;
            }
        }

        CalibrationEvidenceSummary evidence = BuildEvidence(result);

        var artifact = new IndicatorCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            StrategyId = compatibility.StrategyId,
            StrategyImplementationVersion = compatibility.StrategyImplementationVersion,
            OptionsSchemaVersion = compatibility.OptionsSchemaVersion,
            ManifestVersion = manifest.ManifestVersion,
            Scope = IndicatorCalibrationArtifact.InstrumentScope,
            Instrument = instrument,
            TimeframeTopologyHash = compatibility.TimeframeTopologyHash,
            CandleDataIdentityHash = candleDataIdentityHash,
            BaselineConfigurationHash = baselineConfigurationHash,
            ResolvedCandidateConfigurationHash = resolvedCandidateConfigurationHash,
            Overrides = overrides,
            AblationOverrides = ablationOverrides,
            Evidence = evidence,
            Outcome = result.Outcome,
            ExperimentLedgerId = experimentLedgerId,
            ExperimentLedgerChecksum = experimentLedgerChecksum,
            PromotionStatus = CalibrationPromotionStatus.PendingReview,
            CreatedAt = createdAt
        };
        artifact.Validate();
        return artifact;
    }

    private static CalibrationEvidenceSummary BuildEvidence(IndicatorCalibrationOrchestrationResult result)
    {
        FoldCandidateSelectionResult[] withValidation = result.FoldResults
            .Where(fold => fold.ValidationResult is not null)
            .ToArray();

        decimal externalHoldoutBaselineExpectancy = result.ExternalHoldoutBaselineResult.MedianExpectancyR;
        decimal externalHoldoutImprovement =
            (result.ExternalHoldoutCandidateScore ?? decimal.MinValue) is var candidateScore && candidateScore > decimal.MinValue &&
            (result.ExternalHoldoutBaselineScore ?? decimal.MinValue) is var baselineScore && baselineScore > decimal.MinValue
                ? candidateScore - baselineScore
                : 0m;

        return new CalibrationEvidenceSummary
        {
            FoldCount = result.FoldResults.Count,
            AcceptableFoldCount = result.Aggregation.AcceptableFoldCount,
            AcceptableFoldPercent = result.Aggregation.AcceptableFoldPercent,
            MedianValidationExpectancyR = Median(withValidation.Select(fold => fold.ValidationResult!.MedianExpectancyR)),
            MedianValidationDrawdownR = Median(withValidation.Select(fold => fold.ValidationResult!.MaximumDrawdownR)),
            MedianValidationTradeCount = (int)Median(withValidation.Select(fold => (decimal)fold.ValidationResult!.TradeCount)),
            TrainValidationDegradation = Median(withValidation.Select(fold =>
                (fold.TrainingScore ?? 0m) - (fold.ValidationScore ?? fold.TrainingScore ?? 0m))),
            BaselineMedianExpectancyR = Median(withValidation.Select(fold => fold.BaselineValidationResult!.MedianExpectancyR)),
            ImprovementOverBaseline = externalHoldoutImprovement,
            ExternalHoldoutExpectancyR = result.ExternalHoldoutCandidateResult.MedianExpectancyR,
            ExternalHoldoutDrawdownR = result.ExternalHoldoutCandidateResult.MaximumDrawdownR,
            ExternalHoldoutTradeCount = result.ExternalHoldoutCandidateResult.TradeCount,
            ExternalHoldoutBaselineExpectancyR = externalHoldoutBaselineExpectancy,
            TotalCandidatesEvaluated = Math.Max(1, result.TotalCandidatesEvaluated)
        };
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0m;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
    }
}
