using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using RiskManager;
using Simulator.Models;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class QuantitativeRiskAndMultiTimeframeTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Now =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void FixedFractionalSizer_UsesOriginalStopRiskAndRoundsDown()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 0.5m,
            MinimumQuantity = 1m,
            QuantityStep = 10m,
            MaximumAccountMarginUsagePercent = 30m,
            MaximumSinglePositionMarginPercent = 10m,
            Leverage = 20m
        });

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99.1m),
            RequestedQuantity = 1_000m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        });

        // 0.5% of 100,000 = 500. 500 / 0.9 = 555.55, rounded down to 550.
        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True);
            Assert.That(result.RiskBudget, Is.EqualTo(500m));
            Assert.That(result.Quantity, Is.EqualTo(550m));
            Assert.That(result.EstimatedLossAtStop, Is.EqualTo(495m));
        });
    }

    [Test]
    public void PositionSizer_AppliesCurrencyConversionAndSinglePositionMarginCap()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            MaximumAccountMarginUsagePercent = 30m,
            MaximumSinglePositionMarginPercent = 0.1m,
            Leverage = 20m
        });

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            RequestedQuantity = 100_000m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 0.8m
        });

        // The risk budget would allow 1,250 units, but the 100 account-currency
        // single-position margin cap permits only floor(100 / (100*0.8/20)) = 25.
        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True);
            Assert.That(result.Quantity, Is.EqualTo(25m));
            Assert.That(result.EstimatedMargin, Is.EqualTo(100m));
            Assert.That(result.EstimatedLossAtStop, Is.EqualTo(20m));
        });
    }

    [Test]
    public void PreTradeRisk_UsesQuoteToAccountCurrencyConversion()
    {
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            MaximumLossPerTrade = 350m
        });

        RiskAssessment assessment = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            Quantity = 500m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 0.8m
        });

        Assert.Multiple(() =>
        {
            Assert.That(assessment.Approved, Is.False);
            Assert.That(assessment.EstimatedLossAtStop, Is.EqualTo(400m));
            Assert.That(assessment.Summary, Does.Contain("350"));
        });
    }

    [Test]
    public async Task ProgressiveAgent_UsesAllRoleIntervalsAndVetoesStrongSecondaryOpposition()
    {
        BarInterval entry = BarInterval.Minutes(5);
        BarInterval confirmation = BarInterval.Minutes(15);
        BarInterval setup = BarInterval.Minutes(30);
        BarInterval secondary = BarInterval.Hours(1);
        BarInterval trend = BarInterval.Hours(2);
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = trend,
            SecondaryTrendIntervals = [secondary],
            SetupIntervals = [setup],
            ConfirmationInterval = confirmation,
            EntryInterval = entry,
            MinimumSecondaryTrendAlignments = 1,
            MinimumSetupAlignments = 1,
            MinimumConfirmationAlignments = 1,
            StrongOppositionVeto = true,
            PriceActionConfirmation = PriceActionConfirmationMode.Disabled
        };
        var agent = new LegacyProgressiveAgent(options);
        AgentMarketContext context = Context(
            Snapshot(trend, bullish: true),
            Snapshot(secondary, bullish: false, breakDirection: MarketStructureBreak.Bearish),
            Snapshot(setup, bullish: true),
            Snapshot(confirmation, bullish: true),
            Snapshot(entry, bullish: true));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(agent.RequiredIntervals, Is.EquivalentTo(
                new[] { entry, confirmation, setup, secondary, trend }));
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.ReasonCode, Is.EqualTo("SecondaryTrendOppositionVeto"));
        });
    }

    [Test]
    public async Task ProgressiveAgent_AllowsSecondaryPullbackWhenAlignmentIsSoftAndNoOpposingBreakExists()
    {
        BarInterval entry = BarInterval.Minutes(5);
        BarInterval confirmation = BarInterval.Minutes(15);
        BarInterval setup = BarInterval.Minutes(30);
        BarInterval secondary = BarInterval.Hours(1);
        BarInterval trend = BarInterval.Hours(2);
        var agent = new LegacyProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = trend,
            SecondaryTrendIntervals = [secondary],
            SetupIntervals = [setup],
            ConfirmationInterval = confirmation,
            EntryInterval = entry,
            MinimumSecondaryTrendAlignments = 0,
            MinimumSetupAlignments = 1,
            MinimumConfirmationAlignments = 1,
            StrongOppositionVeto = true,
            PriceActionConfirmation = PriceActionConfirmationMode.Disabled
        });

        AgentDecision decision = await agent.EvaluateAsync(Context(
            Snapshot(trend, bullish: true),
            Snapshot(secondary, bullish: false),
            Snapshot(setup, bullish: true),
            Snapshot(confirmation, bullish: true),
            Snapshot(entry, bullish: true)));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(decision.Reason, Does.Contain("secondary 0/0"));
        });
    }

    [Test]
    public async Task ProgressiveAgent_EntersWhenRoleBasedConsensusIsAligned()
    {
        BarInterval entry = BarInterval.Minutes(5);
        BarInterval confirmation = BarInterval.Minutes(15);
        BarInterval setup = BarInterval.Minutes(30);
        BarInterval secondary = BarInterval.Hours(1);
        BarInterval trend = BarInterval.Hours(2);
        var agent = new LegacyProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = trend,
            SecondaryTrendIntervals = [secondary],
            SetupIntervals = [setup],
            ConfirmationInterval = confirmation,
            EntryInterval = entry,
            MinimumSecondaryTrendAlignments = 1,
            MinimumSetupAlignments = 1,
            MinimumConfirmationAlignments = 1,
            PriceActionConfirmation = PriceActionConfirmationMode.Disabled
        });

        AgentDecision decision = await agent.EvaluateAsync(Context(
            Snapshot(trend, bullish: true),
            Snapshot(secondary, bullish: true),
            Snapshot(setup, bullish: true),
            Snapshot(confirmation, bullish: true),
            Snapshot(entry, bullish: true)));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(decision.Reason, Does.Contain("secondary 1/1"));
            Assert.That(decision.Reason, Does.Contain("setup 1/1"));
            Assert.That(decision.Reason, Does.Contain("confirmation 1/1"));
        });
    }

    [Test]
    public void RuntimeValidation_RejectsManagementIntervalsThatCannotBeAggregatedFromAnalysisBase()
    {
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = BarInterval.Minutes(1),
            AnalysisBaseInterval = BarInterval.Minutes(5),
            AnalysisIntervals = [BarInterval.Minutes(15), BarInterval.Hours(1)],
            LegacyPositionManagement = PositionManagementOptions.LegacyDefaults with
            {
                FastStructureInterval = BarInterval.Minutes(7),
                MainStructureInterval = BarInterval.Minutes(15),
                ThesisInterval = BarInterval.Hours(1)
            }
        };

        Assert.That(
            () => runtime.Validate(),
            Throws.ArgumentException.With.Message.Contains("not aligned"));
    }

    [Test]
    public void MechanicalScope_ProtectsProfitWithoutWaitingForStructureSnapshot()
    {
        var manager = new StructureBasedTradeManager(PositionManagementOptions.LegacyDefaults);
        ManagedTradeState trade = ManagedTrade(currentPrice: 110m, maximumFavourableR: 1.1m);

        TradeManagementRecommendation recommendation = manager.Evaluate(
            trade,
            Snapshot(BarInterval.Minutes(5), bullish: true),
            TradeManagementEvaluationScope.Mechanical);

        Assert.Multiple(() =>
        {
            Assert.That(recommendation.Action, Is.EqualTo(TradeManagementAction.ReduceAndMoveStop));
            Assert.That(recommendation.PositionReduction?.StageId, Is.EqualTo("scale-1r"));
            Assert.That(recommendation.PositionReduction?.QuantityToClose, Is.EqualTo(20m));
            Assert.That(recommendation.ProposedStopPrice, Is.GreaterThanOrEqualTo(100m));
        });
    }

    [Test]
    public void ThesisScope_DoesNotDuplicateMechanicalScaleOutOrStopMove()
    {
        var manager = new StructureBasedTradeManager(PositionManagementOptions.LegacyDefaults);

        TradeManagementRecommendation recommendation = manager.Evaluate(
            ManagedTrade(currentPrice: 110m, maximumFavourableR: 1.1m),
            Snapshot(BarInterval.Hours(1), bullish: true),
            TradeManagementEvaluationScope.Thesis);

        Assert.Multiple(() =>
        {
            Assert.That(recommendation.Action, Is.EqualTo(TradeManagementAction.Hold));
            Assert.That(recommendation.PositionReduction, Is.Null);
            Assert.That(recommendation.ProposedStopPrice, Is.Null);
        });
    }

    private static AgentDecision BuyDecision(decimal reference, decimal stop) => new()
    {
        DecisionId = "sizing-test",
        StrategyName = "test",
        Action = AgentAction.Buy,
        Instrument = Instrument,
        SuggestedQuantity = 1_000m,
        QuantityUnit = QuantityUnit.Units,
        ReferencePrice = reference,
        StopLossPrice = stop,
        Confidence = 80m,
        CreatedAt = Now,
        Reason = "test"
    };

    private static AccountSnapshot Account(decimal balance) => new()
    {
        AccountId = "account",
        Currency = "GBP",
        Balance = balance,
        MarginUsed = 0m,
        UnrealizedProfitLoss = 0m,
        CanTrade = true
    };

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = Now,
        Analysis = new MultiTimeframeAnalysis(
            Instrument,
            Now,
            snapshots.ToDictionary(snapshot => snapshot.Interval)),
        Account = Account(100_000m),
        Positions = [],
        OpenOrders = []
    };

    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        bool bullish,
        MarketStructureBreak breakDirection = MarketStructureBreak.None)
    {
        decimal open = bullish ? 100m : 110m;
        decimal close = bullish ? 110m : 100m;
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
                Math.Max(open, close) + 1m,
                Math.Min(open, close) - 1m,
                close),
            Indicators = new IndicatorSnapshot
            {
                Atr = 2m,
                Rsi = bullish ? 60m : 40m,
                BollingerMiddle = 105m
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
                Break = breakDirection,
                Strength = 80m
            },
            Confidence = new ConfidenceScore
            {
                Total = 80m,
                Contributions = []
            }
        };
    }

    private static ManagedTradeState ManagedTrade(
        decimal currentPrice,
        decimal maximumFavourableR) => new()
    {
        Instrument = Instrument,
        Side = OrderSide.Buy,
        EntryPrice = 100m,
        InitialStopPrice = 90m,
        CurrentStopPrice = 90m,
        CurrentPrice = currentPrice,
        InitialQuantity = 100m,
        CurrentQuantity = 100m,
        MinimumQuantityIncrement = 1m,
        MaximumFavourableExcursionR = maximumFavourableR,
        EvaluatedAt = Now,
        MinimumPriceIncrement = 0.01m,
        AnalysisBarsSinceLastAmendment = int.MaxValue,
        AnalysisBarsSinceLastReduction = int.MaxValue
    };
}
