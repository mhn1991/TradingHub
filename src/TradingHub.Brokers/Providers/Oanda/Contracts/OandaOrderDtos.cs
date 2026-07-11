using System.Text.Json.Serialization;

namespace TradingHub.Brokers.Oanda.Contracts;

internal sealed record OandaCreateOrderEnvelopeDto
{
    [JsonPropertyName("order")]
    public required OandaCreateOrderDto Order { get; init; }
}

internal sealed record OandaCreateOrderDto
{
    [JsonPropertyName("units")]
    public required string Units { get; init; }

    [JsonPropertyName("instrument")]
    public required string Instrument { get; init; }

    [JsonPropertyName("timeInForce")]
    public required string TimeInForce { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("positionFill")]
    public string PositionFill { get; init; } = "DEFAULT";

    [JsonPropertyName("price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Price { get; init; }

    [JsonPropertyName("clientExtensions")]
    public required OandaClientExtensionsDto ClientExtensions { get; init; }
}

internal sealed record OandaClientExtensionsDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("tag")]
    public required string Tag { get; init; }
}

internal sealed record OandaOrderResponseDto
{
    public OandaTransactionDto? OrderCreateTransaction { get; init; }

    public OandaTransactionDto? OrderFillTransaction { get; init; }

    public OandaTransactionDto? OrderCancelTransaction { get; init; }

    public OandaTransactionDto? OrderRejectTransaction { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

internal sealed record OandaTransactionDto
{
    public string? Id { get; init; }

    public string? OrderId { get; init; }

    public string? Instrument { get; init; }

    public string? Units { get; init; }

    public string? Price { get; init; }

    public string? Time { get; init; }

    public string? Reason { get; init; }

    public string? Commission { get; init; }
}

internal sealed record OandaCancelResponseDto
{
    public OandaTransactionDto? OrderCancelTransaction { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
