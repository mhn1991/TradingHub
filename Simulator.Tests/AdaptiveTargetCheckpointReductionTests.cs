using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.TargetManagement;
using TradeManager;

namespace Simulator.Tests;

/// <summary>
/// Structural Indicator and Adaptive Target Management Plan §4.4: a v2 trade's target-aware
/// checkpoint partial replaces the generic <see cref="ScaleOutRule"/> loop for that trade only.
/// </summary>
[TestFixture]
public sealed class AdaptiveTargetCheckpointReductionTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void CheckpointReached_ProducesAdaptiveTargetCheckpointReduction_AtThePlannedFraction()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            MinimumRunnerFraction = 0.4m
        });
        ManagedTradeState trade = Trade(currentPrice: 101.5m, plan: Plan(checkpointR: 1.5m, partialFraction: 0.25m));

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.Multiple(() =>
        {
            Assert.That(result.PositionReduction, Is.Not.Null);
            Assert.That(result.PositionReduction!.Reason, Is.EqualTo(PositionReductionReason.AdaptiveTargetCheckpoint));
            Assert.That(result.PositionReduction.StageId, Is.EqualTo("checkpoint-1"));
            Assert.That(result.PositionReduction.FractionOfInitialQuantity, Is.EqualTo(0.25m));
            Assert.That(result.PositionReduction.QuantityToClose, Is.EqualTo(25m));
        });
    }

    [Test]
    public void CheckpointNotYetReached_DoesNotReduce()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions { Mode = TrailingStopMode.Disabled });
        // openProfitR = (100.9-100)/1 = 0.9R, below the 1.5R checkpoint.
        ManagedTradeState trade = Trade(currentPrice: 100.9m, plan: Plan(checkpointR: 1.5m, partialFraction: 0.25m));

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void CheckpointAlreadyProcessed_NeverReducesTwice()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions { Mode = TrailingStopMode.Disabled });
        ManagedTradeState trade = Trade(currentPrice: 101.5m, plan: Plan(checkpointR: 1.5m, partialFraction: 0.25m)) with
        {
            CompletedReductionStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "checkpoint-1" }
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void AdaptiveTrade_NeverFiresGenericScaleOutRules_EvenWhenConfiguredAndTriggered()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableScaleOut = true,
            ScaleOutRules = [new ScaleOutRule { StageId = "generic-1", ActivationR = 1.0m, FractionOfInitialQuantity = 0.15m }],
            MinimumRunnerFraction = 0.4m
        });
        // openProfitR = 1.5R clears the generic rule's 1.0R activation, but the trade carries an
        // adaptive plan with no checkpoint due yet (2.5R) - the generic rule must still not fire.
        ManagedTradeState trade = Trade(currentPrice: 101.5m, plan: Plan(checkpointR: 2.5m, partialFraction: 0.25m));

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void FixedStructuralTarget_NeverPartials()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions { Mode = TrailingStopMode.Disabled });
        ManagedTradeState trade = Trade(currentPrice: 105m, plan: Plan(checkpointR: 1.5m, partialFraction: 0m) with
        {
            ExitPolicy = TradeExitPolicy.FixedStructuralTarget,
            SelectedCheckpointId = null
        });

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.That(result.PositionReduction, Is.Null);
    }

    [Test]
    public void PlanMinimumRunnerFraction_OverridesALowerProfileFloor()
    {
        // Profile floor is 0.1 (would allow closing 90%), but the plan pins 0.6 at entry -
        // CreateReduction must respect the higher, plan-pinned floor.
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            MinimumRunnerFraction = 0.1m
        });
        TradeTargetPlan plan = Plan(checkpointR: 1.5m, partialFraction: 0.5m) with { MinimumRunnerFraction = 0.6m };
        ManagedTradeState trade = Trade(currentPrice: 101.5m, plan: plan);

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis());

        Assert.Multiple(() =>
        {
            Assert.That(result.PositionReduction, Is.Not.Null);
            // Desired 50% would leave 50 (below the 60 floor) - clamped to 40 closed / 60 remaining.
            Assert.That(result.PositionReduction!.QuantityToClose, Is.EqualTo(40m));
            Assert.That(result.PositionReduction.QuantityRemainingAfterReduction, Is.EqualTo(60m));
        });
    }

    private static TradeTargetPlan Plan(decimal checkpointR, decimal partialFraction) => new()
    {
        PlanVersion = 1,
        Revision = 0,
        ExitPolicy = TradeExitPolicy.PartialThenRunner,
        OriginalEntry = 100m,
        OriginalStop = 99m,
        InitialRiskPrice = 1m,
        SelectedCheckpointId = "checkpoint-1",
        SelectedTerminalId = "terminal-1",
        Candidates =
        [
            new TradeTargetCandidate
            {
                CandidateId = "checkpoint-1",
                ClusterId = "checkpoint-1",
                SourceKind = TradeTargetSourceKind.Swing,
                SourceId = "swing-1",
                SourceInterval = BarInterval.Minutes(15),
                LifecycleState = "Confirmed",
                LowerBoundary = 100m + checkpointR,
                UpperBoundary = 100m + checkpointR,
                ExecutionPrice = 100m + checkpointR,
                Role = TradeTargetRole.Checkpoint,
                Tier = TradeTargetSignificanceTier.TierC,
                DistanceAtr = checkpointR,
                TargetR = checkpointR,
                AvailableAt = Start
            }
        ],
        PartialFraction = partialFraction,
        MinimumRunnerFraction = 0.5m,
        PlannedR = 0.25m * checkpointR + 0.75m * 3m,
        ConservativeOpportunityR = 3m,
        CreatedAt = Start,
        LastRevisedAt = Start
    };

    private static ManagedTradeState Trade(decimal currentPrice, TradeTargetPlan plan) => new()
    {
        Instrument = Instrument,
        Side = OrderSide.Buy,
        EntryPrice = 100m,
        InitialStopPrice = 99m,
        CurrentStopPrice = 99m,
        CurrentPrice = currentPrice,
        EvaluatedAt = Start,
        MinimumPriceIncrement = 0.01m,
        InitialQuantity = 100m,
        CurrentQuantity = 100m,
        MinimumQuantityIncrement = 1m,
        ExitPolicy = plan.ExitPolicy,
        TargetPlan = plan
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
            Version = 1,
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
