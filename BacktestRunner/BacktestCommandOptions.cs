using System.Globalization;
using Agent.Strategies;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;
using RiskManager;
using RiskManager.Calibration;
using RiskManager.Safety;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;
using ChartAnnotator.Regime;
using RiskManager.Conditions;
using PortfolioManager.Risk;
using PortfolioManager.CrossMarket;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Value;
using ChartAnnotator.NeoWave;
using Simulator.Execution;
using Simulator.Financing;
using Agent.Configuration;

namespace BacktestRunner;

internal sealed record BacktestCommandOptions
{
    public InstrumentKey Instrument { get; init; } = new(RecommendedSimulationDefaults.Instrument);
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public BarInterval ExecutionInterval { get; init; } = BarInterval.Minutes(1);
    public BarInterval AnalysisBaseInterval { get; init; } = BarInterval.Minutes(1);
    /// <summary>Structural-confluence's own trigger/setup/context timeframe - see the identical fields on <see cref="BacktestRuntimeOptions"/>.</summary>
    public BarInterval StructuralTriggerInterval { get; init; } = BarInterval.Minutes(5);
    public BarInterval StructuralSetupInterval { get; init; } = Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultSetupInterval;
    public BarInterval StructuralContextInterval { get; init; } = Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultContextInterval;
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
    /// <summary>Quote-currency to account-currency rates, e.g. "JPY=0.0067,CHF=1.12".</summary>
    public IReadOnlyDictionary<string, decimal> QuoteRates { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public decimal Quantity { get; init; } = 1_000m;
    public PositionSizingOptions PositionSizing { get; init; } =
        RecommendedSimulationDefaults.PositionSizing;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;

    /// <summary>breakout-detector bracket, in ATR multiples (§3.19). Target/stop is the R multiple.</summary>
    public decimal BreakoutStopAtrMultiple { get; init; } = 1.5m;

    public decimal BreakoutTargetAtrMultiple { get; init; } = 3.0m;

    /// <summary>--trend-tactical-no-ml runs the design's rung B: trend gate only.</summary>
    public bool TrendTacticalRequireClassifier { get; init; } = true;

    /// <summary>--trend-tactical-cooldown-bars N</summary>
    public int TrendTacticalReentryCooldownBars { get; init; }

    /// <summary>--trend-tactical-one-entry-per-trend</summary>
    public bool TrendTacticalOneEntryPerTrend { get; init; }

    /// <summary>--trend-tactical-ml-stop</summary>
    public bool TrendTacticalUseMlStop { get; init; }

    /// <summary>--trend-tactical-secondary-trends 30,60</summary>
    public IReadOnlyList<int> TrendTacticalSecondaryTrendMinutes { get; init; } = [];

    /// <summary>--trend-tactical-swing-entry enables Rung A's validated entry rule.</summary>
    public bool TrendTacticalUseSwingEntry { get; init; }

    /// <summary>--trend-entry-percentile 0.05</summary>
    public decimal TrendTacticalEntryPercentile { get; init; } = 0.05m;

    /// <summary>--trend-entry-mode PriceOnly|TimeOnly|PriceAndTime</summary>
    public TrendStatistics.Trading.SwingEntryMode TrendTacticalSwingEntryMode { get; init; } =
        TrendStatistics.Trading.SwingEntryMode.PriceOnly;

    /// <summary>
    /// --classifier-model PATH. Without it the ML agents fall back to a no-trade model and report
    /// zero trades with no error.
    /// </summary>
    public string? ClassifierModelPath { get; init; }

    /// <summary>--stop-model PATH. Absent means the agent keeps its structural stop.</summary>
    public string? StopModelPath { get; init; }

    /// <summary>Populated from the loaded artifact so the agent matches the model it will score with.</summary>
    public TradingClassifier.Configuration.ClassifierOptions? ClassifierOptions { get; init; }

    /// <summary>Signal/trigger timeframe from the model artifact.</summary>
    public BarInterval? ClassifierSignalInterval { get; init; }

    /// <summary>--alfonso-top / --alfonso-middle / --alfonso-lower.</summary>
    /// <summary>--aggregation-gap-tolerance N (0..1). Needed for timeframes at or above a daily close.</summary>
    public double AggregationGapToleranceFraction { get; init; }

    public BarInterval? AlfonsoTopInterval { get; init; }
    public BarInterval? AlfonsoMiddleInterval { get; init; }
    public BarInterval? AlfonsoLowerInterval { get; init; }
    public decimal? AlfonsoRewardMultiple { get; init; }
    public decimal? AlfonsoStopPadding { get; init; }
    public bool AlfonsoUseStructuralSwingStop { get; init; }
    public bool AlfonsoUseOpposingZoneTarget { get; init; }
    public int AlfonsoStructuralStopLookbackCandles { get; init; } = 48;
    public bool AlfonsoEliminationRequiresClose { get; init; } = false;
    public bool AlfonsoTrendlineBreakRequiresClose { get; init; } = true;
    public bool AlfonsoRequireValidZoneForTrendChange { get; init; } = true;

    public bool AlfonsoTreatAmbiguousBaseAsContinuation { get; init; }

    public bool AlfonsoRequireTradeableZoneForTrendChange { get; init; }

    public bool AlfonsoRequireStructuralAgreement { get; init; }

    public bool AlfonsoRequireConfirmedTrendStructure { get; init; }

    public string? AlfonsoCandidateLogPath { get; init; }

    public string? AlfonsoInventoryLogPath { get; init; }

    public decimal AlfonsoMaximumPlacementDistanceAtr { get; init; } = 3m;

    public decimal AlfonsoRestingOrderReplacementAtr { get; init; }

    public bool AlfonsoRequireReversalConfirmation { get; init; }

    public AlfonsoEntryPolicy AlfonsoEntryPolicy { get; init; } = AlfonsoEntryPolicy.Core;

    public bool AlfonsoAllowPositionManagement { get; init; }

    public PositionManagementOptions AlfonsoPositionManagement { get; init; } =
        PositionManagementOptions.BracketOnlyDefaults;

    public bool AlfonsoRequireDriftAlignment { get; init; }

    public int AlfonsoDriftLookbackCandles { get; init; } = 60;
    public bool AlfonsoSwingBreakIsAnAccomplishment { get; init; } = true;
    public bool AlfonsoHalfZoneEntry { get; init; }
    public bool AlfonsoRequireValidHost { get; init; } = true;

    /// <summary>--alfonso-single-candle-drop-rally restores the pre-2026-09-05 one-candle base.</summary>
    public bool AlfonsoDropRallyBaseSpansBothCandles { get; init; } = true;

    /// <summary>--alfonso-swing-anchor-at-base-end restores the pre-2026-09-05 swing anchoring.</summary>
    public bool AlfonsoAnchorSwingsAtExtreme { get; init; } = true;

    /// <summary>--alfonso-maintain-structure re-checks module 5's structural condition while a trend runs.</summary>
    public bool AlfonsoMaintainStructuralAgreement { get; init; }

    /// <summary>--alfonso-invalidate-price-structure: neutral on a confirmed price-swing close break.</summary>
    public bool AlfonsoInvalidateOnPriceStructureBreak { get; init; }

    /// <summary>--alfonso-allow-contradicting-trendlines restores the pre-2026-09-06 fit.</summary>
    public bool AlfonsoRejectContradictingTrendlines { get; init; } = true;

    /// <summary>--alfonso-min-stop-top-atr N: stop floor in ATR of the TOP timeframe.</summary>
    public decimal AlfonsoMinimumStopTopAtrMultiple { get; init; }

    /// <summary>--alfonso-structure-log PATH: decision-time trend, trendline and zones as JSONL.</summary>
    public string? AlfonsoStructureLogPath { get; init; }
    /// <summary>--alfonso-trend-audit PATH: four trend variants on identical live closed candles.</summary>
    public string? AlfonsoTrendAuditPath { get; init; }
    public bool AlfonsoOverExtensionTrendlines { get; init; }
    public ZoneGrade AlfonsoMinimumZoneGrade { get; init; } = ZoneGrade.Weak;
    public decimal AlfonsoMinimumImpulseToBaseRatio { get; init; } =
        new ImbalanceOptions().MinimumImpulseToBaseRatio;
    public bool AlfonsoRequireNestedEntries { get; init; }
    public bool AlfonsoRequireControlAgreement { get; init; } = true;
    public bool AlfonsoAllowConfirmationEntries { get; init; }
    public decimal AlfonsoMaximumCostToRiskFraction { get; init; }
    public decimal AlfonsoMinimumStopAtrMultiple { get; init; }
    public decimal AlfonsoMinimumAtrPercentile { get; init; }
    public decimal AlfonsoMaximumAtrPercentile { get; init; } = 1m;
    public decimal AlfonsoMinimumProfitMarginMultiple { get; init; } = 3.0m;
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
    public bool InvertSignalPolarity { get; init; }
    public int WarmupDays { get; init; } = RecommendedSimulationDefaults.WarmupDays;
    public int ProgressIntervalMs { get; init; } = 500;
    public IReadOnlyList<string> Strategies { get; init; } = ["legacy", "improved"];
    /// <summary>
    /// Optional §7 multi-instrument portfolio clock: assigns each strategy its own
    /// instrument instead of every strategy trading <see cref="Instrument"/>. JSON/
    /// programmatic only for now, matching AnnotationOptions/MarketRegimeRouting -
    /// no individual --flag yet. Null/empty preserves today's single-instrument
    /// behaviour exactly.
    /// </summary>
    public IReadOnlyList<StrategyInstrumentAssignment>? StrategyAssignments { get; init; }
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
    public PositionManagementOptions StructuralPositionManagement { get; init; } =
        PositionManagementOptions.StructuralDefaults;
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
    /// <summary>
    /// Optional, independent value-location evidence (spec §13.3). Programmatic/JSON
    /// callers may set this directly; no individual --flag parsing yet. Enabled by
    /// default (2026-07-16 agent decision-quality pass) - it's a proven, tested, soft
    /// confidence nudge, not a new/unvalidated capability.
    /// </summary>
    public ValueLocationEvidenceOptions ValueLocationEvidence { get; init; } = new() { Enabled = true };
    /// <summary>
    /// Cross-market currency-strength coordinator (spec §5). Programmatic/JSON callers
    /// may set this directly; no individual --flag parsing yet.
    /// </summary>
    public CurrencyStrengthOptions CurrencyStrength { get; init; } = new();
    /// <summary>
    /// Optional currency-strength confluence/opposition evidence (2026-07-16 agent
    /// decision-quality pass). Programmatic/JSON callers may set this directly; no
    /// individual --flag parsing yet. Disabled by default - a new capability, and also
    /// depends on <see cref="CurrencyStrength"/> being separately enabled with baskets.
    /// </summary>
    public CurrencyStrengthEvidenceOptions CurrencyStrengthEvidence { get; init; } = new();
    /// <summary>
    /// Causal monowave/hypothesis analysis. CLI activation is explicit through --neo-wave;
    /// RecordOnly is the safe default mode and does not change decisions or risk.
    /// </summary>
    public bool NeoWaveEnabled { get; init; }
    public NeoWaveEvidenceOptions NeoWaveEvidence { get; init; } = new();
    public BarInterval? NeoWaveEvidenceInterval { get; init; }
    /// <summary>
    /// Setup-calibration policy configuration (audit §16). Programmatic/JSON callers may set
    /// this directly; no individual --flag parsing yet - use --setup-calibration-artifact-id
    /// to enable via a server-owned artifact from the calibration repository instead.
    /// </summary>
    public SetupCalibrationPolicyOptions SetupCalibration { get; init; } = new();
    public SetupCalibrationArtifact? SetupCalibrationArtifact { get; init; }
    /// <summary>Resolved via <see cref="ICalibrationArtifactRepository"/> at startup when set.</summary>
    public string? SetupCalibrationArtifactId { get; init; }
    /// <summary>
    /// Trade-management calibration configuration (audit §16). Programmatic/JSON callers may
    /// set this directly; no individual --flag parsing yet - use
    /// --management-calibration-artifact-id to enable via a server-owned artifact instead.
    /// </summary>
    public TradeManagementCalibrationOptions ManagementCalibration { get; init; } = new();
    public TradeManagementCalibration? ManagementCalibrationArtifact { get; init; }
    /// <summary>Resolved via <see cref="ICalibrationArtifactRepository"/> at startup when set.</summary>
    public string? ManagementCalibrationArtifactId { get; init; }
    /// <summary>
    /// Statistical meta-model policy (audit §17). Programmatic/JSON callers may set this
    /// directly; no individual --flag parsing yet - use --meta-model-artifact-id to enable
    /// via a server-owned artifact from the calibration repository instead.
    /// </summary>
    public MetaModelPolicyOptions MetaModel { get; init; } = new();
    public MetaModelArtifact? MetaModelArtifact { get; init; }
    /// <summary>Resolved via <see cref="ICalibrationArtifactRepository"/> at startup when set.</summary>
    public string? MetaModelArtifactId { get; init; }
    /// <summary>
    /// Root directory <see cref="SetupCalibrationArtifactId"/>/<see cref="ManagementCalibrationArtifactId"/>/
    /// <see cref="MetaModelArtifactId"/> are resolved from.
    /// </summary>
    public string CalibrationArtifactsDirectory { get; init; } = Path.Combine(".cache", "calibration-artifacts");
    /// <summary>
    /// Opt-in: before the main evaluation run, train a fresh leakage-safe calibration bundle
    /// (setup + meta-model + management, via <c>QuantResearch.Training.CalibrationTrainingPipeline</c>)
    /// per strategy in <see cref="Strategies"/>, on the window immediately preceding <see cref="From"/>.
    /// This is an isolated research job - it never wires its output into the run it's part of; it
    /// only produces a PendingReview <c>CalibrationBundleCandidate</c> for later explicit review/
    /// promotion (use --setup-calibration-artifact-id etc., or the promotion API, to actually use
    /// an approved artifact). Default false preserves today's behaviour exactly.
    /// </summary>
    public bool AutoTrainCalibration { get; init; }
    public int AutoTrainCalibrationWindowDays { get; init; } = 180;
    public int AutoTrainCalibrationFolds { get; init; } = 5;
    public int AutoTrainCalibrationEmbargoHours { get; init; } = 24;
    /// <summary>Optional; defaults to "{strategy}-auto" per strategy when unset.</summary>
    public string? AutoTrainCalibrationStrategyVersion { get; init; }

    /// <summary>
    /// For every structural-confluence assignment that does not already carry an explicit
    /// <see cref="StrategyInstrumentAssignment.IndicatorCalibrationArtifactId"/>/
    /// <see cref="StrategyInstrumentAssignment.LiquidityBreakRetestCalibrationArtifactId"/> pin,
    /// resolves the most recent Approved+Improved artifact for that assignment's instrument and
    /// this request's <see cref="BacktestRuntimeOptions.StructuralTriggerInterval"/> (via
    /// <see cref="BestApprovedCalibrationArtifactResolver"/>) and pins it for this one invocation -
    /// same as if a human had typed <c>--request-json</c> with that GUID already filled in. Never
    /// changes anything server-side; a run with no matching approved artifact simply runs
    /// unpinned. Default false preserves today's behaviour exactly.
    /// </summary>
    public bool AutoApplyIndicatorCalibration { get; init; }
    public SimulationAccountMode AccountMode { get; init; } = SimulationAccountMode.IndependentStrategyAccounts;
    /// <summary>Enabled by default (2026-07-16 agent decision-quality pass) - regime routing/risk is proven and tested.</summary>
    public bool RegimeEnabled { get; init; } = true;
    public int EfficiencyRatioPeriod { get; init; } = 14;
    /// <summary>AGENT-03: enabled by default - session/rollover/spread/stale-data protection
    /// must not be silently off for any caller that doesn't know to opt in explicitly;
    /// --no-trading-conditions opts out. --trading-conditions is still accepted as a harmless
    /// legacy no-op.</summary>
    public bool TradingConditionsEnabled { get; init; } = true;
    /// <summary>Enabled by default (2026-07-16 agent decision-quality pass) - drawdown/volatility-scaled risk is proven and tested.</summary>
    public bool AdaptiveRiskEnabled { get; init; } = true;
    /// <summary>
    /// Enabled by default so CLI behavior matches the Dashboard's interactive single-run
    /// default. Building chart replay chunks measured at roughly a third of throughput
    /// locally; --no-capture-market-replay opts out for a faster run when the replay chart
    /// isn't needed (e.g. metrics-only/perf-testing runs).
    /// </summary>
    public bool CaptureMarketReplay { get; init; } = true;
    public decimal MaximumPortfolioHeatPercent { get; init; } = 1.5m;
    public decimal MaximumNetCurrencyExposurePercent { get; init; } = 300m;
    public decimal MaximumGrossCurrencyExposurePercent { get; init; } = 300m;
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
                "invert-signal-polarity" or
                "legacy-disable-mechanical-protection" or "improved-disable-mechanical-protection" or
                "trend-tactical-no-ml" or "trend-tactical-one-entry-per-trend" or
                "trend-tactical-ml-stop" or "trend-tactical-swing-entry" or
                "alfonso-elimination-on-wick" or "alfonso-elimination-on-close" or
                "alfonso-trendline-break-full-candle" or
                "alfonso-allow-invalid-zones" or "alfonso-no-swing-break" or
                "alfonso-nested-only" or "alfonso-ignore-control" or "alfonso-structural-stop" or
                "alfonso-confirmation-trades" or "alfonso-opposing-zone-target" or
                "alfonso-ambiguous-base-cp" or "alfonso-tradeable-zone-trend" or
                "alfonso-no-structural-agreement" or "alfonso-confirm-entry" or
                "alfonso-with-drift" or
                "alfonso-manage-position" or "alfonso-structural-agreement" or "alfonso-confirm-trend-structure" or
                "alfonso-half-entry" or "alfonso-allow-invalid-hosts" or
                "alfonso-single-candle-drop-rally" or
                "alfonso-swing-anchor-at-base-end" or "alfonso-maintain-structure" or
                "alfonso-invalidate-price-structure" or
                "alfonso-allow-contradicting-trendlines" or
                "alfonso-overextension-trendlines" or
                "legacy-no-scale-out" or "improved-no-scale-out" or
                "legacy-no-profit-floor" or "improved-no-profit-floor" or
                "legacy-no-giveback" or "improved-no-giveback" or
                "legacy-no-stagnation-reduction" or "improved-no-stagnation-reduction" or
                "legacy-no-structure-reduction" or "improved-no-structure-reduction" or
                "legacy-no-momentum-reduction" or "improved-no-momentum-reduction" or
                "legacy-no-volatility-reduction" or "improved-no-volatility-reduction" or
                "legacy-enable-cost-stress-reduction" or "improved-enable-cost-stress-reduction" or
                "legacy-neo-wave-invalidation-exit" or "improved-neo-wave-invalidation-exit" or
                "regime" or "no-regime" or "trading-conditions" or "no-trading-conditions" or
                "adaptive-risk" or "no-adaptive-risk" or "financing" or
                "neo-wave" or "auto-train-calibration" or
                "capture-market-replay" or "no-capture-market-replay" or
                "pullback-only" or "candidate-fixes")
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
        BarInterval structuralTriggerInterval = values.GetValueOrDefault("structural-trigger-interval") is { } rawStructuralTrigger
            ? ParseInterval(rawStructuralTrigger)
            : BarInterval.Minutes(5);
        BarInterval structuralSetupInterval = values.GetValueOrDefault("structural-setup-interval") is { } rawStructuralSetup
            ? ParseInterval(rawStructuralSetup)
            : Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultSetupInterval;
        BarInterval structuralContextInterval = values.GetValueOrDefault("structural-context-interval") is { } rawStructuralContext
            ? ParseInterval(rawStructuralContext)
            : Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultContextInterval;
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

        // Portfolio v1: every strategy in --strategies trades every instrument in --instruments
        // (cartesian). Same source/broker for all of them - --source stays a single global flag.
        // Mode is left at its record default (Shadow) rather than forced to Executable: Shadow
        // vs Executable isn't read anywhere in the backtest engine yet (only validated for the
        // live host's "one owner per instrument" rule), so setting it would only risk tripping
        // that validation on multi-strategy/instrument runs for no behavioural benefit here.
        string[] instrumentTokens = (values.GetValueOrDefault("instruments") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(ReadInstrumentsFile(values.GetValueOrDefault("instruments-file")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<StrategyInstrumentAssignment>? strategyAssignments = instrumentTokens.Length == 0
            ? null
            : instrumentTokens
                .Select(token => new InstrumentKey(token))
                .SelectMany(instrument => strategies.Select(strategy => new StrategyInstrumentAssignment
                {
                    StrategyType = TradingAgentTypeIds.Normalize(strategy),
                    Instrument = instrument
                }))
                .ToArray();

        if (strategies.Any(item => string.Equals(item, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase)) &&
            (BarIntervalParser.CompareDuration(structuralTriggerInterval, structuralSetupInterval) > 0 ||
             BarIntervalParser.CompareDuration(structuralSetupInterval, structuralContextInterval) > 0))
        {
            throw new ArgumentException(
                $"Structural-confluence timeframe stack must satisfy structural-trigger-interval ({BarIntervalParser.Format(structuralTriggerInterval)}) <= " +
                $"structural-setup-interval ({BarIntervalParser.Format(structuralSetupInterval)}) <= " +
                $"structural-context-interval ({BarIntervalParser.Format(structuralContextInterval)}).");
        }
        BarInterval trendInterval = ParseInterval(values.GetValueOrDefault("trend-interval") ?? "2h");
        BarInterval confirmationInterval = ParseInterval(values.GetValueOrDefault("confirmation-interval") ?? "15m");
        BarInterval entryInterval = ParseInterval(values.GetValueOrDefault("entry-interval") ?? "5m");
        BarInterval[] secondaryTrendIntervals = ParseIntervalList(
            values.GetValueOrDefault("secondary-trend-intervals") ?? "1h");
        BarInterval[] setupIntervals = ParseIntervalList(
            values.GetValueOrDefault("setup-intervals") ?? "30m");
        BarInterval[] additionalConfirmationIntervals = ParseIntervalList(
            values.GetValueOrDefault("additional-confirmation-intervals"));

        // Opt-in unified surface (PROJECT_STATE.md §4b, Phase 2): --timeframes overrides only the
        // Progressive roles it specifies, per-role, leaving every role it omits on the discrete
        // flags/defaults above untouched. Applied before the Minimum*Alignments defaults below so
        // those defaults (which key off list length) see the overridden lists, not the stale ones.
        if (values.GetValueOrDefault("timeframes") is string timeframesSpec && !string.IsNullOrWhiteSpace(timeframesSpec))
        {
            TimeframePlan overridePlan = TimeframePlanTextFormat.Parse(timeframesSpec);

            IReadOnlyList<TimeframeAssignment> contextGates = overridePlan.For(TimeframeRole.Context, TimeframeInfluence.Gate);
            if (contextGates.Count > 0)
                trendInterval = contextGates[0].Interval;

            IReadOnlyList<TimeframeAssignment> contextVotes = overridePlan.For(TimeframeRole.Context, TimeframeInfluence.Vote);
            if (contextVotes.Count > 0)
                secondaryTrendIntervals = [.. contextVotes.Select(a => a.Interval)];

            IReadOnlyList<TimeframeAssignment> setupVotes = overridePlan.For(TimeframeRole.Setup, TimeframeInfluence.Vote);
            if (setupVotes.Count > 0)
                setupIntervals = [.. setupVotes.Select(a => a.Interval)];

            IReadOnlyList<TimeframeAssignment> confirmationVotes = overridePlan.For(TimeframeRole.Confirmation, TimeframeInfluence.Vote);
            if (confirmationVotes.Count > 0)
            {
                confirmationInterval = confirmationVotes[0].Interval;
                additionalConfirmationIntervals = [.. confirmationVotes.Skip(1).Select(a => a.Interval)];
            }

            IReadOnlyList<TimeframeAssignment> triggerGates = overridePlan.For(TimeframeRole.Trigger, TimeframeInfluence.Gate);
            if (triggerGates.Count > 0)
                entryInterval = triggerGates[0].Interval;
        }

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
        // Alfonso is bracket-only unless --alfonso-manage-position is given, so its trailing mode
        // defaults to disabled rather than to the structure-atr default the other stacks use.
        bool alfonsoManaged = values.ContainsKey("alfonso-manage-position");
        PositionManagementOptions alfonsoManagement = alfonsoManaged
            ? ParsePositionManagement(
                values.ContainsKey("alfonso-trailing-mode")
                    ? values
                    : new Dictionary<string, string?>(values) { ["alfonso-trailing-mode"] = "break-even" },
                "alfonso",
                entryInterval,
                structureDefault: 2m,
                preserveTarget: true)
            : PositionManagementOptions.BracketOnlyDefaults;

        PositionManagementOptions structuralManagement = PositionManagementOptions.StructuralDefaults;
        if (strategies.Any(item => string.Equals(item, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase)))
        {
            // BacktestRuntimeOptions.Validate() checks the Legacy/Improved/Structural
            // position-management stacks unconditionally regardless of which strategies are active,
            // so all three must agree with whatever structural timeframe stack this run targets -
            // reusing the same helper the indicator-calibration adapters use. At the 5m/15m/1h
            // default this reproduces each preset's existing values exactly, so this is a no-op
            // unless a non-default --structural-trigger-interval/--structural-setup-interval/
            // --structural-context-interval is given. Only fires when structural-confluence is
            // actually in --strategies, so a plain legacy/improved run's own --legacy-*/--improved-*
            // flags are never touched; running structural-confluence together with legacy/improved at
            // a non-default structural timeframe will override those flags here too, since all three
            // stacks must agree.
            (legacyManagement, improvedManagement, structuralManagement) = CalibrationTimeframeStack.BuildPositionManagementOverrides(
                structuralTriggerInterval, structuralSetupInterval, structuralContextInterval);
        }

        return new BacktestCommandOptions
        {
            ShowHelp = values.ContainsKey("help"),
            Instrument = new InstrumentKey(
                values.GetValueOrDefault("instrument") ?? RecommendedSimulationDefaults.Instrument),
            From = from,
            To = to,
            ExecutionInterval = execution,
            AnalysisBaseInterval = analysisBase,
            StructuralTriggerInterval = structuralTriggerInterval,
            StructuralSetupInterval = structuralSetupInterval,
            StructuralContextInterval = structuralContextInterval,
            AnalysisIntervals = analysis,
            PrecisionMode = precision,
            SourceKind = source,
            ImportedCandlePath = importedPath,
            StrategyAssignments = strategyAssignments,
            TrendInterval = trendInterval,
            SecondaryTrendIntervals = secondaryTrendIntervals,
            SetupIntervals = setupIntervals,
            ConfirmationInterval = confirmationInterval,
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
            QuoteRates = ParseQuoteRates(values.GetValueOrDefault("quote-rate")),
            Quantity = quantity,
            PositionSizing = positionSizing,
            Leverage = leverage,
            CommissionRate = ParseDecimal(values.GetValueOrDefault("commission-rate"), 0.00002m, -1m, "commission-rate", allowZero: true),
            SpreadBasisPoints = ParseDecimal(values.GetValueOrDefault("spread-bps"), 1m, -1m, "spread-bps", allowZero: true),
            SlippageBasisPoints = ParseDecimal(values.GetValueOrDefault("slippage-bps"), 0.5m, -1m, "slippage-bps", allowZero: true),
            MinimumRewardRisk = ParseDecimal(values.GetValueOrDefault("minimum-rr"), 1.5m, 0m, "minimum-rr", allowZero: true),
            BreakoutStopAtrMultiple = ParseDecimal(
                values.GetValueOrDefault("breakout-stop-atr"), 1.5m, 0m, "breakout-stop-atr"),
            BreakoutTargetAtrMultiple = ParseDecimal(
                values.GetValueOrDefault("breakout-target-atr"), 3.0m, 0m, "breakout-target-atr"),
            AggregationGapToleranceFraction = (double)ParseDecimal(
                values.GetValueOrDefault("aggregation-gap-tolerance"), 0m, 0m,
                "aggregation-gap-tolerance", allowZero: true),
            AlfonsoTopInterval = values.GetValueOrDefault("alfonso-top") is string alfonsoTop
                ? ParseInterval(alfonsoTop) : null,
            AlfonsoMiddleInterval = values.GetValueOrDefault("alfonso-middle") is string alfonsoMiddle
                ? ParseInterval(alfonsoMiddle) : null,
            AlfonsoLowerInterval = values.GetValueOrDefault("alfonso-lower") is string alfonsoLower
                ? ParseInterval(alfonsoLower) : null,
            AlfonsoRewardMultiple = values.ContainsKey("alfonso-reward")
                ? ParseDecimal(values.GetValueOrDefault("alfonso-reward"), 3m, 0m, "alfonso-reward")
                : null,
            AlfonsoUseStructuralSwingStop = values.ContainsKey("alfonso-structural-stop"),
            AlfonsoUseOpposingZoneTarget = values.ContainsKey("alfonso-opposing-zone-target"),
            AlfonsoStructuralStopLookbackCandles = ParseInt(
                values.GetValueOrDefault("alfonso-stop-lookback"), 48, 5, 10000, "alfonso-stop-lookback"),
            AlfonsoStopPadding = values.ContainsKey("alfonso-stop-padding")
                ? ParseDecimal(
                    values.GetValueOrDefault("alfonso-stop-padding"), 0.25m, 0m, "alfonso-stop-padding",
                    allowZero: true)
                : null,
            // Wick penetration is the literal Module 4 default. Keep the former wick flag accepted
            // for script compatibility; --alfonso-elimination-on-close selects the stricter reading.
            AlfonsoEliminationRequiresClose = values.ContainsKey("alfonso-elimination-on-close") &&
                                               !values.ContainsKey("alfonso-elimination-on-wick"),
            AlfonsoTrendlineBreakRequiresClose = !values.ContainsKey("alfonso-trendline-break-full-candle"),
            AlfonsoRequireValidZoneForTrendChange = !values.ContainsKey("alfonso-allow-invalid-zones"),
            AlfonsoTreatAmbiguousBaseAsContinuation =
                values.ContainsKey("alfonso-ambiguous-base-cp"),
            AlfonsoRequireTradeableZoneForTrendChange =
                values.ContainsKey("alfonso-tradeable-zone-trend"),
            // Off by default since 2026-09-02. The former disable flag stays accepted so existing
            // scripts keep working; --alfonso-structural-agreement opts back in.
            AlfonsoRequireStructuralAgreement =
                values.ContainsKey("alfonso-structural-agreement") &&
                !values.ContainsKey("alfonso-no-structural-agreement"),
            AlfonsoRequireConfirmedTrendStructure = values.ContainsKey("alfonso-confirm-trend-structure"),
            AlfonsoCandidateLogPath = values.GetValueOrDefault("alfonso-candidate-log"),
            AlfonsoInventoryLogPath = values.GetValueOrDefault("alfonso-inventory-log"),
            AlfonsoMaximumPlacementDistanceAtr = ParseDecimal(
                values.GetValueOrDefault("alfonso-max-placement-atr"), 3m, 0m,
                "alfonso-max-placement-atr", allowZero: true),
            AlfonsoRestingOrderReplacementAtr = ParseDecimal(
                values.GetValueOrDefault("alfonso-replace-resting-atr"), 0m, 0m,
                "alfonso-replace-resting-atr", allowZero: true),
            AlfonsoRequireReversalConfirmation = values.ContainsKey("alfonso-confirm-entry"),
            AlfonsoEntryPolicy = ParseAlfonsoEntryPolicy(values.GetValueOrDefault("alfonso-entry-policy")),
            AlfonsoAllowPositionManagement = alfonsoManaged,
            AlfonsoPositionManagement = alfonsoManagement,
            AlfonsoRequireDriftAlignment = values.ContainsKey("alfonso-with-drift"),
            AlfonsoDriftLookbackCandles = ParseInt(
                values.GetValueOrDefault("alfonso-drift-lookback"), 60, 0, 10_000,
                "alfonso-drift-lookback"),
            AlfonsoSwingBreakIsAnAccomplishment = !values.ContainsKey("alfonso-no-swing-break"),
            AlfonsoHalfZoneEntry = values.ContainsKey("alfonso-half-entry"),
            AlfonsoRequireValidHost = !values.ContainsKey("alfonso-allow-invalid-hosts"),
            AlfonsoDropRallyBaseSpansBothCandles =
                !values.ContainsKey("alfonso-single-candle-drop-rally"),
            AlfonsoAnchorSwingsAtExtreme =
                !values.ContainsKey("alfonso-swing-anchor-at-base-end"),
            AlfonsoMaintainStructuralAgreement = values.ContainsKey("alfonso-maintain-structure"),
            AlfonsoInvalidateOnPriceStructureBreak = values.ContainsKey("alfonso-invalidate-price-structure"),
            AlfonsoRejectContradictingTrendlines =
                !values.ContainsKey("alfonso-allow-contradicting-trendlines"),
            AlfonsoStructureLogPath = values.GetValueOrDefault("alfonso-structure-log"),
            AlfonsoTrendAuditPath = values.GetValueOrDefault("alfonso-trend-audit"),
            AlfonsoMinimumStopTopAtrMultiple = ParseDecimal(
                values.GetValueOrDefault("alfonso-min-stop-top-atr"), 0m, 0m,
                "alfonso-min-stop-top-atr", allowZero: true),
            AlfonsoOverExtensionTrendlines = values.ContainsKey("alfonso-overextension-trendlines"),
            AlfonsoMinimumZoneGrade = ParseZoneGrade(values.GetValueOrDefault("alfonso-min-grade")),
            AlfonsoMinimumImpulseToBaseRatio = ParseDecimal(
                values.GetValueOrDefault("alfonso-min-impulse-ratio"),
                new ImbalanceOptions().MinimumImpulseToBaseRatio, 0m, "alfonso-min-impulse-ratio"),
            AlfonsoRequireNestedEntries = values.ContainsKey("alfonso-nested-only"),
            AlfonsoRequireControlAgreement = !values.ContainsKey("alfonso-ignore-control"),
            AlfonsoAllowConfirmationEntries = values.ContainsKey("alfonso-confirmation-trades"),
            AlfonsoMaximumCostToRiskFraction = ParseDecimal(
                values.GetValueOrDefault("alfonso-max-cost-to-risk"), 0m, 0m,
                "alfonso-max-cost-to-risk", allowZero: true),
            AlfonsoMinimumStopAtrMultiple = ParseDecimal(
                values.GetValueOrDefault("alfonso-min-stop-atr"), 0m, 0m,
                "alfonso-min-stop-atr", allowZero: true),
            AlfonsoMinimumProfitMarginMultiple = ParseDecimal(
                values.GetValueOrDefault("alfonso-profit-margin"), 3.0m, 0m,
                "alfonso-profit-margin", allowZero: true),
            AlfonsoMinimumAtrPercentile = ParseDecimal(
                values.GetValueOrDefault("alfonso-atr-percentile-min"), 0m, 0m,
                "alfonso-atr-percentile-min", allowZero: true),
            AlfonsoMaximumAtrPercentile = ParseDecimal(
                values.GetValueOrDefault("alfonso-atr-percentile-max"), 1m, 0m,
                "alfonso-atr-percentile-max"),
            TrendTacticalRequireClassifier = !values.ContainsKey("trend-tactical-no-ml"),
            TrendTacticalReentryCooldownBars = ParseInt(
                values.GetValueOrDefault("trend-tactical-cooldown-bars"), 0, 0, 5000,
                "trend-tactical-cooldown-bars"),
            TrendTacticalOneEntryPerTrend = values.ContainsKey("trend-tactical-one-entry-per-trend"),
            TrendTacticalUseMlStop = values.ContainsKey("trend-tactical-ml-stop"),
            TrendTacticalUseSwingEntry = values.ContainsKey("trend-tactical-swing-entry"),
            TrendTacticalEntryPercentile = ParseDecimal(
                values.GetValueOrDefault("trend-entry-percentile"), 0.05m, 0m, "trend-entry-percentile"),
            TrendTacticalSwingEntryMode = Enum.Parse<TrendStatistics.Trading.SwingEntryMode>(
                values.GetValueOrDefault("trend-entry-mode") ?? "PriceOnly", ignoreCase: true),
            TrendTacticalSecondaryTrendMinutes =
                values.TryGetValue("trend-tactical-secondary-trends", out string? secondaries)
                    ? [.. secondaries!.Split(',', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries).Select(int.Parse)]
                    : [],
            ClassifierModelPath = values.GetValueOrDefault("classifier-model"),
            StopModelPath = values.GetValueOrDefault("stop-model"),
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
            InvertSignalPolarity = values.ContainsKey("invert-signal-polarity"),
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
            ImprovedPositionManagement = improvedManagement,
            StructuralPositionManagement = structuralManagement
            ,AccountMode = ParseAccountMode(values.GetValueOrDefault("account-mode"))
            // Enabled by default (2026-07-16 agent decision-quality pass); --no-regime opts out.
            // --regime is still accepted as a harmless legacy no-op.
            ,RegimeEnabled = !values.ContainsKey("no-regime")
            ,EfficiencyRatioPeriod = ParseInt(values.GetValueOrDefault("er-period"), 14, 2, 10_000, "er-period")
            // AGENT-03: enabled by default; --no-trading-conditions opts out.
            // --trading-conditions is still accepted as a harmless legacy no-op.
            ,TradingConditionsEnabled = !values.ContainsKey("no-trading-conditions")
            // Enabled by default (2026-07-16 agent decision-quality pass); --no-adaptive-risk opts out.
            // --adaptive-risk is still accepted as a harmless legacy no-op.
            ,AdaptiveRiskEnabled = !values.ContainsKey("no-adaptive-risk")
            // Matches the Dashboard's interactive single-run default; --no-capture-market-replay
            // opts out for a faster run. --capture-market-replay is accepted as a harmless no-op.
            ,CaptureMarketReplay = !values.ContainsKey("no-capture-market-replay")
            ,NeoWaveEnabled = values.ContainsKey("neo-wave")
            ,NeoWaveEvidence = new NeoWaveEvidenceOptions
            {
                Mode = values.ContainsKey("neo-wave")
                    ? Enum.Parse<NeoWaveEvidenceMode>(values.GetValueOrDefault("neo-wave-mode") ?? "RecordOnly", true)
                    : NeoWaveEvidenceMode.Disabled
            }
            ,NeoWaveEvidenceInterval = values.GetValueOrDefault("neo-wave-interval") is string neoWaveInterval
                ? ParseInterval(neoWaveInterval)
                : null
            ,MaximumPortfolioHeatPercent = ParseDecimal(values.GetValueOrDefault("maximum-portfolio-heat-percent"), 1.5m, 0m, "maximum-portfolio-heat-percent")
            ,ExecutionFillModel = Enum.Parse<SimulationFillModel>(values.GetValueOrDefault("fill-model") ?? "MidpointPlusConfiguredSpread", true)
            ,StressScenario = Enum.Parse<StressExecutionScenario>(values.GetValueOrDefault("stress-scenario") ?? "Base", true)
            ,MaximumFillQuantityPerFrame = ParseOptionalPositiveDecimal(values.GetValueOrDefault("fill-capacity"), "fill-capacity")
            ,FinancingEnabled = values.ContainsKey("financing")
            ,FinancingLongAnnualPercent = ParseSignedDecimal(values.GetValueOrDefault("financing-long-annual-percent"), 0m, "financing-long-annual-percent")
            ,FinancingShortAnnualPercent = ParseSignedDecimal(values.GetValueOrDefault("financing-short-annual-percent"), 0m, "financing-short-annual-percent")
            ,SetupCalibrationArtifactId = values.GetValueOrDefault("setup-calibration-artifact-id")
            ,ManagementCalibrationArtifactId = values.GetValueOrDefault("management-calibration-artifact-id")
            ,MetaModelArtifactId = values.GetValueOrDefault("meta-model-artifact-id")
            ,CalibrationArtifactsDirectory = values.GetValueOrDefault("calibration-artifacts-directory")
                ?? Path.Combine(".cache", "calibration-artifacts")
            ,AutoTrainCalibration = values.ContainsKey("auto-train-calibration")
            ,AutoTrainCalibrationWindowDays = ParseInt(
                values.GetValueOrDefault("auto-train-calibration-window-days"), 180, 30, 3650, "auto-train-calibration-window-days")
            ,AutoTrainCalibrationFolds = ParseInt(
                values.GetValueOrDefault("auto-train-calibration-folds"), 5, 3, 50, "auto-train-calibration-folds")
            ,AutoTrainCalibrationEmbargoHours = ParseInt(
                values.GetValueOrDefault("auto-train-calibration-embargo-hours"), 24, 0, 24 * 30, "auto-train-calibration-embargo-hours")
            ,AutoTrainCalibrationStrategyVersion = values.GetValueOrDefault("auto-train-calibration-strategy-version")
            ,AutoApplyIndicatorCalibration = values.ContainsKey("auto-apply-calibration")
        };
    }

    public BacktestRequest ToBacktestRequest() => new()
    {
        Instrument = Instrument,
        From = From,
        To = To,
        Environment = Environment,
        Strategies = Strategies,
        StrategyAssignments = StrategyAssignments,
        StartingBalance = StartingBalance,
        BaseCurrency = BaseCurrency ?? ResolveBaseCurrency(),
        QuoteToBaseCurrencyRates = QuoteRates,
        Quantity = Quantity,
        Leverage = Leverage,
        CommissionRate = CommissionRate,
        SpreadBasisPoints = SpreadBasisPoints,
        SlippageBasisPoints = SlippageBasisPoints,
        MinimumRewardRisk = MinimumRewardRisk,
        BreakoutStopAtrMultiple = BreakoutStopAtrMultiple,
        BreakoutTargetAtrMultiple = BreakoutTargetAtrMultiple,
        TrendTacticalRequireClassifier = TrendTacticalRequireClassifier,
        TrendTacticalReentryCooldownBars = TrendTacticalReentryCooldownBars,
        TrendTacticalOneEntryPerTrend = TrendTacticalOneEntryPerTrend,
        TrendTacticalUseMlStop = TrendTacticalUseMlStop,
        TrendTacticalSecondaryTrendMinutes = TrendTacticalSecondaryTrendMinutes,
        TrendTacticalUseSwingEntry = TrendTacticalUseSwingEntry,
        TrendTacticalEntryPercentile = TrendTacticalEntryPercentile,
        TrendTacticalSwingEntryMode = TrendTacticalSwingEntryMode,
        ClassifierOptions = ClassifierOptions,
        ClassifierSignalInterval = ClassifierSignalInterval,
        AlfonsoTopInterval = AlfonsoTopInterval,
        AlfonsoMiddleInterval = AlfonsoMiddleInterval,
        AlfonsoLowerInterval = AlfonsoLowerInterval,
        AlfonsoRewardMultiple = AlfonsoRewardMultiple,
        AlfonsoStopPadding = AlfonsoStopPadding,
        AlfonsoUseStructuralSwingStop = AlfonsoUseStructuralSwingStop,
        AlfonsoUseOpposingZoneTarget = AlfonsoUseOpposingZoneTarget,
        AlfonsoStructuralStopLookbackCandles = AlfonsoStructuralStopLookbackCandles,
        AlfonsoEliminationRequiresClose = AlfonsoEliminationRequiresClose,
        AlfonsoTrendlineBreakRequiresClose = AlfonsoTrendlineBreakRequiresClose,
        AlfonsoRequireValidZoneForTrendChange = AlfonsoRequireValidZoneForTrendChange,
        AlfonsoTreatAmbiguousBaseAsContinuation = AlfonsoTreatAmbiguousBaseAsContinuation,
        AlfonsoRequireTradeableZoneForTrendChange = AlfonsoRequireTradeableZoneForTrendChange,
        AlfonsoRequireStructuralAgreement = AlfonsoRequireStructuralAgreement,
        AlfonsoRequireConfirmedTrendStructure = AlfonsoRequireConfirmedTrendStructure,
        AlfonsoCandidateLogPath = AlfonsoCandidateLogPath,
        AlfonsoInventoryLogPath = AlfonsoInventoryLogPath,
        AlfonsoMaximumPlacementDistanceAtr = AlfonsoMaximumPlacementDistanceAtr,
        AlfonsoRestingOrderReplacementAtr = AlfonsoRestingOrderReplacementAtr,
        AlfonsoRequireReversalConfirmation = AlfonsoRequireReversalConfirmation,
        AlfonsoEntryPolicy = AlfonsoEntryPolicy,
        AlfonsoAllowPositionManagement = AlfonsoAllowPositionManagement,
        AlfonsoRequireDriftAlignment = AlfonsoRequireDriftAlignment,
        AlfonsoDriftLookbackCandles = AlfonsoDriftLookbackCandles,
        AlfonsoSwingBreakIsAnAccomplishment = AlfonsoSwingBreakIsAnAccomplishment,
        AlfonsoHalfZoneEntry = AlfonsoHalfZoneEntry,
        AlfonsoRequireValidHost = AlfonsoRequireValidHost,
        AlfonsoDropRallyBaseSpansBothCandles = AlfonsoDropRallyBaseSpansBothCandles,
        AlfonsoAnchorSwingsAtExtreme = AlfonsoAnchorSwingsAtExtreme,
        AlfonsoMaintainStructuralAgreement = AlfonsoMaintainStructuralAgreement,
        AlfonsoInvalidateOnPriceStructureBreak = AlfonsoInvalidateOnPriceStructureBreak,
        AlfonsoRejectContradictingTrendlines = AlfonsoRejectContradictingTrendlines,
        AlfonsoMinimumStopTopAtrMultiple = AlfonsoMinimumStopTopAtrMultiple,
        AlfonsoStructureLogPath = AlfonsoStructureLogPath,
        AlfonsoTrendAuditPath = AlfonsoTrendAuditPath,
        AlfonsoOverExtensionTrendlines = AlfonsoOverExtensionTrendlines,
        AlfonsoMinimumZoneGrade = AlfonsoMinimumZoneGrade,
        AlfonsoMinimumImpulseToBaseRatio = AlfonsoMinimumImpulseToBaseRatio,
        AlfonsoRequireNestedEntries = AlfonsoRequireNestedEntries,
        AlfonsoRequireControlAgreement = AlfonsoRequireControlAgreement,
        AlfonsoAllowConfirmationEntries = AlfonsoAllowConfirmationEntries,
        AlfonsoMaximumCostToRiskFraction = AlfonsoMaximumCostToRiskFraction,
        AlfonsoMinimumStopAtrMultiple = AlfonsoMinimumStopAtrMultiple,
        AlfonsoMinimumProfitMarginMultiple = AlfonsoMinimumProfitMarginMultiple,
        AlfonsoMinimumAtrPercentile = AlfonsoMinimumAtrPercentile,
        AlfonsoMaximumAtrPercentile = AlfonsoMaximumAtrPercentile,
        PriceActionConfirmation = PriceActionConfirmation,
        MinimumPriceActionConfidence = MinimumPriceActionConfidence,
        RejectStrongOpposingPriceAction = RejectStrongOpposingPriceAction,
        InvertSignalPolarity = InvertSignalPolarity,
        CaptureMarketReplay = CaptureMarketReplay,
        OutputDirectory = Path.Combine(OutputDirectory, "simulations"),
        CacheDirectory = CacheDirectory,
        JobsDirectory = JobsDirectory,
        Runtime = new BacktestRuntimeOptions
        {
            AccountMode = AccountMode,
            AggregationGapToleranceFraction = AggregationGapToleranceFraction,
            ExecutionInterval = ExecutionInterval,
            AnalysisBaseInterval = AnalysisBaseInterval,
            StructuralTriggerInterval = StructuralTriggerInterval,
            StructuralSetupInterval = StructuralSetupInterval,
            StructuralContextInterval = StructuralContextInterval,
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
            // Alfonso's stack was parsed and stored but never copied into the runtime options, so
            // --alfonso-manage-position ran as a silent no-op: zero stop-amendment requests.
            AlfonsoPositionManagement = AlfonsoPositionManagement,
            ImprovedPositionManagement = ImprovedPositionManagement,
            StructuralPositionManagement = StructuralPositionManagement,
            PositionSizing = PositionSizing,
            // Structural playbooks consume detector output. Defaults leave SupplyDemand/Liquidity
            // disabled, which yields StructuralZoneUnavailable / no pools forever. Auto-enable when
            // structural-confluence is on the run (Dashboard workspace already does this).
            AnnotationOptions = EnableStructuralDetectorsIfNeeded(AnnotationOptions with
            {
                EfficiencyRatioPeriod = EfficiencyRatioPeriod,
                MarketRegime = AnnotationOptions.MarketRegime with { Enabled = RegimeEnabled },
                NeoWave = AnnotationOptions.NeoWave with { Enabled = NeoWaveEnabled }
            }, Strategies, StrategyAssignments),
            MarketRegimeRouting = MarketRegimeRouting with { Enabled = RegimeEnabled },
            ValueLocationEvidence = ValueLocationEvidence,
            CurrencyStrengthEvidence = CurrencyStrengthEvidence,
            NeoWaveEvidence = NeoWaveEvidence,
            NeoWaveEvidenceInterval = NeoWaveEvidenceInterval,
            CurrencyStrength = CurrencyStrength,
            RegimeManagement = new RegimeManagementOptions { Enabled = RegimeEnabled },
            TradingConditions = new TradingConditionOptions { Enabled = TradingConditionsEnabled },
            AdaptiveRisk = new AdaptiveRiskOptions { Enabled = AdaptiveRiskEnabled },
            SetupCalibration = SetupCalibrationArtifact is null
                ? SetupCalibration
                : SetupCalibration with { Enabled = true },
            SetupCalibrationArtifact = SetupCalibrationArtifact,
            ManagementCalibration = ManagementCalibrationArtifact is null
                ? ManagementCalibration
                : ManagementCalibration with { Enabled = true },
            ManagementCalibrationArtifact = ManagementCalibrationArtifact,
            MetaModel = MetaModelArtifact is null
                ? MetaModel
                : MetaModel with { Enabled = true },
            MetaModelArtifact = MetaModelArtifact,
            PortfolioRisk = new PortfolioRiskOptions
            {
                MaximumTotalOpenRiskPercent = MaximumPortfolioHeatPercent,
                MaximumPendingRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumStrategyRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumInstrumentRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumCurrencyStopRiskPercent = Math.Min(0.75m, MaximumPortfolioHeatPercent),
                MaximumNetCurrencyExposurePercent = MaximumNetCurrencyExposurePercent,
                MaximumGrossCurrencyExposurePercent = MaximumGrossCurrencyExposurePercent
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

    private static AlfonsoEntryPolicy ParseAlfonsoEntryPolicy(string? value) => value?.ToLowerInvariant() switch
    {
        null or "core" => AlfonsoEntryPolicy.Core,
        "lower-reversal" => AlfonsoEntryPolicy.LowerTimeframeReversal,
        _ => throw new ArgumentException("--alfonso-entry-policy must be core or lower-reversal.")
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

    /// <summary>
    /// The account currency. Defaults to <see cref="Simulator.Models.BacktestRequest.DefaultBaseCurrency"/>
    /// rather than the instrument's quote currency: deriving it meant a GBP/JPY run banked a yen
    /// account while its USD-quoted siblings banked dollars, and nothing in the output said so.
    /// Pass <c>--base-currency</c> to denominate a run in something else.
    /// </summary>
    public string ResolveBaseCurrency() =>
        string.IsNullOrWhiteSpace(BaseCurrency)
            ? Simulator.Models.BacktestRequest.DefaultBaseCurrency
            : BaseCurrency;

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

    /// <summary>
    /// Parses <c>--quote-rate JPY=0.0067,CHF=1.12</c> into quote-currency to account-currency rates.
    /// </summary>
    private static IReadOnlyDictionary<string, decimal> ParseQuoteRates(string? value)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return rates;

        foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate) ||
                rate <= 0m)
            {
                throw new ArgumentException(
                    $"Unknown --quote-rate entry '{entry}'. Use CURRENCY=RATE with a positive rate, " +
                    "for example JPY=0.0067.");
            }

            rates[parts[0].ToUpperInvariant()] = rate;
        }

        return rates;
    }

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
            EnableNeoWaveInvalidationExit =
                values.ContainsKey($"{prefix}-neo-wave-invalidation-exit"),
            NeoWaveInvalidationBufferAtr = ParseDecimal(
                values.GetValueOrDefault($"{prefix}-neo-wave-invalidation-buffer-atr"),
                defaults.NeoWaveInvalidationBufferAtr,
                -1m,
                $"{prefix}-neo-wave-invalidation-buffer-atr",
                allowZero: true),
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

    /// <summary>
    /// One instrument key per line; blank lines and lines starting with '#' are ignored so a
    /// watchlist file can carry comments. Merged (union, deduplicated) with any --instruments
    /// tokens given on the same command line rather than requiring one or the other.
    /// </summary>
    private static IEnumerable<string> ReadInstrumentsFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];
        string resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            throw new ArgumentException($"--instruments-file '{resolved}' does not exist.");
        return File.ReadLines(resolved)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
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

    /// <summary>
    /// Parses --alfonso-min-grade. Absent means no gate, which is module 7's scoring measured rather
    /// than trusted; the module never states a pass mark.
    /// </summary>
    private static ZoneGrade ParseZoneGrade(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ZoneGrade.Weak;

        return value.Trim().ToLowerInvariant() switch
        {
            "weak" or "none" or "any" => ZoneGrade.Weak,
            "medium" => ZoneGrade.Medium,
            "strong" => ZoneGrade.Strong,
            _ => throw new ArgumentException(
                $"--alfonso-min-grade must be weak, medium or strong; got '{value}'.")
        };
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

    private static ChartAnnotationOptions EnableStructuralDetectorsIfNeeded(
        ChartAnnotationOptions annotation,
        IReadOnlyList<string> strategies,
        IReadOnlyList<StrategyInstrumentAssignment>? assignments)
    {
        bool structural = strategies.Any(item =>
                string.Equals(item, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase)) ||
            assignments is { Count: > 0 } list && list.Any(item =>
                string.Equals(item.StrategyType, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase) ||
                item.AgentDefinitionOverride?.Kind == TradingAgentKind.StructuralConfluence);
        if (!structural)
            return annotation;

        return annotation with
        {
            SupplyDemand = annotation.SupplyDemand with { Enabled = true },
            Liquidity = annotation.Liquidity with { Enabled = true }
        };
    }
}
