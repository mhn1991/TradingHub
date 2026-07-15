using Brokers.Models;
using ChartAnnotator.Models;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class EquityProtectionDirectiveTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void FlattenAllPositions_ForcesExitAheadOfEveryOtherRule()
    {
        // Configure a manager that would otherwise Hold (no rules enabled/triggered).
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled
        });
        var directive = new EquityProtectionDirective
        {
            TierId = "flatten-tier",
            Action = EquityProtectionPositionAction.FlattenAllPositions
        };

        TradeManagementRecommendation result = manager.Evaluate(
            Trade(OrderSide.Buy, 101.5m),
            Analysis(90, atr: 0.5m),
            TradeManagementEvaluationScope.Combined,
            directive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(result.ExitReason, Is.EqualTo(TradeManagementExitReason.EquityProtectionBreach));
            Assert.That(result.ReasonCode, Is.EqualTo("EquityProtectionFlatten"));
        });
    }

    [Test]
    public void ReduceOpenPositions_ForcesReductionRespectingRunnerFloor()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            MinimumRunnerFraction = 0.5m
        });
        var directive = new EquityProtectionDirective
        {
            TierId = "reduce-tier",
            Action = EquityProtectionPositionAction.ReduceOpenPositions,
            // A large fraction that would otherwise cut well below the runner floor.
            ReductionFraction = 0.9m
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade,
            Analysis(90, atr: 0.5m),
            TradeManagementEvaluationScope.Combined,
            directive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.RiskReduction));
            Assert.That(result.PositionReduction?.StageId, Is.EqualTo("equity-protection-reduce-tier"));
            decimal remaining = 100m - result.PositionReduction!.QuantityToClose;
            Assert.That(remaining, Is.GreaterThanOrEqualTo(50m),
                "The reduction must never cut below the configured minimum runner fraction.");
        });
    }

    [Test]
    public void ReduceOpenPositions_DoesNotRefireForATierAlreadyCompleted()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            MinimumRunnerFraction = 0.4m
        });
        var directive = new EquityProtectionDirective
        {
            TierId = "reduce-tier",
            Action = EquityProtectionPositionAction.ReduceOpenPositions,
            ReductionFraction = 0.2m
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 80m,
            MinimumQuantityIncrement = 1m,
            CompletedReductionStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "equity-protection-reduce-tier"
            }
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade,
            Analysis(90, atr: 0.5m),
            TradeManagementEvaluationScope.Combined,
            directive);

        Assert.That(result.PositionReduction, Is.Null,
            "A tier that already completed its reduction must not fire again.");
    }

    [Test]
    public void NullDirective_IsANoOp_MatchingPreExistingBehaviour()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled
        });

        TradeManagementRecommendation withDefault = manager.Evaluate(
            Trade(OrderSide.Buy, 101.5m),
            Analysis(90, atr: 0.5m));
        TradeManagementRecommendation withExplicitNull = manager.Evaluate(
            Trade(OrderSide.Buy, 101.5m),
            Analysis(90, atr: 0.5m),
            TradeManagementEvaluationScope.Combined,
            null);

        Assert.Multiple(() =>
        {
            Assert.That(withDefault.Action, Is.EqualTo(TradeManagementAction.Hold));
            Assert.That(withExplicitNull.Action, Is.EqualTo(TradeManagementAction.Hold));
        });
    }

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

    private static AnalysisSnapshot Analysis(long version, decimal atr)
    {
        BarInterval interval = BarInterval.Minutes(5);
        DateTimeOffset available = interval.AddTo(Start);
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = available,
            Version = version,
            LatestCandle = TestCandles.Create(Instrument, Start, interval, 100m, 105m, 95m, 102m),
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
