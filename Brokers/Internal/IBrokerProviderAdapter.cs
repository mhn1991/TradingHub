using Networking;

namespace Brokers;

internal interface IBrokerProviderAdapter
{
    BrokerProvider Provider { get; }

    CandlePriceBasis PriceBasis { get; }

    BrokerResult<HttpNetworkRequest> CreateCandleRequest(
        CandleQuery query,
        BrokerInstrumentConfiguration instrument,
        int limit);

    CandleBatch ParseCandles(
        CandleQuery query,
        BrokerInstrumentConfiguration instrument,
        int limit,
        HttpNetworkResponse response);

    BrokerError ParseError(HttpNetworkResponse response);
}
