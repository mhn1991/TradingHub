using Simulator.Calibration;
using Simulator.Services;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Erases one calibrated strategy's <c>TOptions</c> generic parameter behind a per-strategy
/// adapter, so the non-generic <see cref="IIndicatorCalibrationApplicationService"/> (blueprint
/// §17.1's own interface has no <c>TOptions</c> parameter) can dispatch by
/// <see cref="IndicatorCalibrationRequest.StrategyId"/> alone. Registering a new strategy (Phase 8)
/// means adding one adapter implementation here, not touching the application service.
/// </summary>
public interface IIndicatorCalibrationStrategyAdapter
{
    string StrategyId { get; }
    string ManifestVersion { get; }

    CalibrationBudgetPreview Preview(IndicatorCalibrationRequest request, int maximumCoordinatePasses);

    Task<(IndicatorCalibrationOrchestrationResult Result, IndicatorCalibrationArtifact Artifact)> RunAsync(
        IndicatorCalibrationRequest request,
        IBacktestApplicationService backtests,
        string calibrationId,
        string experimentLedgerId,
        string experimentLedgerChecksum,
        int maximumCoordinatePasses,
        CancellationToken cancellationToken);
}
