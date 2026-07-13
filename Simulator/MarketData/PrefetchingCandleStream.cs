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

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

        Task producer = Task.Run(async () =>
        {
            try
            {
                await foreach (MarketCandle candle in _inner.StreamAsync(request, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    writeSequence++;
                    await channel.Writer.WriteAsync(candle, cancellationToken).ConfigureAwait(false);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                producerError = exception;
                channel.Writer.TryComplete(exception);
            }
        }, cancellationToken);

        try
        {
            await foreach (MarketCandle candle in channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                readSequence++;
                // Unread count is derived from absolute sequences, not physical ring slots.
                long unread = writeSequence - readSequence;
                _ = unread; // available for diagnostics / future low-watermark hooks
                _ = _lowWatermark;
                yield return candle;
            }
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch
            {
                // Surface via producerError / channel completion.
            }
        }

        if (producerError is not null)
            throw producerError;
    }
}
