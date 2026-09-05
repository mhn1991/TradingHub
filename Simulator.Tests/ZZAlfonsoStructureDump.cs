using System.Globalization;
using System.Text;
using System.Text.Json;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Dumps what the trend layer actually saw at each trade's decision moment, so the trade page can
/// draw the agent's OWN trendlines and imbalances rather than a reconstruction of them.
/// <para>
/// The trendlines are not in the run output - `AlfonsoTrendSnapshot.Line` lives on the analyzer and
/// is never serialised - so they are recovered by replaying the production classes over the same
/// candles, the pattern <see cref="ZZAlfonsoTrendLayerDiagnostic"/> established. Each timeframe is
/// fed only its own closed bars, exactly as the agent feeds them, or the layer stops being able to
/// disagree with the timeframe above it.
/// </para>
/// <para>
/// Input: a JSON array of {inst, at} decision points (ALFONSO_POINTS, default points.json in the
/// data directory). Output: JSONL, one row per decision point per timeframe (ALFONSO_STRUCTURE).
/// </para>
/// </summary>
[TestFixture]
[Explicit("data export over real cached candles, not a correctness test")]
public sealed class ZZAlfonsoStructureDump
{
    private static string DataDirectory =>
        Environment.GetEnvironmentVariable("ALFONSO_DATA") ?? "/mnt/storage/scratch/alfonso";

    private static readonly (string Suffix, int Minutes)[] Timeframes =
        [("m15", 15), ("h1", 60), ("h4", 240)];

    private sealed record Point(string Inst, DateTimeOffset At);

    private static List<AlfonsoBar> Load(string name)
    {
        string path = Path.Combine(DataDirectory, name);
        if (!File.Exists(path))
            return [];

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

        bars.Sort((left, right) => left.OpenTime.CompareTo(right.OpenTime));
        return bars;
    }

    [Test]
    public void DumpStructureAtDecisionPoints()
    {
        string pointsPath = Environment.GetEnvironmentVariable("ALFONSO_POINTS")
            ?? Path.Combine(DataDirectory, "points.json");
        string outPath = Environment.GetEnvironmentVariable("ALFONSO_STRUCTURE")
            ?? Path.Combine(DataDirectory, "structure.jsonl");

        if (!File.Exists(pointsPath))
            Assert.Ignore($"No decision points at {pointsPath}.");

        List<Point> points = JsonSerializer.Deserialize<List<Point>>(
            File.ReadAllText(pointsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        StringBuilder output = new();
        int rows = 0;

        foreach (IGrouping<string, Point> group in points.GroupBy(p => p.Inst))
        {
            foreach ((string suffix, int minutes) in Timeframes)
            {
                List<AlfonsoBar> bars = Load($"{group.Key}-{suffix}.csv");
                if (bars.Count == 0)
                    continue;

                TimeSpan interval = TimeSpan.FromMinutes(minutes);
                AlfonsoTimeframeAnalyzer analyzer = new(interval);

                // Decision points in ascending order; a bar "covers" a point when the point falls at
                // or before that bar's close and after the previous one's.
                List<DateTimeOffset> wanted = group.Select(p => p.At).OrderBy(a => a).ToList();
                int next = 0;

                foreach (AlfonsoBar bar in bars)
                {
                    analyzer.Apply(bar);
                    DateTimeOffset close = bar.OpenTime + interval;

                    while (next < wanted.Count && wanted[next] <= close)
                    {
                        output.AppendLine(Row(group.Key, wanted[next], suffix, analyzer));
                        rows++;
                        next++;
                    }

                    if (next >= wanted.Count)
                        break;
                }
            }
        }

        File.WriteAllText(outPath, output.ToString());
        TestContext.Out.WriteLine($"{rows} rows -> {outPath}");
    }

    private static string Row(string inst, DateTimeOffset at, string tf, AlfonsoTimeframeAnalyzer analyzer)
    {
        AlfonsoTrendSnapshot trend = analyzer.Trend;
        Trendline? line = trend.Line;

        object? lineOut = line is null ? null : new
        {
            dir = line.Direction.ToString(),
            fromTime = line.FromTime.ToUnixTimeSeconds(),
            fromPrice = line.FromPrice,
            toTime = line.ToTime.ToUnixTimeSeconds(),
            toPrice = line.ToPrice,
            slope = line.Slope
        };

        // Live zones, nearest first - what the agent could have chosen among at that instant.
        var zones = analyzer.Zones
            .Where(z => z.State != ImbalanceState.Eliminated)
            .Take(24)
            .Select(z => new
            {
                kind = z.Kind.ToString(),
                proximal = z.Proximal,
                distal = z.Distal,
                state = z.State.ToString(),
                baseEnd = z.BaseEnd.ToUnixTimeSeconds(),
                tradeable = z.IsTradeable
            });

        return JsonSerializer.Serialize(new
        {
            inst,
            at = at.ToUnixTimeSeconds(),
            tf,
            trend = trend.Trend.ToString(),
            overExtended = trend.IsOverExtended,
            reason = trend.Reason,
            line = lineOut,
            zones
        });
    }
}
