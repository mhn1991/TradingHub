using Agent.Strategies;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.Value;
using LiveTrading;
using NUnit.Framework;
using PortfolioManager.CrossMarket;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Calibration;
using RiskManager.Conditions;
using RiskManager.Safety;
using TradeManager;
using TradingCore.Pipeline;

namespace TradingCore.Tests;

[TestFixture]
public sealed class LiveTradingPolicyBundleTests
{
    [Test]
    public void ValidBundle_PassesValidation()
    {
        Assert.That(() => Valid().Validate(), Throws.Nothing);
    }

    [Test]
    public void EmptyPolicyBundleId_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with { PolicyBundleId = Guid.Empty };
        Assert.That(bundle.Validate, Throws.ArgumentException);
    }

    [Test]
    public void RevisionBelowOne_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with { Revision = 0 };
        Assert.That(bundle.Validate, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void MismatchedFeatureSchemaHash_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with { FeatureSchemaHash = "not-the-real-hash" };
        Assert.That(bundle.Validate, Throws.ArgumentException);
    }

    [Test]
    public void BlankConfigurationHash_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with { ConfigurationHash = " " };
        Assert.That(bundle.Validate, Throws.ArgumentException);
    }

    [Test]
    public void FutureCreatedAt_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with { CreatedAt = DateTimeOffset.UtcNow.AddDays(1) };
        Assert.That(bundle.Validate, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void InvalidNestedOption_Throws()
    {
        LiveTradingPolicyBundle bundle = Valid() with
        {
            PositionSizing = new PositionSizingOptions { FixedQuantity = -1m }
        };
        Assert.That(bundle.Validate, Throws.Exception);
    }

    private static LiveTradingPolicyBundle Valid()
    {
        RuntimeFeaturePolicy featurePolicy = new()
        {
            AnnotationOptions = new ChartAnnotationOptions(),
            MarketRegimeRouting = new(),
            ValueLocationEvidence = new ValueLocationEvidenceOptions(),
            CurrencyStrengthEvidence = new CurrencyStrengthEvidenceOptions(),
            RsiBollingerSignals = new RsiBollingerSignalOptions(),
            DmiConfirmationEnabled = true,
            CurrencyStrength = new CurrencyStrengthOptions(),
            SetupCalibration = new SetupCalibrationPolicyOptions()
        };

        return new LiveTradingPolicyBundle
        {
            PolicyBundleId = Guid.NewGuid(),
            Revision = 1,
            StrategyVersion = "v1",
            FeatureSchemaHash = featurePolicy.ComputeHash(),
            FeaturePolicy = featurePolicy,
            PositionSizing = new PositionSizingOptions(),
            AdaptiveRisk = new AdaptiveRiskOptions(),
            CorrelationRisk = new PortfolioManager.Correlation.CorrelationRiskOptions(),
            PortfolioRisk = new PortfolioRiskOptions(),
            TradingConditions = new TradingConditionOptions(),
            AccountSafety = new TradingSafetyOptions(),
            LegacyManagement = new PositionManagementOptions(),
            RegimeManagement = new TradeManager.RegimeManagementOptions(),
            ImprovedManagement = new PositionManagementOptions(),
            ConfigurationHash = "config-hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
