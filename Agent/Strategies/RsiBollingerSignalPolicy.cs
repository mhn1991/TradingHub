using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>
/// Direction-aware use of the enriched RSI and Bollinger analysis. The raw chart
/// confidence is intentionally direction-neutral; this policy converts indicator
/// context into an aligned boost, an entry trigger, or an explicit opposing veto.
/// </summary>
public sealed record RsiBollingerSignalOptions
{
    public bool Enabled { get; init; } = true;
    public int MaximumRsiRelationshipAgeCandles { get; init; } = 8;
    public decimal MinimumRsiRelationshipStrength { get; init; } = 45m;
    public decimal OpposingRsiVetoStrength { get; init; } = 65m;
    public int MinimumBollingerSamples { get; init; } = 20;

    /// <summary>
    /// Directional location threshold on the 0..100 %B scale. A bullish value must
    /// be at or above this threshold; the bearish threshold is its mirror.
    /// </summary>
    public decimal BollingerConfirmationPercentB { get; init; } = 55m;

    /// <summary>
    /// Strong squeeze-release threshold. Bullish releases must be at or above this
    /// value; bearish releases use its mirror (100 - value).
    /// </summary>
    public decimal BollingerBreakoutPercentB { get; init; } = 80m;

    /// <summary>
    /// Expanded movement beyond this opposing %B level vetoes an entry. The bearish
    /// mirror is 100 - value.
    /// </summary>
    public decimal BollingerOppositionPercentB { get; init; } = 35m;

    public bool VetoStrongOpposingRsiRelationship { get; init; } = true;
    public bool VetoOpposingBollingerExpansion { get; init; } = true;
    public decimal RsiExtremeRelationshipBoost { get; init; } = 2.5m;
    public decimal MaximumConfidenceAdjustment { get; init; } = 8m;

    public void Validate()
    {
        if (MaximumRsiRelationshipAgeCandles < 0 ||
            MinimumRsiRelationshipStrength is < 0m or > 100m ||
            OpposingRsiVetoStrength is < 0m or > 100m ||
            OpposingRsiVetoStrength < MinimumRsiRelationshipStrength ||
            MinimumBollingerSamples < 1 ||
            BollingerConfirmationPercentB is < 50m or > 100m ||
            BollingerBreakoutPercentB is < 50m or > 100m ||
            BollingerBreakoutPercentB < BollingerConfirmationPercentB ||
            BollingerOppositionPercentB is < 0m or > 50m ||
            BollingerOppositionPercentB >= BollingerConfirmationPercentB ||
            RsiExtremeRelationshipBoost < 0m ||
            MaximumConfidenceAdjustment is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(RsiBollingerSignalOptions));
        }
    }
}

public sealed record RsiBollingerSignalAssessment
{
    public static RsiBollingerSignalAssessment Disabled { get; } = new()
    {
        Explanation = "Directional RSI/Bollinger signals are disabled."
    };

    public RsiRelationshipSnapshot? AlignedRsiRelationship { get; init; }
    public RsiRelationshipSnapshot? OpposingRsiRelationship { get; init; }
    public bool RsiMomentumAligned { get; init; }
    public bool RsiMomentumOpposes { get; init; }
    public bool RsiExtremeConfirmsRelationship { get; init; }
    public bool BollingerPositionAligned { get; init; }
    public bool BollingerExpansionAligned { get; init; }
    public bool BollingerReleaseTrigger { get; init; }
    public bool BollingerExpansionOpposes { get; init; }
    public bool HasEntryTrigger { get; init; }
    public decimal ConfidenceAdjustment { get; init; }
    public string? VetoReasonCode { get; init; }
    public required string Explanation { get; init; }

    public bool IsVetoed => VetoReasonCode is not null;
}

public static class RsiBollingerSignalPolicy
{
    public static RsiBollingerSignalAssessment Evaluate(
        AnalysisSnapshot snapshot,
        PriceActionDirection expectedDirection,
        RsiBollingerSignalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new RsiBollingerSignalOptions();
        options.Validate();
        if (!options.Enabled)
            return RsiBollingerSignalAssessment.Disabled;
        if (expectedDirection is not (PriceActionDirection.Bullish or PriceActionDirection.Bearish))
            throw new ArgumentOutOfRangeException(nameof(expectedDirection));

        bool bullish = expectedDirection == PriceActionDirection.Bullish;
        RsiAnalysisSnapshot rsi = snapshot.Indicators.RsiAnalysis;
        RsiRelationshipSnapshot? relationship = rsi.LatestRelationship;
        bool relationshipIsUsable = relationship is not null &&
            relationship.AgeCandles <= options.MaximumRsiRelationshipAgeCandles &&
            relationship.Strength >= options.MinimumRsiRelationshipStrength;

        RsiRelationshipSnapshot? alignedRelationship = relationshipIsUsable &&
            RelationshipDirection(relationship!.Type) == expectedDirection
                ? relationship
                : null;
        RsiRelationshipSnapshot? opposingRelationship = relationshipIsUsable &&
            RelationshipDirection(relationship!.Type) == Opposite(expectedDirection)
                ? relationship
                : null;

        bool momentumAligned = bullish
            ? rsi.MomentumDirection == MomentumDirection.Rising
            : rsi.MomentumDirection == MomentumDirection.Falling;
        bool momentumOpposes = bullish
            ? rsi.MomentumDirection == MomentumDirection.Falling
            : rsi.MomentumDirection == MomentumDirection.Rising;
        bool rsiExtremeConfirms = alignedRelationship is not null && (bullish
            ? rsi.Zone == RsiZone.Oversold
            : rsi.Zone == RsiZone.Overbought);

        BollingerAnalysisSnapshot bollinger = snapshot.Indicators.BollingerAnalysis;
        bool bollingerReady = bollinger.SampleCount >= options.MinimumBollingerSamples &&
            bollinger.PercentB is decimal;
        decimal percentB = bollinger.PercentB ?? 50m;
        decimal bearishConfirmation = 100m - options.BollingerConfirmationPercentB;
        decimal bearishBreakout = 100m - options.BollingerBreakoutPercentB;
        decimal bearishOpposition = 100m - options.BollingerOppositionPercentB;

        bool positionAligned = bollingerReady && (bullish
            ? percentB >= options.BollingerConfirmationPercentB
            : percentB <= bearishConfirmation);
        bool expanding = bollinger.IsExpansion ||
            bollinger.WidthDirection == VolatilityDirection.Expanding;
        bool expansionAligned = positionAligned && expanding;
        bool releaseTrigger = bollingerReady && bollinger.SqueezeReleased && (bullish
            ? percentB >= options.BollingerBreakoutPercentB
            : percentB <= bearishBreakout);
        bool expansionOpposes = bollingerReady && expanding && (bullish
            ? percentB <= options.BollingerOppositionPercentB
            : percentB >= bearishOpposition);

        // A completed RSI swing relationship may trigger only with current momentum
        // directional Bollinger location, or a matching RSI extreme. A squeeze release
        // beyond the outer 20% of the envelope is sufficiently specific to stand alone.
        bool relationshipTrigger = alignedRelationship is not null &&
            (momentumAligned || positionAligned || rsiExtremeConfirms);
        bool hasEntryTrigger = relationshipTrigger || releaseTrigger;

        decimal adjustment = 0m;
        if (alignedRelationship is not null)
            adjustment += Math.Min(5m, alignedRelationship.Strength / 20m);
        if (rsiExtremeConfirms)
            adjustment += options.RsiExtremeRelationshipBoost;
        if (opposingRelationship is not null)
            adjustment -= Math.Min(6m, opposingRelationship.Strength / 15m);
        if (momentumAligned)
            adjustment += 1m;
        else if (momentumOpposes)
            adjustment -= 1m;
        if (releaseTrigger)
            adjustment += 4m;
        else if (expansionAligned)
            adjustment += 2m;
        else if (positionAligned)
            adjustment += 0.5m;
        if (expansionOpposes)
            adjustment -= 4m;
        adjustment = Math.Clamp(
            adjustment,
            -options.MaximumConfidenceAdjustment,
            options.MaximumConfidenceAdjustment);

        string? vetoReason = null;
        if (options.VetoStrongOpposingRsiRelationship &&
            opposingRelationship?.Strength >= options.OpposingRsiVetoStrength)
        {
            vetoReason = "OpposingRsiRelationship";
        }
        else if (options.VetoOpposingBollingerExpansion && expansionOpposes)
        {
            vetoReason = "OpposingBollingerExpansion";
        }

        var evidence = new List<string>(4);
        if (alignedRelationship is not null)
            evidence.Add($"aligned {alignedRelationship.Type} ({alignedRelationship.Strength:F0})");
        if (rsiExtremeConfirms)
            evidence.Add($"RSI {rsi.Zone} confirms the relationship");
        if (opposingRelationship is not null)
            evidence.Add($"opposing {opposingRelationship.Type} ({opposingRelationship.Strength:F0})");
        if (releaseTrigger)
            evidence.Add($"aligned squeeze release at %B {percentB:F1}");
        else if (expansionAligned)
            evidence.Add($"aligned expansion at %B {percentB:F1}");
        else if (expansionOpposes)
            evidence.Add($"opposing expansion at %B {percentB:F1}");
        if (momentumAligned)
            evidence.Add("RSI momentum aligned");
        else if (momentumOpposes)
            evidence.Add("RSI momentum opposing");

        return new RsiBollingerSignalAssessment
        {
            AlignedRsiRelationship = alignedRelationship,
            OpposingRsiRelationship = opposingRelationship,
            RsiMomentumAligned = momentumAligned,
            RsiMomentumOpposes = momentumOpposes,
            RsiExtremeConfirmsRelationship = rsiExtremeConfirms,
            BollingerPositionAligned = positionAligned,
            BollingerExpansionAligned = expansionAligned,
            BollingerReleaseTrigger = releaseTrigger,
            BollingerExpansionOpposes = expansionOpposes,
            HasEntryTrigger = hasEntryTrigger,
            ConfidenceAdjustment = adjustment,
            VetoReasonCode = vetoReason,
            Explanation = evidence.Count == 0
                ? "No recent directional RSI/Bollinger confirmation."
                : string.Join(", ", evidence) + $"; adjustment {adjustment:+0.0;-0.0;0.0}."
        };
    }

    public static PriceActionDirection RelationshipDirection(RsiRelationshipType type) => type switch
    {
        RsiRelationshipType.RegularBullishDivergence or
            RsiRelationshipType.HiddenBullishDivergence or
            RsiRelationshipType.BullishConvergence => PriceActionDirection.Bullish,
        RsiRelationshipType.RegularBearishDivergence or
            RsiRelationshipType.HiddenBearishDivergence or
            RsiRelationshipType.BearishConvergence => PriceActionDirection.Bearish,
        _ => PriceActionDirection.Neutral
    };

    private static PriceActionDirection Opposite(PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish
            ? PriceActionDirection.Bearish
            : PriceActionDirection.Bullish;
}
