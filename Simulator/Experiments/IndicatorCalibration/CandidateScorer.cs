using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record CandidateScoreResult
{
    public required bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }
    public decimal? Score { get; init; }

    public static CandidateScoreResult Ineligible(string reason) =>
        new() { PassedEligibilityGates = false, EligibilityFailureReason = reason, Score = null };

    public static CandidateScoreResult Eligible(decimal score) =>
        new() { PassedEligibilityGates = true, Score = score };
}

/// <summary>
/// Applies a <see cref="CalibrationScoringPolicy"/>'s eligibility gates first, then the
/// configured risk-adjusted objective (blueprint §10: "First apply eligibility gates ... Then
/// rank eligible candidates using the configured risk-adjusted objective"). Only
/// <see cref="CandidateScoreResult.PassedEligibilityGates"/> candidates carry a non-null score -
/// an ineligible candidate is never ranked, regardless of how its raw numbers might compare.
/// </summary>
public static class CandidateScorer
{
    /// <summary>
    /// Default objective: median expectancy penalized by drawdown and turnover (trade-count
    /// relative to the fold's minimum). Additional <see cref="CalibrationScoringPolicy.ObjectiveId"/>
    /// values can be added here later without changing any caller.
    /// </summary>
    public const string MedianExpectancyDrawdownPenalizedObjective = "median-expectancy-drawdown-penalized";

    public static CandidateScoreResult Score(BacktestEvaluationResult result, CalibrationScoringPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);

        if (!result.DataQualityValid)
            return CandidateScoreResult.Ineligible("Data quality gate failed.");
        if (result.TradeCount < policy.MinimumTradesPerFold)
        {
            return CandidateScoreResult.Ineligible(
                $"Trade count {result.TradeCount} is below the minimum {policy.MinimumTradesPerFold}.");
        }
        if (result.MaximumDrawdownR > policy.MaximumDrawdownR)
        {
            return CandidateScoreResult.Ineligible(
                $"Drawdown {result.MaximumDrawdownR:F2}R exceeds the maximum {policy.MaximumDrawdownR:F2}R.");
        }
        if (result.MedianExpectancyR < policy.MinimumMedianExpectancyR)
        {
            return CandidateScoreResult.Ineligible(
                $"Median expectancy {result.MedianExpectancyR:F2}R is below the minimum {policy.MinimumMedianExpectancyR:F2}R.");
        }
        if (result.ProfitFactor < policy.MinimumProfitFactor)
        {
            return CandidateScoreResult.Ineligible(
                $"Profit factor {result.ProfitFactor:F2} is below the minimum {policy.MinimumProfitFactor:F2}.");
        }

        // Only one objective is implemented today; ObjectiveId exists so a manifest can declare
        // which objective it expects (and a future objective can be added here without changing
        // any caller), not because this switch currently branches on it.
        decimal score = ComputeMedianExpectancyDrawdownPenalized(result, policy);
        return CandidateScoreResult.Eligible(score);
    }

    private static decimal ComputeMedianExpectancyDrawdownPenalized(BacktestEvaluationResult result, CalibrationScoringPolicy policy)
    {
        decimal turnoverPenalty = result.TradeCount > policy.MinimumTradesPerFold
            ? 0m
            : policy.TurnoverPenaltyWeight * (policy.MinimumTradesPerFold - result.TradeCount);
        return result.MedianExpectancyR
            - policy.DrawdownPenaltyWeight * result.MaximumDrawdownR
            - turnoverPenalty;
    }
}
