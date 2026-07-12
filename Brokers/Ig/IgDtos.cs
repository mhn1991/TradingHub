using System.Text.Json.Serialization;

namespace Brokers.Ig;

internal sealed record IgLoginRequest(
    string Identifier,
    string Password,
    bool EncryptedPassword);

internal sealed record IgLoginResult(
    IgLoginResponse Response,
    IgSessionTokens Tokens);

internal sealed record IgLoginResponse
{
    [JsonPropertyName("currentAccountId")]
    public required string CurrentAccountId { get; init; }

    [JsonPropertyName("lightstreamerEndpoint")]
    public string? LightstreamerEndpoint { get; init; }
}

internal sealed record IgSwitchAccountRequest(
    string AccountId,
    bool DefaultAccount);

internal sealed record IgSwitchAccountResult(
    IgSwitchAccountResponse Response,
    IgSessionTokens? Tokens);

internal sealed record IgSwitchAccountResponse
{
    [JsonPropertyName("dealingEnabled")]
    public bool DealingEnabled { get; init; }
}

internal sealed record IgAccountDto
{
    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("accountType")]
    public string? AccountType { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("balance")]
    public IgAccountBalance? Balance { get; init; }
}

internal sealed record IgAccountBalance
{
    [JsonPropertyName("available")]
    public decimal? Available { get; init; }

    [JsonPropertyName("balance")]
    public decimal? Balance { get; init; }

    [JsonPropertyName("deposit")]
    public decimal? Deposit { get; init; }

    [JsonPropertyName("profitLoss")]
    public decimal? ProfitLoss { get; init; }
}

internal sealed record IgPricesResponse
{
    [JsonPropertyName("prices")]
    public IReadOnlyList<IgHistoricalPrice> Prices { get; init; } = [];
}

internal sealed record IgHistoricalPrice
{
    [JsonPropertyName("snapshotTime")]
    public string? SnapshotTime { get; init; }

    [JsonPropertyName("snapshotTimeUTC")]
    public string? SnapshotTimeUtc { get; init; }

    [JsonPropertyName("openPrice")]
    public IgPricePoint? OpenPrice { get; init; }

    [JsonPropertyName("highPrice")]
    public IgPricePoint? HighPrice { get; init; }

    [JsonPropertyName("lowPrice")]
    public IgPricePoint? LowPrice { get; init; }

    [JsonPropertyName("closePrice")]
    public IgPricePoint? ClosePrice { get; init; }

    [JsonPropertyName("lastTradedVolume")]
    public decimal? LastTradedVolume { get; init; }
}

internal sealed record IgPricePoint
{
    [JsonPropertyName("bid")]
    public decimal? Bid { get; init; }

    [JsonPropertyName("ask")]
    public decimal? Ask { get; init; }

    [JsonPropertyName("lastTraded")]
    public decimal? LastTraded { get; init; }
}

internal sealed record IgPositionsResponse
{
    [JsonPropertyName("positions")]
    public IReadOnlyList<IgPositionItem> Positions { get; init; } = [];
}

internal sealed record IgPositionItem
{
    [JsonPropertyName("market")]
    public IgMarketData? Market { get; init; }

    [JsonPropertyName("position")]
    public IgPositionData? Position { get; init; }
}

internal sealed record IgPositionData
{
    [JsonPropertyName("dealId")]
    public string? DealId { get; init; }

    [JsonPropertyName("dealReference")]
    public string? DealReference { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("size")]
    public decimal Size { get; init; }

    [JsonPropertyName("level")]
    public decimal? Level { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

internal sealed record IgWorkingOrdersResponse
{
    [JsonPropertyName("workingOrders")]
    public IReadOnlyList<IgWorkingOrder> WorkingOrders { get; init; } = [];
}

internal sealed record IgWorkingOrder
{
    [JsonPropertyName("marketData")]
    public IgMarketData? MarketData { get; init; }

    [JsonPropertyName("workingOrderData")]
    public IgWorkingOrderData? WorkingOrderData { get; init; }
}

internal sealed record IgMarketData
{
    [JsonPropertyName("epic")]
    public string? Epic { get; init; }
}

internal sealed record IgWorkingOrderData
{
    [JsonPropertyName("dealId")]
    public string? DealId { get; init; }

    [JsonPropertyName("dealReference")]
    public string? DealReference { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("orderType")]
    public string? OrderType { get; init; }

    [JsonPropertyName("orderSize")]
    public decimal? Size { get; init; }

    [JsonPropertyName("orderLevel")]
    public decimal? Level { get; init; }

    [JsonPropertyName("createdDate")]
    public string? CreatedDate { get; init; }

    [JsonPropertyName("createdDateUTC")]
    public string? CreatedDateUtc { get; init; }
}
