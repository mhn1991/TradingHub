using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brokers.Binance;

internal sealed record BinanceKlineRow(
    long OpenTime,
    string Open,
    string High,
    string Low,
    string Close,
    string Volume,
    long CloseTime)
{
    public static BinanceKlineRow FromJson(JsonElement[] row)
    {
        if (row.Length < 7)
        {
            throw new JsonException("Binance kline row contained fewer than seven fields.");
        }

        return new BinanceKlineRow(
            row[0].GetInt64(),
            GetRequiredString(row[1], "open"),
            GetRequiredString(row[2], "high"),
            GetRequiredString(row[3], "low"),
            GetRequiredString(row[4], "close"),
            GetRequiredString(row[5], "volume"),
            row[6].GetInt64());
    }

    private static string GetRequiredString(JsonElement element, string fieldName) =>
        element.GetString()
        ?? throw new JsonException($"Binance kline field '{fieldName}' was null.");
}

internal sealed record BinanceAccountResponse
{
    [JsonPropertyName("canTrade")]
    public bool CanTrade { get; init; }

    [JsonPropertyName("accountType")]
    public string? AccountType { get; init; }

    [JsonPropertyName("balances")]
    public IReadOnlyList<BinanceBalance> Balances { get; init; } = [];
}

internal sealed record BinanceBalance
{
    [JsonPropertyName("asset")]
    public required string Asset { get; init; }

    [JsonPropertyName("free")]
    public string? Free { get; init; }

    [JsonPropertyName("locked")]
    public string? Locked { get; init; }
}

internal sealed record BinanceOrderDto
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("orderId")]
    public long OrderId { get; init; }

    [JsonPropertyName("clientOrderId")]
    public string? ClientOrderId { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("origQty")]
    public string? OriginalQuantity { get; init; }

    [JsonPropertyName("executedQty")]
    public string? ExecutedQuantity { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("time")]
    public long Time { get; init; }
}

internal sealed record BinanceCommissionResponse
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("standardCommission")]
    public BinanceCommissionRates? StandardCommission { get; init; }

    [JsonPropertyName("discount")]
    public BinanceCommissionDiscount? Discount { get; init; }
}

internal sealed record BinanceCommissionRates
{
    [JsonPropertyName("maker")]
    public string? Maker { get; init; }

    [JsonPropertyName("taker")]
    public string? Taker { get; init; }

    [JsonPropertyName("buyer")]
    public string? Buyer { get; init; }

    [JsonPropertyName("seller")]
    public string? Seller { get; init; }
}

internal sealed record BinanceCommissionDiscount
{
    [JsonPropertyName("enabledForAccount")]
    public bool EnabledForAccount { get; init; }

    [JsonPropertyName("enabledForSymbol")]
    public bool EnabledForSymbol { get; init; }

    [JsonPropertyName("discountAsset")]
    public string? DiscountAsset { get; init; }

    [JsonPropertyName("discount")]
    public string? Discount { get; init; }
}
