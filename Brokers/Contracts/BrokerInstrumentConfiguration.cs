namespace Brokers;

/// <summary>
/// Maps one system-wide instrument ID to the exact symbol required by this broker.
/// </summary>
public sealed record BrokerInstrumentConfiguration(
    string InstrumentId,
    string ProviderSymbol,
    bool IsEnabled = true);
