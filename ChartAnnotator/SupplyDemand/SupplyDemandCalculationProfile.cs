using System.Security.Cryptography;
using System.Text;

namespace ChartAnnotator.SupplyDemand;

/// <summary>
/// Named score components recorded separately, never hidden-mixed (blueprint §5.9: "Record
/// components separately"). <c>SupplyDemandZone.QualityScore</c> is a weighted combination of
/// these, computed by the detector - the weights here only express relative emphasis, not a
/// probability.
/// </summary>
public sealed record SupplyDemandScoringWeights
{
    public decimal DepartureStrength { get; init; } = 1.0m;
    public decimal DepartureEfficiency { get; init; } = 1.0m;
    public decimal BaseCompactness { get; init; } = 1.0m;
    public decimal BaseDuration { get; init; } = 0.5m;
    public decimal StructureBreak { get; init; } = 0.5m;
    public decimal FairValueGap { get; init; } = 0.5m;
    public decimal Freshness { get; init; } = 1.0m;
    public decimal TouchPenalty { get; init; } = 1.0m;
    public decimal PenetrationPenalty { get; init; } = 1.0m;
    public decimal AgePenalty { get; init; } = 0.5m;
    public decimal HigherTimeframeAlignment { get; init; } = 0.5m;
    public decimal SupportResistanceConfluence { get; init; } = 0.5m;
    public decimal LiquidityConfluence { get; init; } = 0.5m;
    public decimal RoomToOpposingZone { get; init; } = 0.5m;

    public static SupplyDemandScoringWeights Default { get; } = new();
}

/// <summary>
/// Every setting that changes computed supply/demand analysis (blueprint §4: "Include every
/// setting that changes calculation"). Agent interpretation thresholds (how an Agent reacts to a
/// zone) belong on Agent policy, not here - this profile only affects what a zone <em>is</em>.
/// Two Agents with identical profiles share one computed set of zones.
/// </summary>
public sealed record SupplyDemandCalculationProfile
{
    public bool Enabled { get; init; }
    public int AtrPeriod { get; init; } = 14;

    // Objective base detection (§5.4)
    public int MinimumBaseCandles { get; init; } = 1;
    public int MaximumBaseCandles { get; init; } = 6;
    public decimal MaximumBaseRangeAtr { get; init; } = 1.25m;
    public decimal MaximumAverageBaseBodyAtr { get; init; } = 0.35m;
    public decimal MinimumCandleOverlapRatio { get; init; } = 0.40m;
    public decimal MaximumBaseEfficiencyRatio { get; init; } = 0.35m;

    // Objective departure detection (§5.5)
    public decimal MinimumDepartureAtr { get; init; } = 1.50m;
    public decimal MinimumDepartureEfficiency { get; init; } = 0.65m;
    public decimal MinimumDirectionalBodyRatio { get; init; } = 0.55m;
    public int MaximumDepartureCandles { get; init; } = 5;
    public bool RequireStructureBreak { get; init; }
    public bool RequireFairValueGap { get; init; }

    // Boundary / invalidation (§5.6 / §5.7)
    public ZoneBoundaryMode BoundaryMode { get; init; } = ZoneBoundaryMode.FullWickRange;
    public ZoneInvalidationMode InvalidationMode { get; init; } = ZoneInvalidationMode.CloseBeyondDistal;
    public decimal InvalidationPenetrationRatio { get; init; } = 1.0m;

    // Expiry / merge / nesting (§5.10)
    public int? MaximumZoneAgeBars { get; init; }
    public decimal MergeOverlapRatio { get; init; } = 0.50m;
    public bool AllowNestedSameSideZones { get; init; } = true;
    public int MaximumMergedSourceCount { get; init; } = 5;

    // Freshness / mitigation tracking (§5.8) and bounding (§16) - not individually named in the
    // blueprint's profile field list, but required to make "Approached"/"PartiallyMitigated" and
    // active-zone/event bounds explicit and configurable rather than hidden constants.
    public decimal ApproachDistanceAtr { get; init; } = 1.0m;
    public decimal PartialMitigationPenetrationRatio { get; init; } = 0.50m;
    public int MaximumActiveZones { get; init; } = 50;
    public int MaximumRetainedEvents { get; init; } = 200;

    public SupplyDemandScoringWeights ScoringWeights { get; init; } = SupplyDemandScoringWeights.Default;

    /// <summary>
    /// Bumped whenever the detection rules themselves change in a way that isn't captured by a
    /// field value (e.g. a new eligibility check) - hashed like every other field by
    /// <see cref="SupplyDemandCalculationProfileHasher"/>, called out separately because it
    /// exists specifically to catch rule changes that field-diffing alone would miss.
    /// </summary>
    public int RuleSetVersion { get; init; } = 1;

    public void Validate()
    {
        if (AtrPeriod < 1 ||
            MinimumBaseCandles < 1 ||
            MaximumBaseCandles < MinimumBaseCandles ||
            MaximumBaseRangeAtr <= 0m ||
            MaximumAverageBaseBodyAtr <= 0m ||
            MinimumCandleOverlapRatio is < 0m or > 1m ||
            MaximumBaseEfficiencyRatio is < 0m or > 1m ||
            MinimumDepartureAtr <= 0m ||
            MinimumDepartureEfficiency is < 0m or > 1m ||
            MinimumDirectionalBodyRatio is < 0m or > 1m ||
            MaximumDepartureCandles < 1 ||
            InvalidationPenetrationRatio <= 0m ||
            (MaximumZoneAgeBars is int age && age < 1) ||
            MergeOverlapRatio is < 0m or > 1m ||
            MaximumMergedSourceCount < 1 ||
            ApproachDistanceAtr < 0m ||
            PartialMitigationPenetrationRatio is <= 0m or > 1m ||
            MaximumActiveZones < 1 ||
            MaximumRetainedEvents < 1 ||
            !Enum.IsDefined(BoundaryMode) ||
            !Enum.IsDefined(InvalidationMode) ||
            RuleSetVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SupplyDemandCalculationProfile));
        }
    }
}

/// <summary>
/// Canonicalization/hashing for <see cref="SupplyDemandCalculationProfile"/>, mirroring
/// <c>ChartAnnotator.Engine.ChartAnnotationOptionsHasher</c>. Every field participates - a curated
/// subset risks silently omitting a field that changes computed zones, which is exactly the
/// failure mode profile separation exists to prevent (blueprint: "Different analysis profiles get
/// separate snapshots").
/// </summary>
public static class SupplyDemandCalculationProfileHasher
{
    public static string ComputeHash(SupplyDemandCalculationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToString() ?? string.Empty));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
