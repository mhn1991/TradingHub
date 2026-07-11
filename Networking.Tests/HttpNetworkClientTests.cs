using System.Net;
using System.Text;
using Networking.Tests.TestDoubles;

namespace Networking.Tests;

public sealed class HttpNetworkClientTests
{
    [TestCase(HttpStatusCode.OK, HttpResponseCategory.Success, true)]
    [TestCase(HttpStatusCode.BadRequest, HttpResponseCategory.ClientError, false)]
    [TestCase(HttpStatusCode.InternalServerError, HttpResponseCategory.ServerError, false)]
    public async Task SendAsync_ReturnsEveryHttpStatusWithoutThrowing(
        HttpStatusCode statusCode,
        HttpResponseCategory expectedCategory,
        bool expectedSuccess)
    {
        const string responseBody = "{\"message\":\"broker response\"}";
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            response.Headers.TryAddWithoutValidation("X-Request-Id", "request-1");
            return Task.FromResult(response);
        }));
        await using var client = new HttpNetworkClient(httpClient);

        HttpNetworkResponse response = await client.SendAsync(CreateGetRequest());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(statusCode));
            Assert.That(response.Category, Is.EqualTo(expectedCategory));
            Assert.That(response.IsSuccessStatusCode, Is.EqualTo(expectedSuccess));
            Assert.That(response.GetBodyAsString(), Is.EqualTo(responseBody));
            Assert.That(response.TryGetHeader("x-request-id", out IReadOnlyList<string>? values), Is.True);
            Assert.That(values, Is.EqualTo(new[] { "request-1" }));
        });
    }

    [Test]
    public async Task SendAsync_ReturnsRedirectAndResolvesRelativeLocation()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                RequestMessage = request
            };
            response.Headers.Location = new Uri("../v2/orders", UriKind.Relative);
            return Task.FromResult(response);
        }));
        await using var client = new HttpNetworkClient(httpClient);
        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            new Uri("https://broker.example/v1/orders"),
            HttpMethod.Get);

        HttpNetworkResponse response = await client.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.Category, Is.EqualTo(HttpResponseCategory.Redirect));
            Assert.That(response.RedirectLocation, Is.EqualTo(new Uri("https://broker.example/v2/orders")));
        });
    }

    [Test]
    public async Task SendAsync_ForwardsMethodHeadersAndBody()
    {
        string? receivedMethod = null;
        string? receivedAuthorization = null;
        string? receivedBody = null;
        string? receivedContentType = null;

        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            receivedMethod = request.Method.Method;
            receivedAuthorization = request.Headers.Authorization?.ToString();
            receivedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            receivedContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.Created) { RequestMessage = request };
        }));
        await using var client = new HttpNetworkClient(httpClient);
        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            new Uri("https://broker.example/v1/orders"),
            HttpMethod.Post,
            [new HttpNetworkHeader("Authorization", "Bearer secret")],
            Encoding.UTF8.GetBytes("{\"symbol\":\"GBP_USD\"}"),
            "application/json");

        HttpNetworkResponse response = await client.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(receivedMethod, Is.EqualTo("POST"));
            Assert.That(receivedAuthorization, Is.EqualTo("Bearer secret"));
            Assert.That(receivedBody, Is.EqualTo("{\"symbol\":\"GBP_USD\"}"));
            Assert.That(receivedContentType, Is.EqualTo("application/json"));
        });
    }

    [Test]
    public async Task SendAsync_CallerCancellationPropagates()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await using var client = new HttpNetworkClient(httpClient);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await client.SendAsync(CreateGetRequest(), source.Token));
    }

    [Test]
    public async Task SendAsync_RequestTimeoutHasDistinctException()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await using var client = new HttpNetworkClient(httpClient);
        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            new Uri("https://broker.example/v1/orders"),
            HttpMethod.Get,
            timeout: TimeSpan.FromMilliseconds(50));

        NetworkTimeoutException? exception = Assert.ThrowsAsync<NetworkTimeoutException>(
            async () => await client.SendAsync(request));

        Assert.That(exception!.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(50)));
    }

    [Test]
    public async Task SendAsync_RejectsOversizedResponse()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(new byte[5])
            })));
        await using var client = new HttpNetworkClient(
            httpClient,
            new HttpNetworkClientOptions { MaxResponseBodyBytes = 4 });

        NetworkResponseTooLargeException? exception =
            Assert.ThrowsAsync<NetworkResponseTooLargeException>(
                async () => await client.SendAsync(CreateGetRequest()));

        Assert.That(exception!.MaximumBodyBytes, Is.EqualTo(4));
    }

    [Test]
    public async Task SendAsync_WrapsTransportFailure()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(static (_, _) =>
            throw new HttpRequestException("DNS failed")));
        await using var client = new HttpNetworkClient(httpClient);

        NetworkTransportException? exception = Assert.ThrowsAsync<NetworkTransportException>(
            async () => await client.SendAsync(CreateGetRequest()));

        Assert.That(exception!.InnerException, Is.TypeOf<HttpRequestException>());
    }

    private static HttpNetworkRequest CreateGetRequest() => new(
        NetworkProtocol.Https,
        new Uri("https://broker.example/v1/instruments"),
        HttpMethod.Get);
}
