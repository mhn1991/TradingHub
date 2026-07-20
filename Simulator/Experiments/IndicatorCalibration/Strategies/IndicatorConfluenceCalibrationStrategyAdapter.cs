using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Wires <see cref="IndicatorConfluenceCalibrationManifest"/> and
/// <see cref="BacktestCandidateEvaluatorFactory{TOptions}"/> together behind the non-generic
/// <see cref="IIndicatorCalibrationStrategyAdapter"/> seam (blueprint §19 Phase 6).
/// </summary>
/// <param name="resolveBaselineOptions">
/// Resolves the actual effective <see cref="StructuralConfluenceStrategyOptions"/> for a given
/// instrument. Defaults to the strategy's own declared defaults (<c>new StructuralConfluenceStrategyOptions()</c>)
/// when not supplied - wiring this to the instrument's real configured assignment (so a calibration
/// run measures against what is actually running, not just the strategy's factory defaults) is
/// Phase 7 scope (explicit assignment pinning); this constructor parameter is exactly the seam that
/// work will fill in, not a placeholder that needs replacing wholesale.
/// </param>
/// <param name="candidateCacheRootDirectory">
/// Root directory for the per-calibration-run persistent candidate cache (blueprint §14 resume).
/// Each <see cref="RunAsync"/> call uses the subdirectory <c>{root}/{calibrationId}</c> - re-running
/// the same calibration id against the same request (i.e. resuming) reuses whatever that directory
/// already holds, so already-completed real backtests are never re-run.
/// </param>
public sealed class IndicatorConfluenceCalibrationStrategyAdapter(
    Func<InstrumentKey, StructuralConfluenceStrategyOptions>? resolveBaselineOptions = null,
    string candidateCacheRootDirectory = ".cache/indicator-calibration-candidates")
    : IIndicatorCalibrationStrategyAdapter
{
    private readonly IndicatorConfluenceCalibrationManifest _manifest = new();
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

        StructuralConfluenceStrategyOptions baselineStructuralOptions = _resolveBaselineOptions(request.Instrument);

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
                StrategyExecutionMode = StrategyExecutionMode.Sequential
                // SourceKind left at its default (OandaCandles): no Candles are supplied below,
                // so BacktestCandidateEvaluator lets the engine read historical data itself,
                // exactly like a normal backtest.
            },
            WarmupDays = request.Timeline.WarmupDays,
            Cache = new FileCalibrationCandidateCache(Path.Combine(candidateCacheRootDirectory, calibrationId))
        };

        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            backtests,
            _manifest,
            baselineStructuralOptions.IndicatorConfluence,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = baselineStructuralOptions with { IndicatorConfluence = indicatorOptions }
            },
            settings);

        IndicatorCalibrationOrchestrationResult result = await IndicatorCalibrationOrchestrator.RunAsync(
            request, baselineStructuralOptions.IndicatorConfluence, _manifest, factory,
            maximumCoordinatePasses, cancellationToken).ConfigureAwait(false);

        CalibrationCompatibilityIdentity compatibility =
            IndicatorConfluenceCalibrationCompatibility.Instance.Describe(request.TimeframeTopology);
        IndicatorCalibrationArtifact artifact = IndicatorCalibrationArtifactFactory.BuildArtifact(
            result, _manifest, baselineStructuralOptions.IndicatorConfluence, compatibility,
            calibrationId: calibrationId,
            instrument: request.Instrument.Value,
            candleDataIdentityHash: ComputeCandleDataIdentityHash(request),
            baselineConfigurationHash: request.BaselineConfigurationHash,
            experimentLedgerId: experimentLedgerId,
            experimentLedgerChecksum: experimentLedgerChecksum,
            createdAt: DateTimeOffset.UtcNow);

        return (result, artifact);
    }

    /// <summary>
    /// Proxy identity for "which real market data this run consumed": instrument + timeline
    /// boundaries + timeframe topology hash. Not a content hash of the actual candle bytes - this
    /// adapter lets the engine stream historical data itself (blueprint's real cache/broker path)
    /// rather than pre-loading and hashing it, so the literal bytes are never in hand here. Good
    /// enough to detect "this run used a different window or topology than that artifact records";
    /// not a substitute for <c>BacktestEvaluationIdentity.CandleDataHash</c>'s per-evaluation,
    /// byte-content hash (Phase 3), which still guards the candidate cache.
    /// </summary>
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
