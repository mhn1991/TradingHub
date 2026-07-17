using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Training.Experiments;
using Simulator.Calibration;
using Simulator.Models;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class MetaModelCalibratorTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Calibrate_GroupsByStrategyRegimeConfidenceAndAlignment()
    {
        SimulatedTradeRecord[] trades =
        [
            Trade(confidence: 61m, alignment: 0.6m, rMultiple: 1.0m),
            Trade(confidence: 62m, alignment: 0.6m, rMultiple: 1.5m),
            Trade(confidence: 61m, alignment: 0.6m, rMultiple: -0.5m),
            Trade(confidence: 61m, alignment: 0.1m, rMultiple: 2.0m) // different alignment bucket
        ];

        MetaModelArtifact artifact = MetaModelCalibrator.Calibrate(
            trades,
            confidenceBucketWidth: 5m,
            alignmentBucketWidth: 0.25m,
            calibrationId: "cal-1",
            modelVersion: "meta-v1",
            trainingFrom: Start,
            trainingTo: Start.AddDays(30),
            dataHash: "hash",
            createdAt: DateTimeOffset.UtcNow);

        Assert.That(artifact.Buckets, Has.Count.EqualTo(2));

        MetaModelBucket mainBucket = artifact.Buckets.Single(bucket => bucket.AlignmentFrom == 0.5m);
        Assert.Multiple(() =>
        {
            Assert.That(mainBucket.Samples, Is.EqualTo(3));
            Assert.That(mainBucket.ConfidenceFrom, Is.EqualTo(60m));
            Assert.That(mainBucket.ConfidenceTo, Is.EqualTo(65m));
            Assert.That(mainBucket.WinRate, Is.EqualTo(2m / 3m));
            Assert.That(mainBucket.ExpectedR, Is.EqualTo((1.0m + 1.5m - 0.5m) / 3m));
        });

        MetaModelBucket lowAlignmentBucket = artifact.Buckets.Single(bucket => bucket.AlignmentFrom == 0.0m);
        Assert.That(lowAlignmentBucket.Samples, Is.EqualTo(1));
    }

    [Test]
    public void Calibrate_TradesWithoutAlignment_AreExcluded()
    {
        SimulatedTradeRecord[] trades = [Trade(confidence: 61m, alignment: null, rMultiple: 1.0m)];

        MetaModelArtifact artifact = MetaModelCalibrator.Calibrate(
            trades,
            confidenceBucketWidth: 5m,
            alignmentBucketWidth: 0.25m,
            calibrationId: "cal-1",
            modelVersion: "meta-v1",
            trainingFrom: Start,
            trainingTo: Start.AddDays(30),
            dataHash: "hash",
            createdAt: DateTimeOffset.UtcNow);

        Assert.That(artifact.Buckets, Is.Empty);
    }

    [Test]
    public void Calibrate_ProducesArtifactConsumableByCalibratedSetupMetaModel()
    {
        SimulatedTradeRecord[] trades =
        [
            Trade(confidence: 61m, alignment: 0.6m, rMultiple: 1.0m),
            Trade(confidence: 62m, alignment: 0.6m, rMultiple: 1.5m)
        ];

        MetaModelArtifact artifact = MetaModelCalibrator.Calibrate(
            trades,
            confidenceBucketWidth: 5m,
            alignmentBucketWidth: 0.25m,
            calibrationId: "cal-1",
            modelVersion: "meta-v1",
            trainingFrom: Start,
            trainingTo: Start.AddDays(30),
            dataHash: "hash",
            createdAt: DateTimeOffset.UtcNow);

        Assert.That(() => new CalibratedSetupMetaModel(artifact, new MetaModelPolicyOptions()), Throws.Nothing);
    }

    private static SimulatedTradeRecord Trade(decimal confidence, decimal? alignment, decimal rMultiple) => new()
    {
        StrategyId = "improved",
        StrategyName = "Improved Progressive",
        SetupId = $"setup-{Guid.NewGuid():N}",
        Instrument = Instrument,
        Side = OrderSide.Buy,
        SetupStartedAt = Start,
        SignalCreatedAt = Start,
        OpenedAt = Start,
        ClosedAt = Start.AddHours(2),
        EntryRegime = MarketRegime.TrendingUp,
        EntryConfidence = confidence,
        EntryMultiTimeframeAlignment = alignment,
        RMultiple = rMultiple,
        SetupReason = "fixture"
    };
}
