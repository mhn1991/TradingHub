using Brokers.Models;
using ChartAnnotator.Models;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class NeoWaveTradeManagementTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly DateTimeOffset Start =
        new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void Buy_CrossesEntryPinnedInvalidationWithBuffer_Exits()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableNeoWaveInvalidationExit = true,
            NeoWaveInvalidationBufferAtr = 0.10m
        });

        TradeManagementRecommendation result = manager.Evaluate(
            Trade(OrderSide.Buy, currentPrice: 98.89m, invalidation: 99m),
            Analysis(atr: 1m),
            TradeManagementEvaluationScope.Thesis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(result.ExitReason, Is.EqualTo(TradeManagementExitReason.NeoWaveInvalidation));
            Assert.That(result.ReasonCode, Is.EqualTo("NeoWaveEntryHypothesisInvalidated"));
            Assert.That(result.Reason, Does.Contain("wave-entry-1"));
        });
    }

    [Test]
    public void Sell_CrossesEntryPinnedInvalidationWithBuffer_Exits()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableNeoWaveInvalidationExit = true,
            NeoWaveInvalidationBufferAtr = 0.25m
        });

        TradeManagementRecommendation result = manager.Evaluate(
            Trade(OrderSide.Sell, currentPrice: 101.26m, invalidation: 101m),
            Analysis(atr: 1m),
            TradeManagementEvaluationScope.Thesis);

        Assert.That(result.ExitReason, Is.EqualTo(TradeManagementExitReason.NeoWaveInvalidation));
    }

    [Test]
    public void MarginalCrossInsideConfiguredBuffer_DoesNotExit()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableNeoWaveInvalidationExit = true,
            NeoWaveInvalidationBufferAtr = 0.25m
        });

        TradeManagementRecommendation result = manager.Evaluate(
            Trade(OrderSide.Buy, currentPrice: 98.80m, invalidation: 99m),
            Analysis(atr: 1m),
            TradeManagementEvaluationScope.Thesis);

        Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Hold));
    }

    [Test]
    public void FeatureDisabled_DoesNotUseEntryInvalidation()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableNeoWaveInvalidationExit = false
        });

        TradeManagementRecommendation result = manager.Evaluate(
            Trade(OrderSide.Buy, currentPrice: 98m, invalidation: 99m),
            Analysis(atr: 1m),
            TradeManagementEvaluationScope.Thesis);

        Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Hold));
    }

    [Test]
    public void MissingEntryInvalidation_FailsNeutral()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableNeoWaveInvalidationExit = true
        });

        ManagedTradeState trade = Trade(OrderSide.Buy, currentPrice: 98m, invalidation: 99m) with
        {
            EntryNeoWaveInvalidationPrice = null
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade,
            Analysis(atr: 1m),
            TradeManagementEvaluationScope.Thesis);

        Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Hold));
    }

    private static ManagedTradeState Trade(
        OrderSide side,
        decimal currentPrice,
        decimal invalidation) => new()
    {
        Instrument = Instrument,
        Side = side,
        EntryPrice = 100m,
        InitialStopPrice = side == OrderSide.Buy ? 99m : 101m,
        CurrentStopPrice = side == OrderSide.Buy ? 99m : 101m,
        CurrentPrice = currentPrice,
        EvaluatedAt = Start,
        MinimumPriceIncrement = 0.0001m,
        EntryNeoWaveHypothesisId = "wave-entry-1",
        EntryNeoWaveInvalidationPrice = invalidation
    };

    private static AnalysisSnapshot Analysis(decimal atr)
    {
        BarInterval interval = BarInterval.Minutes(15);
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = interval.AddTo(Start),
            Version = 1,
            LatestCandle = TestCandles.Create(
                Instrument, Start, interval, 100m, 101m, 98m, 99m),
            Indicators = new IndicatorSnapshot { Atr = atr },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot(),
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
    }
}
