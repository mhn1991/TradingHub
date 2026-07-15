using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class RsiBollingerSignalPolicyTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void RsiRelationship_IsScoredForItsDirectionAndVetoesTheOpposite()
    {
        RsiRelationshipSnapshot relationship = Relationship(
            RsiRelationshipType.RegularBullishDivergence,
            strength: 80m,
            age: 2);
        AnalysisSnapshot snapshot = Snapshot(new IndicatorSnapshot
        {
            Atr = 0.001m,
            Rsi = 54m,
            BollingerMiddle = 1.10m,
            RsiAnalysis = new RsiAnalysisSnapshot
            {
                MomentumDirection = MomentumDirection.Rising,
                LatestRelationship = relationship
            },
            BollingerAnalysis = new BollingerAnalysisSnapshot
            {
                SampleCount = 30,
                PercentB = 72m,
                WidthDirection = VolatilityDirection.Expanding,
                IsExpansion = true
            }
        });

        RsiBollingerSignalAssessment bullish = RsiBollingerSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish);
        RsiBollingerSignalAssessment bearish = RsiBollingerSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bearish);

        Assert.Multiple(() =>
        {
            Assert.That(bullish.AlignedRsiRelationship, Is.SameAs(relationship));
            Assert.That(bullish.HasEntryTrigger, Is.True);
            Assert.That(bullish.BollingerExpansionAligned, Is.True);
            Assert.That(bullish.ConfidenceAdjustment, Is.Positive);
            Assert.That(bullish.IsVetoed, Is.False);

            Assert.That(bearish.OpposingRsiRelationship, Is.SameAs(relationship));
            Assert.That(bearish.ConfidenceAdjustment, Is.Negative);
            Assert.That(bearish.VetoReasonCode, Is.EqualTo("OpposingRsiRelationship"));
        });
    }

    [Test]
    public void DirectionalSqueezeRelease_CanTriggerWithoutRsiRelationship()
    {
        AnalysisSnapshot snapshot = Snapshot(new IndicatorSnapshot
        {
            Atr = 0.001m,
            Rsi = 57m,
            BollingerMiddle = 1.10m,
            RsiAnalysis = new RsiAnalysisSnapshot
            {
                MomentumDirection = MomentumDirection.Stable
            },
            BollingerAnalysis = new BollingerAnalysisSnapshot
            {
                SampleCount = 30,
                PercentB = 86m,
                WidthDirection = VolatilityDirection.Expanding,
                SqueezeReleased = true,
                IsExpansion = true
            }
        });

        RsiBollingerSignalAssessment result = RsiBollingerSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish);

        Assert.Multiple(() =>
        {
            Assert.That(result.BollingerReleaseTrigger, Is.True);
            Assert.That(result.HasEntryTrigger, Is.True);
            Assert.That(result.ConfidenceAdjustment, Is.GreaterThanOrEqualTo(4m));
            Assert.That(result.IsVetoed, Is.False);
        });
    }

    [Test]
    public void OversoldRsi_AddsWeightAndConfirmsBullishRelationship()
    {
        RsiRelationshipSnapshot relationship = Relationship(
            RsiRelationshipType.RegularBullishDivergence,
            strength: 60m,
            age: 1);
        AnalysisSnapshot oversold = Snapshot(new IndicatorSnapshot
        {
            Atr = 0.001m,
            Rsi = 27m,
            BollingerMiddle = 1.10m,
            RsiAnalysis = new RsiAnalysisSnapshot
            {
                Zone = RsiZone.Oversold,
                MomentumDirection = MomentumDirection.Stable,
                LatestRelationship = relationship
            }
        });
        AnalysisSnapshot neutral = oversold with
        {
            Indicators = oversold.Indicators with
            {
                RsiAnalysis = oversold.Indicators.RsiAnalysis with
                {
                    Zone = RsiZone.Neutral
                }
            }
        };

        RsiBollingerSignalAssessment confirmed = RsiBollingerSignalPolicy.Evaluate(
            oversold,
            PriceActionDirection.Bullish);
        RsiBollingerSignalAssessment unconfirmed = RsiBollingerSignalPolicy.Evaluate(
            neutral,
            PriceActionDirection.Bullish);

        Assert.Multiple(() =>
        {
            Assert.That(confirmed.RsiExtremeConfirmsRelationship, Is.True);
            Assert.That(confirmed.HasEntryTrigger, Is.True);
            Assert.That(
                confirmed.ConfidenceAdjustment - unconfirmed.ConfidenceAdjustment,
                Is.EqualTo(2.5m));
            Assert.That(unconfirmed.HasEntryTrigger, Is.False);
        });
    }

    [Test]
    public void OverboughtRsi_AddsWeightAndConfirmsBearishRelationship()
    {
        RsiRelationshipSnapshot relationship = Relationship(
            RsiRelationshipType.RegularBearishDivergence,
            strength: 60m,
            age: 1);
        AnalysisSnapshot overbought = Snapshot(new IndicatorSnapshot
        {
            Atr = 0.001m,
            Rsi = 73m,
            BollingerMiddle = 1.10m,
            RsiAnalysis = new RsiAnalysisSnapshot
            {
                Zone = RsiZone.Overbought,
                MomentumDirection = MomentumDirection.Stable,
                LatestRelationship = relationship
            }
        });
        AnalysisSnapshot neutral = overbought with
        {
            Indicators = overbought.Indicators with
            {
                RsiAnalysis = overbought.Indicators.RsiAnalysis with
                {
                    Zone = RsiZone.Neutral
                }
            }
        };

        RsiBollingerSignalAssessment confirmed = RsiBollingerSignalPolicy.Evaluate(
            overbought,
            PriceActionDirection.Bearish);
        RsiBollingerSignalAssessment unconfirmed = RsiBollingerSignalPolicy.Evaluate(
            neutral,
            PriceActionDirection.Bearish);

        Assert.Multiple(() =>
        {
            Assert.That(confirmed.RsiExtremeConfirmsRelationship, Is.True);
            Assert.That(confirmed.HasEntryTrigger, Is.True);
            Assert.That(
                confirmed.ConfidenceAdjustment - unconfirmed.ConfidenceAdjustment,
                Is.EqualTo(2.5m));
            Assert.That(unconfirmed.HasEntryTrigger, Is.False);
        });
    }

    [Test]
    public void UndirectedSqueeze_DoesNotBecomeAnEntryTrigger()
    {
        AnalysisSnapshot snapshot = Snapshot(new IndicatorSnapshot
        {
            Atr = 0.001m,
            Rsi = 50m,
            BollingerMiddle = 1.10m,
            RsiAnalysis = new RsiAnalysisSnapshot
            {
                MomentumDirection = MomentumDirection.Stable
            },
            BollingerAnalysis = new BollingerAnalysisSnapshot
            {
                SampleCount = 30,
                PercentB = 50m,
                WidthDirection = VolatilityDirection.Contracting,
                IsSqueeze = true,
                WidthRegime = BollingerWidthRegime.Squeeze
            }
        });

        RsiBollingerSignalAssessment result = RsiBollingerSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish);

        Assert.Multiple(() =>
        {
            Assert.That(result.HasEntryTrigger, Is.False);
            Assert.That(result.ConfidenceAdjustment, Is.Zero);
            Assert.That(result.IsVetoed, Is.False);
        });
    }

    private static RsiRelationshipSnapshot Relationship(
        RsiRelationshipType type,
        decimal strength,
        int age) => new()
    {
        Type = type,
        FirstPivotTime = Now.AddMinutes(-30),
        SecondPivotTime = Now.AddMinutes(-10),
        ConfirmedAt = Now.AddMinutes(-5),
        FirstPrice = 1.101m,
        SecondPrice = 1.100m,
        FirstRsi = 38m,
        SecondRsi = 44m,
        PriceChange = -0.001m,
        RsiChange = 6m,
        Strength = strength,
        AgeCandles = age
    };

    private static AnalysisSnapshot Snapshot(IndicatorSnapshot indicators) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument,
            Now.AddMinutes(-5),
            Interval,
            1.100m,
            1.102m,
            1.099m,
            1.101m),
        Indicators = indicators,
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 60m, Contributions = [] }
    };
}
