namespace Brokers.Models;

public sealed record AssetBalance(
    string Asset,
    decimal Free,
    decimal Locked);

public sealed record AccountSnapshot
{
    public required string AccountId { get; init; }
    public string? AccountType { get; init; }
    public string? Currency { get; init; }
    public decimal? Balance { get; init; }
    public decimal? Available { get; init; }
    public decimal? MarginUsed { get; init; }
    public decimal? UnrealizedProfitLoss { get; init; }
    /// <summary>Broker-authoritative net asset value/equity when the venue provides it.</summary>
    public decimal? Equity { get; init; }

    public string? LastTransactionId { get; init; }
    public bool? CanTrade { get; init; }
    public IReadOnlyList<AssetBalance> AssetBalances { get; init; } = [];
}
