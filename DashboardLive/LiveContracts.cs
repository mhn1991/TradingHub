using Dashboard.Contracts;

namespace Dashboard.Live;

public enum LiveConnectionState
{
    Starting,
    WarmingUp,
    Connected,
    Reconnecting,
    Faulted,
    Stopped
}

public sealed record LiveFeedStatus
{
    public required LiveConnectionState State { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? LastMessageAt { get; init; }
    public DateTimeOffset? LastClosedCandleAt { get; init; }
    public int ReconnectAttempt { get; init; }
    public long GapsDetected { get; init; }
}

public sealed record LiveReplayPayload(
    long Revision,
    LiveFeedStatus Status,
    ReplayDataset Dataset);

public sealed record LiveReplayUpdate(
    long Revision,
    LiveFeedStatus Status,
    IReadOnlyList<ReplayFrame> Frames);
