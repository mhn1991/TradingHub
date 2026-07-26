using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

internal readonly record struct CciAssessment(EvidenceAlignment Alignment, decimal Quality, string State);
internal readonly record struct RsiAssessment(EvidenceAlignment Alignment, decimal Quality, string State);
internal readonly record struct LocalTrendOppositionAssessment(
    bool DmiOpposes,
    bool CciOpposes,
    bool BollingerExpansionOpposes)
{
    public bool IsCoherent =>
        DmiOpposes && CciOpposes && BollingerExpansionOpposes;
}

internal readonly record struct StructuralTriggerAnchor(decimal LowerPrice, decimal UpperPrice)
{
    public decimal Lower => Math.Min(LowerPrice, UpperPrice);
    public decimal Upper => Math.Max(LowerPrice, UpperPrice);
}

internal enum StructuralTriggerProfile
{
    BreakRetestContinuation,
    SweepReversal,
    TrendPullback,
    IndicatorTrendContinuation,
    IndicatorStructuralInvalidation,
    IndicatorStrongOpposition
}

internal static class StructuralPlaybookRules
{
    public static TimeSpan Bars(BarInterval interval, int count) =>
        TimeSpan.FromSeconds(BarIntervalParser.ApproximateSeconds(interval) * count);

    public static bool IsFresh(DateTimeOffset eventAt, DateTimeOffset now, BarInterval interval, int bars) =>
        eventAt <= now && now - eventAt <= Bars(interval, bars);

    public static int TriggerResponseBars(int maximumTriggerBars, int maximumArmedSetupBars) =>
        Math.Min(maximumTriggerBars, maximumArmedSetupBars);

    public static DateTimeOffset TriggerWindowExpiresAt(
        DateTimeOffset catalystAt,
        BarInterval triggerInterval,
        int maximumTriggerBars,
        int maximumArmedSetupBars) =>
        catalystAt + Bars(
            triggerInterval,
            TriggerResponseBars(maximumTriggerBars, maximumArmedSetupBars));

    public static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    /// <summary>
    /// Selects the candidate that should represent a multi-hypothesis playbook in runtime state
    /// and diagnostics. Readiness remains decisive; otherwise preserve the hypothesis that has
    /// made the most lifecycle progress instead of replacing it with a merely newer candidate.
    /// </summary>
    public static PlaybookEvaluation SelectRepresentativeCandidate(
        IReadOnlyList<PlaybookEvaluation> evaluations)
    {
        ArgumentOutOfRangeException.ThrowIfZero(evaluations.Count);

        return evaluations
            .OrderByDescending(item => item.IsReady)
            .ThenByDescending(item => LifecycleProgress(item.Lifecycle))
            .ThenBy(item => item.MandatoryGates.Count(gate =>
                gate.LimitsConfidenceFloor && !gate.Passed))
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.CatalystAt)
            .ThenBy(item => item.SetupId, StringComparer.Ordinal)
            .First();
    }

    private static int LifecycleProgress(StructuralSetupLifecycle lifecycle) => lifecycle switch
    {
        StructuralSetupLifecycle.CandidateProduced => 4,
        StructuralSetupLifecycle.AwaitingTrigger => 3,
        StructuralSetupLifecycle.CatalystObserved => 2,
        StructuralSetupLifecycle.Armed => 1,
        StructuralSetupLifecycle.Dormant => 0,
        StructuralSetupLifecycle.Invalidated or StructuralSetupLifecycle.Expired => -1,
        _ => 0
    };

    public static (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) Trigger(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        StructuralTriggerProfile profile,
        decimal minimumConfidence,
        int maximumBars,
        DateTimeOffset notBefore,
        StructuralTriggerAnchor? anchor = null)
    {
        TimeSpan triggerWindow = Bars(evidence.Trigger.Interval, maximumBars);
        DateTimeOffset earliest = evidence.AvailableAt - triggerWindow;
        DateTimeOffset latest = notBefore + triggerWindow;
        PriceActionEvent? triggerEvent = evidence.TriggerEvidence.Events
            .Where(item => item.Direction == direction && item.Confidence >= minimumConfidence &&
                item.ConfirmedAt >= earliest && item.ConfirmedAt >= notBefore &&
                item.ConfirmedAt <= latest && item.ConfirmedAt <= evidence.AvailableAt &&
                IsTriggerType(profile, item.Type) &&
                (!anchor.HasValue || ReferencesAnchor(item, anchor.Value)))
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.ConfirmedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .FirstOrDefault();
        PriceActionSetup? setup = evidence.TriggerEvidence.Setups
            .Where(item => item.Direction == direction && item.Phase == PriceActionSetupPhase.Triggered &&
                item.Confidence >= minimumConfidence && item.TriggeredAt >= earliest &&
                item.TriggeredAt >= notBefore && item.TriggeredAt <= latest &&
                item.TriggeredAt <= evidence.AvailableAt &&
                IsSetupType(profile, item.Type) &&
                (!anchor.HasValue || ReferencesAnchor(item, anchor.Value)))
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.TriggeredAt)
            .ThenBy(item => item.SetupId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (setup is not null && (triggerEvent is null || setup.Confidence > triggerEvent.Confidence))
            return (null, setup, Math.Clamp(setup.Confidence, 0m, 100m));
        return (triggerEvent, null, triggerEvent is null ? 0m : Math.Clamp(triggerEvent.Confidence, 0m, 100m));
    }

    internal static bool ReferencesAnchor(PriceActionEvent item, StructuralTriggerAnchor anchor) =>
        Contains(anchor, item.RetestLevel) ||
        Contains(anchor, item.BrokenLevel) ||
        Contains(anchor, item.ReferenceLevel);

    internal static bool ReferencesAnchor(PriceActionSetup item, StructuralTriggerAnchor anchor) =>
        Contains(anchor, item.ReferenceLevel);

    private static bool Contains(StructuralTriggerAnchor anchor, decimal? level) =>
        level is decimal value && value >= anchor.Lower && value <= anchor.Upper;

    public static CciAssessment AssessCci(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        bool continuation,
        int maximumRelationshipAgeCandles = 8,
        decimal minimumRelationshipStrength = 45m)
    {
        decimal? cci = evidence.Indicators.Cci;
        CciAnalysisSnapshot analysis = evidence.Indicators.CciAnalysis;
        if (!cci.HasValue || analysis.SampleCount == 0)
            return new CciAssessment(EvidenceAlignment.Unavailable, 0m, "Unavailable");

        bool bullish = direction == PriceActionDirection.Bullish;
        CciRelationshipSnapshot? relationship = analysis.LatestRelationship;
        bool relationshipUsable = relationship is not null &&
            relationship.AgeCandles <= maximumRelationshipAgeCandles &&
            relationship.Strength >= minimumRelationshipStrength;
        EvidenceAlignment relationshipAlignment = relationshipUsable
            ? ClassifyCciRelationship(relationship!.Type, direction, continuation)
            : EvidenceAlignment.Neutral;
        bool alignedRelationship = relationshipAlignment == EvidenceAlignment.Aligned;
        bool conflictRelationship = relationshipAlignment == EvidenceAlignment.Conflicting;

        bool aligned = continuation
            ? bullish
                ? cci > 0m && analysis.MomentumDirection != MomentumDirection.Falling || analysis.CrossedUpZero
                : cci < 0m && analysis.MomentumDirection != MomentumDirection.Rising || analysis.CrossedDownZero
            : bullish
                ? analysis.CrossedUpFromExtremeNegative || analysis.MomentumDirection == MomentumDirection.Rising && analysis.BarsSinceExtremeNegative is >= 0 and <= 12
                : analysis.CrossedDownFromExtremePositive || analysis.MomentumDirection == MomentumDirection.Falling && analysis.BarsSinceExtremePositive is >= 0 and <= 12;
        aligned |= alignedRelationship;
        bool conflict = conflictRelationship || (bullish
            ? cci < -100m && analysis.MomentumDirection == MomentumDirection.Falling
            : cci > 100m && analysis.MomentumDirection == MomentumDirection.Rising);

        if (conflict)
            return new CciAssessment(EvidenceAlignment.Conflicting, 20m,
                relationshipAlignment == EvidenceAlignment.Conflicting
                    ? $"Conflicting:{relationship!.Type}"
                    : "Conflicting:Momentum");
        if (aligned)
        {
            bool alignedConvergence = alignedRelationship && relationship!.Type is
                CciRelationshipType.BullishConvergence or CciRelationshipType.BearishConvergence;
            decimal relationshipQuality = alignedRelationship
                ? alignedConvergence ? 55m : 65m
                : 60m;
            if (alignedRelationship)
                relationshipQuality = Math.Max(
                    relationshipQuality, Math.Clamp(relationship!.Strength, 0m, 100m));
            return new CciAssessment(EvidenceAlignment.Aligned,
                Math.Clamp(relationshipQuality + Math.Abs(analysis.MomentumChange ?? 0m) / 5m, 0m, 100m),
                alignedRelationship ? $"Aligned:{relationship!.Type}" : "Aligned:Momentum");
        }
        return new CciAssessment(EvidenceAlignment.Neutral, 45m, "Neutral");
    }

    public static RsiAssessment AssessRsi(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        bool continuation,
        int maximumRelationshipAgeCandles = 8,
        decimal minimumRelationshipStrength = 45m)
    {
        decimal? rsi = evidence.Indicators.Rsi;
        RsiAnalysisSnapshot analysis = evidence.Indicators.RsiAnalysis;
        if (!rsi.HasValue || analysis.SampleCount == 0)
            return new RsiAssessment(EvidenceAlignment.Unavailable, 0m, "Unavailable");

        RsiRelationshipSnapshot? relationship = analysis.LatestRelationship;
        bool relationshipUsable = relationship is not null &&
            relationship.AgeCandles <= maximumRelationshipAgeCandles &&
            relationship.Strength >= minimumRelationshipStrength;
        EvidenceAlignment relationshipAlignment = relationshipUsable
            ? ClassifyRsiRelationship(relationship!.Type, direction, continuation)
            : EvidenceAlignment.Neutral;

        bool bullish = direction == PriceActionDirection.Bullish;
        bool dynamicAlignment = continuation
            ? bullish
                ? analysis.MomentumDirection == MomentumDirection.Rising &&
                    analysis.Zone is RsiZone.Neutral or RsiZone.Bullish
                : analysis.MomentumDirection == MomentumDirection.Falling &&
                    analysis.Zone is RsiZone.Neutral or RsiZone.Bearish
            : bullish
                ? analysis.MomentumDirection == MomentumDirection.Rising &&
                    analysis.Zone is RsiZone.Oversold or RsiZone.Bearish
                : analysis.MomentumDirection == MomentumDirection.Falling &&
                    analysis.Zone is RsiZone.Overbought or RsiZone.Bullish;

        if (relationshipAlignment == EvidenceAlignment.Conflicting)
            return new RsiAssessment(EvidenceAlignment.Conflicting, 20m,
                $"Conflicting:{relationship!.Type}");
        if (relationshipAlignment == EvidenceAlignment.Aligned || dynamicAlignment)
        {
            bool convergence = relationship?.Type is
                RsiRelationshipType.BullishConvergence or RsiRelationshipType.BearishConvergence;
            decimal quality = relationshipAlignment == EvidenceAlignment.Aligned
                ? convergence ? 55m : 65m
                : 55m;
            if (relationshipAlignment == EvidenceAlignment.Aligned)
                quality = Math.Max(quality, Math.Clamp(relationship!.Strength, 0m, 100m));
            return new RsiAssessment(EvidenceAlignment.Aligned, quality,
                relationshipAlignment == EvidenceAlignment.Aligned
                    ? $"Aligned:{relationship!.Type}"
                    : "Aligned:Momentum");
        }

        return new RsiAssessment(EvidenceAlignment.Neutral, 45m, "Neutral");
    }

    /// <summary>
    /// Detects a coherent trigger-timeframe trend against a continuation hypothesis. No single
    /// indicator can reject the setup: DMI must point the other way while ADX strengthens, CCI
    /// must independently conflict, and Bollinger width must expand at an opposing location.
    /// The constituent analyzers own their normal sampling/relationship rules, so this adds no
    /// replay-calibrated threshold.
    /// </summary>
    internal static LocalTrendOppositionAssessment AssessLocalTrendOpposition(
        PriceActionDirection direction,
        AdxAnalysisSnapshot adx,
        CciAssessment cci,
        RsiBollingerSignalAssessment rsiBollinger)
    {
        bool dmiOpposes = adx.Adx.HasValue &&
            adx.IsTrendStrengthening &&
            adx.DirectionalBias is PriceActionDirection.Bullish or PriceActionDirection.Bearish &&
            adx.DirectionalBias != direction;
        return new LocalTrendOppositionAssessment(
            dmiOpposes,
            cci.Alignment == EvidenceAlignment.Conflicting,
            rsiBollinger.BollingerExpansionOpposes);
    }

    public static bool ConfirmationPasses(StructuralConfirmationMode mode, CciAssessment cci) => mode switch
    {
        StructuralConfirmationMode.Disabled => true,
        StructuralConfirmationMode.Soft => true,
        StructuralConfirmationMode.Required => cci.Alignment == EvidenceAlignment.Aligned,
        _ => false
    };

    public static decimal Confidence(
        IReadOnlyList<MandatoryGate> gates,
        decimal contextAdjustment,
        decimal confirmationAdjustment,
        decimal confluenceAdjustment) =>
        gates.Any(item => !item.Passed)
            ? 0m
            : Math.Clamp(gates
                .Where(item => item.LimitsConfidenceFloor)
                .Select(item => item.Quality)
                .DefaultIfEmpty(0m)
                .Min() +
                Math.Clamp(contextAdjustment, -8m, 8m) +
                Math.Clamp(confirmationAdjustment, -10m, 10m) +
                Math.Clamp(confluenceAdjustment, 0m, 8m), 0m, 100m);

    public static decimal ConfirmationAdjustment(
        StructuralConfirmationMode mode,
        CciAssessment cci,
        StructuralConfirmationOptions options) => mode == StructuralConfirmationMode.Disabled
        ? 0m
        : cci.Alignment switch
        {
            EvidenceAlignment.Aligned => options.SoftAlignedAdjustment,
            EvidenceAlignment.Conflicting => options.SoftConflictAdjustment,
            _ => 0m
        };

    internal static EvidenceAlignment ClassifyCciRelationship(
        CciRelationshipType type,
        PriceActionDirection direction,
        bool continuation) => ClassifyRelationship(
            IsBullish(type), IsBearish(type), IsRegular(type), IsHidden(type), IsConvergence(type),
            direction, continuation);

    internal static EvidenceAlignment ClassifyRsiRelationship(
        RsiRelationshipType type,
        PriceActionDirection direction,
        bool continuation) => ClassifyRelationship(
            IsBullish(type), IsBearish(type), IsRegular(type), IsHidden(type), IsConvergence(type),
            direction, continuation);

    internal static bool IsTriggerType(StructuralTriggerProfile profile, PriceActionEventType type) =>
        profile switch
        {
            StructuralTriggerProfile.BreakRetestContinuation => type is
                PriceActionEventType.BullishRetestHeld or PriceActionEventType.BearishRetestHeld or
                PriceActionEventType.BullishDisplacement or PriceActionEventType.BearishDisplacement,
            StructuralTriggerProfile.SweepReversal => type is
                PriceActionEventType.BullishChangeOfCharacter or PriceActionEventType.BearishChangeOfCharacter,
            StructuralTriggerProfile.TrendPullback => type is
                PriceActionEventType.BullishRejection or PriceActionEventType.BearishRejection or
                PriceActionEventType.BullishRetestHeld or PriceActionEventType.BearishRetestHeld,
            StructuralTriggerProfile.IndicatorTrendContinuation => type is
                PriceActionEventType.BullishBreakOfStructure or PriceActionEventType.BearishBreakOfStructure,
            StructuralTriggerProfile.IndicatorStructuralInvalidation => type is
                PriceActionEventType.BullishBreakOfStructure or PriceActionEventType.BearishBreakOfStructure or
                PriceActionEventType.BullishChangeOfCharacter or PriceActionEventType.BearishChangeOfCharacter,
            StructuralTriggerProfile.IndicatorStrongOpposition => type is not
                (PriceActionEventType.BullishPullback or PriceActionEventType.BearishPullback),
            _ => false
        };

    internal static bool IsSetupType(StructuralTriggerProfile profile, PriceActionSetupType type) =>
        profile switch
        {
            StructuralTriggerProfile.BreakRetestContinuation => type is
                PriceActionSetupType.BullishBreakRetestHold or PriceActionSetupType.BearishBreakRetestHold,
            StructuralTriggerProfile.SweepReversal => type is
                PriceActionSetupType.BullishSweepChoCh or PriceActionSetupType.BearishSweepChoCh or
                PriceActionSetupType.BullishChoChRetestHold or PriceActionSetupType.BearishChoChRetestHold,
            StructuralTriggerProfile.TrendPullback => type is
                PriceActionSetupType.BullishBreakRetestHold or PriceActionSetupType.BearishBreakRetestHold,
            StructuralTriggerProfile.IndicatorTrendContinuation => type is
                PriceActionSetupType.BullishBreakRetestHold or PriceActionSetupType.BearishBreakRetestHold,
            StructuralTriggerProfile.IndicatorStructuralInvalidation => true,
            StructuralTriggerProfile.IndicatorStrongOpposition => true,
            _ => false
        };

    private static EvidenceAlignment ClassifyRelationship(
        bool bullishType,
        bool bearishType,
        bool regular,
        bool hidden,
        bool convergence,
        PriceActionDirection direction,
        bool continuation)
    {
        bool sameDirection = direction == PriceActionDirection.Bullish ? bullishType : bearishType;
        bool oppositeDirection = direction == PriceActionDirection.Bullish ? bearishType : bullishType;
        if (oppositeDirection && (regular || hidden))
            return EvidenceAlignment.Conflicting;
        if (!sameDirection)
            return EvidenceAlignment.Neutral;
        if (convergence || (continuation && hidden) || (!continuation && regular))
            return EvidenceAlignment.Aligned;
        return EvidenceAlignment.Neutral;
    }

    private static bool IsBullish(CciRelationshipType type) => type is
        CciRelationshipType.RegularBullishDivergence or CciRelationshipType.HiddenBullishDivergence or
        CciRelationshipType.BullishConvergence;

    private static bool IsBearish(CciRelationshipType type) => type is
        CciRelationshipType.RegularBearishDivergence or CciRelationshipType.HiddenBearishDivergence or
        CciRelationshipType.BearishConvergence;

    private static bool IsRegular(CciRelationshipType type) => type is
        CciRelationshipType.RegularBullishDivergence or CciRelationshipType.RegularBearishDivergence;

    private static bool IsHidden(CciRelationshipType type) => type is
        CciRelationshipType.HiddenBullishDivergence or CciRelationshipType.HiddenBearishDivergence;

    private static bool IsConvergence(CciRelationshipType type) => type is
        CciRelationshipType.BullishConvergence or CciRelationshipType.BearishConvergence;

    private static bool IsBullish(RsiRelationshipType type) => type is
        RsiRelationshipType.RegularBullishDivergence or RsiRelationshipType.HiddenBullishDivergence or
        RsiRelationshipType.BullishConvergence;

    private static bool IsBearish(RsiRelationshipType type) => type is
        RsiRelationshipType.RegularBearishDivergence or RsiRelationshipType.HiddenBearishDivergence or
        RsiRelationshipType.BearishConvergence;

    private static bool IsRegular(RsiRelationshipType type) => type is
        RsiRelationshipType.RegularBullishDivergence or RsiRelationshipType.RegularBearishDivergence;

    private static bool IsHidden(RsiRelationshipType type) => type is
        RsiRelationshipType.HiddenBullishDivergence or RsiRelationshipType.HiddenBearishDivergence;

    private static bool IsConvergence(RsiRelationshipType type) => type is
        RsiRelationshipType.BullishConvergence or RsiRelationshipType.BearishConvergence;
}
