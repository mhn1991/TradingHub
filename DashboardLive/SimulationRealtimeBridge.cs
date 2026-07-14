using System.Threading.Channels;
using Simulator.Models;
using Simulator.Services;

namespace Dashboard.Live;

/// <summary>Forwards application-service job updates to SignalR subscribers.</summary>
public sealed class SimulationRealtimeBridge : IHostedService
{
    private readonly BacktestApplicationService _service;
    private readonly SimulationRealtimePublisher _publisher;
    private readonly ILogger<SimulationRealtimeBridge> _logger;
    private readonly Channel<RealtimeNotification> _notifications =
        Channel.CreateUnbounded<RealtimeNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenSource? _lifetime;
    private Task? _pump;

    public SimulationRealtimeBridge(
        BacktestApplicationService service,
        SimulationRealtimePublisher publisher,
        ILogger<SimulationRealtimeBridge> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _service.JobUpdated += OnJobUpdated;
        _service.TradeCompleted += OnTradeCompleted;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pump = PumpAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _service.JobUpdated -= OnJobUpdated;
        _service.TradeCompleted -= OnTradeCompleted;
        _notifications.Writer.TryComplete();
        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _lifetime?.Cancel();
            }
        }
        _lifetime?.Dispose();
    }

    private void OnJobUpdated(Simulator.Models.SimulationJobSnapshot snapshot) =>
        _notifications.Writer.TryWrite(new SnapshotNotification(snapshot));

    private void OnTradeCompleted(Guid simulationId, string strategyId, SimulatedTradeRecord trade) =>
        _notifications.Writer.TryWrite(new TradeNotification(simulationId, strategyId, trade));

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await foreach (RealtimeNotification notification in
                       _notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                switch (notification)
                {
                    case SnapshotNotification snapshot:
                        await _publisher.PublishAsync(snapshot.Snapshot, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case TradeNotification trade:
                        await _publisher.PublishTradeAsync(
                                trade.SimulationId,
                                trade.StrategyId,
                                trade.Trade,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Failed to publish a simulation realtime notification.");
            }
        }
    }

    private abstract record RealtimeNotification;
    private sealed record SnapshotNotification(SimulationJobSnapshot Snapshot) : RealtimeNotification;
    private sealed record TradeNotification(
        Guid SimulationId,
        string StrategyId,
        SimulatedTradeRecord Trade) : RealtimeNotification;
}
