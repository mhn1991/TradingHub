using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;

namespace Dashboard.Contracts;

public sealed record ReplayDataset(
    int SchemaVersion,
    string Title,
    string Instrument,
    DateTimeOffset GeneratedAt,
    string Source,
    ChartAnnotationOptions Parameters,
    IReadOnlyList<ReplaySeries> Series)
{
    public IReadOnlyList<ReplayTrade> Trades { get; init; } = [];
    public ReplayPerformanceSummary? Performance { get; init; }
}



public sealed record BacktestMarketDataset(
    int SchemaVersion,
    string Title,
    string Instrument,
    DateTimeOffset GeneratedAt,
    string Source,
    ChartAnnotationOptions Parameters,
    IReadOnlyList<ReplaySeries> Series);

public sealed record BacktestRunDataset(
    int SchemaVersion,
    string StrategyName,
    string Title,
    IReadOnlyList<ReplayTrade> Trades,
    ReplayPerformanceSummary Performance);

public sealed record ReplayTrade(
    string StrategyName,
    string SetupId,
    string Side,
    DateTimeOffset SetupStartedAt,
    DateTimeOffset? ConfirmationAt,
    DateTimeOffset SignalCreatedAt,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal? SignalPrice,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal Quantity,
    decimal? ExpectedRewardRisk,
    string? StopSource,
    string? TargetSource,
    decimal GrossProfitLoss,
    decimal Commission,
    decimal NetProfitLoss,
    decimal? RMultiple,
    decimal? MaximumFavourableExcursionPrice,
    decimal MaximumFavourableExcursionAmount,
    decimal? MaximumFavourableExcursionR,
    DateTimeOffset? MaximumFavourableExcursionAt,
    decimal? MaximumAdverseExcursionPrice,
    decimal MaximumAdverseExcursionAmount,
    decimal? MaximumAdverseExcursionR,
    DateTimeOffset? MaximumAdverseExcursionAt,
    string ExitReason,
    string SetupReason,
    string? ExitReasonText)
{
    public string? PositionId { get; init; }
    public decimal? InitialStopLossPrice { get; init; }
    public decimal? CurrentStopLossPrice { get; init; }
    public decimal? FinalStopLossPrice { get; init; }
    public int StopAmendmentCount { get; init; }
    public DateTimeOffset? BreakEvenActivatedAt { get; init; }
    public DateTimeOffset? StructureTrailingActivatedAt { get; init; }
    public decimal MaximumLockedInR { get; init; }
    public decimal? AverageExitPrice { get; init; }
    public decimal InitialQuantity { get; init; }
    public decimal RemainingQuantity { get; init; }
    public int PositionReductionCount { get; init; }
    public DateTimeOffset? RunnerActivatedAt { get; init; }
    public DateTimeOffset? ProfitFloorActivatedAt { get; init; }
    public DateTimeOffset? MaximumGivebackProtectionActivatedAt { get; init; }
    public IReadOnlyList<string> CompletedReductionStageIds { get; init; } = [];
    public IReadOnlyList<Simulator.Models.PartialExitRecord> PartialExits { get; init; } = [];
    public IReadOnlyList<Simulator.Models.StopAmendmentRecord> StopAmendments { get; init; } = [];
}

public sealed record ReplayPerformanceSummary(
    int TradeCount,
    int WinningTrades,
    int LosingTrades,
    decimal NetProfit,
    decimal WinRatePercent,
    decimal? AverageR,
    decimal? ProfitFactor,
    decimal MaximumDrawdown,
    string Currency,
    decimal StartingBalance,
    decimal FinalBalance,
    decimal FinalEquity,
    decimal TotalCommission);

public sealed record BacktestManifest(
    int SchemaVersion,
    string Title,
    DateTimeOffset GeneratedAt,
    string Instrument,
    DateTimeOffset From,
    DateTimeOffset To,
    string ExecutionInterval,
    string MarketFile,
    IReadOnlyList<BacktestManifestRun> Runs);

public sealed record BacktestManifestRun(
    string Id,
    string StrategyName,
    string File,
    ReplayPerformanceSummary Performance);

public sealed record ReplaySeries(
    string Interval,
    int IntervalSeconds,
    IReadOnlyList<ReplayFrame> Frames);

public sealed record ReplayFrame(
    int Index,
    DateTimeOffset AvailableAt,
    ReplayCandle Candle,
    IndicatorSnapshot Indicators,
    IReadOnlyList<SwingPoint> Swings,
    IReadOnlyList<PriceZone> PriceZones,
    IReadOnlyList<Trendline> Trendlines,
    IReadOnlyList<PriceChannel> Channels,
    MarketStructureSnapshot MarketStructure,
    PriceActionSnapshot PriceAction,
    ConfidenceScore Confidence,
    double AnalysisMicroseconds);

public sealed record ReplayCandle(
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public static class ReplayContractMapper
{
    public static ReplayFrame ToFrame(
        AnalysisSnapshot snapshot,
        int index,
        double analysisMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Candle candle = snapshot.LatestCandle;
        return new ReplayFrame(
            Index: index,
            AvailableAt: snapshot.AvailableAt,
            Candle: new ReplayCandle(
                candle.OpenTime,
                candle.CloseTime ?? snapshot.AvailableAt,
                candle.Prices.Open,
                candle.Prices.High,
                candle.Prices.Low,
                candle.Prices.Close,
                candle.Volume?.Value ?? 0m),
            Indicators: snapshot.Indicators,
            Swings: snapshot.Swings,
            PriceZones: snapshot.PriceZones,
            Trendlines: snapshot.Trendlines,
            Channels: snapshot.Channels,
            MarketStructure: snapshot.MarketStructure,
            PriceAction: snapshot.PriceAction,
            Confidence: snapshot.Confidence,
            AnalysisMicroseconds: analysisMicroseconds);
    }
}

public static class SimulationReplayMapper
{
    public static IReadOnlyList<ReplayTrade> ToTrades(Simulator.Models.SimulationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Trades.Select(trade => new ReplayTrade(
            trade.StrategyName,
            trade.SetupId,
            trade.Side.ToString(),
            trade.SetupStartedAt,
            trade.ConfirmationAt,
            trade.SignalCreatedAt,
            trade.OpenedAt,
            trade.ClosedAt,
            trade.SignalPrice,
            trade.EntryPrice,
            trade.ExitPrice,
            trade.StopLossPrice,
            trade.TakeProfitPrice,
            trade.Quantity,
            trade.ExpectedRewardRisk,
            trade.StopSource,
            trade.TargetSource,
            trade.GrossProfitLoss,
            trade.Commission,
            trade.NetProfitLoss,
            trade.RMultiple,
            trade.MaximumFavourableExcursionPrice,
            trade.MaximumFavourableExcursionAmount,
            trade.MaximumFavourableExcursionR,
            trade.MaximumFavourableExcursionAt,
            trade.MaximumAdverseExcursionPrice,
            trade.MaximumAdverseExcursionAmount,
            trade.MaximumAdverseExcursionR,
            trade.MaximumAdverseExcursionAt,
            trade.ExitReason.ToString(),
            trade.SetupReason,
            trade.ExitReasonText)
        {
            PositionId = trade.PositionId,
            InitialStopLossPrice = trade.InitialStopLossPrice,
            CurrentStopLossPrice = trade.CurrentStopLossPrice,
            FinalStopLossPrice = trade.FinalStopLossPrice,
            StopAmendmentCount = trade.StopAmendmentCount,
            BreakEvenActivatedAt = trade.BreakEvenActivatedAt,
            StructureTrailingActivatedAt = trade.StructureTrailingActivatedAt,
            MaximumLockedInR = trade.MaximumLockedInR,
            AverageExitPrice = trade.AverageExitPrice,
            InitialQuantity = trade.InitialQuantity,
            RemainingQuantity = trade.RemainingQuantity,
            PositionReductionCount = trade.PositionReductionCount,
            RunnerActivatedAt = trade.RunnerActivatedAt,
            ProfitFloorActivatedAt = trade.ProfitFloorActivatedAt,
            MaximumGivebackProtectionActivatedAt = trade.MaximumGivebackProtectionActivatedAt,
            CompletedReductionStageIds = trade.CompletedReductionStageIds,
            PartialExits = trade.PartialExits,
            StopAmendments = trade.StopAmendments
        }).ToArray();
    }

    public static ReplayPerformanceSummary ToPerformance(Simulator.Models.SimulationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Simulator.Models.SimulatedTradeRecord[] closed = result.Trades
            .Where(trade => trade.ClosedAt is not null)
            .ToArray();
        int wins = closed.Count(trade => trade.NetProfitLoss > 0m);
        int losses = closed.Count(trade => trade.NetProfitLoss < 0m);
        decimal grossProfit = closed.Where(trade => trade.NetProfitLoss > 0m).Sum(trade => trade.NetProfitLoss);
        decimal grossLoss = Math.Abs(closed.Where(trade => trade.NetProfitLoss < 0m).Sum(trade => trade.NetProfitLoss));
        decimal running = 0m;
        decimal peak = 0m;
        decimal drawdown = 0m;
        foreach (Simulator.Models.SimulatedTradeRecord trade in closed.OrderBy(trade => trade.ClosedAt))
        {
            running += trade.NetProfitLoss;
            peak = Math.Max(peak, running);
            drawdown = Math.Max(drawdown, peak - running);
        }
        decimal[] rValues = closed.Where(trade => trade.RMultiple is not null)
            .Select(trade => trade.RMultiple!.Value).ToArray();
        return new ReplayPerformanceSummary(
            closed.Length,
            wins,
            losses,
            closed.Sum(trade => trade.NetProfitLoss),
            closed.Length == 0 ? 0m : wins * 100m / closed.Length,
            rValues.Length == 0 ? null : rValues.Average(),
            grossLoss == 0m ? null : grossProfit / grossLoss,
            drawdown,
            result.Ledger.FirstOrDefault(entry => entry.Type == Simulator.Models.LedgerEntryType.Deposit)?.Currency ?? "USD",
            result.StartingBalance,
            result.FinalBalance,
            result.FinalEquity,
            result.TotalCommission);
    }
}
