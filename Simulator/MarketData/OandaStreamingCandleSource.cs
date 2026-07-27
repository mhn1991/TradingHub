using System.Net;
using System.Runtime.CompilerServices;
using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.MarketData;

/// <summary>
/// Pages OANDA history and streams candles to the simulator while writing the cache.
/// First uncached run does not wait for the full year to download before yielding.
/// </summary>
public sealed class OandaStreamingCandleSource :
    IHistoricalCandleStreamWithProgress,
    IPagedHistoricalCandleSource,
    IAsyncDisposable
{
    private readonly IMarketDataClient _marketData;
    private readonly BrokerEnvironment _environment;
    private readonly string _cacheDirectory;
    private readonly IAsyncDisposable? _owner;

    public OandaStreamingCandleSource(
        IMarketDataClient marketData,
        BrokerEnvironment environment = BrokerEnvironment.Demo,
        string? cacheDirectory = null,
        IAsyncDisposable? owner = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _environment = environment;
        _cacheDirectory = cacheDirectory ?? Path.Combine(".cache", "oanda");
        _owner = owner;
    }

    public event Action<CandleDownloadProgress>? ProgressChanged;

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string cachePath = StreamingCandleCache.GetPath(
            request.CacheDirectory ?? _cacheDirectory,
            "oanda",
            _environment,
            request.Instrument,
            request.BaseInterval,
            request.From,
            request.To);

        if (!request.RefreshCache &&
            !request.NoCache &&
            await StreamingCandleCacheWriter.TryValidateManifestAsync(cachePath, request, cancellationToken)
                .ConfigureAwait(false))
        {
            RaiseProgress(request.Instrument, 0, null, "ReadingCache", fromCache: true, pages: 0);
            long count = 0;
            await foreach (MarketCandle candle in StreamingCandleCache.ReadStreamAsync(
                               cachePath,
                               request.Instrument,
                               request.BaseInterval,
                               cancellationToken).ConfigureAwait(false))
            {
                count++;
                if (count % 2_000 == 0)
                    RaiseProgress(request.Instrument, count, candle.OpenTime, "ReadingCache", fromCache: true, pages: 0);
                yield return candle;
            }

            RaiseProgress(request.Instrument, count, null, "CacheLoaded", fromCache: true, pages: 0);
            yield break;
        }

        RaiseProgress(request.Instrument, 0, null, "DownloadingData", fromCache: false, pages: 0);

        StreamingCandleCacheWriter? cacheWriter = request.NoCache
            ? null
            : new StreamingCandleCacheWriter(cachePath, request, "oanda", _environment);

        long downloaded = 0;
        int pages = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        try
        {
            await foreach (MarketCandle candle in DownloadPagesAsync(
                               request,
                               onPage: page => pages = page,
                               cancellationToken).ConfigureAwait(false))
            {
                first ??= candle.OpenTime;
                last = candle.OpenTime;
                downloaded++;

                if (cacheWriter is not null)
                    await cacheWriter.AppendAsync(candle, cancellationToken).ConfigureAwait(false);

                if (downloaded % 500 == 0)
                    RaiseProgress(request.Instrument, downloaded, candle.OpenTime, "DownloadingData", fromCache: false, pages);

                // Yield immediately — do not wait for the complete requested range.
                yield return candle;
            }

            if (cacheWriter is not null && first is not null && last is not null)
            {
                await cacheWriter.CommitAsync(
                    new StreamingCacheCommitMetadata(
                        downloaded,
                        first.Value,
                        last.Value,
                        cacheWriter.CurrentHashHex!,
                        DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }

            RaiseProgress(request.Instrument, downloaded, last, "DownloadComplete", fromCache: false, pages);
        }
        finally
        {
            // Dispose aborts an uncommitted temporary stream. This covers cancellation,
            // source failure, and consumers that stop enumeration early.
            if (cacheWriter is not null)
                await cacheWriter.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<HistoricalCandlePage> ReadPageAsync(
        HistoricalPageRequest request,
        CancellationToken cancellationToken = default)
    {
        int pageSize = Math.Clamp(request.PageSize, 1, 5_000);
        DateTimeOffset pageTo = request.Cursor;
        for (int i = 0; i < pageSize && pageTo < request.To; i++)
            pageTo = request.BaseInterval.AddTo(pageTo);
        if (pageTo > request.To)
            pageTo = request.To;

        IReadOnlyList<Candle> page = await GetPageWithRetryAsync(
            new CandleQuery(request.Instrument, request.BaseInterval, pageSize, request.Cursor, pageTo),
            cancellationToken).ConfigureAwait(false);

        Candle[] complete = page
            .Where(c => c.IsComplete &&
                        c.OpenTime >= request.Cursor &&
                        c.OpenTime < pageTo &&
                        c.OpenTime < request.To)
            .OrderBy(c => c.OpenTime)
            .ToArray();

        DateTimeOffset? lastOpen = null;
        var candles = new List<MarketCandle>(complete.Length);
        foreach (Candle candle in complete)
        {
            if (lastOpen is not null && candle.OpenTime <= lastOpen)
                continue;
            lastOpen = candle.OpenTime;
            candles.Add(MarketCandle.FromMid(candle));
        }

        DateTimeOffset? next = candles.Count == 0
            ? pageTo
            : request.BaseInterval.AddTo(candles[^1].OpenTime);
        bool isComplete = next is null || next >= request.To || (candles.Count == 0 && pageTo >= request.To);
        return new HistoricalCandlePage
        {
            Candles = candles,
            NextCursor = next,
            IsComplete = isComplete,
            PageNumber = request.PageNumber
        };
    }

    private async IAsyncEnumerable<MarketCandle> DownloadPagesAsync(
        HistoricalCandleRequest request,
        Action<int> onPage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DateTimeOffset cursor = request.From;
        int pageSize = Math.Clamp(request.PageSize, 1, 5_000);
        int pageNumber = 0;
        DateTimeOffset? lastEmittedOpen = null;

        while (cursor < request.To)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;
            onPage(pageNumber);

            HistoricalCandlePage page = await ReadPageAsync(
                new HistoricalPageRequest(
                    request.Instrument,
                    request.BaseInterval,
                    request.From,
                    request.To,
                    cursor,
                    pageSize,
                    pageNumber),
                cancellationToken).ConfigureAwait(false);

            if (page.Candles.Count == 0)
            {
                DateTimeOffset next = page.NextCursor ?? request.BaseInterval.AddTo(cursor);
                if (next <= cursor)
                    throw new InvalidOperationException("OANDA paging did not advance after an empty page.");
                cursor = next;
                continue;
            }

            foreach (MarketCandle candle in page.Candles)
            {
                if (lastEmittedOpen is not null && candle.OpenTime <= lastEmittedOpen)
                    continue;
                lastEmittedOpen = candle.OpenTime;
                yield return candle;
            }

            cursor = page.NextCursor ?? request.To;
            if (page.IsComplete)
                yield break;
        }
    }

    private async Task<IReadOnlyList<Candle>> GetPageWithRetryAsync(
        CandleQuery query,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        TimeSpan delay = TimeSpan.FromMilliseconds(200);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _marketData.GetCandlesAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (BrokerApiException exception) when (!IsTransient(exception) || attempt >= maxAttempts)
            {
                throw;
            }
            catch (HttpRequestException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 8_000));
            }
            catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 8_000));
            }
        }
    }

    private static bool IsTransient(BrokerApiException exception)
    {
        // Permanent: auth / not found / bad request.
        int code = (int)exception.StatusCode;
        if (code is 401 or 403 or 404 or 400)
            return false;
        return code is 429 or >= 500;
    }

    private static void ValidateRequest(HistoricalCandleRequest request)
    {
        if (request.Instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.", nameof(request));
        if (!request.BaseInterval.IsValid)
            throw new ArgumentException("Interval is invalid.", nameof(request));
        if (request.From >= request.To)
            throw new ArgumentException("From must be earlier than To.", nameof(request));
    }

    private void RaiseProgress(
        InstrumentKey instrument, long count, DateTimeOffset? latest, string status, bool fromCache, int pages) =>
        ProgressChanged?.Invoke(new CandleDownloadProgress(instrument, count, latest, status, fromCache, pages));

    public ValueTask DisposeAsync() => _owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
