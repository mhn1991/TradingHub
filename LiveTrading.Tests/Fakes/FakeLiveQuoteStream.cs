using System.Runtime.CompilerServices;
using Brokers.Models;
using LiveTrading.MarketData;

namespace LiveTrading.Tests.Fakes;

/// <summary>
/// Deterministic, scripted <see cref="ILiveQuoteStream"/> test double. Each entry in <see
/// cref="ConnectionScripts"/> represents one connection attempt: <see cref="ReconnectingQuoteFeed"/>
/// calls <c>ReadAsync</c> again after a script's sequence completes (mirroring the real
/// <c>StreamPricesAsync</c> "yield break on close, caller reconnects" contract), which drives the
/// next queued script. The last script repeats indefinitely once exhausted.
/// </summary>
public sealed class FakeLiveQuoteStream : ILiveQuoteStream
{
    public List<Func<IEnumerable<LiveQuoteEvent>>> ConnectionScripts { get; } = [];
    public int ConnectionAttempts { get; private set; }

    public async IAsyncEnumerable<LiveQuoteEvent> ReadAsync(
        IReadOnlyList<InstrumentKey> instruments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int index = Math.Min(ConnectionAttempts, ConnectionScripts.Count - 1);
        ConnectionAttempts++;
        if (index < 0)
        {
            yield break;
        }

        foreach (LiveQuoteEvent evt in ConnectionScripts[index]())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }
}
