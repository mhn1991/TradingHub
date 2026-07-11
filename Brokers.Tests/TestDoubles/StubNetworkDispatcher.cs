using Networking;

namespace Brokers.Tests.TestDoubles;

internal sealed class StubNetworkDispatcher(
    Func<NetworkRequest, NetworkResponse> responseFactory) : INetworkDispatcher
{
    public List<NetworkRequest> Requests { get; } = [];

    public Task<TResponse> DispatchAsync<TResponse>(
        NetworkRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : NetworkResponse
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        NetworkResponse response = responseFactory(request);

        if (response is not TResponse typedResponse)
        {
            throw new InvalidOperationException(
                $"The stub returned {response.GetType().Name}, not {typeof(TResponse).Name}.");
        }

        return Task.FromResult(typedResponse);
    }
}
