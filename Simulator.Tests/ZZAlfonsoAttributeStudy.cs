using System.Globalization;
using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Does the course's own scoring predict anything?
/// <para>
/// Module 7 is a hand-written scoring model: departure strength, a 2:1 impulse, freshness, the
/// accomplishment, basing quality. Every one of those is recorded on each zone, and none of them has
/// ever been checked against what the trade actually did. That check needs no model and settles the
/// question a model would be asked: if no attribute separates outcomes, there is nothing for one to
/// learn, and only the geometry can move the result.
/// </para>
/// </summary>
[TestFixture]
[Explicit("study over real cached candles")]
public sealed class ZZAlfonsoAttributeStudy
{
    private const decimal RoundTripBasisPoints = 2.4m;
    private const decimal Padding = 0.25m;

    private static string Dir =>
        Environment.GetEnvironmentVariable("ALFONSO_DATA") ?? "/mnt/storage/scratch/alfonso";

    internal sealed record Entry
    {
        public required DateTimeOffset At { get; init; }
        public required decimal R { get; init; }
        public required bool Won { get; init; }
        public required Imbalance Zone { get; init; }
        public required SequenceRole Timeframe { get; init; }
        public required bool Nested { get; init; }
        public required decimal? RangePosition { get; init; }
    }

    private static List<AlfonsoBar> Load(string name)
    {
        string path = Path.Combine(Dir, name);
        if (!File.Exists(path))
            Assert.Ignore($"missing {path}");

        List<AlfonsoBar> bars = [];
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] p = line.Split(',');
            if (p.Length < 5) continue;
            bars.Add(new AlfonsoBar(
                DateTimeOffset.Parse(p[0], CultureInfo.InvariantCulture),
                decimal.Parse(p[1], CultureInfo.InvariantCulture),
                decimal.Parse(p[2], CultureInfo.InvariantCulture),
                decimal.Parse(p[3], CultureInfo.InvariantCulture),
                decimal.Parse(p[4], CultureInfo.InvariantCulture)));
        }

        return bars;
    }

    /// <summary>Collects every entry the agent would take, with the attributes it had at the time.</summary>
    internal static List<Entry> Collect(
        string prefix, decimal reward = 3m, ImbalanceOptions? zoneOptions = null)
    {
        List<AlfonsoBar> h4 = Load($"{prefix}-h4.csv");
        List<AlfonsoBar> h1 = Load($"{prefix}-h1.csv");
        List<AlfonsoBar> m15 = Load($"{prefix}-m15.csv");

        AlfonsoSequenceAnalyzer analyzer = new(TimeframeSequence.Scalping, zoneOptions);

        List<(DateTimeOffset Closes, SequenceRole Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar b in h4) stream.Add((b.OpenTime.AddHours(4), SequenceRole.Top, b));
        foreach (AlfonsoBar b in h1) stream.Add((b.OpenTime.AddHours(1), SequenceRole.Middle, b));
        foreach (AlfonsoBar b in m15) stream.Add((b.OpenTime.AddMinutes(15), SequenceRole.Lower, b));
        stream.Sort((l, r) => l.Closes.CompareTo(r.Closes));

        List<(int Bar, bool Long, Imbalance Zone, SequenceRole Tf, bool Nested, decimal? Pos)> taken = [];
        HashSet<DateTimeOffset> seen = [];
        int index = -1;

        foreach ((_, SequenceRole role, AlfonsoBar bar) in stream)
        {
            analyzer.Apply(role, bar);
            if (role != SequenceRole.Lower) continue;
            index++;

            foreach (TradeCandidate candidate in analyzer.Candidates(bar.Close))
            {
                Imbalance zone = candidate.Zone;
                bool arrived = candidate.Side == ImbalanceKind.Demand
                    ? bar.Low <= zone.Proximal
                    : bar.High >= zone.Proximal;
                if (!arrived || zone.State != ImbalanceState.Fresh || !seen.Add(zone.BaseEnd))
                    continue;

                taken.Add((index, candidate.Side == ImbalanceKind.Demand, zone,
                    candidate.EntryTimeframe, candidate.Host is not null,
                    analyzer[SequenceRole.Top].Range.Position));
                break;
            }
        }

        List<Entry> entries = [];
        foreach ((int at, bool isLong, Imbalance zone, SequenceRole tf, bool nested, decimal? pos) in taken)
        {
            decimal entry = zone.Proximal;
            decimal stop = zone.StopPrice(Padding);
            decimal risk = Math.Abs(entry - stop);
            if (risk <= 0m) continue;

            decimal costR = entry * RoundTripBasisPoints / 10_000m / risk;
            decimal target = isLong ? entry + (risk * reward) : entry - (risk * reward);

            for (int i = at + 1; i < m15.Count; i++)
            {
                AlfonsoBar bar = m15[i];
                bool hitStop = isLong ? bar.Low <= stop : bar.High >= stop;
                bool hitTarget = isLong ? bar.High >= target : bar.Low <= target;
                if (!hitStop && !hitTarget) continue;

                // Stop first when a bar spans both: with OHLC the order is unknowable, and
                // assuming the target would flatter every distant-target row.
                entries.Add(new Entry
                {
                    At = bar.OpenTime,
                    R = hitStop ? -1m - costR : reward - costR,
                    Won = !hitStop,
                    Zone = zone,
                    Timeframe = tf,
                    Nested = nested,
                    RangePosition = pos
                });
                break;
            }
        }

        return entries;
    }

    private static void Bucket(string label, IEnumerable<IGrouping<string, Entry>> groups)
    {
        TestContext.Out.WriteLine($"\n{label}");
        TestContext.Out.WriteLine($"  {"value",-22}{"n",5}{"win%",9}{"avgR",10}{"netR",10}");
        foreach (IGrouping<string, Entry> group in groups.OrderBy(g => g.Key))
        {
            List<Entry> rows = group.ToList();
            if (rows.Count == 0) continue;
            TestContext.Out.WriteLine(
                $"  {group.Key,-22}{rows.Count,5}" +
                $"{rows.Count(r => r.Won) / (double)rows.Count,9:P1}" +
                $"{rows.Average(r => r.R),10:F4}{rows.Sum(r => r.R),10:F2}");
        }
    }

    private static double Spearman(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        static double[] Ranks(IReadOnlyList<double> v)
        {
            int[] order = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            double[] ranks = new double[v.Count];
            int i = 0;
            while (i < order.Length)
            {
                int j = i;
                while (j + 1 < order.Length && v[order[j + 1]] == v[order[i]]) j++;
                double shared = (i + j) / 2.0 + 1.0;
                for (int k = i; k <= j; k++) ranks[order[k]] = shared;
                i = j + 1;
            }
            return ranks;
        }

        if (xs.Count < 3) return 0;
        double[] rx = Ranks(xs), ry = Ranks(ys);
        double mx = rx.Average(), my = ry.Average();
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            double a = rx[i] - mx, b = ry[i] - my;
            num += a * b; dx += a * a; dy += b * b;
        }
        return dx <= 0 || dy <= 0 ? 0 : num / Math.Sqrt(dx * dy);
    }

    [TestCase("full-xauusd")]
    [TestCase("xauusd")]
    public void DoTheCoursesOwnScoringAttributesPredictOutcome(string prefix)
    {
        List<Entry> all = Collect(prefix);
        Assert.That(all, Is.Not.Empty);

        TestContext.Out.WriteLine(
            $"=== {prefix}: {all.Count} entries, {all[0].At:yyyy-MM-dd} -> {all[^1].At:yyyy-MM-dd} ===");
        TestContext.Out.WriteLine(
            $"overall  win {all.Count(e => e.Won) / (double)all.Count:P1}  " +
            $"avgR {all.Average(e => e.R):+0.0000}  netR {all.Sum(e => e.R):+0.00}");

        Bucket("departure strength (module 7's headline odds enhancer)",
            all.GroupBy(e => e.Zone.Strength.ToString()));
        Bucket("accomplishment",
            all.GroupBy(e => e.Zone.Accomplished == Accomplishment.None
                ? "None"
                : e.Zone.Accomplished.ToString()));
        Bucket("swing vs continuation pattern",
            all.GroupBy(e => e.Zone.IsContinuationPattern ? "continuation" : "swing"));
        Bucket("nested in a higher timeframe zone",
            all.GroupBy(e => e.Nested ? "nested" : "standalone"));
        Bucket("entry timeframe", all.GroupBy(e => e.Timeframe.ToString()));
        Bucket("base candle count", all.GroupBy(e => $"{e.Zone.BaseCandleCount} candles"));
        Bucket("impulse:base ratio",
            all.GroupBy(e => e.Zone.ImpulseToBaseRatio switch
            {
                < 2m => "a <2.0 (sub-2:1)",
                < 3m => "b 2.0-3.0",
                < 5m => "c 3.0-5.0",
                _ => "d >=5.0"
            }));
        Bucket("top-timeframe range position",
            all.GroupBy(e => e.RangePosition switch
            {
                null => "unavailable",
                < 0.2m => "a bottom 20%",
                < 0.5m => "b 20-50%",
                < 0.8m => "c 50-80%",
                _ => "d top 20%"
            }));

        TestContext.Out.WriteLine("\ncontinuous attributes, Spearman against realised R:");
        foreach ((string name, Func<Entry, double> f) in new (string, Func<Entry, double>)[]
        {
            ("impulse:base ratio", e => (double)e.Zone.ImpulseToBaseRatio),
            ("impulse displacement", e => (double)e.Zone.ImpulseDisplacement),
            ("zone width", e => (double)e.Zone.Width),
            ("base candle count", e => e.Zone.BaseCandleCount),
            ("impulse bars", e => e.Zone.ImpulseBarsTracked)
        })
        {
            double rho = Spearman(all.Select(f).ToList(), all.Select(e => (double)e.R).ToList());
            TestContext.Out.WriteLine($"  {name,-24} rho {rho,8:F4}");
        }

        // Out-of-sample discipline: split by time and see whether anything found in the first half
        // still holds in the second. An attribute that only separates in-sample separated nothing.
        int half = all.Count / 2;
        List<Entry> first = all.Take(half).ToList(), second = all.Skip(half).ToList();
        TestContext.Out.WriteLine(
            $"\nchronological split  first {first.Count} ({first[0].At:yyyy-MM} -> {first[^1].At:yyyy-MM})" +
            $"  second {second.Count} ({second[0].At:yyyy-MM} -> {second[^1].At:yyyy-MM})");
        TestContext.Out.WriteLine($"  {"attribute",-30}{"first avgR",14}{"second avgR",14}{"agrees",9}");
        foreach ((string name, Func<Entry, bool> pick) in new (string, Func<Entry, bool>)[]
        {
            ("Strong or Gap departure", e => e.Zone.Strength != ImpulseStrength.Weak),
            ("swing (not continuation)", e => !e.Zone.IsContinuationPattern),
            ("nested", e => e.Nested),
            ("impulse:base >= 3", e => e.Zone.ImpulseToBaseRatio >= 3m),
            ("has an accomplishment", e => e.Zone.Accomplished != Accomplishment.None)
        })
        {
            List<Entry> a = first.Where(pick).ToList(), b = second.Where(pick).ToList();
            if (a.Count == 0 || b.Count == 0) continue;
            decimal ea = a.Average(e => e.R) - first.Average(e => e.R);
            decimal eb = b.Average(e => e.R) - second.Average(e => e.R);
            TestContext.Out.WriteLine(
                $"  {name,-30}{ea,14:+0.0000;-0.0000}{eb,14:+0.0000;-0.0000}" +
                $"{(Math.Sign(ea) == Math.Sign(eb) ? "yes" : "NO"),9}");
        }
    }
}
