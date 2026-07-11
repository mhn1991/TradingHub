using TradingHub.Abstractions.Brokers;
using TradingHub.Brokers.Configuration;

namespace TradingHub.Brokers.Factory;

public interface IBrokerProvider
{
    string ProviderName { get; }

    IBrokerGateway Create(
        BrokerDefinition definition,
        BrokerCredentialSet credentials);
}
