using System.Text.Json;

namespace TradingHub.Brokers.Binance.Http;

internal static class BinanceJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
