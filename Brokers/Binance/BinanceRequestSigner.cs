using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Brokers.Binance;

internal sealed class BinanceRequestSigner
{
    private readonly byte[] _secretKey;
    private readonly TimeProvider _timeProvider;
    private readonly long _receiveWindowMilliseconds;

    public BinanceRequestSigner(
        string apiKey,
        string secretKey,
        TimeSpan receiveWindow,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (receiveWindow <= TimeSpan.Zero || receiveWindow > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(receiveWindow),
                "Binance receive window must be greater than zero and no more than 60 seconds.");
        }

        ApiKey = apiKey;
        _secretKey = Encoding.UTF8.GetBytes(secretKey);
        _timeProvider = timeProvider;
        _receiveWindowMilliseconds = checked((long)receiveWindow.TotalMilliseconds);
    }

    public string ApiKey { get; }

    public string CreateSignedQuery(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var queryParts = parameters
            .Where(parameter => parameter.Value is not null)
            .Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}")
            .ToList();

        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        queryParts.Add($"recvWindow={_receiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        queryParts.Add($"timestamp={timestamp.ToString(CultureInfo.InvariantCulture)}");

        string query = string.Join('&', queryParts);
        byte[] signatureBytes = HMACSHA256.HashData(_secretKey, Encoding.UTF8.GetBytes(query));
        string signature = Convert.ToHexString(signatureBytes).ToLowerInvariant();
        return $"{query}&signature={signature}";
    }
}
