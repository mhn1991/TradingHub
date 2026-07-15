using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class RegimeDegradationReductionTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void NotTradeableRegimeWithSufficientProfit_TriggersReduction()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 1m,
            RegimeDegradationReductionFraction = 0.25m,
            MinimumRunnerFraction = 0.4m
        });
        AnalysisSnapshot analysis = Analysis(regime: NotTradeableRegime());
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.RegimeDegradation));
            Assert.That(result.PositionReduction?.QuantityToClose, Is.EqualTo(25m));
        });
    }

    [Test]
    public void TradeableRegime_NeverTriggers()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 1m,
            RegimeDegradationReductionFraction = 0.25m,
            MinimumRunnerFraction = 0.4m
        });
        // TrendingUp is tradeable, even though it's a real classified regime.
        AnalysisSnapshot analysis = Analysis(regime: TradeableRegime());
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void InsufficientOpenProfit_DoesNotTrigger()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 2m,
            RegimeDegradationReductionFraction = 0.25m,
            MinimumRunnerFraction = 0.4m
        });
        AnalysisSnapshot analysis = Analysis(regime: NotTradeableRegime());
        // Only 0.5R open profit, below the configured 2R minimum.
        ManagedTradeState trade = Trade(OrderSide.Buy, 100.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void MaximumReductions_CapsRepeatedTriggers()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 1m,
            RegimeDegradationReductionFraction = 0.10m,
            MaximumRegimeDegradationReductions = 1,
            MinimumRunnerFraction = 0.4m
        });
        AnalysisSnapshot analysis = Analysis(regime: NotTradeableRegime());
        // Already reduced once for this reason - the cap must block a second reduction.
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 90m,
            MinimumQuantityIncrement = 1m,
            RegimeDegradationReductionCount = 1
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void RunnerFloor_ClampsReductionBelowMinimumRunnerFraction()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 1m,
            // A large fraction that would otherwise cut well below the runner floor.
            RegimeDegradationReductionFraction = 0.90m,
            MinimumRunnerFraction = 0.60m
        });
        AnalysisSnapshot analysis = Analysis(regime: NotTradeableRegime());
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
        decimal remaining = 100m - result.PositionReduction!.QuantityToClose;
        Assert.That(remaining, Is.GreaterThanOrEqualTo(60m),
            "The reduction must never cut below the configured minimum runner fraction.");
    }

    [Test]
    public void ClassificationDisabled_UnknownRegimeStaysTradeable_BehaviourUnchanged()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRegimeDegradationReduction = true,
            RegimeDegradationMinimumOpenProfitR = 1m,
            RegimeDegradationReductionFraction = 0.25m,
            MinimumRunnerFraction = 0.4m
        });
        // No override: AnalysisSnapshot.MarketRegime defaults to MarketRegimeSnapshot.Unknown,
        // matching every existing test and every run with regime classification disabled.
        AnalysisSnapshot analysis = Analysis(regime: null);
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.That(result.PositionReduction, Is.Null);
    }

    private static MarketRegimeSnapshot NotTradeableRegime() => new()
    {
        Regime = MarketRegime.HighVolatilityDisorder,
        Confidence = 85m,
        ConfirmedAt = Start,
        AgeCandles = 5,
        Contributions = [],
        ReasonCode = "HighVolatilityDisorder",
        IsTradeable = false
    };

    private static MarketRegimeSnapshot TradeableRegime() => new()
    {
        Regime = MarketRegime.TrendingUp,
        Confidence = 85m,
        ConfirmedAt = Start,
        AgeCandles = 5,
        Contributions = [],
        ReasonCode = "TrendingUp",
        IsTradeable = true
    };

    private static ManagedTradeState Trade(OrderSide side, decimal currentPrice) => new()
    {
        Instrument = Instrument,
        Side = side,
        EntryPrice = 100m,
        InitialStopPrice = side == OrderSide.Buy ? 99m : 101m,
        CurrentStopPrice = side == OrderSide.Buy ? 98m : 102m,
        CurrentPrice = currentPrice,
        EvaluatedAt = Start,
        MinimumPriceIncrement = 0.01m
    };

    private static AnalysisSnapshot Analysis(MarketRegimeSnapshot? regime)
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
            MarketRegime = regime ?? MarketRegimeSnapshot.Unknown,
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
    }
}
