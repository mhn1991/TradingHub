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
/// Proves the request-level wiring (blueprint §19 Phase 7's "real consumer") actually works: a
/// <see cref="BacktestRequest"/> with a pinned, approved, compatible artifact comes out with its
/// assignment's <see cref="StrategyInstrumentAssignment.AgentDefinitionOverride"/> correctly
/// overlaid; a request with no pin is untouched; a request whose pin is incompatible throws before
/// ever reaching the engine.
/// </summary>
[TestFixture]
public sealed class IndicatorConfluenceRequestOverlayResolverTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-request-overlay-tests", Guid.NewGuid().ToString("N"));
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
        var manifest = new IndicatorConfluenceCalibrationManifest();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        var artifact = new IndicatorCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = "request-overlay-test",
            StrategyId = identity.StrategyId,
            StrategyImplementationVersion = identity.StrategyImplementationVersion,
            OptionsSchemaVersion = identity.OptionsSchemaVersion,
            ManifestVersion = manifest.ManifestVersion,
            Scope = IndicatorCalibrationArtifact.InstrumentScope,
            Instrument = Instrument.Value,
            TimeframeTopologyHash = identity.TimeframeTopologyHash,
            CandleDataIdentityHash = "candle-hash",
            BaselineConfigurationHash = IndicatorCalibrationHash.ComputeOfObject(baseline.IndicatorConfluence),
            ResolvedCandidateConfigurationHash = "resolved-hash",
            Overrides =
            [
                new CalibratedParameterOverride
                {
                    ParameterId = "minimum-adx",
                    DefaultValue = 20m,
                    CalibratedValue = 25m,
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

        BacktestRequest result = await IndicatorConfluenceRequestOverlayResolver.ApplyToRequestAsync(request, repository);

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
            IndicatorCalibrationArtifactId = artifactId
        };
        BacktestRequest request = BuildRequest(assignment);

        BacktestRequest result = await IndicatorConfluenceRequestOverlayResolver.ApplyToRequestAsync(request, repository);

        StructuralConfluenceStrategyOptions overlaid = result.StrategyAssignments![0].AgentDefinitionOverride!.StructuralConfluence!;
        Assert.That(overlaid.IndicatorConfluence.MinimumAdx, Is.EqualTo(25m));
    }

    [Test]
    public void ApplyToRequestAsync_PinnedIncompatibleArtifact_ThrowsBeforeReachingTheEngine()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = Guid.NewGuid() // never stored - guaranteed missing
        };
        BacktestRequest request = BuildRequest(assignment);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorConfluenceRequestOverlayResolver.ApplyToRequestAsync(request, repository));
    }

    [Test]
    public void ApplyToRequestAsync_PinOnNonStructuralConfluenceStrategy_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.LegacyProgressive,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = Guid.NewGuid()
        };
        BacktestRequest request = BuildRequest(assignment) with { Strategies = [TradingAgentTypeIds.LegacyProgressive] };

        Assert.ThrowsAsync<ArgumentException>(() =>
            IndicatorConfluenceRequestOverlayResolver.ApplyToRequestAsync(request, repository));
    }
}
