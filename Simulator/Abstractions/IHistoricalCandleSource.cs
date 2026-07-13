using System.Runtime.CompilerServices;
using Brokers.Models;

namespace Simulator.Abstractions;

public interface IHistoricalCandleSource
{
    IAsyncEnumerable<Candle> ReadAsync(
        CancellationToken cancellationToken = default);
}

public sealed class EnumerableCandleSource(IEnumerable<Candle> candles) : IHistoricalCandleSource
{
    private readonly IEnumerable<Candle> _candles = candles ?? throw new ArgumentNullException(nameof(candles));

    public async IAsyncEnumerable<Candle> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (Candle candle in _candles.OrderBy(item => item.OpenTime))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return candle;
            await Task.Yield();
        }
    }
}
