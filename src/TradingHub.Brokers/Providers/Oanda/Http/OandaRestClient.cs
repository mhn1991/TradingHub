using System.Net.Http.Headers;
using System.Net.Http.Json;
using TradingHub.Brokers.Internal;

namespace TradingHub.Brokers.Oanda.Http;

internal sealed class OandaRestClient
{
    private readonly HttpClient _httpClient;
    private readonly OandaBrokerOptions _options;

    public OandaRestClient(HttpClient httpClient, OandaBrokerOptions options)
    {
        BrokerHttpPolicy.EnsureExactEndpoint(httpClient, options.RestEndpoint, "OANDA");
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<HttpResponseMessage> SendAsync<TBody>(
        HttpMethod method,
        string relativePath,
        TBody? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        request.Headers.TryAddWithoutValidation("Accept-Datetime-Format", "RFC3339");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: OandaJson.SerializerOptions);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
