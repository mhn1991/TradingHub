using TrendStatistics.Detection;
using TrendStatistics.Segmentation;

namespace TrendStatistics.Trading;

/// <summary>Whether a symbol has enough ungated trend edge to be worth gating at all.</summary>
public readonly record struct SymbolGateVerdict(
    string Symbol,
    int TrendCount,
    double BaselineProfitFactor,
    bool IsEligible,
    string Reason);

/// <summary>
/// Blueprint section 51's <c>DirectionGate</c>, generalised to a symbol gate.
/// <para>
/// Measured on 16.5 years across five symbols, the entry gate amplifies an existing edge but cannot
/// create one. Gold and silver have ungated baseline profit factors of 1.072 and 1.184 and rise to
/// 1.80 and 1.98 once gated. EUR/USD, GBP/USD and USD/JPY are all below 1.0 ungated, and gating
/// makes GBP/USD strictly worse (0.918 -> 0.666) - filtering to fewer trades concentrates a
/// negative expectancy rather than diluting it.
/// </para>
/// <para>
/// So eligibility is decided on the ungated baseline, measured per symbol, never assumed from asset
/// class. A symbol that cannot clear the baseline is not a candidate for the swing strategy at any
/// entry threshold.
/// </para>
/// </summary>
public sealed class SymbolGate(double minimumBaselineProfitFactor = 1.05, int minimumTrends = 100)
{
    private readonly double _minimumProfitFactor = minimumBaselineProfitFactor;
    private readonly int _minimumTrends = minimumTrends;

    /// <summary>
    /// Scores a symbol from its completed trend library using the naive confirm-to-end hold, which
    /// is the strategy with the entry gate switched off.
    /// </summary>
    public SymbolGateVerdict Evaluate(string symbol, IReadOnlyList<TrendRecord> trends)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(trends);

        TrendRecord? wrongSymbol = trends.FirstOrDefault(
            trend => !string.Equals(trend.Symbol, symbol, StringComparison.Ordinal));
        if (wrongSymbol is not null)
        {
            throw new ArgumentException(
                $"Trend symbol '{wrongSymbol.Symbol}' cannot be scored by gate '{symbol}'.",
                nameof(trends));
        }

        if (trends.Count < _minimumTrends)
        {
            return new SymbolGateVerdict(symbol, trends.Count, 0,
                false, $"Only {trends.Count} trends; {_minimumTrends} required before judging a baseline.");
        }

        decimal[] returns = [.. trends.Select(BaselineReturnPct)];
        decimal profit = returns.Where(value => value > 0m).Sum();
        decimal loss = -returns.Where(value => value <= 0m).Sum();
        double profitFactor = loss == 0m ? (profit > 0m ? double.PositiveInfinity : 0) : (double)(profit / loss);

        bool eligible = profitFactor >= _minimumProfitFactor;
        return new SymbolGateVerdict(symbol, trends.Count, profitFactor, eligible,
            eligible
                ? $"Baseline PF {profitFactor:F3} clears {_minimumProfitFactor:F2}; the entry gate has an edge to amplify."
                : $"Baseline PF {profitFactor:F3} is below {_minimumProfitFactor:F2}; gating would concentrate a losing expectancy.");
    }

    /// <summary>Confirmation close to trend-end close, signed so positive is profit either way.</summary>
    private static decimal BaselineReturnPct(TrendRecord trend)
    {
        if (trend.ConfirmationPrice == 0m)
            return 0m;
        decimal move = (trend.EndPrice - trend.ConfirmationPrice) / trend.ConfirmationPrice * 100m;
        return trend.Direction == TrendDirection.Bullish ? move : -move;
    }
}
