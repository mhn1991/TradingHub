namespace Brokers;

public enum BrokerErrorKind
{
    Validation,
    Unsupported,
    Authentication,
    Permission,
    NotFound,
    RateLimited,
    Redirect,
    Timeout,
    Transport,
    Server,
    InvalidResponse,
    Provider
}
