namespace ChartAnnotator.Confluence;

public enum ConfluenceDirection
{
    Bullish,
    Bearish
}

/// <summary>
/// Explicit relationship between a price-derived supply/demand zone and a price-inferred
/// liquidity pool/sweep (blueprint §8: "Implement explicit relationships, not hidden score
/// mixing"). <see cref="QualityScore"/> is not a win probability.
/// </summary>
public sealed record SupplyDemandLiquidityConfluence
{
    public required Guid ConfluenceId { get; init; }
    public required Guid ZoneId { get; init; }
    public required Guid? PoolId { get; init; }
    public required Guid? SweepId { get; init; }

    public required ConfluenceDirection Direction { get; init; }
    public required bool ZoneWasFresh { get; init; }
    public required bool LiquidityWasSwept { get; init; }
    public required bool DisplacementConfirmed { get; init; }
    public required bool StructureShiftConfirmed { get; init; }

    public required decimal DistanceBetweenZoneAndPoolAtr { get; init; }
    public required decimal QualityScore { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
}

public sealed record SupplyDemandLiquidityConfluenceSnapshot
{
    public required bool IsEnabled { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyList<SupplyDemandLiquidityConfluence> Relationships { get; init; }

    public static SupplyDemandLiquidityConfluenceSnapshot Disabled { get; } = new()
    {
        IsEnabled = false,
        SnapshotVersion = 0,
        AvailableAt = DateTimeOffset.MinValue,
        Relationships = []
    };
}

public sealed record SupplyDemandLiquidityConfluenceOptions
{
    public bool Enabled { get; init; }
    public decimal MaximumDistanceAtr { get; init; } = 0.50m;
    public int MaximumRetainedRelationships { get; init; } = 128;

    public void Validate()
    {
        if (MaximumDistanceAtr < 0m || MaximumRetainedRelationships < 1)
            throw new ArgumentOutOfRangeException(nameof(SupplyDemandLiquidityConfluenceOptions));
    }
}
