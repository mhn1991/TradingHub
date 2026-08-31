using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.Models;
using TradingClassifier.Signals;

namespace TradingClassifier.Evaluation;

/// <summary>One simulated round trip.</summary>
public readonly record struct ClassifierTrade(
    DateTimeOffset Opened,
    DateTimeOffset Closed,
    TradeLabel Side,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal NetProfit,
    double Confidence);

/// <summary>Section 22's metric set, all of it after costs.</summary>
public sealed record TradingReport
{
    public required int Trades { get; init; }
    public required int Winners { get; init; }
    public required int Losers { get; init; }
    public required double WinRate { get; init; }
    public required decimal AverageWinner { get; init; }
    public required decimal AverageLoser { get; init; }
    public required double ProfitFactor { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal GrossProfit { get; init; }
    public required decimal GrossLoss { get; init; }
    public required decimal MaximumDrawdown { get; init; }
    public required double SharpeRatio { get; init; }
    public required double SortinoRatio { get; init; }
    public required decimal Expectancy { get; init; }
    public required IReadOnlyList<ClassifierTrade> TradeList { get; init; }

    /// <summary>
    /// Greatest number of positions open at the same moment.
    /// <para>
    /// Anything above 1 means the reported trades are not independent observations and could not
    /// have been taken with one unit of capital. A hold of <c>PredictionHorizon</c> bars with a
    /// signal that can fire every bar permits up to <c>PredictionHorizon</c> simultaneous
    /// positions, which inflates both the trade count and the apparent statistical confidence.
    /// </para>
    /// </summary>
    public required int MaximumConcurrentPositions { get; init; }

    /// <summary>Mean number of open positions while any position is open.</summary>
    public required double AverageConcurrentPositions { get; init; }

    /// <summary>Section 36's bar: positive expectancy and a profit factor above 1, after costs.</summary>
    public bool MeetsSuccessBar => Trades > 0 && ProfitFactor > 1.0 && Expectancy > 0m;

    public string ToText() =>
        $"trades={Trades}  win-rate={WinRate:P2}  PF={ProfitFactor:F3}  net={NetProfit:F2}  " +
        $"expectancy={Expectancy:F4}  maxDD={MaximumDrawdown:F2}  Sharpe={SharpeRatio:F3}  " +
        $"Sortino={SortinoRatio:F3}  maxConcurrent={MaximumConcurrentPositions}";
}

/// <summary>Spread, commission and slippage - section 22 insists metrics are reported after these.</summary>
public sealed record TradingCostModel
{
    /// <summary>Half-spread paid on entry and again on exit, in price units.</summary>
    public decimal HalfSpread { get; init; }

    /// <summary>Flat cost per round trip, in profit units.</summary>
    public decimal CommissionPerTrade { get; init; }

    /// <summary>Adverse price movement applied on entry and exit, in price units.</summary>
    public decimal SlippagePerSide { get; init; }

    public static TradingCostModel Free => new();
}

/// <summary>
/// Section 26's "run backtest" step: turn probabilities into trades and score them.
/// <para>
/// The simulated trade holds for exactly <c>PredictionHorizon</c> candles, because that is the
/// question the model was trained to answer. Adding a stop or a target here would be measuring a
/// trade-management scheme the classifier never saw, and section 24's ablation would then be
/// attributing that scheme's behaviour to the features.
/// </para>
/// <para>
/// <c>allowOverlappingPositions</c> defaults to <b>false</b>: one position at a time, which is
/// what a single account could actually have traded. It defaulted to <c>true</c> until the
/// P0a parity pass, and because <c>train</c>, the ladder and the ablation all omitted the
/// argument, those commands silently measured a different execution policy from
/// <c>walk-forward</c>. Every classifier figure produced before that fix counted up to
/// <c>PredictionHorizon</c> simultaneous holds as independent trades. Callers that genuinely
/// model a portfolio permitting concurrent holds must now opt in explicitly.
/// </para>
/// </summary>
public static class ClassifierBacktester
{
    public static TradingReport Run(
        IReadOnlyList<LabeledFeatureRow> rows,
        IReadOnlyList<Prediction> predictions,
        ClassifierOptions options,
        TradingCostModel? costs = null,
        bool allowOverlappingPositions = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(options);
        if (rows.Count != predictions.Count)
            throw new ArgumentException("Rows and predictions must be the same length.", nameof(predictions));

        costs ??= TradingCostModel.Free;
        ConfidenceSignalGenerator generator = new(options);
        List<ClassifierTrade> trades = [];

        // Rows are one per candle, so the row `horizon` positions later is the exit candle. Using
        // the row list rather than re-reading candles keeps entry and exit on the same clock the
        // label was built on.
        int freeFrom = 0;
        for (int index = 0; index + options.PredictionHorizon < rows.Count; index++)
        {
            // One unit of capital: a new signal is ignored while the previous position is open.
            // This is what a single account could actually have traded.
            if (!allowOverlappingPositions && index < freeFrom)
                continue;

            TradingSignal signal = generator.Generate(predictions[index]);
            if (!signal.IsActionable)
                continue;

            freeFrom = index + options.PredictionHorizon;

            LabeledFeatureRow entry = rows[index];
            LabeledFeatureRow exit = rows[index + options.PredictionHorizon];

            bool isBuy = signal.Action == TradeLabel.Buy;
            decimal entryPrice = entry.Features.Close + (isBuy ? costs.HalfSpread + costs.SlippagePerSide : -costs.HalfSpread - costs.SlippagePerSide);
            decimal exitPrice = exit.Features.Close + (isBuy ? -costs.HalfSpread - costs.SlippagePerSide : costs.HalfSpread + costs.SlippagePerSide);

            decimal profit = (isBuy ? exitPrice - entryPrice : entryPrice - exitPrice) - costs.CommissionPerTrade;

            trades.Add(new ClassifierTrade(
                entry.Features.Timestamp,
                exit.Features.Timestamp,
                signal.Action,
                entryPrice,
                exitPrice,
                profit,
                isBuy ? predictions[index].BuyProbability : predictions[index].SellProbability));
        }

        return Summarise(trades);
    }

    /// <summary>
    /// Sweeps the open/close events to find how many positions were live simultaneously.
    /// </summary>
    private static (int Maximum, double Average) Concurrency(IReadOnlyList<ClassifierTrade> trades)
    {
        if (trades.Count == 0)
            return (0, 0);

        List<(DateTimeOffset At, int Delta)> events = [];
        foreach (ClassifierTrade trade in trades)
        {
            events.Add((trade.Opened, 1));
            events.Add((trade.Closed, -1));
        }
        // Closes are processed before opens at the same instant, so a position that ends exactly
        // when another begins is not counted as an overlap.
        events.Sort((left, right) => left.At != right.At
            ? left.At.CompareTo(right.At)
            : left.Delta.CompareTo(right.Delta));

        int open = 0, maximum = 0;
        long weighted = 0, samples = 0;
        foreach ((_, int delta) in events)
        {
            open += delta;
            maximum = Math.Max(maximum, open);
            if (open > 0) { weighted += open; samples++; }
        }

        return (maximum, samples == 0 ? 0 : (double)weighted / samples);
    }

    public static TradingReport Summarise(IReadOnlyList<ClassifierTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        if (trades.Count == 0)
        {
            return new TradingReport
            {
                Trades = 0, Winners = 0, Losers = 0, WinRate = 0,
                AverageWinner = 0m, AverageLoser = 0m, ProfitFactor = 0,
                NetProfit = 0m, GrossProfit = 0m, GrossLoss = 0m, MaximumDrawdown = 0m,
                SharpeRatio = 0, SortinoRatio = 0, Expectancy = 0m, TradeList = trades,
                MaximumConcurrentPositions = 0, AverageConcurrentPositions = 0
            };
        }

        decimal grossProfit = trades.Where(trade => trade.NetProfit > 0m).Sum(trade => trade.NetProfit);
        decimal grossLoss = -trades.Where(trade => trade.NetProfit <= 0m).Sum(trade => trade.NetProfit);
        int winners = trades.Count(trade => trade.NetProfit > 0m);
        int losers = trades.Count - winners;

        decimal equity = 0m;
        decimal peak = 0m;
        decimal maximumDrawdown = 0m;
        foreach (ClassifierTrade trade in trades)
        {
            equity += trade.NetProfit;
            peak = Math.Max(peak, equity);
            maximumDrawdown = Math.Max(maximumDrawdown, peak - equity);
        }

        double[] returns = trades.Select(trade => (double)trade.NetProfit).ToArray();
        double mean = returns.Average();
        double variance = returns.Length > 1
            ? returns.Sum(value => (value - mean) * (value - mean)) / (returns.Length - 1)
            : 0;
        double standardDeviation = Math.Sqrt(variance);

        // Sortino only penalises downside dispersion; with no losing trade there is none, and the
        // ratio is reported as 0 rather than infinity so it stays sortable and comparable.
        double[] downside = returns.Where(value => value < 0).ToArray();
        double downsideDeviation = downside.Length > 0
            ? Math.Sqrt(downside.Sum(value => value * value) / downside.Length)
            : 0;

        return new TradingReport
        {
            Trades = trades.Count,
            Winners = winners,
            Losers = losers,
            WinRate = (double)winners / trades.Count,
            AverageWinner = winners == 0 ? 0m : grossProfit / winners,
            AverageLoser = losers == 0 ? 0m : -grossLoss / losers,
            // No losing trade at all makes the ratio undefined rather than infinitely good; on a
            // small sample that is usually two lucky trades, not an edge.
            ProfitFactor = grossLoss == 0m ? (grossProfit > 0m ? double.PositiveInfinity : 0) : (double)(grossProfit / grossLoss),
            NetProfit = trades.Sum(trade => trade.NetProfit),
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            MaximumDrawdown = maximumDrawdown,
            SharpeRatio = standardDeviation == 0 ? 0 : mean / standardDeviation,
            SortinoRatio = downsideDeviation == 0 ? 0 : mean / downsideDeviation,
            Expectancy = trades.Sum(trade => trade.NetProfit) / trades.Count,
            TradeList = trades,
            MaximumConcurrentPositions = Concurrency(trades).Maximum,
            AverageConcurrentPositions = Concurrency(trades).Average
        };
    }
}
