using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Writes the trend layer's decision-time state - the trend, its live trendline and every live
/// imbalance, per timeframe - as one JSONL row per order the agent places.
/// <para>
/// This exists for the reason <see cref="AlfonsoCandidateLog"/> already records: replaying the
/// analyzer classes outside the run does not reproduce the agent, because the bars differ. Measured
/// directly (3.67), a replay over exported CSVs agreed with the run's own scenario text on 77% of
/// 4h rows, 72% of 1h and only <b>38% of 15m</b> - so a page drawing those trendlines was showing
/// the reader something the agent never saw on the timeframe that matters most. The live analyzers
/// are the only honest source.
/// </para>
/// </summary>
public static class AlfonsoStructureLog
{
    private static readonly ConcurrentDictionary<string, Writer> Writers = new(StringComparer.Ordinal);

    static AlfonsoStructureLog()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();
    }

    /// <summary>A sink appending to <paramref name="path"/>; one writer per path, shared.</summary>
    public static Action<string, DateTimeOffset, AlfonsoSequenceAnalyzer> ToFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Writer writer = Writers.GetOrAdd(Path.GetFullPath(path), full => new Writer(full));
        return writer.Write;
    }

    /// <summary>Flushes and closes every open log. Safe to call more than once.</summary>
    public static void CloseAll()
    {
        foreach (string key in Writers.Keys.ToList())
        {
            if (Writers.TryRemove(key, out Writer? writer))
                writer.Dispose();
        }
    }

    private sealed class Writer : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _out;

        public Writer(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // No BOM: this is JSONL read by tools, and a leading BOM makes the first line fail to parse.
            _out = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = false };
        }

        public void Write(string instrument, DateTimeOffset at, AlfonsoSequenceAnalyzer analyzer)
        {
            // Hand-rolled JSON rather than JsonSerializer: this assembly is AOT-analysed (see
            // Simulator.AotSmoke) and reflection-based serialisation is a build error there. The
            // shape is fixed and small, so writing it out is cheaper than a source-generated context.
            StringBuilder row = new();
            row.Append("{\"inst\":").Append(Quote(instrument))
               .Append(",\"at\":").Append(at.ToUnixTimeSeconds())
               .Append(",\"timeframes\":{");

            bool first = true;
            foreach ((SequenceRole role, _) in analyzer.Sequence.All())
            {
                if (!first)
                    row.Append(',');
                first = false;
                row.Append(Quote(role.ToString())).Append(':');
                AppendTimeframe(row, analyzer[role]);
            }

            row.Append("}}");

            lock (_gate)
            {
                _out.WriteLine(row.ToString());
                _out.Flush();
            }
        }

        private static void AppendTimeframe(StringBuilder row, AlfonsoTimeframeAnalyzer timeframe)
        {
            AlfonsoTrendSnapshot trend = timeframe.Trend;
            row.Append("{\"minutes\":").Append(Num((decimal)timeframe.Interval.TotalMinutes))
               .Append(",\"trend\":").Append(Quote(trend.Trend.ToString()))
               .Append(",\"overExtended\":").Append(trend.IsOverExtended ? "true" : "false")
               .Append(",\"reason\":").Append(Quote(trend.Reason ?? string.Empty))
               .Append(",\"line\":");

            if (trend.Line is Trendline line)
            {
                row.Append("{\"dir\":").Append(Quote(line.Direction.ToString()))
                   .Append(",\"fromTime\":").Append(line.FromTime.ToUnixTimeSeconds())
                   .Append(",\"fromPrice\":").Append(Num(line.FromPrice))
                   .Append(",\"toTime\":").Append(line.ToTime.ToUnixTimeSeconds())
                   .Append(",\"toPrice\":").Append(Num(line.ToPrice))
                   .Append(",\"slope\":").Append(Num(line.Slope))
                   .Append('}');
            }
            else
            {
                row.Append("null");
            }

            row.Append(",\"zones\":[");
            bool first = true;
            foreach (Imbalance zone in timeframe.Zones)
            {
                if (zone.State == ImbalanceState.Eliminated)
                    continue;
                if (!first)
                    row.Append(',');
                first = false;
                row.Append("{\"kind\":").Append(Quote(zone.Kind.ToString()))
                   .Append(",\"proximal\":").Append(Num(zone.Proximal))
                   .Append(",\"distal\":").Append(Num(zone.Distal))
                   .Append(",\"state\":").Append(Quote(zone.State.ToString()))
                   .Append(",\"baseEnd\":").Append(zone.BaseEnd.ToUnixTimeSeconds())
                   .Append(",\"tradeable\":").Append(zone.IsTradeable ? "true" : "false")
                   .Append('}');
            }

            row.Append("]}");
        }

        private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Quote(string value)
        {
            StringBuilder escaped = new("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            escaped.Append(c);
                        break;
                }
            }

            return escaped.Append('"').ToString();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _out.Flush();
                _out.Dispose();
            }
        }
    }
}
