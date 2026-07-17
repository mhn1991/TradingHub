using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class ConfidenceScaledStagnationTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void Disabled_UsesFixedStagnationBars_RegardlessOfConfidence()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationBars = 12,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = false
        });
        // Even at zero confidence, the fixed 12-bar threshold (not the low-confidence 8) governs.
        ManagedTradeState trade = Trade(EntryConfidence: 0m, barsWithoutNewMfe: 12);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.Stagnation));
    }

    [Test]
    public void Enabled_LowConfidence_TriggersAtMinimumBars()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = true,
            MinimumStagnationBarsAtLowConfidence = 8,
            MaximumStagnationBarsAtHighConfidence = 20
        });
        ManagedTradeState trade = Trade(EntryConfidence: 0m, barsWithoutNewMfe: 8);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.Stagnation));
    }

    [Test]
    public void Enabled_LowConfidence_BelowMinimumBars_DoesNotTrigger()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = true,
            MinimumStagnationBarsAtLowConfidence = 8,
            MaximumStagnationBarsAtHighConfidence = 20
        });
        ManagedTradeState trade = Trade(EntryConfidence: 0m, barsWithoutNewMfe: 7);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void Enabled_HighConfidence_RequiresMaximumBars()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = true,
            MinimumStagnationBarsAtLowConfidence = 8,
            MaximumStagnationBarsAtHighConfidence = 20
        });
        // At full confidence, 19 stagnant bars is still short of the 20-bar high-confidence threshold.
        ManagedTradeState trade = Trade(EntryConfidence: 100m, barsWithoutNewMfe: 19);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void Enabled_HighConfidence_TriggersAtMaximumBars()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = true,
            MinimumStagnationBarsAtLowConfidence = 8,
            MaximumStagnationBarsAtHighConfidence = 20
        });
        ManagedTradeState trade = Trade(EntryConfidence: 100m, barsWithoutNewMfe: 20);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.Stagnation));
    }

    [Test]
    public void Enabled_MidConfidence_InterpolatesThreshold()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.75m,
            StagnationReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.4m,
            EnableConfidenceScaledStagnation = true,
            MinimumStagnationBarsAtLowConfidence = 8,
            MaximumStagnationBarsAtHighConfidence = 20
        });
        // 50% confidence interpolates to 8 + 0.5 * (20 - 8) = 14 bars.
        ManagedTradeState belowThreshold = Trade(EntryConfidence: 50m, barsWithoutNewMfe: 13);
        ManagedTradeState atThreshold = Trade(EntryConfidence: 50m, barsWithoutNewMfe: 14);

        TradeManagementRecommendation belowResult = manager.Evaluate(belowThreshold, Analysis());
        TradeManagementRecommendation atResult = manager.Evaluate(atThreshold, Analysis());

        Assert.Multiple(() =>
        {
            Assert.That(belowResult.PositionReduction, Is.Null);
            Assert.That(atResult.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.Stagnation));
        });
    }

    private static ManagedTradeState Trade(decimal EntryConfidence, int barsWithoutNewMfe) => new()
    {
        Instrument = Instrument,
        Side = OrderSide.Buy,
        EntryPrice = 100m,
        InitialStopPrice = 99m,
        CurrentStopPrice = 98m,
        CurrentPrice = 101.5m,
        EvaluatedAt = Start,
        MinimumPriceIncrement = 0.01m,
        InitialQuantity = 100m,
        CurrentQuantity = 100m,
        MinimumQuantityIncrement = 1m,
        EntryConfidence = EntryConfidence,
        AnalysisBarsWithoutNewMfe = barsWithoutNewMfe
    };

    private static AnalysisSnapshot Analysis()
    {
        BarInterval interval = BarInterval.Minutes(15);
        DateTimeOffset available = interval.AddTo(Start);
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = available,
            Version = 91,
            LatestCandle = TestCandles.Create(Instrument, Start, interval, 100m, 105m, 95m, 102m),
            Indicators = new IndicatorSnapshot { Atr = 0.5m },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot(),
            MarketRegime = MarketRegimeSnapshot.Unknown,
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
    }
}
