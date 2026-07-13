using Simulator.Services;

namespace Dashboard.Live;

/// <summary>Forwards application-service job updates to SignalR subscribers.</summary>
public sealed class SimulationRealtimeBridge : IHostedService
{
    private readonly BacktestApplicationService _service;
    private readonly SimulationRealtimePublisher _publisher;

    public SimulationRealtimeBridge(
        BacktestApplicationService service,
        SimulationRealtimePublisher publisher)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _service.JobUpdated += OnJobUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _service.JobUpdated -= OnJobUpdated;
        return Task.CompletedTask;
    }

    private void OnJobUpdated(Simulator.Models.SimulationJobSnapshot snapshot) =>
        _ = _publisher.PublishAsync(snapshot);
}
