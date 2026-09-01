using System.Globalization;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Runs the Set and Forget zone and trend engines over real XAU/USD candles and reports what they
/// found.
/// <para>
/// This is a diagnostic, not a correctness test, and it exists because the unit tests cannot answer
/// the question that matters. They pin each rule against a hand-built fixture; they cannot say
/// whether the zones land where a person reading the same chart would draw them. CLAUDE.md records
/// that this repo has already shipped two subtle bugs in supply/demand pool lifecycle code that the
/// unit suite passed identically with and without - so on this particular subsystem, a green suite
/// is not evidence.
/// </para>
/// <para>
/// Bars come from CSVs extracted from the simulator's own market cache. Point ALFONSO_DATA at the
/// directory holding xauusd-m15.csv, xauusd-h1.csv and xauusd-h4.csv.
/// </para>
/// </summary>
[TestFixture]
[Explicit("diagnostic over real cached candles, not a correctness test")]
public sealed class ZZAlfonsoRealDataDiagnostic
{
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

    [TestCase("xauusd-d1full.csv", 1440)]
    [TestCase("xauusd-d1.csv", 1440)]
    [TestCase("xagusd-h4.csv", 240)]
    [TestCase("xagusd-h1.csv", 60)]
    [TestCase("xagusd-m15.csv", 15)]
    [TestCase("xauusd-m1.csv", 1)]
    [TestCase("xauusd-m5.csv", 5)]
    [TestCase("xauusd-m30.csv", 30)]
    [TestCase("xauusd-h4.csv", 240)]
    [TestCase("xauusd-h1.csv", 60)]
    [TestCase("xauusd-m15.csv", 15)]
    public void ReportZonesAndTrendOnRealCandles(string file, int intervalMinutes)
    {
        List<AlfonsoBar> bars = Load(file);
        AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(intervalMinutes));

        List<Imbalance> created = [];
        List<Imbalance> eliminated = [];
        int tested = 0;
        Dictionary<AlfonsoTrend, int> barsInTrend = [];
        Dictionary<RangeLocation, int> barsInRange = [];
        int overExtendedBars = 0;
        int buyingBlocked = 0;
        int sellingBlocked = 0;
        int controlledBars = 0;
        int controlDemand = 0;

        int peakLive = 0;
        foreach (AlfonsoBar bar in bars)
        {
            ImbalanceDetectorUpdate update = analyzer.Apply(bar);
            peakLive = Math.Max(peakLive, analyzer.Zones.Count);
            created.AddRange(update.Created);
            eliminated.AddRange(update.Eliminated);
            tested += update.Tested.Count;

            AlfonsoTrendSnapshot state = analyzer.Trend;
            barsInTrend[state.Trend] = barsInTrend.GetValueOrDefault(state.Trend) + 1;
            if (state.IsOverExtended)
                overExtendedBars++;

            SupplyDemandRange range = analyzer.Range;
            barsInRange[range.Location] = barsInRange.GetValueOrDefault(range.Location) + 1;
            if (!range.AllowsBuying)
                buyingBlocked++;
            if (!range.AllowsSelling)
                sellingBlocked++;

            if (analyzer.InControl is { } control)
            {
                controlledBars++;
                if (control.Kind == ImbalanceKind.Demand)
                    controlDemand++;
            }
        }

        TestContext.Out.WriteLine($"=== {file}  ({bars.Count:N0} bars, {bars[0].OpenTime:yyyy-MM-dd} -> {bars[^1].OpenTime:yyyy-MM-dd}) ===");
        TestContext.Out.WriteLine($"zones created      {created.Count:N0}   ({created.Count / (double)bars.Count:P2} of bars)");
        TestContext.Out.WriteLine($"  demand           {created.Count(z => z.Kind == ImbalanceKind.Demand):N0}");
        TestContext.Out.WriteLine($"  supply           {created.Count(z => z.Kind == ImbalanceKind.Supply):N0}");
        TestContext.Out.WriteLine($"  continuation     {created.Count(z => z.IsContinuationPattern):N0}");
        TestContext.Out.WriteLine($"  tradeable        {created.Count(z => z.MeetsTradeabilityCriteria):N0}");
        TestContext.Out.WriteLine($"zones eliminated   {eliminated.Count:N0}");
        TestContext.Out.WriteLine($"tests completed    {tested:N0}");
        TestContext.Out.WriteLine($"still live at end  {analyzer.Zones.Count:N0}");
        TestContext.Out.WriteLine($"PEAK live zones    {peakLive:N0}   (cap {new ImbalanceOptions().MaximumTrackedZones})");

        TestContext.Out.WriteLine("\nstrength:");
        foreach (ImpulseStrength strength in Enum.GetValues<ImpulseStrength>())
            TestContext.Out.WriteLine($"  {strength,-8} {created.Count(z => z.Strength == strength):N0}");

        TestContext.Out.WriteLine("\naccomplishment:");
        foreach (Accomplishment flag in (Accomplishment[])
            [Accomplishment.TrendlineBreak, Accomplishment.OpposingImbalanceEliminated,
             Accomplishment.ExtremeBroken, Accomplishment.SwingBroken])
        {
            TestContext.Out.WriteLine($"  {flag,-28} {created.Count(z => z.Accomplished.HasFlag(flag)):N0}");
        }

        TestContext.Out.WriteLine(
            $"  {"(none - not valid)",-28} {created.Count(z => z.Accomplished == Accomplishment.None):N0}");

        TestContext.Out.WriteLine("\nbars by trend state:");
        foreach ((AlfonsoTrend trend, int count) in barsInTrend.OrderByDescending(pair => pair.Value))
            TestContext.Out.WriteLine($"  {trend,-16} {count,7:N0}  {count / (double)bars.Count:P1}");
        TestContext.Out.WriteLine($"  over-extended    {overExtendedBars,7:N0}  {overExtendedBars / (double)bars.Count:P1}");

        TestContext.Out.WriteLine("\nbars by range location:");
        foreach ((RangeLocation location, int count) in barsInRange.OrderByDescending(pair => pair.Value))
            TestContext.Out.WriteLine($"  {location,-16} {count,7:N0}  {count / (double)bars.Count:P1}");
        TestContext.Out.WriteLine($"  buying blocked   {buyingBlocked,7:N0}  {buyingBlocked / (double)bars.Count:P1}");
        TestContext.Out.WriteLine($"  selling blocked  {sellingBlocked,7:N0}  {sellingBlocked / (double)bars.Count:P1}");
        TestContext.Out.WriteLine(
            $"  zone in control  {controlledBars,7:N0}  {controlledBars / (double)bars.Count:P1}" +
            $"  (demand {controlDemand:N0} / supply {controlledBars - controlDemand:N0})");

        if (created.Count > 0)
        {
            TestContext.Out.WriteLine("\nbase size / impulse ratio / width (percentiles):");
            Percentiles("base candles", created.Select(z => (decimal)z.BaseCandleCount));
            Percentiles("impulse:base", created.Select(z => z.ImpulseToBaseRatio));
            Percentiles("width", created.Select(z => z.Width));
            Percentiles("impulse ATRs", created.Select(z => z.ImpulseDisplacement));

            TestContext.Out.WriteLine("\nfirst 8 zones:");
            foreach (Imbalance zone in created.Take(8))
            {
                TestContext.Out.WriteLine(
                    $"  {zone.ConfirmedAt:yyyy-MM-dd HH:mm}  {zone.Kind,-6} " +
                    $"prox {zone.Proximal,10:F2}  dist {zone.Distal,10:F2}  " +
                    $"base {zone.BaseCandleCount}  ratio {zone.ImpulseToBaseRatio,6:F2}  " +
                    $"{zone.Strength,-6} {(zone.IsContinuationPattern ? "CP" : "swing")}  {zone.Accomplished}");
            }
        }

        // The only hard assertion: the engine must actually find something. Zero zones over months
        // of real data would mean the rules never fire, which is the failure mode that looks
        // identical to a measured null.
        Assert.That(created, Is.Not.Empty, "no zones found on real data");
    }

    /// <summary>
    /// Which gate is actually binding. Each row relaxes exactly one rule, so the drop from the
    /// permissive row to the default row prices that rule on real candles rather than in theory.
    /// </summary>
    [Test]
    public void ReportWhichGateIsBinding()
    {
        List<AlfonsoBar> bars = Load("xauusd-h4.csv");

        (string Label, ImbalanceOptions Options)[] variants =
        [
            ("default", new ImbalanceOptions()),
            ("no accomplishment gate", new ImbalanceOptions { RequireAccomplishment = false }),
            ("consolidation 1 -> 1, impulse window 30", new ImbalanceOptions { MaximumImpulseCandles = 30 }),
            ("base up to 6, basing body <= 0.65", new ImbalanceOptions { MaximumBasingBodyRatio = 0.65m }),
            ("impulse >= 1.5 ATR", new ImbalanceOptions { MinimumImpulseAtrMultiple = 1.5m }),
            ("impulse speed window 3", new ImbalanceOptions { ImpulseSpeedCandles = 3 }),
            ("no accomplishment + 1.5 ATR", new ImbalanceOptions
            {
                MinimumImpulseAtrMultiple = 1.5m,
                RequireAccomplishment = false
            })
        ];

        TestContext.Out.WriteLine($"{"variant",-44}{"zones",8}{"tradeable",11}{"strong",8}{"swings",8}");
        foreach ((string label, ImbalanceOptions options) in variants)
        {
            ImbalanceDetector detector = new(TimeSpan.FromHours(4), options);
            List<Imbalance> created = [];
            foreach (AlfonsoBar bar in bars)
                created.AddRange(detector.Apply(bar).Created);

            TestContext.Out.WriteLine(
                $"{label,-44}{created.Count,8}" +
                $"{created.Count(z => z.MeetsTradeabilityCriteria),11}" +
                $"{created.Count(z => z.Strength != ImpulseStrength.Weak),8}" +
                $"{created.Count(z => !z.IsContinuationPattern),8}");
        }
    }

    /// <summary>
    /// The whole three-timeframe sequence over real candles. The question this answers is the only
    /// one that decides whether an agent built on this can trade at all: how often does a permitted
    /// alignment actually occur, and does a plannable zone exist when it does?
    /// </summary>
    [TestCase("xauusd")]
    [TestCase("xagusd")]
    public void ReportSequenceCandidatesOnRealCandles(string symbol = "xauusd")
    {
        List<AlfonsoBar> h4 = Load($"{symbol}-h4.csv");
        List<AlfonsoBar> h1 = Load($"{symbol}-h1.csv");
        List<AlfonsoBar> m15 = Load($"{symbol}-m15.csv");

        AlfonsoSequenceAnalyzer analyzer = new(TimeframeSequence.Scalping);

        // Merged by CLOSE time, so a candle is only ever applied once the market could have seen it.
        List<(DateTimeOffset Closes, SequenceRole Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar bar in h4)
            stream.Add((bar.OpenTime.AddHours(4), SequenceRole.Top, bar));
        foreach (AlfonsoBar bar in h1)
            stream.Add((bar.OpenTime.AddHours(1), SequenceRole.Middle, bar));
        foreach (AlfonsoBar bar in m15)
            stream.Add((bar.OpenTime.AddMinutes(15), SequenceRole.Lower, bar));
        stream.Sort((left, right) => left.Closes.CompareTo(right.Closes));

        Dictionary<string, int> scenarioBars = [];
        int tradeableBars = 0;
        int barsWithCandidates = 0;
        int totalCandidates = 0;
        Dictionary<SequenceRole, int> byEntryTimeframe = [];
        List<TradeCandidate> examples = [];

        // A zone stays plannable on every bar until price reaches it or it breaks, so the raw
        // candidate count is bars-times-zones and says nothing about trade frequency. The distinct
        // zones are the opportunities.
        HashSet<(DateTimeOffset, ImbalanceKind, SequenceRole)> distinct = [];

        foreach ((_, SequenceRole role, AlfonsoBar bar) in stream)
        {
            analyzer.Apply(role, bar);
            if (role != SequenceRole.Lower)
                continue;

            ScenarioResolution scenario = analyzer.Scenario;
            string key = $"{analyzer[SequenceRole.Top].Trend.Trend}/{analyzer[SequenceRole.Middle].Trend.Trend}/{analyzer[SequenceRole.Lower].Trend.Trend}";
            scenarioBars[key] = scenarioBars.GetValueOrDefault(key) + 1;

            if (!scenario.CanTrade)
                continue;

            tradeableBars++;
            IReadOnlyList<TradeCandidate> candidates = analyzer.Candidates(bar.Close);
            if (candidates.Count == 0)
                continue;

            barsWithCandidates++;
            totalCandidates += candidates.Count;
            foreach (TradeCandidate candidate in candidates)
            {
                byEntryTimeframe[candidate.EntryTimeframe] =
                    byEntryTimeframe.GetValueOrDefault(candidate.EntryTimeframe) + 1;
                distinct.Add((candidate.Zone.BaseEnd, candidate.Side, candidate.EntryTimeframe));
            }

            if (examples.Count < 6)
                examples.Add(candidates[0]);
        }

        int lowerBars = m15.Count;
        TestContext.Out.WriteLine($"=== {symbol} H4/H1/M15 over {lowerBars:N0} execution bars ===");
        TestContext.Out.WriteLine($"tradeable alignment   {tradeableBars,7:N0}  {tradeableBars / (double)lowerBars:P1}");
        TestContext.Out.WriteLine($"bars with candidates  {barsWithCandidates,7:N0}  {barsWithCandidates / (double)lowerBars:P1}");
        TestContext.Out.WriteLine($"candidates total      {totalCandidates,7:N0}");
        TestContext.Out.WriteLine($"DISTINCT zones        {distinct.Count,7:N0}   <- plannable opportunities");

        TestContext.Out.WriteLine("\ncandidates by entry timeframe:");
        foreach ((SequenceRole role, int count) in byEntryTimeframe.OrderByDescending(pair => pair.Value))
            TestContext.Out.WriteLine($"  {role,-8} {count,7:N0}");

        TestContext.Out.WriteLine("\ntop alignments by execution bars:");
        foreach ((string key, int count) in scenarioBars.OrderByDescending(pair => pair.Value).Take(8))
            TestContext.Out.WriteLine($"  {key,-52} {count,7:N0}  {count / (double)lowerBars:P1}");

        TestContext.Out.WriteLine("\nexample candidates:");
        foreach (TradeCandidate candidate in examples)
        {
            TestContext.Out.WriteLine(
                $"  {candidate.Zone.ConfirmedAt:yyyy-MM-dd HH:mm}  {candidate.Side,-6} " +
                $"at {candidate.EntryTimeframe,-6} prox {candidate.Zone.Proximal,9:F2} " +
                $"stop {candidate.Zone.StopPrice(0.25m),9:F2} target {candidate.Zone.TargetPrice(0.25m, 3m),9:F2}" +
                $"  host {(candidate.Host is null ? "-" : candidate.Host.Proximal.ToString("F2"))}");
        }
    }

    /// <summary>
    /// Drives the ENTRY DECISION over real candles, not just the analyzer.
    /// <para>
    /// The analyzer reporting candidates says nothing about whether the agent ever acts on one: the
    /// agent applies further gates - price must have reached the proximal line, the level must still
    /// be fresh - and a fault in any of them produces zero trades while every layer underneath looks
    /// healthy. That is exactly what happened: the candidate filter dropped any zone price had
    /// entered, so the one bar that matters was excluded and a fifteen-day run traded nothing.
    /// </para>
    /// </summary>
    [TestCase("xauusd")]
    [TestCase("xagusd")]
    public void ReportEntryTriggersOnRealCandles(string symbol = "xauusd")
    {
        List<AlfonsoBar> h4 = Load($"{symbol}-h4.csv");
        List<AlfonsoBar> h1 = Load($"{symbol}-h1.csv");
        List<AlfonsoBar> m15 = Load($"{symbol}-m15.csv");

        AlfonsoSequenceAnalyzer analyzer = new(TimeframeSequence.Scalping);

        List<(DateTimeOffset Closes, SequenceRole Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar bar in h4)
            stream.Add((bar.OpenTime.AddHours(4), SequenceRole.Top, bar));
        foreach (AlfonsoBar bar in h1)
            stream.Add((bar.OpenTime.AddHours(1), SequenceRole.Middle, bar));
        foreach (AlfonsoBar bar in m15)
            stream.Add((bar.OpenTime.AddMinutes(15), SequenceRole.Lower, bar));
        stream.Sort((left, right) => left.Closes.CompareTo(right.Closes));

        int reached = 0;
        int freshBlocked = 0;
        HashSet<DateTimeOffset> triggered = [];
        List<string> firstFew = [];
        bool holding = false;
        decimal entry = 0m, stop = 0m, target = 0m;
        bool longSide = false;
        int wins = 0, losses = 0;

        foreach ((_, SequenceRole role, AlfonsoBar bar) in stream)
        {
            analyzer.Apply(role, bar);
            if (role != SequenceRole.Lower)
                continue;

            // A crude bracket, only to confirm entries resolve to outcomes at all.
            if (holding)
            {
                bool hitStop = longSide ? bar.Low <= stop : bar.High >= stop;
                bool hitTarget = longSide ? bar.High >= target : bar.Low <= target;
                if (hitStop) { losses++; holding = false; }
                else if (hitTarget) { wins++; holding = false; }
                continue;
            }

            foreach (TradeCandidate candidate in analyzer.Candidates(bar.Close))
            {
                Imbalance zone = candidate.Zone;
                bool arrived = candidate.Side == ImbalanceKind.Demand
                    ? bar.Low <= zone.Proximal
                    : bar.High >= zone.Proximal;
                if (!arrived)
                    continue;

                reached++;
                if (zone.State != ImbalanceState.Fresh) { freshBlocked++; continue; }
                if (!triggered.Add(zone.BaseEnd))
                    continue;

                longSide = candidate.Side == ImbalanceKind.Demand;
                entry = zone.Proximal;
                stop = zone.StopPrice(0.25m);
                target = zone.TargetPrice(0.25m, 3m);
                holding = true;

                if (firstFew.Count < 5)
                {
                    firstFew.Add(
                        $"  {bar.OpenTime:yyyy-MM-dd HH:mm}  {(longSide ? "BUY " : "SELL")} " +
                        $"@{entry,9:F2} stop {stop,9:F2} target {target,9:F2} " +
                        $"risk {Math.Abs(entry - stop),6:F2}");
                }

                break;
            }
        }

        TestContext.Out.WriteLine($"=== {symbol} agent-level entry triggers, H4/H1/M15 ===");
        TestContext.Out.WriteLine($"arrivals at a planned zone   {reached,7:N0}");
        TestContext.Out.WriteLine($"blocked as not fresh         {freshBlocked,7:N0}");
        TestContext.Out.WriteLine($"DISTINCT entries taken       {triggered.Count,7:N0}");
        TestContext.Out.WriteLine($"resolved  win {wins} / loss {losses}" +
            (wins + losses > 0 ? $"   hit-rate {wins / (double)(wins + losses):P1}" : ""));
        TestContext.Out.WriteLine("\nfirst entries:");
        foreach (string line in firstFew)
            TestContext.Out.WriteLine(line);

        // The load-bearing assertion. Zero entries over eight months of real candles means a gate is
        // broken, and it looks identical to a strategy that simply never qualifies.
        Assert.That(triggered, Is.Not.Empty, "the agent never entered on eight months of real data");
    }

    /// <summary>
    /// Sweeps the fixed reward multiple over a CONSTANT entry set.
    /// <para>
    /// The target never affects which entries are taken, only how they resolve, so one pass can
    /// price every multiple against exactly the same trades. That isolates the geometry: any
    /// difference between rows is the target and nothing else. Running a backtest per multiple would
    /// take hours and would also let the entry sets drift apart, because a longer-held trade blocks
    /// later entries.
    /// </para>
    /// <para>
    /// Costs are charged per trade in R, not as a flat number: the round trip is a fixed fraction of
    /// PRICE, so a tight zone pays a far larger share of its own R than a wide one. That is the whole
    /// reason the answer depends on timeframe.
    /// </para>
    /// <para>
    /// Where a single bar spans both barriers the STOP is taken first. With OHLC alone the true
    /// order is unknowable, and assuming the target would flatter every row - most of all the
    /// distant targets this sweep is meant to judge.
    /// </para>
    /// </summary>
    [TestCase("h4-h1-m15", 240, 60, 15)]
    [TestCase("d1-h4-h1", 1440, 240, 60)]
    public void SweepRewardMultipleOverAConstantEntrySet(
        string label, int topMinutes, int middleMinutes, int lowerMinutes)
    {
        Dictionary<int, string> files = new()
        {
            [1] = "xauusd-m1.csv", [5] = "xauusd-m5.csv", [15] = "full-xauusd-m15.csv",
            [30] = "xauusd-m30.csv", [60] = "full-xauusd-h1.csv", [240] = "full-xauusd-h4.csv", [1440] = "xauusd-d1full.csv"
        };
        if (!files.ContainsKey(topMinutes) || !files.ContainsKey(middleMinutes) || !files.ContainsKey(lowerMinutes))
            Assert.Ignore("no candle file for one of the requested timeframes");

        List<AlfonsoBar> top = Load(files[topMinutes]);
        List<AlfonsoBar> middle = Load(files[middleMinutes]);
        List<AlfonsoBar> lower = Load(files[lowerMinutes]);

        AlfonsoSequenceAnalyzer analyzer = new(new TimeframeSequence
        {
            Top = TimeSpan.FromMinutes(topMinutes),
            Middle = TimeSpan.FromMinutes(middleMinutes),
            Lower = TimeSpan.FromMinutes(lowerMinutes)
        });

        List<(DateTimeOffset Closes, SequenceRole Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar bar in top)
            stream.Add((bar.OpenTime.AddMinutes(topMinutes), SequenceRole.Top, bar));
        foreach (AlfonsoBar bar in middle)
            stream.Add((bar.OpenTime.AddMinutes(middleMinutes), SequenceRole.Middle, bar));
        foreach (AlfonsoBar bar in lower)
            stream.Add((bar.OpenTime.AddMinutes(lowerMinutes), SequenceRole.Lower, bar));
        stream.Sort((left, right) => left.Closes.CompareTo(right.Closes));

        // Collect entries once. Index into the execution series so exits can be walked forward.
        List<(int Bar, bool Long, decimal Entry, decimal Stop)> entries = [];
        HashSet<DateTimeOffset> taken = [];
        int executionIndex = -1;

        foreach ((_, SequenceRole role, AlfonsoBar bar) in stream)
        {
            analyzer.Apply(role, bar);
            if (role != SequenceRole.Lower)
                continue;

            executionIndex++;
            foreach (TradeCandidate candidate in analyzer.Candidates(bar.Close))
            {
                Imbalance zone = candidate.Zone;
                bool arrived = candidate.Side == ImbalanceKind.Demand
                    ? bar.Low <= zone.Proximal
                    : bar.High >= zone.Proximal;
                if (!arrived || zone.State != ImbalanceState.Fresh || !taken.Add(zone.BaseEnd))
                    continue;

                entries.Add((
                    executionIndex,
                    candidate.Side == ImbalanceKind.Demand,
                    zone.Proximal,
                    zone.StopPrice(0.25m)));
                break;
            }
        }

        const decimal roundTripBasisPoints = 2.4m;
        decimal[] multiples = [1.0m, 1.5m, 2.0m, 2.5m, 3.0m, 4.0m, 5.0m];

        TestContext.Out.WriteLine(
            $"=== reward sweep · {label} · {entries.Count} entries · costs {roundTripBasisPoints}bp round trip ===");
        TestContext.Out.WriteLine(
            $"{"target",8}{"n",6}{"win%",8}{"PF_R",9}{"netR",10}{"avgR",10}{"unresolved",12}");

        foreach (decimal multiple in multiples)
        {
            List<decimal> results = [];
            int unresolved = 0;

            foreach ((int at, bool isLong, decimal entry, decimal stop) in entries)
            {
                decimal risk = Math.Abs(entry - stop);
                if (risk <= 0m)
                    continue;

                decimal costR = entry * roundTripBasisPoints / 10_000m / risk;
                decimal target = isLong ? entry + (risk * multiple) : entry - (risk * multiple);

                bool done = false;
                for (int index = at + 1; index < lower.Count; index++)
                {
                    AlfonsoBar bar = lower[index];
                    bool hitStop = isLong ? bar.Low <= stop : bar.High >= stop;
                    bool hitTarget = isLong ? bar.High >= target : bar.Low <= target;

                    if (hitStop) { results.Add(-1m - costR); done = true; break; }
                    if (hitTarget) { results.Add(multiple - costR); done = true; break; }
                }

                if (!done)
                    unresolved++;
            }

            if (results.Count == 0)
                continue;

            List<decimal> wins = results.Where(value => value > 0m).ToList();
            List<decimal> losses = results.Where(value => value <= 0m).ToList();
            decimal profitFactor = losses.Count > 0 && losses.Sum() != 0m
                ? wins.Sum() / -losses.Sum()
                : 0m;

            TestContext.Out.WriteLine(
                $"{multiple,7:F1}:1{results.Count,6}{wins.Count / (double)results.Count,8:P1}" +
                $"{profitFactor,9:F3}{results.Sum(),10:F2}{results.Average(),10:F4}{unresolved,12}");
        }
    }

    /// <summary>
    /// Sensitivity of the swing / continuation split, which decides everything downstream.
    /// <para>
    /// A zone is a swing only when nothing in the preceding LegInLookbackCandles traded beyond its
    /// distal. Module 3 forbids drawing trendlines from continuation patterns, so this one number
    /// controls how many swings exist, therefore whether a trendline can be drawn at all, therefore
    /// how often the course's PRIMARY route to creating an imbalance - the trendline break - can
    /// fire. It fired 3 times in 152 zones on daily gold and 9 times in 145 on H4, which is either
    /// correct or a symptom of this threshold being wrong. The sweep is the only way to tell.
    /// </para>
    /// </summary>
    [Test]
    public void SweepLegInLookbackAndWatchTheSwingSupply()
    {
        List<AlfonsoBar> bars = Load("full-xauusd-h4.csv");

        TestContext.Out.WriteLine($"=== leg-in lookback sweep, {bars.Count:N0} H4 bars ===");
        TestContext.Out.WriteLine(
            $"{"lookback",10}{"zones",8}{"swings",8}{"swing%",9}{"tlBreaks",10}{"tradeable",11}");

        foreach (int lookback in (int[])[2, 3, 5, 8, 12, 20])
        {
            AlfonsoTimeframeAnalyzer analyzer = new(
                TimeSpan.FromHours(4), new ImbalanceOptions { LegInLookbackCandles = lookback });

            List<Imbalance> created = [];
            foreach (AlfonsoBar bar in bars)
                created.AddRange(analyzer.Apply(bar).Created);

            int swings = created.Count(z => !z.IsContinuationPattern);
            TestContext.Out.WriteLine(
                $"{lookback,10}{created.Count,8}{swings,8}" +
                $"{(created.Count == 0 ? 0 : swings / (double)created.Count),9:P1}" +
                $"{created.Count(z => z.Accomplished.HasFlag(Accomplishment.TrendlineBreak)),10}" +
                $"{created.Count(z => z.MeetsTradeabilityCriteria),11}");
        }
    }

    private static void Percentiles(string label, IEnumerable<decimal> values)
    {
        decimal[] sorted = values.OrderBy(value => value).ToArray();
        decimal At(double q) => sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
        TestContext.Out.WriteLine(
            $"  {label,-13} p10 {At(0.10):F2}  p50 {At(0.50):F2}  p90 {At(0.90):F2}  max {sorted[^1]:F2}");
    }
}
