using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;
using TradingCore.Pipeline;

namespace Simulator.Tests;

/// <summary>
/// Phase 5 of the multi-agent architecture: <see cref="StrategyInstrumentAssignment"/>'s new
/// Mode/AgentOptionsOverride/AnalysisOptionsOverride fields, and the "at most one Executable
/// assignment per instrument" validation that closes a real, previously-unflagged gap - before
/// this phase the simulator had no shadow/executable concept per assignment at all.
/// </summary>
[TestFixture]
public sealed class StrategyAssignmentValidationTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly InstrumentKey OtherInstrument = new("FX:GBP/USD");
    // Minute-aligned, unlike DateTimeOffset.UtcNow - MultiTimeframeAggregator throws if a base
    // candle's coverage window straddles a higher-timeframe boundary.
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static BacktestRequest BuildRequest(IReadOnlyList<StrategyInstrumentAssignment> assignments) => new()
    {
        Instrument = Instrument,
        From = Start,
        To = Start.AddMinutes(60),
        StrategyAssignments = assignments,
        InlineCandles = BuildCandles(Instrument, BarInterval.Minutes(1), Start, 60),
        Runtime = new BacktestRuntimeOptions { SourceKind = HistoricalDataSourceKind.InlineTestData, WarmupDays = 0 }
    };

    [Test]
    public void Validate_TwoExecutableAssignmentsOnSameInstrument_Throws()
    {
        BacktestRequest request = BuildRequest([
            new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument, Mode = AgentExecutionMode.Executable },
            new StrategyInstrumentAssignment { StrategyType = "improved", Instrument = Instrument, Mode = AgentExecutionMode.Executable }
        ]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => request.Validate())!;
        Assert.That(exception.Message, Does.Contain("Executable"));
    }

    [Test]
    public void Validate_TwoExecutableAssignmentsOnDifferentInstruments_DoesNotThrow()
    {
        BacktestRequest request = BuildRequest([
            new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument, Mode = AgentExecutionMode.Executable },
            new StrategyInstrumentAssignment { StrategyType = "improved", Instrument = OtherInstrument, Mode = AgentExecutionMode.Executable }
        ]);

        Assert.DoesNotThrow(() => request.Validate());
    }

    [Test]
    public void Validate_OneExecutableOneShadowOnSameInstrument_DoesNotThrow()
    {
        BacktestRequest request = BuildRequest([
            new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument, Mode = AgentExecutionMode.Executable },
            new StrategyInstrumentAssignment { StrategyType = "improved", Instrument = Instrument, Mode = AgentExecutionMode.Shadow }
        ]);

        Assert.DoesNotThrow(() => request.Validate());
    }

    [Test]
    public void Validate_DefaultMode_IsShadow()
    {
        var assignment = new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument };
        Assert.That(assignment.Mode, Is.EqualTo(AgentExecutionMode.Shadow));
    }

    [Test]
    public async Task RunToCompletionAsync_PerAssignmentAnalysisOptionsOverride_ProducesDistinctFeaturePolicyHash()
    {
        string temp = Path.Combine(Path.GetTempPath(), "th-assignment-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        await using var service = new BacktestApplicationService(new FileSimulationJobRepository(Path.Combine(temp, "jobs")));

        BacktestRequest request = BuildRequest([
            new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument, Id = "default-options" },
            new StrategyInstrumentAssignment
            {
                StrategyType = "legacy",
                Instrument = Instrument,
                Id = "overridden-options",
                AnalysisOptionsOverride = new ChartAnnotationOptions { RsiPeriod = 21 }
            }
        ]) with
        {
            OutputDirectory = Path.Combine(temp, "out"),
            JobsDirectory = Path.Combine(temp, "jobs")
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);

        StrategySimulationResult defaultResult = result.Strategies.Single(s => s.StrategyId == "default-options");
        StrategySimulationResult overriddenResult = result.Strategies.Single(s => s.StrategyId == "overridden-options");

        Assert.Multiple(() =>
        {
            Assert.That(defaultResult.FeaturePolicyHash, Is.Not.Null.And.Not.Empty);
            Assert.That(overriddenResult.FeaturePolicyHash, Is.Not.Null.And.Not.Empty);
            Assert.That(overriddenResult.FeaturePolicyHash, Is.Not.EqualTo(defaultResult.FeaturePolicyHash),
                "An assignment with AnalysisOptionsOverride must resolve to a different feature-policy hash " +
                "than an unmodified assignment sharing the same run.");
        });
    }

    [Test]
    public async Task RunToCompletionAsync_UnmodifiedAssignment_MatchesRunSharedAnnotationOptionsHash()
    {
        string temp = Path.Combine(Path.GetTempPath(), "th-assignment-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        await using var service = new BacktestApplicationService(new FileSimulationJobRepository(Path.Combine(temp, "jobs")));

        // Two assignments, neither overrides AnalysisOptionsOverride - both must resolve to the
        // identical feature-policy hash, proving an unmodified assignment is unaffected by the
        // per-assignment override mechanism existing at all.
        BacktestRequest request = BuildRequest([
            new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = Instrument, Id = "one" },
            new StrategyInstrumentAssignment { StrategyType = "improved", Instrument = Instrument, Id = "two" }
        ]) with
        {
            OutputDirectory = Path.Combine(temp, "out"),
            JobsDirectory = Path.Combine(temp, "jobs")
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);

        StrategySimulationResult one = result.Strategies.Single(s => s.StrategyId == "one");
        StrategySimulationResult two = result.Strategies.Single(s => s.StrategyId == "two");

        Assert.That(one.FeaturePolicyHash, Is.EqualTo(two.FeaturePolicyHash));
    }

    private static Candle[] BuildCandles(InstrumentKey instrument, BarInterval interval, DateTimeOffset start, int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1m;
        for (int i = 0; i < count; i++)
        {
            decimal open = price;
            decimal close = price + (decimal)Math.Sin(i / 11.0) * 0.001m + 0.00001m;
            DateTimeOffset openTime = start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(open, Math.Max(open, close) + 0.0003m, Math.Min(open, close) - 0.0003m, close),
                IsComplete = true
            };
            price = close;
        }
        return candles;
    }
}
