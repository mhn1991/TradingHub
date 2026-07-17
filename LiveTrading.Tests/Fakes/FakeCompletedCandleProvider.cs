using Brokers.Models;
using LiveTrading.MarketData;

namespace LiveTrading.Tests.Fakes;

/// <summary>Deterministic, in-memory <see cref="ICompletedCandleProvider"/> test double - no
/// real network access exists in this environment, so this drives every non-Explicit market-data
/// test in this project.</summary>
public sealed class FakeCompletedCandleProvider : ICompletedCandleProvider
{
    private readonly Dictionary<(InstrumentKey Instrument, BarInterval Interval), List<Candle>> _candles = [];
    private readonly Queue<Exception> _pendingFailures = new();
    public int CallCount { get; private set; }

    public void Seed(InstrumentKey instrument, BarInterval interval, IEnumerable<Candle> candles)
    {
        var key = (instrument, interval);
        if (!_candles.TryGetValue(key, out List<Candle>? list))
        {
            list = [];
            _candles[key] = list;
        }

        list.AddRange(candles);
    }

    public void EnqueueFailure(Exception exception) => _pendingFailures.Enqueue(exception);

    public Task<IReadOnlyList<Candle>> GetCompletedCandlesAsync(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset fromExclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (_pendingFailures.Count > 0)
        {
            throw _pendingFailures.Dequeue();
        }

        if (!_candles.TryGetValue((instrument, interval), out List<Candle>? list))
        {
            return Task.FromResult<IReadOnlyList<Candle>>([]);
        }

        IReadOnlyList<Candle> result = list
            .Where(c => c.OpenTime > fromExclusive && c.OpenTime <= toInclusive)
            .OrderBy(c => c.OpenTime)
            .ToList();
        return Task.FromResult(result);
    }
}
