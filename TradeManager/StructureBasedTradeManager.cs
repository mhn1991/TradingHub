using Brokers.Models;
using ChartAnnotator.Models;

namespace TradeManager;

public enum TradeManagementAction
{
    Hold,
    MoveStop,
    Exit
}

public sealed record StructureBasedTradeManagementOptions
{
    public decimal BreakEvenActivationR { get; init; } = 1m;
    public decimal StructureTrailActivationR { get; init; } = 1.5m;
    public decimal AtrBufferMultiplier { get; init; } = 0.25m;
    public decimal BreakEvenBufferAtr { get; init; } = 0.05m;
    public decimal MinimumStopImprovementAtr { get; init; } = 0.05m;
    public bool ExitOnAdverseStructureBreak { get; init; } = true;
}

public sealed record ManagedTradeState
{
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialStopPrice { get; init; }
    public required decimal CurrentStopPrice { get; init; }
    public required decimal CurrentPrice { get; init; }
}

public sealed record TradeManagementRecommendation
{
    public required TradeManagementAction Action { get; init; }
    public decimal? ProposedStopPrice { get; init; }
    public required decimal OpenProfitR { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Produces deterministic stop/exit recommendations from confirmed chart structure.
/// It never moves a stop away from the entry-side risk boundary.
/// </summary>
public sealed class StructureBasedTradeManager
{
    private readonly StructureBasedTradeManagementOptions _options;

    public StructureBasedTradeManager(StructureBasedTradeManagementOptions? options = null)
    {
        _options = options ?? new StructureBasedTradeManagementOptions();
        if (_options.BreakEvenActivationR <= 0m ||
            _options.StructureTrailActivationR < _options.BreakEvenActivationR ||
            _options.AtrBufferMultiplier < 0m ||
            _options.BreakEvenBufferAtr < 0m ||
            _options.MinimumStopImprovementAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(analysis);
        Validate(trade, analysis);

        decimal initialRisk = Math.Abs(trade.EntryPrice - trade.InitialStopPrice);
        decimal favourableMove = trade.Side == OrderSide.Buy
            ? trade.CurrentPrice - trade.EntryPrice
            : trade.EntryPrice - trade.CurrentPrice;
        decimal openProfitR = favourableMove / initialRisk;

        if (_options.ExitOnAdverseStructureBreak && IsAdverseBreak(trade.Side, analysis.MarketStructure.Break))
        {
            return new TradeManagementRecommendation
            {
                Action = TradeManagementAction.Exit,
                OpenProfitR = openProfitR,
                Reason = $"An adverse {analysis.MarketStructure.Break} market-structure break was confirmed."
            };
        }

        decimal? atr = analysis.Indicators.Atr;
        if (atr is not > 0m || openProfitR < _options.BreakEvenActivationR)
        {
            return Hold(openProfitR, atr is not > 0m
                ? "ATR is not ready, so no dynamic stop can be calculated."
                : $"Open profit {openProfitR:F2}R has not reached the break-even threshold.");
        }

        decimal candidate = BreakEvenCandidate(trade, atr.Value);
        string reason = $"Protecting the trade after it reached {openProfitR:F2}R.";

        if (openProfitR >= _options.StructureTrailActivationR)
        {
            decimal? structuralCandidate = FindStructuralCandidate(trade, analysis, atr.Value);
            if (structuralCandidate is decimal structureStop &&
                IsMoreProtective(trade.Side, structureStop, candidate))
            {
                candidate = structureStop;
                reason = "Trailing behind the nearest confirmed swing/zone with an ATR buffer.";
            }
        }

        decimal minimumImprovement = atr.Value * _options.MinimumStopImprovementAtr;
        if (!ImprovesCurrentStop(trade, candidate, minimumImprovement) ||
            !IsBeforeCurrentPrice(trade.Side, candidate, trade.CurrentPrice))
        {
            return Hold(openProfitR, "No valid structure provides a safer stop improvement yet.");
        }

        return new TradeManagementRecommendation
        {
            Action = TradeManagementAction.MoveStop,
            ProposedStopPrice = candidate,
            OpenProfitR = openProfitR,
            Reason = reason
        };
    }

    private decimal BreakEvenCandidate(ManagedTradeState trade, decimal atr)
    {
        decimal buffer = atr * _options.BreakEvenBufferAtr;
        return trade.Side == OrderSide.Buy
            ? trade.EntryPrice + buffer
            : trade.EntryPrice - buffer;
    }

    private decimal? FindStructuralCandidate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal atr)
    {
        decimal buffer = atr * _options.AtrBufferMultiplier;
        if (trade.Side == OrderSide.Buy)
        {
            decimal[] levels = analysis.PriceZones
                .Where(zone =>
                    (zone.Type is PriceZoneType.Support or PriceZoneType.Mixed) &&
                    zone.UpperPrice < trade.CurrentPrice)
                .Select(zone => zone.LowerPrice)
                .Concat(analysis.Swings
                    .Where(swing => swing.Type == SwingType.Low && swing.Price < trade.CurrentPrice)
                    .Select(swing => swing.Price))
                .ToArray();
            return levels.Length == 0 ? null : levels.Max() - buffer;
        }

        decimal[] resistanceLevels = analysis.PriceZones
            .Where(zone =>
                (zone.Type is PriceZoneType.Resistance or PriceZoneType.Mixed) &&
                zone.LowerPrice > trade.CurrentPrice)
            .Select(zone => zone.UpperPrice)
            .Concat(analysis.Swings
                .Where(swing => swing.Type == SwingType.High && swing.Price > trade.CurrentPrice)
                .Select(swing => swing.Price))
            .ToArray();
        return resistanceLevels.Length == 0 ? null : resistanceLevels.Min() + buffer;
    }

    private static bool IsAdverseBreak(OrderSide side, MarketStructureBreak structureBreak) =>
        side switch
        {
            OrderSide.Buy => structureBreak == MarketStructureBreak.Bearish,
            OrderSide.Sell => structureBreak == MarketStructureBreak.Bullish,
            _ => false
        };

    private static bool IsMoreProtective(OrderSide side, decimal candidate, decimal comparison) =>
        side == OrderSide.Buy ? candidate > comparison : candidate < comparison;

    private static bool ImprovesCurrentStop(
        ManagedTradeState trade,
        decimal candidate,
        decimal minimumImprovement) => trade.Side switch
        {
            OrderSide.Buy => candidate >= trade.CurrentStopPrice + minimumImprovement,
            OrderSide.Sell => candidate <= trade.CurrentStopPrice - minimumImprovement,
            _ => false
        };

    private static bool IsBeforeCurrentPrice(OrderSide side, decimal stop, decimal currentPrice) =>
        side == OrderSide.Buy ? stop < currentPrice : stop > currentPrice;

    private static TradeManagementRecommendation Hold(decimal openProfitR, string reason) => new()
    {
        Action = TradeManagementAction.Hold,
        OpenProfitR = openProfitR,
        Reason = reason
    };

    private static void Validate(ManagedTradeState trade, AnalysisSnapshot analysis)
    {
        if (trade.Instrument.IsEmpty || trade.Instrument != analysis.Instrument)
        {
            throw new ArgumentException("Trade and analysis instruments must match.", nameof(trade));
        }

        if (trade.EntryPrice <= 0m ||
            trade.InitialStopPrice <= 0m ||
            trade.CurrentStopPrice <= 0m ||
            trade.CurrentPrice <= 0m ||
            trade.EntryPrice == trade.InitialStopPrice)
        {
            throw new ArgumentException("Trade prices and initial risk must be positive.", nameof(trade));
        }

        bool initialStopValid = trade.Side switch
        {
            OrderSide.Buy => trade.InitialStopPrice < trade.EntryPrice,
            OrderSide.Sell => trade.InitialStopPrice > trade.EntryPrice,
            _ => false
        };
        if (!initialStopValid)
        {
            throw new ArgumentException("The initial stop is on the wrong side of entry.", nameof(trade));
        }
    }
}
