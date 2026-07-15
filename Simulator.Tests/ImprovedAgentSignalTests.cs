using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class ImprovedAgentSignalTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly BarInterval Fifteen = BarInterval.Minutes(15);
    private static readonly BarInterval Hour = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ImprovedAgent_EntersOnPriceActionSetup_WithoutEntryDetectSide()
    {
        // Entry TF lacks RSI/structure DetectSide, but a triggered PA retest is present.
        var agent = new ImprovedProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            PriceActionConfirmation = PriceActionConfirmationMode.Soft,
            MinimumPriceActionConfidence = 55m
        });

        AnalysisSnapshot hour = Aligned(Hour, bullish: true, confidence: 70m);
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true, confidence: 68m);
        AnalysisSnapshot five = EntryWithPaSetupOnly(
            nearbyResistance: 114m,
            swingLow: 107m);

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.StopLossPrice, Is.Not.Null);
            Assert.That(decision.TakeProfitPrice, Is.Not.Null);
            Assert.That(decision.StopLossPrice!.Value, Is.LessThan(decision.ReferencePrice!.Value));
            Assert.That(decision.TakeProfitPrice!.Value, Is.GreaterThan(decision.ReferencePrice!.Value));
            Assert.That(decision.ExpectedRewardRisk, Is.GreaterThanOrEqualTo(1.5m));
            Assert.That(decision.PriceActionSetupType, Is.EqualTo(PriceActionSetupType.BullishBreakRetestHold));
        });
    }

    [Test]
    public async Task ImprovedAgent_RejectsEntry_WhenNearestBarrierCannotProvideMinimumR()
    {
        var agent = new ImprovedProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            FallbackStopAtr = 1.5m,
            FallbackTargetAtr = 3m
        });

        // Price 110, structural stop ~107, nearest resistance only 0.4 above. A farther
        // target must not be used to manufacture R:R through that first barrier.
        AnalysisSnapshot hour = Aligned(Hour, bullish: true, confidence: 72m, close: 110m, atr: 2m);
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true, confidence: 70m, close: 110m, atr: 2m);
        AnalysisSnapshot five = Aligned(Five, bullish: true, confidence: 70m, close: 110m, atr: 2m) with
        {
            Swings =
            [
                new SwingPoint
                {
                    Type = SwingType.Low,
                    Price = 107m,
                    PivotTime = Now.AddMinutes(-30),
                    ConfirmedAt = Now.AddMinutes(-20),
                    Strength = 2
                }
            ],
            PriceZones =
            [
                Zone(110.4m, 110.6m, PriceZoneType.Resistance),
                Zone(114.5m, 115.0m, PriceZoneType.Resistance)
            ]
        };

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe), decision.Reason);
            Assert.That(decision.ReasonCode, Is.EqualTo("RewardBlockedByStructure"));
            Assert.That(decision.Reason, Does.Contain("Nearest opposing structure"));
        });
    }

    [Test]
    public async Task ImprovedAgent_ProjectsMinimumR_WhenNoStructuralTargetExists()
    {
        var agent = new ImprovedProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            FallbackStopAtr = 1.0m,
            FallbackTargetAtr = 1.0m
        });

        AnalysisSnapshot hour = Aligned(Hour, bullish: true, confidence: 70m, close: 100m, atr: 2m);
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true, confidence: 70m, close: 100m, atr: 2m);
        AnalysisSnapshot five = Aligned(Five, bullish: true, confidence: 70m, close: 100m, atr: 2m);

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.ExpectedRewardRisk, Is.GreaterThanOrEqualTo(1.5m));
            Assert.That(decision.TakeProfitPrice!.Value, Is.GreaterThanOrEqualTo(103m));
        });
    }

    [Test]
    public async Task ImprovedAgent_EntersOnAlignedRsiRelationship_WithoutClassicOrPaTrigger()
    {
        var agent = new ImprovedProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            MinimumEntryConfidence = 58m,
            PriceActionConfirmation = PriceActionConfirmationMode.Soft
        });

        AnalysisSnapshot hour = Aligned(Hour, bullish: true, confidence: 70m);
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true, confidence: 70m);
        AnalysisSnapshot five = Aligned(Five, bullish: true, confidence: 40m) with
        {
            PriceAction = PriceActionSnapshot.Empty
        };
        five = five with
        {
            Indicators = five.Indicators with
            {
                RsiAnalysis = new RsiAnalysisSnapshot
                {
                    MomentumDirection = MomentumDirection.Rising,
                    LatestRelationship = new RsiRelationshipSnapshot
                    {
                        Type = RsiRelationshipType.HiddenBullishDivergence,
                        FirstPivotTime = Now.AddMinutes(-30),
                        SecondPivotTime = Now.AddMinutes(-10),
                        ConfirmedAt = Now.AddMinutes(-5),
                        FirstPrice = 108m,
                        SecondPrice = 109m,
                        FirstRsi = 48m,
                        SecondRsi = 43m,
                        PriceChange = 1m,
                        RsiChange = -5m,
                        Strength = 80m,
                        AgeCandles = 1
                    }
                }
            }
        };

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.ReasonCode, Is.EqualTo("RsiBollingerTrigger"));
            Assert.That(decision.Reason, Does.Contain("HiddenBullishDivergence"));
            Assert.That(decision.Confidence, Is.GreaterThan(58m));
        });
    }

    private static AnalysisSnapshot EntryWithPaSetupOnly(
        decimal nearbyResistance,
        decimal swingLow)
    {
        // Close 110, no RSI → DetectSide fails; PA setup provides the entry trigger.
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = Five,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(
                Instrument,
                Now.AddMinutes(-5),
                Five,
                109.5m,
                110.5m,
                109.0m,
                110.0m),
            Indicators = new IndicatorSnapshot
            {
                Atr = 2m,
                Rsi = null,
                BollingerMiddle = 108m
            },
            Swings =
            [
                new SwingPoint
                {
                    Type = SwingType.Low,
                    Price = swingLow,
                    PivotTime = Now.AddMinutes(-40),
                    ConfirmedAt = Now.AddMinutes(-30),
                    Strength = 2
                }
            ],
            PriceZones =
            [
                Zone(nearbyResistance - 0.2m, nearbyResistance + 0.2m, PriceZoneType.Resistance)
            ],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = MarketStructureDirection.Unknown,
                Strength = 40m
            },
            PriceAction = new PriceActionSnapshot
            {
                Bias = PriceActionDirection.Bullish,
                BullishScore = 72m,
                Setups =
                [
                    new PriceActionSetup
                    {
                        SetupId = "ltf-retest",
                        Type = PriceActionSetupType.BullishBreakRetestHold,
                        Direction = PriceActionDirection.Bullish,
                        Phase = PriceActionSetupPhase.Triggered,
                        ArmedAt = Now.AddMinutes(-15),
                        TriggeredAt = Now,
                        ArmedSequence = 10,
                        TriggeredSequence = 12,
                        Confidence = 80m,
                        ReferenceLevel = swingLow + 1m,
                        EntryReference = 110m,
                        ReasonCode = "RetestHoldTriggered",
                        Explanation = "test"
                    }
                ]
            },
            Confidence = new ConfidenceScore { Total = 65m, Contributions = [] }
        };
    }

    private static AnalysisSnapshot Aligned(
        BarInterval interval,
        bool bullish,
        decimal confidence = 70m,
        decimal close = 110m,
        decimal atr = 2m)
    {
        decimal open = bullish ? close - 1m : close + 1m;
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(
                Instrument,
                Now.AddSeconds(-BarIntervalParser.ApproximateSeconds(interval)),
                interval,
                open,
                Math.Max(open, close) + 0.5m,
                Math.Min(open, close) - 0.5m,
                close),
            Indicators = new IndicatorSnapshot
            {
                Atr = atr,
                Rsi = bullish ? 58m : 42m,
                BollingerMiddle = close
            },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = bullish
                    ? MarketStructureDirection.Rising
                    : MarketStructureDirection.Falling,
                Strength = 75m
            },
            PriceAction = new PriceActionSnapshot
            {
                Bias = bullish ? PriceActionDirection.Bullish : PriceActionDirection.Bearish,
                BullishScore = bullish ? 60m : 20m,
                BearishScore = bullish ? 20m : 60m
            },
            Confidence = new ConfidenceScore { Total = confidence, Contributions = [] }
        };
    }

    private static PriceZone Zone(decimal lower, decimal upper, PriceZoneType type) => new()
    {
        LowerPrice = lower,
        UpperPrice = upper,
        CentrePrice = (lower + upper) / 2m,
        TouchCount = 3,
        Strength = 70m,
        Type = type
    };

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = Now,
        Analysis = new MultiTimeframeAnalysis(
            Instrument,
            Now,
            snapshots.ToDictionary(snapshot => snapshot.Interval)),
        Account = new AccountSnapshot
        {
            AccountId = "a",
            Currency = "USD",
            Balance = 100_000m,
            Available = 100_000m,
            CanTrade = true
        },
        Positions = [],
        OpenOrders = []
    };
}
