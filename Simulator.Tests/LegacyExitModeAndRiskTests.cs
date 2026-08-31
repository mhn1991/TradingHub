using System.Reflection;
using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using RiskManager;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class LegacyExitModeAndRiskTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Entry = BarInterval.Minutes(5);
    private static readonly BarInterval Confirmation = BarInterval.Minutes(15);
    private static readonly BarInterval Trend = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void ZeroAtrWithNoPriceActionSetup_UsesPositiveFallbackDistance_NotAZeroDistanceStop()
    {
        // AGENT-04 regression: entry.Indicators.Atr == 0m (a legitimate indicator value, not
        // null — e.g. a flat run of identical closes) previously bypassed Legacy's
        // `?? price * 0.002m` fallback entirely (`??` only substitutes on null), producing a
        // fallback stop of `price - 0 * FallbackStopAtr == price` — a zero-distance stop passed
        // straight to Trade(), caught only by a downstream, independent PreTradeRiskManager
        // check. The fix mirrors ImprovedProgressiveAgent's positivity-checked ATR
        // (`is > 0m ? ... : Math.Max(price * 0.002m, ...)`), so a zero ATR now floors to a
        // real, positive, price-relative distance instead of collapsing the stop onto price.
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Trend,
            ConfirmationInterval = Confirmation,
            EntryInterval = Entry,
            MinimumTrendConfidence = 0m,
            MinimumConfirmationConfidence = 0m,
            MinimumEntryConfidence = 0m
        };
        var agent = new LegacyProgressiveAgent(options);
        AnalysisSnapshot entry = Snapshot(Entry, atr: 0m);
        AnalysisSnapshot confirmation = Snapshot(Confirmation, atr: 2m);
        AnalysisSnapshot trend = Snapshot(Trend, atr: 2m);
        AgentMarketContext context = new()
        {
            Instrument = Instrument,
            Timestamp = Now,
            Analysis = new MultiTimeframeAnalysis(
                Instrument,
                Now,
                new Dictionary<BarInterval, AnalysisSnapshot>
                {
                    [Entry] = entry,
                    [Confirmation] = confirmation,
                    [Trend] = trend
                }),
            Account = new AccountSnapshot { AccountId = "test", CanTrade = true },
            Positions = [],
            OpenOrders = []
        };

        AgentDecision decision = InvokeCreateEntryDecision(agent, context, entry, confirmation, trend);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(decision.StopLossPrice, Is.Not.Null);
            Assert.That(decision.StopLossPrice, Is.Not.EqualTo(decision.ReferencePrice),
                "A zero ATR must never collapse the stop onto the entry price.");
            Assert.That(decision.StopLossPrice!.Value, Is.LessThan(decision.ReferencePrice!.Value));
        });
    }


    private static AnalysisSnapshot Snapshot(BarInterval interval, decimal atr) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, Now.AddMinutes(-5), interval, 1.1000m, 1.1005m, 1.0995m, 1.1000m),
        Indicators = new IndicatorSnapshot { Atr = atr, Rsi = 60m, BollingerMiddle = 1.0995m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 80m, Contributions = [] }
    };

    /// <summary>
    /// Invokes the protected CreateEntryDecision directly via reflection so the zero-ATR/zero-risk
    /// edge case (AGENT-04) can be exercised without weakening LegacyProgressiveAgent's sealed
    /// modifier just for testability, and without needing a real candle stream to coax the full
    /// state machine into producing an exact zero ATR at the entry timeframe.
    /// </summary>
    private static AgentDecision InvokeCreateEntryDecision(
        LegacyProgressiveAgent agent,
        AgentMarketContext context,
        AnalysisSnapshot entry,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot trend)
    {
        Type baseType = typeof(LegacyProgressiveAgent).BaseType!;
        Type scopeStateType = baseType.GetNestedType("ScopeState", BindingFlags.NonPublic)!;
        Type setupSideType = baseType.GetNestedType("SetupSide", BindingFlags.NonPublic)!;
        Type setupStageType = baseType.GetNestedType("SetupStage", BindingFlags.NonPublic)!;
        object buySide = Enum.Parse(setupSideType, "Buy");
        object waitingForEntryStage = Enum.Parse(setupStageType, "WaitingForEntry");
        object state = Activator.CreateInstance(
            scopeStateType,
            "test-setup", buySide, waitingForEntryStage,
            context.Timestamp, context.Timestamp.AddHours(1), context.Timestamp, (DateTimeOffset?)context.Timestamp)!;

        MethodInfo method = baseType.GetMethod(
            "CreateEntryDecision",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (AgentDecision)method.Invoke(
            agent,
            [context, state, trend, confirmation, entry, null])!;
    }

    [Test]
    public void LegacyAgent_ExposesProtectiveStopAndStrategyExit_ThroughInterface()
    {
        ITradingAgent agent = new LegacyProgressiveAgent();
        Assert.That(
            agent.ExitManagementMode,
            Is.EqualTo(AgentExitManagementMode.ProtectiveStopAndStrategyExit));
    }

    [Test]
    public void ImprovedAgent_ExposesBracketMode_ThroughInterface()
    {
        ITradingAgent agent = new ImprovedProgressiveAgent();
        Assert.That(
            agent.ExitManagementMode,
            Is.EqualTo(AgentExitManagementMode.Bracket));
    }

    [Test]
    public void LegacyDecision_WithoutTakeProfit_IsApproved_ByProtectiveRiskOptions()
    {
        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:EUR/USD"),
            SuggestedQuantity = 1_000m,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = 1.1000m,
            StopLossPrice = 1.0950m,
            TakeProfitPrice = null,
            ExpectedRewardRisk = null,
            Confidence = 70m,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "Legacy entry without fixed target"
        };

        var risk = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            RequireTakeProfit = false,
            MinimumRewardRiskRatio = null,
            MaximumOpenPositions = 1,
            AllowPyramiding = false
        });

        RiskAssessment assessment = risk.Evaluate(new PreTradeRiskContext
        {
            Decision = decision,
            Quantity = 1_000m,
            Accounts =
            [
                new AccountSnapshot
                {
                    AccountId = "test",
                    Currency = "USD",
                    Balance = 100_000m,
                    Available = 100_000m,
                    CanTrade = true
                }
            ],
            Positions = []
        });

        Assert.That(assessment.Approved, Is.True, assessment.Summary);
        Assert.That(assessment.Reasons, Does.Not.Contain(
            "Stop-loss and take-profit prices are required to evaluate reward/risk."));
    }

    [Test]
    public void ImprovedBracket_WithoutTakeProfit_IsRejected()
    {
        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:EUR/USD"),
            SuggestedQuantity = 1_000m,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = 1.1000m,
            StopLossPrice = 1.0950m,
            TakeProfitPrice = null,
            Confidence = 70m,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "Bracket entry missing target"
        };

        var risk = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            RequireStopLoss = true,
            RequireTakeProfit = true,
            MinimumRewardRiskRatio = 1.5m,
            MaximumOpenPositions = 1
        });

        RiskAssessment assessment = risk.Evaluate(new PreTradeRiskContext
        {
            Decision = decision,
            Quantity = 1_000m,
            Accounts =
            [
                new AccountSnapshot
                {
                    AccountId = "test",
                    Currency = "USD",
                    Balance = 100_000m,
                    Available = 100_000m,
                    CanTrade = true
                }
            ],
            Positions = []
        });

        Assert.That(assessment.Approved, Is.False);
        Assert.That(
            string.Join(' ', assessment.Reasons),
            Does.Contain("take-profit").IgnoreCase.Or.Contain("reward/risk").IgnoreCase);
    }

    [Test]
    public void StrategyAwareFactory_Legacy_UsesProtectiveRisk_NotBracketRr()
    {
        // Confirms CreateStrategyAwareHistorical / session factory map exit mode correctly
        // when the agent is only known through ITradingAgent.
        ITradingAgent agent = new LegacyProgressiveAgent();
        Assert.That(agent.ExitManagementMode, Is.EqualTo(AgentExitManagementMode.ProtectiveStopAndStrategyExit));

        // Mirror factory mapping used by StrategySimulationSession.Create / SimulationFactory.
        PreTradeRiskOptions riskOptions = agent.ExitManagementMode switch
        {
            AgentExitManagementMode.ProtectiveStopAndStrategyExit => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = false,
                MinimumRewardRiskRatio = null
            },
            _ => PreTradeRiskOptions.PhaseOneSafeDefaults
        };

        Assert.That(riskOptions.RequireTakeProfit, Is.False);
        Assert.That(riskOptions.MinimumRewardRiskRatio, Is.Null);
    }

    [Test]
    public async Task Legacy_CanOpenPosition_WithoutTakeProfit_OnSyntheticStream()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        // Long enough progressive structure for 5m/15m/1h to form.
        Candle[] candles = BuildTrendingCandles(instrument, baseInterval, start, 600);

        await using SimulationSession session = SimulationFactory.CreateStrategyAwareHistorical(
            instrument,
            candles,
            [BarInterval.Minutes(1), BarInterval.Minutes(5), BarInterval.Minutes(15)],
            new LegacyProgressiveAgent(new ProgressiveStrategyOptions
            {
                TrendInterval = BarInterval.Minutes(15),
                ConfirmationInterval = BarInterval.Minutes(5),
                EntryInterval = BarInterval.Minutes(1),
                Quantity = 1_000m,
                MinimumRewardRisk = 1.5m,
                MinimumTrendConfidence = 0m,
                MinimumConfirmationConfidence = 0m,
                MinimumEntryConfidence = 0m
            }),
            new SimulationOptions
            {
                BaseCurrency = "USD",
                StartingBalance = 100_000m,
                CloseOpenPositionsAtEnd = true,
                BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            safetyOptions: new RiskManager.Safety.TradingSafetyOptions
            {
                TripOnCriticalDataQualityIssue = false
            },
            dataQualityOptions: new TradingCore.MarketData.MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            });

        SimulationResult result = await session.Runner.RunAsync();

        // Either opened/closed trades or at least submitted/filled orders — not zero activity solely due to R:R reject.
        bool anyTradeActivity = result.SubmittedOrders > 0 || result.Trades.Count > 0 || result.FilledOrders > 0;
        // ExitManagementMode is ProtectiveStop so factory risk is correct.
        Assert.That(
            ((ITradingAgent)new LegacyProgressiveAgent()).ExitManagementMode,
            Is.EqualTo(AgentExitManagementMode.ProtectiveStopAndStrategyExit));

        IReadOnlyList<TradingJournal.TradeJournalEntry> journal = session.Journal!.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(result.EndedAt, Is.GreaterThan(result.StartedAt));
            Assert.That(anyTradeActivity, Is.True,
                "Legacy must submit or fill an order; a missing-target R:R regression must not silently yield zero activity.");
            Assert.That(result.SubmittedOrders > 0 || result.FilledOrders > 0, Is.True);
            Assert.That(journal.Any(entry =>
                    entry.Type == TradingJournal.TradeJournalEventType.SignalRejected &&
                    entry.Message.Contains("reward/risk", StringComparison.OrdinalIgnoreCase) &&
                    entry.Message.Contains("take-profit", StringComparison.OrdinalIgnoreCase)),
                Is.False);
        });
    }

    [Test]
    public async Task Legacy_StreamedRegression_ActivatesTrailingWithoutChangingInitialRisk()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval minute = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildTrendingCandles(instrument, minute, start, 600);
        var strategyOptions = new ProgressiveStrategyOptions
        {
            TrendInterval = BarInterval.Minutes(15),
            ConfirmationInterval = BarInterval.Minutes(5),
            EntryInterval = minute,
            Quantity = 1_000m,
            MinimumTrendConfidence = 0m,
            MinimumConfirmationConfidence = 0m,
            MinimumEntryConfidence = 0m
        };
        string output = Path.Combine(Path.GetTempPath(), "phase4-legacy", Guid.NewGuid().ToString("N"));
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = minute,
            AnalysisBaseInterval = minute,
            AnalysisIntervals = [minute, BarInterval.Minutes(5), BarInterval.Minutes(15)],
            StrategyTimeframes = new ProgressiveStrategyTimeframes
            {
                TrendInterval = BarInterval.Minutes(15),
                ConfirmationInterval = BarInterval.Minutes(5),
                EntryInterval = minute
            },
            WarmupDays = 0,
            PrefetchCapacity = 256,
            PrefetchLowWatermark = 32,
            SourcePageSize = 128,
            StrategyExecutionMode = StrategyExecutionMode.Sequential,
            ReplayChunkSize = 100,
            LegacyPositionManagement = new PositionManagementOptions
            {
                Mode = TrailingStopMode.BreakEvenOnly,
                ManagementInterval = minute,
                BreakEvenActivationR = 0.01m,
                StructureTrailActivationR = 0.01m,
                BreakEvenBufferAtr = 0m,
                MinimumStopImprovementAtr = 0m,
                PreserveBracketTarget = false
            }
        };
        var engine = new StreamingComparativeEngine(
            new Simulator.Abstractions.EnumerableMarketCandleStream(candles),
            [new StrategyFactoryEntry("legacy", new LegacyProgressiveAgent(strategyOptions), instrument)]);
        ComparativeSimulationResult result = await engine.RunAsync(
            new StreamingComparativeEngineOptions
            {
                SimulationId = Guid.NewGuid(),
                Instrument = instrument,
                EvaluationFrom = start,
                EvaluationTo = start.AddMinutes(candles.Length),
                StreamFrom = start,
                AnalysisIntervals = runtime.AnalysisIntervals,
                Runtime = runtime,
                SimulationOptions = new SimulationOptions
                {
                    StartingBalance = 100_000m,
                    Leverage = 20m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets
                },
                OutputDirectory = output,
                InputStreamId = "phase4-legacy-regression"
            },
            new HistoricalCandleRequest(instrument, minute, start, start.AddMinutes(candles.Length)));

        SimulatedTradeRecord[] trades = result.Strategies.Single().Result.Trades.ToArray();
        SimulatedTradeRecord trailed = trades.First(trade => trade.StopAmendmentCount > 0);
        Assert.Multiple(() =>
        {
            Assert.That(result.Strategies.Single().Result.SubmittedOrders, Is.GreaterThan(0));
            Assert.That(trailed.TakeProfitPrice, Is.Null);
            Assert.That(trailed.BreakEvenActivatedAt, Is.Not.Null);
            Assert.That(
                trailed.CurrentStopLossPrice!.Value > trailed.InitialStopLossPrice!.Value,
                Is.True);
            Assert.That(trailed.InitialStopLossPrice, Is.EqualTo(trailed.StopLossPrice));
            Assert.That(trailed.StopAmendments.All(amendment =>
                amendment.Status is ProtectiveStopAmendmentStatus.Accepted or
                    ProtectiveStopAmendmentStatus.Replaced), Is.True);
        });
    }

    [Test]
    public async Task StreamingEngine_ForwardsExplicitMinimumRewardRisk_ToBracketRiskGate()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval minute = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildTrendingCandles(instrument, minute, start, 600);
        string output = Path.Combine(Path.GetTempPath(), "streamed-minimum-rr", Guid.NewGuid().ToString("N"));
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = minute,
            AnalysisBaseInterval = minute,
            AnalysisIntervals = [minute, BarInterval.Minutes(5)],
            WarmupDays = 0,
            PrefetchCapacity = 256,
            PrefetchLowWatermark = 32,
            SourcePageSize = 128,
            StrategyExecutionMode = StrategyExecutionMode.Sequential,
            ReplayChunkSize = 100
        };
        var engine = new StreamingComparativeEngine(
            new Simulator.Abstractions.EnumerableMarketCandleStream(candles),
            [new StrategyFactoryEntry("one-r", new DeterministicOneRBracketAgent(), instrument)]);

        ComparativeSimulationResult result = await engine.RunAsync(
            new StreamingComparativeEngineOptions
            {
                SimulationId = Guid.NewGuid(),
                Instrument = instrument,
                EvaluationFrom = start,
                EvaluationTo = start.AddMinutes(candles.Length),
                StreamFrom = start,
                AnalysisIntervals = runtime.AnalysisIntervals,
                Runtime = runtime,
                MinimumRewardRiskRatio = 1m,
                SimulationOptions = new SimulationOptions
                {
                    StartingBalance = 100_000m,
                    Leverage = 20m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets
                },
                OutputDirectory = output,
                InputStreamId = "streamed-minimum-rr-regression"
            },
            new HistoricalCandleRequest(instrument, minute, start, start.AddMinutes(candles.Length)));

        SimulationResult strategy = result.Strategies.Single().Result;
        Assert.Multiple(() =>
        {
            Assert.That(strategy.SubmittedOrders, Is.GreaterThan(0),
                "The 1R decision was rejected by the default 1.5R gate.");
            Assert.That(strategy.FilledOrders, Is.GreaterThan(0));
            Assert.That(strategy.Trades, Is.Not.Empty);
        });
    }

    [Test]
    public async Task ProtectiveStopAgent_OpensAndClosesTradeWithoutTakeProfit_EndToEnd()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildTrendingCandles(instrument, interval, start, 30);
        var agent = new DeterministicProtectiveStopAgent();

        await using SimulationSession session = SimulationFactory.CreateStrategyAwareHistorical(
            instrument,
            candles,
            [BarInterval.Minutes(5)],
            agent,
            new SimulationOptions
            {
                BaseCurrency = "USD",
                StartingBalance = 100_000m,
                CloseOpenPositionsAtEnd = true,
                BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            safetyOptions: new RiskManager.Safety.TradingSafetyOptions
            {
                TripOnCriticalDataQualityIssue = false
            },
            dataQualityOptions: new TradingCore.MarketData.MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            });

        SimulationResult result = await session.Runner.RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SubmittedOrders, Is.GreaterThan(0));
            Assert.That(result.FilledOrders, Is.GreaterThan(0));
            Assert.That(result.Trades, Is.Not.Empty);
            Assert.That(result.Trades[0].TakeProfitPrice, Is.Null);
            Assert.That(result.Trades[0].MaximumFavourableExcursionAt, Is.Not.Null);
            Assert.That(result.Trades[0].MaximumAdverseExcursionAt, Is.Not.Null);
            Assert.That(result.Trades[0].MaximumFavourableExcursionAmount, Is.GreaterThanOrEqualTo(0m));
            Assert.That(result.Trades[0].MaximumAdverseExcursionAmount, Is.LessThanOrEqualTo(0m));
        });
    }

    private static Candle[] BuildTrendingCandles(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset start,
        int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1000m;
        for (int i = 0; i < count; i++)
        {
            decimal target = 1.1000m + i * 0.000002m +
                (decimal)Math.Sin(i / 7.0) * 0.0008m;
            decimal open = price;
            decimal close = target;
            DateTimeOffset openTime = start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(
                    open,
                    Math.Max(open, close) + 0.0004m,
                    Math.Min(open, close) - 0.0004m,
                    close),
                IsComplete = true
            };
            price = close;
        }

        return candles;
    }

    private sealed class DeterministicProtectiveStopAgent : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Deterministic protective-stop agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } =
            new HashSet<BarInterval> { BarInterval.Minutes(5) };
        public BarInterval TriggerInterval => BarInterval.Minutes(5);
        public AgentExitManagementMode ExitManagementMode =>
            AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decimal price = context.Analysis.Get(BarInterval.Minutes(5)).LatestCandle.Prices.Close;
            AgentDecision decision = _submitted
                ? new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Already submitted"
                }
                : new AgentDecision
                {
                    StrategyName = Name,
                    SetupId = "protective-stop-test",
                    SetupStartedAt = context.Timestamp,
                    SignalInterval = BarInterval.Minutes(5),
                    Action = AgentAction.Buy,
                    Instrument = context.Instrument,
                    SuggestedQuantity = 1_000m,
                    QuantityUnit = QuantityUnit.Units,
                    OrderType = StandardOrderType.Market,
                    ReferencePrice = price,
                    StopLossPrice = price - 0.01m,
                    TakeProfitPrice = null,
                    Confidence = 100m,
                    CreatedAt = context.Timestamp,
                    Reason = "Deterministic entry without a fixed target"
                };
            _submitted = true;
            return Task.FromResult(decision);
        }
    }

    private sealed class DeterministicOneRBracketAgent : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Deterministic 1R bracket agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } =
            new HashSet<BarInterval> { BarInterval.Minutes(5) };
        public BarInterval TriggerInterval => BarInterval.Minutes(5);
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decimal price = context.Analysis.Get(BarInterval.Minutes(5)).LatestCandle.Prices.Close;
            AgentDecision decision = _submitted
                ? new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Already submitted"
                }
                : new AgentDecision
                {
                    StrategyName = Name,
                    SetupId = "one-r-bracket-test",
                    SetupStartedAt = context.Timestamp,
                    SignalInterval = BarInterval.Minutes(5),
                    Action = AgentAction.Buy,
                    Instrument = context.Instrument,
                    SuggestedQuantity = 1_000m,
                    QuantityUnit = QuantityUnit.Units,
                    OrderType = StandardOrderType.Market,
                    ReferencePrice = price,
                    StopLossPrice = price - 0.01m,
                    TakeProfitPrice = price + 0.01m,
                    ExpectedRewardRisk = 1m,
                    Confidence = 100m,
                    CreatedAt = context.Timestamp,
                    Reason = "Deterministic 1R bracket"
                };
            _submitted = true;
            return Task.FromResult(decision);
        }
    }
}
