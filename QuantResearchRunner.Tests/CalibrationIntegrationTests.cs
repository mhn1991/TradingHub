using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Calibration;
using QuantResearch.Training.Mapping;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;
using TradeManager;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Proves the Stage 6 pipeline end-to-end (trade mapping -> ConfidenceCalibrator/
/// TradeManagementCohortAnalyzer -> FileCalibrationArtifactRepository -> the existing
/// production consumers) using directly-constructed trades rather than a live backtest -
/// producing real trades through the full confidence-gated dual-strategy pipeline from
/// synthetic candle data proved too fragile for a reliable CI test (see
/// FeatureSwitchMapperTests' doc comment for the same finding), so this integration proof
/// starts one layer in, at the trade-record boundary, which is exactly where the CLI
/// handlers (RunCalibrateSetupsAsync/RunCalibrateManagementAsync) hand off to this pipeline.
/// </summary>
[TestFixture]
public sealed class CalibrationIntegrationTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task SetupCalibrationPipeline_ProducesArtifact_ConsumableByProductionPolicy()
    {
        SimulatedTradeRecord[] trades =
        [
            Trade(confidence: 61m, rMultiple: 1.2m),
            Trade(confidence: 62m, rMultiple: 0.8m),
            Trade(confidence: 63m, rMultiple: -0.5m),
            Trade(confidence: 64m, rMultiple: 1.0m)
        ];
        SetupOutcome[] outcomes = ResearchTradeMapper.ToSetupOutcomes(trades).ToArray();

        SetupCalibrationArtifact artifact = ConfidenceCalibrator.Calibrate(
            outcomes,
            bucketWidth: 5m,
            calibrationId: "setup-integration-test",
            trainingFrom: Start,
            trainingTo: Start.AddDays(30),
            strategyVersion: "improved",
            featureSchemaHash: MetaLabelFeatureFactory.SchemaVersion,
            dataHash: "test-data-hash",
            createdAt: DateTimeOffset.UtcNow);

        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        CalibrationArtifactMetadata metadata = await repository.StoreSetupAsync(artifact);
        SetupCalibrationArtifact? loaded = await repository.GetSetupAsync(metadata.Id);

        Assert.That(loaded, Is.Not.Null);

        var policy = new SetupCalibrationPolicy(
            loaded,
            new SetupCalibrationPolicyOptions { Enabled = true },
            requiredFeatureSchemaHash: MetaLabelFeatureFactory.SchemaVersion);
        SetupCalibrationDecision decision = policy.Evaluate("improved", "FX", MarketRegime.TrendingUp.ToString(), 62m);

        Assert.Multiple(() =>
        {
            Assert.That(loaded!.Buckets, Is.Not.Empty);
            Assert.That(decision.RiskMultiplier, Is.InRange(0m, 1m));
        });
    }

    [Test]
    public async Task ManagementCalibrationPipeline_ProducesArtifact_ConsumableByProductionPolicy()
    {
        SimulatedTradeRecord[] trades =
        [
            TradeWithPath(confidence: 61m),
            TradeWithPath(confidence: 63m)
        ];
        TradePathObservation[] observations = trades
            .Select(ResearchTradeMapper.ToTradePathObservation)
            .Where(observation => observation is not null)
            .Select(observation => observation!)
            .ToArray();
        Assert.That(observations, Has.Length.EqualTo(2));

        TradeManagementCalibration artifact = TradeManagementCohortAnalyzer.Analyze(
            observations,
            calibrationId: "management-integration-test",
            sourceDataHash: "test-data-hash",
            createdAt: DateTimeOffset.UtcNow);

        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        CalibrationArtifactMetadata metadata = await repository.StoreManagementAsync(artifact);
        TradeManagementCalibration? loaded = await repository.GetManagementAsync(metadata.Id);

        Assert.That(loaded, Is.Not.Null);

        var policy = new TradeManagementCalibrationPolicy(loaded!, minimumSamples: 1);
        (PositionManagementOptions Options, string ReasonCode) result = policy.Apply(
            PositionManagementOptions.ImprovedDefaults,
            new TradeManagementCalibrationContext
            {
                StrategyId = "improved",
                InstrumentGroup = "FX",
                Regime = MarketRegime.TrendingUp.ToString(),
                SetupType = "Breakout",
                Direction = "Buy",
                Session = "London",
                VolatilityBucket = "Normal",
                Confidence = 62m
            });

        Assert.That(result.Options, Is.Not.Null);
    }

    private static SimulatedTradeRecord Trade(decimal confidence, decimal rMultiple) => new()
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
        EntrySetupType = "Breakout",
        EntrySession = "London",
        EntryVolatilityBucket = "Normal",
        RMultiple = rMultiple,
        SetupReason = "fixture"
    };

    private static SimulatedTradeRecord TradeWithPath(decimal confidence) => Trade(confidence, rMultiple: 1.1m) with
    {
        MaximumFavourableExcursionR = 1.5m,
        MaximumAdverseExcursionR = -0.2m,
        ExcursionPath =
        [
            new SimulatedTradePathPoint { BarsAfterEntry = 0, MfeR = 0.2m, MaeR = -0.1m },
            new SimulatedTradePathPoint { BarsAfterEntry = 1, MfeR = 0.8m, MaeR = -0.2m },
            new SimulatedTradePathPoint { BarsAfterEntry = 2, MfeR = 1.5m, MaeR = -0.2m }
        ]
    };

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", "calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
