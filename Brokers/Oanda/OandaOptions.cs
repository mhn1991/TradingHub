using Brokers.Abstractions;

namespace Brokers.Oanda;

public sealed class OandaOptions
{
    public required BrokerEnvironment Environment { get; init; }
    public required string AccountId { get; init; }
    public required string AccessToken { get; init; }
    public Uri? BaseAddress { get; init; }
    public Uri? StreamBaseAddress { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public IReadOnlyDictionary<string, string> InstrumentMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
