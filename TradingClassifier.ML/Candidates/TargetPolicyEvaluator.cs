namespace TradingClassifier.ML.Candidates;

public sealed record TargetPolicyResult
{
    public required string Policy { get; init; }
    public required int Trades { get; init; }
    public required int Wins { get; init; }
    public required double TotalR { get; init; }
    public double WinRate => Trades == 0 ? 0 : (double)Wins / Trades;
    public double ExpectancyR => Trades == 0 ? 0 : TotalR / Trades;
}

/// <summary>
/// Scores target-placement policies on the same candidates, so a prediction is judged by whether it
/// would have changed the money — not by its correlation alone.
/// <para>
/// This exists because §3.19 measured a concrete defect: the fixed 3.0 ATR target was reached by only
/// 9.6% of trades, the best fixed multiple in the valid range was ~0.75R, and <b>every</b> fixed
/// multiple still lost. If MFE is predictable per-candidate, a per-trade target should beat every
/// fixed one. If it does not, MFE prediction has no operational value here however good its rho is.
/// </para>
/// <para>
/// Scoring rule, matching §3.19: the stop sits at 1R. A trade wins its target multiple if the
/// favourable excursion reached it AND the ordering says favourable came first (or the stop was
/// never reached); otherwise it loses 1R. Where both barriers were touched and the ordering is
/// unknown, the PESSIMISTIC outcome is taken — never the flattering one.
/// </para>
/// </summary>
public static class TargetPolicyEvaluator
{
    public static TargetPolicyResult Fixed(IReadOnlyList<FoldPrediction> predictions, double target)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        return Score($"fixed {target:F2}R", predictions, _ => target);
    }

    /// <summary>
    /// Target placed at the model's predicted MFE, scaled and clamped. <paramref name="scale"/> below
    /// 1 asks for less than predicted, which is the sane direction: a target you actually reach beats
    /// a bigger one you do not.
    /// </summary>
    public static TargetPolicyResult Predicted(
        IReadOnlyList<FoldPrediction> predictions,
        double scale = 1.0,
        double minimum = 0.25,
        double maximum = 3.0)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        return Score(
            $"predicted MFE x{scale:F2}",
            predictions,
            item => Math.Clamp(item.Predicted * scale, minimum, maximum));
    }

    private static TargetPolicyResult Score(
        string policy,
        IReadOnlyList<FoldPrediction> predictions,
        Func<FoldPrediction, double> targetFor)
    {
        double total = 0;
        int wins = 0;

        foreach (FoldPrediction item in predictions)
        {
            double target = targetFor(item);
            double favourable = (double)item.Outcome.MaximumFavourableR;
            double adverse = (double)item.Outcome.MaximumAdverseR;

            bool reachedTarget = favourable >= target;
            bool reachedStop = adverse >= 1.0;

            double r;
            if (reachedTarget && reachedStop)
            {
                // Both touched: honour the recorded ordering, and be pessimistic when it is unknown.
                r = item.Outcome.FavourableCameFirst == true ? target : -1.0;
            }
            else if (reachedTarget)
            {
                r = target;
            }
            else if (reachedStop)
            {
                r = -1.0;
            }
            else
            {
                // Neither barrier reached: the trade is closed where it actually ended.
                r = (double)item.Outcome.RealizedRAfterCosts;
            }

            total += r;
            if (r > 0) wins++;
        }

        return new TargetPolicyResult
        {
            Policy = policy,
            Trades = predictions.Count,
            Wins = wins,
            TotalR = total
        };
    }
}
