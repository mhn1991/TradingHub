using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Observes the actual agent feed, not a re-bucketed historical replay. Three independent shadow
/// analyzers receive exactly the bar just applied to the live baseline. They never place orders or
/// mutate the baseline. Each shadow has its own zone detector as trendline breaks affect zones.
/// </summary>
public sealed class AlfonsoTrendAudit
{
    private readonly AlfonsoStrategyOptions _options;
    private readonly Action<AlfonsoTrendAuditRow> _sink;
    private readonly Dictionary<(string Instrument, SequenceRole Role), AlfonsoTimeframeAnalyzer[]> _shadows = [];

    public AlfonsoTrendAudit(AlfonsoStrategyOptions options, Action<AlfonsoTrendAuditRow> sink)
    {
        ValidateBaseline(options);
        _options = options;
        _sink = sink;
    }

    private static void ValidateBaseline(AlfonsoStrategyOptions options)
    {
        if (options.Trend.InvalidateOnPriceStructureBreak || options.Trend.RequireConfirmedTrendStructure)
            throw new ArgumentException("Trend audit requires both trend fixes disabled on the live baseline.", nameof(options));
    }

    // The caller supplies each new closed bar once, immediately after the live analyzer applies it.
    public void Observe(string instrument, DateTimeOffset availableAt, SequenceRole role,
        AlfonsoBar bar, AlfonsoTimeframeAnalyzer baseline)
    {
        lock (_shadows)
        {
            if (!_shadows.TryGetValue((instrument, role), out AlfonsoTimeframeAnalyzer[]? shadows))
            {
                shadows = [Create(true, false), Create(false, true), Create(true, true)];
                _shadows.Add((instrument, role), shadows);
            }
            foreach (AlfonsoTimeframeAnalyzer shadow in shadows)
                shadow.Apply(bar);
            _sink(new(instrument, (int)baseline.Interval.TotalMinutes, bar.OpenTime, availableAt,
                bar.Open, bar.High, bar.Low, bar.Close,
                [State(baseline), .. shadows.Select(State)]));

            AlfonsoTimeframeAnalyzer Create(bool invalidate, bool confirm) => new(
                baseline.Interval, _options.Zones,
                _options.Trend with { InvalidateOnPriceStructureBreak = invalidate, RequireConfirmedTrendStructure = confirm },
                _options.Range);
        }
    }

    private static AlfonsoTrendAuditState State(AlfonsoTimeframeAnalyzer analyzer) => new(
        analyzer.Trend.Trend.ToString(), analyzer.Trend.Reason,
        analyzer.Trend.OpposingEliminations, analyzer.Trend.IsOverExtended);

    public static Action<string, DateTimeOffset, SequenceRole, AlfonsoBar, AlfonsoTimeframeAnalyzer> ToFile(
        string path, AlfonsoStrategyOptions options)
    {
        // Validate before creating the output. One audit file per agent/run; refuse accidental overwrite.
        ValidateBaseline(options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        StreamWriter writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Dispose();
        int count = 0;
        AlfonsoTrendAudit audit = new(options, Write);
        return audit.Observe;

        void Write(AlfonsoTrendAuditRow row)
        {
            writer.WriteLine(JsonSerializer.Serialize(row, AlfonsoTrendAuditJsonContext.Default.AlfonsoTrendAuditRow));
            if (++count % 64 == 0) writer.Flush();
        }
    }
}

/// <summary>States are ordered: baseline, price invalidation, confirmed structure, both.</summary>
public sealed record AlfonsoTrendAuditRow(string Instrument, int IntervalMinutes,
    DateTimeOffset OpenTime, DateTimeOffset AvailableAt,
    decimal Open, decimal High, decimal Low, decimal Close, AlfonsoTrendAuditState[] States);

public sealed record AlfonsoTrendAuditState(string Trend, string Reason, int OpposingEliminations, bool IsOverExtended);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AlfonsoTrendAuditRow))]
internal partial class AlfonsoTrendAuditJsonContext : JsonSerializerContext;
