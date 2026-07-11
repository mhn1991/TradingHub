namespace Brokers;

/// <summary>
/// A broker operation result. Expected provider/network failures are values; caller cancellation still propagates.
/// </summary>
public sealed class BrokerResult<T>
    where T : class
{
    private BrokerResult(T? value, BrokerError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public BrokerError? Error { get; }

    public static BrokerResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BrokerResult<T>(value, null);
    }

    public static BrokerResult<T> Failure(BrokerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new BrokerResult<T>(null, error);
    }
}
