using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ChartAnnotator.NeoWave;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class NeoWaveAnalysisTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void FutureConfirmedSwing_IsNotVisibleBeforeItsConfirmationTime()
    {
        var analyzer = new NeoWaveAnalyzer(EnabledOptions());
        SwingPoint[] swings =
        [
            Swing(0, 100m, SwingType.Low, confirmedBar: 2),
            Swing(2, 110m, SwingType.High, confirmedBar: 4),
            Swing(4, 105m, SwingType.Low, confirmedBar: 6)
        ];

        NeoWaveSnapshot before = analyzer.Update(
            swings,
            CandleAt(closeBar: 5, close: 108m),
            currentAtr: 2m);
        NeoWaveSnapshot after = analyzer.Update(
            swings,
            CandleAt(closeBar: 6, close: 105m),
            currentAtr: 2m);

        Assert.Multiple(() =>
        {
            Assert.That(before.ConfirmedMonoWaves, Has.Count.EqualTo(1));
            Assert.That(before.ConfirmedMonoWaves.All(item => item.AvailableAt <= before.AvailableAt), Is.True);
            Assert.That(after.ConfirmedMonoWaves, Has.Count.EqualTo(2));
            Assert.That(after.ConfirmedMonoWaves[^1].AvailableAt, Is.EqualTo(Start.AddMinutes(30)));
        });
    }

    [Test]
    public void FiveConfirmedLegs_ProduceDeterministicPreferredImpulseHypothesis()
    {
        var analyzer = new NeoWaveAnalyzer(EnabledOptions());
        SwingPoint[] swings =
        [
            Swing(0, 100m, SwingType.Low, 1),
            Swing(2, 110m, SwingType.High, 3),
            Swing(4, 105m, SwingType.Low, 5),
            Swing(6, 121m, SwingType.High, 7),
            Swing(8, 112m, SwingType.Low, 9),
            Swing(10, 126m, SwingType.High, 11)
        ];

        NeoWaveSnapshot first = analyzer.Update(swings, CandleAt(11, 126m), 4m);
        NeoWaveSnapshot second = analyzer.Update(swings, CandleAt(11, 126m), 4m);
        NeoWaveHypothesis preferred = first.Hypotheses.Single(item => item.HypothesisId == first.PreferredHypothesisId);

        Assert.Multiple(() =>
        {
            Assert.That(first.ConfirmedMonoWaves, Has.Count.EqualTo(5));
            Assert.That(preferred.PatternType, Is.EqualTo(NeoWavePatternType.ImpulseCandidate));
            Assert.That(preferred.Direction, Is.EqualTo(NeoWaveDirection.Up));
            Assert.That(preferred.Status, Is.EqualTo(NeoWaveHypothesisStatus.Preferred));
            Assert.That(preferred.StructuralScore, Is.EqualTo(100m));
            Assert.That(first.InvalidationPrice, Is.EqualTo(100m));
            Assert.That(first.ConfirmedMonoWaves.Select(item => item.WaveId),
                Is.EqualTo(second.ConfirmedMonoWaves.Select(item => item.WaveId)));
            Assert.That(first.PreferredHypothesisId, Is.EqualTo(second.PreferredHypothesisId));
        });
    }

    [Test]
    public void ProvisionalLeg_IsSeparatedFromConfirmedMonoWaves()
    {
        var analyzer = new NeoWaveAnalyzer(EnabledOptions());
        SwingPoint[] swings =
        [
            Swing(0, 100m, SwingType.Low, 1),
            Swing(2, 110m, SwingType.High, 3)
        ];

        NeoWaveSnapshot snapshot = analyzer.Update(swings, CandleAt(4, 106m), 2m);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ConfirmedMonoWaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.ProvisionalWave, Is.Not.Null);
            Assert.That(snapshot.ProvisionalWave!.Direction, Is.EqualTo(NeoWaveDirection.Down));
            Assert.That(snapshot.ProvisionalWave.AvailableAt, Is.EqualTo(snapshot.AvailableAt));
        });
    }


    [Test]
    public void HistoricalWaveLength_UsesAtrAvailableAtConfirmation_NotCurrentAtr()
    {
        var analyzer = new NeoWaveAnalyzer(EnabledOptions() with { MinimumWaveLengthAtr = 1m });
        SwingPoint[] swings =
        [
            Swing(0, 100m, SwingType.Low, 1),
            Swing(2, 110m, SwingType.High, 3)
        ];
        IndicatorPoint[] history =
        [
            new(Start.AddMinutes(15), 2m, null, null, null, null, 0m)
        ];

        NeoWaveSnapshot snapshot = analyzer.Update(
            swings,
            CandleAt(10, 111m),
            currentAtr: 100m,
            indicatorHistory: history);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ConfirmedMonoWaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.ConfirmedMonoWaves[0].LengthAtr, Is.EqualTo(5m));
        });
    }

    [Test]
    public void CorrectivePattern_IsRecordedButDoesNotCreateDirectionalSoftInfluence()
    {
        NeoWaveSnapshot source = UpImpulseSnapshot(conflict: 0m);
        NeoWaveHypothesis correction = source.Hypotheses[0] with
        {
            PatternType = NeoWavePatternType.ZigZagCorrection
        };
        NeoWaveSnapshot wave = source with
        {
            Hypotheses = [correction],
            PreferredHypothesisId = correction.HypothesisId
        };
        var options = new NeoWaveEvidenceOptions
        {
            Mode = NeoWaveEvidenceMode.SoftConfidenceAndRisk,
            AlignmentConfidenceAdjustment = 10m,
            OppositionConfidenceAdjustment = -10m,
            MaximumConflictRiskReduction = 0.50m
        };

        NeoWaveDecisionEvidence evidence = NeoWaveEvidenceEvaluator.Evaluate(
            Analysis(wave),
            buy: true,
            options);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ConfidenceAdjustment, Is.Zero);
            Assert.That(evidence.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(evidence.DirectionAligned, Is.False);
            Assert.That(evidence.InvalidationPrice, Is.Null);
            Assert.That(evidence.ReasonCodes, Does.Contain("NeoWavePatternNonDirectional"));
        });
    }

    [Test]
    public void Evidence_CanReduceButNeverIncreaseRisk()
    {
        NeoWaveSnapshot wave = UpImpulseSnapshot(conflict: 80m);
        AnalysisSnapshot snapshot = Analysis(wave);
        var options = new NeoWaveEvidenceOptions
        {
            Mode = NeoWaveEvidenceMode.SoftConfidenceAndRisk,
            MinimumRiskMultiplier = 0.70m,
            MaximumConflictRiskReduction = 0.30m
        };

        NeoWaveDecisionEvidence aligned = NeoWaveEvidenceEvaluator.Evaluate(snapshot, buy: true, options);
        NeoWaveDecisionEvidence opposed = NeoWaveEvidenceEvaluator.Evaluate(snapshot, buy: false, options);

        Assert.Multiple(() =>
        {
            Assert.That(aligned.RiskMultiplier, Is.InRange(0m, 1m));
            Assert.That(opposed.RiskMultiplier, Is.InRange(0m, 1m));
            Assert.That(aligned.RiskMultiplier, Is.LessThanOrEqualTo(1m));
            Assert.That(opposed.RiskMultiplier, Is.LessThanOrEqualTo(1m));
            Assert.That(aligned.DirectionAligned, Is.True);
            Assert.That(opposed.DirectionOpposed, Is.True);
        });
    }

    [Test]
    public void RiskBudget_ComposesNeoWaveMultiplierAsNeverIncreaseRiskScalar()
    {
        var policy = new RiskBudgetPolicy(new AdaptiveRiskOptions { Enabled = false });
        RiskBudgetDecision decision = policy.Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            NeoWaveMultiplier = 0.80m
        });

        Assert.Multiple(() =>
        {
            Assert.That(decision.NeoWaveMultiplier, Is.EqualTo(0.80m));
            Assert.That(decision.CombinedMultiplier, Is.EqualTo(0.80m));
        });
    }

    private static NeoWaveOptions EnabledOptions() => new()
    {
        Enabled = true,
        MinimumWaveLengthAtr = 0m,
        MinimumHypothesisScore = 40m,
        PreferredHypothesisMinimumScore = 55m
    };

    private static SwingPoint Swing(int pivotBar, decimal price, SwingType type, int confirmedBar) => new()
    {
        PivotTime = Start.AddMinutes(pivotBar * 5),
        ConfirmedAt = Start.AddMinutes(confirmedBar * 5),
        Price = price,
        Type = type,
        Strength = 2
    };

    private static Candle CandleAt(int closeBar, decimal close) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        OpenTime = Start.AddMinutes((closeBar - 1) * 5),
        CloseTime = Start.AddMinutes(closeBar * 5),
        Prices = new Ohlc(close, close + 0.5m, close - 0.5m, close),
        IsComplete = true
    };

    private static NeoWaveSnapshot UpImpulseSnapshot(decimal conflict)
    {
        var hypothesis = new NeoWaveHypothesis
        {
            HypothesisId = "up-impulse",
            PatternType = NeoWavePatternType.ImpulseCandidate,
            Direction = NeoWaveDirection.Up,
            Degree = NeoWaveDegree.Minor,
            ComponentWaveIds = ["1", "2", "3", "4", "5"],
            Status = NeoWaveHypothesisStatus.Preferred,
            StructuralScore = 90m,
            Maturity = 100m,
            AvailableAt = Start,
            SupportingRuleIds = ["test"],
            ViolatedRuleIds = [],
            Invalidation = new NeoWaveInvalidationCondition
            {
                Comparison = NeoWaveInvalidationComparison.Below,
                Price = 100m,
                Description = "test"
            }
        };
        return new NeoWaveSnapshot
        {
            Enabled = true,
            AvailableAt = Start,
            ConfirmedMonoWaves = [],
            Relationships = [],
            Hypotheses = [hypothesis],
            PreferredHypothesisId = hypothesis.HypothesisId,
            StructuralBias = NeoWaveDirection.Up,
            StructuralScore = 90m,
            Maturity = 100m,
            ConflictScore = conflict,
            InvalidationPrice = 100m,
            InvalidationDistanceAtr = 2m,
            Quality = new NeoWaveQuality
            {
                IsReady = true,
                ConfirmedMonoWaveCount = 5,
                ConfirmedSwingCount = 6,
                HypothesisCount = 1,
                ReasonCode = "Ready"
            }
        };
    }

    private static AnalysisSnapshot Analysis(NeoWaveSnapshot wave) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = Start,
        Version = 1,
        LatestCandle = CandleAt(1, 110m),
        Indicators = new IndicatorSnapshot { Atr = 5m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        NeoWave = wave,
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
