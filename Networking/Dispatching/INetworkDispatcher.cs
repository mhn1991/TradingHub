namespace Networking;

/// <summary>
/// Top-level Networking entry point.
/// </summary>
public interface INetworkDispatcher
{
    Task<TResponse> DispatchAsync<TResponse>(
        NetworkRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : NetworkResponse;
}
