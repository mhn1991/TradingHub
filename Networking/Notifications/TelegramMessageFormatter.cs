using System.Globalization;
using System.Text;

namespace Networking.Notifications;

/// <summary>
/// Renders a <see cref="SignalNotification"/> as Telegram HTML. Pure and separate from the
/// sender so message shape is unit-testable without a network round trip.
/// </summary>
public static class TelegramMessageFormatter
{
    /// <summary>Telegram rejects messages over 4096 characters, so a long reason is trimmed.</summary>
    public const int MaximumMessageLength = 4096;

    public static string Format(SignalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // "parse_mode=HTML" means any of these fields could otherwise inject markup or break the
        // message; a reason string is free-form and comes from agent code, so escape everything.
        var builder = new StringBuilder();
        builder.Append(notification.Side == SignalSide.Buy ? "\U0001F7E2 <b>BUY</b>" : "\U0001F534 <b>SELL</b>");
        builder.Append(" · <b>").Append(Escape(notification.Instrument)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(notification.Interval))
            builder.Append(" · ").Append(Escape(notification.Interval));
        builder.Append('\n');

        builder.Append("Strategy: ").Append(Escape(notification.Strategy)).Append('\n');
        builder.Append("Time: ")
            .Append(Escape(notification.DecisionTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)))
            .Append('\n');

        AppendPrice(builder, "Entry", notification.ReferencePrice);
        AppendPrice(builder, "Stop", notification.StopLossPrice);
        AppendPrice(builder, "Target", notification.TakeProfitPrice);

        // Only meaningful with all three legs present, and only when the stop is on the correct
        // side of entry — a zero or inverted risk would otherwise divide by zero or print nonsense.
        decimal? rewardRisk = RewardRisk(notification);
        if (rewardRisk is { } ratio)
            builder.Append("R:R: ").Append(ratio.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');

        if (notification.Confidence is { } confidence)
            builder.Append("Confidence: ").Append(confidence.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');

        if (!string.IsNullOrWhiteSpace(notification.Reason))
            builder.Append('\n').Append(Escape(notification.Reason));

        string message = builder.ToString();
        return message.Length <= MaximumMessageLength
            ? message
            : message[..(MaximumMessageLength - 1)] + "…";
    }

    /// <summary>Reward-to-risk, or null when it cannot be computed honestly.</summary>
    public static decimal? RewardRisk(SignalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.ReferencePrice is not { } entry ||
            notification.StopLossPrice is not { } stop ||
            notification.TakeProfitPrice is not { } target)
        {
            return null;
        }

        decimal risk = notification.Side == SignalSide.Buy ? entry - stop : stop - entry;
        decimal reward = notification.Side == SignalSide.Buy ? target - entry : entry - target;
        if (risk <= 0m || reward <= 0m)
            return null;
        return decimal.Round(reward / risk, 2, MidpointRounding.AwayFromZero);
    }

    private static void AppendPrice(StringBuilder builder, string label, decimal? value)
    {
        if (value is not { } price)
            return;
        builder.Append(label).Append(": ").Append(price.ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
