using System.Runtime.CompilerServices;
using Brokers.Models;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Abstractions;

public interface IHistoricalCandleStream
{
    IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        CancellationToken cancellationToken = default);
}

public interface IHistoricalCandleStreamWithProgress : IHistoricalCandleStream
{
    event Action<CandleDownloadProgress>? ProgressChanged;
}

public sealed record CandleDownloadProgress(
    long DownloadedCandles,
    DateTimeOffset? LatestCandleOpenTime,
    string Status,
    bool FromCache,
    int PagesRead = 0);

/// <summary>Adapts the legacy mid-only <see cref="IHistoricalCandleSource"/> to the streaming envelope.</summary>
public sealed class HistoricalCandleSourceStreamAdapter(IHistoricalCandleSource source) : IHistoricalCandleStream
{
    private readonly IHistoricalCandleSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (Candle candle in _source.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (candle.OpenTime < request.From || candle.OpenTime >= request.To)
                continue;
            yield return MarketCandle.FromMid(candle);
        }
    }
}

/// <summary>In-memory stream used by tests and inline-candle jobs.</summary>
public sealed class EnumerableMarketCandleStream(IEnumerable<Candle> candles) : IHistoricalCandleStream
{
    private readonly IEnumerable<Candle> _candles = candles ?? throw new ArgumentNullException(nameof(candles));

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (Candle candle in _candles.OrderBy(item => item.OpenTime))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candle.OpenTime < request.From || candle.OpenTime >= request.To)
                continue;
            yield return MarketCandle.FromMid(candle);
            await Task.Yield();
        }
    }
}
