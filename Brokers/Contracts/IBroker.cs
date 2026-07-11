namespace Brokers;

public interface IBroker
{
    string Id { get; }

    string Name { get; }

    BrokerProvider Provider { get; }

    BrokerEnvironment Environment { get; }

    Uri RestEndpoint { get; }

    Uri? StreamingEndpoint { get; }

    CandlePriceBasis CandlePriceBasis { get; }

    IReadOnlyCollection<BrokerInstrumentConfiguration> Instruments { get; }

    Task<BrokerResult<CandleBatch>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default);
}
