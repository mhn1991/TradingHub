using System.Globalization;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Measures whether the module 5 trend layer carries directional information.
/// <para>
/// The six-instrument run showed 92 of 127 entries labelled Downtrend across markets that mostly
/// rose, and every entry was aligned with the agent's own reading - so the entry engine is doing
/// what it is told and the question is whether what it is told is worth anything. Every filter
/// tested so far (nesting, the 2:1 multiple, module 6's control gate) sits downstream of this call,
/// which is a plausible reason none of them generalised.
/// </para>
/// <para>
/// The test is a barrier race with the same payoff geometry the agent actually trades: from each
/// bar's close, a 1xATR stop against a 3xATR target, stop checked first. Break-even is a 25% hit
/// rate. Each state's hit rate is reported against the unconditional rate for the same direction
/// over the same bars, which is the null - a state that merely tracks drift will match it.
/// </para>
/// </summary>
[TestFixture]
[Explicit("diagnostic over real cached candles, not a correctness test")]
public sealed class ZZAlfonsoTrendLayerDiagnostic
{
    private const int AtrPeriod = 14;
    private const int Horizon = 100;
    private const decimal StopAtr = 1.0m;
    private const decimal TargetAtr = 3.0m;

    private static string DataDirectory =>
        Environment.GetEnvironmentVariable("ALFONSO_DATA") ?? "/mnt/storage/scratch/alfonso";

    private static List<AlfonsoBar> Load(string name)
    {
        string path = Path.Combine(DataDirectory, name);
        if (!File.Exists(path))
            Assert.Ignore($"No candle file at {path}.");

        List<AlfonsoBar> bars = [];
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] parts = line.Split(',');
            if (parts.Length < 5)
                continue;

            bars.Add(new AlfonsoBar(
                DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture),
                decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                decimal.Parse(parts[4], CultureInfo.InvariantCulture)));
        }

        return bars;
    }

    /// <summary>Causal Wilder ATR, so bar i only ever sees bars up to i.</summary>
    private static decimal[] AverageTrueRange(IReadOnlyList<AlfonsoBar> bars)
    {
        decimal[] atr = new decimal[bars.Count];
        decimal running = 0m;
        for (int index = 0; index < bars.Count; index++)
        {
            decimal previousClose = index == 0 ? bars[0].Open : bars[index - 1].Close;
            decimal trueRange = Math.Max(
                bars[index].High - bars[index].Low,
                Math.Max(
                    Math.Abs(bars[index].High - previousClose),
                    Math.Abs(bars[index].Low - previousClose)));

            running = index == 0 ? trueRange : ((running * (AtrPeriod - 1)) + trueRange) / AtrPeriod;
            atr[index] = running;
        }

        return atr;
    }

    /// <summary>1 = target first, 0 = stop first, null = neither inside the horizon.</summary>
    private static int? Race(IReadOnlyList<AlfonsoBar> bars, decimal[] atr, int index, int direction)
    {
        if (atr[index] <= 0m)
            return null;

        decimal entry = bars[index].Close;
        decimal stop = entry - (direction * StopAtr * atr[index]);
        decimal target = entry + (direction * TargetAtr * atr[index]);

        int last = Math.Min(index + Horizon, bars.Count - 1);
        for (int forward = index + 1; forward <= last; forward++)
        {
            bool stopped = direction > 0 ? bars[forward].Low <= stop : bars[forward].High >= stop;
            if (stopped)
                return 0;

            bool reached = direction > 0 ? bars[forward].High >= target : bars[forward].Low <= target;
            if (reached)
                return 1;
        }

        return null;
    }

    private static string Verdict(int wins, int resolved, double baseline)
    {
        if (resolved == 0)
            return "no resolved races";

        double rate = wins / (double)resolved;
        double expectancy = (4.0 * rate) - 1.0;
        double edge = rate - baseline;
        return $"{rate,6:P1}  EV {expectancy,+7:F3}R   vs base {baseline,6:P1}  edge {edge,+6:P1}";
    }

    [TestCase("gold")]
    [TestCase("silver")]
    [TestCase("nas100")]
    [TestCase("us30")]
    [TestCase("eurusd")]
    [TestCase("gbpjpy")]
    public void ReportTrendStateAccuracy(string symbol)
    {
        foreach ((string suffix, int minutes) in new[] { ("m15", 15), ("h1", 60), ("h4", 240), ("d1", 1440) })
        {
            List<AlfonsoBar> bars = Load($"{symbol}-{suffix}.csv");
            if (bars.Count < AtrPeriod * 4)
                continue;

            decimal[] atr = AverageTrueRange(bars);
            AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(minutes));

            Dictionary<AlfonsoTrend, int> occupancy = [];
            Dictionary<AlfonsoTrend, (int Wins, int Resolved)> conditional = [];
            int longWins = 0, longResolved = 0, shortWins = 0, shortResolved = 0;
            Dictionary<string, int> establishedBy = [];
            AlfonsoTrend previous = AlfonsoTrend.Unknown;

            for (int index = 0; index < bars.Count; index++)
            {
                analyzer.Apply(bars[index]);
                AlfonsoTrendSnapshot snapshot = analyzer.Trend;
                AlfonsoTrend state = snapshot.Trend;
                occupancy[state] = occupancy.GetValueOrDefault(state) + 1;

                if (state != previous && state is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend)
                {
                    string reason = snapshot.Reason ?? "(none)";
                    string key = reason.Contains("without", StringComparison.OrdinalIgnoreCase)
                        ? $"{state} without a trendline"
                        : reason.Contains("trendline", StringComparison.OrdinalIgnoreCase)
                            ? $"{state} with a trendline"
                            : $"{state} other";
                    establishedBy[key] = establishedBy.GetValueOrDefault(key) + 1;
                }

                previous = state;

                // Unconditional null for this bar, both directions.
                if (Race(bars, atr, index, +1) is { } longOutcome)
                {
                    longResolved++;
                    longWins += longOutcome;
                }

                if (Race(bars, atr, index, -1) is { } shortOutcome)
                {
                    shortResolved++;
                    shortWins += shortOutcome;
                }

                // State-conditional: trade the direction the agent would have taken.
                int direction = state switch
                {
                    AlfonsoTrend.Uptrend => +1,
                    AlfonsoTrend.Downtrend => -1,
                    _ => 0,
                };

                if (direction == 0)
                    continue;

                if (Race(bars, atr, index, direction) is not { } outcome)
                    continue;

                (int wins, int resolved) = conditional.GetValueOrDefault(state);
                conditional[state] = (wins + outcome, resolved + 1);
            }

            double longBase = longResolved == 0 ? 0 : longWins / (double)longResolved;
            double shortBase = shortResolved == 0 ? 0 : shortWins / (double)shortResolved;

            TestContext.Out.WriteLine(
                $"=== {symbol} {suffix}  {bars.Count:N0} bars  " +
                $"{bars[0].OpenTime:yyyy-MM-dd} -> {bars[^1].OpenTime:yyyy-MM-dd} ===");

            foreach (AlfonsoTrend state in Enum.GetValues<AlfonsoTrend>())
            {
                int count = occupancy.GetValueOrDefault(state);
                TestContext.Out.WriteLine(
                    $"  {state,-15} {count,7:N0}  {count / (double)bars.Count,7:P1}");
            }

            foreach (AlfonsoTrend state in new[] { AlfonsoTrend.Uptrend, AlfonsoTrend.Downtrend })
            {
                (int wins, int resolved) = conditional.GetValueOrDefault(state);
                double baseline = state == AlfonsoTrend.Uptrend ? longBase : shortBase;
                TestContext.Out.WriteLine(
                    $"  {state,-15} races {resolved,6:N0}  {Verdict(wins, resolved, baseline)}");
            }

            TestContext.Out.WriteLine(
                $"  unconditional   long {longBase,6:P1} ({longResolved:N0})   " +
                $"short {shortBase,6:P1} ({shortResolved:N0})   break-even 25.0%");

            if (establishedBy.Count > 0)
            {
                TestContext.Out.WriteLine("  established by:");
                foreach ((string key, int count) in establishedBy.OrderByDescending(pair => pair.Value))
                    TestContext.Out.WriteLine($"    {key,-32} {count,5:N0}");
            }

            TestContext.Out.WriteLine(string.Empty);
        }
    }

    private static readonly string[] Symbols =
        ["gold", "silver", "nas100", "us30", "eurusd", "gbpjpy"];

    /// <summary>
    /// One configuration under test, described by what it changes from the module defaults.
    /// </summary>
    private sealed record ZoneConfiguration(
        string Name, ImbalanceOptions Options, AlfonsoTrendOptions? Trend = null);

    private static List<ZoneConfiguration> Configurations() =>
    [
        new("default", new ImbalanceOptions()),

        // Raise the bar on the leg-out relative to the base - module 7's "minimum 2:1 imbalance"
        // is a floor, not a target, and 2:1 currently admits a level every seven bars.
        new("impulse:base 3", new ImbalanceOptions { MinimumImpulseToBaseRatio = 3.0m }),
        new("impulse:base 5", new ImbalanceOptions { MinimumImpulseToBaseRatio = 5.0m }),

        // Require the leg-out to be a genuinely large move for the instrument.
        new("impulse 3xATR", new ImbalanceOptions { MinimumImpulseAtrMultiple = 3.0m }),
        new("impulse 4xATR", new ImbalanceOptions { MinimumImpulseAtrMultiple = 4.0m }),

        // A tighter base is a more precisely located level.
        new("base body 0.35", new ImbalanceOptions { MaximumBasingBodyRatio = 0.35m }),
        new("base <= 3 candles", new ImbalanceOptions { MaximumBaseCandles = 3 }),

        // Kill zones on closes rather than wicks, so ordinary noise stops resetting the trend.
        new("elim on close", new ImbalanceOptions { EliminationRequiresClose = true }),

        // Two clear candles instead of one before the level counts as formed.
        new("consolidate 2", new ImbalanceOptions { ConsolidationAwayCandles = 2 }),

        // Combinations of whatever the single changes suggest is worth stacking.
        new("strict", new ImbalanceOptions
        {
            MinimumImpulseToBaseRatio = 3.0m,
            MinimumImpulseAtrMultiple = 3.0m,
            EliminationRequiresClose = true,
        }),
        new("very strict", new ImbalanceOptions
        {
            MinimumImpulseToBaseRatio = 5.0m,
            MinimumImpulseAtrMultiple = 4.0m,
            MaximumBasingBodyRatio = 0.35m,
            EliminationRequiresClose = true,
        }),

        // Step 1: only a zone that met the tradeability bar may move the trend. Until this is on,
        // the impulse thresholds below cannot reach the trend layer at all, which is why the first
        // sweep returned five configurations bit-identical to default.
        new("GATED default", new ImbalanceOptions(), Gate),
        new("GATED ratio 3", new ImbalanceOptions { MinimumImpulseToBaseRatio = 3.0m }, Gate),
        new("GATED ratio 5", new ImbalanceOptions { MinimumImpulseToBaseRatio = 5.0m }, Gate),
        new("GATED 3xATR", new ImbalanceOptions { MinimumImpulseAtrMultiple = 3.0m }, Gate),
        new("GATED 4xATR", new ImbalanceOptions { MinimumImpulseAtrMultiple = 4.0m }, Gate),
        new("GATED strict", new ImbalanceOptions
        {
            MinimumImpulseToBaseRatio = 3.0m,
            MinimumImpulseAtrMultiple = 3.0m,
        }, Gate),
    ];

    private static AlfonsoTrendOptions Gate =>
        new() { RequireTradeableZoneForTrendChange = true };

    /// <summary>
    /// Sweeps zone-quality thresholds and scores each one on the trend layer's directional edge
    /// rather than on P&L.
    /// <para>
    /// P&L over this window gives ~20 trades per instrument, which cannot separate settings. The
    /// barrier race gives hundreds to thousands of observations per instrument, so it can. The
    /// reported figure is the edge over the unconditional rate for the same direction, averaged
    /// across the six instruments, with the count of instruments where it came out positive -
    /// consistency across instruments is the thing that has repeatedly failed for this strategy.
    /// </para>
    /// </summary>
    [TestCase("m15", 15)]
    [TestCase("h1", 60)]
    [TestCase("h4", 240)]
    public void SweepZoneQuality(string suffix, int minutes)
    {
        TestContext.Out.WriteLine($"=== zone-quality sweep on {suffix} ===");
        TestContext.Out.WriteLine(
            $"{"configuration",-20} {"zones",8} {"flips",7} {"OOA",7} " +
            $"{"up edge",9} {"pos",5} {"down edge",10} {"pos",5}");

        foreach (ZoneConfiguration configuration in Configurations())
        {
            List<double> upEdges = [];
            List<double> downEdges = [];
            long zones = 0;
            long flips = 0;
            long outOfAlignment = 0;
            long totalBars = 0;

            foreach (string symbol in Symbols)
            {
                List<AlfonsoBar> bars = Load($"{symbol}-{suffix}.csv");
                if (bars.Count < AtrPeriod * 4)
                    continue;

                decimal[] atr = AverageTrueRange(bars);
                AlfonsoTimeframeAnalyzer analyzer =
                    new(TimeSpan.FromMinutes(minutes), configuration.Options, configuration.Trend);

                int upWins = 0, upRaces = 0, downWins = 0, downRaces = 0;
                int longWins = 0, longRaces = 0, shortWins = 0, shortRaces = 0;
                AlfonsoTrend previous = AlfonsoTrend.Unknown;

                for (int index = 0; index < bars.Count; index++)
                {
                    ImbalanceDetectorUpdate update = analyzer.Apply(bars[index]);
                    zones += update.Created.Count;

                    AlfonsoTrend state = analyzer.Trend.Trend;
                    if (state != previous && state is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend)
                        flips++;

                    previous = state;
                    if (state == AlfonsoTrend.OutOfAlignment)
                        outOfAlignment++;

                    if (Race(bars, atr, index, +1) is { } longOutcome)
                    {
                        longRaces++;
                        longWins += longOutcome;
                    }

                    if (Race(bars, atr, index, -1) is { } shortOutcome)
                    {
                        shortRaces++;
                        shortWins += shortOutcome;
                    }

                    if (state == AlfonsoTrend.Uptrend && Race(bars, atr, index, +1) is { } up)
                    {
                        upRaces++;
                        upWins += up;
                    }
                    else if (state == AlfonsoTrend.Downtrend && Race(bars, atr, index, -1) is { } down)
                    {
                        downRaces++;
                        downWins += down;
                    }
                }

                totalBars += bars.Count;

                if (upRaces > 0 && longRaces > 0)
                    upEdges.Add((upWins / (double)upRaces) - (longWins / (double)longRaces));
                if (downRaces > 0 && shortRaces > 0)
                    downEdges.Add((downWins / (double)downRaces) - (shortWins / (double)shortRaces));
            }

            double upMean = upEdges.Count == 0 ? 0 : upEdges.Average();
            double downMean = downEdges.Count == 0 ? 0 : downEdges.Average();
            int upPositive = upEdges.Count(edge => edge > 0);
            int downPositive = downEdges.Count(edge => edge > 0);

            TestContext.Out.WriteLine(
                $"{configuration.Name,-20} {zones,8:N0} {flips,7:N0} " +
                $"{outOfAlignment / (double)Math.Max(totalBars, 1),7:P1} " +
                $"{upMean,9:P1} {$"{upPositive}/{upEdges.Count}",5} " +
                $"{downMean,10:P1} {$"{downPositive}/{downEdges.Count}",5}");
        }

        TestContext.Out.WriteLine(string.Empty);
    }

    /// <summary>
    /// Tests whether resting limit orders are adversely selected by drift.
    /// <para>
    /// The six-instrument run took 92 shorts to 35 longs while every trend state sat at balanced
    /// occupancy, so the skew is not a directional call. The candidate proposed here is the entry
    /// mechanism: the agent rests limit orders ahead of price, so a sell sits above and a buy sits
    /// below. In a market that drifts up, price walks into the sell and away from the buy.
    /// </para>
    /// <para>
    /// This measures the price path directly rather than the agent, which gives thousands of
    /// observations per instrument instead of the twenty-odd trades the backtest produces. A buy
    /// limit is placed k x ATR below each close and a sell limit k x ATR above, and the question is
    /// simply which one price reaches within the horizon.
    /// </para>
    /// </summary>
    [TestCase("m15", 15)]
    [TestCase("h1", 60)]
    public void ReportLimitFillAsymmetry(string suffix, int minutes)
    {
        _ = minutes;
        TestContext.Out.WriteLine($"=== resting-limit touch rates on {suffix} ===");
        TestContext.Out.WriteLine(
            $"{"instrument",-10} {"drift",8} " +
            $"{"buy@1xATR",10} {"sell@1xATR",11} {"ratio",7}   " +
            $"{"buy@2xATR",10} {"sell@2xATR",11} {"ratio",7}");

        foreach (string symbol in Symbols)
        {
            List<AlfonsoBar> bars = Load($"{symbol}-{suffix}.csv");
            if (bars.Count < AtrPeriod * 4)
                continue;

            decimal[] atr = AverageTrueRange(bars);
            decimal drift = (bars[^1].Close - bars[0].Open) / bars[0].Open;
            string row = $"{symbol,-10} {drift,8:P1} ";

            foreach (decimal multiple in new[] { 1.0m, 2.0m })
            {
                int buyTouched = 0, sellTouched = 0, placed = 0;

                for (int index = 0; index < bars.Count; index++)
                {
                    if (atr[index] <= 0m)
                        continue;

                    decimal buy = bars[index].Close - (multiple * atr[index]);
                    decimal sell = bars[index].Close + (multiple * atr[index]);
                    bool hitBuy = false, hitSell = false;

                    int last = Math.Min(index + Horizon, bars.Count - 1);
                    for (int forward = index + 1; forward <= last; forward++)
                    {
                        hitBuy |= bars[forward].Low <= buy;
                        hitSell |= bars[forward].High >= sell;
                        if (hitBuy && hitSell)
                            break;
                    }

                    placed++;
                    if (hitBuy)
                        buyTouched++;
                    if (hitSell)
                        sellTouched++;
                }

                double buyRate = placed == 0 ? 0 : buyTouched / (double)placed;
                double sellRate = placed == 0 ? 0 : sellTouched / (double)placed;
                double ratio = buyRate <= 0 ? 0 : sellRate / buyRate;
                row += $"{buyRate,10:P1} {sellRate,11:P1} {ratio,7:F2}   ";
            }

            TestContext.Out.WriteLine(row);
        }

        TestContext.Out.WriteLine(
            "\nobserved trade skew to beat: 92 sells / 35 buys = 2.63:1");
        TestContext.Out.WriteLine(string.Empty);
    }

    /// <summary>
    /// Counts how often the three timeframes jointly agree on a direction.
    /// <para>
    /// Entries need H4/H1/M15 alignment, so what governs the long/short mix is the *joint*
    /// distribution, not the marginals. Each timeframe on its own sits near 14% Uptrend and 14%
    /// Downtrend, which looked balanced and led me to rule the trend layer out as the source of the
    /// 92-sells / 35-buys skew. Marginal balance does not imply joint balance, and the resting-limit
    /// explanation measured only 1.01-1.16:1 against the 2.63:1 that needs explaining.
    /// </para>
    /// </summary>
    [TestCase("gold")]
    [TestCase("silver")]
    [TestCase("nas100")]
    [TestCase("us30")]
    [TestCase("eurusd")]
    [TestCase("gbpjpy")]
    public void ReportJointAlignment(string symbol)
    {
        List<AlfonsoBar> top = Load($"{symbol}-h4.csv");
        List<AlfonsoBar> middle = Load($"{symbol}-h1.csv");
        List<AlfonsoBar> lower = Load($"{symbol}-m15.csv");

        AlfonsoTimeframeAnalyzer topAnalyzer = new(TimeSpan.FromHours(4));
        AlfonsoTimeframeAnalyzer middleAnalyzer = new(TimeSpan.FromHours(1));
        AlfonsoTimeframeAnalyzer lowerAnalyzer = new(TimeSpan.FromMinutes(15));

        // Replay all three in true chronological order so each bar only ever sees closed history.
        List<(DateTimeOffset At, int Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar bar in top)
            stream.Add((bar.OpenTime + TimeSpan.FromHours(4), 0, bar));
        foreach (AlfonsoBar bar in middle)
            stream.Add((bar.OpenTime + TimeSpan.FromHours(1), 1, bar));
        foreach (AlfonsoBar bar in lower)
            stream.Add((bar.OpenTime + TimeSpan.FromMinutes(15), 2, bar));
        stream.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Role.CompareTo(b.Role));

        int allUp = 0, allDown = 0, lowerBars = 0;
        Dictionary<string, int> joint = [];

        foreach ((_, int role, AlfonsoBar bar) in stream)
        {
            switch (role)
            {
                case 0: topAnalyzer.Apply(bar); continue;
                case 1: middleAnalyzer.Apply(bar); continue;
                default: lowerAnalyzer.Apply(bar); break;
            }

            lowerBars++;
            AlfonsoTrend t = topAnalyzer.Trend.Trend;
            AlfonsoTrend m = middleAnalyzer.Trend.Trend;
            AlfonsoTrend l = lowerAnalyzer.Trend.Trend;

            if (t == AlfonsoTrend.Uptrend && m == AlfonsoTrend.Uptrend && l == AlfonsoTrend.Uptrend)
                allUp++;
            if (t == AlfonsoTrend.Downtrend && m == AlfonsoTrend.Downtrend && l == AlfonsoTrend.Downtrend)
                allDown++;

            string key = $"{Short(t)}/{Short(m)}/{Short(l)}";
            joint[key] = joint.GetValueOrDefault(key) + 1;
        }

        double ratio = allUp == 0 ? double.NaN : allDown / (double)allUp;
        TestContext.Out.WriteLine(
            $"=== {symbol}: joint three-timeframe alignment over {lowerBars:N0} m15 bars ===");
        TestContext.Out.WriteLine(
            $"  all three Uptrend    {allUp,7:N0}  {allUp / (double)lowerBars,7:P2}");
        TestContext.Out.WriteLine(
            $"  all three Downtrend  {allDown,7:N0}  {allDown / (double)lowerBars,7:P2}");
        TestContext.Out.WriteLine(
            $"  DOWN:UP ratio        {ratio,7:F2}   (trade skew to explain: 2.63)");
        TestContext.Out.WriteLine("  most common joint states:");
        foreach ((string key, int count) in joint.OrderByDescending(pair => pair.Value).Take(5))
            TestContext.Out.WriteLine($"    {key,-18} {count,7:N0}  {count / (double)lowerBars,7:P1}");
        TestContext.Out.WriteLine(string.Empty);
    }

    private static string Short(AlfonsoTrend trend) => trend switch
    {
        AlfonsoTrend.Uptrend => "up",
        AlfonsoTrend.Downtrend => "down",
        AlfonsoTrend.OutOfAlignment => "ooa",
        _ => "?",
    };

    [TestCase("gold")]
    [TestCase("silver")]
    [TestCase("nas100")]
    [TestCase("us30")]
    [TestCase("eurusd")]
    [TestCase("gbpjpy")]
    public void ReportEliminationAsymmetry(string symbol)
    {
        TestContext.Out.WriteLine($"=== {symbol}: what the trend layer is fed ===");
        TestContext.Out.WriteLine(
            $"  {"tf",-5} {"demand made",12} {"supply made",12} {"demand elim",12} {"supply elim",12}  drift");

        foreach ((string suffix, int minutes) in new[] { ("m15", 15), ("h1", 60), ("h4", 240), ("d1", 1440) })
        {
            List<AlfonsoBar> bars = Load($"{symbol}-{suffix}.csv");
            if (bars.Count < AtrPeriod * 4)
                continue;

            AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(minutes));
            int demandMade = 0, supplyMade = 0, demandGone = 0, supplyGone = 0;

            foreach (AlfonsoBar bar in bars)
            {
                ImbalanceDetectorUpdate update = analyzer.Apply(bar);
                demandMade += update.Created.Count(zone => zone.Kind == ImbalanceKind.Demand);
                supplyMade += update.Created.Count(zone => zone.Kind == ImbalanceKind.Supply);
                demandGone += update.Eliminated.Count(zone => zone.Kind == ImbalanceKind.Demand);
                supplyGone += update.Eliminated.Count(zone => zone.Kind == ImbalanceKind.Supply);
            }

            decimal drift = (bars[^1].Close - bars[0].Open) / bars[0].Open;
            TestContext.Out.WriteLine(
                $"  {suffix,-5} {demandMade,12:N0} {supplyMade,12:N0} " +
                $"{demandGone,12:N0} {supplyGone,12:N0}  {drift,7:P1}");
        }

        TestContext.Out.WriteLine(string.Empty);
    }
}
