using System.Security.Cryptography;
using System.Text;

namespace TradingHub.Brokers.Binance.Http;

internal static class BinanceRequestSigner
{
    public static string CreateSignedQuery(
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        string secretKey)
    {
        var query = string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(query)));
        return $"{query}&signature={signature}";
    }
}
