using System.Globalization;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Does module 7's grading order the zones the hard gates currently reject?
/// <para>
/// §3.50 measured the grade on the population that reaches the candidate list and found it almost
/// constant: nothing below 7/10, nothing graded Weak. That is not evidence the grading is useless,
/// only that it is computed after the gates have already made the population uniform - an
/// accomplishment is required, 2:1 is required, a weak departure is refused, and freshness has
/// already selected. This harness removes that censoring by ignoring the gates entirely and
/// evaluating EVERY structure the detector builds, then asking whether the score separates outcomes
/// across the full range.
/// </para>
/// <para>
/// It is a zone-quality study, not a strategy backtest. There is no single order slot, no scenario
/// filter on selection and no position limit: every zone that price reaches is one observation, so
/// the sample is not conditioned on what the agent happened to be able to trade. The scenario's
/// verdict is recorded per row so it can be cut on afterwards rather than baked in.
/// </para>
/// <para>
/// Causality: bars are merged by CLOSE time and applied in that order (the pattern §3.47 verified),
/// zones are snapshotted BEFORE the bar that touches them is applied, and outcomes are resolved
/// only from bars after the touch. The known caveat on this data is §3.30's: `export_bars.py`
/// writes an incomplete bucket as if whole. That understates bar quality uniformly across every
/// bucket compared here, so it cannot manufacture a difference between them.
/// </para>
/// </summary>
[TestFixture]
[Explicit("study over real cached candles")]
public sealed class ZZAlfonsoGradeStudy
{
    private const decimal RoundTripBasisPoints = 2.4m;
    private const decimal Padding = 0.25m;
    private const decimal Reward = 3m;

    private static string Dir =>
        Environment.GetEnvironmentVariable("ALFONSO_DATA") ?? "/mnt/storage/scratch/alfonso";

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

    /// <summary>One zone, at the moment price first reached it, with what happened next.</summary>
    private sealed record Touch
    {
        public required int BarIndex { get; init; }
        public required Imbalance Zone { get; init; }
        public required SequenceRole Role { get; init; }
        public required bool ScenarioPermitted { get; init; }
        public required bool WouldHaveBeenOffered { get; init; }
    }

    [TestCase("full-xauusd")]
    [TestCase("gold")]
    [TestCase("silver")]
    [TestCase("eurusd")]
    [TestCase("gbpjpy")]
    [TestCase("nas100")]
    [TestCase("us30")]
    public void DoesTheGradeOrderTheZonesTheGatesReject(string prefix)
    {
        List<AlfonsoBar> h4 = Load($"{prefix}-h4.csv");
        List<AlfonsoBar> h1 = Load($"{prefix}-h1.csv");
        List<AlfonsoBar> m15 = Load($"{prefix}-m15.csv");

        ImbalanceOptions zoneOptions = new();
        AlfonsoSequenceAnalyzer analyzer = new(TimeframeSequence.Scalping, zoneOptions);

        List<(DateTimeOffset Closes, SequenceRole Role, AlfonsoBar Bar)> stream = [];
        foreach (AlfonsoBar b in h4) stream.Add((b.OpenTime.AddHours(4), SequenceRole.Top, b));
        foreach (AlfonsoBar b in h1) stream.Add((b.OpenTime.AddHours(1), SequenceRole.Middle, b));
        foreach (AlfonsoBar b in m15) stream.Add((b.OpenTime.AddMinutes(15), SequenceRole.Lower, b));
        stream.Sort((l, r) => l.Closes.CompareTo(r.Closes));

        List<Touch> touches = [];
        HashSet<(SequenceRole, DateTimeOffset, ImbalanceKind)> seen = [];
        int index = -1;

        foreach ((_, SequenceRole role, AlfonsoBar bar) in stream)
        {
            if (role != SequenceRole.Lower)
            {
                analyzer.Apply(role, bar);
                continue;
            }

            index++;

            // Snapshot BEFORE applying. A zone that this bar both reaches and eliminates is gone
            // from the live list afterwards - and those are exactly the losses, so reading the list
            // after the bar would drop them and flatter every bucket.
            ScenarioResolution scenario = analyzer.Scenario;
            List<(SequenceRole Role, Imbalance Zone)> live = [];
            foreach ((SequenceRole r, _) in analyzer.Sequence.All())
            {
                foreach (Imbalance zone in analyzer.ZonesOf(r))
                    live.Add((r, zone));
            }

            IReadOnlyList<TradeCandidate> offered = analyzer.Candidates(bar.Close);
            HashSet<DateTimeOffset> offeredKeys = [.. offered.Select(c => c.Zone.BaseEnd)];

            analyzer.Apply(role, bar);

            foreach ((SequenceRole r, Imbalance zone) in live)
            {
                bool reached = zone.Kind == ImbalanceKind.Demand
                    ? bar.Low <= zone.Proximal
                    : bar.High >= zone.Proximal;
                if (!reached)
                    continue;

                if (!seen.Add((r, zone.BaseEnd, zone.Kind)))
                    continue;

                touches.Add(new Touch
                {
                    BarIndex = index,
                    Zone = zone,
                    Role = r,
                    ScenarioPermitted = scenario.CanTrade && scenario.Side == zone.Kind,
                    WouldHaveBeenOffered = offeredKeys.Contains(zone.BaseEnd)
                });
            }
        }

        string outPath = Path.Combine(Dir, $"gradestudy-{prefix}.csv");
        using StreamWriter writer = new(outPath, append: false);
        writer.WriteLine(
            "at,role,side,proximal,distal,stop,target,risk,r,won,resolved," +
            "scoreTotal,grade,pointsAccomplishment,pointsImpulse,pointsBase,pointsFreshness,pointsRR," +
            "strength,accomplishmentCount,accomplished,impulseToBaseRatio,baseCandles," +
            "continuation,meetsTradeability,scenarioPermitted,offered");

        int resolvedCount = 0;
        foreach (Touch touch in touches)
        {
            Imbalance zone = touch.Zone;
            bool isLong = zone.Kind == ImbalanceKind.Demand;
            decimal entry = zone.Proximal;
            decimal stop = zone.StopPrice(Padding);
            decimal risk = Math.Abs(entry - stop);
            if (risk <= 0m)
                continue;

            decimal costR = entry * RoundTripBasisPoints / 10_000m / risk;
            decimal target = isLong ? entry + (risk * Reward) : entry - (risk * Reward);

            decimal? r = null;
            bool won = false;

            // The touching bar counts: the limit fills inside it, so the rest of that same bar can
            // take the stop. Ignoring it would drop the fastest losses.
            for (int i = touch.BarIndex; i < m15.Count; i++)
            {
                AlfonsoBar bar = m15[i];
                bool hitStop = isLong ? bar.Low <= stop : bar.High >= stop;
                bool hitTarget = isLong ? bar.High >= target : bar.Low <= target;
                if (!hitStop && !hitTarget)
                    continue;

                // Stop first when one bar spans both: the intrabar order is unknowable from OHLC,
                // and assuming the target would flatter every wide-target row.
                won = !hitStop;
                r = hitStop ? -1m - costR : Reward - costR;
                break;
            }

            if (r is not null)
                resolvedCount++;

            ZoneScore score = ZoneScorer.Score(zone, zoneOptions);
            int accomplishments = Enum.GetValues<Accomplishment>()
                .Count(f => f != Accomplishment.None && zone.Accomplished.HasFlag(f));

            writer.WriteLine(string.Join(',',
                m15[touch.BarIndex].OpenTime.ToString("O", CultureInfo.InvariantCulture),
                touch.Role,
                zone.Kind,
                zone.Proximal.ToString(CultureInfo.InvariantCulture),
                zone.Distal.ToString(CultureInfo.InvariantCulture),
                stop.ToString(CultureInfo.InvariantCulture),
                target.ToString(CultureInfo.InvariantCulture),
                risk.ToString(CultureInfo.InvariantCulture),
                r?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                won,
                r is not null,
                score.Total,
                score.Grade,
                score.Accomplishment,
                score.Impulse,
                score.BaseStructure,
                score.Freshness,
                score.RewardRisk,
                zone.Strength,
                accomplishments,
                $"\"{zone.Accomplished}\"",
                zone.ImpulseToBaseRatio.ToString(CultureInfo.InvariantCulture),
                zone.BaseCandleCount,
                zone.IsContinuationPattern,
                zone.MeetsTradeabilityCriteria,
                touch.ScenarioPermitted,
                touch.WouldHaveBeenOffered));
        }

        TestContext.Out.WriteLine(
            $"{prefix}: {touches.Count} zone touches, {resolvedCount} resolved -> {outPath}");
        Assert.That(touches, Is.Not.Empty);
    }
}
