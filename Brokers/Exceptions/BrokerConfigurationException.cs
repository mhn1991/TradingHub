namespace Brokers;

public sealed class BrokerConfigurationException : Exception
{
    public BrokerConfigurationException(string message)
        : base(message)
    {
    }
}
