using TradingHub.Abstractions.Brokers;

namespace TradingHub.Brokers.Factory;

public interface IBrokerFactory
{
    IReadOnlyCollection<string> ConfiguredBrokerIds { get; }

    Task<IBrokerGateway> CreateAsync(
        string brokerId,
        CancellationToken cancellationToken = default);
}
