using TradingClassifier.Features;
using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Runtime;
using TrendStatistics.Segmentation;
using TrendStatistics.Statistics;
using TrendStatistics.Trading;

namespace TradingClassifierRunner;

/// <summary>
/// Blueprint sections 29 and 30: diagnostic entry-percentile sweeps. Every row uses the same
/// reliability-qualified start date and causal profiles. The grid is descriptive only; production
/// parameter selection belongs to the separate walk-forward command.
/// </summary>
public static class SwingEntryExperiment
{
    private sealed record Trade(
        TrendDirection Direction,
        decimal EntryPrice,
        decimal ExitPrice,
        int BarsHeld,
        decimal RoundTripCostPct)
    {
        public decimal GrossReturnPct => Direction == TrendDirection.Bullish
            ? (ExitPrice - EntryPrice) / EntryPrice * 100m
            : (EntryPrice - ExitPrice) / EntryPrice * 100m;

        public decimal ReturnPct => GrossReturnPct - RoundTripCostPct;
    }

    public static int Run(
        IReadOnlyList<ClassifierCandle> candles,
        string symbol,
        TrendDetectorConfig config,
        int minimumSamples,
        decimal roundTripCostPct = 0.03m)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0)
            throw new ArgumentException("No candles supplied.", nameof(candles));
        if (roundTripCostPct < 0m)
            throw new ArgumentOutOfRangeException(nameof(roundTripCostPct));

        TrendStatistics.Data.Candle[] converted = [.. candles.Select(candle => new TrendStatistics.Data.Candle
        {
            Symbol = symbol,
            OpenTime = candle.Timestamp - config.Timeframe,
            Open = candle.Open,
            High = candle.High,
            Low = candle.Low,
            Close = candle.Close
        })];

        IReadOnlyList<TrendRecord> library = new TrendSegmenter(config).Segment(converted);
        Dictionary<int, SymbolTrendProfile> profileCache = [];
        DateTimeOffset? evaluationStart = FindCommonEvaluationStart(
            symbol, library, minimumSamples, profileCache);

        Console.WriteLine($"Swing entry diagnostics (sections 29/30) on {converted.Length:N0} candles, " +
            $"{converted[0].OpenTime:yyyy-MM-dd} -> {converted[^1].OpenTime:yyyy-MM-dd}");
        Console.WriteLine($"Causal profiles; common reliability-qualified start; " +
            $"round-trip cost {roundTripCostPct:F3}%; minimum {minimumSamples} samples per side.");
        Console.WriteLine("Grid rows are diagnostics, not parameter selection; use trend-walk-forward for selection.\n");

        if (evaluationStart is null)
        {
            Console.WriteLine("No date has reliable bull and bear profiles; no comparable experiment can run.");
            return 0;
        }

        TrendRecord[] gateHistory = [.. library.Where(trend => trend.EndTime <= evaluationStart.Value)];
        SymbolGateVerdict verdict = new SymbolGate().Evaluate(symbol, gateHistory);
        Console.WriteLine($"evaluation starts {evaluationStart:yyyy-MM-dd}; symbol gate is frozen as of that date: " +
            $"{(verdict.IsEligible ? "ELIGIBLE" : "REJECTED")} - {verdict.Reason}\n");

        Console.WriteLine($"{"mode",-16}{"entry",7}{"trades",8}{"win%",8}{"PF",9}" +
            $"{"net%",10}{"mean%",9}{"maxDD%",9}");

        Report("BASELINE conf", 0m, Simulate(
            converted, config, null, minimumSamples, false, evaluationStart.Value,
            roundTripCostPct, profileCache));

        foreach (SwingEntryMode mode in (SwingEntryMode[])
            [SwingEntryMode.PriceOnly, SwingEntryMode.TimeOnly, SwingEntryMode.PriceAndTime])
        {
            foreach (decimal entry in (decimal[])[0.05m, 0.10m, 0.15m, 0.20m, 0.25m])
            {
                SwingEntryOptions options = new()
                {
                    EntryPercentile = entry,
                    Mode = mode,
                    MinimumTrendSamples = minimumSamples,
                    MaximumEntryPercentile = 0.95m
                };
                Report(mode.ToString(), entry, Simulate(
                    converted, config, options, minimumSamples, false, evaluationStart.Value,
                    roundTripCostPct, profileCache));
            }
        }

        Console.WriteLine();
        foreach (decimal entry in (decimal[])[0.05m, 0.10m, 0.15m])
        {
            SwingEntryOptions options = new()
            {
                EntryPercentile = entry,
                Mode = SwingEntryMode.TimeOnly,
                MinimumTrendSamples = minimumSamples,
                MaximumEntryPercentile = 0.95m
            };
            Report("Time+MANAGED", entry, Simulate(
                converted, config, options, minimumSamples, true, evaluationStart.Value,
                roundTripCostPct, profileCache));
        }

        return 0;
    }

    private static DateTimeOffset? FindCommonEvaluationStart(
        string symbol,
        IReadOnlyList<TrendRecord> library,
        int minimumSamples,
        IDictionary<int, SymbolTrendProfile> profileCache)
    {
        List<TrendRecord> history = [];
        foreach (TrendRecord trend in library.OrderBy(trend => trend.EndTime))
        {
            history.Add(trend);
            int bull = history.Count(item => item.Direction == TrendDirection.Bullish);
            int bear = history.Count - bull;
            if (bull < minimumSamples || bear < minimumSamples)
                continue;

            SymbolTrendProfile profile = SymbolTrendProfile.Build(symbol, history);
            profileCache[history.Count] = profile;
            if (profile.Bull.IsReliable(minimumSamples) && profile.Bear.IsReliable(minimumSamples))
                return trend.EndTime;
        }
        return null;
    }

    private static List<Trade> Simulate(
        IReadOnlyList<TrendStatistics.Data.Candle> candles,
        TrendDetectorConfig config,
        SwingEntryOptions? options,
        int minimumSamples,
        bool managedExit,
        DateTimeOffset evaluationStart,
        decimal roundTripCostPct,
        IDictionary<int, SymbolTrendProfile> profileCache)
    {
        TrendDetector detector = new(config);
        SwingSignalGenerator? generator = options is null ? null : new SwingSignalGenerator(options);
        ExhaustionEstimator exhaustionEstimator = new();
        List<TrendRecord> completed = [];
        List<Trade> trades = [];
        HashSet<DateTimeOffset> tradedTrends = [];

        decimal? entryPrice = null;
        TrendDirection? entryDirection = null;
        int entryBar = 0;
        int bar = 0;
        SwingPositionManager? manager = null;
        TrendStatistics.Data.Candle? lastCandle = null;

        SymbolTrendProfile CurrentProfile(string currentSymbol)
        {
            if (!profileCache.TryGetValue(completed.Count, out SymbolTrendProfile? profile))
            {
                profile = SymbolTrendProfile.Build(currentSymbol, completed);
                profileCache[completed.Count] = profile;
            }
            return profile;
        }

        foreach (TrendStatistics.Data.Candle candle in candles)
        {
            bar++;
            lastCandle = candle;
            TrendDetectorUpdate update = detector.Apply(candle);
            DateTimeOffset closeTime = candle.OpenTime + config.Timeframe;

            if (update.CompletedTrend is TrendRecord finished)
            {
                if (entryPrice is decimal open && entryDirection is TrendDirection direction)
                {
                    trades.Add(new Trade(
                        direction, open, candle.Close, bar - entryBar, roundTripCostPct));
                    entryPrice = null;
                    entryDirection = null;
                    manager = null;
                }

                completed.Add(finished);
                continue;
            }

            if (entryPrice is decimal held
                && entryDirection is TrendDirection heldDirection
                && managedExit
                && manager is not null)
            {
                DirectionTrendProfile side = CurrentProfile(candle.Symbol).For(heldDirection);
                if (side.IsReliable(minimumSamples))
                {
                    TrendProgress progress = new TrendProgressEstimator(minimumSamples)
                        .Estimate(update.State, side);
                    JointDistribution joint = JointDistribution.FromTrends(
                        [.. side.MoveDurationPairs.Select(pair => pair.MovePct)],
                        [.. side.MoveDurationPairs.Select(pair => pair.DurationBars)]);
                    TrendExhaustionState exhaustion = exhaustionEstimator.Estimate(
                        progress, joint, update.State.Phase == TrendPhase.Exhaustion);

                    SwingPositionUpdate position = manager.Update(
                        heldDirection,
                        held,
                        candle.Close,
                        candle.High,
                        candle.Low,
                        update.State,
                        exhaustion);
                    if (position.ShouldExit)
                    {
                        trades.Add(new Trade(
                            heldDirection, held, candle.Close, bar - entryBar, roundTripCostPct));
                        entryPrice = null;
                        entryDirection = null;
                        manager = null;
                        continue;
                    }
                }
            }

            if (entryPrice is not null || closeTime < evaluationStart)
                continue;

            TrendState state = update.State;
            if (state.Phase is not (TrendPhase.Confirmed or TrendPhase.Mature)
                || state.Direction is not TrendDirection live
                || state.ConfirmationTime is not DateTimeOffset confirmation
                || tradedTrends.Contains(confirmation))
                continue;

            if (generator is not null)
            {
                DirectionTrendProfile side = CurrentProfile(candle.Symbol).For(live);
                SwingSignal signal = generator.Evaluate(state, side);
                if (!signal.IsActionable)
                    continue;
            }

            entryPrice = candle.Close;
            entryDirection = live;
            entryBar = bar;
            tradedTrends.Add(confirmation);

            if (managedExit)
            {
                // Confirmation-aligned historical MAE cannot serve as the stop distribution for a
                // later percentile entry. Use the explicit backstop until entry-aligned MAE exists.
                manager = new SwingPositionManager(new SwingPositionOptions
                {
                    MaximumAdversePct = 3.0m
                });
            }
        }

        if (lastCandle is not null
            && entryPrice is decimal finalEntry
            && entryDirection is TrendDirection finalDirection)
        {
            trades.Add(new Trade(
                finalDirection, finalEntry, lastCandle.Close, bar - entryBar + 1, roundTripCostPct));
        }

        return trades;
    }

    private static void Report(string mode, decimal entry, List<Trade> trades)
    {
        if (trades.Count == 0)
        {
            Console.WriteLine($"{mode,-16}{entry,7:F2}{0,8}{"-",8}{"-",9}{"-",10}{"-",9}{"-",9}");
            return;
        }

        decimal[] returns = [.. trades.Select(trade => trade.ReturnPct)];
        decimal profit = returns.Where(value => value > 0m).Sum();
        decimal loss = -returns.Where(value => value <= 0m).Sum();

        decimal equity = 100m;
        decimal peak = 100m;
        decimal drawdown = 0m;
        foreach (decimal value in returns)
        {
            equity *= 1m + value / 100m;
            peak = Math.Max(peak, equity);
            if (peak > 0m)
                drawdown = Math.Max(drawdown, (peak - equity) / peak * 100m);
        }

        double profitFactor = loss == 0m
            ? (profit > 0m ? double.PositiveInfinity : 0d)
            : (double)(profit / loss);
        Console.WriteLine($"{mode,-16}{entry,7:F2}{trades.Count,8}" +
            $"{(double)returns.Count(value => value > 0m) / returns.Length,8:P1}" +
            $"{profitFactor,9:F3}{equity - 100m,10:F2}{returns.Average(),9:F3}{drawdown,9:F2}");
    }
}
