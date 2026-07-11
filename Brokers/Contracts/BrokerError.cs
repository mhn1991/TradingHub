namespace Brokers;

public sealed record BrokerError(
    BrokerErrorKind Kind,
    string Message,
    string? ProviderCode = null,
    int? HttpStatusCode = null,
    bool IsRetryable = false,
    TimeSpan? RetryAfter = null,
    string? RequestId = null);
