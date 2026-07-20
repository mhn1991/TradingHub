namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Produces an <see cref="ICandidateEvaluator"/> bound to a specific date window. This is the
/// seam the orchestrator uses to get a training-window evaluator, a validation-window evaluator,
/// and the external-holdout evaluator for one calibration run - Phase 6 supplies the real
/// backtest-running implementation; Phase 5's own tests use a synthetic one whose score can be
/// made to depend on the window, which is what makes the leakage-safety tests meaningful (blueprint §18.2).
/// </summary>
public interface ICandidateEvaluatorFactory
{
    ICandidateEvaluator CreateEvaluator(DateTimeOffset from, DateTimeOffset to);
}

/// <summary>Test-only factory whose scoring function receives the bound window explicitly, so a test can give training and validation windows deliberately different optima.</summary>
public sealed class SyntheticCandidateEvaluatorFactory(
    Func<DateTimeOffset, DateTimeOffset, CalibrationCandidate, BacktestEvaluationResult> score) : ICandidateEvaluatorFactory
{
    private readonly Func<DateTimeOffset, DateTimeOffset, CalibrationCandidate, BacktestEvaluationResult> _score =
        score ?? throw new ArgumentNullException(nameof(score));

    public ICandidateEvaluator CreateEvaluator(DateTimeOffset from, DateTimeOffset to) =>
        new SyntheticCandidateEvaluator(candidate => _score(from, to, candidate));
}
