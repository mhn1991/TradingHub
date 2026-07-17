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

    [JsonPropertyName("lastTransactionID")]
    public string? LastTransactionId { get; init; }
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

    [JsonPropertyName("NAV")]
    public string? Nav { get; init; }

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

    [JsonPropertyName("clientOrderID")]
    public string? ClientOrderId { get; init; }

    [JsonPropertyName("tradeID")]
    public string? TradeId { get; init; }
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


internal sealed record OandaOpenTradesResponse
{
    [JsonPropertyName("trades")]
    public IReadOnlyList<OandaTrade> Trades { get; init; } = [];

    [JsonPropertyName("lastTransactionID")]
    public string? LastTransactionId { get; init; }
}

internal sealed record OandaTrade
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("instrument")]
    public required string Instrument { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("openTime")]
    public string? OpenTime { get; init; }

    [JsonPropertyName("initialUnits")]
    public string? InitialUnits { get; init; }

    [JsonPropertyName("currentUnits")]
    public string? CurrentUnits { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public string? UnrealizedPl { get; init; }

    [JsonPropertyName("clientExtensions")]
    public OandaClientExtensions? ClientExtensions { get; init; }

    [JsonPropertyName("stopLossOrder")]
    public OandaLinkedTradeOrder? StopLossOrder { get; init; }

    [JsonPropertyName("takeProfitOrder")]
    public OandaLinkedTradeOrder? TakeProfitOrder { get; init; }
}

internal sealed record OandaLinkedTradeOrder
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }
}

internal sealed record OandaInstrumentsResponse
{
    [JsonPropertyName("instruments")]
    public IReadOnlyList<OandaInstrument> Instruments { get; init; } = [];
}

internal sealed record OandaInstrument
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("pipLocation")]
    public int PipLocation { get; init; }

    [JsonPropertyName("displayPrecision")]
    public int DisplayPrecision { get; init; }

    [JsonPropertyName("tradeUnitsPrecision")]
    public int TradeUnitsPrecision { get; init; }

    [JsonPropertyName("minimumTradeSize")]
    public string? MinimumTradeSize { get; init; }

    [JsonPropertyName("maximumOrderUnits")]
    public string? MaximumOrderUnits { get; init; }

    [JsonPropertyName("marginRate")]
    public string? MarginRate { get; init; }
}

internal sealed record OandaCreateOrderEnvelope
{
    [JsonPropertyName("order")]
    public required OandaCreateOrderRequest Order { get; init; }
}

internal sealed record OandaCreateOrderRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("instrument")]
    public required string Instrument { get; init; }

    [JsonPropertyName("units")]
    public required string Units { get; init; }

    [JsonPropertyName("timeInForce")]
    public required string TimeInForce { get; init; }

    [JsonPropertyName("positionFill")]
    public string PositionFill { get; init; } = "DEFAULT";

    [JsonPropertyName("price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Price { get; init; }

    [JsonPropertyName("gtdTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GtdTime { get; init; }

    [JsonPropertyName("clientExtensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaClientExtensions? ClientExtensions { get; init; }

    [JsonPropertyName("tradeClientExtensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaClientExtensions? TradeClientExtensions { get; init; }

    [JsonPropertyName("stopLossOnFill")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaDependentOrderRequest? StopLossOnFill { get; init; }

    [JsonPropertyName("takeProfitOnFill")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaDependentOrderRequest? TakeProfitOnFill { get; init; }
}

internal sealed record OandaDependentOrderRequest
{
    [JsonPropertyName("price")]
    public required string Price { get; init; }

    [JsonPropertyName("timeInForce")]
    public string TimeInForce { get; init; } = "GTC";
}

internal sealed record OandaTradeCloseRequest
{
    [JsonPropertyName("units")]
    public required string Units { get; init; }
}

internal sealed record OandaTradeDependentOrdersRequest
{
    [JsonPropertyName("stopLoss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaDependentOrderRequest? StopLoss { get; init; }

    [JsonPropertyName("takeProfit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OandaDependentOrderRequest? TakeProfit { get; init; }
}

internal sealed record OandaTradeDependentOrdersResponse
{
    [JsonPropertyName("stopLossOrderTransaction")]
    public OandaTransaction? StopLossOrderTransaction { get; init; }

    [JsonPropertyName("stopLossOrderCancelTransaction")]
    public OandaTransaction? StopLossOrderCancelTransaction { get; init; }

    [JsonPropertyName("takeProfitOrderTransaction")]
    public OandaTransaction? TakeProfitOrderTransaction { get; init; }

    [JsonPropertyName("takeProfitOrderCancelTransaction")]
    public OandaTransaction? TakeProfitOrderCancelTransaction { get; init; }

    [JsonPropertyName("lastTransactionID")]
    public string? LastTransactionId { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

internal sealed record OandaOrderMutationResponse
{
    [JsonPropertyName("orderCreateTransaction")]
    public OandaTransaction? OrderCreateTransaction { get; init; }

    [JsonPropertyName("orderFillTransaction")]
    public OandaTransaction? OrderFillTransaction { get; init; }

    [JsonPropertyName("orderCancelTransaction")]
    public OandaTransaction? OrderCancelTransaction { get; init; }

    [JsonPropertyName("orderRejectTransaction")]
    public OandaTransaction? OrderRejectTransaction { get; init; }

    [JsonPropertyName("lastTransactionID")]
    public string? LastTransactionId { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

internal sealed record OandaTradeSummary
{
    [JsonPropertyName("tradeID")]
    public string? TradeId { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("realizedPL")]
    public string? RealizedPl { get; init; }
}

internal sealed record OandaTransactionsResponse
{
    [JsonPropertyName("transactions")]
    public IReadOnlyList<OandaTransaction> Transactions { get; init; } = [];

    [JsonPropertyName("lastTransactionID")]
    public required string LastTransactionId { get; init; }
}

internal sealed record OandaTransaction
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("orderID")]
    public string? OrderId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("instrument")]
    public string? Instrument { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("clientExtensions")]
    public OandaClientExtensions? ClientExtensions { get; init; }

    [JsonPropertyName("clientOrderID")]
    public string? ClientOrderId { get; init; }

    [JsonPropertyName("tradeOpened")]
    public OandaTradeSummary? TradeOpened { get; init; }

    [JsonPropertyName("tradeReduced")]
    public OandaTradeSummary? TradeReduced { get; init; }

    [JsonPropertyName("tradesClosed")]
    public IReadOnlyList<OandaTradeSummary> TradesClosed { get; init; } = [];

    [JsonPropertyName("tradeID")]
    public string? TradeId { get; init; }
}

internal sealed record OandaPricingStreamMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("instrument")]
    public string? Instrument { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("tradeable")]
    public bool Tradeable { get; init; }

    [JsonPropertyName("bids")]
    public IReadOnlyList<OandaPriceBucket> Bids { get; init; } = [];

    [JsonPropertyName("asks")]
    public IReadOnlyList<OandaPriceBucket> Asks { get; init; } = [];
}

internal sealed record OandaPriceBucket
{
    [JsonPropertyName("price")]
    public string? Price { get; init; }
}
