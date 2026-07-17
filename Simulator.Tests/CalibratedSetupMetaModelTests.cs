using Agent.Models;
using ChartAnnotator.Regime;
using RiskManager.Calibration;
using Simulator.Calibration;

namespace Simulator.Tests;

[TestFixture]
public sealed class CalibratedSetupMetaModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Evaluate_NoMatchingBucket_ReturnsNeutral()
    {
        var model = new CalibratedSetupMetaModel(Artifact([]), new MetaModelPolicyOptions());

        MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Trade, Is.True);
            Assert.That(decision.Probability, Is.EqualTo(0.5m));
            Assert.That(decision.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(decision.ReasonCode, Is.EqualTo("MetaModelBucketUnavailable"));
        });
    }

    [Test]
    public void Evaluate_BucketBelowMinimumSamples_ReturnsNeutral()
    {
        MetaModelBucket bucket = Bucket(samples: 5, winRate: 0.8m, expectedR: 1.0m);
        var model = new CalibratedSetupMetaModel(Artifact([bucket]), new MetaModelPolicyOptions { MinimumSamples = 30 });

        MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

        Assert.That(decision.ReasonCode, Is.EqualTo("MetaModelBucketUnavailable"));
    }

    [Test]
    public void Evaluate_NegativeExpectedR_RejectsTrade()
    {
        MetaModelBucket bucket = Bucket(samples: 50, winRate: 0.3m, expectedR: -0.4m);
        var model = new CalibratedSetupMetaModel(Artifact([bucket]), new MetaModelPolicyOptions { MinimumSamples = 30 });

        MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Trade, Is.False);
            Assert.That(decision.RiskMultiplier, Is.Zero);
            Assert.That(decision.ReasonCode, Is.EqualTo("MetaModelNegativeExpectancy"));
        });
    }

    [Test]
    public void Evaluate_WeakPositiveExpectedR_ReducesRisk()
    {
        MetaModelBucket bucket = Bucket(samples: 50, winRate: 0.55m, expectedR: 0.1m);
        var model = new CalibratedSetupMetaModel(
            Artifact([bucket]),
            new MetaModelPolicyOptions
            {
                MinimumSamples = 30,
                WeakPositiveExpectedRThreshold = 0.2m,
                WeakPositiveRiskMultiplier = 0.5m
            });

        MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Trade, Is.True);
            Assert.That(decision.RiskMultiplier, Is.EqualTo(0.5m));
            Assert.That(decision.ReasonCode, Is.EqualTo("MetaModelWeakExpectancy"));
        });
    }

    [Test]
    public void Evaluate_StrongPositiveExpectedR_NeverExceedsFullRisk()
    {
        MetaModelBucket bucket = Bucket(samples: 50, winRate: 0.75m, expectedR: 5.0m);
        var model = new CalibratedSetupMetaModel(Artifact([bucket]), new MetaModelPolicyOptions { MinimumSamples = 30 });

        MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Trade, Is.True);
            Assert.That(decision.RiskMultiplier, Is.EqualTo(1m), "A strong signal must never leverage above base risk.");
            Assert.That(decision.ReasonCode, Is.EqualTo("MetaModelStrongExpectancy"));
        });
    }

    [Test]
    public void Evaluate_AcrossManyRandomBuckets_RiskMultiplierNeverExceedsOne()
    {
        var random = new Random(17);
        for (int i = 0; i < 200; i++)
        {
            decimal expectedR = (decimal)(random.NextDouble() * 10 - 2); // [-2, 8)
            MetaModelBucket bucket = Bucket(samples: 50, winRate: 0.5m, expectedR: expectedR);
            var model = new CalibratedSetupMetaModel(Artifact([bucket]), new MetaModelPolicyOptions { MinimumSamples = 30 });

            MetaLabelDecision decision = model.Evaluate(Features(confidence: 62m, alignment: 0.6m));

            Assert.That(decision.RiskMultiplier, Is.InRange(0m, 1m));
            Assert.That(() => decision.Validate(), Throws.Nothing);
        }
    }

    [Test]
    public void Constructor_MismatchedFeatureSchemaHash_Throws()
    {
        MetaModelArtifact artifact = Artifact([]) with { FeatureSchemaHash = "some-other-schema" };

        Assert.Throws<ArgumentException>(() => new CalibratedSetupMetaModel(artifact, new MetaModelPolicyOptions()));
    }

    private static MetaModelArtifact Artifact(IReadOnlyList<MetaModelBucket> buckets) => new()
    {
        SchemaVersion = 1,
        CalibrationId = "meta-test",
        ModelVersion = "meta-v1",
        FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
        TrainingFrom = Start.AddMonths(-1),
        TrainingTo = Start,
        DataHash = "hash",
        CreatedAt = Start,
        Buckets = buckets
    };

    private static MetaModelBucket Bucket(int samples, decimal winRate, decimal expectedR) => new()
    {
        StrategyId = "improved",
        Regime = MarketRegime.TrendingUp.ToString(),
        ConfidenceFrom = 60m,
        ConfidenceTo = 65m,
        AlignmentFrom = 0.5m,
        AlignmentTo = 0.75m,
        Samples = samples,
        WinRate = winRate,
        BrierScore = 0.2m,
        ExpectedR = expectedR
    };

    private static MetaLabelFeatures Features(decimal confidence, decimal alignment) => new()
    {
        StrategyId = "improved",
        Instrument = "FX:GBP/USD",
        AvailableAt = Start,
        Direction = AgentAction.Buy,
        Regime = MarketRegime.TrendingUp,
        RegimeConfidence = 80m,
        SetupConfidence = confidence,
        MultiTimeframeAlignment = alignment,
        PriceActionEvents = [],
        FeatureSchemaVersion = MetaLabelFeatureFactory.SchemaVersion
    };
}
