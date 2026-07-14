using System.Text.Json.Serialization;
using Agent.Strategies;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.MarketData;
using RiskManager;
using RiskManager.Safety;
using Simulator.MarketData;
using TradeManager;

namespace Simulator.Models;

public enum StrategyExecutionMode
{
    Sequential,
    ParallelWorkers
}

public enum AnalysisSharingMode
{
    SharedImmutableSnapshots,
    IndependentPerStrategy
}

public enum StrategyFailurePolicy
{
    StopEntireComparison,
    StopFailedStrategyOnly
}

public enum AmbiguousIntrabarPolicy
{
    ConservativeStopFirst,
    OptimisticTargetFirst,
    NearestToOpenFirst
}

public enum FillModel
{
    HistoricalBidAsk,
    MidpointPlusConfiguredSpread
}

public enum SimulationJobStatus
{
    Queued,
    PreparingData,
    DownloadingData,
    LoadingCache,
    WarmingUp,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Exporting,
    Completed,
    Failed
}

/// <summary>Validated runtime configuration for streamed comparative backtests.</summary>
public sealed record BacktestRuntimeOptions
{
    /// <summary>Legacy alias for <see cref="ExecutionInterval"/>.</summary>
    public BarInterval BaseInterval
    {
        get => ExecutionInterval;
        init => ExecutionInterval = value;
    }

    /// <summary>Finest candle used for fills/stops/OCO/MFE.</summary>
    public BarInterval ExecutionInterval { get; init; } = BarInterval.Minutes(1);

    /// <summary>Smallest candle delivered to ChartAnnotator (default 1m).</summary>
    public BarInterval AnalysisBaseInterval { get; init; } = BarInterval.Minutes(1);

    public IReadOnlyList<BarInterval> AnalysisIntervals { get; init; } =
        [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)];

    public SimulationPrecisionMode PrecisionMode { get; init; } = SimulationPrecisionMode.Fast;
    public HistoricalDataSourceKind SourceKind { get; init; } = HistoricalDataSourceKind.OandaCandles;
    public string? ImportedCandlePath { get; init; }

    public ProgressiveStrategyTimeframes StrategyTimeframes { get; init; } = new();

    public int SourcePageSize { get; init; } = 5_000;
    public int PrefetchCapacity { get; init; } = 20_000;
    public int PrefetchLowWatermark { get; init; } = 5_000;

    public int WarmupDays { get; init; } = 45;
    public int? WarmupBaseCandleCount { get; init; }

    public StrategyExecutionMode StrategyExecutionMode { get; init; } = StrategyExecutionMode.ParallelWorkers;
    public int StrategyChannelCapacity { get; init; } = 4;
    public int MaximumParallelStrategies { get; init; } = 4;
    public StrategyFailurePolicy StrategyFailurePolicy { get; init; } = StrategyFailurePolicy.StopEntireComparison;

    public AnalysisSharingMode AnalysisSharingMode { get; init; } = AnalysisSharingMode.SharedImmutableSnapshots;
    public AmbiguousIntrabarPolicy AmbiguousIntrabarPolicy { get; init; } = AmbiguousIntrabarPolicy.ConservativeStopFirst;

    /// <summary>
    /// Historical bid/ask fill model is not yet wired to OANDA price components.
    /// Keep false so results do not claim HistoricalBidAsk incorrectly.
    /// </summary>
    public bool UseHistoricalBidAsk { get; init; }
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    public int ReplayChunkSize { get; init; } = 5_000;
    public int ExecutionDetailPreEntryFrames { get; init; } = 120;
    public int ExecutionDetailPostExitFrames { get; init; } = 120;
    public int ProgressPublishIntervalMilliseconds { get; init; } = 500;

    public int CandleCapacity { get; init; } = 5_000;
    public int LedgerCapacity { get; init; } = 100_000;
    public int OrderEventCapacity { get; init; } = 16_384;

    public BaseCandleGapPolicy BaseCandleGapPolicy { get; init; } = BaseCandleGapPolicy.ResetIncompleteBuckets;

    public PositionManagementOptions LegacyPositionManagement { get; init; } =
        PositionManagementOptions.LegacyDefaults;

    public PositionManagementOptions ImprovedPositionManagement { get; init; } =
        PositionManagementOptions.ImprovedDefaults;

    /// <summary>
    /// Account-level safety and daily equity-profit protection. Null thresholds leave
    /// the corresponding rule disabled.
    /// </summary>
    public TradingSafetyOptions SafetyOptions { get; init; } = new();

    /// <summary>Order-size calculation and capital/margin reservation policy.</summary>
    public PositionSizingOptions PositionSizing { get; init; } = new();

    public SimulationTimeframeOptions ToTimeframeOptions() => new()
    {
        ExecutionInterval = ExecutionInterval,
        AnalysisBaseInterval = AnalysisBaseInterval,
        AnalysisIntervals = AnalysisIntervals
    };

    public void Validate(int selectedStrategyCount = 1)
    {
        if (!ExecutionInterval.IsValid)
            throw new ArgumentException("ExecutionInterval must be valid.");
        if (!AnalysisBaseInterval.IsValid)
            throw new ArgumentException("AnalysisBaseInterval must be valid.");
        if (AnalysisIntervals is null || AnalysisIntervals.Count == 0)
            throw new ArgumentException("At least one analysis interval is required.");
        if (AnalysisIntervals.Any(interval => !interval.IsValid))
            throw new ArgumentException("Every analysis interval must be valid.");

        ToTimeframeOptions().Validate();
        StrategyTimeframes.Validate();
        LegacyPositionManagement.Validate();
        ImprovedPositionManagement.Validate();
        SafetyOptions.Validate();
        PositionSizing.Validate();

        BarInterval[] derivedAnalysisIntervals = StrategyTimeframes.RequiredIntervals
            .Concat(ResolveManagementIntervals("legacy"))
            .Concat(ResolveManagementIntervals("improved"))
            .Distinct()
            .ToArray();
        foreach (BarInterval interval in derivedAnalysisIntervals)
        {
            if (!interval.IsValid ||
                BarIntervalParser.CompareDuration(interval, AnalysisBaseInterval) < 0)
            {
                throw new ArgumentException(
                    $"Derived analysis interval {BarIntervalParser.Format(interval)} must be " +
                    $">= analysis base {BarIntervalParser.Format(AnalysisBaseInterval)}.");
            }

            if (interval != AnalysisBaseInterval &&
                interval.Unit is BarUnit.Second or BarUnit.Minute or BarUnit.Hour &&
                !BarIntervalParser.IsDivisible(interval, AnalysisBaseInterval))
            {
                throw new ArgumentException(
                    $"Derived analysis interval {BarIntervalParser.Format(interval)} is not aligned with " +
                    $"analysis base {BarIntervalParser.Format(AnalysisBaseInterval)}.");
            }
        }

        HistoricalCapabilityRegistry.ValidateExecutionInterval(SourceKind, ExecutionInterval);

        if (UseHistoricalBidAsk &&
            !HistoricalCapabilityRegistry.Get(SourceKind).SupportsHistoricalBidAsk)
        {
            throw new ArgumentException(
                $"Historical bid/ask is not available for {HistoricalCapabilityRegistry.Get(SourceKind).SourceName}.");
        }

        if (SourceKind == HistoricalDataSourceKind.ImportedSecondCandles)
        {
            if (string.IsNullOrWhiteSpace(ImportedCandlePath) || !File.Exists(ImportedCandlePath))
            {
                throw new ArgumentException(
                    "ImportedCandlePath must point to an existing file for recorded/imported second sources.");
            }
        }

        if (SourcePageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(SourcePageSize), "SourcePageSize must be >= 1.");
        if (PrefetchCapacity <= SourcePageSize)
            throw new ArgumentOutOfRangeException(nameof(PrefetchCapacity), "PrefetchCapacity must be greater than SourcePageSize.");
        if (PrefetchLowWatermark < 1)
            throw new ArgumentOutOfRangeException(nameof(PrefetchLowWatermark), "PrefetchLowWatermark must be >= 1.");
        if (PrefetchLowWatermark >= PrefetchCapacity)
            throw new ArgumentOutOfRangeException(nameof(PrefetchLowWatermark), "PrefetchLowWatermark must be < PrefetchCapacity.");
        if (StrategyChannelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(StrategyChannelCapacity));
        if (MaximumParallelStrategies < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumParallelStrategies));
        if (selectedStrategyCount < 1)
            throw new ArgumentOutOfRangeException(nameof(selectedStrategyCount));
        if (selectedStrategyCount > MaximumParallelStrategies)
            throw new ArgumentException(
                $"Selected strategy count ({selectedStrategyCount}) exceeds MaximumParallelStrategies ({MaximumParallelStrategies}).");
        if (ReplayChunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ReplayChunkSize));
        if (ExecutionDetailPreEntryFrames < 0 || ExecutionDetailPostExitFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(ExecutionDetailPreEntryFrames));
        if (ProgressPublishIntervalMilliseconds < 50)
            throw new ArgumentOutOfRangeException(nameof(ProgressPublishIntervalMilliseconds), "Progress interval must be >= 50ms.");
        if (WarmupDays < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupDays));
        if (WarmupBaseCandleCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupBaseCandleCount));
        if (CandleCapacity < 1 || LedgerCapacity < 1 || OrderEventCapacity < 1)
            throw new ArgumentException("Capacities must be positive.");
        if (!Enum.IsDefined(StrategyExecutionMode) ||
            !Enum.IsDefined(AnalysisSharingMode) ||
            !Enum.IsDefined(StrategyFailurePolicy) ||
            !Enum.IsDefined(AmbiguousIntrabarPolicy))
        {
            throw new ArgumentException("One or more enumeration values are invalid.");
        }
    }

    public PositionManagementOptions GetPositionManagement(string strategyId) =>
        strategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? LegacyPositionManagement
            : ImprovedPositionManagement;

    public BarInterval ResolveManagementInterval(string strategyId)
    {
        PositionManagementOptions options = GetPositionManagement(strategyId);
        return options.MainStructureInterval ?? options.ManagementInterval ??
            StrategyTimeframes.ConfirmationInterval;
    }

    public IReadOnlyList<BarInterval> ResolveManagementIntervals(string strategyId)
    {
        PositionManagementOptions options = GetPositionManagement(strategyId);
        BarInterval fast = options.FastStructureInterval ?? StrategyTimeframes.EntryInterval;
        BarInterval main = options.MainStructureInterval ?? options.ManagementInterval ??
            StrategyTimeframes.ConfirmationInterval;
        BarInterval thesis = options.ThesisInterval ?? StrategyTimeframes.TrendInterval;
        return [fast, main, thesis];
    }

    public OcoFillPolicy ToOcoFillPolicy() => AmbiguousIntrabarPolicy switch
    {
        AmbiguousIntrabarPolicy.OptimisticTargetFirst => OcoFillPolicy.TakeProfitFirst,
        AmbiguousIntrabarPolicy.NearestToOpenFirst => OcoFillPolicy.NearestToOpenFirst,
        _ => OcoFillPolicy.StopLossFirst
    };

    public DateTimeOffset ResolveWarmupFrom(DateTimeOffset evaluationFrom)
    {
        DateTimeOffset byDays = WarmupDays <= 0
            ? evaluationFrom
            : evaluationFrom.AddDays(-WarmupDays);
        if (WarmupBaseCandleCount is null or <= 0)
            return byDays;

        // Rough calendar estimate for execution-candle count (FX weekdays ≈ 5/7).
        double stepMinutes = ExecutionInterval.Unit switch
        {
            BarUnit.Second => ExecutionInterval.Value / 60.0,
            BarUnit.Minute => ExecutionInterval.Value,
            BarUnit.Hour => ExecutionInterval.Value * 60.0,
            _ => 1.0
        };
        double calendarMinutes = WarmupBaseCandleCount.Value * stepMinutes * 7.0 / 5.0;
        DateTimeOffset byCount = evaluationFrom.AddMinutes(-calendarMinutes);
        return byCount < byDays ? byCount : byDays;
    }
}

/// <summary>User/API request that starts a simulation job.</summary>
public sealed record BacktestRequest
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public IReadOnlyList<string> Strategies { get; init; } = ["legacy", "improved"];
    public decimal StartingBalance { get; init; } = 100_000m;
    public string? BaseCurrency { get; init; }
    public decimal Quantity { get; init; } = 1_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;
    public string OutputDirectory { get; init; } = Path.Combine("Dashboard", "public", "data", "simulations");
    public string CacheDirectory { get; init; } = Path.Combine(".cache", "oanda");
    public string JobsDirectory { get; init; } = Path.Combine(".cache", "simulation-jobs");
    public BacktestRuntimeOptions Runtime { get; init; } = new();
    /// <summary>Optional in-memory candle override for tests and CI without OANDA.</summary>
    [JsonIgnore]
    public IReadOnlyList<Candle>? InlineCandles { get; init; }
    [JsonIgnore]
    public string? AccountId { get; init; }
    [JsonIgnore]
    public string? AccessToken { get; init; }

    public void Validate()
    {
        if (Instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.");
        if (From >= To)
            throw new ArgumentException("From must be earlier than To.");
        if (Strategies is null || Strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.");
        if (StartingBalance <= 0 || Quantity <= 0 || Leverage <= 0 || MinimumRewardRisk <= 0)
            throw new ArgumentException("Balance, quantity, leverage, and minimum R:R must be positive.");
        if (!Enum.IsDefined(PriceActionConfirmation) || MinimumPriceActionConfidence is < 0m or > 100m)
            throw new ArgumentException("Price-action confirmation mode and confidence must be valid.");
        if (InlineCandles is null && Runtime.SourceKind == HistoricalDataSourceKind.InlineTestData)
            throw new ArgumentException("InlineTestData requires InlineCandles and is not an external source.");

        // Inline fixtures use the inline capability set (includes 1s for tests).
        BacktestRuntimeOptions runtime = InlineCandles is not null
            ? Runtime with { SourceKind = HistoricalDataSourceKind.InlineTestData }
            : Runtime;
        runtime.Validate(Strategies.Count);
    }

    public DateTimeOffset ResolveWarmupFrom() => Runtime.ResolveWarmupFrom(From);
}
