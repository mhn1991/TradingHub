using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Snapshots the live zone population per timeframe as the run proceeds.
/// <para>
/// The candidate log cannot answer how the inventory is composed: it only ever contains the side the
/// prevailing scenario permits, so demand and supply are never observable at the same instant. This
/// exists because fill rate turned out to be a steep function of distance from price - 21-35% inside
/// 3 ATR, under 1.5% beyond 6, zero beyond 16 - so what the agent can actually trade is decided by
/// where its zones sit, not by how it enters at them.
/// </para>
/// </summary>
public static class AlfonsoInventoryLog
{
    private static readonly ConcurrentDictionary<string, Writer> Writers = new(StringComparer.Ordinal);

    static AlfonsoInventoryLog() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();

    public static Action<AlfonsoInventorySnapshot> ToFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Writers.GetOrAdd(Path.GetFullPath(path), full => new Writer(full)).Write;
    }

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
            "at,instrument,role,kind,liveZones,reachable,medianDistanceAtr,nearestDistanceAtr,price,atr," +
            "scenarioBlocked,rangeBlocked,controlBlocked,overExtended,notTradeable,noHost," +
            "notAccepted,notLive,noRoom,passed," +
            "barNear,barFar,stateNear,stateFar,pendingNear,pendingFar,passedNear";

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

        internal void Write(AlfonsoInventorySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            string line = string.Join(',',
                snapshot.At.ToString("O", CultureInfo.InvariantCulture),
                Quote(snapshot.Instrument.ToString()),
                Quote(snapshot.Role.ToString()),
                Quote(snapshot.Kind.ToString()),
                snapshot.LiveZones.ToString(CultureInfo.InvariantCulture),
                snapshot.Reachable.ToString(CultureInfo.InvariantCulture),
                Number(snapshot.MedianDistanceAtr),
                Number(snapshot.NearestDistanceAtr),
                Number(snapshot.Price),
                Number(snapshot.Atr),
                Count(snapshot.Filters?.ScenarioBlocked),
                Count(snapshot.Filters?.RangeBlocked),
                Count(snapshot.Filters?.ControlBlocked),
                Count(snapshot.Filters?.OverExtended),
                Count(snapshot.Filters?.NotTradeable),
                Count(snapshot.Filters?.NoHost),
                Count(snapshot.Filters?.NotAccepted),
                Count(snapshot.Filters?.NotLive),
                Count(snapshot.Filters?.NoRoom),
                Count(snapshot.Filters?.Passed),
                Count(snapshot.Filters?.BarNear),
                Count(snapshot.Filters?.BarFar),
                Count(snapshot.Filters?.StateNear),
                Count(snapshot.Filters?.StateFar),
                Count(snapshot.Filters?.PendingNear),
                Count(snapshot.Filters?.PendingFar),
                Count(snapshot.Filters?.PassedNear));

            lock (_gate)
            {
                _writer.WriteLine(line);
            }
        }

        private static string Count(long? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        private static string Number(decimal? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

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
