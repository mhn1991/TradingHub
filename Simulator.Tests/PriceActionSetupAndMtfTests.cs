using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.PriceAction;

namespace Simulator.Tests;

[TestFixture]
public sealed class PriceActionSetupAndMtfTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void SetupComposer_BreakThenRetestHold_TriggersBreakRetestSetup()
    {
        var analyzer = new PriceActionAnalyzer();
        var composer = new PriceActionSetupComposer();
        SwingPoint high = Swing(SwingType.High, 101m, Start.AddMinutes(-10));
        MarketStructureSnapshot structure = Structure(high: high, direction: MarketStructureDirection.Rising);

        Candle first = CandleAt(0, 100m, 101.05m, 99.8m, 100.8m);
        Candle breakCandle = CandleAt(1, 100.8m, 101.5m, 100.7m, 101.25m);
        Candle retest = CandleAt(2, 101.25m, 101.4m, 101.03m, 101.22m);

        PriceActionSnapshot s0 = analyzer.Update(first, [first], [high], [], structure, structure, Indicators(1m), 1);
        s0 = composer.Apply(s0, first, 1);

        PriceActionSnapshot s1 = analyzer.Update(breakCandle, [first, breakCandle], [high], [], structure, structure, Indicators(1m), 2);
        s1 = composer.Apply(s1, breakCandle, 2);
        Assert.That(
            s1.Setups.Any(item =>
                item.Type == PriceActionSetupType.BullishBreakRetestHold &&
                item.Phase == PriceActionSetupPhase.Armed),
            Is.True);

        PriceActionSnapshot s2 = analyzer.Update(retest, [first, breakCandle, retest], [high], [], structure, structure, Indicators(1m), 3);
        s2 = composer.Apply(s2, retest, 3);

        PriceActionSetup triggered = s2.Setups.Single(item =>
            item.Type == PriceActionSetupType.BullishBreakRetestHold &&
            item.Phase == PriceActionSetupPhase.Triggered);
        Assert.Multiple(() =>
        {
            Assert.That(triggered.ReferenceLevel, Is.EqualTo(101m));
            Assert.That(s2.HasTriggeredSetup(PriceActionDirection.Bullish, 50m), Is.True);
        });
    }

    [Test]
    public void SetupComposer_SweepThenDisplacement_TriggersSweepDisplacement()
    {
        var composer = new PriceActionSetupComposer();
        long seq = 1;
        DateTimeOffset t0 = Start;
        var sweep = new PriceActionEvent
        {
            EventId = "sweep",
            Type = PriceActionEventType.SellSideLiquiditySweep,
            Direction = PriceActionDirection.Bullish,
            ConfirmedAt = t0,
            ConfirmedSequence = seq,
            ReferenceLevel = 100m,
            Strength = 70m,
            Confidence = 68m,
            ReasonCode = "SellSideLiquiditySweep",
            Explanation = "test"
        };
        PriceActionSnapshot armed = composer.Apply(
            Snapshot([sweep]),
            CandleAt(0, 100.5m, 100.8m, 99.9m, 100.4m),
            seq);
        Assert.That(
            armed.Setups.Any(s =>
                s.Type == PriceActionSetupType.BullishSweepDisplacement &&
                s.Phase == PriceActionSetupPhase.Armed),
            Is.True);

        var displacement = new PriceActionEvent
        {
            EventId = "disp",
            Type = PriceActionEventType.BullishDisplacement,
            Direction = PriceActionDirection.Bullish,
            ConfirmedAt = t0.AddMinutes(5),
            ConfirmedSequence = seq + 1,
            Strength = 80m,
            Confidence = 75m,
            ReasonCode = "BullishDisplacementConfirmed",
            Explanation = "test"
        };
        PriceActionSnapshot triggered = composer.Apply(
            Snapshot([displacement]),
            CandleAt(1, 100.4m, 101.5m, 100.3m, 101.4m),
            seq + 1);

        Assert.That(
            triggered.Setups.Any(s =>
                s.Type == PriceActionSetupType.BullishSweepDisplacement &&
                s.Phase == PriceActionSetupPhase.Triggered),
            Is.True);
    }

    [Test]
    public void SetupComposer_OpposingBreak_InvalidatesSweepChoChArm()
    {
        var composer = new PriceActionSetupComposer();
        var sweep = new PriceActionEvent
        {
            EventId = "bull-sweep",
            Type = PriceActionEventType.SellSideLiquiditySweep,
            Direction = PriceActionDirection.Bullish,
            ConfirmedAt = Start,
            ConfirmedSequence = 1,
            ReferenceLevel = 100m,
            Strength = 70m,
            Confidence = 70m,
            ReasonCode = "SellSideLiquiditySweep",
            Explanation = "test"
        };
        composer.Apply(Snapshot([sweep]), CandleAt(0, 100m, 101m, 99m, 100.5m), 1);

        var opposingBreak = new PriceActionEvent
        {
            EventId = "bear-break",
            Type = PriceActionEventType.BearishChangeOfCharacter,
            Direction = PriceActionDirection.Bearish,
            ConfirmedAt = Start.AddMinutes(5),
            ConfirmedSequence = 2,
            ReferenceLevel = 99m,
            Strength = 80m,
            Confidence = 80m,
            ReasonCode = "BearishChoCh",
            Explanation = "test"
        };
        PriceActionSnapshot invalidated = composer.Apply(
            Snapshot([opposingBreak]),
            CandleAt(1, 100.5m, 100.6m, 98.8m, 99m),
            2);

        Assert.That(
            invalidated.Setups.Any(setup =>
                setup.Type == PriceActionSetupType.BullishSweepChoCh &&
                setup.Phase == PriceActionSetupPhase.Invalidated),
            Is.True);
    }

    [Test]
    public void MtfPolicy_RequiredWithContext_BlocksWhenHtfNotArmed()
    {
        AnalysisSnapshot trend = Analysis(
            structure: MarketStructureDirection.Sideways,
            priceAction: PriceActionSnapshot.Empty);
        AnalysisSnapshot entry = Analysis(
            structure: MarketStructureDirection.Rising,
            priceAction: new PriceActionSnapshot
            {
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "ltf",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Triggered,
                        ArmedAt = Start,
                        TriggeredAt = Start,
                        ArmedSequence = 1,
                        TriggeredSequence = 2,
                        Confidence = 80m,
                        ReferenceLevel = 100m,
                        ReasonCode = "RetestHoldTriggered",
                        Explanation = "test"
                    }
                ]
            });

        PriceActionGateResult gate = MultiTimeframePriceActionPolicy.EvaluateEntry(
            trend,
            entry,
            PriceActionDirection.Bullish,
            PriceActionConfirmationMode.RequiredWithContext,
            new MultiTimeframePriceActionOptions { RequireHtfArm = true });

        Assert.That(gate.Allowed, Is.False);
        Assert.That(gate.ReasonCode, Is.EqualTo("HtfContextNotArmed"));
    }

    [Test]
    public void MtfPolicy_RequiredWithContext_AllowsWhenHtfContinuationAndLtfTriggered()
    {
        AnalysisSnapshot trend = Analysis(
            structure: MarketStructureDirection.Rising,
            priceAction: new PriceActionSnapshot
            {
                Bias = PriceActionDirection.Bullish,
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "htf",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Armed,
                        ArmedAt = Start,
                        ArmedSequence = 1,
                        Confidence = 70m,
                        ReferenceLevel = 100m,
                        ReasonCode = "SetupArmed",
                        Explanation = "htf armed"
                    }
                ]
            });
        AnalysisSnapshot entry = Analysis(
            structure: MarketStructureDirection.Rising,
            atr: 1m,
            priceAction: new PriceActionSnapshot
            {
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "ltf",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Triggered,
                        ArmedAt = Start,
                        TriggeredAt = Start.AddMinutes(5),
                        ArmedSequence = 2,
                        TriggeredSequence = 3,
                        Confidence = 85m,
                        ReferenceLevel = 100.2m,
                        EntryReference = 100.3m,
                        ReasonCode = "RetestHoldTriggered",
                        Explanation = "ltf trigger"
                    }
                ]
            });

        PriceActionGateResult gate = MultiTimeframePriceActionPolicy.EvaluateEntry(
            trend,
            entry,
            PriceActionDirection.Bullish,
            PriceActionConfirmationMode.RequiredWithContext);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Allowed, Is.True);
            Assert.That(gate.TriggeredSetup, Is.Not.Null);
            Assert.That(gate.Context.BullishContinuationArmed, Is.True);
            Assert.That(gate.ConfidenceBoost, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void MtfPolicy_SoftMode_AlwaysAllowsWithOptionalBoost()
    {
        AnalysisSnapshot trend = Analysis(MarketStructureDirection.Falling, PriceActionSnapshot.Empty);
        AnalysisSnapshot entry = Analysis(MarketStructureDirection.Rising, PriceActionSnapshot.Empty);

        PriceActionGateResult gate = MultiTimeframePriceActionPolicy.EvaluateEntry(
            trend,
            entry,
            PriceActionDirection.Bullish,
            PriceActionConfirmationMode.Soft);

        Assert.That(gate.Allowed, Is.True);
    }

    [Test]
    public void MtfPolicy_WeakStructureAlone_DoesNotArmContext()
    {
        AnalysisSnapshot trend = Analysis(
            MarketStructureDirection.Rising,
            PriceActionSnapshot.Empty) with
        {
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = MarketStructureDirection.Rising,
                Strength = 40m
            }
        };

        MultiTimeframePriceActionContext context = MultiTimeframePriceActionPolicy.BuildContext(
            trend,
            new MultiTimeframePriceActionOptions { MinimumHtfSetupConfidence = 55m });

        Assert.That(context.BullishContinuationArmed, Is.False);
    }

    [Test]
    public void MtfPolicy_RequiredWithContext_DoesNotUseExpiredHtfArm()
    {
        AnalysisSnapshot trend = Analysis(
            structure: MarketStructureDirection.Sideways,
            priceAction: new PriceActionSnapshot
            {
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "stale-htf",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Armed,
                        ArmedAt = Start,
                        ArmedSequence = 1,
                        Confidence = 80m,
                        ReferenceLevel = 100m,
                        ReasonCode = "SetupArmed",
                        Explanation = "stale"
                    }
                ]
            }) with
        {
            AvailableAt = Start.AddHours(3)
        };
        AnalysisSnapshot entry = Analysis(
            structure: MarketStructureDirection.Rising,
            priceAction: new PriceActionSnapshot
            {
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "ltf",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Triggered,
                        ArmedAt = Start.AddHours(3),
                        TriggeredAt = Start.AddHours(3),
                        ArmedSequence = 2,
                        TriggeredSequence = 3,
                        Confidence = 80m,
                        ReferenceLevel = 100m,
                        ReasonCode = "RetestHoldTriggered",
                        Explanation = "test"
                    }
                ]
            });

        PriceActionGateResult gate = MultiTimeframePriceActionPolicy.EvaluateEntry(
            trend,
            entry,
            PriceActionDirection.Bullish,
            PriceActionConfirmationMode.RequiredWithContext,
            new MultiTimeframePriceActionOptions
            {
                RequireHtfArm = true,
                HtfArmExpiryBars = 20
            });

        Assert.Multiple(() =>
        {
            Assert.That(gate.Allowed, Is.False);
            Assert.That(gate.ReasonCode, Is.EqualTo("HtfContextNotArmed"));
            Assert.That(gate.Context.HtfSetup, Is.Null);
        });
    }

    private static PriceActionSnapshot Snapshot(IReadOnlyList<PriceActionEvent> events) => new()
    {
        Events = events,
        Bias = events.FirstOrDefault()?.Direction ?? PriceActionDirection.Neutral
    };

    private static AnalysisSnapshot Analysis(
        MarketStructureDirection structure,
        PriceActionSnapshot priceAction,
        decimal atr = 1m) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = Start,
        Version = 1,
        LatestCandle = CandleAt(0, 100m, 100.5m, 99.5m, 100.2m),
        Indicators = new IndicatorSnapshot
        {
            Atr = atr,
            BollingerAnalysis = BollingerAnalysisSnapshot.Empty,
            AtrAnalysis = AtrAnalysisSnapshot.Empty,
            RsiAnalysis = RsiAnalysisSnapshot.Empty
        },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketStructure = new MarketStructureSnapshot
        {
            Direction = structure,
            Strength = 80m
        },
        PriceAction = priceAction,
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };

    private static IndicatorSnapshot Indicators(decimal atr) => new()
    {
        Atr = atr,
        BollingerAnalysis = BollingerAnalysisSnapshot.Empty,
        AtrAnalysis = AtrAnalysisSnapshot.Empty,
        RsiAnalysis = RsiAnalysisSnapshot.Empty
    };

    private static MarketStructureSnapshot Structure(
        SwingPoint? high = null,
        SwingPoint? low = null,
        MarketStructureDirection direction = MarketStructureDirection.Unknown) => new()
    {
        Direction = direction,
        PreviousDirection = direction,
        LastSwingHigh = high,
        LastSwingLow = low
    };

    private static SwingPoint Swing(SwingType type, decimal price, DateTimeOffset pivot) => new()
    {
        Type = type,
        Price = price,
        PivotTime = pivot,
        ConfirmedAt = pivot.AddMinutes(5),
        Strength = 3
    };

    private static Candle CandleAt(
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => TestCandles.Create(
        Instrument,
        Start.AddMinutes(index * 5),
        Interval,
        open,
        high,
        low,
        close);
}
