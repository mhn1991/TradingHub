using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Abstractions;

namespace Simulator.MarketData;

/// <summary>Pages OANDA history in deterministic chronological chunks. Only complete candles are emitted.</summary>
public sealed class OandaHistoricalCandleSource : IHistoricalCandleSource
{
    private readonly IMarketDataClient _marketData;
    private readonly InstrumentKey _instrument;
    private readonly BarInterval _interval;
    private readonly DateTimeOffset _from;
    private readonly DateTimeOffset _to;
    private readonly int _pageSize;

    public OandaHistoricalCandleSource(
        IMarketDataClient marketData,
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize = 5_000)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        if (instrument.IsEmpty) throw new ArgumentException("Instrument is required.", nameof(instrument));
        if (!interval.IsValid) throw new ArgumentException("Interval is invalid.", nameof(interval));
        if (from >= to) throw new ArgumentException("From must be earlier than To.");
        if (pageSize is < 1 or > 5_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _instrument = instrument;
        _interval = interval;
        _from = from;
        _to = to;
        _pageSize = pageSize;
    }

    public async IAsyncEnumerable<Candle> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursor = _from;
        DateTimeOffset intervalEnd(DateTimeOffset value) => _interval.AddTo(value);
        while (cursor < _to)
        {
            DateTimeOffset pageTo = cursor;
            for (int i = 0; i < _pageSize && pageTo < _to; i++) pageTo = intervalEnd(pageTo);
            if (pageTo > _to) pageTo = _to;

            IReadOnlyList<Candle> page = await _marketData.GetCandlesAsync(
                new CandleQuery(_instrument, _interval, _pageSize, cursor, pageTo),
                cancellationToken).ConfigureAwait(false);

            Candle[] complete = page.Where(c => c.IsComplete && c.OpenTime >= cursor && c.OpenTime < pageTo && c.OpenTime < _to)
                .OrderBy(c => c.OpenTime).ToArray();
            if (complete.Length == 0)
            {
                cursor = pageTo;
                continue;
            }

            DateTimeOffset? last = null;
            foreach (Candle candle in complete)
            {
                if (last is not null && candle.OpenTime <= last) continue;
                last = candle.OpenTime;
                yield return candle;
            }

            DateTimeOffset next = intervalEnd(complete[^1].OpenTime);
            cursor = next > cursor ? next : pageTo;
        }
    }
}
