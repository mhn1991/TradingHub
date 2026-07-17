using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Regime;
using LiveTrading.Agents;
using NUnit.Framework;
using RiskManager.Calibration;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class SignalFunnelTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;
    private static readonly BarInterval M5 = AgentTestSupport.M5;

    private static AgentMarketContext Context(MultiTimeframeAnalysis analysis, DateTimeOffset now) => new()
    {
        Instrument = Instrument,
        Timestamp = now,
        Analysis = analysis,
        Account = new AccountSnapshot { AccountId = "test-account" },
        Positions = [],
        OpenOrders = [],
        StrategyId = "strategy-1"
    };

    private static AgentDecision BuyDecision(DateTimeOffset now, MarketRegime? regimeLabel = null) => new()
    {
        DecisionId = "decision-1",
        SetupId = "setup-1",
        Action = AgentAction.Buy,
        Instrument = Instrument,
        Confidence = 0.75m,
        CreatedAt = now,
        Reason = "test",
        ReferencePrice = 1.1002m,
        StopLossPrice = 1.0950m,
        TakeProfitPrice = 1.1100m,
        RegimeLabel = regimeLabel
    };

    [Test]
    public void BuildCandidate_MultiTimeframeAlignment_MatchesDirectComputation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1, M5);
        AgentDecision decision = BuyDecision(now);
        var result = new TradingPipelineResult
        {
            Status = TradingPipelineStatus.Processed,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid,
            Decision = decision
        };

        LiveTradeCandidate candidate = SignalFunnel.BuildCandidate("strategy-1", Context(analysis, now), result, decisionEpoch: 1);

        decimal expected = MetaLabelFeatureFactory.ComputeMultiTimeframeAlignment(decision.Action, analysis);
        Assert.That(candidate.MultiTimeframeAlignment, Is.EqualTo(expected));
    }

    [Test]
    public void BuildCandidate_RegimeLabelPresent_UsesDecisionRegimeLabel()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        AgentDecision decision = BuyDecision(now, regimeLabel: MarketRegime.TrendingUp);
        var result = new TradingPipelineResult
        {
            Status = TradingPipelineStatus.Processed,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid,
            Decision = decision
        };

        LiveTradeCandidate candidate = SignalFunnel.BuildCandidate("strategy-1", Context(analysis, now), result, decisionEpoch: 1);

        Assert.That(candidate.EntryRegime, Is.EqualTo(MarketRegime.TrendingUp));
    }

    [Test]
    public void BuildCandidate_NoRegimeLabel_FallsBackToShortestSnapshotRegime()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1, M5); // both default to MarketRegimeSnapshot.Unknown
        AgentDecision decision = BuyDecision(now, regimeLabel: null);
        var result = new TradingPipelineResult
        {
            Status = TradingPipelineStatus.Processed,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid,
            Decision = decision
        };

        LiveTradeCandidate candidate = SignalFunnel.BuildCandidate("strategy-1", Context(analysis, now), result, decisionEpoch: 1);

        Assert.That(candidate.EntryRegime, Is.EqualTo(analysis.Get(M1).MarketRegime.Regime));
    }

    [Test]
    public void BuildCandidate_FeatureDisabled_UsesDisabledAuditDefaults()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        AgentDecision decision = BuyDecision(now);
        var result = new TradingPipelineResult
        {
            Status = TradingPipelineStatus.Processed,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid,
            Decision = decision
            // SetupCalibration and MetaLabel left null - both features disabled for this pipeline run.
        };

        LiveTradeCandidate candidate = SignalFunnel.BuildCandidate("strategy-1", Context(analysis, now), result, decisionEpoch: 1);

        Assert.Multiple(() =>
        {
            Assert.That(candidate.SetupCalibration, Is.EqualTo(SetupCalibrationAudit.Disabled));
            Assert.That(candidate.MetaLabel, Is.EqualTo(MetaLabelAudit.Disabled));
            Assert.That(candidate.MetaLabel.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void BuildCandidate_NoDecision_Throws()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        var result = new TradingPipelineResult
        {
            Status = TradingPipelineStatus.RejectedByDataQuality,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid
        };

        Assert.Throws<InvalidOperationException>(
            () => SignalFunnel.BuildCandidate("strategy-1", Context(analysis, now), result, decisionEpoch: 1));
    }
}
