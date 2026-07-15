using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.PriceAction;

/// <summary>
/// Builds same-timeframe composite setups from atomic <see cref="PriceActionEvent"/>s
/// and the active break-retest machine. Stateful per chart key.
/// </summary>
public sealed class PriceActionSetupComposer
{
    private readonly PriceActionSetupOptions _options;
    private ArmState? _retestArm;
    private ArmState? _sweepDisplacementArm;
    private ArmState? _sweepChoChArm;

    public PriceActionSetupComposer(PriceActionSetupOptions? options = null)
    {
        _options = options ?? new PriceActionSetupOptions();
        _options.Validate();
    }

    public PriceActionSnapshot Apply(
        PriceActionSnapshot atomic,
        Candle candle,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(atomic);
        ArgumentNullException.ThrowIfNull(candle);

        var emitted = new List<PriceActionSetup>(8);
        DateTimeOffset at = candle.CloseTime ?? candle.OpenTime;
        decimal close = candle.Prices.Close;

        ProcessRetestFamily(atomic, at, sequence, close, emitted);
        ProcessSweepDisplacement(atomic, at, sequence, close, emitted);
        ProcessSweepChoCh(atomic, at, sequence, close, emitted);

        // Still-armed setups surface every bar so agents/dashboard see live state.
        EmitArmedIfLive(_retestArm, emitted);
        EmitArmedIfLive(_sweepDisplacementArm, emitted);
        EmitArmedIfLive(_sweepChoChArm, emitted);

        return atomic with
        {
            Setups = emitted
                .OrderByDescending(item => item.Phase == PriceActionSetupPhase.Triggered)
                .ThenByDescending(item => item.Confidence)
                .ThenBy(item => item.Type)
                .ToArray()
        };
    }

    private void ProcessRetestFamily(
        PriceActionSnapshot atomic,
        DateTimeOffset at,
        long sequence,
        decimal close,
        List<PriceActionSetup> emitted)
    {
        // Arm from break / CHOCH events on this bar.
        PriceActionEvent? breakEvent = atomic.Events.FirstOrDefault(item =>
            item.Type is PriceActionEventType.BullishBreakOfStructure or
                PriceActionEventType.BearishBreakOfStructure or
                PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter);

        if (breakEvent is not null)
        {
            bool choch = breakEvent.Type is
                PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter;
            PriceActionSetupType type = breakEvent.Direction == PriceActionDirection.Bullish
                ? choch
                    ? PriceActionSetupType.BullishChoChRetestHold
                    : PriceActionSetupType.BullishBreakRetestHold
                : choch
                    ? PriceActionSetupType.BearishChoChRetestHold
                    : PriceActionSetupType.BearishBreakRetestHold;

            if (_options.IsEnabled(type))
            {
                _retestArm = new ArmState(
                    type,
                    breakEvent.Direction,
                    breakEvent.BrokenLevel ?? breakEvent.ReferenceLevel,
                    at,
                    sequence,
                    0,
                    [breakEvent.EventId],
                    breakEvent.Confidence,
                    choch);
            }
        }

        BreakRetestSnapshot retest = atomic.ActiveRetest;
        if (_retestArm is null)
            return;

        if (retest.State is BreakRetestState.RetestFailed)
        {
            emitted.Add(ToSetup(_retestArm, PriceActionSetupPhase.Invalidated, at, sequence, close,
                "RetestFailed", "The break-retest setup was invalidated by a deep close through the level."));
            _retestArm = null;
            return;
        }

        if (retest.State is BreakRetestState.Expired)
        {
            emitted.Add(ToSetup(_retestArm, PriceActionSetupPhase.Expired, at, sequence, close,
                "RetestExpired", "The break-retest setup expired before a hold was confirmed."));
            _retestArm = null;
            return;
        }

        PriceActionEvent? held = atomic.Events.FirstOrDefault(item =>
            (item.Type is PriceActionEventType.BullishRetestHeld or
                PriceActionEventType.BearishRetestHeld) &&
            item.Direction == _retestArm.Direction);

        if (held is not null || retest.State == BreakRetestState.RetestHeld)
        {
            decimal confidence = AverageConfidence(_retestArm.SourceConfidence, held?.Confidence ?? 88m);
            if (confidence >= _options.MinimumSetupConfidence)
            {
                var sources = _retestArm.SourceEventIds.ToList();
                if (held is not null)
                    sources.Add(held.EventId);
                ArmState arm = _retestArm with
                {
                    SourceEventIds = sources,
                    SourceConfidence = confidence
                };
                emitted.Add(ToSetup(arm, PriceActionSetupPhase.Triggered, at, sequence,
                    held?.RetestLevel ?? held?.ReferenceLevel ?? close,
                    "RetestHoldTriggered",
                    $"{arm.Type} triggered: level held after the structural break/CHOCH."));
            }

            _retestArm = null;
            return;
        }

        // Keep arm alive while awaiting / in progress.
        if (retest.State is BreakRetestState.AwaitingRetest or BreakRetestState.RetestInProgress or
            BreakRetestState.None)
        {
            _retestArm = _retestArm with { BarsArmed = _retestArm.BarsArmed + (breakEvent is null ? 1 : 0) };
        }
    }

    private void ProcessSweepDisplacement(
        PriceActionSnapshot atomic,
        DateTimeOffset at,
        long sequence,
        decimal close,
        List<PriceActionSetup> emitted)
    {
        PriceActionEvent? sweep = atomic.Events.FirstOrDefault(item =>
            item.Type is PriceActionEventType.SellSideLiquiditySweep or
                PriceActionEventType.BuySideLiquiditySweep);
        if (sweep is not null)
        {
            PriceActionSetupType type = sweep.Direction == PriceActionDirection.Bullish
                ? PriceActionSetupType.BullishSweepDisplacement
                : PriceActionSetupType.BearishSweepDisplacement;
            if (_options.IsEnabled(type))
            {
                _sweepDisplacementArm = new ArmState(
                    type,
                    sweep.Direction,
                    sweep.ReferenceLevel,
                    at,
                    sequence,
                    0,
                    [sweep.EventId],
                    sweep.Confidence,
                    IsChoCh: false);
            }
        }

        if (_sweepDisplacementArm is null)
            return;

        if (BreakEventInvalidatesSweep(_sweepDisplacementArm, atomic))
        {
            emitted.Add(ToSetup(_sweepDisplacementArm, PriceActionSetupPhase.Invalidated, at, sequence, close,
                "SweepInvalidated", "An opposing structural break invalidated the sweep-displacement setup."));
            _sweepDisplacementArm = null;
            return;
        }

        _sweepDisplacementArm = _sweepDisplacementArm with
        {
            BarsArmed = _sweepDisplacementArm.BarsArmed + (sweep is null ? 1 : 0)
        };

        if (_sweepDisplacementArm.BarsArmed > _options.SweepFollowThroughBars)
        {
            emitted.Add(ToSetup(_sweepDisplacementArm, PriceActionSetupPhase.Expired, at, sequence, close,
                "SweepFollowThroughExpired",
                "No same-direction displacement followed the liquidity sweep in time."));
            _sweepDisplacementArm = null;
            return;
        }

        PriceActionEvent? displacement = atomic.Events.FirstOrDefault(item =>
            (item.Type is PriceActionEventType.BullishDisplacement or
                PriceActionEventType.BearishDisplacement) &&
            item.Direction == _sweepDisplacementArm.Direction);

        if (displacement is null)
            return;

        decimal confidence = AverageConfidence(_sweepDisplacementArm.SourceConfidence, displacement.Confidence);
        if (confidence < _options.MinimumSetupConfidence)
            return;

        var sources = _sweepDisplacementArm.SourceEventIds.Append(displacement.EventId).ToArray();
        ArmState arm = _sweepDisplacementArm with
        {
            SourceEventIds = sources,
            SourceConfidence = confidence
        };
        emitted.Add(ToSetup(arm, PriceActionSetupPhase.Triggered, at, sequence, close,
            "SweepDisplacementTriggered",
            $"{arm.Type} triggered: liquidity sweep followed by directional displacement."));
        _sweepDisplacementArm = null;
    }

    private void ProcessSweepChoCh(
        PriceActionSnapshot atomic,
        DateTimeOffset at,
        long sequence,
        decimal close,
        List<PriceActionSetup> emitted)
    {
        PriceActionEvent? sweep = atomic.Events.FirstOrDefault(item =>
            item.Type is PriceActionEventType.SellSideLiquiditySweep or
                PriceActionEventType.BuySideLiquiditySweep);
        if (sweep is not null)
        {
            PriceActionSetupType type = sweep.Direction == PriceActionDirection.Bullish
                ? PriceActionSetupType.BullishSweepChoCh
                : PriceActionSetupType.BearishSweepChoCh;
            if (_options.IsEnabled(type))
            {
                _sweepChoChArm = new ArmState(
                    type,
                    sweep.Direction,
                    sweep.ReferenceLevel,
                    at,
                    sequence,
                    0,
                    [sweep.EventId],
                    sweep.Confidence,
                    IsChoCh: true);
            }
        }

        if (_sweepChoChArm is null)
            return;

        if (BreakEventInvalidatesSweep(_sweepChoChArm, atomic))
        {
            emitted.Add(ToSetup(_sweepChoChArm, PriceActionSetupPhase.Invalidated, at, sequence, close,
                "SweepInvalidated", "An opposing structural break invalidated the sweep-CHOCH setup."));
            _sweepChoChArm = null;
            return;
        }

        _sweepChoChArm = _sweepChoChArm with
        {
            BarsArmed = _sweepChoChArm.BarsArmed + (sweep is null ? 1 : 0)
        };

        if (_sweepChoChArm.BarsArmed > _options.ChoChFollowThroughBars)
        {
            emitted.Add(ToSetup(_sweepChoChArm, PriceActionSetupPhase.Expired, at, sequence, close,
                "SweepChoChExpired",
                "No change-of-character followed the liquidity sweep in time."));
            _sweepChoChArm = null;
            return;
        }

        PriceActionEvent? choch = atomic.Events.FirstOrDefault(item =>
            (item.Type is PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter or
                PriceActionEventType.BullishBreakOfStructure or
                PriceActionEventType.BearishBreakOfStructure) &&
            item.Direction == _sweepChoChArm.Direction);

        // Prefer true CHOCH; allow BOS in the sweep direction as structural follow-through.
        if (choch is null)
            return;

        decimal confidence = AverageConfidence(_sweepChoChArm.SourceConfidence, choch.Confidence);
        if (confidence < _options.MinimumSetupConfidence)
            return;

        var sources = _sweepChoChArm.SourceEventIds.Append(choch.EventId).ToArray();
        ArmState arm = _sweepChoChArm with
        {
            SourceEventIds = sources,
            SourceConfidence = confidence
        };
        emitted.Add(ToSetup(arm, PriceActionSetupPhase.Triggered, at, sequence,
            choch.BrokenLevel ?? close,
            "SweepChoChTriggered",
            $"{arm.Type} triggered: liquidity sweep followed by structural break/CHOCH."));
        _sweepChoChArm = null;
    }

    private static bool BreakEventInvalidatesSweep(ArmState arm, PriceActionSnapshot atomic)
    {
        PriceActionDirection opposite = arm.Direction == PriceActionDirection.Bullish
            ? PriceActionDirection.Bearish
            : PriceActionDirection.Bullish;
        return atomic.Events.Any(item =>
            item.Direction == opposite &&
            item.Type is (PriceActionEventType.BullishBreakOfStructure or
                PriceActionEventType.BearishBreakOfStructure or
                PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter));
    }

    private static void EmitArmedIfLive(ArmState? arm, List<PriceActionSetup> emitted)
    {
        if (arm is null)
            return;
        // Avoid duplicating if a terminal row for the same arm was already emitted.
        if (emitted.Any(item => item.SetupId == BuildSetupId(arm) &&
                item.Phase is not PriceActionSetupPhase.Armed))
        {
            return;
        }

        if (emitted.Any(item => item.SetupId == BuildSetupId(arm) && item.Phase == PriceActionSetupPhase.Armed))
            return;

        emitted.Add(ToSetup(arm, PriceActionSetupPhase.Armed, arm.ArmedAt, arm.ArmedSequence,
            arm.Level, "SetupArmed", $"{arm.Type} is armed and waiting for follow-through."));
    }

    private static PriceActionSetup ToSetup(
        ArmState arm,
        PriceActionSetupPhase phase,
        DateTimeOffset at,
        long sequence,
        decimal? entryReference,
        string reasonCode,
        string explanation) => new()
    {
        SetupId = BuildSetupId(arm),
        Type = arm.Type,
        Direction = arm.Direction,
        Phase = phase,
        ArmedAt = arm.ArmedAt,
        TriggeredAt = phase == PriceActionSetupPhase.Triggered ? at : null,
        ArmedSequence = arm.ArmedSequence,
        TriggeredSequence = phase == PriceActionSetupPhase.Triggered ? sequence : null,
        Confidence = Math.Clamp(arm.SourceConfidence, 0m, 100m),
        ReferenceLevel = arm.Level,
        EntryReference = entryReference,
        ReasonCode = reasonCode,
        Explanation = explanation,
        SourceEventIds = arm.SourceEventIds
    };

    private static string BuildSetupId(ArmState arm) =>
        $"{arm.Type}:{arm.ArmedSequence}:{arm.ArmedAt:O}";

    private static decimal AverageConfidence(decimal first, decimal second) =>
        Math.Clamp((first + second) / 2m, 0m, 100m);

    private sealed record ArmState(
        PriceActionSetupType Type,
        PriceActionDirection Direction,
        decimal? Level,
        DateTimeOffset ArmedAt,
        long ArmedSequence,
        int BarsArmed,
        IReadOnlyList<string> SourceEventIds,
        decimal SourceConfidence,
        bool IsChoCh);
}
