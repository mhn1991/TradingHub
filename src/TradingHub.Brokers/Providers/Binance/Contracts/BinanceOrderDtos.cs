namespace TradingHub.Brokers.Binance.Contracts;

internal sealed record BinanceOrderResponseDto
{
    public string? Symbol { get; init; }

    public long OrderId { get; init; }

    public string? ClientOrderId { get; init; }

    public long TransactTime { get; init; }

    public string? Price { get; init; }

    public string? OrigQty { get; init; }

    public string? ExecutedQty { get; init; }

    public string? CummulativeQuoteQty { get; init; }

    public string? Status { get; init; }

    public string? TimeInForce { get; init; }

    public string? Type { get; init; }

    public string? Side { get; init; }

    public IReadOnlyList<BinanceFillDto> Fills { get; init; } = [];
}

internal sealed record BinanceFillDto
{
    public string? Price { get; init; }

    public string? Qty { get; init; }

    public string? Commission { get; init; }

    public string? CommissionAsset { get; init; }

    public long TradeId { get; init; }
}

internal sealed record BinanceErrorDto
{
    public int Code { get; init; }

    public string? Msg { get; init; }
}
