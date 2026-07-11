using TradingHub.Domain.Markets;

namespace TradingHub.Abstractions.MarketData;

/// <summary>
/// Produces canonical internal market bars. Provider-specific DTOs must be mapped
/// before values cross this boundary.
/// </summary>
public interface IMarketDataSource
{
    IAsyncEnumerable<PriceBar> ReadAsync(CancellationToken cancellationToken = default);
}
