using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ExecutionManager;
using RiskManager.Safety;
using System.Security.Cryptography;
using System.Text;
using Simulator.Broker;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Time;
using TradingCore.MarketData;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class Phase4TrailingStopTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start =
        new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [TestCase(100.99, TradeManagementAction.Hold)]
    [TestCase(101.00, TradeManagementAction.MoveStop)]
    public void LongBreakEven_ActivatesOnlyAtConfiguredR(
        decimal executablePrice,
        TradeManagementAction expected)
    {
        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Buy, executablePrice),
            Analysis(10, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(expected));
            Assert.That(result.OpenProfitR, Is.EqualTo(executablePrice - 100m));
            if (expected == TradeManagementAction.MoveStop)
            {
                Assert.That(result.ProposedStopPrice, Is.GreaterThan(100m));
                Assert.That(result.AmendmentReason, Is.EqualTo(StopAmendmentReason.BreakEven));
            }
        });
    }

    [TestCase(99.01, TradeManagementAction.Hold)]
    [TestCase(99.00, TradeManagementAction.MoveStop)]
    public void ShortBreakEven_ActivatesSymmetrically(
        decimal executablePrice,
        TradeManagementAction expected)
    {
        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Sell, executablePrice),
            Analysis(10, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(expected));
            Assert.That(result.OpenProfitR, Is.EqualTo(100m - executablePrice));
            if (expected == TradeManagementAction.MoveStop)
                Assert.That(result.ProposedStopPrice, Is.LessThan(100m));
        });
    }

    [Test]
    public void BreakEven_IncludesEntryExitSpreadSlippageAndAtrBuffer()
    {
        ManagedTradeState trade = Trade(OrderSide.Buy, 102m) with
        {
            EntryCommissionPrice = 0.1m,
            ExpectedExitCommissionPrice = 0.1m,
            SpreadPrice = 0.2m,
            SlippagePrice = 0.3m
        };
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.BreakEvenOnly,
            BreakEvenBufferAtr = 0.05m,
            MinimumStopImprovementAtr = 0m
        });

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis(10, atr: 2m));

        Assert.Multiple(() =>
        {
            Assert.That(result.CostAdjustedBreakEvenPrice, Is.EqualTo(101m));
            Assert.That(result.AtrBufferPrice, Is.EqualTo(0.1m));
            Assert.That(result.ProposedStopPrice, Is.EqualTo(101.1m));
        });
    }

    [Test]
    public void LongStructureTrail_UsesNearestProtectiveConfirmedSwingOverZone()
    {
        AnalysisSnapshot analysis = Analysis(20, atr: 1m) with
        {
            Swings = [Swing(102.8m, SwingType.Low, confirmed: true)],
            PriceZones = [Zone(102.5m, 103m, PriceZoneType.Support)]
        };

        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Buy, 104m) with { InitialStopPrice = 98m },
            analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(result.ProposedStopPrice, Is.EqualTo(102.55m));
            Assert.That(result.StructuralLevel, Is.EqualTo(102.8m));
            Assert.That(result.AmendmentReason, Is.EqualTo(StopAmendmentReason.StructureSwing));
        });
    }

    [Test]
    public void ShortStructureTrail_UsesConfirmedSwingAndAtrBuffer()
    {
        AnalysisSnapshot analysis = Analysis(21, atr: 1m) with
        {
            Swings = [Swing(97.2m, SwingType.High, confirmed: true)],
            PriceZones = [Zone(97m, 97.6m, PriceZoneType.Resistance)]
        };

        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Sell, 96m) with { InitialStopPrice = 102m },
            analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(result.ProposedStopPrice, Is.EqualTo(97.45m));
            Assert.That(result.ProposedStopPrice, Is.GreaterThan(96m));
            Assert.That(result.ProposedStopPrice, Is.LessThan(100m));
        });
    }

    [Test]
    public void UnconfirmedSwing_IsIgnoredAndFallsBackToBreakEven()
    {
        AnalysisSnapshot analysis = Analysis(22, atr: 1m) with
        {
            Swings = [Swing(102.8m, SwingType.Low, confirmed: false)]
        };

        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Buy, 104m) with { InitialStopPrice = 98m },
            analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.AmendmentReason, Is.EqualTo(StopAmendmentReason.BreakEven));
            Assert.That(result.StructureSource, Is.EqualTo("Cost-adjusted break-even"));
        });
    }

    [Test]
    public void InitialRisk_RemainsTheRDenominatorAfterCurrentStopMoves()
    {
        TradeManagementRecommendation result = Manager().Evaluate(
            Trade(OrderSide.Buy, 102m) with
            {
                InitialStopPrice = 98m,
                CurrentStopPrice = 100.5m
            },
            Analysis(30, atr: 0.5m));

        Assert.That(result.OpenProfitR, Is.EqualTo(1m));
    }

    [Test]
    public void CooldownStaleSnapshotAndMinimumImprovement_PreventChurn()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.BreakEvenOnly,
            MinimumAnalysisBarsBetweenAmendments = 2,
            MinimumStopImprovementAtr = 0.1m,
            BreakEvenBufferAtr = 0m
        });
        AnalysisSnapshot analysis = Analysis(40, atr: 1m);

        TradeManagementRecommendation cooldown = manager.Evaluate(
            Trade(OrderSide.Buy, 102m) with { AnalysisBarsSinceLastAmendment = 1 }, analysis);
        TradeManagementRecommendation stale = manager.Evaluate(
            Trade(OrderSide.Buy, 102m) with { LastAmendmentSnapshotVersion = 40 }, analysis);
        TradeManagementRecommendation tooSmall = manager.Evaluate(
            Trade(OrderSide.Buy, 102m) with { CurrentStopPrice = 99.95m }, analysis);

        Assert.Multiple(() =>
        {
            Assert.That(cooldown.Action, Is.EqualTo(TradeManagementAction.Hold));
            Assert.That(stale.Action, Is.EqualTo(TradeManagementAction.Hold));
            Assert.That(tooSmall.Action, Is.EqualTo(TradeManagementAction.Hold));
        });
    }

    [TestCase(true, TradeManagementAction.Exit)]
    [TestCase(false, TradeManagementAction.MoveStop)]
    public void AdverseStructureExit_IsExplicitlyConfigurable(bool enabled, TradeManagementAction expected)
    {
        AnalysisSnapshot analysis = Analysis(50, atr: 0.5m) with
        {
            MarketStructure = new MarketStructureSnapshot { Break = MarketStructureBreak.Bearish }
        };
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.BreakEvenOnly,
            ExitOnAdverseStructureBreak = enabled,
            MinimumStopImprovementAtr = 0m
        });

        Assert.That(manager.Evaluate(Trade(OrderSide.Buy, 102m), analysis).Action, Is.EqualTo(expected));
    }

    [TestCase("1m")]
    [TestCase("5s")]
    [TestCase("1s")]
    public async Task AcceptedLongAmendment_IsNotAppliedRetroactivelyAndClosesOnNextCandle(string intervalText)
    {
        BarInterval interval = BarIntervalParser.Parse(intervalText);
        await using OpenTrade fixture = await OpenTrade.CreateAsync(OrderSide.Buy, interval);
        AmendProtectiveStopRequest request = fixture.Amendment(101m, "long-next-frame");

        ProtectiveStopAmendmentResult result = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Replaced));
            Assert.That(fixture.Broker.Positions.GetOpenPositionsAsync().Result, Has.Count.EqualTo(1));
        });

        await fixture.NextAsync(104m, 105m, 100m, 104m);
        Assert.That(await fixture.Broker.Positions.GetOpenPositionsAsync(), Is.Empty);
    }

    [Test]
    public async Task AcceptedShortAmendment_IsDirectionalAndNextFrameOnly()
    {
        await using OpenTrade fixture = await OpenTrade.CreateAsync(OrderSide.Sell, BarInterval.Seconds(5));
        ProtectiveStopAmendmentResult result = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(fixture.Amendment(99m, "short-next-frame"));

        Assert.That(result.AcceptedStopPrice, Is.EqualTo(99m));
        Assert.That(await fixture.Broker.Positions.GetOpenPositionsAsync(), Has.Count.EqualTo(1));
        await fixture.NextAsync(96m, 100m, 95m, 96m);
        Assert.That(await fixture.Broker.Positions.GetOpenPositionsAsync(), Is.Empty);
    }

    [TestCase(OrderSide.Buy)]
    [TestCase(OrderSide.Sell)]
    public async Task AmendedStop_RemainsInOcoAndTargetCancelsIt(OrderSide side)
    {
        await using OpenTrade fixture = await OpenTrade.CreateAsync(side, BarInterval.Minutes(1));
        decimal newStop = side == OrderSide.Buy ? 101m : 99m;
        await fixture.Broker.ProtectiveOrders.AmendProtectiveStopAsync(
            fixture.Amendment(newStop, $"oco-{side}"));

        if (side == OrderSide.Buy)
            await fixture.NextAsync(104m, 111m, 103m, 110m);
        else
            await fixture.NextAsync(96m, 97m, 89m, 90m);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Broker.Positions.GetOpenPositionsAsync().Result, Is.Empty);
            Assert.That(fixture.Broker.Orders.GetOpenOrdersAsync().Result, Is.Empty);
        });
    }

    [Test]
    public async Task RejectedWideningPreservesOldStopAndDuplicateIsIdempotent()
    {
        await using OpenTrade fixture = await OpenTrade.CreateAsync(OrderSide.Buy, BarInterval.Minutes(1));
        AmendProtectiveStopRequest widening = fixture.Amendment(94m, "reject-widening");
        ProtectiveStopAmendmentResult rejected = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(widening);
        BrokerOrder retained = (await fixture.Broker.Orders.GetOpenOrdersAsync())
            .Single(order => order.Type == StandardOrderType.Stop.ToString());

        AmendProtectiveStopRequest valid = fixture.Amendment(101m, "duplicate-stable");
        ProtectiveStopAmendmentResult first = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(valid);
        ProtectiveStopAmendmentResult duplicate = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(valid);
        ProtectiveStopAmendmentResult conflict = await fixture.Broker.ProtectiveOrders
            .AmendProtectiveStopAsync(valid with { NewStopPrice = 102m });

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Rejected));
            Assert.That(retained.BrokerOrderId, Is.EqualTo(fixture.Stop.BrokerOrderId));
            Assert.That(duplicate.CurrentStopOrderId, Is.EqualTo(first.CurrentStopOrderId));
            Assert.That(conflict.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Rejected));
            Assert.That(conflict.RejectionReason, Does.Contain("different payload"));
        });
    }

    [Test]
    public async Task EntrySafetyLock_DoesNotBlockRiskReducingAmendment()
    {
        await using OpenTrade fixture = await OpenTrade.CreateAsync(OrderSide.Buy, BarInterval.Minutes(1));
        var safety = new TradingSafetyController();
        safety.Trip(SafetyTripReason.Manual, "No new entries", fixture.Clock.UtcNow);
        var coordinator = new ExecutionCoordinator(safety: safety);
        ProtectiveStopAmendmentCommand command = fixture.Command(101.009m, "safety-amendment");

        ProtectiveStopAmendmentResult result = await coordinator.AmendProtectiveStopAsync(
            command,
            fixture.Broker);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Replaced));
            Assert.That(result.AcceptedStopPrice, Is.EqualTo(101m));
            Assert.That(safety.Snapshot.CanOpenNewTrades, Is.False);
        });
    }

    [Test]
    public async Task StreamedStrategySession_MechanicalProtectionDoesNotWaitForFiveOrFifteenMinuteClose()
    {
        BarInterval execution = BarInterval.Minutes(1);
        var agent = new BuyOrSellOnceAgent(OrderSide.Buy, execution);
        await using StrategySimulationSession session = StrategySimulationSession.Create(
            "execution-frame-protection",
            agent,
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = false
            },
            dataQualityOptions: new MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.BreakEvenOnly,
                EvaluateMechanicalProtectionOnEveryExecutionFrame = true,
                FastStructureInterval = BarInterval.Minutes(5),
                MainStructureInterval = BarInterval.Minutes(15),
                ThesisInterval = BarInterval.Hours(1),
                BreakEvenActivationR = 1m,
                StructureTrailActivationR = 1m,
                BreakEvenBufferAtr = 0m,
                MinimumStopImprovementAtr = 0m
            });

        await session.ProcessFrameAsync(SessionFrame(1, execution, 100m, 101m, 99m, 100m));
        StrategyFrameResult protectedFrame = await session.ProcessFrameAsync(
            SessionFrame(2, execution, 100m, 103m, 99m, 102m));

        Assert.Multiple(() =>
        {
            Assert.That(session.ActiveTrade, Is.Not.Null);
            Assert.That(session.ActiveTrade!.StopAmendmentCount, Is.EqualTo(1));
            Assert.That(session.ActiveTrade.CurrentStopLossPrice, Is.GreaterThanOrEqualTo(100m));
            Assert.That(protectedFrame.Events.Any(item =>
                item.Type == StrategyReplayEventType.TradeManagementEvaluated &&
                item.ReasonCode is not null &&
                item.ReasonCode.StartsWith("Mechanical:", StringComparison.Ordinal)), Is.True);
            Assert.That(protectedFrame.Events.Select(item => item.Type),
                Does.Contain(StrategyReplayEventType.StopAmendmentAccepted));
        });
    }

    [TestCase(OrderSide.Buy)]
    [TestCase(OrderSide.Sell)]
    public async Task StreamedStrategySession_AmendsAfterFrameAndClassifiesBreakEvenExit(OrderSide side)
    {
        BarInterval interval = BarInterval.Minutes(1);
        var agent = new BuyOrSellOnceAgent(side, interval);
        await using StrategySimulationSession session = StrategySimulationSession.Create(
            "phase4-streamed",
            agent,
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = false
            },
            dataQualityOptions: new MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.BreakEvenOnly,
                ManagementInterval = interval,
                BreakEvenActivationR = 1m,
                StructureTrailActivationR = 1m,
                BreakEvenBufferAtr = 0.05m,
                MinimumStopImprovementAtr = 0m
            });

        await session.ProcessFrameAsync(SessionFrame(1, interval, 100m, 101m, 99m, 100m));
        StrategyFrameResult amended = side == OrderSide.Buy
            ? await session.ProcessFrameAsync(SessionFrame(2, interval, 100m, 103m, 99m, 102m))
            : await session.ProcessFrameAsync(SessionFrame(2, interval, 100m, 101m, 97m, 98m));

        Assert.Multiple(() =>
        {
            Assert.That(session.ActiveTrade, Is.Not.Null);
            Assert.That(session.ActiveTrade!.StopAmendmentCount, Is.EqualTo(1));
            Assert.That(amended.Events.Select(item => item.Type),
                Does.Contain(StrategyReplayEventType.StopAmendmentAccepted));
            Assert.That(session.ActiveTrade.CurrentStopLossPrice,
                side == OrderSide.Buy ? Is.GreaterThan(100m) : Is.LessThan(100m));
        });

        // Frame 2 crossed the newly proposed break-even level, but it remained open
        // because the amendment only became effective at sequence 3.
        StrategyFrameResult closed = side == OrderSide.Buy
            ? await session.ProcessFrameAsync(SessionFrame(3, interval, 102m, 103m, 100m, 101m))
            : await session.ProcessFrameAsync(SessionFrame(3, interval, 98m, 100m, 97m, 99m));

        Assert.Multiple(() =>
        {
            Assert.That(closed.NewlyCompletedTrade, Is.Not.Null);
            Assert.That(closed.NewlyCompletedTrade!.ExitReason,
                Is.EqualTo(SimulatedTradeExitReason.BreakEvenStop));
            Assert.That(closed.NewlyCompletedTrade.InitialStopLossPrice,
                Is.EqualTo(side == OrderSide.Buy ? 98m : 102m));
            Assert.That(closed.NewlyCompletedTrade.StopAmendments, Has.Count.EqualTo(1));
            Assert.That(closed.Events.Select(item => item.Type),
                Does.Contain(StrategyReplayEventType.StopHit));
        });
    }

    [Test]
    public async Task SequentialAndTaskWorker_ProduceSameLifecycleAndStopHistoryFingerprint()
    {
        BarInterval interval = BarInterval.Minutes(1);
        MarketFrame[] frames =
        [
            SessionFrame(1, interval, 100m, 101m, 99m, 100m),
            SessionFrame(2, interval, 100m, 103m, 99m, 102m),
            SessionFrame(3, interval, 102m, 103m, 100m, 101m)
        ];
        await using StrategySimulationSession sequential = CreateTestSession(OrderSide.Buy, interval);
        foreach (MarketFrame frame in frames)
            await sequential.ProcessFrameAsync(frame);

        StrategySimulationSession taskSession = CreateTestSession(OrderSide.Buy, interval);
        await using var worker = new StrategyWorkerHost(taskSession, channelCapacity: 2);
        Task<StrategyFrameResult[]> completion = await worker.EnqueueBatchAsync(frames);
        await completion;

        Assert.Multiple(() =>
        {
            Assert.That(sequential.Trades, Has.Count.EqualTo(1));
            Assert.That(taskSession.Trades, Has.Count.EqualTo(1));
            Assert.That(Fingerprint(taskSession.Trades), Is.EqualTo(Fingerprint(sequential.Trades)));
            Assert.That(taskSession.Trades[0].StopAmendments, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void LegacyDefaults_ScaleOutOnceAndPreserveConfiguredRunner()
    {
        var manager = new StructureBasedTradeManager(PositionManagementOptions.LegacyDefaults);
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m,
            MaximumFavourableExcursionR = 1m
        };

        TradeManagementRecommendation first = manager.Evaluate(trade, Analysis(60, atr: 0.2m));
        TradeManagementRecommendation duplicate = manager.Evaluate(
            trade with
            {
                CurrentQuantity = 80m,
                CompletedReductionStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "scale-1r"
                }
            },
            Analysis(61, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(TradeManagementAction.ReduceAndMoveStop));
            Assert.That(first.PositionReduction, Is.Not.Null);
            Assert.That(first.PositionReduction!.StageId, Is.EqualTo("scale-1r"));
            Assert.That(first.PositionReduction.QuantityToClose, Is.EqualTo(20m));
            Assert.That(first.PositionReduction.QuantityRemainingAfterReduction, Is.EqualTo(80m));
            Assert.That(duplicate.PositionReduction, Is.Null);
        });
    }

    [Test]
    public void MaximumGiveback_ProducesRatchetThenExitAfterFloorIsBreached()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableMaximumGiveback = true,
            MaximumGivebackRules =
            [
                new ProfitGivebackRule { ActivationR = 3m, MaximumGivebackR = 0.5m }
            ],
            MinimumStopImprovementAtr = 0m
        });
        ManagedTradeState trade = Trade(OrderSide.Buy, 102.6m) with
        {
            MaximumFavourableExcursionR = 3m
        };

        TradeManagementRecommendation ratchet = manager.Evaluate(trade, Analysis(70, atr: 0.2m));
        TradeManagementRecommendation breached = manager.Evaluate(
            trade with { CurrentPrice = 102.4m },
            Analysis(71, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(ratchet.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(ratchet.ProposedStopPrice, Is.EqualTo(102.5m));
            Assert.That(ratchet.AmendmentReason, Is.EqualTo(StopAmendmentReason.MfeGiveback));
            Assert.That(breached.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(breached.ExitReason, Is.EqualTo(TradeManagementExitReason.MaximumGivebackBreached));
        });
    }

    [Test]
    public void Stagnation_ReducesOnlyOnceAndNeverConsumesRunner()
    {
        var options = new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStagnationReduction = true,
            StagnationMinimumOpenProfitR = 0.5m,
            StagnationBars = 3,
            StagnationReductionFraction = 0.2m,
            MinimumRunnerFraction = 0.5m
        };
        var manager = new StructureBasedTradeManager(options);
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 55m,
            MinimumQuantityIncrement = 1m,
            AnalysisBarsWithoutNewMfe = 3
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, Analysis(80, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.PositionReduction, Is.Not.Null);
            Assert.That(result.PositionReduction!.QuantityToClose, Is.EqualTo(5m));
            Assert.That(result.PositionReduction.QuantityRemainingAfterReduction, Is.EqualTo(50m));
        });
    }

    [Test]
    public void MomentumDecay_RequiresTwoConfirmationsIncludingDivergenceOrPriceAction()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableMomentumDecayReduction = true,
            MomentumDecayMinimumOpenProfitR = 1m,
            MomentumDecayReductionFraction = 0.2m,
            MinimumRunnerFraction = 0.5m
        });
        AnalysisSnapshot analysis = Analysis(90, atr: 0.5m) with
        {
            Indicators = new IndicatorSnapshot
            {
                Atr = 0.5m,
                RsiAnalysis = new RsiAnalysisSnapshot
                {
                    MomentumDirection = MomentumDirection.Falling,
                    LatestRelationship = new RsiRelationshipSnapshot
                    {
                        Type = RsiRelationshipType.RegularBearishDivergence,
                        FirstPivotTime = Start.AddMinutes(-15),
                        SecondPivotTime = Start.AddMinutes(-5),
                        ConfirmedAt = Start,
                        FirstPrice = 101m,
                        SecondPrice = 102m,
                        FirstRsi = 70m,
                        SecondRsi = 65m,
                        PriceChange = 1m,
                        RsiChange = -5m,
                        Strength = 80m,
                        AgeCandles = 1
                    }
                },
                AdxAnalysis = new AdxAnalysisSnapshot
                {
                    StrengthDirection = MomentumDirection.Falling,
                    IsTrendStrengthening = false
                }
            }
        };
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
            Assert.That(result.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.MomentumDecay));
            Assert.That(result.PositionReduction?.QuantityToClose, Is.EqualTo(20m));
        });
    }

    [Test]
    public void VolatilityExhaustion_RequiresMiddleBandLossContractionAndTrendWeakening()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableVolatilityExhaustionReduction = true,
            VolatilityExhaustionMinimumOpenProfitR = 1m,
            VolatilityExhaustionReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.5m
        });
        AnalysisSnapshot analysis = Analysis(91, atr: 0.5m) with
        {
            LatestCandle = TestCandles.Create(
                Instrument, Start, BarInterval.Minutes(5), 102m, 102.5m, 100.5m, 100.8m),
            Indicators = new IndicatorSnapshot
            {
                Atr = 0.5m,
                BollingerMiddle = 101m,
                BollingerAnalysis = new BollingerAnalysisSnapshot
                {
                    WidthDirection = VolatilityDirection.Contracting,
                    WidthRegime = BollingerWidthRegime.Wide,
                    BandwidthChangePercent = -10m
                },
                AdxAnalysis = new AdxAnalysisSnapshot
                {
                    StrengthDirection = MomentumDirection.Falling,
                    IsTrendStrengthening = false
                }
            }
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 101.5m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m,
            VolatilityExpansionSeenSinceEntry = true
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);
        TradeManagementRecommendation withoutPriorExpansion = manager.Evaluate(
            trade with { VolatilityExpansionSeenSinceEntry = false },
            analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.PositionReduction?.Reason,
                Is.EqualTo(PositionReductionReason.VolatilityExhaustion));
            Assert.That(result.PositionReduction?.QuantityToClose, Is.EqualTo(15m));
            Assert.That(withoutPriorExpansion.PositionReduction, Is.Null,
                "Contraction alone must not be treated as post-expansion exhaustion.");
        });
    }

    [Test]
    public void ProfitFloor_RatchetsStopAndExitsOnlyAfterLockedFloorIsBreached()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableProfitFloor = true,
            ProfitFloorRules =
            [
                new ProfitFloorRule { ActivationR = 2m, LockedProfitR = 0.75m }
            ],
            MinimumStopImprovementAtr = 0m
        });
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            MaximumFavourableExcursionR = 2.2m
        };

        TradeManagementRecommendation ratchet = manager.Evaluate(
            trade,
            Analysis(94, atr: 0.2m));
        TradeManagementRecommendation breached = manager.Evaluate(
            trade with { CurrentPrice = 100.70m },
            Analysis(95, atr: 0.2m));

        Assert.Multiple(() =>
        {
            Assert.That(ratchet.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(ratchet.ProposedStopPrice, Is.EqualTo(100.75m));
            Assert.That(ratchet.AmendmentReason, Is.EqualTo(StopAmendmentReason.ProfitFloor));
            Assert.That(breached.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(breached.ExitReason, Is.EqualTo(TradeManagementExitReason.ProfitFloorBreached));
        });
    }

    [Test]
    public void StructuralDeterioration_ReducesOnceAfterAdverseConfirmedBreak()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableStructuralDeteriorationReduction = true,
            StructuralDeteriorationReductionFraction = 0.20m,
            MaximumStructuralDeteriorationReductions = 1,
            MinimumRunnerFraction = 0.5m
        });
        AnalysisSnapshot analysis = Analysis(96, atr: 0.5m) with
        {
            MarketStructure = new MarketStructureSnapshot
            {
                Break = MarketStructureBreak.Bearish
            }
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation first = manager.Evaluate(trade, analysis);
        TradeManagementRecommendation second = manager.Evaluate(
            trade with { StructuralDeteriorationReductionCount = 1 },
            analysis with { Version = 97 });

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(first.PositionReduction?.Reason,
                Is.EqualTo(PositionReductionReason.StructuralDeterioration));
            Assert.That(first.PositionReduction?.QuantityToClose, Is.EqualTo(20m));
            Assert.That(second.PositionReduction, Is.Null);
        });
    }

    [Test]
    public void OpposingStructure_CanActivateScaleOutBeforeRThreshold()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableScaleOut = true,
            ScaleOutRules =
            [
                new ScaleOutRule
                {
                    StageId = "opposing-zone",
                    ActivationR = 2m,
                    MinimumOpenProfitR = 0.5m,
                    FractionOfInitialQuantity = 0.25m,
                    TriggerMode = ScaleOutTriggerMode.OpposingStructure
                }
            ],
            OpposingStructureProximityAtr = 0.5m,
            MinimumRunnerFraction = 0.5m
        });
        AnalysisSnapshot analysis = Analysis(98, atr: 1m) with
        {
            PriceZones =
            [
                Zone(100.8m, 101.2m, PriceZoneType.Resistance)
            ]
        };
        ManagedTradeState trade = Trade(OrderSide.Buy, 100.8m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m
        };

        TradeManagementRecommendation result = manager.Evaluate(trade, analysis);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.ReducePosition));
            Assert.That(result.PositionReduction?.Reason,
                Is.EqualTo(PositionReductionReason.OpposingStructure));
            Assert.That(result.PositionReduction?.QuantityToClose, Is.EqualTo(25m));
            Assert.That(result.PositionReduction?.StructureSource, Does.Contain("resistance"));
        });
    }

    [Test]
    public void ExecutionCostStress_ReducesOnlyWhenSpreadIsLargeRelativeToAtr()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableExecutionCostStressReduction = true,
            MaximumSpreadToAtrRatio = 0.20m,
            ExecutionCostStressMinimumOpenProfitR = 0.5m,
            ExecutionCostStressReductionFraction = 0.15m,
            MinimumRunnerFraction = 0.5m
        });
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m,
            SpreadPrice = 0.11m
        };

        TradeManagementRecommendation stressed = manager.Evaluate(
            trade,
            Analysis(99, atr: 0.5m));
        TradeManagementRecommendation normal = manager.Evaluate(
            trade with { SpreadPrice = 0.05m },
            Analysis(100, atr: 0.5m));

        Assert.Multiple(() =>
        {
            Assert.That(stressed.PositionReduction?.Reason,
                Is.EqualTo(PositionReductionReason.ExecutionCostStress));
            Assert.That(stressed.PositionReduction?.QuantityToClose, Is.EqualTo(15m));
            Assert.That(normal.PositionReduction, Is.Null);
        });
    }

    [Test]
    public void UtcRiskWindow_CanCrossMidnightAndRunsOnlyOnce()
    {
        var manager = new StructureBasedTradeManager(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            EnableRiskWindowReduction = true,
            RiskWindowStartUtc = new TimeOnly(21, 45),
            RiskWindowEndUtc = new TimeOnly(22, 15),
            RiskWindowMinimumOpenProfitR = 0.5m,
            RiskWindowReductionFraction = 0.25m,
            MinimumRunnerFraction = 0.5m
        });
        ManagedTradeState trade = Trade(OrderSide.Buy, 101m) with
        {
            InitialQuantity = 100m,
            CurrentQuantity = 100m,
            MinimumQuantityIncrement = 1m,
            EvaluatedAt = new DateTimeOffset(2026, 6, 1, 22, 0, 0, TimeSpan.Zero)
        };

        TradeManagementRecommendation first = manager.Evaluate(trade, Analysis(92, atr: 0.5m));
        TradeManagementRecommendation second = manager.Evaluate(
            trade with { RiskWindowReductionCompleted = true },
            Analysis(93, atr: 0.5m));

        Assert.Multiple(() =>
        {
            Assert.That(first.PositionReduction?.Reason, Is.EqualTo(PositionReductionReason.SessionRisk));
            Assert.That(first.PositionReduction?.QuantityToClose, Is.EqualTo(25m));
            Assert.That(second.PositionReduction, Is.Null);
        });
    }

    [Test]
    public async Task PartialClose_KeepsAndResizesProtectiveOrders_ThenCannotReversePosition()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = new SimulatedBrokerClient(new SimulationOptions
        {
            StartingBalance = 100_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        }, clock);
        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            StopLoss = new StopLossInstruction(95m),
            TakeProfit = new TakeProfitInstruction(110m),
            ClientOrderId = "partial-protected-entry"
        });
        clock.AdvanceTo(Start.AddMinutes(1));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start, BarInterval.Minutes(1), 100m, 101m, 99m, 100m));

        var coordinator = new ExecutionCoordinator();
        OrderSubmission? submitted = await coordinator.ProcessAsync(new AgentDecision
        {
            DecisionId = "partial-close-25",
            SetupId = "partial-protected",
            StrategyName = "test",
            Action = AgentAction.Close,
            Instrument = Instrument,
            SuggestedQuantity = 25m,
            QuantityUnit = QuantityUnit.Units,
            ReferencePrice = 102m,
            Confidence = 100m,
            CreatedAt = clock.UtcNow,
            Reason = "Test staged reduction"
        }, broker);

        Assert.That(submitted?.Status, Is.Not.EqualTo(SubmissionStatus.Rejected));
        Assert.That(await broker.Orders.GetOpenOrdersAsync(), Has.Count.EqualTo(3));

        clock.AdvanceTo(Start.AddMinutes(2));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start.AddMinutes(1), BarInterval.Minutes(1), 102m, 103m, 101m, 102m));
        BrokerPosition remaining = (await broker.Positions.GetOpenPositionsAsync()).Single();
        BrokerOrder[] protective = (await broker.Orders.GetOpenOrdersAsync())
            .Where(order => order.Type is "Stop" or "Limit")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(remaining.Quantity, Is.EqualTo(75m));
            Assert.That(protective, Has.Length.EqualTo(2));
            Assert.That(protective.All(order => order.Quantity == 75m), Is.True);
        });

        clock.AdvanceTo(Start.AddMinutes(3));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start.AddMinutes(2), BarInterval.Minutes(1), 102m, 111m, 101m, 110m));
        IReadOnlyList<BrokerPosition> finalPositions = await broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> finalOrders = await broker.Orders.GetOpenOrdersAsync();
        Assert.Multiple(() =>
        {
            Assert.That(finalPositions, Is.Empty);
            Assert.That(finalOrders, Is.Empty);
        });
    }

    [Test]
    public async Task TwoReduceOnlyCloses_SubmittedBeforeOneCandle_CannotReversePosition()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = new SimulatedBrokerClient(new SimulationOptions
        {
            StartingBalance = 100_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        }, clock);

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            ClientOrderId = "reduce-only-entry"
        });
        clock.AdvanceTo(Start.AddMinutes(1));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start, BarInterval.Minutes(1), 100m, 101m, 99m, 100m));

        // Simulate two independently accepted close requests becoming eligible on the same
        // execution candle. The second request is stale after the first reduction and must be
        // clamped to the remaining position rather than opening a short position.
        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Sell,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(25m, QuantityUnit.Units),
            ClientOrderId = "reduce-only-first",
            ReduceOnly = true
        });
        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Sell,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            ClientOrderId = "reduce-only-stale-full-close",
            ReduceOnly = true
        });

        clock.AdvanceTo(Start.AddMinutes(2));
        await broker.Runtime.ProcessExecutionCandleAsync(
            TestCandles.Create(Instrument, Start.AddMinutes(1), BarInterval.Minutes(1), 102m, 103m, 101m, 102m));

        IReadOnlyList<BrokerPosition> finalPositions = await broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> finalOrders = await broker.Orders.GetOpenOrdersAsync();
        Assert.Multiple(() =>
        {
            Assert.That(finalPositions, Is.Empty);
            Assert.That(finalOrders.Where(order =>
                order.ClientOrderId is "reduce-only-first" or "reduce-only-stale-full-close"),
                Is.Empty);
        });
    }

    private static StructureBasedTradeManager Manager() => new(new PositionManagementOptions
    {
        Mode = TrailingStopMode.StructureAtr,
        BreakEvenActivationR = 1m,
        StructureTrailActivationR = 1.5m,
        AtrBufferMultiplier = 0.25m,
        BreakEvenBufferAtr = 0.05m,
        MinimumStopImprovementAtr = 0m
    });

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

    private static SwingPoint Swing(decimal price, SwingType type, bool confirmed) => new()
    {
        PivotTime = Start.AddMinutes(-10),
        ConfirmedAt = confirmed ? Start : Start.AddMinutes(10),
        Price = price,
        Type = type,
        Strength = 3
    };

    private static PriceZone Zone(decimal lower, decimal upper, PriceZoneType type) => new()
    {
        LowerPrice = lower,
        UpperPrice = upper,
        CentrePrice = (lower + upper) / 2m,
        Type = type,
        TouchCount = 3,
        Strength = 80m
    };

    private static MarketFrame SessionFrame(
        long sequence,
        BarInterval interval,
        decimal open,
        decimal high,
        decimal low,
        decimal close)
    {
        DateTimeOffset openTime = Start.AddMinutes(sequence - 1);
        Candle candle = TestCandles.Create(Instrument, openTime, interval, open, high, low, close);
        AnalysisSnapshot snapshot = Analysis(sequence, atr: 1m) with
        {
            Interval = interval,
            AvailableAt = candle.CloseTime!.Value,
            Version = sequence,
            LatestCandle = candle
        };
        return new MarketFrame
        {
            Sequence = sequence,
            AvailableAt = candle.CloseTime.Value,
            ExecutionCandle = MarketCandle.FromMid(candle),
            AnalysisBaseCandle = candle,
            ClosedIntervals = new HashSet<BarInterval> { interval },
            Snapshots = new Dictionary<BarInterval, AnalysisSnapshot> { [interval] = snapshot },
            InputStreamId = "phase4-session-stream",
            IsWarmup = false,
            IsLastCandle = false
        };
    }

    private static StrategySimulationSession CreateTestSession(OrderSide side, BarInterval interval) =>
        StrategySimulationSession.Create(
            "phase4-streamed",
            new BuyOrSellOnceAgent(side, interval),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = false
            },
            dataQualityOptions: new MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.BreakEvenOnly,
                ManagementInterval = interval,
                BreakEvenActivationR = 1m,
                StructureTrailActivationR = 1m,
                BreakEvenBufferAtr = 0.05m,
                MinimumStopImprovementAtr = 0m
            });

    private static string Fingerprint(IReadOnlyList<SimulatedTradeRecord> trades)
    {
        string canonical = string.Join('\n', trades.Select(trade =>
            $"{trade.SetupId}|{trade.Side}|{trade.SignalCreatedAt:O}|{trade.OpenedAt:O}|{trade.ClosedAt:O}|" +
            $"{trade.EntryPrice}|{trade.ExitPrice}|{trade.InitialStopLossPrice}|{trade.FinalStopLossPrice}|" +
            $"{trade.ExitReason}|{trade.RMultiple}|" +
            string.Join(';', trade.StopAmendments.Select(amendment =>
                $"{amendment.RequestedSequence},{amendment.EffectiveSequence},{amendment.PreviousStopPrice}," +
                $"{amendment.AcceptedStopPrice},{amendment.Reason},{amendment.Status}"))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed class BuyOrSellOnceAgent(OrderSide side, BarInterval interval) : ITradingAgent
    {
        private bool _submitted;
        public string Name => "Phase 4 streamed trailing agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_submitted)
            {
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Entry already submitted"
                });
            }

            _submitted = true;
            return Task.FromResult(new AgentDecision
            {
                StrategyName = Name,
                SetupId = $"phase4-streamed-{side}",
                SetupStartedAt = context.Timestamp,
                SignalInterval = interval,
                Action = side == OrderSide.Buy ? AgentAction.Buy : AgentAction.Sell,
                Instrument = context.Instrument,
                SuggestedQuantity = 1m,
                QuantityUnit = QuantityUnit.Units,
                OrderType = StandardOrderType.Market,
                ReferencePrice = 100m,
                StopLossPrice = side == OrderSide.Buy ? 98m : 102m,
                Confidence = 100m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic streamed entry"
            });
        }
    }

    private sealed class OpenTrade : IAsyncDisposable
    {
        private long _sequence = 1;

        private OpenTrade(
            OrderSide side,
            BarInterval interval,
            HistoricalSimulationClock clock,
            SimulatedBrokerClient broker,
            BrokerPosition position,
            BrokerOrder stop)
        {
            Side = side;
            Interval = interval;
            Clock = clock;
            Broker = broker;
            Position = position;
            Stop = stop;
        }

        public OrderSide Side { get; }
        public BarInterval Interval { get; }
        public HistoricalSimulationClock Clock { get; }
        public SimulatedBrokerClient Broker { get; }
        public BrokerPosition Position { get; }
        public BrokerOrder Stop { get; }

        public static async Task<OpenTrade> CreateAsync(OrderSide side, BarInterval interval)
        {
            var clock = new HistoricalSimulationClock();
            clock.AdvanceTo(Start);
            var broker = new SimulatedBrokerClient(new SimulationOptions
            {
                StartingBalance = 10_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            }, clock);
            await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
            {
                Instrument = Instrument,
                Side = side,
                Type = StandardOrderType.Market,
                Quantity = new OrderQuantity(1m, QuantityUnit.Units),
                StopLoss = new StopLossInstruction(side == OrderSide.Buy ? 95m : 105m),
                TakeProfit = new TakeProfitInstruction(side == OrderSide.Buy ? 110m : 90m),
                ClientOrderId = $"phase4-{side}-{BarIntervalParser.Format(interval)}"
            });
            clock.AdvanceTo(interval.AddTo(Start));
            decimal close = side == OrderSide.Buy ? 104m : 96m;
            await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
                Instrument,
                Start,
                interval,
                100m,
                Math.Max(104m, close),
                Math.Min(96m, close),
                close));
            BrokerPosition position = (await broker.Positions.GetOpenPositionsAsync()).Single();
            BrokerOrder stop = (await broker.Orders.GetOpenOrdersAsync())
                .Single(order => order.Type == StandardOrderType.Stop.ToString());
            return new OpenTrade(side, interval, clock, broker, position, stop);
        }

        public AmendProtectiveStopRequest Amendment(decimal newStop, string id) => new()
        {
            Instrument = Instrument,
            PositionId = Position.PositionId,
            ExistingStopOrderId = Stop.BrokerOrderId,
            CurrentStopPrice = Stop.Price!.Value,
            NewStopPrice = newStop,
            CurrentExecutablePrice = Side == OrderSide.Buy ? 104m : 96m,
            PositionQuantity = Position.Quantity,
            PositionSide = Side,
            MinimumPriceIncrement = 0.01m,
            ClientAmendmentId = id,
            Reason = StopAmendmentReason.BreakEven.ToString(),
            RequestedAt = Clock.UtcNow,
            EffectiveFromExecutionSequence = _sequence + 1
        };

        public ProtectiveStopAmendmentCommand Command(decimal proposedStop, string id) => new()
        {
            Instrument = Instrument,
            StrategyId = "phase4",
            SetupId = "phase4-setup",
            PositionId = Position.PositionId,
            ExistingStopOrderId = Stop.BrokerOrderId,
            PositionSide = Side,
            PositionQuantity = Position.Quantity,
            EntryPrice = Position.AveragePrice!.Value,
            InitialStopPrice = Stop.Price!.Value,
            CurrentStopPrice = Stop.Price.Value,
            ProposedStopPrice = proposedStop,
            CurrentExecutablePrice = Side == OrderSide.Buy ? 104m : 96m,
            MinimumPriceIncrement = 0.01m,
            OpenProfitR = 1m,
            AmendmentReason = StopAmendmentReason.BreakEven,
            Reason = "Risk reducing test amendment",
            CreatedAt = Clock.UtcNow,
            RequestedSequence = _sequence,
            EffectiveFromExecutionSequence = _sequence + 1,
            ClientAmendmentId = id
        };

        public async Task NextAsync(decimal open, decimal high, decimal low, decimal close)
        {
            DateTimeOffset openTime = Interval.AddTo(Start);
            Clock.AdvanceTo(Interval.AddTo(openTime));
            _sequence++;
            await Broker.Runtime.ProcessExecutionCandleAsync(
                TestCandles.Create(Instrument, openTime, Interval, open, high, low, close));
        }

        public ValueTask DisposeAsync() => Broker.DisposeAsync();
    }
}
