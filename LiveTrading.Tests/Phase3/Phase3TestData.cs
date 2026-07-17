using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.Regime;
using ChartAnnotator.Value;
using LiveTrading.Agents;
using LiveTrading.Portfolio;
using PortfolioManager.CrossMarket;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Calibration;
using RiskManager.Conditions;
using RiskManager.Safety;
using TradeManager;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Phase3;

internal static class Phase3TestData
{
    internal static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    internal static LiveTradingPolicyBundle Policy()
    {
        var featurePolicy = new RuntimeFeaturePolicy
        {
            AnnotationOptions = new ChartAnnotationOptions(),
            MarketRegimeRouting = new MarketRegimePolicyOptions(),
            ValueLocationEvidence = new ValueLocationEvidenceOptions(),
            CurrencyStrengthEvidence = new CurrencyStrengthEvidenceOptions(),
            RsiBollingerSignals = new RsiBollingerSignalOptions(),
            DmiConfirmationEnabled = true,
            CurrencyStrength = new CurrencyStrengthOptions(),
            SetupCalibration = new SetupCalibrationPolicyOptions()
        };
        var policy = new LiveTradingPolicyBundle
        {
            PolicyBundleId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Revision = 3,
            StrategyVersion = "improved-test-v1",
            FeatureSchemaHash = featurePolicy.ComputeHash(),
            FeaturePolicy = featurePolicy,
            PositionSizing = new PositionSizingOptions
            {
                Mode = PositionSizingMode.FixedFractionalRisk,
                RiskPercentOfEquity = 0.10m,
                MinimumQuantity = 1m,
                QuantityStep = 1m,
                MaximumAccountMarginUsagePercent = 15m,
                MaximumSinglePositionMarginPercent = 10m,
                Leverage = 20m
            },
            AdaptiveRisk = new AdaptiveRiskOptions(),
            CorrelationRisk = new PortfolioManager.Correlation.CorrelationRiskOptions(),
            PortfolioRisk = new PortfolioRiskOptions
            {
                MaximumTotalOpenRiskPercent = 0.50m,
                MaximumPendingRiskPercent = 0.50m,
                MaximumStrategyRiskPercent = 0.50m,
                MaximumInstrumentRiskPercent = 0.50m,
                MaximumCurrencyStopRiskPercent = 0.50m,
                MaximumMarginUsagePercent = 15m,
                MaximumSinglePositionMarginPercent = 10m,
                MinimumUnallocatedMarginReservePercent = 50m,
                MaximumOpenPositions = 2
            },
            TradingConditions = new TradingConditionOptions
            {
                Enabled = true,
                SoftMaximumSpreadAtr = 0.25m,
                HardMaximumSpreadAtr = 0.50m,
                EconomicEventFilterEnabled = false
            },
            AccountSafety = new TradingSafetyOptions(),
            LegacyManagement = PositionManagementOptions.LegacyDefaults,
            RegimeManagement = new TradeManager.RegimeManagementOptions(),
            ImprovedManagement = PositionManagementOptions.ImprovedDefaults,
            ConfigurationHash = "phase3-test-configuration",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        policy.Validate();
        return policy;
    }

    internal static LiveTradeCandidate Candidate(
        string candidateId = "candidate-1",
        string decisionId = "decision-1",
        decimal setupMultiplier = 1m,
        decimal metaMultiplier = 1m) => new()
    {
        CandidateId = candidateId,
        DecisionId = decisionId,
        SetupId = "setup-1",
        StrategyId = "improved",
        Instrument = Instrument,
        Action = AgentAction.Buy,
        DecisionTime = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        DecisionEpoch = 1_721_130_400_000,
        MarketSequence = 1,
        ReferencePrice = 1.1000m,
        StopLossPrice = 1.0950m,
        TakeProfitPrice = 1.1100m,
        RawConfidence = 75m,
        MultiTimeframeAlignment = 0.90m,
        EntryRegime = MarketRegime.TrendingUp,
        SetupCalibration = new SetupCalibrationAudit
        {
            Trade = true,
            RiskMultiplier = setupMultiplier,
            ReasonCode = "SetupCalibrationApproved",
            Explanation = "test"
        },
        MetaLabel = new MetaLabelAudit
        {
            Trade = true,
            Probability = 0.65m,
            RiskMultiplier = metaMultiplier,
            ReasonCode = "MetaLabelApproved",
            ModelVersion = "test-model"
        },
        TradingCondition = new TradingConditionDecision
        {
            Action = TradingConditionAction.Allow,
            RiskMultiplier = 1m,
            ReasonCode = "TradingConditionsApproved",
            Explanation = "test",
            Session = TradingSession.London,
            SpreadAtr = 0.10m
        }
    };

    internal static LiveOpportunityEvaluationContext Context(
        LiveTradingPolicyBundle policy,
        IPortfolioReservationBook reservations) => new()
    {
        Portfolio = new LivePortfolioSnapshot
        {
            Account = new AccountSnapshot
            {
                AccountId = "practice-account",
                Currency = "USD",
                Balance = 100_000m,
                MarginUsed = 0m,
                UnrealizedProfitLoss = 0m,
                CanTrade = true
            },
            Equity = 100_000m,
            Positions = [],
            Orders = [],
            Reservations = reservations.Snapshot,
            CurrentOpenRiskAccountCurrency = 0m
        },
        PolicyResolver = _ => policy,
        QuoteToAccountCurrencyRates = new Dictionary<InstrumentKey, decimal>
        {
            [Instrument] = 1m
        },
        InstrumentRiskSpecs = new Dictionary<InstrumentKey, InstrumentRiskSpec>
        {
            [Instrument] = InstrumentRiskSpec.UnitNotional
        }
    };

    internal static PortfolioApprovedDecision ApprovedDecision()
    {
        LiveTradingPolicyBundle policy = Policy();
        AgentDecision decision = new()
        {
            DecisionId = "decision-1",
            SetupId = "setup-1",
            StrategyId = "improved",
            StrategyName = "improved",
            Action = AgentAction.Buy,
            Instrument = Instrument,
            SuggestedQuantity = 20_000m,
            PortfolioOriginalQuantity = 20_000m,
            PortfolioAllocatedQuantity = 20_000m,
            PortfolioReservationId = "res:improved:decision-1",
            QuantityIsPortfolioApproved = true,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = 1.1000m,
            StopLossPrice = 1.0950m,
            TakeProfitPrice = 1.1100m,
            ExpectedRewardRisk = 2m,
            Confidence = 75m,
            CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
            Reason = "test"
        };
        return new PortfolioApprovedDecision
        {
            Candidate = Candidate(),
            Decision = decision,
            ReservationId = "res:improved:decision-1",
            PortfolioScore = 3m,
            RiskBudget = new LiveRiskBudgetAudit
            {
                BaseRiskAmount = 100m,
                SetupMultiplier = 1m,
                MetaLabelMultiplier = 1m,
                RegimeMultiplier = 1m,
                TradingConditionMultiplier = 1m,
                DrawdownMultiplier = 1m,
                VolatilityMultiplier = 1m,
                LiquidityMultiplier = 1m,
                CorrelationMultiplier = 1m,
                StrategyMultiplier = 1m,
                EquityProtectionMultiplier = 1m,
                CombinedMultiplier = 1m,
                FinalRiskAmount = 100m,
                ReasonCode = "BaseRiskBudget"
            },
            PositionSizing = new LivePositionSizingAudit
            {
                RequestedRisk = 100m,
                RawQuantity = 20_000m,
                BrokerNormalizedQuantity = 20_000m,
                EstimatedStopLoss = 100m,
                EstimatedMargin = 1_100m,
                ExpectedCosts = 0m,
                QuoteConversionPath = "EUR/USD quote->USD@1",
                ReasonCode = "FixedFractionalRisk"
            },
            PolicyBundleId = policy.PolicyBundleId,
            PolicyRevision = policy.Revision,
            ConfigurationHash = policy.ConfigurationHash
        };
    }
}
