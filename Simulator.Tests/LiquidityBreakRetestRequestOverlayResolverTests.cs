using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// The <see cref="LiquidityBreakRetestOptions"/> counterpart to
/// <see cref="IndicatorConfluenceRequestOverlayResolverTests"/> - proves the second
/// calibration-enabled strategy's request-level overlay wiring works the same way: no-op when
/// unpinned, correct overlay when compatible, throws on a missing artifact.
/// </summary>
[TestFixture]
public sealed class LiquidityBreakRetestRequestOverlayResolverTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-lbr-request-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static BacktestRequest BuildRequest(StrategyInstrumentAssignment assignment) => new()
    {
        Instrument = Instrument,
        From = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        Strategies = [TradingAgentTypeIds.StructuralConfluence],
        StrategyAssignments = [assignment],
        InlineCandles = [],
        Runtime = new BacktestRuntimeOptions
        {
            SourceKind = HistoricalDataSourceKind.InlineTestData,
            ExecutionInterval = BarInterval.Minutes(1),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(15), BarInterval.Hours(1)],
            WarmupDays = 0,
            // StructuralConfluenceStrategyOptions defaults enable LiquiditySweepReversal/
            // SupplyDemandPullback/LiquidityBreakRetest, so BacktestRequest.Validate() requires
            // these two detectors on (see ValidateStructuralAnnotationRequirements).
            AnnotationOptions = new ChartAnnotationOptions
            {
                Liquidity = new LiquidityCalculationProfile { Enabled = true },
                SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
            }
        }
    };

    private static CalibrationEvidenceSummary BuildEvidence() => new()
    {
        FoldCount = 3,
        AcceptableFoldCount = 2,
        AcceptableFoldPercent = 66.7m,
        MedianValidationExpectancyR = 0.2m,
        MedianValidationDrawdownR = 1m,
        MedianValidationTradeCount = 25,
        TrainValidationDegradation = 0.05m,
        BaselineMedianExpectancyR = 0.1m,
        ImprovementOverBaseline = 0.1m,
        ExternalHoldoutExpectancyR = 0.15m,
        ExternalHoldoutDrawdownR = 1.2m,
        ExternalHoldoutTradeCount = 30,
        ExternalHoldoutBaselineExpectancyR = 0.05m,
        TotalCandidatesEvaluated = 200
    };

    private static async Task<Guid> StoreApprovedArtifactAsync(
        FileCalibrationArtifactRepository repository, TimeframeTopology topology, StructuralConfluenceStrategyOptions baseline)
    {
        var manifest = new LiquidityBreakRetestCalibrationManifest();
        CalibrationCompatibilityIdentity identity = LiquidityBreakRetestCalibrationCompatibility.Instance.Describe(topology);
        var artifact = new IndicatorCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = "lbr-request-overlay-test",
            StrategyId = identity.StrategyId,
            StrategyImplementationVersion = identity.StrategyImplementationVersion,
            OptionsSchemaVersion = identity.OptionsSchemaVersion,
            ManifestVersion = manifest.ManifestVersion,
            Scope = IndicatorCalibrationArtifact.InstrumentScope,
            Instrument = Instrument.Value,
            TimeframeTopologyHash = identity.TimeframeTopologyHash,
            CandleDataIdentityHash = "candle-hash",
            BaselineConfigurationHash = IndicatorCalibrationHash.ComputeOfObject(baseline.LiquidityBreakRetest),
            ResolvedCandidateConfigurationHash = "resolved-hash",
            Overrides =
            [
                new CalibratedParameterOverride
                {
                    ParameterId = "minimum-adx",
                    DefaultValue = 18m,
                    CalibratedValue = 22m,
                    FoldSupportPercent = 100m,
                    PlateauWidth = 0m,
                    SelectionStage = "CrossFoldAggregation"
                }
            ],
            AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal),
            Evidence = BuildEvidence(),
            Outcome = CalibrationOutcome.Improved,
            ExperimentLedgerId = "ledger-1",
            ExperimentLedgerChecksum = "checksum-1",
            PromotionStatus = CalibrationPromotionStatus.PendingReview,
            CreatedAt = DateTimeOffset.UtcNow
        };
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);
        await repository.UpdatePromotionStatusAsync(metadata.Id, CalibrationPromotionStatus.Approved);
        return metadata.Id;
    }

    [Test]
    public async Task ApplyToRequestAsync_NoAssignmentsPinned_ReturnsRequestUnchanged()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence, Instrument = Instrument
        };
        BacktestRequest request = BuildRequest(assignment);

        BacktestRequest result = await LiquidityBreakRetestRequestOverlayResolver.ApplyToRequestAsync(request, repository);

        Assert.That(result, Is.SameAs(request));
    }

    [Test]
    public async Task ApplyToRequestAsync_PinnedCompatibleArtifact_OverlaysTheAssignment()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new StructuralConfluenceStrategyOptions();
        var probeRequest = BuildRequest(new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence, Instrument = Instrument
        });
        TimeframeTopology topology = IndicatorConfluenceTimeframeTopology.Resolve(probeRequest.Runtime, baseline);
        Guid artifactId = await StoreApprovedArtifactAsync(repository, topology, baseline);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            LiquidityBreakRetestCalibrationArtifactId = artifactId
        };
        BacktestRequest request = BuildRequest(assignment);

        BacktestRequest result = await LiquidityBreakRetestRequestOverlayResolver.ApplyToRequestAsync(request, repository);

        StructuralConfluenceStrategyOptions overlaid = result.StrategyAssignments![0].AgentDefinitionOverride!.StructuralConfluence!;
        Assert.That(overlaid.LiquidityBreakRetest.MinimumAdx, Is.EqualTo(22m));
    }

    [Test]
    public void ApplyToRequestAsync_PinnedMissingArtifact_ThrowsBeforeReachingTheEngine()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            LiquidityBreakRetestCalibrationArtifactId = Guid.NewGuid()
        };
        BacktestRequest request = BuildRequest(assignment);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiquidityBreakRetestRequestOverlayResolver.ApplyToRequestAsync(request, repository));
    }

    [Test]
    public async Task BothArtifactsPinnedOnOneAssignment_ComposeWithoutInterfering()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new StructuralConfluenceStrategyOptions();
        var probeRequest = BuildRequest(new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence, Instrument = Instrument
        });
        TimeframeTopology topology = IndicatorConfluenceTimeframeTopology.Resolve(probeRequest.Runtime, baseline);

        Guid liquidityArtifactId = await StoreApprovedArtifactAsync(repository, topology, baseline);

        var indicatorManifest = new IndicatorConfluenceCalibrationManifest();
        CalibrationCompatibilityIdentity indicatorIdentity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        var indicatorArtifact = new IndicatorCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = "combined-test-indicator",
            StrategyId = indicatorIdentity.StrategyId,
            StrategyImplementationVersion = indicatorIdentity.StrategyImplementationVersion,
            OptionsSchemaVersion = indicatorIdentity.OptionsSchemaVersion,
            ManifestVersion = indicatorManifest.ManifestVersion,
            Scope = IndicatorCalibrationArtifact.InstrumentScope,
            Instrument = Instrument.Value,
            TimeframeTopologyHash = indicatorIdentity.TimeframeTopologyHash,
            CandleDataIdentityHash = "candle-hash",
            BaselineConfigurationHash = IndicatorCalibrationHash.ComputeOfObject(baseline.IndicatorConfluence),
            ResolvedCandidateConfigurationHash = "resolved-hash",
            Overrides =
            [
                new CalibratedParameterOverride
                {
                    ParameterId = "minimum-adx", DefaultValue = 20m, CalibratedValue = 30m,
                    FoldSupportPercent = 100m, PlateauWidth = 0m, SelectionStage = "CrossFoldAggregation"
                }
            ],
            AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal),
            Evidence = BuildEvidence(),
            Outcome = CalibrationOutcome.Improved,
            ExperimentLedgerId = "ledger-2",
            ExperimentLedgerChecksum = "checksum-2",
            PromotionStatus = CalibrationPromotionStatus.PendingReview,
            CreatedAt = DateTimeOffset.UtcNow
        };
        CalibrationArtifactMetadata indicatorMetadata = await repository.StoreIndicatorParametersAsync(indicatorArtifact);
        await repository.UpdatePromotionStatusAsync(indicatorMetadata.Id, CalibrationPromotionStatus.Approved);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = indicatorMetadata.Id,
            LiquidityBreakRetestCalibrationArtifactId = liquidityArtifactId
        };
        BacktestRequest request = BuildRequest(assignment);

        BacktestRequest afterIndicator = await IndicatorConfluenceRequestOverlayResolver.ApplyToRequestAsync(request, repository);
        BacktestRequest afterBoth = await LiquidityBreakRetestRequestOverlayResolver.ApplyToRequestAsync(afterIndicator, repository);

        StructuralConfluenceStrategyOptions overlaid = afterBoth.StrategyAssignments![0].AgentDefinitionOverride!.StructuralConfluence!;
        Assert.Multiple(() =>
        {
            Assert.That(overlaid.IndicatorConfluence.MinimumAdx, Is.EqualTo(30m), "The first overlay's change must survive the second.");
            Assert.That(overlaid.LiquidityBreakRetest.MinimumAdx, Is.EqualTo(22m), "The second overlay must apply on top.");
        });
    }
}
