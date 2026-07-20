using LiveTrading.Persistence;

namespace TradingHub.Persistence.Postgres.Live;

internal sealed class DualWriteLiveTradingPersistence(
    ILiveTradingPersistence postgres,
    ILiveTradingPersistence legacy,
    bool postgresReads) : ILiveTradingPersistence
{
    public async Task RunAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(postgres.RunAsync(cancellationToken), legacy.RunAsync(cancellationToken))
            .ConfigureAwait(false);

    public async ValueTask AppendAsync<T>(string streamName, T payload, CancellationToken cancellationToken)
    {
        await postgres.AppendAsync(streamName, payload, cancellationToken).ConfigureAwait(false);
        await legacy.AppendAsync(streamName, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveCheckpointAsync(LiveEngineCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await postgres.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        await legacy.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task<LiveEngineCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken) =>
        (postgresReads ? postgres : legacy).LoadCheckpointAsync(cancellationToken);
}
