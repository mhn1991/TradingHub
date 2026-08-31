using Networking.Notifications;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class TelegramMessageFormatterTests
{
    private static SignalNotification Base(SignalSide side = SignalSide.Buy) => new()
    {
        Instrument = "METAL:XAU/USD",
        Side = side,
        Strategy = "Breakout detector agent",
        DecisionTime = new DateTimeOffset(2026, 8, 27, 2, 5, 0, TimeSpan.Zero),
        Interval = "5m"
    };

    [Test]
    public void Format_BuyAndSell_AreDistinguishable()
    {
        Assert.That(TelegramMessageFormatter.Format(Base()), Does.Contain("BUY"));
        Assert.That(TelegramMessageFormatter.Format(Base(SignalSide.Sell)), Does.Contain("SELL"));
        Assert.That(TelegramMessageFormatter.Format(Base()), Does.Not.Contain("SELL"));
    }

    [Test]
    public void Format_RendersTheDecisionTimeInUtc()
    {
        // A signal raised in a local offset must still read as UTC, or an alert is ambiguous.
        SignalNotification local = Base() with
        {
            DecisionTime = new DateTimeOffset(2026, 8, 27, 3, 5, 0, TimeSpan.FromHours(1))
        };

        Assert.That(TelegramMessageFormatter.Format(local), Does.Contain("2026-08-27 02:05:00 UTC"));
    }

    [Test]
    public void RewardRisk_LongAndShort_ComputeFromTheCorrectSide()
    {
        SignalNotification longSignal = Base() with
        {
            ReferencePrice = 100m, StopLossPrice = 98m, TakeProfitPrice = 106m
        };
        SignalNotification shortSignal = Base(SignalSide.Sell) with
        {
            ReferencePrice = 100m, StopLossPrice = 102m, TakeProfitPrice = 94m
        };

        Assert.That(TelegramMessageFormatter.RewardRisk(longSignal), Is.EqualTo(3.00m));
        Assert.That(TelegramMessageFormatter.RewardRisk(shortSignal), Is.EqualTo(3.00m));
    }

    [Test]
    public void RewardRisk_WithAStopOnTheWrongSide_IsOmitted()
    {
        // A long whose stop sits above entry is malformed; printing a negative or inverted ratio
        // would present nonsense as fact, so it is dropped instead.
        SignalNotification inverted = Base() with
        {
            ReferencePrice = 100m, StopLossPrice = 104m, TakeProfitPrice = 106m
        };

        Assert.That(TelegramMessageFormatter.RewardRisk(inverted), Is.Null);
        Assert.That(TelegramMessageFormatter.Format(inverted), Does.Not.Contain("R:R"));
    }

    [Test]
    public void RewardRisk_WithoutAllThreeLegs_IsOmitted()
    {
        Assert.That(TelegramMessageFormatter.RewardRisk(Base() with { ReferencePrice = 100m }), Is.Null);
    }

    [Test]
    public void Format_OmitsLevelsThatWereNotSupplied()
    {
        string message = TelegramMessageFormatter.Format(Base());

        Assert.That(message, Does.Not.Contain("Entry"));
        Assert.That(message, Does.Not.Contain("Stop"));
        Assert.That(message, Does.Contain("METAL:XAU/USD"));
    }

    [Test]
    public void Format_EscapesMarkupInFreeFormText()
    {
        // Reason text is authored in agent code and rendered with parse_mode=HTML; unescaped
        // angle brackets would either break the message or inject markup.
        SignalNotification injected = Base() with { Reason = "range <b>broken</b> & held" };

        string message = TelegramMessageFormatter.Format(injected);

        Assert.That(message, Does.Contain("&lt;b&gt;broken&lt;/b&gt;"));
        Assert.That(message, Does.Contain("&amp;"));
    }

    [Test]
    public void Format_TruncatesBeyondTheTelegramLimit()
    {
        SignalNotification verbose = Base() with { Reason = new string('x', 6000) };

        string message = TelegramMessageFormatter.Format(verbose);

        Assert.That(message.Length, Is.LessThanOrEqualTo(TelegramMessageFormatter.MaximumMessageLength));
        Assert.That(message, Does.EndWith("…"));
    }
}
