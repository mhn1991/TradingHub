namespace Networking.Duplex;

/// <summary>
/// Represents one long-lived, full-duplex connection.
/// WebSocket and FIX adapters can both implement this contract.
/// </summary>
public interface IDuplexSession : IAsyncDisposable
{
    bool IsStarted { get; }

    Task Completion { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}
