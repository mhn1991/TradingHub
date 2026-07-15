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
    public void PositionSizer_AppliesContractMultiplierToPerUnitRisk()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 0.5m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            MaximumAccountMarginUsagePercent = 30m,
            MaximumSinglePositionMarginPercent = 10m,
            Leverage = 20m
        });

        // Multiplier 10 makes per-unit loss 9 instead of 0.9 → quantity 55 instead of 555.
        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99.1m),
            RequestedQuantity = 1_000m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m,
            InstrumentSpec = new InstrumentRiskSpec { ContractMultiplier = 10m }
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True);
            Assert.That(result.RiskBudget, Is.EqualTo(500m));
            Assert.That(result.Quantity, Is.EqualTo(55m));
            Assert.That(result.EstimatedLossAtStop, Is.EqualTo(495m));
        });
    }

    [Test]
    public void PositionSizer_CapsQuantityUsingPortfolioOpenRiskHeat()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            MaximumAccountMarginUsagePercent = 50m,
            MaximumSinglePositionMarginPercent = 50m,
            Leverage = 50m,
            MaximumOpenRiskPercentOfEquity = 1m
        });

        // Equity 100_000 → 1% heat budget = 1_000. Known open risk 700 leaves 300 for the new trade.
        // Per-unit loss = 1 → quantity capped at 300 (risk budget alone would allow 1_000).
        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            RequestedQuantity = 100_000m,
            Accounts = [Account(balance: 100_000m)],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "open-1",
                    Instrument = new InstrumentKey("FX:EUR/USD"),
                    Side = OrderSide.Buy,
                    Quantity = 700m,
                    AveragePrice = 1.1m
                }
            ],
            QuoteToAccountCurrencyRate = 1m,
            KnownOpenRiskAccountCurrency = 700m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True);
            Assert.That(result.Quantity, Is.EqualTo(300m));
            Assert.That(result.OpenRiskAccountCurrency, Is.EqualTo(700m));
            Assert.That(result.ProjectedOpenRiskAccountCurrency, Is.EqualTo(1_000m));
        });
    }

    [Test]
    public void PreTradeRisk_RejectsWhenProjectedOpenRiskHeatExceedsCap()
    {
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            MaximumOpenRiskPercentOfEquity = 1m
        });

        RiskAssessment assessment = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            Quantity = 500m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m,
            KnownOpenRiskAccountCurrency = 700m
        });

        // New trade risk 500 + open 700 = 1_200 → 1.2% of equity > 1%.
        Assert.Multiple(() =>
        {
            Assert.That(assessment.Approved, Is.False);
            Assert.That(assessment.OpenRiskAccountCurrency, Is.EqualTo(700m));
            Assert.That(assessment.ProjectedOpenRiskAccountCurrency, Is.EqualTo(1_200m));
            Assert.That(assessment.Summary, Does.Contain("open risk").IgnoreCase);
        });
    }

    [Test]
    public void PreTradeRisk_EstimatesOpenRiskFromAssumedStopDistancePercent()
    {
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            MaximumOpenRiskPercentOfEquity = 2m,
            AssumedOpenPositionRiskDistancePercentOfPrice = 1m
        });

        // Open position on a different instrument so pyramiding rules do not apply:
        // avg 100, qty 200, assumed distance 1% of price = 1 → open risk 200.
        // New trade: distance 1 × qty 100 = 100. Projected 300 → 0.3% of equity, under 2%.
        RiskAssessment assessment = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            Quantity = 100m,
            Accounts = [Account(balance: 100_000m)],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "open-1",
                    Instrument = new InstrumentKey("FX:EUR/USD"),
                    Side = OrderSide.Buy,
                    Quantity = 200m,
                    AveragePrice = 100m
                }
            ],
            QuoteToAccountCurrencyRate = 1m
        });

        Assert.Multiple(() =>
        {
            Assert.That(assessment.Approved, Is.True, assessment.Summary);
            Assert.That(assessment.OpenRiskAccountCurrency, Is.EqualTo(200m));
            Assert.That(assessment.ProjectedOpenRiskAccountCurrency, Is.EqualTo(300m));
        });
    }

    [Test]
    public void PreTradeRisk_DoesNotRequireCurrencyRateForStructuralChecksOnly()
    {
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            MinimumRewardRiskRatio = 1.5m
        });

        RiskAssessment assessment = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m) with
            {
                TakeProfitPrice = 102m
            },
            Quantity = 100m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 0m
        });

        Assert.That(assessment.Approved, Is.True, assessment.Summary);
    }

    [Test]
    public void MaximumLossPercentageOfBalance_UsesPercentagePoints()
    {
        // 0.5 means half a percent of balance, not 50%.
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            MaximumLossPercentageOfBalance = 0.5m
        });

        // Loss 600 on 100_000 balance = 0.6% → reject.
        RiskAssessment rejected = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            Quantity = 600m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        });

        // Loss 400 = 0.4% → approve.
        RiskAssessment approved = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = BuyDecision(reference: 100m, stop: 99m),
            Quantity = 400m,
            Accounts = [Account(balance: 100_000m)],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        });

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Approved, Is.False);
            Assert.That(rejected.Summary, Does.Contain("0.500%"));
            Assert.That(approved.Approved, Is.True, approved.Summary);
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
    public async Task ProgressiveAgent_ContradictoryStructureAndCandle_WaitsWithoutDmiResolution()
    {
        BarInterval entry = BarInterval.Minutes(5);
        BarInterval confirmation = BarInterval.Minutes(15);
        BarInterval trend = BarInterval.Hours(1);
        var agent = new LegacyProgressiveAgent(new ProgressiveStrategyOptions
        {
            TrendInterval = trend,
            ConfirmationInterval = confirmation,
            EntryInterval = entry,
            PriceActionConfirmation = PriceActionConfirmationMode.Disabled
        });
        AnalysisSnapshot contradictoryTrend = Snapshot(trend, bullish: true) with
        {
            LatestCandle = TestCandles.Create(
                Instrument,
                Now.AddHours(-1),
                trend,
                110m,
                111m,
                99m,
                100m),
            Indicators = new IndicatorSnapshot
            {
                Atr = 2m,
                Rsi = 50m,
                BollingerMiddle = 105m,
                AdxAnalysis = AdxAnalysisSnapshot.Empty
            }
        };

        AgentDecision decision = await agent.EvaluateAsync(Context(
            contradictoryTrend,
            Snapshot(confirmation, bullish: true),
            Snapshot(entry, bullish: true)));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.ReasonCode, Is.EqualTo("PrimaryTrendNotReady"));
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
        // Pure mechanical frames still move stops / floors, but R-threshold scale-outs
        // require a management-bar scope so 1s/5s noise does not fire partials (P2).
        var manager = new StructureBasedTradeManager(PositionManagementOptions.LegacyDefaults);
        ManagedTradeState trade = ManagedTrade(currentPrice: 110m, maximumFavourableR: 1.1m);

        TradeManagementRecommendation mechanical = manager.Evaluate(
            trade,
            Snapshot(BarInterval.Minutes(5), bullish: true),
            TradeManagementEvaluationScope.Mechanical);
        TradeManagementRecommendation managementBar = manager.Evaluate(
            trade,
            Snapshot(BarInterval.Minutes(5), bullish: true),
            TradeManagementEvaluationScope.FastStructure);

        Assert.Multiple(() =>
        {
            Assert.That(mechanical.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(mechanical.PositionReduction, Is.Null);
            Assert.That(mechanical.ProposedStopPrice, Is.GreaterThanOrEqualTo(100m));

            Assert.That(managementBar.Action, Is.EqualTo(TradeManagementAction.ReduceAndMoveStop));
            Assert.That(managementBar.PositionReduction?.StageId, Is.EqualTo("scale-1r"));
            Assert.That(managementBar.PositionReduction?.QuantityToClose, Is.EqualTo(20m));
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
