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

        MetaModelBucket? bucket = _artifact.Buckets.FirstOrDefault(candidate =>
            string.Equals(candidate.StrategyId, features.StrategyId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Regime, features.Regime.ToString(), StringComparison.OrdinalIgnoreCase) &&
            features.SetupConfidence >= candidate.ConfidenceFrom &&
            features.SetupConfidence < candidate.ConfidenceTo &&
            features.MultiTimeframeAlignment >= candidate.AlignmentFrom &&
            features.MultiTimeframeAlignment < candidate.AlignmentTo);

        if (bucket is null || bucket.Samples < _options.MinimumSamples)
        {
            return new MetaLabelDecision
            {
                Trade = true,
                Probability = 0.5m,
                ModelVersion = _artifact.ModelVersion,
                ReasonCode = "MetaModelBucketUnavailable",
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
                RiskMultiplier = Math.Clamp(_options.WeakPositiveRiskMultiplier, 0m, 1m)
            };
        }

        return new MetaLabelDecision
        {
            Trade = true,
            Probability = probability,
            ModelVersion = _artifact.ModelVersion,
            ReasonCode = "MetaModelStrongExpectancy",
            RiskMultiplier = 1m
        };
    }
}
