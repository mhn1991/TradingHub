using Brokers.Abstractions;

namespace Brokers.Binance;

public sealed class BinanceOptions
{
    public required BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    /// <summary>Optional for public market-data-only use.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Optional for public market-data-only use.</summary>
    public string? SecretKey { get; init; }
    public Uri? BaseAddress { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReceiveWindow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public IReadOnlyDictionary<string, string> InstrumentMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
