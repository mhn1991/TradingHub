using Brokers.Abstractions;

namespace Brokers.Ig;

public sealed class IgOptions
{
    public required BrokerEnvironment Environment { get; init; }
    public required string ApiKey { get; init; }
    public required string Identifier { get; init; }
    public required string Password { get; init; }
    public string? AccountId { get; init; }
    public Uri? BaseAddress { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public IReadOnlyDictionary<string, string> InstrumentMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
