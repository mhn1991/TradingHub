namespace Brokers.Abstractions;

public sealed record BrokerDescriptor(
    BrokerKind Kind,
    BrokerEnvironment Environment,
    string? AccountId);
