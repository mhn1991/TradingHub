using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Writes <see cref="AlfonsoCandidateRecord"/> rows to a CSV as the agent decides.
/// <para>
/// This exists because measuring the strategy by replaying candles through the analyzer classes
/// outside the simulator turned out not to reproduce the agent: on EUR/USD the agent took 14 trades
/// whose scenario says all three timeframes agreed, while a standalone replay of the same window
/// found one such bar in 16,420. Same classes, different bars - the replay's aggregation has no gap
/// tolerance and no incomplete-bucket rule. So the only trustworthy place to read decision-time
/// state is inside the run.
/// </para>
/// </summary>
public static class AlfonsoCandidateLog
{
    private static readonly ConcurrentDictionary<string, Writer> Writers = new(StringComparer.Ordinal);

    static AlfonsoCandidateLog()
    {
        // Buffered writes with no explicit close would leave the tail of the run unwritten, which
        // on a diagnostic is worse than not logging at all - the file looks complete and is not.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();
    }

    /// <summary>
    /// A sink appending to <paramref name="path"/>. Repeated calls for the same path share one
    /// writer, so the several agent instances a run may build cannot interleave partial lines.
    /// </summary>
    public static Action<AlfonsoCandidateRecord> ToFile(string path)
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
        private const string Header =
            "at,instrument,outcome,side,entryTimeframe,topTrend,middleTrend,lowerTrend," +
            "proximal,distal,stop,target,risk,marketPrice,nested,continuationPattern,state," +
            "strength,accomplished,impulseToBaseRatio,impulseDisplacement,baseCandles," +
            "costToRisk,stopAtrMultiple,atrPercentile,rangePosition,scenario";

        private readonly Lock _gate = new();
        private readonly StreamWriter _writer;

        internal Writer(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = false };
            _writer.WriteLine(Header);
        }

        internal void Write(AlfonsoCandidateRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            string line = string.Join(',',
                record.At.ToString("O", CultureInfo.InvariantCulture),
                record.Instrument.ToString(),
                record.Outcome,
                record.Side,
                record.EntryTimeframe,
                Text(record.TopTrend),
                Text(record.MiddleTrend),
                Text(record.LowerTrend),
                Number(record.Proximal),
                Number(record.Distal),
                Number(record.Stop),
                Number(record.Target),
                Number(record.Risk),
                Number(record.MarketPrice),
                record.Nested,
                record.IsContinuationPattern,
                record.State,
                record.Strength,
                record.Accomplished,
                Number(record.ImpulseToBaseRatio),
                Number(record.ImpulseDisplacement),
                record.BaseCandleCount.ToString(CultureInfo.InvariantCulture),
                Number(record.CostToRisk),
                Number(record.StopAtrMultiple),
                Number(record.AtrPercentile),
                Number(record.RangePosition),
                Quote(record.Scenario));

            lock (_gate)
            {
                _writer.WriteLine(line);
            }
        }

        private static string Text(object? value) => value?.ToString() ?? string.Empty;

        private static string Number(decimal? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Scenario text contains commas, so it is the one field that needs quoting.</summary>
        private static string Quote(string value) =>
            $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Flush();
                _writer.Dispose();
            }
        }
    }
}
