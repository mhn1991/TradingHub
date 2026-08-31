using System.Net;
using System.Text;
using System.Text.Json;
using Networking.Notifications;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class TelegramSignalNotifierTests
{
    private static SignalNotification Signal(
        SignalSide side = SignalSide.Buy,
        decimal? entry = 4642.30m,
        decimal? stop = 4638.32m,
        decimal? target = 4650.26m,
        string reason = "Range high broken with expansion.",
        DateTimeOffset? at = null) => new()
    {
        Instrument = "METAL:XAU/USD",
        Side = side,
        Strategy = "Breakout detector agent",
        DecisionTime = at ?? new DateTimeOffset(2026, 8, 27, 2, 5, 0, TimeSpan.Zero),
        Interval = "5m",
        ReferencePrice = entry,
        StopLossPrice = stop,
        TakeProfitPrice = target,
        Confidence = 72m,
        Reason = reason
    };

    private static TelegramNotifierOptions Options(bool enabled = true, int dedupeMinutes = 60) => new()
    {
        Enabled = enabled,
        BotToken = "123456:TEST-TOKEN",
        ChatId = "-1001234567890",
        DeduplicationMinutes = dedupeMinutes,
        BaseAddress = new Uri("https://telegram.invalid/")
    };

    /// <summary>Captures requests instead of performing them, so no test touches the network.</summary>
    private sealed class StubHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"ok\":true}")
        : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];
        public int CallCount => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string payload = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, payload));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static TelegramSignalNotifier Notifier(
        StubHandler handler, TelegramNotifierOptions options, TimeProvider? time = null) =>
        new(options, new HttpClient(handler) { BaseAddress = options.BaseAddress }, time);

    [Test]
    public async Task Notify_PostsToSendMessageWithConfiguredChat()
    {
        var handler = new StubHandler();
        using TelegramSignalNotifier notifier = Notifier(handler, Options());

        bool delivered = await notifier.NotifyAsync(Signal());

        Assert.That(delivered, Is.True);
        Assert.That(handler.CallCount, Is.EqualTo(1));
        Assert.That(handler.Requests[0].Path, Is.EqualTo("/bot123456:TEST-TOKEN/sendMessage"));
        using JsonDocument sent = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.That(sent.RootElement.GetProperty("chat_id").GetString(), Is.EqualTo("-1001234567890"));
        Assert.That(sent.RootElement.GetProperty("text").GetString(), Does.Contain("METAL:XAU/USD"));
    }

    [Test]
    public async Task Notify_WhenDisabled_MakesNoHttpCall()
    {
        var handler = new StubHandler();
        using TelegramSignalNotifier notifier = Notifier(handler, Options(enabled: false));

        bool delivered = await notifier.NotifyAsync(Signal());

        Assert.That(delivered, Is.False);
        Assert.That(handler.CallCount, Is.Zero, "a disabled notifier must not touch the network");
    }

    [Test]
    public async Task Notify_WhenTelegramRejects_ReturnsFalseAndDoesNotThrow()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"ok\":false,\"description\":\"chat not found\"}");
        string? reported = null;
        using var notifier = new TelegramSignalNotifier(
            Options(), new HttpClient(handler) { BaseAddress = Options().BaseAddress })
        {
            OnError = (message, _) => reported = message
        };

        bool delivered = await notifier.NotifyAsync(Signal());

        Assert.That(delivered, Is.False);
        Assert.That(reported, Does.Contain("chat not found"));
    }

    [Test]
    public async Task Notify_WhenTransportFails_ReturnsFalseAndDoesNotThrow()
    {
        using var notifier = new TelegramSignalNotifier(
            Options(), new HttpClient(new ThrowingHandler()) { BaseAddress = Options().BaseAddress });

        bool delivered = await notifier.NotifyAsync(Signal());

        Assert.That(delivered, Is.False, "a transport failure must be contained, not propagated to the agent");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }

    [Test]
    public async Task Notify_SuppressesTheSameSignalInsideTheWindow()
    {
        var handler = new StubHandler();
        using TelegramSignalNotifier notifier = Notifier(handler, Options());

        Assert.That(await notifier.NotifyAsync(Signal()), Is.True);
        Assert.That(await notifier.NotifyAsync(Signal()), Is.False, "the same candle must not alert twice");
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Notify_SendsAgainOnceTheWindowExpires()
    {
        var handler = new StubHandler();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 27, 2, 5, 0, TimeSpan.Zero));
        using TelegramSignalNotifier notifier = Notifier(handler, Options(dedupeMinutes: 30), time);

        Assert.That(await notifier.NotifyAsync(Signal()), Is.True);
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.That(await notifier.NotifyAsync(Signal()), Is.True);
        Assert.That(handler.CallCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Notify_AfterAFailedSend_AllowsARetryOfTheSameSignal()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{\"ok\":false}");
        using TelegramSignalNotifier notifier = Notifier(handler, Options());

        Assert.That(await notifier.NotifyAsync(Signal()), Is.False);
        // Deduplication must not swallow the retry: nothing was delivered the first time.
        Assert.That(await notifier.NotifyAsync(Signal()), Is.False);
        Assert.That(handler.CallCount, Is.EqualTo(2), "a failed send must release its dedupe slot");
    }

    [Test]
    public async Task Notify_DistinctCandlesEachAlert()
    {
        var handler = new StubHandler();
        using TelegramSignalNotifier notifier = Notifier(handler, Options());

        await notifier.NotifyAsync(Signal(at: new DateTimeOffset(2026, 8, 27, 2, 5, 0, TimeSpan.Zero)));
        await notifier.NotifyAsync(Signal(at: new DateTimeOffset(2026, 8, 27, 2, 10, 0, TimeSpan.Zero)));

        Assert.That(handler.CallCount, Is.EqualTo(2));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Test]
    public void Validate_EnabledWithoutCredentials_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TelegramNotifierOptions { Enabled = true }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TelegramNotifierOptions { Enabled = true, BotToken = "t" }.Validate());
    }

    [Test]
    public void Validate_DisabledWithoutCredentials_IsAllowed()
    {
        // The normal state on a dev machine and in every backtest.
        Assert.DoesNotThrow(() => new TelegramNotifierOptions().Validate());
    }

    [Test]
    public async Task Construct_WithAnAlreadyUsedHttpClient_DoesNotThrow()
    {
        // Regression: the probe CLI reuses one HttpClient for getMe and then the send. Setting
        // any property on an HttpClient after its first request throws, so the notifier must not
        // reconfigure a client it did not create.
        var handler = new StubHandler();
        var shared = new HttpClient(handler);
        await shared.GetAsync("https://telegram.invalid/warmup");

        TelegramSignalNotifier? notifier = null;
        Assert.DoesNotThrow(() => notifier = new TelegramSignalNotifier(Options(), shared));
        notifier?.Dispose();
        shared.Dispose();
    }

    [Test]
    public async Task Notify_ThroughAnAlreadyUsedHttpClient_StillDelivers()
    {
        var handler = new StubHandler();
        var shared = new HttpClient(handler);
        await shared.GetAsync("https://telegram.invalid/warmup");
        using var notifier = new TelegramSignalNotifier(Options(), shared);

        Assert.That(await notifier.NotifyAsync(Signal()), Is.True);
        Assert.That(handler.Requests[^1].Path, Is.EqualTo("/bot123456:TEST-TOKEN/sendMessage"));
        shared.Dispose();
    }

    [Test]
    public void Construct_DisabledWithNoCredentials_DoesNotThrow()
    {
        // The realistic dev/backtest configuration: wiring present, nothing configured. The
        // endpoint is still built at construction, so an empty token must not break it.
        Assert.DoesNotThrow(() =>
        {
            using var notifier = new TelegramSignalNotifier(new TelegramNotifierOptions());
        });
    }

    [Test]
    public async Task NullNotifier_ReportsNotDelivered()
    {
        Assert.That(await NullSignalNotifier.Instance.NotifyAsync(Signal()), Is.False);
    }
}
