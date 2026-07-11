using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Application.Strategies;

public interface ITradingStrategy
{
    string Id { get; }

    ValueTask<IReadOnlyList<TradeIntent>> OnBarAsync(
        StrategyContext context,
        PriceBar bar,
        CancellationToken cancellationToken = default);
}
