using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>
/// Multi-timeframe price-action context: HTF arms/vetoes LTF composite setups.
/// See <c>PRICE_ACTION_SETUPS_AND_MTF.md</c>.
/// </summary>
public sealed record MultiTimeframePriceActionOptions
{
    public bool RequireHtfArm { get; init; } = true;
    public bool VetoAgainstHtfStructure { get; init; } = true;
    /// <summary>
    /// When set, LTF setup reference/entry must lie within this many trigger-TF ATRs
    /// of the HTF context level (if one was recorded).
    /// </summary>
    public decimal ContextLevelProximityAtr { get; init; } = 0.75m;
    /// <summary>
    /// HTF arm expires after this many HTF events/bars of age, measured using
    /// HTF snapshot AvailableAt vs arm time when only snapshots are available —
    /// approximated via version/time on the HTF snapshot.
    /// </summary>
    public int HtfArmExpiryBars { get; init; } = 20;
    public decimal MinimumHtfSetupConfidence { get; init; } = 55m;
    public decimal MinimumLtfSetupConfidence { get; init; } = 55m;

    public void Validate()
    {
        if (ContextLevelProximityAtr < 0m ||
            HtfArmExpiryBars is < 1 or > 10_000 ||
            MinimumHtfSetupConfidence is < 0m or > 100m ||
            MinimumLtfSetupConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(MultiTimeframePriceActionOptions));
        }
    }
}

public sealed record MultiTimeframePriceActionContext
{
    public static MultiTimeframePriceActionContext Empty { get; } = new()
    {
        TrendBias = PriceActionDirection.Neutral,
        TrendStructure = MarketStructureDirection.Unknown
    };

    public required PriceActionDirection TrendBias { get; init; }
    public required MarketStructureDirection TrendStructure { get; init; }
    public bool BullishContinuationArmed { get; init; }
    public bool BearishContinuationArmed { get; init; }
    public bool BullishReversalArmed { get; init; }
    public bool BearishReversalArmed { get; init; }
    public decimal? ContextLevel { get; init; }
    public DateTimeOffset? ContextArmedAt { get; init; }
    public string? ContextReason { get; init; }
    public PriceActionSetup? HtfSetup { get; init; }

    public bool AnyArm =>
        BullishContinuationArmed || BearishContinuationArmed ||
        BullishReversalArmed || BearishReversalArmed;
}

public sealed record PriceActionGateResult
{
    public required bool Allowed { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public MultiTimeframePriceActionContext Context { get; init; } =
        MultiTimeframePriceActionContext.Empty;
    public PriceActionSetup? TriggeredSetup { get; init; }
    public decimal ConfidenceBoost { get; init; }
}

/// <summary>
/// Evaluates HTF context and whether an LTF composite setup may fire an entry.
/// Stateless: arms are derived each call from HTF snapshot setups/events.
/// </summary>
public static class MultiTimeframePriceActionPolicy
{
    public static readonly IReadOnlyList<PriceActionSetupType> ContinuationSetups =
    [
        PriceActionSetupType.BullishBreakRetestHold,
        PriceActionSetupType.BearishBreakRetestHold
    ];

    public static readonly IReadOnlyList<PriceActionSetupType> ReversalSetups =
    [
        PriceActionSetupType.BullishChoChRetestHold,
        PriceActionSetupType.BearishChoChRetestHold,
        PriceActionSetupType.BullishSweepDisplacement,
        PriceActionSetupType.BearishSweepDisplacement,
        PriceActionSetupType.BullishSweepChoCh,
        PriceActionSetupType.BearishSweepChoCh
    ];

    public static readonly IReadOnlyList<PriceActionSetupType> AllCoreSetups =
        [.. ContinuationSetups, .. ReversalSetups];

    public static MultiTimeframePriceActionContext BuildContext(
        AnalysisSnapshot trend,
        MultiTimeframePriceActionOptions? options = null)
    {
        options ??= new MultiTimeframePriceActionOptions();
        options.Validate();

        PriceActionDirection bias = trend.PriceAction.Bias;
        MarketStructureDirection structure = trend.MarketStructure.Direction;

        // Prefer currently armed or recently triggered HTF composite setups.
        PriceActionSetup? htfSetup = trend.PriceAction.Setups
            .Where(setup =>
                setup.Confidence >= options.MinimumHtfSetupConfidence &&
                (setup.Phase is PriceActionSetupPhase.Armed or PriceActionSetupPhase.Triggered) &&
                IsFresh(trend, setup.ArmedAt, options.HtfArmExpiryBars))
            .OrderByDescending(setup => setup.Phase == PriceActionSetupPhase.Triggered)
            .ThenByDescending(setup => setup.Confidence)
            .ThenByDescending(setup => setup.ArmedAt)
            .FirstOrDefault();

        bool bullCont = false;
        bool bearCont = false;
        bool bullRev = false;
        bool bearRev = false;
        decimal? level = null;
        DateTimeOffset? armedAt = null;
        string? reason = null;

        if (htfSetup is not null)
        {
            level = htfSetup.ReferenceLevel ?? htfSetup.EntryReference;
            armedAt = htfSetup.ArmedAt;
            reason = $"{htfSetup.Type}/{htfSetup.Phase}";
            switch (htfSetup.Type)
            {
                case PriceActionSetupType.BullishBreakRetestHold:
                    bullCont = true;
                    break;
                case PriceActionSetupType.BearishBreakRetestHold:
                    bearCont = true;
                    break;
                case PriceActionSetupType.BullishChoChRetestHold:
                case PriceActionSetupType.BullishSweepDisplacement:
                case PriceActionSetupType.BullishSweepChoCh:
                    bullRev = true;
                    break;
                case PriceActionSetupType.BearishChoChRetestHold:
                case PriceActionSetupType.BearishSweepDisplacement:
                case PriceActionSetupType.BearishSweepChoCh:
                    bearRev = true;
                    break;
            }
        }
        else
        {
            // Fallback: atomic HTF events + structure.
            bool bullishStructure = structure == MarketStructureDirection.Rising &&
                trend.MarketStructure.Strength >= options.MinimumHtfSetupConfidence;
            bool bullishBias = bias == PriceActionDirection.Bullish &&
                trend.PriceAction.BullishScore >= options.MinimumHtfSetupConfidence;
            bool bullishBreak = trend.PriceAction.Events.Any(item =>
                    item.Type is PriceActionEventType.BullishBreakOfStructure &&
                    item.Confidence >= options.MinimumHtfSetupConfidence);
            if (bullishStructure || bullishBias || bullishBreak)
            {
                bullCont = structure != MarketStructureDirection.Falling;
            }

            bool bearishStructure = structure == MarketStructureDirection.Falling &&
                trend.MarketStructure.Strength >= options.MinimumHtfSetupConfidence;
            bool bearishBias = bias == PriceActionDirection.Bearish &&
                trend.PriceAction.BearishScore >= options.MinimumHtfSetupConfidence;
            bool bearishBreak = trend.PriceAction.Events.Any(item =>
                    item.Type is PriceActionEventType.BearishBreakOfStructure &&
                    item.Confidence >= options.MinimumHtfSetupConfidence);
            if (bearishStructure || bearishBias || bearishBreak)
            {
                bearCont = structure != MarketStructureDirection.Rising;
            }

            if (trend.PriceAction.Events.Any(item =>
                    (item.Type is PriceActionEventType.BullishChangeOfCharacter or
                        PriceActionEventType.SellSideLiquiditySweep) &&
                    item.Confidence >= options.MinimumHtfSetupConfidence))
            {
                bullRev = true;
                reason = "HtfAtomicReversalBullish";
            }

            if (trend.PriceAction.Events.Any(item =>
                    (item.Type is PriceActionEventType.BearishChangeOfCharacter or
                        PriceActionEventType.BuySideLiquiditySweep) &&
                    item.Confidence >= options.MinimumHtfSetupConfidence))
            {
                bearRev = true;
                reason = "HtfAtomicReversalBearish";
            }

            reason ??= structure != MarketStructureDirection.Unknown
                ? $"HtfStructure:{structure}"
                : $"HtfBias:{bias}";
        }

        // Soft structure veto on conflicting continuation arms.
        if (options.VetoAgainstHtfStructure)
        {
            if (structure == MarketStructureDirection.Falling)
                bullCont = false;
            if (structure == MarketStructureDirection.Rising)
                bearCont = false;
        }

        return new MultiTimeframePriceActionContext
        {
            TrendBias = bias,
            TrendStructure = structure,
            BullishContinuationArmed = bullCont,
            BearishContinuationArmed = bearCont,
            BullishReversalArmed = bullRev,
            BearishReversalArmed = bearRev,
            ContextLevel = level,
            ContextArmedAt = armedAt,
            ContextReason = reason,
            HtfSetup = htfSetup
        };
    }

    public static PriceActionGateResult EvaluateEntry(
        AnalysisSnapshot trend,
        AnalysisSnapshot entry,
        PriceActionDirection expectedDirection,
        PriceActionConfirmationMode mode,
        MultiTimeframePriceActionOptions? mtfOptions = null,
        IReadOnlyCollection<PriceActionSetupType>? allowedLtfSetups = null)
    {
        mtfOptions ??= new MultiTimeframePriceActionOptions();
        mtfOptions.Validate();
        allowedLtfSetups ??= AllCoreSetups;

        MultiTimeframePriceActionContext context = BuildContext(trend, mtfOptions);

        if (mode == PriceActionConfirmationMode.Disabled)
        {
            return new PriceActionGateResult
            {
                Allowed = true,
                ReasonCode = "PriceActionDisabled",
                Explanation = "Price-action confirmation is disabled.",
                Context = context
            };
        }

        PriceActionSetup? triggered = entry.PriceAction.GetBestTriggeredSetup(
            expectedDirection,
            mtfOptions.MinimumLtfSetupConfidence,
            allowedLtfSetups);

        bool atomicTrigger = entry.PriceAction.HasConfirmedTrigger(
            expectedDirection,
            mtfOptions.MinimumLtfSetupConfidence);

        bool contextArmed = expectedDirection == PriceActionDirection.Bullish
            ? context.BullishContinuationArmed || context.BullishReversalArmed
            : context.BearishContinuationArmed || context.BearishReversalArmed;

        if (mode == PriceActionConfirmationMode.Soft)
        {
            // Soft: always allow; setups only boost confidence / annotate.
            decimal boost = 0m;
            if (triggered is not null)
                boost += 8m;
            else if (atomicTrigger)
                boost += 4m;
            if (contextArmed)
                boost += 4m;

            return new PriceActionGateResult
            {
                Allowed = true,
                ReasonCode = triggered is not null
                    ? "SoftSetupPresent"
                    : atomicTrigger
                        ? "SoftAtomicTrigger"
                        : "SoftNoSetup",
                Explanation = triggered is not null
                    ? $"Soft mode: LTF setup {triggered.Type} present."
                    : "Soft mode: no hard PA requirement.",
                Context = context,
                TriggeredSetup = triggered,
                ConfidenceBoost = boost
            };
        }

        // Hard modes: opposing HTF structure without a reversal arm vetoes entry.
        if (mtfOptions.VetoAgainstHtfStructure)
        {
            if (expectedDirection == PriceActionDirection.Bullish &&
                context.TrendStructure == MarketStructureDirection.Falling &&
                !context.BullishReversalArmed)
            {
                return new PriceActionGateResult
                {
                    Allowed = false,
                    ReasonCode = "HtfStructureVeto",
                    Explanation = "Higher-timeframe structure is falling and no bullish reversal arm is active.",
                    Context = context
                };
            }

            if (expectedDirection == PriceActionDirection.Bearish &&
                context.TrendStructure == MarketStructureDirection.Rising &&
                !context.BearishReversalArmed)
            {
                return new PriceActionGateResult
                {
                    Allowed = false,
                    ReasonCode = "HtfStructureVeto",
                    Explanation = "Higher-timeframe structure is rising and no bearish reversal arm is active.",
                    Context = context
                };
            }
        }

        // Required: need LTF triggered setup OR (legacy) atomic trigger.
        if (triggered is null && !atomicTrigger)
        {
            return new PriceActionGateResult
            {
                Allowed = false,
                ReasonCode = "LtfSetupNotTriggered",
                Explanation =
                    "Waiting for a triggered LTF price-action setup (or legacy atomic trigger).",
                Context = context
            };
        }

        if (mode == PriceActionConfirmationMode.RequiredWithContext)
        {
            if (mtfOptions.RequireHtfArm && !contextArmed)
            {
                return new PriceActionGateResult
                {
                    Allowed = false,
                    ReasonCode = "HtfContextNotArmed",
                    Explanation =
                        "RequiredWithContext: higher-timeframe context is not armed for this direction.",
                    Context = context,
                    TriggeredSetup = triggered
                };
            }

            if (triggered is not null &&
                context.ContextLevel is decimal ctxLevel &&
                entry.Indicators.Atr is decimal atr and > 0m &&
                mtfOptions.ContextLevelProximityAtr > 0m)
            {
                decimal reference = triggered.EntryReference ??
                    triggered.ReferenceLevel ??
                    entry.LatestCandle.Prices.Close;
                decimal distanceAtr = Math.Abs(reference - ctxLevel) / atr;
                if (distanceAtr > mtfOptions.ContextLevelProximityAtr)
                {
                    return new PriceActionGateResult
                    {
                        Allowed = false,
                        ReasonCode = "ContextLevelTooFar",
                        Explanation =
                            $"LTF setup is {distanceAtr:F2} ATR from HTF context level {ctxLevel}.",
                        Context = context,
                        TriggeredSetup = triggered
                    };
                }
            }

            // Continuation LTF under a pure reversal HTF arm is rejected.
            if (triggered is not null && contextArmed)
            {
                bool isContinuation = ContinuationSetups.Contains(triggered.Type);
                bool contArmed = expectedDirection == PriceActionDirection.Bullish
                    ? context.BullishContinuationArmed
                    : context.BearishContinuationArmed;
                bool revArmed = expectedDirection == PriceActionDirection.Bullish
                    ? context.BullishReversalArmed
                    : context.BearishReversalArmed;

                if (isContinuation && !contArmed && revArmed)
                {
                    return new PriceActionGateResult
                    {
                        Allowed = false,
                        ReasonCode = "SetupFamilyMismatch",
                        Explanation =
                            "HTF is armed for reversal but LTF fired a continuation retest setup.",
                        Context = context,
                        TriggeredSetup = triggered
                    };
                }
            }
        }

        decimal confBoost = triggered is not null ? 10m : 5m;
        if (contextArmed)
            confBoost += 5m;

        return new PriceActionGateResult
        {
            Allowed = true,
            ReasonCode = triggered is not null ? "LtfSetupTriggered" : "LtfAtomicTrigger",
            Explanation = triggered is not null
                ? $"LTF setup {triggered.Type} triggered under HTF context {context.ContextReason}."
                : "Legacy atomic price-action trigger accepted.",
            Context = context,
            TriggeredSetup = triggered,
            ConfidenceBoost = confBoost
        };
    }

    private static bool IsFresh(
        AnalysisSnapshot snapshot,
        DateTimeOffset armedAt,
        int maximumBars)
    {
        if (armedAt > snapshot.AvailableAt)
            return false;

        DateTimeOffset expiresAt = armedAt;
        for (int index = 0; index < maximumBars; index++)
            expiresAt = snapshot.Interval.AddTo(expiresAt);

        return snapshot.AvailableAt <= expiresAt;
    }
}
