using ExecutionManager;
using TradingJournal;
using Agent.Models;
using RiskManager;
using RiskManager.Safety;
using TradingCore.MarketData;
using TradeManager;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Structure;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class PhaseOneAndTwoSafetyTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void SafetyController_TripsAfterConfiguredConsecutiveLosses()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            MaximumConsecutiveLosses = 3
        });

        safety.RecordClosedTrade(-10m, Start);
        safety.RecordClosedTrade(-5m, Start.AddMinutes(5));
        TradingSafetySnapshot snapshot = safety.RecordClosedTrade(-1m, Start.AddMinutes(10));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(TradingSafetyState.Tripped));
            Assert.That(snapshot.Reason, Is.EqualTo(SafetyTripReason.ConsecutiveLossLimit));
            Assert.That(snapshot.ConsecutiveLosses, Is.EqualTo(3));
            Assert.That(snapshot.CanOpenNewTrades, Is.False);
        });
    }

    [Test]
    public void DailyEquityProfitTarget_PausesNewEntriesAndResetsNextUtcDay()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            DailyEquityProfitTarget = 500m
        });

        safety.ObserveEquity(100_000m, Start);
        TradingSafetySnapshot locked = safety.ObserveEquity(100_500m, Start.AddHours(8));
        TradingSafetySnapshot nextDay = safety.ObserveEquity(100_500m, Start.AddDays(1));

        Assert.Multiple(() =>
        {
            Assert.That(locked.State, Is.EqualTo(TradingSafetyState.Paused));
            Assert.That(locked.Reason, Is.EqualTo(SafetyTripReason.DailyProfitTarget));
            Assert.That(locked.CanOpenNewTrades, Is.False);
            Assert.That(nextDay.State, Is.EqualTo(TradingSafetyState.Active));
            Assert.That(nextDay.DailyEquityProfitLoss, Is.Zero);
        });
    }

    [Test]
    public void DailyEquityGiveback_PausesOnlyAfterActivationAndConfiguredGiveback()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            DailyEquityGivebackActivation = 400m,
            MaximumDailyEquityGiveback = 150m
        });

        safety.ObserveEquity(100_000m, Start);
        TradingSafetySnapshot peak = safety.ObserveEquity(100_500m, Start.AddHours(1));
        TradingSafetySnapshot protectedSnapshot = safety.ObserveEquity(100_349m, Start.AddHours(2));

        Assert.Multiple(() =>
        {
            Assert.That(peak.State, Is.EqualTo(TradingSafetyState.Active));
            Assert.That(peak.PeakDailyEquityProfitLoss, Is.EqualTo(500m));
            Assert.That(protectedSnapshot.State, Is.EqualTo(TradingSafetyState.Paused));
            Assert.That(protectedSnapshot.Reason, Is.EqualTo(SafetyTripReason.DailyProfitGiveback));
        });
    }

    [Test]
    public void DailyEquityGiveback_RejectsGivebackLargerThanActivation()
    {
        Assert.That(
            () => new TradingSafetyOptions
            {
                DailyEquityGivebackActivation = 400m,
                MaximumDailyEquityGiveback = 500m
            }.Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void DailyEquityGiveback_RequiresBothValues()
    {
        Assert.That(
            () => new TradingSafetyOptions
            {
                DailyEquityGivebackActivation = 400m
            }.Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void DataQualityGate_AllowsVersionDeltaWhenIntermediateTimeframeCandlesExist()
    {
        var gate = new MarketDataQualityGate();
        AnalysisSnapshot first = Snapshot(
            version: 1,
            openTime: Start,
            open: 100m,
            high: 101m,
            low: 99m,
            close: 100m);
        AnalysisSnapshot third = Snapshot(
            version: 3,
            openTime: Start.AddMinutes(10),
            open: 102m,
            high: 103m,
            low: 101m,
            close: 102m);

        DataQualityResult firstResult = gate.Evaluate(Analysis(first));
        DataQualityResult thirdResult = gate.Evaluate(Analysis(third));

        Assert.Multiple(() =>
        {
            Assert.That(firstResult.IsValid, Is.True);
            Assert.That(thirdResult.IsValid, Is.True);
            Assert.That(thirdResult.Issues, Is.Empty);
        });
    }

    [Test]
    public void DataQualityGate_TripsOnImpossibleOhlc()
    {
        var gate = new MarketDataQualityGate();
        AnalysisSnapshot invalid = Snapshot(
            version: 1,
            openTime: Start,
            open: 100m,
            high: 98m,
            low: 101m,
            close: 100m);

        DataQualityResult result = gate.Evaluate(Analysis(invalid));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ShouldTrip, Is.True);
            Assert.That(result.Issues.Select(issue => issue.Code), Does.Contain("candle.ohlc"));
        });
    }

    [Test]
    public async Task PhaseOneExecution_RejectsPoorRewardRiskAndJournalsReason()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = new SimulatedBrokerClient(
            new SimulationOptions { StartingBalance = 10_000m },
            clock);
        var journal = new InMemoryTradeJournal();
        var coordinator = new ExecutionCoordinator(
            riskManager: new PreTradeRiskManager(PreTradeRiskOptions.PhaseOneSafeDefaults),
            journal: journal);
        AgentDecision decision = new()
        {
            Action = AgentAction.Buy,
            Instrument = Instrument,
            SuggestedQuantity = 1m,
            ReferencePrice = 100m,
            StopLossPrice = 99m,
            TakeProfitPrice = 100.5m,
            Confidence = 90m,
            CreatedAt = Start,
            Reason = "Insufficient reward/risk test"
        };

        OrderSubmission submission = (await coordinator.ProcessAsync(decision, broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(submission.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(submission.Certainty, Is.EqualTo(ExecutionCertainty.NotSent));
            Assert.That(submission.RejectionReason, Does.Contain("Reward/risk"));
            Assert.That(
                journal.Snapshot().Any(entry =>
                    entry.Type == TradeJournalEventType.SignalRejected &&
                    entry.Message.Contains("Reward/risk", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public void MarketStructureAnalyzer_DetectsRisingStructureAndBearishBreak()
    {
        var analyzer = new MarketStructureAnalyzer();
        SwingPoint[] swings =
        [
            Swing(Start, 101m, SwingType.High),
            Swing(Start.AddMinutes(5), 99m, SwingType.Low),
            Swing(Start.AddMinutes(10), 103m, SwingType.High),
            Swing(Start.AddMinutes(15), 100m, SwingType.Low)
        ];
        Candle trendingCandle = Candle(Start.AddMinutes(20), 102m, 103m, 101m, 102m);

        MarketStructureSnapshot rising = analyzer.Analyze(swings, trendingCandle, 1m);
        Candle brokenCandle = Candle(Start.AddMinutes(25), 100m, 100.5m, 98m, 98.5m);
        MarketStructureSnapshot broken = analyzer.Analyze(swings, brokenCandle, 1m, rising);

        Assert.Multiple(() =>
        {
            Assert.That(rising.Direction, Is.EqualTo(MarketStructureDirection.Rising));
            Assert.That(rising.DirectionChanged, Is.True);
            Assert.That(broken.Break, Is.EqualTo(MarketStructureBreak.Bearish));
        });
    }

    [Test]
    public void StructureTradeManager_MovesLongStopOnlyInFavour()
    {
        AnalysisSnapshot analysis = Snapshot(
            version: 10,
            openTime: Start,
            open: 103m,
            high: 104.5m,
            low: 102.8m,
            close: 104m) with
        {
            Indicators = new IndicatorSnapshot { Atr = 1m },
            PriceZones =
            [
                new PriceZone
                {
                    LowerPrice = 102.5m,
                    UpperPrice = 103m,
                    CentrePrice = 102.75m,
                    TouchCount = 3,
                    Strength = 80m,
                    Type = PriceZoneType.Support
                }
            ],
            Swings = [Swing(Start.AddMinutes(-5), 102.8m, SwingType.Low)]
        };
        var manager = new StructureBasedTradeManager();
        var trade = new ManagedTradeState
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            EntryPrice = 100m,
            InitialStopPrice = 98m,
            CurrentStopPrice = 99m,
            CurrentPrice = 104m
        };

        TradeManagementRecommendation recommendation = manager.Evaluate(trade, analysis);

        Assert.Multiple(() =>
        {
            Assert.That(recommendation.Action, Is.EqualTo(TradeManagementAction.MoveStop));
            Assert.That(recommendation.ProposedStopPrice, Is.GreaterThan(trade.EntryPrice));
            Assert.That(recommendation.ProposedStopPrice, Is.GreaterThan(trade.CurrentStopPrice));
            Assert.That(recommendation.ProposedStopPrice, Is.LessThan(trade.CurrentPrice));
            Assert.That(recommendation.OpenProfitR, Is.EqualTo(2m));
        });
    }

    [Test]
    public void StructureTradeManager_ExitsOnAdverseStructureBreak()
    {
        AnalysisSnapshot analysis = Snapshot(10, Start, 103m, 104m, 102m, 103m) with
        {
            Indicators = new IndicatorSnapshot { Atr = 1m },
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = MarketStructureDirection.Rising,
                Break = MarketStructureBreak.Bearish
            }
        };
        var trade = new ManagedTradeState
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            EntryPrice = 100m,
            InitialStopPrice = 98m,
            CurrentStopPrice = 100m,
            CurrentPrice = 103m
        };

        TradeManagementRecommendation recommendation =
            new StructureBasedTradeManager(new PositionManagementOptions
            {
                ExitOnAdverseStructureBreak = true
            }).Evaluate(trade, analysis);

        Assert.That(recommendation.Action, Is.EqualTo(TradeManagementAction.Exit));
    }

    private static MultiTimeframeAnalysis Analysis(AnalysisSnapshot snapshot) => new(
        Instrument,
        snapshot.AvailableAt,
        new Dictionary<BarInterval, AnalysisSnapshot> { [Five] = snapshot });

    private static AnalysisSnapshot Snapshot(
        long version,
        DateTimeOffset openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close)
    {
        Candle candle = Candle(openTime, open, high, low, close);
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = Five,
            AvailableAt = candle.CloseTime!.Value,
            Version = version,
            LatestCandle = candle,
            Indicators = new IndicatorSnapshot(),
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
    }

    private static Candle Candle(
        DateTimeOffset openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => new()
        {
            Instrument = Instrument,
            Interval = Five,
            OpenTime = openTime,
            CloseTime = openTime.AddMinutes(5),
            Prices = new Ohlc(open, high, low, close),
            Volume = new MarketVolume(1m, VolumeKind.TickCount),
            IsComplete = true
        };

    private static SwingPoint Swing(
        DateTimeOffset time,
        decimal price,
        SwingType type) => new()
        {
            PivotTime = time,
            ConfirmedAt = time.AddMinutes(5),
            Price = price,
            Type = type,
            Strength = 2
        };
}
