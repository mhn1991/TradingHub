using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>
/// Adds directional support/resistance and relative-volume confluence to an RSI /
/// Bollinger assessment. Volume is never directional by itself: it confirms only
/// when the completed candle direction agrees with the proposed trade.
/// </summary>
public sealed record ZoneVolumeSignalOptions
{
    public bool Enabled { get; init; } = true;
    public decimal MinimumSupportingZoneStrength { get; init; } = 45m;
    public decimal MinimumOpposingZoneStrength { get; init; } = 65m;
    public decimal SupportingZoneProximityAtr { get; init; } = 0.75m;
    public decimal OpposingZoneInfluenceAtr { get; init; } = 1.0m;
    public decimal OpposingZoneVetoDistanceAtr { get; init; } = 0.35m;
    public bool VetoNearbyOpposingZone { get; init; } = true;
    public bool VetoOpposingVolumeSpike { get; init; } = true;
    public bool AllowUnknownVolumeKind { get; init; }
    public decimal SupportingZoneBoost { get; init; } = 2.5m;
    public decimal AlignedElevatedVolumeBoost { get; init; } = 2m;
    public decimal RsiZoneConfluenceBoost { get; init; } = 2m;
    public decimal RsiVolumeConfluenceBoost { get; init; } = 1.5m;
    public decimal MaximumConfidenceAdjustment { get; init; } = 8m;

    public void Validate()
    {
        if (MinimumSupportingZoneStrength is < 0m or > 100m ||
            MinimumOpposingZoneStrength is < 0m or > 100m ||
            SupportingZoneProximityAtr < 0m ||
            OpposingZoneInfluenceAtr < 0m ||
            OpposingZoneVetoDistanceAtr < 0m ||
            OpposingZoneVetoDistanceAtr > OpposingZoneInfluenceAtr ||
            SupportingZoneBoost < 0m ||
            AlignedElevatedVolumeBoost < 0m ||
            RsiZoneConfluenceBoost < 0m ||
            RsiVolumeConfluenceBoost < 0m ||
            MaximumConfidenceAdjustment is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(ZoneVolumeSignalOptions));
        }
    }
}

public sealed record ZoneVolumeSignalAssessment
{
    public static ZoneVolumeSignalAssessment Disabled { get; } = new()
    {
        Explanation = "Directional zone/volume signals are disabled."
    };

    public PriceZone? SupportingZone { get; init; }
    public PriceZone? OpposingZone { get; init; }
    public decimal? SupportingZoneDistanceAtr { get; init; }
    public decimal? OpposingZoneDistanceAtr { get; init; }
    public bool ElevatedVolumeAligned { get; init; }
    public bool LowVolume { get; init; }
    public bool OpposingVolumeSpike { get; init; }
    public bool RsiZoneConfluence { get; init; }
    public bool RsiVolumeConfluence { get; init; }
    public bool HasConfluenceTrigger { get; init; }
    public decimal ConfidenceAdjustment { get; init; }
    public string? VetoReasonCode { get; init; }
    public required string Explanation { get; init; }

    public bool IsVetoed => VetoReasonCode is not null;
}

public static class ZoneVolumeSignalPolicy
{
    public static ZoneVolumeSignalAssessment Evaluate(
        AnalysisSnapshot snapshot,
        PriceActionDirection expectedDirection,
        RsiBollingerSignalAssessment rsiBollinger,
        ZoneVolumeSignalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rsiBollinger);
        options ??= new ZoneVolumeSignalOptions();
        options.Validate();
        if (!options.Enabled)
            return ZoneVolumeSignalAssessment.Disabled;
        if (expectedDirection is not (PriceActionDirection.Bullish or PriceActionDirection.Bearish))
            throw new ArgumentOutOfRangeException(nameof(expectedDirection));

        bool bullish = expectedDirection == PriceActionDirection.Bullish;
        decimal price = snapshot.LatestCandle.Prices.Close;
        decimal? atr = snapshot.Indicators.Atr;

        ZoneCandidate? support = atr is > 0m
            ? snapshot.PriceZones
                .Where(zone =>
                    zone.Strength >= options.MinimumSupportingZoneStrength &&
                    SupportsDirection(zone, bullish, price))
                .Select(zone => new ZoneCandidate(zone, DistanceToZone(price, zone) / atr.Value))
                .Where(item => item.DistanceAtr <= options.SupportingZoneProximityAtr)
                .OrderBy(item => item.DistanceAtr)
                .ThenByDescending(item => item.Zone.Strength)
                .FirstOrDefault()
            : null;

        ZoneCandidate? opposition = atr is > 0m
            ? snapshot.PriceZones
                .Where(zone =>
                    zone.Strength >= options.MinimumOpposingZoneStrength &&
                    OpposesDirection(zone, bullish, price))
                .Select(zone => new ZoneCandidate(zone, DistanceToZone(price, zone) / atr.Value))
                .Where(item => item.DistanceAtr <= options.OpposingZoneInfluenceAtr)
                .OrderBy(item => item.DistanceAtr)
                .ThenByDescending(item => item.Zone.Strength)
                .FirstOrDefault()
            : null;

        VolumeAnalysisSnapshot volume = snapshot.Indicators.VolumeAnalysis;
        bool volumeUsable = volume.RelativeToBaseline is > 0m &&
            (volume.IsReliable || options.AllowUnknownVolumeKind);
        bool candleAligned = bullish
            ? snapshot.LatestCandle.Prices.Close > snapshot.LatestCandle.Prices.Open
            : snapshot.LatestCandle.Prices.Close < snapshot.LatestCandle.Prices.Open;
        bool candleOpposes = bullish
            ? snapshot.LatestCandle.Prices.Close < snapshot.LatestCandle.Prices.Open
            : snapshot.LatestCandle.Prices.Close > snapshot.LatestCandle.Prices.Open;
        bool elevatedVolumeAligned = volumeUsable && volume.IsElevated && candleAligned;
        bool lowVolume = volumeUsable &&
            volume.Regime is VolumeRegime.Low or VolumeRegime.VeryLow;
        bool opposingVolumeSpike = volumeUsable &&
            volume.Regime == VolumeRegime.Spike && candleOpposes;

        bool rsiZoneConfluence = support is not null &&
            rsiBollinger.AlignedRsiRelationship is not null;
        bool rsiVolumeConfluence = elevatedVolumeAligned &&
            rsiBollinger.AlignedRsiRelationship is not null;
        bool confluenceTrigger = rsiBollinger.AlignedRsiRelationship is not null &&
            (support is not null || elevatedVolumeAligned);

        decimal adjustment = 0m;
        if (support is not null)
        {
            decimal proximity = options.SupportingZoneProximityAtr <= 0m
                ? 1m
                : 1m - Math.Min(1m, support.DistanceAtr / options.SupportingZoneProximityAtr);
            adjustment += options.SupportingZoneBoost * (0.5m + proximity * 0.5m) *
                Math.Min(1m, support.Zone.Strength / 75m);
        }
        if (opposition is not null)
        {
            decimal proximity = options.OpposingZoneInfluenceAtr <= 0m
                ? 1m
                : 1m - Math.Min(1m, opposition.DistanceAtr / options.OpposingZoneInfluenceAtr);
            adjustment -= Math.Min(3m, opposition.Zone.Strength / 25m) *
                (0.25m + proximity * 0.75m);
        }
        if (elevatedVolumeAligned)
            adjustment += options.AlignedElevatedVolumeBoost;
        if (lowVolume)
            adjustment -= 1m;
        if (opposingVolumeSpike)
            adjustment -= 4m;
        if (rsiZoneConfluence)
            adjustment += options.RsiZoneConfluenceBoost;
        if (rsiVolumeConfluence)
            adjustment += options.RsiVolumeConfluenceBoost;
        adjustment = Math.Clamp(
            adjustment,
            -options.MaximumConfidenceAdjustment,
            options.MaximumConfidenceAdjustment);

        // High-participation squeeze releases can legitimately break a nearby zone;
        // otherwise a strong close obstacle remains a veto for a fresh entry.
        bool confirmedBreakout = rsiBollinger.BollingerReleaseTrigger &&
            elevatedVolumeAligned;
        string? vetoReason = null;
        if (options.VetoOpposingVolumeSpike && opposingVolumeSpike)
        {
            vetoReason = "OpposingVolumeSpike";
        }
        else if (options.VetoNearbyOpposingZone &&
                 opposition?.DistanceAtr <= options.OpposingZoneVetoDistanceAtr &&
                 !confirmedBreakout)
        {
            vetoReason = "NearbyOpposingZone";
        }

        var evidence = new List<string>(6);
        if (support is not null)
        {
            evidence.Add(
                $"supporting {support.Zone.Type} zone {support.DistanceAtr:F2} ATR away " +
                $"({support.Zone.Strength:F0})");
        }
        if (opposition is not null)
        {
            evidence.Add(
                $"opposing {opposition.Zone.Type} zone {opposition.DistanceAtr:F2} ATR away " +
                $"({opposition.Zone.Strength:F0})");
        }
        if (elevatedVolumeAligned)
        {
            evidence.Add(
                $"aligned {VolumeLabel(volume)} {volume.RelativeToBaseline:F2}x baseline " +
                $"({volume.Percentile:F0}th percentile)");
        }
        else if (opposingVolumeSpike)
        {
            evidence.Add(
                $"opposing {VolumeLabel(volume)} spike {volume.RelativeToBaseline:F2}x baseline");
        }
        else if (lowVolume)
        {
            evidence.Add($"low {VolumeLabel(volume)} {volume.RelativeToBaseline:F2}x baseline");
        }
        if (rsiZoneConfluence)
            evidence.Add("RSI relationship confirmed at structure");
        if (rsiVolumeConfluence)
            evidence.Add("RSI relationship confirmed by participation");

        return new ZoneVolumeSignalAssessment
        {
            SupportingZone = support?.Zone,
            OpposingZone = opposition?.Zone,
            SupportingZoneDistanceAtr = support?.DistanceAtr,
            OpposingZoneDistanceAtr = opposition?.DistanceAtr,
            ElevatedVolumeAligned = elevatedVolumeAligned,
            LowVolume = lowVolume,
            OpposingVolumeSpike = opposingVolumeSpike,
            RsiZoneConfluence = rsiZoneConfluence,
            RsiVolumeConfluence = rsiVolumeConfluence,
            HasConfluenceTrigger = confluenceTrigger,
            ConfidenceAdjustment = adjustment,
            VetoReasonCode = vetoReason,
            Explanation = evidence.Count == 0
                ? "No directional zone/relative-volume confirmation."
                : string.Join(", ", evidence) + $"; adjustment {adjustment:+0.0;-0.0;0.0}."
        };
    }

    private static bool SupportsDirection(PriceZone zone, bool bullish, decimal price) =>
        bullish
            ? zone.LowerPrice <= price &&
              (zone.Type == PriceZoneType.Support ||
               zone.Type == PriceZoneType.Mixed && zone.CentrePrice <= price)
            : zone.UpperPrice >= price &&
              (zone.Type == PriceZoneType.Resistance ||
               zone.Type == PriceZoneType.Mixed && zone.CentrePrice >= price);

    private static bool OpposesDirection(PriceZone zone, bool bullish, decimal price) =>
        bullish
            ? zone.UpperPrice >= price &&
              (zone.Type == PriceZoneType.Resistance ||
               zone.Type == PriceZoneType.Mixed && zone.CentrePrice > price)
            : zone.LowerPrice <= price &&
              (zone.Type == PriceZoneType.Support ||
               zone.Type == PriceZoneType.Mixed && zone.CentrePrice < price);

    private static decimal DistanceToZone(decimal price, PriceZone zone) => price switch
    {
        _ when price < zone.LowerPrice => zone.LowerPrice - price,
        _ when price > zone.UpperPrice => price - zone.UpperPrice,
        _ => 0m
    };

    private static string VolumeLabel(VolumeAnalysisSnapshot volume) => volume.Kind switch
    {
        Brokers.Models.VolumeKind.TickCount => "tick activity",
        Brokers.Models.VolumeKind.BaseAssetQuantity => "base-asset volume",
        Brokers.Models.VolumeKind.LastTradedQuantity => "last-traded volume",
        _ => "volume"
    };

    private sealed record ZoneCandidate(PriceZone Zone, decimal DistanceAtr);
}
