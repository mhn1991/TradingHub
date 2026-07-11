using System.Text.Json;

namespace TradingHub.Brokers.Oanda.Http;

internal static class OandaJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
