namespace Brokers;

public interface IBrokerFactory
{
    IBroker Create(
        BrokerConfiguration configuration,
        IEnumerable<BrokerInstrumentConfiguration> instruments,
        BrokerCredentials? credentials = null);

    ValueTask<IBroker> CreateAsync(
        BrokerConfiguration configuration,
        IEnumerable<BrokerInstrumentConfiguration> instruments,
        CancellationToken cancellationToken = default);
}
