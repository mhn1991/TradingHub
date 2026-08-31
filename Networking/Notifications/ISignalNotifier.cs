namespace Networking.Notifications;

/// <summary>
/// Sends a signal alert to a human-facing channel.
/// <para>
/// <b>Never throws.</b> An alert is a side effect of a trading decision, not part of it: a
/// Telegram outage, a bad token or a network stall must not change what an agent decides or
/// fail the evaluation that produced it. Implementations swallow and report failures instead of
/// propagating them, and return <see langword="false"/> when nothing was delivered.
/// </para>
/// </summary>
public interface ISignalNotifier
{
    /// <summary>
    /// Delivers <paramref name="notification"/>. Returns <see langword="true"/> when the channel
    /// accepted it, <see langword="false"/> when it was suppressed, disabled, or failed.
    /// </summary>
    ValueTask<bool> NotifyAsync(SignalNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Does nothing and reports it. This is the default everywhere, which is what keeps backtests
/// and the simulator silent: an agent constructed without an explicit notifier cannot emit
/// traffic, so replaying 40k candles sends 40k messages to nobody rather than to Telegram.
/// Only a host that deliberately injects a real notifier will send anything.
/// </summary>
public sealed class NullSignalNotifier : ISignalNotifier
{
    public static NullSignalNotifier Instance { get; } = new();

    private NullSignalNotifier()
    {
    }

    public ValueTask<bool> NotifyAsync(
        SignalNotification notification,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
