using TrendStatistics.Detection;

namespace TrendStatistics.Evaluation;

/// <summary>One completed swing trade, for the section 38-40 diagnostics.</summary>
public readonly record struct SwingTrade(
    TrendDirection Direction,
    DateTimeOffset EntryTime,
    decimal EntryPrice,
    DateTimeOffset ExitTime,
    decimal ExitPrice,
    decimal TrendStructuralStartPrice,
    decimal TrendFavorableExtremePrice,
    int BarsHeld,
    decimal? PostEntryFavorableExtremePrice = null)
{
    public decimal ReturnPct => Direction == TrendDirection.Bullish
        ? (ExitPrice - EntryPrice) / EntryPrice * 100m
        : (EntryPrice - ExitPrice) / EntryPrice * 100m;

    private decimal EffectivePostEntryExtreme =>
        PostEntryFavorableExtremePrice ?? TrendFavorableExtremePrice;

    /// <summary>
    /// Section 38: signed return captured as a fraction of the favorable move that was actually
    /// available after entry. A losing trade therefore has a negative capture ratio.
    /// </summary>
    public decimal TrendCaptureRatio
    {
        get
        {
            decimal available = DirectionalMove(EntryPrice, EffectivePostEntryExtreme);
            decimal captured = DirectionalMove(EntryPrice, ExitPrice);
            return available <= 0m ? 0m : captured / available;
        }
    }

    /// <summary>
    /// Section 39: fraction of the completed structural move already consumed before entry.
    /// Lower is better; zero means entry at the structural start and one means entry at the final
    /// favorable extreme.
    /// </summary>
    public decimal EntryDelay
    {
        get
        {
            decimal wholeMove = DirectionalMove(TrendStructuralStartPrice, TrendFavorableExtremePrice);
            if (wholeMove <= 0m) return 0m;
            return Math.Clamp(
                DirectionalMove(TrendStructuralStartPrice, EntryPrice) / wholeMove,
                0m,
                1m);
        }
    }

    /// <summary>
    /// Section 40: percentage points surrendered from post-entry maximum favorable excursion to
    /// the realized return. This intentionally is not normalized by MFE.
    /// </summary>
    public decimal ExitGiveback
    {
        get
        {
            decimal mfePct = DirectionalMove(EntryPrice, EffectivePostEntryExtreme) / EntryPrice * 100m;
            return Math.Max(0m, mfePct - ReturnPct);
        }
    }

    private decimal DirectionalMove(decimal from, decimal to) => Direction == TrendDirection.Bullish
        ? to - from
        : from - to;
}

/// <summary>Blueprint sections 38 to 40 and 64: how a swing result must be judged.</summary>
public sealed record TrendMetricsReport
{
    public required int Trades { get; init; }
    public required double WinRate { get; init; }
    public required double ProfitFactor { get; init; }
    public required decimal NetPct { get; init; }
    public required decimal MeanPct { get; init; }
    public required decimal MaximumDrawdownPct { get; init; }
    public required decimal MedianCaptureRatio { get; init; }
    public required decimal MedianEntryDelay { get; init; }
    public required decimal MedianExitGiveback { get; init; }

    /// <summary>
    /// Section 69's minimum. Deliberately more than "net &gt; 0" - that section opens by rejecting
    /// exactly that test. The remaining section 69 criteria (walk-forward window majority, regime
    /// concentration, bootstrap stability) are properties of a run, not of one report.
    /// </summary>
    public bool MeetsSuccessBar => Trades > 0 && ProfitFactor > 1.0 && NetPct > 0m;

    public string ToText() =>
        $"trades={Trades}  win={WinRate:P1}  PF={ProfitFactor:F3}  net={NetPct:F2}%  " +
        $"mean={MeanPct:F3}%  maxDD={MaximumDrawdownPct:F2}%  " +
        $"capture={MedianCaptureRatio:P1}  entry-delay={MedianEntryDelay:P1}  " +
        $"giveback={MedianExitGiveback:F2}pp";

    public static TrendMetricsReport From(IReadOnlyList<SwingTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (trades.Count == 0)
        {
            return new TrendMetricsReport
            {
                Trades = 0, WinRate = 0, ProfitFactor = 0, NetPct = 0m, MeanPct = 0m,
                MaximumDrawdownPct = 0m, MedianCaptureRatio = 0m,
                MedianEntryDelay = 0m, MedianExitGiveback = 0m
            };
        }

        SwingTrade[] ordered = [.. trades
            .OrderBy(trade => trade.ExitTime)
            .ThenBy(trade => trade.EntryTime)];
        decimal[] returns = [.. ordered.Select(trade => trade.ReturnPct)];
        decimal profit = returns.Where(value => value > 0m).Sum();
        decimal loss = -returns.Where(value => value <= 0m).Sum();

        decimal equity = 100m, peak = 100m, drawdown = 0m;
        foreach (decimal value in returns)
        {
            equity *= 1m + (value / 100m);
            peak = Math.Max(peak, equity);
            if (peak > 0m)
                drawdown = Math.Max(drawdown, (peak - equity) / peak * 100m);
        }

        return new TrendMetricsReport
        {
            Trades = trades.Count,
            WinRate = (double)returns.Count(value => value > 0m) / returns.Length,
            ProfitFactor = loss == 0m ? (profit > 0m ? double.PositiveInfinity : 0) : (double)(profit / loss),
            NetPct = equity - 100m,
            MeanPct = returns.Average(),
            MaximumDrawdownPct = drawdown,
            MedianCaptureRatio = Median([.. ordered.Select(trade => trade.TrendCaptureRatio)]),
            MedianEntryDelay = Median([.. ordered.Select(trade => trade.EntryDelay)]),
            MedianExitGiveback = Median([.. ordered.Select(trade => trade.ExitGiveback)])
        };
    }

    private static decimal Median(decimal[] values)
    {
        Array.Sort(values);
        return values.Length == 0 ? 0m
            : values.Length % 2 == 1 ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2m;
    }
}
