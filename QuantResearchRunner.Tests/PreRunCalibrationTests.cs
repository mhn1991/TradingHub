using Agent.Configuration;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;
using QuantResearch.Training.Pipeline;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class PreRunCalibrationTests
{
    [Test]
    public void Planner_DefaultWindow_IsTwoMonthsEndingTenDaysBeforeEvaluation()
    {
        DateTimeOffset evaluationFrom = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        PreRunCalibrationWindow window = PreRunCalibrationPlanner.Resolve(evaluationFrom);

        Assert.Multiple(() =>
        {
            Assert.That(window.EmbargoDays, Is.EqualTo(10));
            Assert.That(window.TrainMonths, Is.EqualTo(2));
            Assert.That(window.TrainTo, Is.EqualTo(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(window.TrainFrom, Is.EqualTo(new DateTimeOffset(2026, 4, 21, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(window.TrainFrom, Is.LessThan(window.TrainTo));
            Assert.That(window.TrainTo, Is.LessThan(evaluationFrom));
        });
    }

    [Test]
    public void Planner_RejectsOutOfRangeEmbargo()
    {
        DateTimeOffset evaluationFrom = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.That(
            () => PreRunCalibrationPlanner.Resolve(evaluationFrom, trainMonths: 1, embargoDays: 10_000),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Merger_CombinesBucketsAndCohortsByStrategy()
    {
        DateTimeOffset from = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        SetupCalibrationArtifact setupLegacy = new()
        {
            SchemaVersion = 1,
            CalibrationId = "setup-legacy",
            TrainingFrom = from,
            TrainingTo = to,
            Instruments = ["FX"],
            StrategyVersion = "legacy",
            FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
            Parameters = new Dictionary<string, string> { ["bucketWidth"] = "5" },
            TotalSamples = 10,
            CreatedAt = to,
            DataHash = "legacy-hash",
            Buckets =
            [
                new SetupCalibrationBucket
                {
                    StrategyId = "legacy",
                    InstrumentGroup = "FX",
                    Regime = "TrendingUp",
                    ConfidenceFrom = 55m,
                    ConfidenceTo = 60m,
                    Samples = 10,
                    WinRate = 0.6m,
                    AverageR = 0.4m,
                    ExpectedR = 0.4m,
                    BrierScore = 0.2m
                }
            ]
        };
        SetupCalibrationArtifact setupImproved = setupLegacy with
        {
            CalibrationId = "setup-improved",
            StrategyVersion = "improved",
            DataHash = "improved-hash",
            TotalSamples = 12,
            Buckets =
            [
                setupLegacy.Buckets[0] with { StrategyId = "improved", Samples = 12 }
            ]
        };

        SetupCalibrationArtifact mergedSetup =
            CalibrationArtifactMerger.MergeSetup([setupLegacy, setupImproved], "merged-setup");

        MetaModelArtifact metaLegacy = new()
        {
            SchemaVersion = 1,
            CalibrationId = "meta-legacy",
            ModelVersion = "meta-legacy",
            FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
            TrainingFrom = from,
            TrainingTo = to,
            DataHash = "legacy-hash",
            CreatedAt = to,
            Buckets =
            [
                new MetaModelBucket
                {
                    StrategyId = "legacy",
                    Regime = "TrendingUp",
                    ConfidenceFrom = 55m,
                    ConfidenceTo = 60m,
                    AlignmentFrom = 0m,
                    AlignmentTo = 0.25m,
                    Samples = 8,
                    WinRate = 0.55m,
                    BrierScore = 0.2m,
                    ExpectedR = 0.3m
                }
            ]
        };
        MetaModelArtifact metaImproved = metaLegacy with
        {
            CalibrationId = "meta-improved",
            ModelVersion = "meta-improved",
            DataHash = "improved-hash",
            Buckets = [metaLegacy.Buckets[0] with { StrategyId = "improved" }]
        };
        MetaModelArtifact mergedMeta =
            CalibrationArtifactMerger.MergeMetaModel([metaLegacy, metaImproved], "merged-meta");

        TradeManagementCalibration mgmtLegacy = new()
        {
            CalibrationId = "mgmt-legacy",
            SourceDataHash = "legacy-hash",
            CreatedAt = to,
            Cohorts =
            [
                new TradeManagementCohort
                {
                    CohortId = "legacy|FX",
                    StrategyId = "legacy",
                    InstrumentGroup = "FX",
                    Regime = "TrendingUp",
                    SetupType = "BreakRetest",
                    Direction = "Buy",
                    Session = "London",
                    VolatilityBucket = "Normal",
                    ConfidenceBucket = 55,
                    Samples = 5,
                    WinnerMfe80PercentileByBar = new Dictionary<int, decimal> { [1] = 0.5m },
                    MedianMaeBeforeHalfR = 0.2m,
                    MedianDurationBars = 10m,
                    MedianExitEfficiency = 0.5m,
                    MedianStopDistance = 0.001m
                }
            ]
        };
        TradeManagementCalibration mgmtImproved = mgmtLegacy with
        {
            CalibrationId = "mgmt-improved",
            SourceDataHash = "improved-hash",
            Cohorts =
            [
                mgmtLegacy.Cohorts[0] with { CohortId = "improved|FX", StrategyId = "improved" }
            ]
        };
        TradeManagementCalibration mergedMgmt =
            CalibrationArtifactMerger.MergeManagement([mgmtLegacy, mgmtImproved], "merged-mgmt");

        Assert.Multiple(() =>
        {
            // CalibrationArtifactMerger.NormalizeStrategyId canonicalises via TradingAgentTypeIds,
            // so the "legacy"/"improved" aliases fed in above come back as their full ids. Asserting
            // the canonical form is what keeps bucket lookups consistent across merged artifacts.
            Assert.That(
                mergedSetup.Buckets.Select(b => b.StrategyId),
                Is.EquivalentTo(new[] { TradingAgentTypeIds.LegacyProgressive, TradingAgentTypeIds.ImprovedProgressive }));
            Assert.That(mergedSetup.TotalSamples, Is.EqualTo(22));
            Assert.That(mergedSetup.StrategyVersion, Does.Contain("legacy").And.Contain("improved"));
            Assert.That(
                mergedMeta.Buckets.Select(b => b.StrategyId),
                Is.EquivalentTo(new[] { TradingAgentTypeIds.LegacyProgressive, TradingAgentTypeIds.ImprovedProgressive }));
            Assert.That(
                mergedMgmt.Cohorts.Select(c => c.StrategyId),
                Is.EquivalentTo(new[] { TradingAgentTypeIds.LegacyProgressive, TradingAgentTypeIds.ImprovedProgressive }));
            Assert.That(() => mergedSetup.Validate(MetaLabelFeatureFactory.SchemaVersion), Throws.Nothing);
            Assert.That(() => mergedMeta.Validate(MetaLabelFeatureFactory.SchemaVersion), Throws.Nothing);
            Assert.That(() => mergedMgmt.Validate(), Throws.Nothing);
        });
    }

}
