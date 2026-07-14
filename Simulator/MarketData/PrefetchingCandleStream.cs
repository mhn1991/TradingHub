using System.Threading.Channels;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.MarketData;

/// <summary>
/// Bounded producer/consumer prefetch. Uses absolute sequences and never overwrites unread data;
/// backpressure is applied when the channel is full.
/// </summary>
public sealed class PrefetchingCandleStream : IHistoricalCandleStream
{
    private readonly IHistoricalCandleStream _inner;
    private readonly int _capacity;
    private readonly int _lowWatermark;
    private long _peakUnread;
    private long _sourceWaits;
    private long _consumerWaits;
    private long _currentUnread;
    private bool _producerComplete;

    public PrefetchingCandleStream(
        IHistoricalCandleStream inner,
        int capacity,
        int lowWatermark)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (lowWatermark < 1 || lowWatermark >= capacity)
            throw new ArgumentOutOfRangeException(nameof(lowWatermark));
        _capacity = capacity;
        _lowWatermark = lowWatermark;
    }

    public int Capacity => _capacity;
    public int LowWatermark => _lowWatermark;
    public PrefetchDiagnostics SnapshotDiagnostics() => new()
    {
        CurrentUnread = Interlocked.Read(ref _currentUnread),
        PeakUnread = Interlocked.Read(ref _peakUnread),
        Capacity = _capacity,
        LowWatermark = _lowWatermark,
        PagesRequested = 0,
        SourceWaits = Interlocked.Read(ref _sourceWaits),
        ConsumerWaits = Interlocked.Read(ref _consumerWaits),
        ProducerComplete = Volatile.Read(ref _producerComplete),
        SourceComplete = Volatile.Read(ref _producerComplete),
        Mode = "LowWatermarkStream"
    };

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                bool suspended = false;
                await foreach (MarketCandle candle in _inner.StreamAsync(request, producerToken)
                                   .ConfigureAwait(false))
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
                    long unread = written - Volatile.Read(ref readSequence);
                    Interlocked.Exchange(ref _currentUnread, unread);
                    UpdatePeak(ref _peakUnread, unread);
                    suspended = unread >= _capacity;
                }

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
                // Surface via producerError / channel completion.
            }
            unreadGate.Dispose();
        }

        if (producerError is not null && !producerToken.IsCancellationRequested)
            throw producerError;
    }

    private static void UpdatePeak(ref long target, long value)
    {
        long peak = Interlocked.Read(ref target);
        while (value > peak && Interlocked.CompareExchange(ref target, value, peak) != peak)
            peak = Interlocked.Read(ref target);
    }
}
