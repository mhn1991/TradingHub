namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// The seam between the generic search algorithm and one concrete evaluation window/fold. An
/// implementation is bound to a specific date range (a fold's training slice, a fold's
/// validation slice, or the external holdout) - the algorithm itself never sees dates, only this
/// interface, which is what keeps the leakage-safe orchestration (blueprint §8, Phase 5) cleanly
/// separated from the search algorithm (Phase 4). Phase 4 tests use a synthetic in-memory
/// implementation; Phase 6 adds the real backtest-running adapter.
/// </summary>
public interface ICandidateEvaluator
{
    Task<BacktestEvaluationResult> EvaluateAsync(
        CalibrationCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic in-memory evaluator driven by a pure function - used only for Phase 4 algorithm tests, never for real calibration.</summary>
public sealed class SyntheticCandidateEvaluator(Func<CalibrationCandidate, BacktestEvaluationResult> score) : ICandidateEvaluator
{
    private readonly Func<CalibrationCandidate, BacktestEvaluationResult> _score =
        score ?? throw new ArgumentNullException(nameof(score));

    public Task<BacktestEvaluationResult> EvaluateAsync(
        CalibrationCandidate candidate, CancellationToken cancellationToken = default) =>
        Task.FromResult(_score(candidate));
}
