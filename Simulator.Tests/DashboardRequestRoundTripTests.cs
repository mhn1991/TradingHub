using System.Text.Json;
using Dashboard.Live;
using RiskManager.Calibration;
using RiskManager.Conditions;
using Simulator.Execution;
using Simulator.Financing;
using Simulator.Models;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class DashboardRequestRoundTripTests
{
    [Test]
    public void QuantitativeFields_RoundTripThroughJsonAndServerMapping()
    {
        DateTimeOffset from = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new CreateSimulationRequest
        {
            BrokerId = "oanda",
            Instrument = "FX:GBP/USD",
            From = from,
            To = from.AddDays(5),
            AccountMode = "SharedPortfolioAccount",
            RegimeEnabled = true,
            TradingConditionsEnabled = true,
            AllowedSessions = ["London", "LondonNewYorkOverlap"],
            AdaptiveRiskEnabled = true,
            ExecutionFillModel = "VariableSyntheticSpread",
            StressExecutionScenario = "SpreadDouble",
            MaximumFillQuantityPerFrame = 250m,
            FinancingEnabled = true,
            FinancingRates = new Dictionary<string, FinancingRate>
            {
                ["FX:GBP/USD"] = new()
                {
                    LongAnnualPercent = -3.5m,
                    ShortAnnualPercent = 1.25m
                }
            }
        };
        string json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        CreateSimulationRequest restored = JsonSerializer.Deserialize<CreateSimulationRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var broker = new SimulationBrokerOption(
            "oanda",
            "OANDA",
            "Demo",
            "OandaCandles",
            true,
            true,
            "test",
            ["1m", "5m", "15m", "30m", "1h", "2h"],
            [new SimulationInstrumentOption("GBP_USD", "GBP/USD", "FX:GBP/USD", "Forex")]);

        BacktestRequest mapped = restored.ToBacktestRequest(broker);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Runtime.AccountMode, Is.EqualTo(SimulationAccountMode.SharedPortfolioAccount));
            Assert.That(mapped.Runtime.AnnotationOptions.MarketRegime.Enabled, Is.True);
            Assert.That(mapped.Runtime.TradingConditions.AllowedSessions,
                Is.EqualTo(new[] { TradingSession.London, TradingSession.LondonNewYorkOverlap }));
            Assert.That(mapped.Runtime.AdaptiveRisk.Enabled, Is.True);
            Assert.That(mapped.Runtime.Execution.FillModel, Is.EqualTo(SimulationFillModel.VariableSyntheticSpread));
            Assert.That(mapped.Runtime.Execution.StressScenario, Is.EqualTo(StressExecutionScenario.SpreadDouble));
            Assert.That(mapped.Runtime.Execution.FillCapacity.MaximumQuantityPerExecutionFrame, Is.EqualTo(250m));
            Assert.That(mapped.Runtime.Financing.InstrumentRates["FX:GBP/USD"].LongAnnualPercent, Is.EqualTo(-3.5m));
            Assert.That(() => mapped.Validate(), Throws.Nothing);
        });
    }

    [Test]
    public void DefaultConstruction_EnablesRegimeRoutingAndValueLocationEvidence()
    {
        // Regression guard for the 2026-07-16 agent decision-quality default flip: a
        // request that omits these fields entirely (e.g. a raw API caller, not the
        // Dashboard UI which always sends its own explicit form value) must still get
        // the proven, tested evidence types on by default.
        var request = new CreateSimulationRequest();

        Assert.Multiple(() =>
        {
            Assert.That(request.RegimeEnabled, Is.True);
            Assert.That(request.ValueLocationEvidenceEnabled, Is.True);
            Assert.That(request.AdaptiveRiskEnabled, Is.True);
            Assert.That(request.AutoCalibrateBeforeRun, Is.True);
            Assert.That(request.AutoCalibrateTrainMonths, Is.EqualTo(2));
            Assert.That(request.AutoCalibrateEmbargoDays, Is.EqualTo(10));
            Assert.That(request.ShouldAutoCalibrate(null, null, null), Is.True);
        });
    }

    [Test]
    public void ShouldAutoCalibrate_IsFalseWhenManualArtifactsResolved()
    {
        var request = new CreateSimulationRequest { AutoCalibrateBeforeRun = true };
        SetupCalibrationArtifact setup = new()
        {
            SchemaVersion = 1,
            CalibrationId = "x",
            TrainingFrom = DateTimeOffset.UtcNow.AddMonths(-2),
            TrainingTo = DateTimeOffset.UtcNow.AddDays(-10),
            Instruments = ["FX"],
            StrategyVersion = "legacy",
            FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
            Parameters = new Dictionary<string, string>(),
            TotalSamples = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            DataHash = "h",
            Buckets = []
        };

        Assert.That(request.ShouldAutoCalibrate(setup, null, null), Is.False);
        Assert.That(
            new CreateSimulationRequest { AutoCalibrateBeforeRun = false }.ShouldAutoCalibrate(null, null, null),
            Is.False);
    }

    [Test]
    public void ToBacktestRequest_WithResolvedCalibrationArtifacts_EnablesBothPoliciesOnRuntime()
    {
        DateTimeOffset from = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new CreateSimulationRequest
        {
            BrokerId = "oanda",
            Instrument = "FX:GBP/USD",
            From = from,
            To = from.AddDays(5),
            // Server never accepts these directly - only Guid-parseable IDs the handler
            // resolves via ICalibrationArtifactRepository before calling ToBacktestRequest.
            SetupCalibrationArtifactId = Guid.NewGuid().ToString("N"),
            ManagementCalibrationArtifactId = Guid.NewGuid().ToString("N")
        };
        var broker = new SimulationBrokerOption(
            "oanda",
            "OANDA",
            "Demo",
            "OandaCandles",
            true,
            true,
            "test",
            ["1m", "5m", "15m", "30m", "1h", "2h"],
            [new SimulationInstrumentOption("GBP_USD", "GBP/USD", "FX:GBP/USD", "Forex")]);

        SetupCalibrationArtifact setupArtifact = new()
        {
            SchemaVersion = 1,
            CalibrationId = "cal-1",
            TrainingFrom = from.AddMonths(-1),
            TrainingTo = from,
            Instruments = ["FX"],
            StrategyVersion = "improved",
            FeatureSchemaHash = "tradinghub-meta-v1",
            Parameters = new Dictionary<string, string>(),
            TotalSamples = 10,
            CreatedAt = from,
            DataHash = "hash",
            Buckets = []
        };
        TradeManagementCalibration managementArtifact = new()
        {
            CalibrationId = "mgmt-1",
            SourceDataHash = "hash",
            CreatedAt = from,
            Cohorts = []
        };

        BacktestRequest mapped = request.ToBacktestRequest(broker, setupArtifact, managementArtifact);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Runtime.SetupCalibration.Enabled, Is.True);
            Assert.That(mapped.Runtime.SetupCalibrationArtifact, Is.EqualTo(setupArtifact));
            Assert.That(mapped.Runtime.ManagementCalibration.Enabled, Is.True);
            Assert.That(mapped.Runtime.ManagementCalibrationArtifact, Is.EqualTo(managementArtifact));
        });
    }

    [Test]
    public void ToBacktestRequest_WithoutResolvedArtifacts_LeavesBothPoliciesDisabled()
    {
        DateTimeOffset from = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new CreateSimulationRequest
        {
            BrokerId = "oanda",
            Instrument = "FX:GBP/USD",
            From = from,
            To = from.AddDays(5)
        };
        var broker = new SimulationBrokerOption(
            "oanda",
            "OANDA",
            "Demo",
            "OandaCandles",
            true,
            true,
            "test",
            ["1m", "5m", "15m", "30m", "1h", "2h"],
            [new SimulationInstrumentOption("GBP_USD", "GBP/USD", "FX:GBP/USD", "Forex")]);

        BacktestRequest mapped = request.ToBacktestRequest(broker);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Runtime.SetupCalibration.Enabled, Is.False);
            Assert.That(mapped.Runtime.SetupCalibrationArtifact, Is.Null);
            Assert.That(mapped.Runtime.ManagementCalibration.Enabled, Is.False);
            Assert.That(mapped.Runtime.ManagementCalibrationArtifact, Is.Null);
        });
    }
}
