using System.Text.Json;
using Dashboard.Live;
using RiskManager.Conditions;
using Simulator.Execution;
using Simulator.Financing;
using Simulator.Models;

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
}
