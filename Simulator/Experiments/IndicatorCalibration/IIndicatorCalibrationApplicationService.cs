using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>Application-service surface for indicator calibration (blueprint §17.1).</summary>
public interface IIndicatorCalibrationApplicationService
{
    Task<CalibrationBudgetPreview> PreviewAsync(
        IndicatorCalibrationRequest request, CancellationToken cancellationToken = default);

    Task<IndicatorCalibrationRunSummary> StartAsync(
        IndicatorCalibrationRequest request, CancellationToken cancellationToken = default);

    Task<IndicatorCalibrationRunDetails?> GetAsync(
        string calibrationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndicatorCalibrationRunSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the in-flight run and marks it Paused (resumable) rather than Cancelled (final).
    /// Throws if the run is not active in this process or is not in a pausable state (blueprint §14).
    /// </summary>
    Task PauseAsync(string calibrationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused run (or one this process lost track of after a restart, provided its
    /// ledger was not already saved Completed) by re-invoking the same deterministic orchestrator
    /// with the same request through a persistent, calibration-id-keyed candidate cache - proven
    /// to reproduce the identical decision output while skipping already-completed real backtests
    /// (blueprint §14). Throws if the run cannot be found or is not resumable.
    /// </summary>
    Task ResumeAsync(string calibrationId, CancellationToken cancellationToken = default);

    Task CancelAsync(string calibrationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every indicator-calibration artifact currently awaiting review (PendingReview), across every
    /// registered strategy - queried directly from the artifact repository, so this works
    /// regardless of whether the run that produced an artifact is still tracked in this process
    /// (e.g. a prior night's unattended calibration). This is what makes a real approval queue
    /// possible instead of only ever seeing runs this exact process happens to remember.
    /// </summary>
    Task<IReadOnlyList<IndicatorCalibrationPendingApproval>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken = default);

    Task<CalibrationPromotionEvent> ApproveAsync(
        IndicatorCalibrationApprovalRequest request, CancellationToken cancellationToken = default);

    Task RejectAsync(
        IndicatorCalibrationRejectionRequest request, CancellationToken cancellationToken = default);
}
