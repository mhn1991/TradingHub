using System.Threading.Channels;
using Brokers.Models;
using Microsoft.Extensions.Logging;

namespace LiveTrading.MarketData;

public sealed record CompletedCandleCoordinatorOptions
{
    /// <summary>Small delay after each canonical minute boundary before polling, so the broker's
    /// own candle-finalization has time to settle.</summary>
    public TimeSpan FinalizationDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// One instance per instrument. At every canonical M1 boundary: waits a finalization delay,
/// requests completed candles since the last confirmed close, validates strict chronological
/// continuity, publishes every missing candle in order, advances the cursor. Never assumes
/// exactly one candle arrived. A transient poll failure leaves the cursor unchanged - the next
/// successful poll's <c>fromExclusive</c> naturally re-fetches everything missed, which is how
/// "reconnect catches up" is satisfied without a separate code path.
/// </summary>
public sealed class CompletedCandleCoordinator(
    InstrumentKey instrument,
    BarInterval baseInterval,
    ICompletedCandleProvider provider,
    TimeProvider timeProvider,
    CompletedCandleCoordinatorOptions options,
    ILogger<CompletedCandleCoordinator> logger)
{
    public async Task RunAsync(
        ChannelWriter<Candle> output, DateTimeOffset lastConfirmedClose, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset nextBoundary = NextMinuteBoundary(now);
            await Task.Delay(nextBoundary - now, timeProvider, cancellationToken).ConfigureAwait(false);
            await Task.Delay(options.FinalizationDelay, timeProvider, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<Candle> candles;
            try
            {
                candles = await provider.GetCompletedCandlesAsync(
                    instrument, baseInterval, lastConfirmedClose, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Completed-candle poll failed for {Instrument}; cursor unchanged, retrying next boundary.",
                    instrument);
                continue;
            }

            ValidateContinuity(candles, lastConfirmedClose);

            // Materialize before iterating: lastConfirmedClose is mutated inside the loop body,
            // and a lazy LINQ query would otherwise re-evaluate its Where predicate against the
            // already-advanced cursor on each MoveNext, silently dropping every candle after the
            // first.
            DateTimeOffset cursorBeforeThisPoll = lastConfirmedClose;
            List<Candle> newCandles = candles.Where(c => c.OpenTime > cursorBeforeThisPoll).ToList();
            foreach (Candle candle in newCandles)
            {
                await output.WriteAsync(candle, cancellationToken).ConfigureAwait(false);
                lastConfirmedClose = candle.CloseTime ?? baseInterval.AddTo(candle.OpenTime);
            }
        }
    }

    private void ValidateContinuity(IReadOnlyList<Candle> candles, DateTimeOffset lastConfirmedClose)
    {
        for (int i = 0; i < candles.Count; i++)
        {
            if (!candles[i].IsComplete)
            {
                throw new InvalidOperationException(
                    $"ICompletedCandleProvider contract violated: incomplete candle for {instrument} " +
                    $"at {candles[i].OpenTime:O}.");
            }

            if (candles[i].OpenTime <= lastConfirmedClose)
            {
                continue;
            }

            if (i > 0 && candles[i].OpenTime <= candles[i - 1].OpenTime)
            {
                throw new InvalidOperationException(
                    $"Non-monotonic candle sequence for {instrument} at {candles[i].OpenTime:O}.");
            }
        }
    }

    private static DateTimeOffset NextMinuteBoundary(DateTimeOffset from)
    {
        DateTimeOffset truncated = new(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, from.Offset);
        return truncated <= from ? truncated.AddMinutes(1) : truncated;
    }
}
