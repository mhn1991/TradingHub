namespace TradingHub.Brokers.Binance.Contracts;

internal sealed record BinanceAccountDto
{
    public long UpdateTime { get; init; }

    public IReadOnlyList<BinanceBalanceDto> Balances { get; init; } = [];
}

internal sealed record BinanceBalanceDto
{
    public string? Asset { get; init; }

    public string? Free { get; init; }

    public string? Locked { get; init; }
}
