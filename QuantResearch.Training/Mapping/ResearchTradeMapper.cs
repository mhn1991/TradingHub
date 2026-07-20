using Brokers.Models;
using QuantResearch.Calibration;
using QuantResearch.Models;
using Simulator.Models;

namespace QuantResearch.Training.Mapping;

/// <summary>
/// Maps the engine's <see cref="SimulatedTradeRecord"/> onto QuantResearch's simplified
/// trade/outcome projections. Lives here (not in <c>QuantResearch</c>) so that project stays
/// free of a Simulator project reference; this project already depends on Simulator for
/// running the backtests that produce these trades in the first place.
/// </summary>
public static class ResearchTradeMapper
{
    /// <summary>
    /// Maps a closed trade. Open (still active) trades return null so research metrics match
    /// the simulator's closed-trade performance surface.
    /// </summary>
    public static ResearchTrade? ToResearchTrade(SimulatedTradeRecord trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        if (trade.ClosedAt is null)
            return null;

        DateTimeOffset opened = trade.OpenedAt ?? trade.SetupStartedAt;
        DateTimeOffset closed = trade.ClosedAt.Value;
        decimal stopDistance = trade.EntryPrice is decimal entry && trade.InitialStopLossPrice is decimal stop
            ? Math.Abs(entry - stop)
            : 0m;
        (decimal partialR, decimal runnerR) = ResolveContributionR(trade);

        return new ResearchTrade
        {
            TradeId = trade.PositionId ?? $"{trade.SetupId}:{opened:O}",
            StrategyId = trade.StrategyId,
            PlaybookId = trade.PlaybookId,
            Instrument = trade.Instrument.Value,
            InstrumentGroup = InstrumentGroupResolver.Resolve(trade.Instrument),
            Regime = trade.EntryRegime.ToString(),
            SetupType = trade.EntrySetupType,
            Direction = trade.Side.ToString(),
            Session = trade.EntrySession,
            VolatilityBucket = trade.EntryVolatilityBucket,
            Confidence = trade.EntryConfidence,
            OpenedAt = opened,
            ClosedAt = closed,
            RMultiple = trade.RMultiple ?? 0m,
            MaximumFavourableExcursionR = trade.MaximumFavourableExcursionR ?? 0m,
            MaximumAdverseExcursionR = trade.MaximumAdverseExcursionR ?? 0m,
            StopDistance = stopDistance,
            PartialExitContributionR = partialR,
            RunnerContributionR = runnerR,
            NeoWaveHypothesisId = trade.EntryNeoWaveHypothesisId,
            NeoWavePatternType = trade.EntryNeoWavePatternType,
            NeoWaveStructuralScore = trade.EntryNeoWaveStructuralScore,
            NeoWaveConflictScore = trade.EntryNeoWaveConflictScore,
            NeoWaveInvalidationPrice = trade.EntryNeoWaveInvalidationPrice,
            NeoWaveRiskMultiplier = Math.Clamp(trade.NeoWaveRiskMultiplier ?? 1m, 0m, 1m)
        };
    }

    public static IReadOnlyList<ResearchTrade> ToResearchTrades(IEnumerable<SimulatedTradeRecord> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        return trades
            .Select(ToResearchTrade)
            .Where(trade => trade is not null)
            .Select(trade => trade!)
            .ToArray();
    }

    public static SetupOutcome? ToSetupOutcome(SimulatedTradeRecord trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        if (trade.ClosedAt is null)
            return null;

        decimal rMultiple = trade.RMultiple ?? 0m;
        return new SetupOutcome
        {
            StrategyId = trade.StrategyId,
            PlaybookId = trade.PlaybookId,
            InstrumentGroup = InstrumentGroupResolver.Resolve(trade.Instrument),
            Regime = trade.EntryRegime.ToString(),
            Confidence = trade.EntryConfidence,
            Won = rMultiple > 0m,
            RMultiple = rMultiple
        };
    }

    public static IReadOnlyList<SetupOutcome> ToSetupOutcomes(IEnumerable<SimulatedTradeRecord> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        return trades
            .Select(ToSetupOutcome)
            .Where(outcome => outcome is not null)
            .Select(outcome => outcome!)
            .ToArray();
    }

    /// <summary>
    /// Requires <see cref="SimulatedTradeRecord.ExcursionPath"/> to be populated, i.e. the run
    /// that produced this trade must have had <see cref="BacktestRuntimeOptions.DetailedExcursionTracking"/>
    /// enabled - returns null otherwise rather than fabricating a fake single-point path.
    /// Open trades are also skipped.
    /// </summary>
    public static TradePathObservation? ToTradePathObservation(SimulatedTradeRecord trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ResearchTrade? researchTrade = ToResearchTrade(trade);
        if (researchTrade is null)
            return null;
        if (trade.ExcursionPath is not { Count: > 0 } path)
            return null;

        return new TradePathObservation
        {
            Trade = researchTrade,
            Path = path
                .Select(point => new TradePathPoint
                {
                    BarsAfterEntry = point.BarsAfterEntry,
                    MfeR = point.MfeR,
                    MaeR = point.MaeR
                })
                .ToArray()
        };
    }

    /// <summary>
    /// Converts cash partial/runner P&amp;L into R using the same initial-risk unit the
    /// simulator used for <see cref="SimulatedTradeRecord.RMultiple"/> (net / initial risk).
    /// </summary>
    private static (decimal PartialR, decimal RunnerR) ResolveContributionR(SimulatedTradeRecord trade)
    {
        decimal? initialRisk = ResolveInitialRiskAccountCurrency(trade);
        if (initialRisk is null or <= 0m)
            return (0m, trade.RMultiple ?? 0m);

        decimal partialR = trade.RealizedPartialNetProfitLoss / initialRisk.Value;
        decimal residualNet = trade.NetProfitLoss - trade.RealizedPartialNetProfitLoss;
        decimal runnerR = residualNet / initialRisk.Value;
        return (partialR, runnerR);
    }

    private static decimal? ResolveInitialRiskAccountCurrency(SimulatedTradeRecord trade)
    {
        if (trade.PlannedStopRiskAccountCurrency is decimal planned && planned > 0m)
            return planned;

        // RMultiple = NetProfitLoss / initialRisk  =>  initialRisk = Net / R when both known.
        if (trade.RMultiple is decimal r && r != 0m)
            return trade.NetProfitLoss / r;

        return null;
    }
}
