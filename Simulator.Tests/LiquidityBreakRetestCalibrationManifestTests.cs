using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 8 (second calibration-enabled strategy) gate items that don't need a real
/// backtest: the manifest itself is well-formed, its descriptors correctly read/apply onto
/// <see cref="LiquidityBreakRetestOptions"/>, and its cross-parameter constraint behaves as
/// documented. The generic search engine and the real-backtest adapter are already proven by
/// Phase 4/6's own tests (they are strategy-agnostic) - this file only exercises what's actually
/// new here.
/// </summary>
[TestFixture]
public sealed class LiquidityBreakRetestCalibrationManifestTests
{
    private static readonly LiquidityBreakRetestCalibrationManifest Manifest = new();

    [Test]
    public void Manifest_IsStructurallyValid()
    {
        Assert.DoesNotThrow(() => Manifest.Validate());
    }

    [Test]
    public void Manifest_MatchesFrozenCompatibilityIdentity()
    {
        Assert.That(Manifest.StrategyId, Is.EqualTo(LiquidityBreakRetestCalibrationCompatibility.Instance.StrategyId));
        Assert.That(Manifest.OptionsSchemaVersion, Is.EqualTo(LiquidityBreakRetestCalibrationCompatibility.Instance.OptionsSchemaVersion));
    }

    [Test]
    public void ParameterDescriptors_ReadAndApplyRoundTripCorrectly()
    {
        var baseline = new LiquidityBreakRetestOptions();
        CalibrationCandidate candidate = CalibrationCandidate.FromEffectiveOptions(baseline, Manifest);

        foreach (ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> parameter in Manifest.Parameters)
            Assert.That(candidate.RequireNumericValue(parameter.ParameterId), Is.EqualTo(parameter.Read(baseline)));

        LiquidityBreakRetestOptions applied = candidate
            .WithNumericValue("minimum-adx", 25m)
            .ApplyTo(baseline, Manifest);
        Assert.That(applied.MinimumAdx, Is.EqualTo(25m));
        // Untouched fields keep their baseline value.
        Assert.That(applied.MinimumPoolQuality, Is.EqualTo(baseline.MinimumPoolQuality));
    }

    [Test]
    public void AblationDescriptors_ReadAndApplyRoundTripCorrectly()
    {
        var baseline = new LiquidityBreakRetestOptions();
        foreach (ICalibrationAblationDescriptor<LiquidityBreakRetestOptions> ablation in Manifest.Ablations)
        {
            bool original = ablation.Read(baseline);
            LiquidityBreakRetestOptions flipped = ablation.Apply(baseline, !original);
            Assert.That(ablation.Read(flipped), Is.EqualTo(!original));
        }
    }

    [Test]
    public void RetestDistanceBelowDisplacementConstraint_RejectsAnInvalidCombination()
    {
        ICalibrationConstraint<LiquidityBreakRetestOptions> constraint = Manifest.Constraints
            .Single(item => item.ConstraintId == "retest-distance-below-displacement");

        var valid = new LiquidityBreakRetestOptions { MinimumDisplacementAtr = 0.8m, MaximumRetestDistanceAtr = 0.35m };
        var invalid = new LiquidityBreakRetestOptions { MinimumDisplacementAtr = 0.35m, MaximumRetestDistanceAtr = 0.8m };

        Assert.That(constraint.Validate(valid).IsValid, Is.True);
        Assert.That(constraint.Validate(invalid).IsValid, Is.False);
    }

    [Test]
    public void CandidateValidator_RejectsOutOfBoundsValue()
    {
        var baseline = new LiquidityBreakRetestOptions();
        CalibrationCandidate candidate = CalibrationCandidate.FromEffectiveOptions(baseline, Manifest)
            .WithNumericValue("minimum-adx", 500m); // far outside the descriptor's hard bounds

        bool isValid = CalibrationCandidateValidator.TryBuildValidOptions(
            candidate, baseline, Manifest, out _, out string? reason);
        Assert.That(isValid, Is.False);
        Assert.That(reason, Does.Contain("minimum-adx"));
    }
}
