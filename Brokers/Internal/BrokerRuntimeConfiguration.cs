using System.Collections.ObjectModel;

namespace Brokers;

internal sealed class BrokerRuntimeConfiguration
{
    public BrokerRuntimeConfiguration(
        BrokerConfiguration configuration,
        IEnumerable<BrokerInstrumentConfiguration> instruments,
        BrokerCredentials credentials,
        Uri restEndpoint,
        Uri? streamingEndpoint,
        CandlePriceBasis priceBasis)
    {
        BrokerInstrumentConfiguration[] instrumentArray = instruments.ToArray();

        Configuration = configuration;
        Instruments = new ReadOnlyCollection<BrokerInstrumentConfiguration>(instrumentArray);
        InstrumentsById = instrumentArray.ToDictionary(
            instrument => instrument.InstrumentId,
            StringComparer.OrdinalIgnoreCase);
        Credentials = credentials;
        RestEndpoint = restEndpoint;
        StreamingEndpoint = streamingEndpoint;
        PriceBasis = priceBasis;
        RequestTimeout = TimeSpan.FromMilliseconds(configuration.RequestTimeoutMilliseconds);
    }

    public BrokerConfiguration Configuration { get; }

    public IReadOnlyCollection<BrokerInstrumentConfiguration> Instruments { get; }

    public IReadOnlyDictionary<string, BrokerInstrumentConfiguration> InstrumentsById { get; }

    public BrokerCredentials Credentials { get; }

    public Uri RestEndpoint { get; }

    public Uri? StreamingEndpoint { get; }

    public CandlePriceBasis PriceBasis { get; }

    public TimeSpan RequestTimeout { get; }
}
