using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Wires <see cref="SupplyDemandPullbackCalibrationManifest"/> and
/// <see cref="BacktestCandidateEvaluatorFactory{TOptions}"/> together behind the non-generic
/// <see cref="IIndicatorCalibrationStrategyAdapter"/> seam, mirroring
/// <see cref="LiquidityBreakRetestCalibrationStrategyAdapter"/> exactly.
/// </summary>
/// <param name="resolveBaselineOptions">
/// Resolves the actual effective <see cref="StructuralConfluenceStrategyOptions"/> for a given
/// instrument (everything except the timeframe stack, which <see cref="CalibrationTimeframeStack.ApplyTo"/>
/// always overlays from the request afterward). Defaults to the strategy's own declared defaults -
/// wiring this to the instrument's real configured assignment is Phase 7 scope, already applicable
/// once filled in.
/// </param>
/// <param name="candidateCacheRootDirectory">
/// Root directory for the per-calibration-run persistent candidate cache (resume support) - see the
/// identical parameter's remarks on <see cref="IndicatorConfluenceCalibrationStrategyAdapter"/>.
/// </param>
public sealed class SupplyDemandPullbackCalibrationStrategyAdapter(
    Func<InstrumentKey, StructuralConfluenceStrategyOptions>? resolveBaselineOptions = null,
    string candidateCacheRootDirectory = ".cache/supply-demand-pullback-calibration-candidates")
    : IIndicatorCalibrationStrategyAdapter
{
    private readonly SupplyDemandPullbackCalibrationManifest _manifest = new();
    private readonly Func<InstrumentKey, StructuralConfluenceStrategyOptions> _resolveBaselineOptions =
        resolveBaselineOptions ?? (_ => new StructuralConfluenceStrategyOptions());

    public string StrategyId => _manifest.StrategyId;
    public string ManifestVersion => _manifest.ManifestVersion;

    public CalibrationBudgetPreview Preview(IndicatorCalibrationRequest request, int maximumCoordinatePasses) =>
        EvaluationBudgetEstimator.Estimate(request, _manifest, maximumCoordinatePasses);

    public async Task<(IndicatorCalibrationOrchestrationResult Result, IndicatorCalibrationArtifact Artifact)> RunAsync(
        IndicatorCalibrationRequest request,
        IBacktestApplicationService backtests,
        string calibrationId,
        string experimentLedgerId,
        string experimentLedgerChecksum,
        int maximumCoordinatePasses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(backtests);

        StructuralConfluenceStrategyOptions baselineStructuralOptions = CalibrationTimeframeStack.ApplyTo(
            _resolveBaselineOptions(request.Instrument), request.TimeframeTopology);
        (PositionManagementOptions legacyManagement, PositionManagementOptions improvedManagement,
                PositionManagementOptions structuralManagement) =
            CalibrationTimeframeStack.BuildPositionManagementOverrides(
                request.TimeframeTopology.ExecutionInterval, request.TimeframeTopology.SetupInterval,
                request.TimeframeTopology.TrendIntervals[0]);

        var analysisIntervals = new List<BarInterval> { request.TimeframeTopology.SetupInterval };
        analysisIntervals.AddRange(request.TimeframeTopology.ConfirmationIntervals);
        analysisIntervals.AddRange(request.TimeframeTopology.TrendIntervals);
        analysisIntervals.AddRange(request.TimeframeTopology.ManagementIntervals);

        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = request.Instrument,
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            RuntimeTemplate = new BacktestRuntimeOptions
            {
                ExecutionInterval = request.TimeframeTopology.ExecutionInterval,
                AnalysisBaseInterval = request.TimeframeTopology.AnalysisBaseInterval,
                AnalysisIntervals = analysisIntervals.Distinct().ToArray(),
                BaseCandleGapPolicy = request.TimeframeTopology.AlignmentPolicy,
                MaximumParallelStrategies = 1,
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                LegacyPositionManagement = legacyManagement,
                ImprovedPositionManagement = improvedManagement,
                StructuralPositionManagement = structuralManagement
            },
            WarmupDays = request.Timeline.WarmupDays,
            Cache = new FileCalibrationCandidateCache(Path.Combine(candidateCacheRootDirectory, calibrationId))
        };

        var factory = new BacktestCandidateEvaluatorFactory<SupplyDemandPullbackOptions>(
            backtests,
            _manifest,
            baselineStructuralOptions.SupplyDemandPullback,
            pullbackOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = baselineStructuralOptions with { SupplyDemandPullback = pullbackOptions }
            },
            settings);

        IndicatorCalibrationOrchestrationResult result = await IndicatorCalibrationOrchestrator.RunAsync(
            request, baselineStructuralOptions.SupplyDemandPullback, _manifest, factory,
            maximumCoordinatePasses, cancellationToken).ConfigureAwait(false);

        CalibrationCompatibilityIdentity compatibility =
            SupplyDemandPullbackCalibrationCompatibility.Instance.Describe(request.TimeframeTopology);
        IndicatorCalibrationArtifact artifact = IndicatorCalibrationArtifactFactory.BuildArtifact(
            result, _manifest, baselineStructuralOptions.SupplyDemandPullback, compatibility,
            calibrationId: calibrationId,
            instrument: request.Instrument.Value,
            candleDataIdentityHash: ComputeCandleDataIdentityHash(request),
            baselineConfigurationHash: request.BaselineConfigurationHash,
            experimentLedgerId: experimentLedgerId,
            experimentLedgerChecksum: experimentLedgerChecksum,
            createdAt: DateTimeOffset.UtcNow);

        return (result, artifact);
    }

    /// <summary>See the identical helper's remarks on <c>IndicatorConfluenceCalibrationStrategyAdapter</c> - same proxy-identity rationale.</summary>
    private static string ComputeCandleDataIdentityHash(IndicatorCalibrationRequest request) =>
        IndicatorCalibrationHash.ComputeOfObject(new
        {
            request.Instrument.Value,
            request.Timeline.LearningFrom,
            request.Timeline.LearningTo,
            request.Timeline.ExternalHoldoutFrom,
            request.Timeline.ExternalHoldoutTo,
            TimeframeTopologyHash = request.TimeframeTopology.ComputeHash()
        });
}
