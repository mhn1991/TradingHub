using System.Net;
using Networking.Abstractions;
using Networking.Http;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class HttpTransportTests
{
    private static readonly TransportId TransportId = new("test.http");

    [Test]
    public void TransportTimeout_IsReportedSeparatelyFromCallerCancellation()
    {
        using var client = new HttpClient(new DelegateHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        using var transport = new HttpTransport(
            TransportId,
            client,
            defaultTimeout: TimeSpan.FromMilliseconds(50));

        Assert.That(
            async () => await transport.SendAsync(new StringCommand(TransportId)),
            Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task IdempotentCommand_RetriesTransientStatus()
    {
        int calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            HttpStatusCode status = calls == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(calls == 1 ? "retry" : "done")
            });
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        using var transport = new HttpTransport(TransportId, client);

        string response = await transport.SendAsync(
            new StringCommand(TransportId, IsIdempotent: true, MaxTransientRetries: 1));

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.EqualTo("done"));
            Assert.That(calls, Is.EqualTo(2));
        });
    }

    [Test]
    public void IdempotentCommand_DoesNotRetryNonTransientStatus()
    {
        int calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        using var transport = new HttpTransport(TransportId, client);

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await transport.SendAsync(
                    new StringCommand(TransportId, IsIdempotent: true, MaxTransientRetries: 2)),
                Throws.TypeOf<HttpRequestException>());
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    private sealed record StringCommand(
        TransportId TransportId,
        bool IsIdempotent = false,
        int MaxTransientRetries = 0) : IHttpCommand<string>
    {
        public TimeSpan? Timeout => null;
        public bool EnsureSuccessStatusCode => true;

        public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, "resource");

        public async ValueTask<string> ReadResponseAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken) =>
            await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
