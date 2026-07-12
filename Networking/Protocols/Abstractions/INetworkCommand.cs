namespace Networking.Abstractions;

public interface INetworkCommand<TResponse>
{
    TransportId TransportId { get; }
}
