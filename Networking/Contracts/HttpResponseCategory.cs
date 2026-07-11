namespace Networking;

/// <summary>
/// Classifies every possible HTTP status-code range without treating non-success statuses as transport failures.
/// </summary>
public enum HttpResponseCategory
{
    Informational,
    Success,
    Redirect,
    ClientError,
    ServerError,
    Unknown
}
