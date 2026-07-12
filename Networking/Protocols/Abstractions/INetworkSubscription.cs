namespace Networking.Abstractions;

public interface INetworkSubscription<TEvent>
{
    TransportId TransportId { get; }
}
