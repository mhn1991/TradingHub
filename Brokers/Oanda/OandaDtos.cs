using System.Text.Json.Serialization;

namespace Brokers.Oanda;

internal sealed record OandaCandlesResponse
{
    [JsonPropertyName("candles")]
    public IReadOnlyList<OandaCandle> Candles { get; init; } = [];
}

internal sealed record OandaCandle
{
    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("volume")]
    public decimal Volume { get; init; }

    [JsonPropertyName("time")]
    public required string Time { get; init; }

    [JsonPropertyName("mid")]
    public OandaPrice? Mid { get; init; }

    [JsonPropertyName("bid")]
    public OandaPrice? Bid { get; init; }

    [JsonPropertyName("ask")]
    public OandaPrice? Ask { get; init; }
}

internal sealed record OandaPrice
{
    [JsonPropertyName("o")]
    public string? Open { get; init; }

    [JsonPropertyName("h")]
    public string? High { get; init; }

    [JsonPropertyName("l")]
    public string? Low { get; init; }

    [JsonPropertyName("c")]
    public string? Close { get; init; }
}

internal sealed record OandaAccountSummaryResponse
{
    [JsonPropertyName("account")]
    public required OandaAccountSummary Account { get; init; }
}

internal sealed record OandaAccountSummary
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("balance")]
    public string? Balance { get; init; }

    [JsonPropertyName("marginAvailable")]
    public string? MarginAvailable { get; init; }

    [JsonPropertyName("marginUsed")]
    public string? MarginUsed { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public string? UnrealizedPl { get; init; }

    [JsonPropertyName("tradingDisabled")]
    public bool? TradingDisabled { get; init; }
}

internal sealed record OandaPendingOrdersResponse
{
    [JsonPropertyName("orders")]
    public IReadOnlyList<OandaOrder> Orders { get; init; } = [];
}

internal sealed record OandaOrder
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("createTime")]
    public string? CreateTime { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("instrument")]
    public string? Instrument { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("clientExtensions")]
    public OandaClientExtensions? ClientExtensions { get; init; }
}

internal sealed record OandaClientExtensions
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

internal sealed record OandaOpenPositionsResponse
{
    [JsonPropertyName("positions")]
    public IReadOnlyList<OandaPosition> Positions { get; init; } = [];
}

internal sealed record OandaPosition
{
    [JsonPropertyName("instrument")]
    public required string Instrument { get; init; }

    [JsonPropertyName("long")]
    public OandaPositionSide? Long { get; init; }

    [JsonPropertyName("short")]
    public OandaPositionSide? Short { get; init; }
}

internal sealed record OandaPositionSide
{
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("averagePrice")]
    public string? AveragePrice { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public string? UnrealizedPl { get; init; }
}
