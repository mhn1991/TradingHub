using Agent.Strategies.StructuralConfluence.Playbooks;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Agent.Strategies.StructuralConfluence;

internal static class StructuralEntryProfileRouting
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RoutedPlaybooks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["trend-pullback"] = new HashSet<string>(StringComparer.Ordinal)
            {
                SupplyDemandPullbackPlaybook.StableId,
                IndicatorConfluencePlaybook.StableId
            },
            ["range-boundary"] = new HashSet<string>(StringComparer.Ordinal)
            {
                LiquiditySweepReversalPlaybook.StableId
            },
            ["breakout-displacement"] = new HashSet<string>(StringComparer.Ordinal)
            {
                LiquidityBreakRetestPlaybook.StableId
            },
            ["compression-wait"] = new HashSet<string>(StringComparer.Ordinal),
            ["unsafe-reject"] = new HashSet<string>(StringComparer.Ordinal)
        };

    public static bool AllowsEntry(
        PlaybookEvaluation evaluation,
        RegimeGateResult gate)
    {
        if (!gate.RoutingEnabled)
            return true;
        if (!gate.AllowNewEntries)
            return false;

        return !RoutedPlaybooks.TryGetValue(gate.Policy.EntryProfileId, out IReadOnlySet<string>? allowed) ||
            allowed.Contains(evaluation.PlaybookId) ||
            IsMatureBreakRetestTransition(evaluation, gate);
    }

    /// <summary>
    /// A completed break/retest is often classified as an ordinary trend by the time its retest
    /// trigger closes. Preserve that already-proven breakout thesis through this one profile
    /// transition, while requiring its causal pool/break evidence and direction agreement.
    /// </summary>
    private static bool IsMatureBreakRetestTransition(
        PlaybookEvaluation evaluation,
        RegimeGateResult gate) =>
        string.Equals(gate.Policy.EntryProfileId, "trend-pullback", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(evaluation.PlaybookId, LiquidityBreakRetestPlaybook.StableId, StringComparison.Ordinal) &&
        evaluation.Lifecycle == Agent.Models.StructuralSetupLifecycle.CandidateProduced &&
        evaluation.IsReady &&
        evaluation.PrimaryPoolId is not null &&
        evaluation.SupportingEvidence.Contains("AcceptedLiquidityBreak", StringComparer.Ordinal) &&
        DirectionMatchesTrend(evaluation.Direction, gate.Policy.Regime);

    private static bool DirectionMatchesTrend(
        PriceActionDirection direction,
        MarketRegime regime) =>
        (direction, regime) switch
        {
            (PriceActionDirection.Bullish, MarketRegime.TrendingUp) => true,
            (PriceActionDirection.Bearish, MarketRegime.TrendingDown) => true,
            _ => false
        };
}
