using TradingClassifier.Features;
using TrendStatistics.Detection;
using TrendStatistics.Segmentation;

namespace TradingClassifierRunner;

/// <summary>
/// Blueprint section 55 (Phase 2, "Historical Trend Library") plus the section 54 Phase 1
/// acceptance checks, run against real candles.
/// <para>
/// Reports the bull and bear datasets separately, because section 5 is explicit that they must
/// never be pooled - trends in the two directions have different duration and extent behaviour and
/// averaging them describes neither.
/// </para>
/// </summary>
public static class TrendStatsReport
{
    public static int Run(
        IReadOnlyList<ClassifierCandle> candles,
        string symbol,
        TrendDetectorConfig config)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0)
            throw new ArgumentException("No candles supplied.", nameof(candles));

        TrendStatistics.Data.Candle[] converted = candles
            .Select(candle => new TrendStatistics.Data.Candle
            {
                Symbol = symbol,
                // The classifier stamps candles by CLOSE time; TrendStatistics.Candle wants the
                // open. Subtracting one timeframe keeps the same bar, described from its own start.
                OpenTime = candle.Timestamp - config.Timeframe,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close
            })
            .ToArray();

        IReadOnlyList<TrendRecord> trends = new TrendSegmenter(config).Segment(converted);

        Console.WriteLine($"Segmented {converted.Length:N0} candles " +
            $"({converted[0].OpenTime:yyyy-MM-dd} -> {converted[^1].OpenTime:yyyy-MM-dd})");
        Console.WriteLine($"Detected {trends.Count} completed trends " +
            $"(an in-progress trend at the boundary is deliberately not force-closed)\n");

        if (trends.Count == 0)
        {
            Console.Error.WriteLine("No trends detected - section 54 says STOP and fix segmentation.");
            return 2;
        }

        foreach (TrendDirection direction in (TrendDirection[])[TrendDirection.Bullish, TrendDirection.Bearish])
        {
            TrendRecord[] side = trends.Where(trend => trend.Direction == direction).ToArray();
            Console.WriteLine($"--- {direction} ({side.Length} trends) ---");
            if (side.Length == 0)
            {
                Console.WriteLine("  none\n");
                continue;
            }

            Report("total move %", side.Select(t => t.TotalMovePct).ToArray());
            Report("move after confirm %", side.Select(t => t.MoveAfterConfirmationPct).ToArray());
            // The realistic hold: enter at confirmation, exit when the detector ENDS the trend.
            // MoveAfterConfirmationPct measures to the favourable extreme, which is a perfect exit
            // nobody gets - the end signal only fires after a 1.25 ATR retracement from that high.
            Report("confirm -> END %", side.Select(RealisedHoldPct).ToArray());
            Report("max adverse exc %", side.Select(t => t.MaximumAdverseExcursionPct).ToArray());
            Report("max retracement %", side.Select(t => t.MaximumRetracementPct).ToArray());
            Report("ATR-normalised move", side.Select(t => t.AtrNormalizedMove).ToArray());
            Report("duration (bars)", side.Select(t => (decimal)t.DurationBars).ToArray());
            Report("duration (hours)", side.Select(t => t.DurationHours).ToArray());
            Report("confirm delay (bars)", side.Select(t => (decimal)t.ConfirmationDelayBars).ToArray());
            Report("confirm delay %", side.Select(t => t.ConfirmationDelayPct).ToArray());

            Console.WriteLine("  largest by move:");
            foreach (TrendRecord trend in side.OrderByDescending(t => Math.Abs(t.TotalMovePct)).Take(3))
                Console.WriteLine($"    {trend.StructuralStartTime:yyyy-MM-dd} -> {trend.EndTime:yyyy-MM-dd}  " +
                    $"{trend.TotalMovePct,8:F2}%  {trend.DurationBars,4} bars");
            Console.WriteLine("  longest by duration:");
            foreach (TrendRecord trend in side.OrderByDescending(t => t.DurationBars).Take(3))
                Console.WriteLine($"    {trend.StructuralStartTime:yyyy-MM-dd} -> {trend.EndTime:yyyy-MM-dd}  " +
                    $"{trend.TotalMovePct,8:F2}%  {trend.DurationBars,4} bars");
            Console.WriteLine();
        }

        // Not a strategy result - there is no entry rule, no sizing and no costs. It is the
        // ceiling this detector's own definitions imply for a naive confirm-to-end hold.
        Console.WriteLine("--- naive confirm-to-end hold, GROSS of all costs ---");
        decimal[] all = trends.Select(RealisedHoldPct).ToArray();
        decimal[] wins = all.Where(v => v > 0m).ToArray();
        decimal[] losses = all.Where(v => v <= 0m).ToArray();
        decimal grossProfit = wins.Sum();
        decimal grossLoss = -losses.Sum();
        Console.WriteLine($"  trades {all.Length}   win rate {(double)wins.Length / all.Length:P1}   " +
            $"sum {all.Sum():F2}%   mean {all.Average():F3}%   " +
            $"profit factor {(grossLoss == 0m ? 0 : (double)(grossProfit / grossLoss)):F3}");
        foreach (TrendDirection direction in (TrendDirection[])[TrendDirection.Bullish, TrendDirection.Bearish])
        {
            decimal[] side = trends.Where(t => t.Direction == direction).Select(RealisedHoldPct).ToArray();
            if (side.Length == 0) continue;
            decimal profit = side.Where(v => v > 0m).Sum();
            decimal loss = -side.Where(v => v <= 0m).Sum();
            Console.WriteLine($"  {direction,-8} n={side.Length,4}  win rate {(double)side.Count(v => v > 0m) / side.Length:P1}   " +
                $"sum {side.Sum(),8:F2}%   mean {side.Average(),7:F3}%   PF {(loss == 0m ? 0 : (double)(profit / loss)),6:F3}");
        }
        Console.WriteLine();

        // Sections 16-19: how well are those quantiles themselves estimated?
        Console.WriteLine("--- bootstrap confidence intervals (sections 16-19) ---");
        var engine = new TrendStatistics.Statistics.BootstrapEngine(seed: 20260830);
        foreach (TrendDirection direction in (TrendDirection[])[TrendDirection.Bullish, TrendDirection.Bearish])
        {
            TrendStatistics.Statistics.TimedObservation[] moves = [.. trends
                .Where(t => t.Direction == direction)
                .Select(t => new TrendStatistics.Statistics.TimedObservation(
                    t.EndTime, Math.Abs(t.TotalMovePct)))];
            if (moves.Length == 0) continue;
            Console.WriteLine($"  {direction} total move %");
            Console.Write(engine.BuildBlockDistribution(
                moves, TimeSpan.FromDays(90), 10_000).ToText());
        }

        // Section 19: is 10,000 actually needed, or enough?
        TrendStatistics.Statistics.TimedObservation[] bullMoves = [.. trends
            .Where(t => t.Direction == TrendDirection.Bullish)
            .Select(t => new TrendStatistics.Statistics.TimedObservation(
                t.EndTime, Math.Abs(t.TotalMovePct)))];
        if (bullMoves.Length > 0)
        {
            Console.WriteLine("  convergence of the Bullish P95 estimate (section 19):");
            foreach (int iterations in (int[])[1_000, 5_000, 10_000, 20_000])
            {
                var result = engine.BuildBlockDistribution(
                    bullMoves, TimeSpan.FromDays(90), iterations);
                var p95 = result.For(0.95m);
                Console.WriteLine($"    {iterations,6} iters   P95={p95.PointEstimate,7:F3}   " +
                    $"CI {p95.LowerBound,7:F3} -> {p95.UpperBound,7:F3}   width {p95.IntervalWidth,6:F3}");
            }
            Console.WriteLine();
        }

        // Does the block bootstrap actually widen the intervals? Same-direction trends can anchor
        // inside their predecessor's retracement (TrendDetector line 200 sets the re-anchor floor
        // at the previous FAVOURABLE EXTREME, not its end), so consecutive records share bars and
        // are not independent. If that correlation is real, IID resampling understates uncertainty
        // and the block version must come out wider.
        decimal[] bullSample = [.. trends.Where(t => t.Direction == TrendDirection.Bullish)
            .Select(t => Math.Abs(t.TotalMovePct))];
        if (bullSample.Length > 0)
        {
            var engine2 = new TrendStatistics.Statistics.BootstrapEngine(seed: 20260830);
            var timed = trends.Where(t => t.Direction == TrendDirection.Bullish)
                .Select(t => new TrendStatistics.Statistics.TimedObservation(t.EndTime, Math.Abs(t.TotalMovePct)))
                .ToArray();
            var iid = engine2.BuildDistribution(bullSample, 10_000);
            var blocked = engine2.BuildBlockDistribution(timed, TimeSpan.FromDays(90), 10_000);

            Console.WriteLine("--- IID vs 90-day block bootstrap, Bullish total move % ---");
            Console.WriteLine($"  {"q",6}{"estimate",10}{"IID width",12}{"block width",14}{"ratio",9}");
            foreach (decimal q in (decimal[])[0.05m, 0.50m, 0.90m, 0.95m])
            {
                var a = iid.For(q);
                var b = blocked.For(q);
                Console.WriteLine($"  {q,6:F2}{a.PointEstimate,10:F3}{a.IntervalWidth,12:F3}" +
                    $"{b.IntervalWidth,14:F3}{(a.IntervalWidth == 0m ? 0m : b.IntervalWidth / a.IntervalWidth),9:F2}");
            }
            Console.WriteLine($"  {blocked.ResamplingUnitCount} independent 90-day blocks vs " +
                $"{bullSample.Length} observations\n");
        }

        AcceptanceChecks(trends, converted.Length, config);
        return 0;
    }

    /// <summary>
    /// Section 66 is explicit that mean +/- standard deviation must not be the main model, so the
    /// quantiles section 55 asks for are the headline and the mean is reported only alongside.
    /// </summary>
    private static void Report(string name, decimal[] values)
    {
        decimal[] sorted = [.. values.OrderBy(value => value)];
        Console.WriteLine($"  {name,-22} n={sorted.Length,4}  " +
            $"P05={Quantile(sorted, 0.05m),9:F2}  P50={Quantile(sorted, 0.50m),9:F2}  " +
            $"P90={Quantile(sorted, 0.90m),9:F2}  P95={Quantile(sorted, 0.95m),9:F2}  " +
            $"mean={values.Average(),9:F2}");
    }

    /// <summary>
    /// Return of the only mechanically-defined hold the detector supports: in at the confirmation
    /// close, out at the trend-end close. Signed by direction, so positive is a profit either way.
    /// Gross - no spread, commission or slippage.
    /// </summary>
    private static decimal RealisedHoldPct(TrendRecord trend)
    {
        if (trend.ConfirmationPrice == 0m)
            return 0m;
        decimal move = (trend.EndPrice - trend.ConfirmationPrice) / trend.ConfirmationPrice * 100m;
        return trend.Direction == TrendDirection.Bullish ? move : -move;
    }

    /// <summary>Linear-interpolated empirical quantile - blueprint section 15.</summary>
    private static decimal Quantile(decimal[] sorted, decimal q)
    {
        if (sorted.Length == 1)
            return sorted[0];
        decimal position = q * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    /// <summary>Section 54's Phase 1 acceptance criteria, checked mechanically where possible.</summary>
    private static void AcceptanceChecks(IReadOnlyList<TrendRecord> trends, int candleCount, TrendDetectorConfig config)
    {
        Console.WriteLine("--- section 54 acceptance checks ---");

        TrendRecord[] ordered = [.. trends.OrderBy(trend => trend.StructuralStartTime)];
        int structuralOverlaps = 0;
        int sameDirectionOverlaps = 0;
        int boundaryOverlaps = 0;
        int confirmationOverlaps = 0;
        for (int index = 1; index < ordered.Length; index++)
        {
            TrendRecord previous = ordered[index - 1];
            TrendRecord current = ordered[index];

            // Section 9 makes structural overlap EXPECTED: a trend's structural start is the swing
            // extreme, which is discovered retroactively and normally sits before the previous
            // trend's end. The meaningful check is whether two trends were ever live at once, i.e.
            // whether the new one CONFIRMED before the old one ended.
            if (current.StructuralStartTime < previous.EndTime)
            {
                structuralOverlaps++;
                if (current.Direction == previous.Direction)
                {
                    // A one-bar overlap is the interval convention, not concurrency: EndTime is the
                    // closing bar's close, and the next trend anchors on the extreme that ended it.
                    // Anything DEEPER means the detector re-anchored inside a trend it was still
                    // measuring, which would be a genuine double-count.
                    double barsDeep = (previous.EndTime - current.StructuralStartTime).TotalHours
                        / config.Timeframe.TotalHours;
                    if (barsDeep > 1.0001)
                        sameDirectionOverlaps++;
                    else
                        boundaryOverlaps++;
                }
            }

            if (current.ConfirmationTime < previous.EndTime)
                confirmationOverlaps++;
        }
        int overlaps = confirmationOverlaps;

        int bull = trends.Count(t => t.Direction == TrendDirection.Bullish);
        int bear = trends.Count - bull;
        decimal barsPerTrend = (decimal)candleCount / trends.Count;
        int zeroDuration = trends.Count(t => t.DurationBars <= 0);
        int negativeDelay = trends.Count(t => t.ConfirmationDelayBars < 0);
        int badOrder = trends.Count(t => t.ConfirmationTime < t.StructuralStartTime || t.EndTime < t.ConfirmationTime);

        Console.WriteLine($"  structural-start overlaps     {structuralOverlaps}  (expected: section 9 discovers the start retroactively)");
        Console.WriteLine($"    same-direction, 1-bar       {boundaryOverlaps}  (interval convention, benign)");
        // Not a defect: TrendDetector sets the re-anchor floor at the previous trend's FAVOURABLE
        // EXTREME rather than its end, so a same-direction successor legitimately anchors at the
        // retracement low inside its predecessor. The consequence is correlated observations, which
        // is what the block bootstrap exists to price in - not a miscount to be fixed.
        Console.WriteLine($"    same-direction, deeper      {sameDirectionOverlaps}  " +
            $"{(sameDirectionOverlaps == 0 ? "OK" : "(by design - correlated, see block bootstrap)")}");
        Console.WriteLine($"  concurrent live trends        {overlaps}  {(overlaps == 0 ? "OK" : "<-- INVESTIGATE")}");
        Console.WriteLine($"  bull / bear balance           {bull} / {bear}");
        Console.WriteLine($"  candles per detected trend    {barsPerTrend:F1}");
        Console.WriteLine($"  zero-duration trends          {zeroDuration}  {(zeroDuration == 0 ? "OK" : "<-- INVESTIGATE")}");
        Console.WriteLine($"  negative confirmation delay   {negativeDelay}  {(negativeDelay == 0 ? "OK" : "<-- LOOK-AHEAD")}");
        Console.WriteLine($"  out-of-order timestamps       {badOrder}  {(badOrder == 0 ? "OK" : "<-- LOOK-AHEAD")}");
        Console.WriteLine($"  timeout bound (config)        {config.CandidateTimeoutBars} bars");

        if (sameDirectionOverlaps > 0)
        {
            Console.WriteLine($"\n--- the {sameDirectionOverlaps} deeper same-direction overlaps ---");
            Console.WriteLine($"  {"prev start",-12}{"prev end",-12}{"next start",-12}{"next conf",-12}" +
                $"{"dir",-9}{"gap(bars)",10}{"prev move%",11}{"next move%",11}");
            for (int index = 1; index < ordered.Length; index++)
            {
                TrendRecord previous = ordered[index - 1];
                TrendRecord current = ordered[index];
                if (current.StructuralStartTime >= previous.EndTime || current.Direction != previous.Direction)
                    continue;
                if ((previous.EndTime - current.StructuralStartTime).TotalHours / config.Timeframe.TotalHours <= 1.0001)
                    continue;

                // Negative gap = how far back inside the previous trend the new structural start
                // sits. A start at or after the previous FAVOURABLE EXTREME is the benign case:
                // the detector is re-anchoring on the same swing it just finished measuring.
                double gapHours = (current.StructuralStartTime - previous.EndTime).TotalHours;
                int gapBars = (int)Math.Round(gapHours / config.Timeframe.TotalHours);
                bool afterExtreme = current.StructuralStartTime >= previous.FavorableExtremeTime;

                Console.WriteLine($"  {previous.StructuralStartTime:yyyy-MM-dd,-12}"
                    .Replace(",-12", "  ") +
                    $"{previous.EndTime:yyyy-MM-dd}  {current.StructuralStartTime:yyyy-MM-dd}  " +
                    $"{current.ConfirmationTime:yyyy-MM-dd}  {current.Direction,-9}{gapBars,10}" +
                    $"{previous.TotalMovePct,11:F2}{current.TotalMovePct,11:F2}" +
                    (afterExtreme ? "   (starts at/after prev extreme)" : "   <-- starts BEFORE prev extreme"));
            }
        }
    }
}
