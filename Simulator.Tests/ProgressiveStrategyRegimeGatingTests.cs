using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Simulator.Tests;

[TestFixture]
public sealed class ProgressiveStrategyRegimeGatingTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly BarInterval Fifteen = BarInterval.Minutes(15);
    private static readonly BarInterval Hour = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RoutingDisabled_ProducesByteIdenticalDecisionToBaseline()
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            FallbackStopAtr = 1.0m,
            FallbackTargetAtr = 1.0m
            // MarketRegime.Enabled defaults to false.
        };
        var agent = new ImprovedProgressiveAgent(options);

        AnalysisSnapshot hour = Aligned(Hour, bullish: true);
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true);
        AnalysisSnapshot five = Aligned(Five, bullish: true);

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.SuggestedQuantity, Is.EqualTo(options.Quantity));
            Assert.That(decision.RegimeLabel, Is.Null);
            Assert.That(decision.RegimeConfidence, Is.Null);
            Assert.That(decision.RegimePolicyId, Is.Null);
            Assert.That(decision.RegimeEntryProfileId, Is.Null);
            Assert.That(decision.RegimeManagementProfileId, Is.Null);
            Assert.That(decision.RegimeRiskMultiplier, Is.Null);
        });
    }

    [Test]
    public async Task RoutingEnabled_Compression_BlocksNewEntryWithoutReachingCoreLogic()
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            FallbackStopAtr = 1.0m,
            FallbackTargetAtr = 1.0m,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        var agent = new ImprovedProgressiveAgent(options);

        AnalysisSnapshot hour = Aligned(Hour, bullish: true, regime: RegimeSnapshot(MarketRegime.Compression));
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true);
        AnalysisSnapshot five = Aligned(Five, bullish: true);

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe), decision.Reason);
            Assert.That(decision.ReasonCode, Is.EqualTo("RegimeBlocked:Compression"));
            Assert.That(decision.RegimeLabel, Is.EqualTo(MarketRegime.Compression));
            Assert.That(decision.RegimeRiskMultiplier, Is.EqualTo(0m));
        });
    }

    [Test]
    public async Task RoutingEnabled_Range_PreservesRequestedQuantityAndEmitsSizingMultiplier()
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MinimumRewardRisk = 1.5m,
            FallbackStopAtr = 1.0m,
            FallbackTargetAtr = 1.0m,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        var agent = new ImprovedProgressiveAgent(options);

        AnalysisSnapshot hour = Aligned(Hour, bullish: true, regime: RegimeSnapshot(MarketRegime.Range));
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true);
        AnalysisSnapshot five = Aligned(Five, bullish: true);

        AgentDecision decision = await agent.EvaluateAsync(Context(five, fifteen, hour));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.SuggestedQuantity, Is.EqualTo(options.Quantity));
            Assert.That(decision.RegimeLabel, Is.EqualTo(MarketRegime.Range));
            Assert.That(decision.RegimeConfidence, Is.EqualTo(80m));
            Assert.That(decision.RegimePolicyId, Is.EqualTo(nameof(MarketRegime.Range)));
            Assert.That(decision.RegimeEntryProfileId, Is.EqualTo("range-boundary"));
            Assert.That(decision.RegimeRiskMultiplier, Is.EqualTo(0.6m));
        });
    }

    [Test]
    public async Task RoutingEnabled_OpenPosition_ManagementIsNeverBlockedOrRescaled()
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Hour,
            ConfirmationInterval = Fifteen,
            EntryInterval = Five,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        var agent = new ImprovedProgressiveAgent(options);

        // Falling trend structure invalidates a long position regardless of regime.
        AnalysisSnapshot hour = Aligned(Hour, bullish: false, regime: RegimeSnapshot(MarketRegime.HighVolatilityDisorder));
        AnalysisSnapshot fifteen = Aligned(Fifteen, bullish: true);
        AnalysisSnapshot five = Aligned(Five, bullish: true);

        var position = new BrokerPosition
        {
            PositionId = "p1",
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Quantity = 2_500m
        };

        AgentMarketContext context = Context(five, fifteen, hour) with { Positions = [position] };
        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Close), decision.Reason);
            Assert.That(decision.SuggestedQuantity, Is.EqualTo(position.Quantity), "Regime risk multiplier must never rescale a management/close quantity.");
            Assert.That(decision.RegimeLabel, Is.EqualTo(MarketRegime.HighVolatilityDisorder), "Management decisions must still be stamped with regime context.");
        });
    }

    private static MarketRegimeSnapshot RegimeSnapshot(MarketRegime regime) => new()
    {
        Regime = regime,
        Confidence = 80m,
        ConfirmedAt = Now,
        AgeCandles = 5,
        Contributions = [],
        ReasonCode = "TestReason",
        IsTradeable = true
    };

    private static AnalysisSnapshot Aligned(
        BarInterval interval,
        bool bullish,
        decimal confidence = 70m,
        decimal close = 110m,
        decimal atr = 2m,
        MarketRegimeSnapshot? regime = null)
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
            MarketRegime = regime ?? MarketRegimeSnapshot.Unknown,
            Confidence = new ConfidenceScore { Total = confidence, Contributions = [] }
        };
    }

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
