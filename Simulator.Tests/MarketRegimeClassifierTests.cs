using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Simulator.Tests;

[TestFixture]
public sealed class MarketRegimeClassifierTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MarketRegimeRuleInputs Neutral() => new()
    {
        AtrRegime = AtrVolatilityRegime.Normal,
        BollingerWidthRegime = BollingerWidthRegime.Normal,
        BollingerWidthDirection = VolatilityDirection.Stable,
        Adx = 20m,
        AdxDirectionalBias = PriceActionDirection.Neutral,
        EfficiencyRatio = 0.5m,
        EfficiencyState = MarketEfficiencyState.Transitional,
        EfficiencyDirection = MomentumDirection.Stable,
        DonchianClosedAboveUpper = false,
        DonchianClosedBelowLower = false,
        StructureDirection = MarketStructureDirection.Sideways,
        StructureDirectionChanged = false,
        StructureBreak = MarketStructureBreak.None,
        ConsecutiveHigherHighs = 0,
        ConsecutiveHigherLows = 0,
        ConsecutiveLowerHighs = 0,
        ConsecutiveLowerLows = 0,
        RecentAlignedBullishDisplacement = false,
        RecentAlignedBearishDisplacement = false,
        PreviousRegime = MarketRegime.Unknown,
        AdxCalibration = MarketRegimeCalibrationSnapshot.Empty,
        SpreadAtr = null,
        DataQualityOk = true
    };

    #region Pure rule-hierarchy branches

    [Test]
    public void Classify_NeutralInputs_ReturnsUnknown()
    {
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(Neutral(), new MarketRegimeOptions());

        Assert.Multiple(() =>
        {
            Assert.That(result.Regime, Is.EqualTo(MarketRegime.Unknown));
            Assert.That(result.ReasonCode, Is.EqualTo("InsufficientOrContradictoryEvidence"));
        });
    }

    [Test]
    public void Classify_DataQualityFailure_ReturnsIlliquidUnsafe()
    {
        MarketRegimeRuleInputs inputs = Neutral() with { DataQualityOk = false };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.Multiple(() =>
        {
            Assert.That(result.Regime, Is.EqualTo(MarketRegime.IlliquidUnsafe));
            Assert.That(result.ReasonCode, Is.EqualTo("DataQualityFailure"));
        });
    }

    [Test]
    public void Classify_SpreadExceedsHardLimit_ReturnsIlliquidUnsafe()
    {
        var options = new MarketRegimeOptions { HardMaximumSpreadAtr = 0.30m };
        MarketRegimeRuleInputs inputs = Neutral() with { SpreadAtr = 0.5m };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, options);

        Assert.Multiple(() =>
        {
            Assert.That(result.Regime, Is.EqualTo(MarketRegime.IlliquidUnsafe));
            Assert.That(result.ReasonCode, Is.EqualTo("SpreadExceedsHardLimit"));
        });
    }

    [Test]
    public void Classify_VeryHighVolatilityAndChoppyAndUnstableStructure_ReturnsHighVolatilityDisorder()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            AtrRegime = AtrVolatilityRegime.VeryHigh,
            EfficiencyState = MarketEfficiencyState.HighlyChoppy,
            StructureDirection = MarketStructureDirection.Unknown
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.HighVolatilityDisorder));
    }

    [Test]
    public void Classify_AlignedTrendConditions_ReturnsTrendingUp()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            Adx = 30m,
            AdxDirectionalBias = PriceActionDirection.Bullish,
            EfficiencyRatio = 0.5m,
            StructureDirection = MarketStructureDirection.Rising,
            StructureBreak = MarketStructureBreak.None
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.TrendingUp));
    }

    [Test]
    public void Classify_AlignedTrendConditions_ReturnsTrendingDown()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            Adx = 30m,
            AdxDirectionalBias = PriceActionDirection.Bearish,
            EfficiencyRatio = 0.5m,
            StructureDirection = MarketStructureDirection.Falling,
            StructureBreak = MarketStructureBreak.None
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.TrendingDown));
    }

    [Test]
    public void Classify_SqueezeAndLowAtrAndContracting_ReturnsCompression()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            Adx = 15m,
            BollingerWidthRegime = BollingerWidthRegime.Squeeze,
            AtrRegime = AtrVolatilityRegime.Low,
            BollingerWidthDirection = VolatilityDirection.Contracting
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.Compression));
    }

    [Test]
    public void Classify_LowAdxAndLowEfficiencyAndNoPersistentDirection_ReturnsRange()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            Adx = 10m,
            EfficiencyRatio = 0.2m,
            StructureDirection = MarketStructureDirection.Sideways
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.Range));
    }

    [Test]
    public void Classify_BreakoutConditionsAfterCompression_ReturnsBreakoutExpansionUp()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            PreviousRegime = MarketRegime.Compression,
            BollingerWidthRegime = BollingerWidthRegime.Expansion,
            EfficiencyDirection = MomentumDirection.Rising,
            DonchianClosedAboveUpper = true,
            RecentAlignedBullishDisplacement = true
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.BreakoutExpansionUp));
    }

    [Test]
    public void Classify_BreakoutConditionsAfterRange_ReturnsBreakoutExpansionDown()
    {
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            PreviousRegime = MarketRegime.Range,
            BollingerWidthRegime = BollingerWidthRegime.Expansion,
            EfficiencyDirection = MomentumDirection.Rising,
            DonchianClosedBelowLower = true,
            RecentAlignedBearishDisplacement = true
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.BreakoutExpansionDown));
    }

    [Test]
    public void Classify_PriorityOrder_HighVolatilityDisorderWinsOverTrendWhenBothMatch()
    {
        // Simultaneously satisfies HighVolatilityDisorder (very high ATR + choppy +
        // structure-just-changed) and TrendingUp (ADX/DI/structure aligned). The
        // higher-priority disorder branch must win.
        MarketRegimeRuleInputs inputs = Neutral() with
        {
            AtrRegime = AtrVolatilityRegime.VeryHigh,
            EfficiencyState = MarketEfficiencyState.HighlyChoppy,
            EfficiencyRatio = 0.5m,
            StructureDirection = MarketStructureDirection.Rising,
            StructureDirectionChanged = true,
            Adx = 30m,
            AdxDirectionalBias = PriceActionDirection.Bullish,
            StructureBreak = MarketStructureBreak.None
        };
        MarketRegimeEvaluation result = MarketRegimeClassifier.Classify(inputs, new MarketRegimeOptions());

        Assert.That(result.Regime, Is.EqualTo(MarketRegime.HighVolatilityDisorder));
    }

    #endregion

    #region Stateful hysteresis

    private static Candle Candle(int index) => TestCandles.Create(
        Instrument, Start + TimeSpan.FromMinutes(5 * index), Interval, 100m, 101m, 99m, 100m);

    private static IndicatorSnapshot RangeIndicators() => new()
    {
        Atr = 1m,
        AtrAnalysis = new AtrAnalysisSnapshot { Regime = AtrVolatilityRegime.Normal, Direction = VolatilityDirection.Stable, SampleCount = 30 },
        BollingerAnalysis = new BollingerAnalysisSnapshot { WidthRegime = BollingerWidthRegime.Normal, WidthDirection = VolatilityDirection.Stable, SampleCount = 30 },
        AdxAnalysis = new AdxAnalysisSnapshot { Adx = 10m, PlusDi = 10m, MinusDi = 10m, DirectionalBias = PriceActionDirection.Neutral, StrengthDirection = MomentumDirection.Stable },
        EfficiencyRatio = 0.2m,
        EfficiencyAnalysis = new EfficiencyAnalysisSnapshot { State = MarketEfficiencyState.Choppy, Direction = MomentumDirection.Stable, SampleCount = 30 },
        Donchian = DonchianSnapshot.Empty
    };

    private static IndicatorSnapshot TrendUpIndicators() => new()
    {
        Atr = 1m,
        AtrAnalysis = new AtrAnalysisSnapshot { Regime = AtrVolatilityRegime.Normal, Direction = VolatilityDirection.Stable, SampleCount = 30 },
        BollingerAnalysis = new BollingerAnalysisSnapshot { WidthRegime = BollingerWidthRegime.Normal, WidthDirection = VolatilityDirection.Stable, SampleCount = 30 },
        AdxAnalysis = new AdxAnalysisSnapshot { Adx = 30m, PlusDi = 30m, MinusDi = 5m, DirectionalBias = PriceActionDirection.Bullish, StrengthDirection = MomentumDirection.Rising },
        EfficiencyRatio = 0.5m,
        EfficiencyAnalysis = new EfficiencyAnalysisSnapshot { State = MarketEfficiencyState.Efficient, Direction = MomentumDirection.Stable, SampleCount = 30 },
        Donchian = DonchianSnapshot.Empty
    };

    private static MarketStructureSnapshot RangeStructure() => new()
    {
        Direction = MarketStructureDirection.Sideways,
        PreviousDirection = MarketStructureDirection.Sideways,
        Break = MarketStructureBreak.None,
        DirectionChanged = false,
        Strength = 10m
    };

    private static MarketStructureSnapshot TrendUpStructure() => new()
    {
        Direction = MarketStructureDirection.Rising,
        PreviousDirection = MarketStructureDirection.Rising,
        Break = MarketStructureBreak.None,
        DirectionChanged = false,
        Strength = 40m
    };

    [Test]
    public void Update_SingleBarFlicker_DoesNotBecomeAuthoritative()
    {
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            MinimumConfirmationBars = 2,
            MinimumPersistenceBars = 0,
            SwitchConfidenceMargin = 0m
        };
        var classifier = new MarketRegimeClassifier(options);

        MarketRegimeSnapshot snapshot = MarketRegimeSnapshot.Unknown;
        for (int index = 0; index < 3; index++)
        {
            snapshot = classifier.Update(Candle(index), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        }

        Assert.That(snapshot.Regime, Is.EqualTo(MarketRegime.Range));

        // One-bar flicker to TrendUp-satisfying inputs should not flip anything yet.
        MarketRegimeSnapshot flicker = classifier.Update(Candle(3), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(flicker.Regime, Is.EqualTo(MarketRegime.Range));

        // Reverting back to Range confirms the flicker never stuck.
        MarketRegimeSnapshot reverted = classifier.Update(Candle(4), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        Assert.That(reverted.Regime, Is.EqualTo(MarketRegime.Range));
    }

    [Test]
    public void Update_SustainedChallenger_BecomesAuthoritativeAfterConfirmationBars()
    {
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            MinimumConfirmationBars = 2,
            MinimumPersistenceBars = 0,
            SwitchConfidenceMargin = 0m
        };
        var classifier = new MarketRegimeClassifier(options);

        for (int index = 0; index < 3; index++)
        {
            classifier.Update(Candle(index), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        }

        MarketRegimeSnapshot first = classifier.Update(Candle(3), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(first.Regime, Is.EqualTo(MarketRegime.Range), "First challenger bar should not flip yet.");

        MarketRegimeSnapshot second = classifier.Update(Candle(4), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(second.Regime, Is.EqualTo(MarketRegime.TrendingUp), "Second consecutive challenger bar should confirm the flip.");
    }

    [Test]
    public void Update_ChallengerWithLowerAbsoluteConfidenceThanOldRegime_CanStillFlip()
    {
        // Regression test: the switch-confidence check must compare a challenger's
        // own confidence against a fixed neutral-baseline floor, not against the
        // previously confirmed regime's frozen confidence. That frozen value never
        // refreshes once raw stops re-matching the confirmed regime, and confidence
        // is capped at 100 - so comparing against it would eventually make any
        // regime with confidence >= 91 permanently unbeatable (91 + default margin
        // 10 > 100), regardless of how many bars a valid challenger sustains.
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            MinimumConfirmationBars = 2,
            MinimumPersistenceBars = 2,
            SwitchConfidenceMargin = 10m
        };
        var classifier = new MarketRegimeClassifier(options);

        // Confirm TrendingUp first (raw confidence 90 - higher than Range's 85).
        for (int index = 0; index < 4; index++)
        {
            classifier.Update(Candle(index), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        }

        // Sustain a lower-confidence-but-still-valid Range challenger for many bars.
        MarketRegimeSnapshot last = MarketRegimeSnapshot.Unknown;
        for (int index = 4; index < 12; index++)
        {
            last = classifier.Update(Candle(index), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        }

        Assert.That(last.Regime, Is.EqualTo(MarketRegime.Range),
            "A sustained challenger must eventually flip the regime even with lower raw confidence than the old regime.");
    }

    [Test]
    public void Update_PersistenceBars_BlocksChallengeUntilCurrentRegimeHasAged()
    {
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            MinimumConfirmationBars = 1,
            MinimumPersistenceBars = 3,
            SwitchConfidenceMargin = 0m
        };
        var classifier = new MarketRegimeClassifier(options);

        // Three consecutive Range bars: bar0 registers the pending candidate, bar1
        // confirms it but age (1) is still below MinimumPersistenceBars for Unknown's
        // very first successor, bar2 finally satisfies both and flips.
        classifier.Update(Candle(0), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        classifier.Update(Candle(1), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        MarketRegimeSnapshot afterThird = classifier.Update(Candle(2), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        Assert.That(afterThird.Regime, Is.EqualTo(MarketRegime.Range), "Three consecutive Range bars satisfy confirmation and persistence.");

        // Challenge with TrendUp bars. The first two register/confirm the candidate
        // but age (1, then 2) is still below MinimumPersistenceBars=3, so Range must
        // hold. Only the third consecutive challenger bar (age=3) can flip it.
        MarketRegimeSnapshot challenge1 = classifier.Update(Candle(3), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(challenge1.Regime, Is.EqualTo(MarketRegime.Range), "Age has not reached the persistence bar requirement yet.");

        MarketRegimeSnapshot challenge2 = classifier.Update(Candle(4), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(challenge2.Regime, Is.EqualTo(MarketRegime.Range), "The freshly confirmed regime must age before it can be challenged.");

        MarketRegimeSnapshot challenge3 = classifier.Update(Candle(5), TrendUpIndicators(), TrendUpStructure(), PriceActionSnapshot.Empty);
        Assert.That(challenge3.Regime, Is.EqualTo(MarketRegime.TrendingUp), "Once aged sufficiently, a sustained challenger can flip the regime.");
    }

    [Test]
    public void Update_IlliquidUnsafe_BypassesHysteresisImmediately()
    {
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            MinimumConfirmationBars = 5,
            MinimumPersistenceBars = 5,
            SwitchConfidenceMargin = 50m
        };
        var classifier = new MarketRegimeClassifier(options);

        for (int index = 0; index < 3; index++)
        {
            classifier.Update(Candle(index), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty);
        }

        MarketRegimeSnapshot unsafeSnapshot = classifier.Update(
            Candle(3), RangeIndicators(), RangeStructure(), PriceActionSnapshot.Empty, dataQualityOk: false);

        Assert.Multiple(() =>
        {
            Assert.That(unsafeSnapshot.Regime, Is.EqualTo(MarketRegime.IlliquidUnsafe));
            Assert.That(unsafeSnapshot.IsTradeable, Is.False);
        });
    }

    [Test]
    public void Update_Disabled_NeverConstructedByEngine_DefaultsToUnknownAndTradeable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MarketRegimeSnapshot.Unknown.Regime, Is.EqualTo(MarketRegime.Unknown));
            Assert.That(MarketRegimeSnapshot.Unknown.IsTradeable, Is.True);
        });
    }

    #endregion

    #region Calibration freeze

    [Test]
    public void FreezeCalibration_StopsAdxThresholdsFromDriftingAfterFreeze()
    {
        var options = new MarketRegimeOptions
        {
            Enabled = true,
            AdxCalibrationHistoryPeriod = 50,
            AdxCalibrationMinimumSamples = 5
        };
        var calibration = new MarketRegimeCalibration(
            options.AdxCalibrationHistoryPeriod,
            options.AdxCalibrationMinimumSamples,
            options.TrendAdxMinimumPercentile,
            options.RangeAdxMaximumPercentile);

        foreach (decimal adx in new[] { 10m, 12m, 14m, 16m, 18m, 20m })
        {
            calibration.Observe(adx);
        }

        MarketRegimeCalibrationSnapshot beforeFreeze = calibration.Observe(20m);
        calibration.FreezeCalibration(DateTimeOffset.UtcNow);
        MarketRegimeCalibrationSnapshot rightAfterFreeze = calibration.Observe(20m);

        // Feed very different values after freezing; the frozen thresholds must not move.
        for (int index = 0; index < 10; index++)
        {
            calibration.Observe(90m);
        }

        MarketRegimeCalibrationSnapshot afterManyPostFreezeSamples = calibration.Observe(20m);

        Assert.Multiple(() =>
        {
            Assert.That(rightAfterFreeze.IsFrozen, Is.True);
            Assert.That(rightAfterFreeze.CalibratedTrendAdxThreshold, Is.EqualTo(beforeFreeze.CalibratedTrendAdxThreshold));
            Assert.That(afterManyPostFreezeSamples.CalibratedTrendAdxThreshold, Is.EqualTo(rightAfterFreeze.CalibratedTrendAdxThreshold));
            Assert.That(afterManyPostFreezeSamples.SampleCount, Is.EqualTo(rightAfterFreeze.SampleCount));
        });
    }

    #endregion
}
