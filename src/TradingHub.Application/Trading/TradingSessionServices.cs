using TradingHub.Application.Orders;
using TradingHub.Application.Risk;
using TradingHub.Application.Strategies;
using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;

namespace TradingHub.Application.Trading;

public sealed record TradingSessionServices
{
    public required IClock Clock { get; init; }

    public required IBrokerGateway Broker { get; init; }

    public required IRiskEngine RiskEngine { get; init; }

    public required OrderFactory OrderFactory { get; init; }

    public required IReadOnlyList<ITradingStrategy> Strategies { get; init; }
}
