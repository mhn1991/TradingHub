using Microsoft.AspNetCore.SignalR;
using Simulator.Models;

namespace Dashboard.Live;

public sealed class SimulationHub : Hub
{
    public Task Subscribe(Guid simulationId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, simulationId.ToString("N"));

    public Task Unsubscribe(Guid simulationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, simulationId.ToString("N"));
}

public sealed class SimulationRealtimePublisher
{
    private readonly IHubContext<SimulationHub> _hub;

    public SimulationRealtimePublisher(IHubContext<SimulationHub> hub) =>
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public Task PublishAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        string group = snapshot.Id.ToString("N");
        string eventName = snapshot.Status switch
        {
            SimulationJobStatus.Completed => "SimulationCompleted",
            SimulationJobStatus.Failed => "SimulationFailed",
            SimulationJobStatus.Cancelled => "SimulationStatusChanged",
            _ when snapshot.ProcessedBaseCandles > 0 => "SimulationProgressChanged",
            _ => "SimulationStatusChanged"
        };

        return _hub.Clients.Group(group).SendAsync(eventName, snapshot, cancellationToken);
    }

    public Task PublishTradeAsync(
        Guid simulationId,
        string strategyId,
        SimulatedTradeRecord trade,
        CancellationToken cancellationToken = default) =>
        _hub.Clients
            .Group(simulationId.ToString("N"))
            .SendAsync("TradeCompleted", new { simulationId, strategyId, trade }, cancellationToken);
}
