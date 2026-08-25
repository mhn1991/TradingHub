using Brokers.Models;
using ChartAnnotator.Models;
using TradeManager;

namespace Simulator.Tests;

/// <summary>
/// Regression cover for the TradeManager audit (2026-08-19): an equity-protection directive that
/// the runner floor cannot satisfy must stay visible, a stop candidate that cannot be placed must
/// not suppress one that can, and calibration must only ever tighten management.
/// </summary>
[TestFixture]
public sealed class TradeManagerGuardRegressionTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void EquityProtectionAtRunnerFloor_IsReportedRatherThanSilentlyDropped()
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
            ReductionFraction = 0.9m
        };
        // Scale-outs already took the position down to exactly the 50% runner floor, so no
        // reduction is possible. Before the fix this returned a plain Hold and the account-level
        // safety escalation vanished without a trace.
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 50m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade, Analysis(90, atr: 0.5m), TradeManagementEvaluationScope.Combined, directive);

        Assert.Multiple(() =>
        {
            Assert.That(result.ReasonCode, Does.StartWith("EquityProtectionUnsatisfiable|"));
            Assert.That(result.Reason, Does.Contain("reduce-tier"));
            Assert.That(result.PositionReduction, Is.Null);
        });
    }

    [Test]
    public void SatisfiableEquityProtection_IsNotFlagged()
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
            ReductionFraction = 0.9m
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade, Analysis(90, atr: 0.5m), TradeManagementEvaluationScope.Combined, directive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.ReasonCode, Is.EqualTo("EquityProtectionReduce"));
        });
    }

    [Test]
    public void UnplaceableBreakEven_DoesNotSuppressAPlaceableProfitFloorStop()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.StructureAtr,
            BreakEvenActivationR = 1m,
            StructureTrailActivationR = 1m,
            BreakEvenBufferAtr = 0m,
            IncludeEstimatedExitCostsAtBreakEven = true,
            EnableProfitFloor = true,
            ProfitFloorRules = [new ProfitFloorRule { ActivationR = 1m, LockedProfitR = 0.5m }],
            EnableMaximumGiveback = false
        });

        // Tight stop: risk 0.10, round-trip costs 0.20, so the cost-adjusted break-even is 100.20 -
        // above the current price of 100.15 and therefore unplaceable. The 0.5R profit floor at
        // 100.05 is placeable and improves on the current stop, so it must be used.
        ManagedTradeState trade = new()
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            EntryPrice = 100m,
            InitialStopPrice = 99.90m,
            CurrentStopPrice = 99.90m,
            CurrentPrice = 100.15m,
            EvaluatedAt = Start,
            MinimumPriceIncrement = 0.01m,
            EntryCommissionPrice = 0.05m,
            ExpectedExitCommissionPrice = 0.05m,
            SpreadPrice = 0.05m,
            SlippagePrice = 0.025m,
            MaximumFavourableExcursionR = 1.5m
        };

        TradeManagementRecommendation result = manager.Evaluate(
            trade, Analysis(90, atr: 0.5m), TradeManagementEvaluationScope.Combined);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(result.ProposedStopPrice, Is.EqualTo(100.05m));
            Assert.That(result.AmendmentReason, Is.EqualTo(StopAmendmentReason.ProfitFloor));
        });
    }

    [Test]
    public void Calibration_NeverGrantsMorePatienceThanTheStaticPolicy()
    {
        // Cohort median duration of 40 bars is far longer than the static 12. Calibration may only
        // tighten, so StagnationBars must stay at the static value.
        var policy = new TradeManagementCalibrationPolicy(Artifact(medianDurationBars: 40m), minimumSamples: 1);
        var fallback = new PositionManagementOptions { StagnationBars = 12 };

        (PositionManagementOptions calibrated, string reason) = policy.Apply(fallback, Context());

        Assert.Multiple(() =>
        {
            Assert.That(reason, Does.StartWith("ManagementCalibration:"));
            Assert.That(calibrated.StagnationBars, Is.EqualTo(12));
        });
    }

    [Test]
    public void Calibration_StillTightensWhenTheCohortReviewsSooner()
    {
        var policy = new TradeManagementCalibrationPolicy(Artifact(medianDurationBars: 5m), minimumSamples: 1);
        var fallback = new PositionManagementOptions { StagnationBars = 12 };

        (PositionManagementOptions calibrated, _) = policy.Apply(fallback, Context());

        Assert.That(calibrated.StagnationBars, Is.EqualTo(5));
    }

    [Test]
    public void CohortMatching_IsCaseInsensitive()
    {
        var policy = new TradeManagementCalibrationPolicy(Artifact(medianDurationBars: 5m), minimumSamples: 1);

        (_, string reason) = policy.Apply(
            new PositionManagementOptions { StagnationBars = 12 },
            Context() with { Regime = "TRENDINGUP", VolatilityBucket = "normal", Session = "london" });

        Assert.That(reason, Does.StartWith("ManagementCalibration:"),
            "A case drift between the training pipeline and runtime must not degrade to static fallback.");
    }

    private static TradeManagementCalibrationContext Context() => new()
    {
        StrategyId = "strategy",
        InstrumentGroup = "FX",
        Regime = "TrendingUp",
        SetupType = "Setup",
        Direction = "Buy",
        Session = "London",
        VolatilityBucket = "Normal",
        Confidence = 65m
    };

    private static TradeManagementCalibration Artifact(decimal medianDurationBars) => new()
    {
        CalibrationId = "cal-1",
        SourceDataHash = "hash",
        CreatedAt = Start,
        Cohorts =
        [
            new TradeManagementCohort
            {
                CohortId = "cohort-1",
                StrategyId = "strategy",
                InstrumentGroup = "FX",
                Regime = "TrendingUp",
                SetupType = "Setup",
                Direction = "Buy",
                Session = "London",
                VolatilityBucket = "Normal",
                ConfidenceBucket = 60,
                Samples = 50,
                WinnerMfe80PercentileByBar = new Dictionary<int, decimal>(),
                MedianMaeBeforeHalfR = 0.3m,
                MedianDurationBars = medianDurationBars,
                MedianExitEfficiency = 0.5m,
                MedianStopDistance = 0.5m
            }
        ]
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
