using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.Regime;
using PortfolioManager.CrossMarket;
using QuantResearch.Validation;
using QuantResearch.Training.Mapping;
using RiskManager;
using RiskManager.Conditions;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Proves each of the 14 <see cref="FeatureSwitches"/> flags maps onto the exact real
/// simulator option <see cref="FeatureSwitchMapper"/> claims to control (precise,
/// deterministic option-level assertions), and that the resulting disabled configuration
/// reaches the real engine through <see cref="BacktestApplicationService.RunToCompletionAsync"/>
/// without error - the same completion-proof discipline already established by
/// RegimeEndToEndBacktestTests/EquityProtectionEndToEndBacktestTests in this suite.
/// A stronger "the resulting trades must differ" assertion was attempted and abandoned:
/// reliably producing live trades through the full default-confidence-gated dual-strategy
/// pipeline from synthetic candle data proved disproportionately fragile (every existing
/// RunToCompletionAsync-based test in this suite avoids asserting nonzero trade counts for
/// the same reason), so behavioral proof here rests on the option-level assertions instead.
/// </summary>
[TestFixture]
public sealed class FeatureSwitchMapperTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval BaseInterval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task PriceAction_Disabled_ChangesConfirmationMode()
    {
        BacktestRequest baseline = BaseRequest(BaseRuntime()) with
        {
            PriceActionConfirmation = PriceActionConfirmationMode.Required
        };
        BacktestRequest disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { PriceAction = false });

        Assert.That(disabled.PriceActionConfirmation, Is.EqualTo(PriceActionConfirmationMode.Disabled));
        await AssertBothComplete(baseline, disabled);
    }

    [Test]
    public async Task AdxDmi_Disabled_DisablesDmiConfirmation()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with { DmiConfirmationEnabled = true };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { AdxDmi = false });

        Assert.That(disabled.DmiConfirmationEnabled, Is.False);
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task RsiRelationship_Disabled_Independently_LeavesBollingerUsable()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            RsiBollingerSignals = new RsiBollingerSignalOptions { Enabled = true }
        };
        BacktestRuntimeOptions disabledViaRsi = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { RsiRelationship = false, BollingerContext = true });

        Assert.Multiple(() =>
        {
            Assert.That(disabledViaRsi.RsiBollingerSignals.Enabled, Is.True);
            Assert.That(disabledViaRsi.RsiBollingerSignals.MaximumRsiRelationshipAgeCandles, Is.EqualTo(0));
            Assert.That(disabledViaRsi.RsiBollingerSignals.MinimumBollingerSamples, Is.EqualTo(baseline.RsiBollingerSignals.MinimumBollingerSamples));
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabledViaRsi));
    }

    [Test]
    public async Task BollingerContext_Disabled_Independently_LeavesRsiUsable()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            RsiBollingerSignals = new RsiBollingerSignalOptions { Enabled = true }
        };
        BacktestRuntimeOptions disabledViaBollinger = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { RsiRelationship = true, BollingerContext = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabledViaBollinger.RsiBollingerSignals.Enabled, Is.True);
            Assert.That(disabledViaBollinger.RsiBollingerSignals.MinimumBollingerSamples, Is.EqualTo(int.MaxValue));
            Assert.That(disabledViaBollinger.RsiBollingerSignals.MaximumRsiRelationshipAgeCandles,
                Is.EqualTo(baseline.RsiBollingerSignals.MaximumRsiRelationshipAgeCandles));
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabledViaBollinger));
    }

    [Test]
    public async Task RsiAndBollinger_BothDisabled_DisablesSharedSignalBlock()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            RsiBollingerSignals = new RsiBollingerSignalOptions { Enabled = true }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { RsiRelationship = false, BollingerContext = false });

        Assert.That(disabled.RsiBollingerSignals.Enabled, Is.False);
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task RegimeRouting_Disabled_DisablesRoutingPolicy()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            AnnotationOptions = new ChartAnnotationOptions
            {
                AtrAnalysisMinimumSamples = 10,
                BollingerWidthMinimumSamples = 10,
                EfficiencyRatioAnalysisMinimumSamples = 10,
                MarketRegime = new MarketRegimeOptions
                {
                    Enabled = true,
                    MinimumConfirmationBars = 1,
                    MinimumPersistenceBars = 0,
                    AdxCalibrationMinimumSamples = 10
                }
            },
            MarketRegimeRouting = new MarketRegimePolicyOptions { Enabled = true }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { RegimeRouting = false });

        Assert.That(disabled.MarketRegimeRouting.Enabled, Is.False);
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task SecondaryTrend_Disabled_ClearsIntervalsAndAlignments()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            StrategyTimeframes = BaseRuntime().StrategyTimeframes with
            {
                SecondaryTrendIntervals = [BarInterval.Hours(1)],
                MinimumSecondaryTrendAlignments = 1
            }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { SecondaryTrend = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.StrategyTimeframes.SecondaryTrendIntervals, Is.Empty);
            Assert.That(disabled.StrategyTimeframes.MinimumSecondaryTrendAlignments, Is.Zero);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task SetupIntervals_Disabled_ClearsIntervalsAndAlignments()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            StrategyTimeframes = BaseRuntime().StrategyTimeframes with
            {
                SetupIntervals = [BarInterval.Minutes(30)],
                MinimumSetupAlignments = 1
            }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { SetupIntervals = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.StrategyTimeframes.SetupIntervals, Is.Empty);
            Assert.That(disabled.StrategyTimeframes.MinimumSetupAlignments, Is.Zero);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task CurrencyStrength_Disabled_DisablesBothOptions()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            CurrencyStrength = new CurrencyStrengthOptions
            {
                Enabled = true,
                Interval = BarInterval.Hours(1),
                ReturnLookbackBars = 2,
                VolatilityLookbackBars = 4,
                MinimumCurrencyCoveragePercent = 1m,
                Baskets = new Dictionary<string, IReadOnlyList<InstrumentKey>> { ["Majors"] = [Instrument] }
            },
            CurrencyStrengthEvidence = new CurrencyStrengthEvidenceOptions { Enabled = true }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { CurrencyStrength = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.CurrencyStrength.Enabled, Is.False);
            Assert.That(disabled.CurrencyStrengthEvidence.Enabled, Is.False);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task SessionFilter_Disabled_DisablesTradingConditions()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            TradingConditions = new TradingConditionOptions
            {
                Enabled = true,
                AllowedSessions = [TradingSession.Asian]
            }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { SessionFilter = false });

        Assert.That(disabled.TradingConditions.Enabled, Is.False);
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task ScaleOut_Disabled_DisablesBothStrategyProfiles()
    {
        BacktestRuntimeOptions baseline = BaseRuntime();
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { ScaleOut = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.LegacyPositionManagement.EnableScaleOut, Is.False);
            Assert.That(disabled.ImprovedPositionManagement.EnableScaleOut, Is.False);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task ProfitFloor_Disabled_DisablesBothStrategyProfiles()
    {
        BacktestRuntimeOptions baseline = BaseRuntime();
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { ProfitFloor = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.LegacyPositionManagement.EnableProfitFloor, Is.False);
            Assert.That(disabled.ImprovedPositionManagement.EnableProfitFloor, Is.False);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task MfeGiveback_Disabled_DisablesBothStrategyProfiles()
    {
        BacktestRuntimeOptions baseline = BaseRuntime();
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { MfeGiveback = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.LegacyPositionManagement.EnableMaximumGiveback, Is.False);
            Assert.That(disabled.ImprovedPositionManagement.EnableMaximumGiveback, Is.False);
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task StructuralTrailing_Disabled_SwitchesToBreakEvenOnly()
    {
        BacktestRuntimeOptions baseline = BaseRuntime();
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { StructuralTrailing = false });

        Assert.Multiple(() =>
        {
            Assert.That(disabled.LegacyPositionManagement.Mode, Is.EqualTo(TrailingStopMode.BreakEvenOnly));
            Assert.That(disabled.ImprovedPositionManagement.Mode, Is.EqualTo(TrailingStopMode.BreakEvenOnly));
        });
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    [Test]
    public async Task AdaptiveSizing_Disabled_DisablesAdaptiveRisk()
    {
        BacktestRuntimeOptions baseline = BaseRuntime() with
        {
            AdaptiveRisk = new AdaptiveRiskOptions
            {
                Enabled = true,
                VolatilityPercentileSchedule =
                [
                    new RiskMultiplierBand { FromInclusive = 0m, ToExclusive = null, Multiplier = 0.5m }
                ]
            }
        };
        BacktestRuntimeOptions disabled = FeatureSwitchMapper.Apply(
            baseline, new FeatureSwitches { AdaptiveSizing = false });

        Assert.That(disabled.AdaptiveRisk.Enabled, Is.False);
        await AssertBothComplete(BaseRequest(baseline), BaseRequest(disabled));
    }

    private static BacktestRuntimeOptions BaseRuntime() => new()
    {
        BaseInterval = BaseInterval,
        // Must match RecommendedSimulationDefaults.StrategyTimeframes' required intervals
        // (2h trend, 1h secondary, 30m setup, 15m confirm, 5m entry) - a narrower
        // AnalysisIntervals list silently starves every decision of the snapshots it needs.
        AnalysisIntervals = RecommendedSimulationDefaults.AnalysisIntervals,
        StrategyTimeframes = RecommendedSimulationDefaults.StrategyTimeframes,
        WarmupDays = 0,
        AnnotationOptions = new ChartAnnotationOptions
        {
            AtrAnalysisMinimumSamples = 10,
            BollingerWidthMinimumSamples = 10,
            EfficiencyRatioAnalysisMinimumSamples = 10
        },
        PrefetchCapacity = 10_000,
        PrefetchLowWatermark = 1_000,
        SourcePageSize = 500,
        StrategyExecutionMode = StrategyExecutionMode.Sequential,
        ProgressPublishIntervalMilliseconds = 50,
        ReplayChunkSize = 50
    };

    private static BacktestRequest BaseRequest(BacktestRuntimeOptions runtime) => new()
    {
        Instrument = Instrument,
        From = Start,
        To = Start.AddHours(6),
        Strategies = ["legacy", "improved"],
        StartingBalance = 100_000m,
        Quantity = 1_000m,
        OutputDirectory = TempPath("out"),
        JobsDirectory = TempPath("jobs"),
        InlineCandles = BuildSyntheticTrend(Instrument, BaseInterval, Start, count: 400),
        Runtime = runtime
    };

    private static string TempPath(string leaf) =>
        Path.Combine(Path.GetTempPath(), "tradinghub-sim-tests", Guid.NewGuid().ToString("N"), leaf);

    /// <summary>
    /// Runs both the baseline and the switch-disabled request through the real application
    /// service and asserts each completes cleanly - proof the disabled configuration reaches
    /// production code (CreateDefaultStrategies, StreamingComparativeEngine) without error.
    /// </summary>
    private static async Task AssertBothComplete(BacktestRequest baseline, BacktestRequest disabled)
    {
        ComparativeSimulationResult baselineResult = await RunAsync(baseline);
        ComparativeSimulationResult disabledResult = await RunAsync(disabled);

        Assert.Multiple(() =>
        {
            Assert.That(baselineResult.ProcessedBaseCandles, Is.GreaterThan(0));
            Assert.That(baselineResult.Strategies, Has.Count.EqualTo(2));
            Assert.That(File.Exists(Path.Combine(baselineResult.OutputDirectory, "COMPLETE")), Is.True);
            Assert.That(disabledResult.ProcessedBaseCandles, Is.GreaterThan(0));
            Assert.That(disabledResult.Strategies, Has.Count.EqualTo(2));
            Assert.That(File.Exists(Path.Combine(disabledResult.OutputDirectory, "COMPLETE")), Is.True);
        });
    }

    private static async Task<ComparativeSimulationResult> RunAsync(BacktestRequest request)
    {
        await using var service = new BacktestApplicationService(
            new FileSimulationJobRepository(request.JobsDirectory),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });
        return await service.RunToCompletionAsync(request);
    }

    private static Candle[] BuildSyntheticTrend(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset start,
        int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1000m;
        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i / 17.0) * 0.0015m;
            decimal drift = i * 0.00001m;
            decimal open = price;
            decimal close = price + wave + drift + ((i % 23 == 0) ? 0.002m : 0m);
            decimal high = Math.Max(open, close) + 0.0004m;
            decimal low = Math.Min(open, close) - 0.0004m;
            DateTimeOffset openTime = start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(open, high, low, close),
                Volume = new MarketVolume(100 + i, VolumeKind.Unknown),
                IsComplete = true
            };
            price = close;
        }

        return candles;
    }
}
