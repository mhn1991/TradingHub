namespace Brokers;

internal sealed class BrokerPayloadException : Exception
{
    public BrokerPayloadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
