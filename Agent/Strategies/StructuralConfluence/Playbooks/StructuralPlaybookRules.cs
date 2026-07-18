using Agent.Strategies.StructuralConfluence.Evidence;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

internal readonly record struct CciAssessment(EvidenceAlignment Alignment, decimal Quality, string State);

internal static class StructuralPlaybookRules
{
    public static TimeSpan Bars(BarInterval interval, int count) =>
        TimeSpan.FromSeconds(BarIntervalParser.ApproximateSeconds(interval) * count);

    public static bool IsFresh(DateTimeOffset eventAt, DateTimeOffset now, BarInterval interval, int bars) =>
        eventAt <= now && now - eventAt <= Bars(interval, bars);

    public static (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) Trigger(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal minimumConfidence,
        int maximumBars,
        DateTimeOffset notBefore)
    {
        DateTimeOffset earliest = evidence.AvailableAt - Bars(evidence.Trigger.Interval, maximumBars);
        PriceActionEvent? triggerEvent = evidence.TriggerEvidence.Events
            .Where(item => item.Direction == direction && item.Confidence >= minimumConfidence &&
                item.ConfirmedAt >= earliest && item.ConfirmedAt >= notBefore && IsTriggerType(item.Type))
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.ConfirmedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .FirstOrDefault();
        PriceActionSetup? setup = evidence.TriggerEvidence.Setups
            .Where(item => item.Direction == direction && item.Phase == PriceActionSetupPhase.Triggered &&
                item.Confidence >= minimumConfidence && item.TriggeredAt >= earliest && item.TriggeredAt >= notBefore)
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.TriggeredAt)
            .ThenBy(item => item.SetupId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (setup is not null && (triggerEvent is null || setup.Confidence > triggerEvent.Confidence))
            return (null, setup, Math.Clamp(setup.Confidence, 0m, 100m));
        return (triggerEvent, null, triggerEvent is null ? 0m : Math.Clamp(triggerEvent.Confidence, 0m, 100m));
    }

    public static CciAssessment AssessCci(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        bool continuation)
    {
        decimal? cci = evidence.Indicators.Cci;
        CciAnalysisSnapshot analysis = evidence.Indicators.CciAnalysis;
        if (!cci.HasValue || analysis.SampleCount == 0)
            return new CciAssessment(EvidenceAlignment.Unavailable, 0m, "Unavailable");

        bool bullish = direction == PriceActionDirection.Bullish;
        CciRelationshipType relationship = analysis.LatestRelationship?.Type ?? CciRelationshipType.None;
        bool alignedRelationship = bullish
            ? relationship is CciRelationshipType.RegularBullishDivergence or CciRelationshipType.HiddenBullishDivergence or CciRelationshipType.BullishConvergence
            : relationship is CciRelationshipType.RegularBearishDivergence or CciRelationshipType.HiddenBearishDivergence or CciRelationshipType.BearishConvergence;
        bool conflictRelationship = bullish
            ? relationship is CciRelationshipType.RegularBearishDivergence or CciRelationshipType.HiddenBearishDivergence
            : relationship is CciRelationshipType.RegularBullishDivergence or CciRelationshipType.HiddenBullishDivergence;

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
            return new CciAssessment(EvidenceAlignment.Conflicting, 20m, $"Conflicting:{relationship}");
        if (aligned)
            return new CciAssessment(EvidenceAlignment.Aligned, Math.Clamp(60m + Math.Abs(analysis.MomentumChange ?? 0m) / 5m, 0m, 100m), $"Aligned:{relationship}");
        return new CciAssessment(EvidenceAlignment.Neutral, 45m, "Neutral");
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
            : Math.Clamp(gates.Min(item => item.Quality) +
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

    private static bool IsTriggerType(PriceActionEventType type) => type is
        PriceActionEventType.BullishRetestHeld or PriceActionEventType.BearishRetestHeld or
        PriceActionEventType.BullishRejection or PriceActionEventType.BearishRejection or
        PriceActionEventType.BullishDisplacement or PriceActionEventType.BearishDisplacement or
        PriceActionEventType.BullishCompressionBreakout or PriceActionEventType.BearishCompressionBreakout or
        PriceActionEventType.BullishChangeOfCharacter or PriceActionEventType.BearishChangeOfCharacter or
        PriceActionEventType.BullishBreakOfStructure or PriceActionEventType.BearishBreakOfStructure or
        PriceActionEventType.BullishPullback or PriceActionEventType.BearishPullback;
}
