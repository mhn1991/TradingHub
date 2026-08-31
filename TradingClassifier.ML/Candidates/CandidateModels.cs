using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Candidates;

/// <summary>
/// V2 §5.1: a fully specified candidate, as it stood at the DECISION bar.
/// <para>
/// Every field here must be knowable before the fill. <c>EntryPrice</c> is deliberately absent:
/// the fill lands one execution bar after the decision and 86.8% of fills are adverse, so including
/// it would leak the first bar of the outcome into the features.
/// </para>
/// </summary>
public readonly record struct TradingCandidate(
    int Number,
    TradeLabel Side,
    DateTimeOffset DecisionAt,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    decimal DecisionPrice,
    decimal StopPrice,
    decimal TargetPrice,
    string Session);

/// <summary>
/// V2 §5.2: what the candidate actually returned, per unit of risk, after costs.
/// <para>
/// <c>RealizedRAfterCosts</c> is the simulator's own <c>rMultiple</c>, which already blends partial
/// exits — necessary here because up to 61.6% of trades exit in stages, so a single entry/exit pair
/// cannot represent the outcome. <c>ExitReason</c> is retained as a diagnostic, never as a feature.
/// </para>
/// </summary>
public readonly record struct CandidateOutcome(
    decimal RealizedRAfterCosts,
    decimal RealizedNet,
    string ExitReason,
    // Excursions in units of the candidate's ACTUAL initial risk distance, recomputed from prices
    // rather than taken from the trade record's own R fields — §3.19 found those use a different
    // denominator once partial exits are involved, which is why the record's MFE appeared to
    // contradict its own TakeProfit rate.
    decimal MaximumFavourableR,
    decimal MaximumAdverseR,
    // Adverse excursion in ATR units rather than R. Stop-INDEPENDENT, and therefore the only form
    // usable for stop placement: R is defined by the stop distance, so predicting MAE in R to
    // choose a stop is circular — the target would move with the answer.
    decimal MaximumAdverseAtr,
    // Which barrier was reached first, where both were touched. Needed to score any target policy
    // honestly; null when the ordering could not be established.
    bool? FavourableCameFirst);

/// <summary>One training row: causal features at the decision bar, and the realised R they predict.</summary>
public sealed record CandidateRow
{
    public required TradingCandidate Candidate { get; init; }
    public required CandidateOutcome Outcome { get; init; }
    public required float[] Features { get; init; }

    /// <summary>The regression target: after-cost realised R.</summary>
    public float Target => (float)Outcome.RealizedRAfterCosts;
}

public sealed record CandidateDataset
{
    public required IReadOnlyList<CandidateRow> Rows { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }
    public required int Unjoined { get; init; }

    /// <summary>
    /// Columns whose value never varies carry no information. §6.1's "candidate geometry" group is
    /// expected to land here for a fixed-ATR bracket; reporting them is more honest than dropping
    /// them silently.
    /// </summary>
    public IReadOnlyList<string> ConstantFeatures
    {
        get
        {
            List<string> constant = [];
            for (int column = 0; column < FeatureNames.Count; column++)
            {
                float first = Rows[0].Features[column];
                if (Rows.All(row => Math.Abs(row.Features[column] - first) < 1e-9f))
                    constant.Add(FeatureNames[column]);
            }
            return constant;
        }
    }
}
