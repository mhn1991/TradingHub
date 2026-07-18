using System.Security.Cryptography;
using System.Text;

namespace ChartAnnotator.Liquidity;

/// <summary>
/// Named score components for <c>LiquidityPool.QualityScore</c>, recorded separately per
/// blueprint §5.9's "record components separately" principle (applied here to liquidity, which
/// asks for the same discipline in §6.4's factor list).
/// </summary>
public sealed record LiquidityScoringWeights
{
    public decimal Equalness { get; init; } = 1.0m;
    public decimal Visibility { get; init; } = 0.5m;
    public decimal Compression { get; init; } = 0.5m;
    public decimal Prominence { get; init; } = 1.0m;
    public decimal Freshness { get; init; } = 1.0m;

    public static LiquidityScoringWeights Default { get; } = new();
}

/// <summary>
/// Every setting that changes computed liquidity-pool/sweep/breakout analysis (blueprint §4).
/// Agent interpretation thresholds belong on Agent policy, not here.
/// </summary>
public sealed record LiquidityCalculationProfile
{
    public bool Enabled { get; init; }
    public int AtrPeriod { get; init; } = 14;

    // Confirmed swing configuration - reuses the same left/right pivot shape as
    // ChartAnnotator.Structure.SwingDetector rather than introducing a second swing definition.
    public int SwingLeftBars { get; init; } = 2;
    public int SwingRightBars { get; init; } = 2;

    // Equal highs/lows (§6.4)
    public decimal EqualLevelToleranceAtr { get; init; } = 0.10m;
    public int MinimumSourcePoints { get; init; } = 2;
    public int MaximumSourcePoints { get; init; } = 6;
    public decimal MaximumPoolWidthAtr { get; init; } = 0.20m;

    // Isolated swing liquidity (§6.5)
    public decimal MinimumSwingProminenceAtr { get; init; } = 0.50m;
    public int MinimumSwingAgeBars { get; init; } = 3;

    // Range liquidity (§6.6)
    public int MinimumRangeDurationBars { get; init; } = 20;
    public int MinimumRangeTouches { get; init; } = 2;
    public decimal MaximumRangeWidthAtr { get; init; } = 3.0m;

    // Session/day/week reference rules (§6.7) - one authoritative timezone, reference periods
    // only complete (and become AvailableAt-eligible) after their period ends.
    public bool EnablePreviousSessionLevels { get; init; } = true;
    public bool EnablePreviousDayLevels { get; init; } = true;
    public bool EnablePreviousWeekLevels { get; init; } = true;
    public string ReferenceTimeZoneId { get; init; } = "UTC";

    // Round numbers (§6.8) - instrument-aware steps are resolved at detection time from the
    // instrument's price precision/asset class; these are configured overrides, not required.
    public bool EnableRoundNumberLevels { get; init; }
    public decimal? RoundNumberMajorStep { get; init; }
    public decimal? RoundNumberMinorStep { get; init; }

    // Merge rules / expiry
    public decimal MergeOverlapRatio { get; init; } = 0.50m;
    public int? MaximumPoolAgeBars { get; init; }
    public decimal ApproachDistanceAtr { get; init; } = 0.25m;
    public int MaximumActivePools { get; init; } = 128;
    public int MaximumRetainedEvents { get; init; } = 256;
    public int MaximumRetainedSweeps { get; init; } = 128;

    // Sweep detection (§7.3 / §7.4)
    public decimal MinimumSweepPenetrationAtr { get; init; } = 0.05m;
    public decimal MaximumSweepPenetrationAtr { get; init; } = 1.0m;
    public decimal MinimumCloseBackAtr { get; init; }
    public bool RequireSweepDisplacementConfirmation { get; init; }
    public bool RequireSweepStructureShiftConfirmation { get; init; }

    // Accepted breakout (§7.5)
    public decimal MinimumAcceptedBreakCloseDistanceAtr { get; init; } = 0.10m;
    public int MinimumAcceptedBreakHoldBars { get; init; } = 1;
    public bool RequireAcceptedBreakRetest { get; init; }
    public bool RequireAcceptedBreakDisplacement { get; init; }

    public LiquidityScoringWeights ScoringWeights { get; init; } = LiquidityScoringWeights.Default;

    public int RuleSetVersion { get; init; } = 1;

    public void Validate()
    {
        if (AtrPeriod < 1 ||
            SwingLeftBars < 1 ||
            SwingRightBars < 1 ||
            EqualLevelToleranceAtr < 0m ||
            MinimumSourcePoints < 2 ||
            MaximumSourcePoints < MinimumSourcePoints ||
            MaximumPoolWidthAtr <= 0m ||
            MinimumSwingProminenceAtr < 0m ||
            MinimumSwingAgeBars < 0 ||
            MinimumRangeDurationBars < 1 ||
            MinimumRangeTouches < 1 ||
            MaximumRangeWidthAtr <= 0m ||
            string.IsNullOrWhiteSpace(ReferenceTimeZoneId) ||
            (EnableRoundNumberLevels && RoundNumberMajorStep is null && RoundNumberMinorStep is null) ||
            (RoundNumberMajorStep is decimal majorStep && majorStep <= 0m) ||
            (RoundNumberMinorStep is decimal minorStep && minorStep <= 0m) ||
            MergeOverlapRatio is < 0m or > 1m ||
            (MaximumPoolAgeBars is int age && age < 1) ||
            ApproachDistanceAtr < 0m ||
            MaximumActivePools < 1 ||
            MaximumRetainedEvents < 1 ||
            MaximumRetainedSweeps < 1 ||
            MinimumSweepPenetrationAtr <= 0m ||
            MaximumSweepPenetrationAtr < MinimumSweepPenetrationAtr ||
            MinimumCloseBackAtr < 0m ||
            MinimumAcceptedBreakCloseDistanceAtr <= 0m ||
            MinimumAcceptedBreakHoldBars < 0 ||
            RuleSetVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LiquidityCalculationProfile));
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(ReferenceTimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentOutOfRangeException(nameof(ReferenceTimeZoneId), ReferenceTimeZoneId, "Unknown time zone id.");
        }
    }
}

/// <summary>
/// Canonicalization/hashing for <see cref="LiquidityCalculationProfile"/>, mirroring
/// <c>ChartAnnotator.SupplyDemand.SupplyDemandCalculationProfileHasher</c>. Every field
/// participates - the profile has no collection-typed properties, so the record's own
/// <c>ToString()</c> is already content-accurate (unlike <c>ChartAnnotationOptions</c>, which
/// needs explicit list canonicalization).
/// </summary>
public static class LiquidityCalculationProfileHasher
{
    public static string ComputeHash(LiquidityCalculationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToString() ?? string.Empty));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
