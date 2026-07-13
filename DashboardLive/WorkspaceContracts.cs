using Dashboard.Contracts;

namespace Dashboard.Live;

public enum WorkspaceEnvironment
{
    Demo,
    Live
}

public enum WorkspaceDataKind
{
    Replay,
    Market
}

public sealed record WorkspaceAsset(
    string Symbol,
    string DisplayName,
    string Instrument,
    IReadOnlyList<string> Timeframes);

public sealed record WorkspaceBroker(
    string Id,
    string DisplayName,
    WorkspaceEnvironment Environment,
    WorkspaceDataKind DataKind,
    bool IsConfigured,
    bool IsReadOnly,
    string Description,
    IReadOnlyList<WorkspaceAsset> Assets,
    bool CanTrade = false);

public sealed record WorkspaceStreamSelection(
    string BrokerId,
    string Symbol,
    string Interval);

public sealed record WorkspaceCatalog(
    DateTimeOffset GeneratedAt,
    WorkspaceStreamSelection StreamingSelection,
    IReadOnlyList<WorkspaceBroker> Brokers,
    string? Warning = null);

public sealed record WorkspaceSnapshot(
    LiveFeedStatus Status,
    ReplayDataset Dataset);

public sealed record OandaWorkspaceAccount(
    string AccountId,
    string? Currency,
    decimal? Balance,
    decimal? Available,
    decimal? MarginUsed,
    decimal? UnrealizedProfitLoss,
    bool? CanTrade,
    IReadOnlyList<Brokers.Models.BrokerPosition> Positions,
    IReadOnlyList<Brokers.Models.BrokerOrder> PendingOrders,
    bool DemoOrderExecutionEnabled);

public sealed record OandaWorkspaceOrderRequest(
    string Symbol,
    string Side,
    string Type,
    decimal Units,
    decimal? Price,
    decimal? StopLoss,
    decimal? TakeProfit,
    string? ClientOrderId);

public sealed record OandaOrderEventBatch(
    long Revision,
    IReadOnlyList<Brokers.Models.OrderEvent> Events);
