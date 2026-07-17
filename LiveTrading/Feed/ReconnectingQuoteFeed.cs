using System.Threading.Channels;
using Brokers.Models;
using LiveTrading.MarketData;
using Microsoft.Extensions.Logging;

namespace LiveTrading.Feed;

public sealed record ReconnectingQuoteFeedOptions
{
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; init; } = 2.0;
}

/// <summary>
/// Broker-neutral reconnect/backoff orchestration around any <see cref="ILiveQuoteStream"/>.
/// Owns all retry policy so OANDA-specific adapters stay thin, one-shot translation layers (per
/// the plan doc's "do not put general engine state machines" rule for broker-specific projects).
/// Dispatches each tick to the right per-instrument channel and emits fault/recovery signals so
/// market actors can drive their own state without needing to know why a fault occurred.
/// </summary>
public sealed class ReconnectingQuoteFeed(
    ILiveQuoteStream inner,
    TimeProvider timeProvider,
    ReconnectingQuoteFeedOptions options,
    ILogger<ReconnectingQuoteFeed> logger)
{
    public async Task RunAsync(
        IReadOnlyList<InstrumentKey> instruments,
        IReadOnlyDictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> perMarketOutputs,
        CancellationToken cancellationToken,
        Func<LiveQuoteEvent, CancellationToken, ValueTask>? quoteObserver = null,
        Func<bool, string?, CancellationToken, ValueTask>? connectionObserver = null)
    {
        TimeSpan backoff = options.InitialBackoff;
        bool everConnected = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            bool receivedAnyTick = false;
            try
            {
                await foreach (LiveQuoteEvent quoteEvent in inner
                    .ReadAsync(instruments, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!receivedAnyTick)
                    {
                        receivedAnyTick = true;
                        backoff = options.InitialBackoff;
                        if (everConnected)
                        {
                            DateTimeOffset recoveredAt = timeProvider.GetUtcNow();
                            await BroadcastAsync(
                                instrument => new StreamRecoveredMarketEvent
                                {
                                    Instrument = instrument,
                                    ReceivedAt = recoveredAt,
                                    Kind = LiveMarketEventKind.StreamRecovered
                                },
                                perMarketOutputs, cancellationToken).ConfigureAwait(false);
                        }
                        everConnected = true;
                        if (connectionObserver is not null)
                        {
                            await connectionObserver(true, null, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (quoteObserver is not null)
                    {
                        await quoteObserver(quoteEvent, cancellationToken).ConfigureAwait(false);
                    }

                    if (perMarketOutputs.TryGetValue(quoteEvent.Instrument, out ChannelWriter<LiveMarketEvent>? writer))
                    {
                        await writer.WriteAsync(
                            new LiveQuoteMarketEvent
                            {
                                Instrument = quoteEvent.Instrument,
                                ReceivedAt = quoteEvent.Quote.ReceivedAt,
                                Kind = LiveMarketEventKind.Quote,
                                Quote = quoteEvent.Quote
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live quote stream faulted.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            logger.LogWarning(
                "Live quote stream closed{Reconnected}; reconnecting in {Backoff}.",
                receivedAnyTick ? " after receiving data" : string.Empty, backoff);
            DateTimeOffset faultedAt = timeProvider.GetUtcNow();
            if (connectionObserver is not null)
            {
                await connectionObserver(
                    false,
                    "Quote stream closed or faulted.",
                    cancellationToken).ConfigureAwait(false);
            }
            await BroadcastAsync(
                instrument => new StreamFaultMarketEvent
                {
                    Instrument = instrument,
                    ReceivedAt = faultedAt,
                    Kind = LiveMarketEventKind.StreamFault,
                    Reason = "Quote stream closed or faulted."
                },
                perMarketOutputs, cancellationToken).ConfigureAwait(false);

            await Task.Delay(backoff, timeProvider, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(
                Math.Min((long)(backoff.Ticks * options.BackoffMultiplier), options.MaxBackoff.Ticks));
        }
    }

    private static async Task BroadcastAsync(
        Func<InstrumentKey, LiveMarketEvent> factory,
        IReadOnlyDictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> perMarketOutputs,
        CancellationToken cancellationToken)
    {
        foreach ((InstrumentKey instrument, ChannelWriter<LiveMarketEvent> writer) in perMarketOutputs)
        {
            await writer.WriteAsync(factory(instrument), cancellationToken).ConfigureAwait(false);
        }
    }
}
