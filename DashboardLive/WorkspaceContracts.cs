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

/// <summary>
/// A historical window of analysed candles centred on one anchor candle, for timeframe drill-in.
/// Unlike <see cref="WorkspaceSnapshot"/> - a rolling view of "now" - this is anchored to a point
/// in the past and deliberately includes candles on BOTH sides of the anchor.
///
/// Every returned frame is fully warmed up: the analyser is fed <see cref="WarmupCandles"/>
/// additional candles before the first visible one, and those warm-up frames are then discarded.
/// Without that, the earliest candles in the window would carry null/partial ATR, RSI, Bollinger
/// and unconfirmed swings, and the chart would annotate them wrongly rather than not at all.
/// </summary>
/// <param name="AnchorIndex">
/// Index of the anchor candle inside the returned frames. Not necessarily
/// <see cref="BeforeCount"/>: near the start of available history, or near the live edge, fewer
/// candles exist on one side and the window is clamped rather than padded.
/// </param>
/// <param name="WarmupSatisfied">
/// False when the broker could not supply the full warm-up span (typically the very start of
/// available history). The frames are still analysed, but the earliest indicators are less settled,
/// and the caller may wish to say so rather than presenting them as equivalent.
/// </param>
public sealed record WorkspaceWindowSnapshot(
    LiveFeedStatus Status,
    ReplayDataset Dataset,
    string Interval,
    DateTimeOffset RequestedAnchorAt,
    DateTimeOffset ResolvedAnchorAt,
    int AnchorIndex,
    int BeforeCount,
    int AfterCount,
    int WarmupCandles,
    bool WarmupSatisfied);

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
