using LiveTradingHost.Api;
using Microsoft.AspNetCore.SignalR;

namespace LiveTradingHost;

/// <summary>No per-ID grouping - unlike DashboardLive's SimulationHub, exactly one engine runs
/// per host, so every connected client receives every status update.</summary>
public sealed class LiveStatusHub : Hub;

public sealed class LiveStatusRealtimePublisher(IHubContext<LiveStatusHub> hub)
{
    public Task PublishAsync(LiveEngineStatusDto snapshot, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("LiveStatusChanged", snapshot, cancellationToken);
}
