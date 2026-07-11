using TradingHub.Brokers.Internal;

namespace TradingHub.Brokers.Binance.Http;

internal sealed class BinanceRestClient
{
    private readonly HttpClient _httpClient;
    private readonly BinanceBrokerOptions _options;

    public BinanceRestClient(HttpClient httpClient, BinanceBrokerOptions options)
    {
        BrokerHttpPolicy.EnsureExactEndpoint(httpClient, options.RestEndpoint, "Binance");
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<HttpResponseMessage> SendSignedAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        var query = BinanceRequestSigner.CreateSignedQuery(
            parameters,
            _options.Credentials.SecretKey);
        using var request = new HttpRequestMessage(method, $"{relativePath}?{query}");
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _options.Credentials.ApiKey);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
