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
public sealed class OandaStreamingCandleSource : IHistoricalCandleStreamWithProgress, IPagedHistoricalCandleSource
{
    private readonly IMarketDataClient _marketData;
    private readonly BrokerEnvironment _environment;
    private readonly string _cacheDirectory;

    public OandaStreamingCandleSource(
        IMarketDataClient marketData,
        BrokerEnvironment environment = BrokerEnvironment.Demo,
        string? cacheDirectory = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _environment = environment;
        _cacheDirectory = cacheDirectory ?? Path.Combine(".cache", "oanda");
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
            RaiseProgress(0, null, "LoadingCache", fromCache: true, pages: 0);
            long count = 0;
            await foreach (MarketCandle candle in StreamingCandleCache.ReadStreamAsync(
                               cachePath,
                               request.Instrument,
                               request.BaseInterval,
                               cancellationToken).ConfigureAwait(false))
            {
                count++;
                if (count % 2_000 == 0)
                    RaiseProgress(count, candle.OpenTime, "LoadingCache", fromCache: true, pages: 0);
                yield return candle;
            }

            RaiseProgress(count, null, "CacheLoaded", fromCache: true, pages: 0);
            yield break;
        }

        RaiseProgress(0, null, "DownloadingData", fromCache: false, pages: 0);

        StreamingCandleCacheWriter? cacheWriter = request.NoCache
            ? null
            : new StreamingCandleCacheWriter(cachePath, request, "oanda", _environment);

        long downloaded = 0;
        int pages = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Exception? failure = null;

        await foreach (MarketCandle candle in DownloadPagesAsync(request, onPage: p => pages = p, cancellationToken)
                           .ConfigureAwait(false))
        {
            first ??= candle.OpenTime;
            last = candle.OpenTime;
            downloaded++;

            if (cacheWriter is not null)
            {
                try
                {
                    await cacheWriter.AppendAsync(candle, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    break;
                }
            }

            string fingerprint =
                $"{candle.OpenTime:O}|{candle.Mid.Prices.Open}|{candle.Mid.Prices.High}|{candle.Mid.Prices.Low}|{candle.Mid.Prices.Close}";
            hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(fingerprint));

            if (downloaded % 500 == 0)
                RaiseProgress(downloaded, candle.OpenTime, "DownloadingData", fromCache: false, pages);

            // Yield immediately — do not wait for full-year download.
            yield return candle;
        }

        if (failure is not null)
        {
            if (cacheWriter is not null)
                await cacheWriter.AbortAsync(cancellationToken).ConfigureAwait(false);
            await (cacheWriter?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
            throw failure;
        }

        if (cacheWriter is not null && first is not null && last is not null)
        {
            string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            await cacheWriter.CommitAsync(
                new StreamingCacheCommitMetadata(downloaded, first.Value, last.Value, hash, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await cacheWriter.DisposeAsync().ConfigureAwait(false);
        }
        else if (cacheWriter is not null)
        {
            await cacheWriter.AbortAsync(cancellationToken).ConfigureAwait(false);
            await cacheWriter.DisposeAsync().ConfigureAwait(false);
        }

        RaiseProgress(downloaded, last, "DownloadComplete", fromCache: false, pages);
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
        int consecutiveEmpty = 0;

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
                consecutiveEmpty++;
                cursor = page.NextCursor ?? request.BaseInterval.AddTo(cursor);
                if (consecutiveEmpty > 8 || cursor >= request.To)
                    yield break;
                continue;
            }

            consecutiveEmpty = 0;
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

    private void RaiseProgress(long count, DateTimeOffset? latest, string status, bool fromCache, int pages) =>
        ProgressChanged?.Invoke(new CandleDownloadProgress(count, latest, status, fromCache));
}
