using System.Globalization;
using Agent.Strategies;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using RiskManager;
using RiskManager.Safety;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;
using ChartAnnotator.Regime;
using RiskManager.Conditions;
using PortfolioManager.Risk;
using Simulator.Execution;
using Simulator.Financing;

namespace BacktestRunner;

internal sealed record BacktestCommandOptions
{
    public InstrumentKey Instrument { get; init; } = new(RecommendedSimulationDefaults.Instrument);
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public BarInterval ExecutionInterval { get; init; } = BarInterval.Minutes(1);
    public BarInterval AnalysisBaseInterval { get; init; } = BarInterval.Minutes(1);
    public IReadOnlyList<BarInterval> AnalysisIntervals { get; init; } =
        RecommendedSimulationDefaults.AnalysisIntervals;
    public SimulationPrecisionMode PrecisionMode { get; init; } = SimulationPrecisionMode.Fast;
    public HistoricalDataSourceKind SourceKind { get; init; } = HistoricalDataSourceKind.OandaCandles;
    public string? ImportedCandlePath { get; init; }
    public BarInterval TrendInterval { get; init; } = BarInterval.Hours(2);
    public IReadOnlyList<BarInterval> SecondaryTrendIntervals { get; init; } = [BarInterval.Hours(1)];
    public IReadOnlyList<BarInterval> SetupIntervals { get; init; } = [BarInterval.Minutes(30)];
    public BarInterval ConfirmationInterval { get; init; } = BarInterval.Minutes(15);
    public IReadOnlyList<BarInterval> AdditionalConfirmationIntervals { get; init; } = [];
    public BarInterval EntryInterval { get; init; } = BarInterval.Minutes(5);
    public int MinimumSecondaryTrendAlignments { get; init; }
    public int MinimumSetupAlignments { get; init; } = 1;
    public int MinimumConfirmationAlignments { get; init; } = 1;
    public bool StrongOppositionVeto { get; init; } = true;
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public string OutputDirectory { get; init; } = Path.Combine("Dashboard", "public", "data", "backtests");
    public string CacheDirectory { get; init; } = Path.Combine(".cache", "oanda");
    public string JobsDirectory { get; init; } = Path.Combine(".cache", "simulation-jobs");
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    public int PageSize { get; init; } = 5_000;
    public decimal StartingBalance { get; init; } = 100_000m;
    public string? BaseCurrency { get; init; }
    public decimal Quantity { get; init; } = 1_000m;
    public PositionSizingOptions PositionSizing { get; init; } =
        RecommendedSimulationDefaults.PositionSizing;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public decimal? DailyEquityProfitTarget { get; init; }
    public decimal? DailyEquityGivebackActivation { get; init; }
    public decimal? MaximumDailyEquityGiveback { get; init; }
    /// <summary>
    /// Persistent (non-daily-resetting) equity-protection tier. Programmatic/JSON callers
    /// wanting more than one tier should set BacktestRuntimeOptions.SafetyOptions.EquityProtection
    /// directly; these scalar fields configure a single simple tier.
    /// </summary>
    public bool EquityProtectionEnabled { get; init; }
    public decimal? EquityProtectionActivationProfitPercent { get; init; }
    public decimal? EquityProtectionMaxGivebackPercent { get; init; }
    public string EquityProtectionActionType { get; init; } = "PauseNewEntries";
    public decimal EquityProtectionReductionFraction { get; init; } = 0.25m;
    public decimal EquityProtectionFutureRiskMultiplier { get; init; } = 0.5m;
    public int EquityProtectionRecoveryBars { get; init; } = 3;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;
    public int WarmupDays { get; init; } = RecommendedSimulationDefaults.WarmupDays;
    public int ProgressIntervalMs { get; init; } = 500;
    public IReadOnlyList<string> Strategies { get; init; } = ["legacy", "improved"];
    public string StrategyExecution { get; init; } = "parallel";
    public int StrategyChannelCapacity { get; init; } = 4;
    public int MaxParallelStrategies { get; init; } = 4;
    public string StrategyFailurePolicy { get; init; } = "stop-all";
    public string AnalysisSharing { get; init; } = "shared";
    public string AmbiguousPolicy { get; init; } = "stop-first";
    public bool TrailingComparison { get; init; }
    public PositionManagementOptions LegacyPositionManagement { get; init; } =
        PositionManagementOptions.LegacyDefaults;
    public PositionManagementOptions ImprovedPositionManagement { get; init; } =
        PositionManagementOptions.ImprovedDefaults;
    /// <summary>
    /// ChartAnnotator indicator/regime-classification configuration. Programmatic/JSON
    /// callers may set this directly; no individual --flag parsing yet.
    /// </summary>
    public ChartAnnotationOptions AnnotationOptions { get; init; } = new();
    /// <summary>
    /// Regime-based strategy routing configuration. Programmatic/JSON callers may set
    /// this directly; no individual --flag parsing yet.
    /// </summary>
    public MarketRegimePolicyOptions MarketRegimeRouting { get; init; } = new();
    public SimulationAccountMode AccountMode { get; init; } = SimulationAccountMode.IndependentStrategyAccounts;
    public bool RegimeEnabled { get; init; }
    public int EfficiencyRatioPeriod { get; init; } = 14;
    public bool TradingConditionsEnabled { get; init; }
    public bool AdaptiveRiskEnabled { get; init; }
    public decimal MaximumPortfolioHeatPercent { get; init; } = 1.5m;
    public SimulationFillModel ExecutionFillModel { get; init; } = SimulationFillModel.MidpointPlusConfiguredSpread;
    public StressExecutionScenario StressScenario { get; init; } = StressExecutionScenario.Base;
    public decimal? MaximumFillQuantityPerFrame { get; init; }
    public bool FinancingEnabled { get; init; }
    public decimal FinancingLongAnnualPercent { get; init; }
    public decimal FinancingShortAnnualPercent { get; init; }
    public bool ShowHelp { get; init; }

    public static BacktestCommandOptions Parse(string[] args)
    {
        (DateTimeOffset defaultFrom, DateTimeOffset defaultTo) =
            RecommendedSimulationDefaults.PreviousFullMonth(DateTimeOffset.UtcNow);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (token is "-h" or "--help")
            {
                values["help"] = "true";
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{token}'. Use --help for usage.");
            }

            string key = token[2..];
            if (key is "refresh" or "no-cache" or "full-chart-history" or "trailing-comparison" or
                "allow-opposing-price-action" or "allow-timeframe-opposition" or
                "legacy-disable-mechanical-protection" or "improved-disable-mechanical-protection" or
                "legacy-no-scale-out" or "improved-no-scale-out" or
                "legacy-no-profit-floor" or "improved-no-profit-floor" or
                "legacy-no-giveback" or "improved-no-giveback" or
                "legacy-no-stagnation-reduction" or "improved-no-stagnation-reduction" or
                "legacy-no-structure-reduction" or "improved-no-structure-reduction" or
                "legacy-no-momentum-reduction" or "improved-no-momentum-reduction" or
                "legacy-no-volatility-reduction" or "improved-no-volatility-reduction" or
                "legacy-enable-cost-stress-reduction" or "improved-enable-cost-stress-reduction" or
                "regime" or "trading-conditions" or "adaptive-risk" or "financing")
            {
                values[key] = "true";
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argument --{key} requires a value.");
            }

            values[key] = args[++index];
        }

        DateTimeOffset to = ParseDate(values.GetValueOrDefault("to"), defaultTo);
        DateTimeOffset from = ParseDate(
            values.GetValueOrDefault("from"),
            values.ContainsKey("to") ? to.AddMonths(-1) : defaultFrom);
        if (from >= to)
        {
            throw new ArgumentException("--from must be earlier than --to.");
        }

        SimulationPrecisionMode precision = ParsePrecisionMode(values.GetValueOrDefault("precision-mode"));
        SimulationTimeframeOptions precisionDefaults = SimulationTimeframeOptions.FromPrecision(precision);
        string? intervalRaw = values.GetValueOrDefault("execution-interval")
            ?? values.GetValueOrDefault("base-interval");
        BarInterval execution = intervalRaw is null
            ? precisionDefaults.ExecutionInterval
            : ParseInterval(intervalRaw);
        BarInterval analysisBase = ParseInterval(
            values.GetValueOrDefault("analysis-base-interval") ??
            BarIntervalParser.Format(precisionDefaults.AnalysisBaseInterval));
        BarInterval[] analysis = (values.GetValueOrDefault("analysis-intervals") ?? "5m,15m,30m,1h,2h")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseInterval)
            .Distinct()
            .ToArray();
        if (analysis.Length == 0)
        {
            throw new ArgumentException("At least one analysis interval is required.");
        }

        HistoricalDataSourceKind source = ParseSourceKind(
            values.GetValueOrDefault("source"),
            precision);
        string? importedPath = values.GetValueOrDefault("imported-candles");
        if (!string.IsNullOrWhiteSpace(importedPath))
            importedPath = ResolvePath(importedPath);

        string[] strategies = (values.GetValueOrDefault("strategies")
                ?? values.GetValueOrDefault("strategy")
                ?? "legacy,improved")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        BarInterval entryInterval = ParseInterval(values.GetValueOrDefault("entry-interval") ?? "5m");
        BarInterval[] secondaryTrendIntervals = ParseIntervalList(
            values.GetValueOrDefault("secondary-trend-intervals") ?? "1h");
        BarInterval[] setupIntervals = ParseIntervalList(
            values.GetValueOrDefault("setup-intervals") ?? "30m");
        BarInterval[] additionalConfirmationIntervals = ParseIntervalList(
            values.GetValueOrDefault("additional-confirmation-intervals"));
        int minimumSecondaryAlignments = ParseInt(
            values.GetValueOrDefault("minimum-secondary-alignments"),
            0,
            0,
            secondaryTrendIntervals.Length,
            "minimum-secondary-alignments");
        int minimumSetupAlignments = ParseInt(
            values.GetValueOrDefault("minimum-setup-alignments"),
            setupIntervals.Length == 0 ? 0 : 1,
            0,
            setupIntervals.Length,
            "minimum-setup-alignments");
        int minimumConfirmationAlignments = ParseInt(
            values.GetValueOrDefault("minimum-confirmation-alignments"),
            1,
            1,
            1 + additionalConfirmationIntervals.Length,
            "minimum-confirmation-alignments");
        decimal leverage = ParseDecimal(values.GetValueOrDefault("leverage"), 20m, 0m, "leverage");
        decimal quantity = ParseDecimal(values.GetValueOrDefault("quantity"), 1_000m, 0m, "quantity");
        PositionSizingOptions positionSizing = ParsePositionSizing(values, quantity, leverage);
        PositionManagementOptions legacyManagement = ParsePositionManagement(
            values,
            "legacy",
            entryInterval,
            structureDefault: 1.5m,
            preserveTarget: false);
        PositionManagementOptions improvedManagement = ParsePositionManagement(
            values,
            "improved",
            entryInterval,
            structureDefault: 2m,
            preserveTarget: true);

        return new BacktestCommandOptions
        {
            ShowHelp = values.ContainsKey("help"),
            Instrument = new InstrumentKey(
                values.GetValueOrDefault("instrument") ?? RecommendedSimulationDefaults.Instrument),
            From = from,
            To = to,
            ExecutionInterval = execution,
            AnalysisBaseInterval = analysisBase,
            AnalysisIntervals = analysis,
            PrecisionMode = precision,
            SourceKind = source,
            ImportedCandlePath = importedPath,
            TrendInterval = ParseInterval(values.GetValueOrDefault("trend-interval") ?? "2h"),
            SecondaryTrendIntervals = secondaryTrendIntervals,
            SetupIntervals = setupIntervals,
            ConfirmationInterval = ParseInterval(values.GetValueOrDefault("confirmation-interval") ?? "15m"),
            AdditionalConfirmationIntervals = additionalConfirmationIntervals,
            EntryInterval = entryInterval,
            MinimumSecondaryTrendAlignments = minimumSecondaryAlignments,
            MinimumSetupAlignments = minimumSetupAlignments,
            MinimumConfirmationAlignments = minimumConfirmationAlignments,
            StrongOppositionVeto = !values.ContainsKey("allow-timeframe-opposition"),
            Environment = source == HistoricalDataSourceKind.BinanceCandles
                ? BrokerEnvironment.Live
                : ParseEnvironment(values.GetValueOrDefault("environment")),
            OutputDirectory = ResolvePath(
                values.GetValueOrDefault("output") ?? Path.Combine("Dashboard", "public", "data", "backtests")),
            CacheDirectory = ResolvePath(
                values.GetValueOrDefault("cache") ?? Path.Combine(".cache", "historical")),
            JobsDirectory = ResolvePath(
                values.GetValueOrDefault("jobs") ?? Path.Combine(".cache", "simulation-jobs")),
            RefreshCache = values.ContainsKey("refresh"),
            NoCache = values.ContainsKey("no-cache"),
            PageSize = ParseInt(values.GetValueOrDefault("page-size"), 5_000, 1, 5_000, "page-size"),
            StartingBalance = ParseDecimal(values.GetValueOrDefault("starting-balance"), 100_000m, 0m, "starting-balance"),
            BaseCurrency = values.GetValueOrDefault("base-currency")?.Trim().ToUpperInvariant(),
            Quantity = quantity,
            PositionSizing = positionSizing,
            Leverage = leverage,
            CommissionRate = ParseDecimal(values.GetValueOrDefault("commission-rate"), 0.00002m, -1m, "commission-rate", allowZero: true),
            SpreadBasisPoints = ParseDecimal(values.GetValueOrDefault("spread-bps"), 1m, -1m, "spread-bps", allowZero: true),
            SlippageBasisPoints = ParseDecimal(values.GetValueOrDefault("slippage-bps"), 0.5m, -1m, "slippage-bps", allowZero: true),
            MinimumRewardRisk = ParseDecimal(values.GetValueOrDefault("minimum-rr"), 1.5m, 0m, "minimum-rr"),
            DailyEquityProfitTarget = ParseOptionalPositiveDecimal(
                values.GetValueOrDefault("daily-equity-profit-target"),
                "daily-equity-profit-target"),
            DailyEquityGivebackActivation = ParseOptionalPositiveDecimal(
                values.GetValueOrDefault("daily-equity-giveback-activation"),
                "daily-equity-giveback-activation"),
            MaximumDailyEquityGiveback = ParseOptionalPositiveDecimal(
                values.GetValueOrDefault("maximum-daily-equity-giveback"),
                "maximum-daily-equity-giveback"),
            PriceActionConfirmation = ParsePriceActionConfirmation(
                values.GetValueOrDefault("price-action-mode")),
            MinimumPriceActionConfidence = ParseDecimal(
                values.GetValueOrDefault("minimum-price-action-confidence"),
                55m,
                -1m,
                "minimum-price-action-confidence",
                allowZero: true),
            RejectStrongOpposingPriceAction = !values.ContainsKey("allow-opposing-price-action"),
            WarmupDays = ParseInt(
                values.GetValueOrDefault("warmup-days"),
                RecommendedSimulationDefaults.WarmupDays,
                0,
                3650,
                "warmup-days"),
            ProgressIntervalMs = ParseInt(values.GetValueOrDefault("progress-interval"), 500, 50, 60_000, "progress-interval"),
            Strategies = strategies,
            StrategyExecution = values.GetValueOrDefault("strategy-execution") ?? "parallel",
            StrategyChannelCapacity = ParseInt(values.GetValueOrDefault("strategy-channel-capacity"), 4, 1, 64, "strategy-channel-capacity"),
            MaxParallelStrategies = ParseInt(values.GetValueOrDefault("max-parallel-strategies"), 4, 1, 32, "max-parallel-strategies"),
            StrategyFailurePolicy = values.GetValueOrDefault("strategy-failure-policy") ?? "stop-all",
            AnalysisSharing = values.GetValueOrDefault("analysis-sharing") ?? "shared",
            AmbiguousPolicy = values.GetValueOrDefault("ambiguous-policy") ?? "stop-first",
            TrailingComparison = values.ContainsKey("trailing-comparison"),
            LegacyPositionManagement = legacyManagement,
            ImprovedPositionManagement = improvedManagement
            ,AccountMode = ParseAccountMode(values.GetValueOrDefault("account-mode"))
            ,RegimeEnabled = values.ContainsKey("regime")
            ,EfficiencyRatioPeriod = ParseInt(values.GetValueOrDefault("er-period"), 14, 2, 10_000, "er-period")
            ,TradingConditionsEnabled = values.ContainsKey("trading-conditions")
            ,AdaptiveRiskEnabled = values.ContainsKey("adaptive-risk")
            ,MaximumPortfolioHeatPercent = ParseDecimal(values.GetValueOrDefault("maximum-portfolio-heat-percent"), 1.5m, 0m, "maximum-portfolio-heat-percent")
            ,ExecutionFillModel = Enum.Parse<SimulationFillModel>(values.GetValueOrDefault("fill-model") ?? "MidpointPlusConfiguredSpread", true)
            ,StressScenario = Enum.Parse<StressExecutionScenario>(values.GetValueOrDefault("stress-scenario") ?? "Base", true)
            ,MaximumFillQuantityPerFrame = ParseOptionalPositiveDecimal(values.GetValueOrDefault("fill-capacity"), "fill-capacity")
            ,FinancingEnabled = values.ContainsKey("financing")
            ,FinancingLongAnnualPercent = ParseSignedDecimal(values.GetValueOrDefault("financing-long-annual-percent"), 0m, "financing-long-annual-percent")
            ,FinancingShortAnnualPercent = ParseSignedDecimal(values.GetValueOrDefault("financing-short-annual-percent"), 0m, "financing-short-annual-percent")
        };
    }

    public BacktestRequest ToBacktestRequest() => new()
    {
        Instrument = Instrument,
        From = From,
        To = To,
        Environment = Environment,
        Strategies = Strategies,
        StartingBalance = StartingBalance,
        BaseCurrency = BaseCurrency ?? ResolveBaseCurrency(),
        Quantity = Quantity,
        Leverage = Leverage,
        CommissionRate = CommissionRate,
        SpreadBasisPoints = SpreadBasisPoints,
        SlippageBasisPoints = SlippageBasisPoints,
        MinimumRewardRisk = MinimumRewardRisk,
        PriceActionConfirmation = PriceActionConfirmation,
        MinimumPriceActionConfidence = MinimumPriceActionConfidence,
        RejectStrongOpposingPriceAction = RejectStrongOpposingPriceAction,
        OutputDirectory = Path.Combine(OutputDirectory, "simulations"),
        CacheDirectory = CacheDirectory,
        JobsDirectory = JobsDirectory,
        Runtime = new BacktestRuntimeOptions
        {
            AccountMode = AccountMode,
            ExecutionInterval = ExecutionInterval,
            AnalysisBaseInterval = AnalysisBaseInterval,
            AnalysisIntervals = AnalysisIntervals,
            PrecisionMode = PrecisionMode,
            SourceKind = SourceKind,
            ImportedCandlePath = ImportedCandlePath,
            StrategyTimeframes = new ProgressiveStrategyTimeframes
            {
                TrendInterval = TrendInterval,
                SecondaryTrendIntervals = SecondaryTrendIntervals,
                SetupIntervals = SetupIntervals,
                ConfirmationInterval = ConfirmationInterval,
                AdditionalConfirmationIntervals = AdditionalConfirmationIntervals,
                EntryInterval = EntryInterval,
                MinimumSecondaryTrendAlignments = MinimumSecondaryTrendAlignments,
                MinimumSetupAlignments = MinimumSetupAlignments,
                MinimumConfirmationAlignments = MinimumConfirmationAlignments,
                StrongOppositionVeto = StrongOppositionVeto
            },
            SourcePageSize = PageSize,
            PrefetchCapacity = Math.Max(PageSize * 4, 20_000),
            PrefetchLowWatermark = Math.Max(PageSize, 5_000),
            WarmupDays = WarmupDays,
            StrategyExecutionMode = ParseExecutionMode(StrategyExecution),
            StrategyChannelCapacity = StrategyChannelCapacity,
            MaximumParallelStrategies = MaxParallelStrategies,
            StrategyFailurePolicy = ParseFailurePolicy(StrategyFailurePolicy),
            AnalysisSharingMode = ParseAnalysisSharing(AnalysisSharing),
            AmbiguousIntrabarPolicy = ParseAmbiguousPolicy(AmbiguousPolicy),
            RefreshCache = RefreshCache,
            NoCache = NoCache,
            LegacyPositionManagement = LegacyPositionManagement,
            ImprovedPositionManagement = ImprovedPositionManagement,
            PositionSizing = PositionSizing,
            AnnotationOptions = AnnotationOptions with
            {
                EfficiencyRatioPeriod = EfficiencyRatioPeriod,
                MarketRegime = AnnotationOptions.MarketRegime with { Enabled = RegimeEnabled }
            },
            MarketRegimeRouting = MarketRegimeRouting with { Enabled = RegimeEnabled },
            RegimeManagement = new RegimeManagementOptions { Enabled = RegimeEnabled },
            TradingConditions = new TradingConditionOptions { Enabled = TradingConditionsEnabled },
            AdaptiveRisk = new AdaptiveRiskOptions { Enabled = AdaptiveRiskEnabled },
            PortfolioRisk = new PortfolioRiskOptions
            {
                MaximumTotalOpenRiskPercent = MaximumPortfolioHeatPercent,
                MaximumPendingRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumStrategyRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumInstrumentRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumCurrencyRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent)
            },
            Execution = new ExecutionModelOptions
            {
                FillModel = ExecutionFillModel,
                StressScenario = StressScenario,
                FillCapacity = new FillCapacityModel
                {
                    MaximumQuantityPerExecutionFrame = MaximumFillQuantityPerFrame
                }
            },
            Financing = new FinancingOptions
            {
                Enabled = FinancingEnabled,
                InstrumentRates = FinancingEnabled
                    ? new Dictionary<string, FinancingRate>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Instrument.Value] = new FinancingRate
                        {
                            LongAnnualPercent = FinancingLongAnnualPercent,
                            ShortAnnualPercent = FinancingShortAnnualPercent
                        }
                    }
                    : new Dictionary<string, FinancingRate>(StringComparer.OrdinalIgnoreCase)
            },
            SafetyOptions = new TradingSafetyOptions
            {
                DailyEquityProfitTarget = DailyEquityProfitTarget,
                DailyEquityGivebackActivation = DailyEquityGivebackActivation,
                MaximumDailyEquityGiveback = MaximumDailyEquityGiveback,
                EquityProtection = ResolveEquityProtection()
            },
            ProgressPublishIntervalMilliseconds = ProgressIntervalMs
        }
    };

    private EquityProtectionOptions ResolveEquityProtection()
    {
        if (!EquityProtectionEnabled || EquityProtectionMaxGivebackPercent is not decimal givebackPercent)
        {
            return new EquityProtectionOptions { Enabled = EquityProtectionEnabled };
        }

        var action = Enum.Parse<EquityProtectionAction>(EquityProtectionActionType, ignoreCase: true);
        return new EquityProtectionOptions
        {
            Enabled = true,
            RecoveryConfirmationBars = EquityProtectionRecoveryBars,
            Tiers =
            [
                new EquityProtectionTier
                {
                    TierId = "cli-tier",
                    ActivationProfitPercent = EquityProtectionActivationProfitPercent,
                    MaximumGivebackPercent = givebackPercent,
                    Action = action,
                    ReductionFraction = EquityProtectionReductionFraction,
                    FutureRiskMultiplier = EquityProtectionFutureRiskMultiplier
                }
            ]
        };
    }

    public static BarInterval ParseInterval(string value) => BarIntervalParser.Parse(value);

    public static string FormatInterval(BarInterval interval) => BarIntervalParser.Format(interval);


    private static decimal? ParseOptionalPositiveDecimal(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        decimal parsed = ParseDecimal(value, 0m, -1m, name, allowZero: true);
        return parsed == 0m ? null : parsed;
    }

    private static PriceActionConfirmationMode ParsePriceActionConfirmation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "soft" => PriceActionConfirmationMode.Soft,
            "disabled" or "off" => PriceActionConfirmationMode.Disabled,
            "required" or "strict" => PriceActionConfirmationMode.Required,
            "required-with-context" or "context" or "requiredwithcontext" or "mtf" =>
                PriceActionConfirmationMode.RequiredWithContext,
            _ => throw new ArgumentException(
                $"Unknown --price-action-mode '{value}'. Use disabled, soft, required, or required-with-context.")
        };

    private static SimulationPrecisionMode ParsePrecisionMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "fast" => SimulationPrecisionMode.Fast,
            "broker" or "broker-native" or "brokernativeprecision" or "5s" =>
                SimulationPrecisionMode.BrokerNativePrecision,
            "high" or "high-precision" or "highprecision" or "1s" =>
                SimulationPrecisionMode.HighPrecision,
            _ => throw new ArgumentException(
                $"Unknown --precision-mode '{value}'. Use fast, broker-native, or high-precision.")
        };

    private static HistoricalDataSourceKind ParseSourceKind(
        string? value,
        SimulationPrecisionMode precision)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return precision == SimulationPrecisionMode.HighPrecision
                ? HistoricalDataSourceKind.ImportedSecondCandles
                : HistoricalDataSourceKind.OandaCandles;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "oanda" or "oandacandles" => HistoricalDataSourceKind.OandaCandles,
            "binance" or "binancecandles" => HistoricalDataSourceKind.BinanceCandles,
            "imported" or "importedsecondcandles" => HistoricalDataSourceKind.ImportedSecondCandles,
            _ => throw new ArgumentException(
                $"Unknown --source '{value}'. Use oanda, binance, or imported.")
        };
    }

    public string ResolveBaseCurrency()
    {
        if (!string.IsNullOrWhiteSpace(BaseCurrency))
        {
            return BaseCurrency;
        }

        string pair = Instrument.Value[(Instrument.Value.IndexOf(':') + 1)..];
        int slash = pair.LastIndexOf('/');
        return slash >= 0 && slash + 1 < pair.Length ? pair[(slash + 1)..] : "USD";
    }

    private static Simulator.Models.StrategyExecutionMode ParseExecutionMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "sequential" or "seq" => Simulator.Models.StrategyExecutionMode.Sequential,
            "parallel" or "parallelworkers" or "workers" => Simulator.Models.StrategyExecutionMode.ParallelWorkers,
            _ => throw new ArgumentException($"Unknown --strategy-execution '{value}'. Use sequential or parallel.")
        };

    private static Simulator.Models.StrategyFailurePolicy ParseFailurePolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stop-all" or "stopentirecomparison" or "all" => Simulator.Models.StrategyFailurePolicy.StopEntireComparison,
            "stop-one" or "stopfailedstrategyonly" or "one" => Simulator.Models.StrategyFailurePolicy.StopFailedStrategyOnly,
            _ => throw new ArgumentException($"Unknown --strategy-failure-policy '{value}'.")
        };

    private static Simulator.Models.AnalysisSharingMode ParseAnalysisSharing(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "shared" or "sharedimmutablesnapshots" => Simulator.Models.AnalysisSharingMode.SharedImmutableSnapshots,
            "independent" or "independentperstrategy" => Simulator.Models.AnalysisSharingMode.IndependentPerStrategy,
            _ => throw new ArgumentException($"Unknown --analysis-sharing '{value}'.")
        };

    private static Simulator.Models.AmbiguousIntrabarPolicy ParseAmbiguousPolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stop-first" or "conservative" or "conservativestopfirst" => Simulator.Models.AmbiguousIntrabarPolicy.ConservativeStopFirst,
            "target-first" or "optimistic" or "optimistictargetfirst" => Simulator.Models.AmbiguousIntrabarPolicy.OptimisticTargetFirst,
            "nearest-open" or "nearest" or "nearesttoopenfirst" => Simulator.Models.AmbiguousIntrabarPolicy.NearestToOpenFirst,
            _ => throw new ArgumentException($"Unknown --ambiguous-policy '{value}'.")
        };

    private static BarInterval[] ParseIntervalList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseInterval)
                .Distinct()
                .ToArray();

    private static PositionSizingOptions ParsePositionSizing(
        IReadOnlyDictionary<string, string?> values,
        decimal quantity,
        decimal leverage)
    {
        PositionSizingMode mode = values.GetValueOrDefault("position-sizing-mode")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "fixed-fractional" or "fixedfractionalrisk" or "fractional" =>
                PositionSizingMode.FixedFractionalRisk,
            "fixed-cash" or "fixedcashrisk" or "cash" => PositionSizingMode.FixedCashRisk,
            "fixed-quantity" or "fixedquantity" or "quantity" => PositionSizingMode.FixedQuantity,
            string invalid => throw new ArgumentException(
                $"Unknown --position-sizing-mode '{invalid}'. Use fixed-fractional, fixed-cash, or fixed-quantity.")
        };

        decimal maximumQuantity = ParseDecimal(
            values.GetValueOrDefault("maximum-quantity"),
            0m,
            -1m,
            "maximum-quantity",
            allowZero: true);
        var result = new PositionSizingOptions
        {
            Mode = mode,
            FixedQuantity = quantity,
            FixedCashRisk = ParseDecimal(
                values.GetValueOrDefault("fixed-cash-risk"), 250m, 0m, "fixed-cash-risk"),
            RiskPercentOfEquity = ParseDecimal(
                values.GetValueOrDefault("risk-percent"), 0.5m, 0m, "risk-percent"),
            MinimumQuantity = ParseDecimal(
                values.GetValueOrDefault("minimum-quantity"), 1m, 0m, "minimum-quantity"),
            MaximumQuantity = maximumQuantity == 0m ? null : maximumQuantity,
            QuantityStep = ParseDecimal(
                values.GetValueOrDefault("quantity-step"), 1m, 0m, "quantity-step"),
            MaximumAccountMarginUsagePercent = ParseDecimal(
                values.GetValueOrDefault("maximum-account-margin-percent"),
                30m,
                0m,
                "maximum-account-margin-percent"),
            MaximumSinglePositionMarginPercent = ParseDecimal(
                values.GetValueOrDefault("maximum-position-margin-percent"),
                10m,
                0m,
                "maximum-position-margin-percent"),
            Leverage = leverage,
            EstimatedRoundTripCostBasisPoints =
                ParseDecimal(values.GetValueOrDefault("spread-bps"), 1m, -1m, "spread-bps", allowZero: true) +
                2m * ParseDecimal(values.GetValueOrDefault("slippage-bps"), 0.5m, -1m, "slippage-bps", allowZero: true) +
                2m * ParseDecimal(values.GetValueOrDefault("commission-rate"), 0.00002m, -1m, "commission-rate", allowZero: true) * 10_000m
        };
        result.Validate();
        if (result.MaximumSinglePositionMarginPercent > result.MaximumAccountMarginUsagePercent)
        {
            throw new ArgumentException(
                "Maximum one-position margin percentage cannot exceed the account margin percentage.");
        }
        return result;
    }

    private static PositionManagementOptions ParsePositionManagement(
        IReadOnlyDictionary<string, string?> values,
        string prefix,
        BarInterval defaultInterval,
        decimal structureDefault,
        bool preserveTarget)
    {
        string modeValue = values.GetValueOrDefault($"{prefix}-trailing-mode") ?? "structure-atr";
        TrailingStopMode mode = modeValue.Trim().ToLowerInvariant() switch
        {
            "disabled" or "off" => TrailingStopMode.Disabled,
            "break-even" or "breakeven" or "break-even-only" => TrailingStopMode.BreakEvenOnly,
            "structure" or "structure-atr" or "structureatr" => TrailingStopMode.StructureAtr,
            _ => throw new ArgumentException(
                $"Unknown --{prefix}-trailing-mode '{modeValue}'. Use disabled, break-even, or structure-atr.")
        };
        decimal breakEven = ParseDecimal(
            values.GetValueOrDefault($"{prefix}-break-even-r"), 1m, 0m, $"{prefix}-break-even-r");
        decimal structure = ParseDecimal(
            values.GetValueOrDefault($"{prefix}-structure-r"), structureDefault, 0m, $"{prefix}-structure-r");
        decimal atrBuffer = ParseDecimal(
            values.GetValueOrDefault($"{prefix}-atr-buffer"), 0.25m, -1m, $"{prefix}-atr-buffer", allowZero: true);
        PositionManagementOptions defaults = preserveTarget
            ? PositionManagementOptions.ImprovedDefaults
            : PositionManagementOptions.LegacyDefaults;
        PositionManagementOptions result = defaults with
        {
            Mode = mode,
            ManagementInterval = ParseInterval(
                values.GetValueOrDefault($"{prefix}-management-interval") ??
                BarIntervalParser.Format(defaults.MainStructureInterval ?? defaultInterval)),
            EvaluateMechanicalProtectionOnEveryExecutionFrame =
                !values.ContainsKey($"{prefix}-disable-mechanical-protection"),
            FastStructureInterval = ParseInterval(
                values.GetValueOrDefault($"{prefix}-fast-management-interval") ??
                BarIntervalParser.Format(defaults.FastStructureInterval ?? defaultInterval)),
            MainStructureInterval = ParseInterval(
                values.GetValueOrDefault($"{prefix}-main-management-interval") ??
                values.GetValueOrDefault($"{prefix}-management-interval") ??
                BarIntervalParser.Format(defaults.MainStructureInterval ?? defaultInterval)),
            ThesisInterval = ParseInterval(
                values.GetValueOrDefault($"{prefix}-thesis-management-interval") ??
                BarIntervalParser.Format(defaults.ThesisInterval ?? BarInterval.Hours(1))),
            BreakEvenActivationR = breakEven,
            StructureTrailActivationR = structure,
            AtrBufferMultiplier = atrBuffer,
            PreserveBracketTarget = preserveTarget,
            EnableScaleOut = !values.ContainsKey($"{prefix}-no-scale-out"),
            EnableProfitFloor = !values.ContainsKey($"{prefix}-no-profit-floor"),
            EnableMaximumGiveback = !values.ContainsKey($"{prefix}-no-giveback"),
            EnableStagnationReduction = !values.ContainsKey($"{prefix}-no-stagnation-reduction"),
            EnableStructuralDeteriorationReduction =
                !values.ContainsKey($"{prefix}-no-structure-reduction"),
            EnableMomentumDecayReduction =
                !values.ContainsKey($"{prefix}-no-momentum-reduction"),
            EnableVolatilityExhaustionReduction =
                !values.ContainsKey($"{prefix}-no-volatility-reduction"),
            EnableRiskWindowReduction =
                values.ContainsKey($"{prefix}-risk-window-start") ||
                values.ContainsKey($"{prefix}-risk-window-end"),
            RiskWindowStartUtc = ParseOptionalTime(
                values.GetValueOrDefault($"{prefix}-risk-window-start")),
            RiskWindowEndUtc = ParseOptionalTime(
                values.GetValueOrDefault($"{prefix}-risk-window-end")),
            EnableExecutionCostStressReduction =
                values.ContainsKey($"{prefix}-enable-cost-stress-reduction"),
            MinimumRunnerFraction = ParseDecimal(
                values.GetValueOrDefault($"{prefix}-minimum-runner"),
                defaults.MinimumRunnerFraction,
                -1m,
                $"{prefix}-minimum-runner")
        };
        result.Validate();
        return result;
    }

    private static TimeOnly? ParseOptionalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed))
            throw new ArgumentException($"UTC time '{value}' must use HH:mm.");
        return parsed;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string? root = FindSolutionRoot(Directory.GetCurrentDirectory()) ??
            FindSolutionRoot(AppContext.BaseDirectory);
        return Path.GetFullPath(Path.Combine(root ?? Directory.GetCurrentDirectory(), path));
    }

    private static string? FindSolutionRoot(string startingPath)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startingPath));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static BrokerEnvironment ParseEnvironment(string? value) =>
        string.Equals(value, "live", StringComparison.OrdinalIgnoreCase)
            ? BrokerEnvironment.Live
            : BrokerEnvironment.Demo;

    private static DateTimeOffset ParseDate(string? value, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd", "O"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw new ArgumentException($"Date '{value}' must use yyyy-MM-dd or ISO-8601 format.");
        }

        return parsed;
    }

    private static int ParseInt(string? value, int fallback, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} must be from {minimum} to {maximum}.");
        }
        return parsed;
    }

    private static decimal ParseDecimal(
        string? value,
        decimal fallback,
        decimal minimumExclusive,
        string name,
        bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ||
            (allowZero ? parsed < 0m : parsed <= minimumExclusive))
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} contains an invalid numeric value.");
        }
        return parsed;
    }

    private static decimal ParseSignedDecimal(string? value, decimal fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
            throw new ArgumentOutOfRangeException(name, $"--{name} contains an invalid numeric value.");
        return parsed;
    }

    private static SimulationAccountMode ParseAccountMode(string? value) => value?.ToLowerInvariant() switch
    {
        null or "independent" or "independentstrategyaccounts" => SimulationAccountMode.IndependentStrategyAccounts,
        "shared" or "sharedportfolioaccount" => SimulationAccountMode.SharedPortfolioAccount,
        _ => throw new ArgumentException("--account-mode must be independent or shared.")
    };
}
