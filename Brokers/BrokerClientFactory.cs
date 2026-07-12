using Brokers.Binance;
using Brokers.Ig;
using Brokers.Oanda;

namespace Brokers;

public static class BrokerClientFactory
{
    public static OandaBrokerClient CreateOanda(OandaOptions options) => new(options);

    public static BinanceBrokerClient CreateBinance(BinanceOptions options) => new(options);

    public static IgBrokerClient CreateIg(IgOptions options) => new(options);
}
