using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;
using TradingCore.MarketData;

namespace Simulator.Tests;

/// <summary>
/// Regression proof that the 2026-07-16 audit §14-17 fix actually closed the dead-wiring bug:
/// before this pass, <c>BacktestApplicationService</c> never set
/// <c>StreamingComparativeEngineOptions.MetaLabelModel</c> from any Runtime field, so
/// <see cref="SimulatedTradeRecord.MetaLabelRiskMultiplier"/> was always null regardless of
/// configuration. This test drives a real <see cref="StrategySimulationSession"/> with a
/// <see cref="CalibratedSetupMetaModel"/> wired in and asserts the multiplier reaches the
/// closed trade record, and is never greater than 1.
/// </summary>
[TestFixture]
public sealed class MetaModelWiringEndToEndTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    private const string StrategyName = "Meta-model wiring test agent";

    [Test]
    public async Task MetaModel_WiredIntoSession_PopulatesRiskMultiplierOnClosedTrade()
    {
        var metaModel = new CalibratedSetupMetaModel(
            new MetaModelArtifact
            {
                SchemaVersion = 1,
                CalibrationId = "meta-e2e-test",
                ModelVersion = "meta-v1",
                FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
                TrainingFrom = Start.AddMonths(-1),
                TrainingTo = Start,
                DataHash = "hash",
                CreatedAt = Start,
                Buckets = []
            },
            new MetaModelPolicyOptions());

        await using StrategySimulationSession session = StrategySimulationSession.Create(
            "meta-model-wiring-test",
            new BuyOnceAgent(Interval),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = true
            },
            dataQualityOptions: new MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.Disabled,
                ManagementInterval = Interval
            },
            metaModel: metaModel);

        await session.ProcessFrameAsync(SessionFrame(1, 100m, 101m, 99m, 100m));
        await session.ProcessFrameAsync(SessionFrame(2, 100m, 103m, 100m, 102m));
        await session.BuildSimulationResultAsync();

        Assert.That(session.Trades, Has.Count.EqualTo(1));
        SimulatedTradeRecord trade = session.Trades[0];

        Assert.Multiple(() =>
        {
            Assert.That(trade.MetaLabelRiskMultiplier, Is.Not.Null,
                "The meta-model was wired in but never reached the closed trade record - the dead-wiring bug is back.");
            Assert.That(trade.MetaLabelRiskMultiplier, Is.LessThanOrEqualTo(1m),
                "A meta-model must never leverage risk above the strategy's own base sizing.");
        });
    }

    private static MarketFrame SessionFrame(long sequence, decimal open, decimal high, decimal low, decimal close)
    {
        DateTimeOffset openTime = Start.AddMinutes(sequence - 1);
        Candle candle = TestCandles.Create(Instrument, openTime, Interval, open, high, low, close);
        AnalysisSnapshot snapshot = new()
        {
            Instrument = Instrument,
            Interval = Interval,
            AvailableAt = candle.CloseTime!.Value,
            Version = sequence,
            LatestCandle = candle,
            Indicators = new IndicatorSnapshot { Atr = 1m },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot(),
            MarketRegime = new MarketRegimeSnapshot
            {
                Regime = MarketRegime.TrendingUp,
                Confidence = 80m,
                ConfirmedAt = candle.CloseTime.Value,
                AgeCandles = 1,
                Contributions = [],
                ReasonCode = "test",
                IsTradeable = true
            },
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
        return new MarketFrame
        {
            Sequence = sequence,
            AvailableAt = candle.CloseTime.Value,
            ExecutionCandle = MarketCandle.FromMid(candle),
            AnalysisBaseCandle = candle,
            ClosedIntervals = new HashSet<BarInterval> { Interval },
            Snapshots = new Dictionary<BarInterval, AnalysisSnapshot> { [Interval] = snapshot },
            InputStreamId = "meta-model-wiring-test-stream",
            IsWarmup = false,
            IsLastCandle = false
        };
    }

    private sealed class BuyOnceAgent(BarInterval interval) : ITradingAgent
    {
        private bool _submitted;
        public string Name => StrategyName;
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
                SetupId = "meta-model-wiring-buy",
                SetupStartedAt = context.Timestamp,
                SignalInterval = interval,
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 1m,
                QuantityUnit = QuantityUnit.Units,
                OrderType = StandardOrderType.Market,
                ReferencePrice = 100m,
                StopLossPrice = 98m,
                Confidence = 100m,
                ExpectedRewardRisk = 2m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic meta-model wiring entry"
            });
        }
    }
}
