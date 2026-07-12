using Brokers.Abstractions;

namespace Brokers.Exceptions;

public sealed class BrokerFeatureNotSupportedException : NotSupportedException
{
    public BrokerFeatureNotSupportedException(BrokerKind broker, string feature)
        : base($"{broker} does not support the requested feature: {feature}")
    {
        Broker = broker;
        Feature = feature;
    }

    public BrokerKind Broker { get; }

    public string Feature { get; }
}
