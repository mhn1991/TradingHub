namespace Simulator.Experiments.IndicatorCalibration.Persistence;

/// <summary>
/// Resumable storage for one calibration run's research ledger (blueprint §14). Postgres backing
/// is deliberately deferred (blueprint §3.2: "unless the file-backed schema has first been
/// proven") - only a file-backed implementation exists for the first release.
/// </summary>
public interface IIndicatorCalibrationLedgerRepository
{
    /// <summary>
    /// Persists the ledger. A write whose <see cref="IndicatorCalibrationExperimentLedger.Revision"/>
    /// is not strictly greater than the currently-stored revision is silently ignored (mirrors the
    /// same never-regress guard used by every other file-backed job/experiment store in this project).
    /// </summary>
    Task<IndicatorCalibrationExperimentLedger> SaveAsync(
        IndicatorCalibrationExperimentLedger ledger, CancellationToken cancellationToken = default);

    Task<IndicatorCalibrationExperimentLedger?> GetAsync(
        string ledgerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndicatorCalibrationExperimentLedger>> ListAsync(
        int take = 50, CancellationToken cancellationToken = default);
}
