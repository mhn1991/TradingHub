using RiskManager.Calibration;

namespace Simulator.Calibration;

/// <summary>
/// A deterministic, calibration-bucket-driven <see cref="ISetupMetaModel"/> - not a trained ML
/// model (2026-07-16 audit §14-17 decision: this codebase has no ML framework, and a bucket
/// lookup over the same win-rate/Brier-score statistics <c>ConfidenceCalibrator</c> already
/// produces is honest about what "meta-labeling" means here). Every return path is clamped to
/// [0, 1] as defense-in-depth on top of <see cref="MetaLabelDecision.Validate"/>'s own
/// reject-if-greater-than-one guard - this model can only reject, reduce risk, or stay
/// neutral; it can never increase risk above the strategy's own base sizing.
/// </summary>
public sealed class CalibratedSetupMetaModel : ISetupMetaModel
{
    private readonly MetaModelArtifact _artifact;
    private readonly MetaModelPolicyOptions _options;

    public CalibratedSetupMetaModel(MetaModelArtifact artifact, MetaModelPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _options = options ?? new MetaModelPolicyOptions();
        _options.Validate();
        artifact.Validate(requiredFeatureSchemaHash: MetaLabelFeatureFactory.SchemaVersion);
        _artifact = artifact;
    }

    public string ModelVersion => _artifact.ModelVersion;

    public MetaLabelDecision Evaluate(MetaLabelFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (!string.Equals(features.FeatureSchemaVersion, MetaLabelFeatureFactory.SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Meta-label features use schema '{features.FeatureSchemaVersion}', but model '{_artifact.ModelVersion}' requires " +
                $"'{MetaLabelFeatureFactory.SchemaVersion}'. Retraining is required.");
        }

        string playbookId = MetaModelCohorts.NormalizePlaybook(features.PlaybookId);
        string? cciState = MetaModelCohorts.CoarseCciState(features.CciConfirmationState);
        string? confluenceState = MetaModelCohorts.CoarseConfluenceState(features.SupplyDemandLiquidityConfluence);
        IEnumerable<MetaModelBucket> cohort = _artifact.Buckets.Where(candidate =>
            string.Equals(candidate.StrategyId, features.StrategyId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.PlaybookId, playbookId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Regime, features.Regime.ToString(), StringComparison.OrdinalIgnoreCase) &&
            features.SetupConfidence >= candidate.ConfidenceFrom &&
            (features.SetupConfidence < candidate.ConfidenceTo || candidate.ConfidenceTo == 100m && features.SetupConfidence <= 100m));

        (MetaModelBucket? bucket, int? fallbackLevel) = Select(cohort, features.MultiTimeframeAlignment, cciState, confluenceState);

        if (bucket is null)
        {
            return new MetaLabelDecision
            {
                Trade = true,
                Probability = 0.5m,
                ModelVersion = _artifact.ModelVersion,
                ReasonCode = "MetaModelBucketUnavailable",
                BucketFallbackLevel = null,
                RiskMultiplier = 1m
            };
        }

        decimal probability = Math.Clamp(bucket.WinRate, 0m, 1m);
        if (bucket.ExpectedR < 0m)
        {
            return new MetaLabelDecision
            {
                Trade = false,
                Probability = probability,
                ModelVersion = _artifact.ModelVersion,
                ReasonCode = "MetaModelNegativeExpectancy",
                BucketFallbackLevel = fallbackLevel,
                RiskMultiplier = 0m
            };
        }

        if (bucket.ExpectedR < _options.WeakPositiveExpectedRThreshold)
        {
            return new MetaLabelDecision
            {
                Trade = true,
                Probability = probability,
                ModelVersion = _artifact.ModelVersion,
                ReasonCode = "MetaModelWeakExpectancy",
                BucketFallbackLevel = fallbackLevel,
                RiskMultiplier = Math.Clamp(_options.WeakPositiveRiskMultiplier, 0m, 1m)
            };
        }

        return new MetaLabelDecision
        {
            Trade = true,
            Probability = probability,
            ModelVersion = _artifact.ModelVersion,
            ReasonCode = "MetaModelStrongExpectancy",
            BucketFallbackLevel = fallbackLevel,
            RiskMultiplier = 1m
        };
    }

    private (MetaModelBucket? Bucket, int? Level) Select(
        IEnumerable<MetaModelBucket> cohort,
        decimal alignment,
        string? cciState,
        string? confluenceState)
    {
        MetaModelBucket? exact = Best(cohort.Where(candidate =>
            (candidate.CciState is not null || candidate.StructuralConfluenceState is not null) &&
            string.Equals(candidate.CciState, cciState, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.StructuralConfluenceState, confluenceState, StringComparison.OrdinalIgnoreCase) &&
            ContainsAlignment(candidate, alignment)));
        if (exact is not null)
            return (exact, 1);

        MetaModelBucket? aligned = Best(cohort.Where(candidate =>
            candidate.CciState is null && candidate.StructuralConfluenceState is null &&
            (candidate.AlignmentFrom > 0m || candidate.AlignmentTo < 1m) &&
            ContainsAlignment(candidate, alignment)));
        if (aligned is not null)
            return (aligned, 2);

        MetaModelBucket? confidenceOnly = Best(cohort.Where(candidate =>
            candidate.CciState is null && candidate.StructuralConfluenceState is null &&
            candidate.AlignmentFrom == 0m && candidate.AlignmentTo == 1m));
        return confidenceOnly is null ? (null, null) : (confidenceOnly, 3);
    }

    private MetaModelBucket? Best(IEnumerable<MetaModelBucket> candidates) => candidates
        .Where(candidate => candidate.Samples >= _options.MinimumSamples)
        .OrderByDescending(candidate => candidate.Samples)
        .ThenBy(candidate => candidate.AlignmentFrom)
        .FirstOrDefault();

    private static bool ContainsAlignment(MetaModelBucket bucket, decimal alignment) =>
        alignment >= bucket.AlignmentFrom &&
        (alignment < bucket.AlignmentTo || bucket.AlignmentTo == 1m && alignment <= 1m);
}
