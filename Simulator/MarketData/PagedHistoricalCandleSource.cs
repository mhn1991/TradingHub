using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.MarketData;

public sealed record HistoricalPageRequest(
    InstrumentKey Instrument,
    BarInterval BaseInterval,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset Cursor,
    int PageSize,
    int PageNumber);

public sealed record HistoricalCandlePage
{
    public required IReadOnlyList<MarketCandle> Candles { get; init; }
    public required DateTimeOffset? NextCursor { get; init; }
    public required bool IsComplete { get; init; }
    public required int PageNumber { get; init; }
}

public interface IPagedHistoricalCandleSource
{
    ValueTask<HistoricalCandlePage> ReadPageAsync(
        HistoricalPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PrefetchDiagnostics
{
    public required long CurrentUnread { get; init; }
    public required long PeakUnread { get; init; }
    public required int Capacity { get; init; }
    public required int LowWatermark { get; init; }
    public required int PagesRequested { get; init; }
    public required long SourceWaits { get; init; }
    public required long ConsumerWaits { get; init; }
    public required bool ProducerComplete { get; init; }
    public required bool SourceComplete { get; init; }
    public required string Mode { get; init; }
}

/// <summary>
/// Page-aware prefetch: requests the next page only when unread count is at/below low watermark.
/// Never overwrites unread candles; applies backpressure when the buffer is full.
/// </summary>
public sealed class LowWatermarkPrefetchStream : IHistoricalCandleStream
{
    private readonly IPagedHistoricalCandleSource _source;
    private readonly int _capacity;
    private readonly int _lowWatermark;
    private long _peakUnread;
    private int _pagesRequested;
    private long _sourceWaits;
    private long _consumerWaits;
    private long _currentUnread;
    private bool _producerComplete;
    private bool _sourceComplete;

    public LowWatermarkPrefetchStream(
        IPagedHistoricalCandleSource source,
        int capacity,
        int lowWatermark)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (lowWatermark < 1 || lowWatermark >= capacity)
            throw new ArgumentOutOfRangeException(nameof(lowWatermark));
        _capacity = capacity;
        _lowWatermark = lowWatermark;
    }

    public PrefetchDiagnostics SnapshotDiagnostics() => new()
    {
        CurrentUnread = Interlocked.Read(ref _currentUnread),
        PeakUnread = Interlocked.Read(ref _peakUnread),
        Capacity = _capacity,
        LowWatermark = _lowWatermark,
        PagesRequested = Volatile.Read(ref _pagesRequested),
        SourceWaits = Interlocked.Read(ref _sourceWaits),
        ConsumerWaits = Interlocked.Read(ref _consumerWaits),
        ProducerComplete = Volatile.Read(ref _producerComplete),
        SourceComplete = Volatile.Read(ref _sourceComplete),
        Mode = "LowWatermarkPaged"
    };

    public PrefetchDiagnostics SnapshotDiagnostics(long currentUnread) => SnapshotDiagnostics() with
    {
        CurrentUnread = currentUnread
    };

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken producerToken = producerCancellation.Token;
        var channel = Channel.CreateBounded<MarketCandle>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        long writeSequence = 0;
        long readSequence = 0;
        Exception? producerError = null;
        var unreadGate = new SemaphoreSlim(0, int.MaxValue);

        Task producer = Task.Run(async () =>
        {
            try
            {
                DateTimeOffset cursor = request.From;
                int pageNumber = 0;
                bool complete = false;
                bool suspended = false;
                while (!complete && cursor < request.To)
                {
                    producerToken.ThrowIfCancellationRequested();

                    if (suspended)
                    {
                        Interlocked.Increment(ref _sourceWaits);
                        while (Volatile.Read(ref writeSequence) - Volatile.Read(ref readSequence) > _lowWatermark)
                            await unreadGate.WaitAsync(producerToken).ConfigureAwait(false);
                        suspended = false;
                    }

                    pageNumber++;
                    Interlocked.Increment(ref _pagesRequested);
                    HistoricalCandlePage page = await _source.ReadPageAsync(
                        new HistoricalPageRequest(
                            request.Instrument,
                            request.BaseInterval,
                            request.From,
                            request.To,
                            cursor,
                            request.PageSize,
                            pageNumber),
                        producerToken).ConfigureAwait(false);

                    foreach (MarketCandle candle in page.Candles)
                    {
                        if (suspended)
                        {
                            Interlocked.Increment(ref _sourceWaits);
                            while (Volatile.Read(ref writeSequence) - Volatile.Read(ref readSequence) > _lowWatermark)
                                await unreadGate.WaitAsync(producerToken).ConfigureAwait(false);
                            suspended = false;
                        }

                        await channel.Writer.WriteAsync(candle, producerToken).ConfigureAwait(false);
                        long written = Interlocked.Increment(ref writeSequence);
                        long unreadNow = written - Volatile.Read(ref readSequence);
                        Interlocked.Exchange(ref _currentUnread, unreadNow);
                        long peak = Interlocked.Read(ref _peakUnread);
                        while (unreadNow > peak &&
                               Interlocked.CompareExchange(ref _peakUnread, unreadNow, peak) != peak)
                        {
                            peak = Interlocked.Read(ref _peakUnread);
                        }
                        suspended = unreadNow >= _capacity;
                    }

                    complete = page.IsComplete || page.NextCursor is null || page.NextCursor >= request.To;
                    if (page.NextCursor is DateTimeOffset next)
                        cursor = next;
                    else
                        complete = true;
                }

                Volatile.Write(ref _sourceComplete, true);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                producerError = exception;
                channel.Writer.TryComplete(exception);
            }
            finally
            {
                Volatile.Write(ref _producerComplete, true);
            }
        }, producerToken);

        try
        {
            while (true)
            {
                if (!channel.Reader.TryRead(out MarketCandle? candle))
                {
                    Interlocked.Increment(ref _consumerWaits);
                    if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        break;
                    continue;
                }
                if (candle is null)
                    continue;

                long read = Interlocked.Increment(ref readSequence);
                Interlocked.Exchange(ref _currentUnread, Volatile.Read(ref writeSequence) - read);
                unreadGate.Release();
                yield return candle;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch
            {
            }

            unreadGate.Dispose();
        }

        if (producerError is not null && !producerToken.IsCancellationRequested)
            throw producerError;
    }
}

/// <summary>In-memory paged source for tests and inline candle jobs.</summary>
public sealed class EnumerablePagedCandleSource : IPagedHistoricalCandleSource, IHistoricalCandleStream
{
    private readonly MarketCandle[] _candles;

    public EnumerablePagedCandleSource(IEnumerable<Candle> candles)
    {
        _candles = candles
            .OrderBy(c => c.OpenTime)
            .Select(MarketCandle.FromMid)
            .ToArray();
    }

    public EnumerablePagedCandleSource(IEnumerable<MarketCandle> candles)
    {
        _candles = candles.OrderBy(c => c.OpenTime).ToArray();
    }

    public ValueTask<HistoricalCandlePage> ReadPageAsync(
        HistoricalPageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarketCandle[] page = _candles
            .Where(c => c.Instrument == request.Instrument &&
                        c.OpenTime >= request.Cursor &&
                        c.OpenTime >= request.From &&
                        c.OpenTime < request.To)
            .Take(request.PageSize)
            .ToArray();

        DateTimeOffset? next = page.Length == 0
            ? null
            : request.BaseInterval.AddTo(page[^1].OpenTime);
        bool complete = page.Length < request.PageSize || next is null || next >= request.To;
        return ValueTask.FromResult(new HistoricalCandlePage
        {
            Candles = page,
            NextCursor = next,
            IsComplete = complete,
            PageNumber = request.PageNumber
        });
    }

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (MarketCandle candle in _candles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candle.Instrument != request.Instrument)
                continue;
            if (candle.OpenTime < request.From || candle.OpenTime >= request.To)
                continue;
            yield return candle;
            await Task.Yield();
        }
    }
}
