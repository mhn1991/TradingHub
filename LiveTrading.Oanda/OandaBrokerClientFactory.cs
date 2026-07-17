using Brokers;
using Brokers.Abstractions;
using Brokers.Oanda;
using Brokers.Models;

namespace LiveTrading.Oanda;

/// <summary>
/// Provides an explicit read-only construction path for observe/shadow deployments and a separate
/// Practice-only trading path for manually approved or guarded autonomous demo execution.
/// </summary>
public static class OandaBrokerClientFactory
{
    public static IBrokerClient CreateReadOnly(OandaOptions options) =>
        BrokerClientFactory.CreateOanda(options);

    /// <summary>Creates a trading-capable OANDA client only for Practice/Demo.</summary>
    public static ITradingBrokerClient CreateDemoTrading(OandaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Environment != BrokerEnvironment.Demo)
            throw new InvalidOperationException(
                "The first live-trading release refuses autonomous or manual execution outside OANDA Practice/Demo.");
        return BrokerClientFactory.CreateOanda(options);
    }

    public static LiveBrokerCapabilityMatrix CapabilityMatrix => LiveBrokerCapabilityMatrix.OandaCurrent;
}
