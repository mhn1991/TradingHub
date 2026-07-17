using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

/// <summary>
/// AGENT-11 regression: DMI's hard veto inside DetectSide previously collapsed into the same
/// generic PrimaryTrendNotReady/PrimaryTrendInvalidated reason codes as "no structural/tactical
/// candidate at all," making it impossible to tell from the reason code alone whether DMI
/// specifically was the deciding factor. This proves the new distinct "DmiVetoed" reason code
/// actually fires when DMI is the cause, without changing the veto's rejecting effect.
/// </summary>
[TestFixture]
public sealed class DmiVetoReasonCodeTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Entry = BarInterval.Minutes(5);
    private static readonly BarInterval Confirmation = BarInterval.Minutes(15);
    private static readonly BarInterval Trend = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task DmiOpposition_ProducesDmiVetoedReasonCode_NotGenericPrimaryTrendNotReady()
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Trend,
            ConfirmationInterval = Confirmation,
            EntryInterval = Entry,
            MinimumTrendConfidence = 0m,
            MinimumConfirmationConfidence = 0m,
            MinimumEntryConfidence = 0m,
            EnableDmiConfirmation = true
        };
        var agent = new ImprovedProgressiveAgent(options);

        // Bullish candle/Bollinger shape (tactical candidate = Buy, structure Unknown so no
        // structural/tactical contradiction) but a strong, directionally opposing ADX/DMI
        // reading — exactly the DMI-hard-veto branch inside DetectSide.
        AnalysisSnapshot trend = new()
        {
            Instrument = Instrument,
            Interval = Trend,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(Instrument, Now.AddHours(-1), Trend, 100m, 111m, 99m, 110m),
            Indicators = new IndicatorSnapshot
            {
                Atr = 2m,
                Rsi = 60m,
                BollingerMiddle = 105m,
                AdxAnalysis = new AdxAnalysisSnapshot
                {
                    Adx = 30m,
                    DirectionalBias = PriceActionDirection.Bearish
                }
            },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            Confidence = new ConfidenceScore { Total = 80m, Contributions = [] }
        };

        AnalysisSnapshot MinimalSnapshot(BarInterval interval) => new()
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(Instrument, Now.AddMinutes(-5), interval, 100m, 111m, 99m, 110m),
            Indicators = new IndicatorSnapshot { Atr = 2m, Rsi = 60m, BollingerMiddle = 105m },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            Confidence = new ConfidenceScore { Total = 80m, Contributions = [] }
        };

        AgentMarketContext context = new()
        {
            Instrument = Instrument,
            Timestamp = Now,
            Analysis = new MultiTimeframeAnalysis(
                Instrument,
                Now,
                new Dictionary<BarInterval, AnalysisSnapshot>
                {
                    [Trend] = trend,
                    [Confirmation] = MinimalSnapshot(Confirmation),
                    [Entry] = MinimalSnapshot(Entry)
                }),
            Account = new AccountSnapshot { AccountId = "test", CanTrade = true },
            Positions = [],
            OpenOrders = []
        };

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.ReasonCode, Is.EqualTo("DmiVetoed"));
        });
    }
}
