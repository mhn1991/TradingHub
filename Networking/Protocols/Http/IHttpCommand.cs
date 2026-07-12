using Networking.Abstractions;

namespace Networking.Http;

/// <summary>
/// A command that knows how to create its HTTP request and decode its response.
/// Broker-specific projects normally implement this interface.
/// </summary>
public interface IHttpCommand<TResponse> : INetworkCommand<TResponse>
{
    /// <summary>
    /// Overrides the transport's default timeout. Use Timeout.InfiniteTimeSpan for no timeout.
    /// </summary>
    TimeSpan? Timeout { get; }

    /// <summary>
    /// When true, non-success HTTP status codes throw before the response is decoded.
    /// </summary>
    bool EnsureSuccessStatusCode { get; }

    /// <summary>
    /// Allows the transport to retry transient failures. Only commands that are safe to repeat
    /// should enable this flag.
    /// </summary>
    bool IsIdempotent => false;

    /// <summary>
    /// Maximum number of retries after the initial attempt.
    /// </summary>
    int MaxTransientRetries => 0;

    HttpRequestMessage CreateRequest();

    ValueTask<TResponse> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);
}
