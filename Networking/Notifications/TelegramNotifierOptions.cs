namespace Networking.Notifications;

/// <summary>Configuration for <see cref="TelegramSignalNotifier"/>.</summary>
public sealed record TelegramNotifierOptions
{
    /// <summary>
    /// Bot token from @BotFather, of the form <c>123456789:AA...</c>.
    /// <para>
    /// <b>This is a credential.</b> Resolve it from the encrypted secret store
    /// (<c>DBManager.Abstractions/Credentials/SecretResolution.cs</c>, the same mechanism holding
    /// the OANDA access token) or an environment variable. Do not commit it to
    /// <c>appsettings.json</c>.
    /// </para>
    /// </summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>
    /// Target chat. A group id is negative (e.g. <c>-1001234567890</c>); a direct-message id is
    /// positive. For a group, add the bot to it and read the id from <c>getUpdates</c>.
    /// </summary>
    public string ChatId { get; init; } = string.Empty;

    /// <summary>
    /// Master switch. When false the notifier short-circuits without touching the network, so a
    /// deployment can carry the wiring without sending anything.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Per-request timeout. Kept short: an alert is worthless if it blocks a decision.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Suppress a repeat of the same <see cref="SignalNotification.DeduplicationKey"/> seen within
    /// this window. Guards against an agent that re-evaluates the same closed candle. Zero disables.
    /// </summary>
    public int DeduplicationMinutes { get; init; } = 60;

    /// <summary>Overridable for tests; the real Bot API otherwise.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.telegram.org/");

    /// <summary>
    /// Throws when enabled but unusable. Only validates in the enabled case — a disabled notifier
    /// with empty configuration is the normal state for a backtest or a dev machine.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(BotToken))
            throw new InvalidOperationException("Telegram notifications are enabled but no bot token is configured.");
        if (string.IsNullOrWhiteSpace(ChatId))
            throw new InvalidOperationException("Telegram notifications are enabled but no chat id is configured.");
        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException($"Telegram timeout {TimeoutSeconds}s is outside the supported 1-120s range.");
        if (DeduplicationMinutes < 0)
            throw new InvalidOperationException("Telegram deduplication window cannot be negative.");
    }
}
