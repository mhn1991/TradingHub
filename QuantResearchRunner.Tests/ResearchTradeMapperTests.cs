using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Calibration;
using QuantResearch.Models;
using QuantResearch.Training.Mapping;
using Simulator.Models;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class ResearchTradeMapperTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Opened = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Closed = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void ToResearchTrade_MapsAllFieldsFromSimulatedTradeRecord()
    {
        SimulatedTradeRecord trade = Trade() with
        {
            PositionId = "pos-1",
            EntryPrice = 1.3000m,
            InitialStopLossPrice = 1.2950m,
            RMultiple = 1.5m,
            MaximumFavourableExcursionR = 2.0m,
            MaximumAdverseExcursionR = -0.4m,
            NetProfitLoss = 15m,
            PlannedStopRiskAccountCurrency = 10m,
            RealizedPartialNetProfitLoss = 4m,
            EntryNeoWaveHypothesisId = "wave-1",
            EntryNeoWavePatternType = "ImpulseCandidate",
            EntryNeoWaveStructuralScore = 85m,
            EntryNeoWaveConflictScore = 15m,
            EntryNeoWaveInvalidationPrice = 1.2940m,
            NeoWaveRiskMultiplier = 0.8m
        };

        ResearchTrade? result = ResearchTradeMapper.ToResearchTrade(trade);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.TradeId, Is.EqualTo("pos-1"));
            Assert.That(result.StrategyId, Is.EqualTo("improved"));
            Assert.That(result.Instrument, Is.EqualTo("FX:GBP/USD"));
            Assert.That(result.InstrumentGroup, Is.EqualTo("FX"));
            Assert.That(result.Regime, Is.EqualTo(MarketRegime.TrendingUp.ToString()));
            Assert.That(result.SetupType, Is.EqualTo("Breakout"));
            Assert.That(result.Direction, Is.EqualTo(OrderSide.Buy.ToString()));
            Assert.That(result.Session, Is.EqualTo("London"));
            Assert.That(result.VolatilityBucket, Is.EqualTo("Normal"));
            Assert.That(result.Confidence, Is.EqualTo(72m));
            Assert.That(result.OpenedAt, Is.EqualTo(Opened));
            Assert.That(result.ClosedAt, Is.EqualTo(Closed));
            Assert.That(result.RMultiple, Is.EqualTo(1.5m));
            Assert.That(result.MaximumFavourableExcursionR, Is.EqualTo(2.0m));
            Assert.That(result.MaximumAdverseExcursionR, Is.EqualTo(-0.4m));
            Assert.That(result.StopDistance, Is.EqualTo(0.0050m));
            Assert.That(result.PartialExitContributionR, Is.EqualTo(0.4m));
            Assert.That(result.RunnerContributionR, Is.EqualTo(1.1m));
            Assert.That(result.NeoWaveHypothesisId, Is.EqualTo("wave-1"));
            Assert.That(result.NeoWavePatternType, Is.EqualTo("ImpulseCandidate"));
            Assert.That(result.NeoWaveStructuralScore, Is.EqualTo(85m));
            Assert.That(result.NeoWaveConflictScore, Is.EqualTo(15m));
            Assert.That(result.NeoWaveInvalidationPrice, Is.EqualTo(1.2940m));
            Assert.That(result.NeoWaveRiskMultiplier, Is.EqualTo(0.8m));
        });
    }

    [Test]
    public void ToResearchTrade_OpenTrade_ReturnsNull()
    {
        SimulatedTradeRecord trade = Trade() with { ClosedAt = null };

        Assert.That(ResearchTradeMapper.ToResearchTrade(trade), Is.Null);
    }

    [Test]
    public void ToResearchTrades_FiltersOpenTrades()
    {
        SimulatedTradeRecord[] trades =
        [
            Trade() with { PositionId = "closed", ClosedAt = Closed, RMultiple = 1m },
            Trade() with { PositionId = "open", ClosedAt = null, RMultiple = null }
        ];

        IReadOnlyList<ResearchTrade> result = ResearchTradeMapper.ToResearchTrades(trades);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].TradeId, Is.EqualTo("closed"));
    }

    [Test]
    public void ToResearchTrade_MissingPositionId_FallsBackToSetupIdAndOpenedAt()
    {
        SimulatedTradeRecord trade = Trade() with { PositionId = null };

        ResearchTrade? result = ResearchTradeMapper.ToResearchTrade(trade);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.TradeId, Is.EqualTo($"setup-1:{Opened:O}"));
    }

    [Test]
    public void ToResearchTrade_NullOptionalDecimals_DefaultToZero()
    {
        SimulatedTradeRecord trade = Trade() with
        {
            RMultiple = null,
            MaximumFavourableExcursionR = null,
            MaximumAdverseExcursionR = null,
            EntryPrice = null,
            InitialStopLossPrice = null
        };

        ResearchTrade? result = ResearchTradeMapper.ToResearchTrade(trade);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.RMultiple, Is.Zero);
            Assert.That(result.MaximumFavourableExcursionR, Is.Zero);
            Assert.That(result.MaximumAdverseExcursionR, Is.Zero);
            Assert.That(result.StopDistance, Is.Zero);
            Assert.That(result.PartialExitContributionR, Is.Zero);
            Assert.That(result.RunnerContributionR, Is.Zero);
        });
    }

    [Test]
    public void ToResearchTrade_DerivesRiskFromRMultipleWhenPlannedRiskMissing()
    {
        SimulatedTradeRecord trade = Trade() with
        {
            RMultiple = 2m,
            NetProfitLoss = 20m,
            RealizedPartialNetProfitLoss = 5m,
            PlannedStopRiskAccountCurrency = null
        };

        ResearchTrade? result = ResearchTradeMapper.ToResearchTrade(trade);

        Assert.That(result, Is.Not.Null);
        // initialRisk = 20/2 = 10; partial = 5/10 = 0.5; runner = 15/10 = 1.5
        Assert.Multiple(() =>
        {
            Assert.That(result!.PartialExitContributionR, Is.EqualTo(0.5m));
            Assert.That(result.RunnerContributionR, Is.EqualTo(1.5m));
        });
    }

    [Test]
    public void ToSetupOutcome_PositiveRMultiple_MarksAsWon()
    {
        SimulatedTradeRecord trade = Trade() with { RMultiple = 0.8m };

        SetupOutcome? result = ResearchTradeMapper.ToSetupOutcome(trade);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.StrategyId, Is.EqualTo("improved"));
            Assert.That(result.InstrumentGroup, Is.EqualTo("FX"));
            Assert.That(result.Regime, Is.EqualTo(MarketRegime.TrendingUp.ToString()));
            Assert.That(result.Confidence, Is.EqualTo(72m));
            Assert.That(result.Won, Is.True);
            Assert.That(result.RMultiple, Is.EqualTo(0.8m));
        });
    }

    [Test]
    public void ToSetupOutcome_NonPositiveRMultiple_MarksAsLost()
    {
        SimulatedTradeRecord trade = Trade() with { RMultiple = 0m };

        SetupOutcome? result = ResearchTradeMapper.ToSetupOutcome(trade);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Won, Is.False);
    }

    [Test]
    public void ToSetupOutcome_OpenTrade_ReturnsNull()
    {
        SimulatedTradeRecord trade = Trade() with { ClosedAt = null };

        Assert.That(ResearchTradeMapper.ToSetupOutcome(trade), Is.Null);
    }

    private static SimulatedTradeRecord Trade() => new()
    {
        StrategyId = "improved",
        StrategyName = "Improved Progressive",
        SetupId = "setup-1",
        Instrument = Instrument,
        Side = OrderSide.Buy,
        SetupStartedAt = Opened,
        SignalCreatedAt = Opened,
        OpenedAt = Opened,
        ClosedAt = Closed,
        EntryRegime = MarketRegime.TrendingUp,
        EntrySetupType = "Breakout",
        EntrySession = "London",
        EntryVolatilityBucket = "Normal",
        EntryConfidence = 72m,
        SetupReason = "test fixture"
    };
}
