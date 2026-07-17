using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;
using ChartAnnotator.PriceAction;

namespace Simulator.Tests;

[TestFixture]
public sealed class PriceActionAndCalibrationTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/JPY");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void BullishBreakOfStructure_RequiresCompletedCloseBeyondAtrMargin()
    {
        var analyzer = new PriceActionAnalyzer();
        SwingPoint high = Swing(SwingType.High, 101m, Start.AddMinutes(-10));
        MarketStructureSnapshot structure = Structure(high: high, direction: MarketStructureDirection.Rising);
        Candle first = CandleAt(0, 100m, 101.05m, 99.8m, 100.8m);
        Candle wickOnly = CandleAt(1, 100.8m, 101.4m, 100.7m, 101.05m);

        analyzer.Update(first, [first], [high], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot wickResult = analyzer.Update(
            wickOnly,
            [first, wickOnly],
            [high],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(
            wickResult.Events.Any(item => item.Type == PriceActionEventType.BullishBreakOfStructure),
            Is.False,
            "A wick beyond structure must not be treated as a break without a qualifying close.");

        var confirmedAnalyzer = new PriceActionAnalyzer();
        Candle confirmed = CandleAt(1, 100.8m, 101.5m, 100.7m, 101.25m);
        confirmedAnalyzer.Update(first, [first], [high], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = confirmedAnalyzer.Update(
            confirmed,
            [first, confirmed],
            [high],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        PriceActionEvent eventItem = result.Events.Single(
            item => item.Type == PriceActionEventType.BullishBreakOfStructure);
        Assert.Multiple(() =>
        {
            Assert.That(eventItem.Direction, Is.EqualTo(PriceActionDirection.Bullish));
            Assert.That(eventItem.BrokenLevel, Is.EqualTo(101m));
            Assert.That(eventItem.ConfirmedAt, Is.EqualTo(confirmed.CloseTime));
            Assert.That(eventItem.ReasonCode, Is.EqualTo("BullishBreakOfStructure"));
        });
    }

    [Test]
    public void BreakRetest_IsConfirmedOnlyByLaterCompletedCandle()
    {
        var analyzer = new PriceActionAnalyzer();
        SwingPoint high = Swing(SwingType.High, 101m, Start.AddMinutes(-10));
        MarketStructureSnapshot structure = Structure(high: high, direction: MarketStructureDirection.Rising);
        Candle first = CandleAt(0, 100m, 101.05m, 99.8m, 100.8m);
        Candle breakCandle = CandleAt(1, 100.8m, 101.5m, 100.7m, 101.25m);
        Candle retest = CandleAt(2, 101.25m, 101.4m, 101.03m, 101.22m);

        analyzer.Update(first, [first], [high], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot breakResult = analyzer.Update(
            breakCandle,
            [first, breakCandle],
            [high],
            [],
            structure,
            structure,
            Indicators(1m),
            2);
        PriceActionSnapshot retestResult = analyzer.Update(
            retest,
            [first, breakCandle, retest],
            [high],
            [],
            structure,
            structure,
            Indicators(1m),
            3);

        Assert.Multiple(() =>
        {
            Assert.That(breakResult.ActiveRetest.State, Is.EqualTo(BreakRetestState.AwaitingRetest));
            Assert.That(
                breakResult.Events.Any(item => item.Type == PriceActionEventType.BullishRetestHeld),
                Is.False);
            Assert.That(retestResult.ActiveRetest.State, Is.EqualTo(BreakRetestState.RetestHeld));
            Assert.That(
                retestResult.Events.Any(item => item.Type == PriceActionEventType.BullishRetestHeld),
                Is.True);
        });
    }

    [Test]
    public void BullishPullback_InEstablishedUptrendNearSupport_IsDetected()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Rising) with
        {
            ConsecutiveHigherHighs = 2,
            ConsecutiveHigherLows = 2
        };
        SwingPoint support = Swing(SwingType.Low, 100.4m, Start.AddMinutes(-10));
        Candle first = CandleAt(0, 100m, 100.6m, 99.8m, 100.4m);
        // Retraces (close < open) but stays close to the support swing below it.
        Candle pullback = CandleAt(1, 101m, 101.2m, 100.5m, 100.7m);

        analyzer.Update(first, [first], [support], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            pullback,
            [first, pullback],
            [support],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        PriceActionEvent eventItem = result.Events.Single(item => item.Type == PriceActionEventType.BullishPullback);
        Assert.Multiple(() =>
        {
            Assert.That(eventItem.Direction, Is.EqualTo(PriceActionDirection.Bullish));
            Assert.That(eventItem.ReasonCode, Is.EqualTo("BullishTrendPullback"));
            Assert.That(eventItem.ReferenceLevel, Is.EqualTo(100.4m));
        });
    }

    [Test]
    public void Pullback_WithoutEstablishedTrend_IsNotDetected()
    {
        var analyzer = new PriceActionAnalyzer();
        // Rising direction but consecutive counts fall short of MinimumPullbackTrendStrength.
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Rising) with
        {
            ConsecutiveHigherHighs = 1,
            ConsecutiveHigherLows = 1
        };
        SwingPoint support = Swing(SwingType.Low, 100.4m, Start.AddMinutes(-10));
        Candle first = CandleAt(0, 100m, 100.6m, 99.8m, 100.4m);
        Candle pullback = CandleAt(1, 101m, 101.2m, 100.5m, 100.7m);

        analyzer.Update(first, [first], [support], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            pullback,
            [first, pullback],
            [support],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(
            result.Events.Any(item => item.Type is PriceActionEventType.BullishPullback or PriceActionEventType.BearishPullback),
            Is.False);
    }

    [Test]
    public void Pullback_WithNoNearbyReference_IsNotDetected()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Rising) with
        {
            ConsecutiveHigherHighs = 2,
            ConsecutiveHigherLows = 2
        };
        // No swings/zones and no Bollinger middle fallback - there is nothing for the
        // retracement to be "close to", so it must not be treated as a pullback.
        Candle first = CandleAt(0, 100m, 100.6m, 99.8m, 100.4m);
        Candle pullback = CandleAt(1, 101m, 101.2m, 100.5m, 100.7m);

        analyzer.Update(first, [first], [], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            pullback,
            [first, pullback],
            [],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(
            result.Events.Any(item => item.Type is PriceActionEventType.BullishPullback or PriceActionEventType.BearishPullback),
            Is.False);
    }

    [Test]
    public void Pullback_WithOpposingStructuralBreak_IsNotDetected()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Rising) with
        {
            ConsecutiveHigherHighs = 2,
            ConsecutiveHigherLows = 2,
            Break = MarketStructureBreak.Bearish
        };
        SwingPoint support = Swing(SwingType.Low, 100.4m, Start.AddMinutes(-10));
        Candle first = CandleAt(0, 100m, 100.6m, 99.8m, 100.4m);
        Candle pullback = CandleAt(1, 101m, 101.2m, 100.5m, 100.7m);

        analyzer.Update(first, [first], [support], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            pullback,
            [first, pullback],
            [support],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(
            result.Events.Any(item => item.Type is PriceActionEventType.BullishPullback or PriceActionEventType.BearishPullback),
            Is.False);
    }

    [Test]
    public void WarmupCalibration_FreezesBeforeEvaluationData()
    {
        var analyzer = new PriceActionAnalyzer(new PriceActionOptions
        {
            CalibrationLookback = 10,
            CalibrationMinimumSamples = 2
        });
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Sideways);
        Candle first = CandleAt(0, 100m, 100.6m, 99.8m, 100.4m);
        Candle second = CandleAt(1, 100.4m, 101m, 100.2m, 100.8m);

        analyzer.Update(first, [first], [], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot warmup = analyzer.Update(
            second,
            [first, second],
            [],
            [],
            structure,
            structure,
            Indicators(1m),
            2);
        analyzer.FreezeCalibration(second.CloseTime!.Value);

        Candle evaluation = CandleAt(2, 100.8m, 110m, 90m, 109m);
        PriceActionSnapshot result = analyzer.Update(
            evaluation,
            [first, second, evaluation],
            [],
            [],
            structure,
            structure,
            Indicators(1m),
            3);

        Assert.Multiple(() =>
        {
            Assert.That(warmup.Calibration.IsReady, Is.True);
            Assert.That(result.Calibration.IsFrozen, Is.True);
            Assert.That(result.Calibration.SampleCount, Is.EqualTo(warmup.Calibration.SampleCount));
            Assert.That(result.Calibration.MedianBodyAtr, Is.EqualTo(warmup.Calibration.MedianBodyAtr));
            Assert.That(result.Calibration.MedianRangeAtr, Is.EqualTo(warmup.Calibration.MedianRangeAtr));
        });
    }

    [Test]
    public void LatestLeg_ReportsPullbackAsPercentageOfPreviousImpulse()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Rising);
        Candle first = CandleAt(0, 100m, 101m, 99.8m, 100.8m);
        Candle second = CandleAt(1, 100.8m, 104.2m, 100.7m, 104m);
        Candle third = CandleAt(2, 104m, 104.1m, 101.8m, 102m);
        SwingPoint low = Swing(SwingType.Low, 100m, first.OpenTime);
        SwingPoint high = Swing(SwingType.High, 104m, second.OpenTime);
        SwingPoint pullbackLow = Swing(SwingType.Low, 102m, third.OpenTime);

        PriceActionSnapshot result = analyzer.Update(
            third,
            [first, second, third],
            [low, high, pullbackLow],
            [],
            structure,
            structure,
            Indicators(1m),
            3);

        Assert.Multiple(() =>
        {
            Assert.That(result.LatestLeg, Is.Not.Null);
            Assert.That(result.LatestLeg!.Direction, Is.EqualTo(PriceActionDirection.Bearish));
            Assert.That(result.LatestLeg.RetracementPercent, Is.EqualTo(50m));
        });
    }

    [Test]
    public void AdxState_ReportsBullishDirectionalStrengthForPersistentRise()
    {
        var state = new AdxState(5);
        for (int index = 0; index < 20; index++)
        {
            decimal open = 100m + index;
            state.Update(CandleAt(index, open, open + 1.2m, open - 0.2m, open + 1m));
        }

        AdxAnalysisSnapshot snapshot = state.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Adx, Is.Not.Null);
            Assert.That(snapshot.PlusDi, Is.Not.Null);
            Assert.That(snapshot.MinusDi, Is.Not.Null);
            Assert.That(snapshot.PlusDi!.Value, Is.GreaterThan(snapshot.MinusDi!.Value));
            Assert.That(snapshot.DirectionalBias, Is.EqualTo(PriceActionDirection.Bullish));
        });
    }

    [Test]
    public void LiquiditySweep_CanUseOlderEqualSwingNotOnlyLatest()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Sideways);
        // Older equal low is the liquidity pool; a newer higher low is the latest swing.
        SwingPoint olderLow = Swing(SwingType.Low, 100m, Start.AddMinutes(-30));
        SwingPoint newerHigherLow = Swing(SwingType.Low, 100.8m, Start.AddMinutes(-10));
        Candle prior = CandleAt(0, 101m, 101.4m, 100.9m, 101.2m);
        // Wick through the older 100 low and recover above it.
        Candle sweep = CandleAt(1, 101.1m, 101.3m, 99.9m, 100.3m);

        analyzer.Update(prior, [prior], [olderLow, newerHigherLow], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            sweep,
            [prior, sweep],
            [olderLow, newerHigherLow],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        PriceActionEvent sweepEvent = result.Events.Single(
            item => item.Type == PriceActionEventType.SellSideLiquiditySweep);
        Assert.That(sweepEvent.ReferenceLevel, Is.EqualTo(100m));
    }

    [Test]
    public void StructuralRejection_IgnoresMixedZoneOnWrongSideOfPrice()
    {
        var analyzer = new PriceActionAnalyzer();
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Sideways);
        // Mixed zone well above the candle must not seed a bullish "support" rejection.
        var wrongSideZone = new PriceZone
        {
            LowerPrice = 103m,
            UpperPrice = 103.4m,
            CentrePrice = 103.2m,
            TouchCount = 4,
            Strength = 80m,
            Type = PriceZoneType.Mixed
        };
        Candle prior = CandleAt(0, 100.5m, 100.8m, 100.2m, 100.6m);
        // Long lower wick, closes high — but no valid support underneath.
        Candle pin = CandleAt(1, 100.6m, 100.9m, 99.7m, 100.85m);

        analyzer.Update(prior, [prior], [], [wrongSideZone], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            pin,
            [prior, pin],
            [],
            [wrongSideZone],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(
            result.Events.Any(item => item.Type == PriceActionEventType.BullishRejection),
            Is.False);
    }

    [Test]
    public void Displacement_WorksDuringCalibrationWarmupWithFallback()
    {
        var analyzer = new PriceActionAnalyzer(new PriceActionOptions
        {
            CalibrationMinimumSamples = 50,
            DisplacementBodyAtrFallback = 0.40m,
            MinimumDisplacementRangeAtr = 0.50m,
            MinimumDisplacementClosePosition = 0.70m
        });
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Sideways);
        Candle prior = CandleAt(0, 100m, 100.3m, 99.9m, 100.1m);
        // Large bullish displacement body before calibration is ready.
        Candle impulse = CandleAt(1, 100.1m, 101.4m, 100.05m, 101.3m);

        analyzer.Update(prior, [prior], [], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            impulse,
            [prior, impulse],
            [],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.That(result.Calibration.IsReady, Is.False);
        Assert.That(
            result.Events.Any(item => item.Type == PriceActionEventType.BullishDisplacement),
            Is.True);
    }

    [Test]
    public void MinimumTriggerConfidence_FiltersWeakTriggerAndExplainsRejection()
    {
        var analyzer = new PriceActionAnalyzer(new PriceActionOptions
        {
            CalibrationMinimumSamples = 50,
            DisplacementBodyAtrFallback = 0.40m,
            MinimumDisplacementRangeAtr = 0.50m,
            MinimumDisplacementClosePosition = 0.70m,
            MinimumTriggerConfidence = 90m
        });
        MarketStructureSnapshot structure = Structure(direction: MarketStructureDirection.Sideways);
        Candle prior = CandleAt(0, 100m, 100.3m, 99.9m, 100.1m);
        Candle impulse = CandleAt(1, 100.1m, 101.4m, 100.05m, 101.3m);

        analyzer.Update(prior, [prior], [], [], structure, structure, Indicators(1m), 1);
        PriceActionSnapshot result = analyzer.Update(
            impulse,
            [prior, impulse],
            [],
            [],
            structure,
            structure,
            Indicators(1m),
            2);

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Events.Any(item => item.Type == PriceActionEventType.BullishDisplacement),
                Is.False);
            Assert.That(
                result.Diagnostics.Any(item =>
                    item.ReasonCode == "TriggerConfidenceBelowMinimum" && !item.Accepted),
                Is.True);
        });
    }

    [Test]
    public void HasConfirmedTrigger_IncludesLiquiditySweepAndChoCH()
    {
        var snapshot = new PriceActionSnapshot
        {
            Events =
            [
                new PriceActionEvent
                {
                    EventId = "1",
                    Type = PriceActionEventType.SellSideLiquiditySweep,
                    Direction = PriceActionDirection.Bullish,
                    ConfirmedAt = Start,
                    ConfirmedSequence = 1,
                    Strength = 70m,
                    Confidence = 68m,
                    ReasonCode = "SellSideLiquiditySweep",
                    Explanation = "test"
                }
            ]
        };

        Assert.That(snapshot.HasConfirmedTrigger(PriceActionDirection.Bullish, 55m), Is.True);
    }

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
