using Brokers.Models;

namespace PortfolioManager.Risk;

public sealed record PortfolioRiskOptions
{
    public decimal MaximumTotalOpenRiskPercent { get; init; } = 1.5m;
    public decimal MaximumPendingRiskPercent { get; init; } = 0.75m;
    public decimal MaximumStrategyRiskPercent { get; init; } = 0.75m;
    public decimal MaximumInstrumentRiskPercent { get; init; } = 0.75m;
    public decimal MaximumCurrencyRiskPercent { get; init; } = 0.75m;
    public decimal MaximumMarginUsagePercent { get; init; } = 30m;
    public decimal MaximumSinglePositionMarginPercent { get; init; } = 10m;
    public decimal MinimumUnallocatedMarginReservePercent { get; init; } = 30m;
    public int MaximumOpenPositions { get; init; } = 3;

    public void Validate()
    {
        decimal[] percentages =
        [
            MaximumTotalOpenRiskPercent,
            MaximumPendingRiskPercent,
            MaximumStrategyRiskPercent,
            MaximumInstrumentRiskPercent,
            MaximumCurrencyRiskPercent,
            MaximumMarginUsagePercent,
            MaximumSinglePositionMarginPercent,
            MinimumUnallocatedMarginReservePercent
        ];
        if (percentages.Any(value => value is <= 0m or > 100m) ||
            MaximumPendingRiskPercent > MaximumTotalOpenRiskPercent ||
            MaximumStrategyRiskPercent > MaximumTotalOpenRiskPercent ||
            MaximumInstrumentRiskPercent > MaximumTotalOpenRiskPercent ||
            MaximumSinglePositionMarginPercent > MaximumMarginUsagePercent ||
            MaximumMarginUsagePercent + MinimumUnallocatedMarginReservePercent > 100m ||
            MaximumOpenPositions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PortfolioRiskOptions));
        }
    }
}

public sealed record PortfolioPositionLot
{
    public required string LotId { get; init; }
    public required string StrategyId { get; init; }
    public required string DecisionId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal CurrentPrice { get; init; }
    public decimal? ProtectiveStopPrice { get; init; }
    public decimal EstimatedExitCostAccountCurrency { get; init; }
    public decimal MarginAccountCurrency { get; init; }
    public decimal QuoteToAccountCurrencyRate { get; init; } = 1m;
    public decimal ContractMultiplier { get; init; } = 1m;
    public string CorrelationClusterId { get; init; } = "unclassified";
}

public sealed record PortfolioHeatSnapshot
{
    public required decimal OpenHeat { get; init; }
    public required decimal PendingHeat { get; init; }
    public required decimal TotalHeat { get; init; }
    public required decimal Equity { get; init; }
    public required decimal TotalHeatPercent { get; init; }
    public required IReadOnlyDictionary<string, decimal> StrategyHeat { get; init; }
    public required IReadOnlyDictionary<InstrumentKey, decimal> InstrumentHeat { get; init; }
    public required IReadOnlyDictionary<string, decimal> ClusterHeat { get; init; }
}

public sealed record PortfolioRiskDecision
{
    public required bool Approved { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public required PortfolioHeatSnapshot Heat { get; init; }
}

public interface IPortfolioRiskManager
{
    PortfolioRiskDecision Evaluate(
        decimal equity,
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations);
}

public sealed class PortfolioRiskManager : IPortfolioRiskManager
{
    private readonly PortfolioRiskOptions _options;

    public PortfolioRiskManager(PortfolioRiskOptions? options = null)
    {
        _options = options ?? new PortfolioRiskOptions();
        _options.Validate();
    }

    public PortfolioRiskDecision Evaluate(
        decimal equity,
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations)
    {
        PortfolioHeatSnapshot heat = PortfolioHeatCalculator.Calculate(equity, positions, reservations);
        bool approved = equity > 0m && heat.TotalHeatPercent <= _options.MaximumTotalOpenRiskPercent;
        return new PortfolioRiskDecision
        {
            Approved = approved,
            Heat = heat,
            ReasonCode = approved ? "PortfolioHeatWithinLimit" : "PortfolioHeatLimit",
            Explanation = approved
                ? $"Portfolio heat is {heat.TotalHeatPercent:F3}% of equity."
                : $"Portfolio heat {heat.TotalHeatPercent:F3}% exceeds {_options.MaximumTotalOpenRiskPercent:F3}%."
        };
    }
}

public static class PortfolioHeatCalculator
{
    public static PortfolioHeatSnapshot Calculate(
        decimal equity,
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(reservations);
        decimal Risk(PortfolioPositionLot lot)
        {
            if (lot.ProtectiveStopPrice is not decimal stop) return 0m;
            decimal lossDistance = lot.Side == OrderSide.Buy
                ? Math.Max(0m, lot.CurrentPrice - stop)
                : Math.Max(0m, stop - lot.CurrentPrice);
            return lossDistance * lot.Quantity * lot.ContractMultiplier *
                lot.QuoteToAccountCurrencyRate + lot.EstimatedExitCostAccountCurrency;
        }

        decimal open = positions.Sum(Risk);
        decimal pending = reservations.Sum(item => item.RemainingRiskAccountCurrency);
        var strategy = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var instrument = new Dictionary<InstrumentKey, decimal>();
        var cluster = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (PortfolioPositionLot lot in positions.OrderBy(item => item.LotId, StringComparer.Ordinal))
        {
            decimal risk = Risk(lot);
            Add(strategy, lot.StrategyId, risk);
            Add(instrument, lot.Instrument, risk);
            Add(cluster, lot.CorrelationClusterId, risk);
        }
        foreach (PortfolioReservation reservation in reservations.OrderBy(item => item.CreatedSequence))
        {
            Add(strategy, reservation.StrategyId, reservation.RemainingRiskAccountCurrency);
            Add(instrument, reservation.Instrument, reservation.RemainingRiskAccountCurrency);
            Add(cluster, reservation.CorrelationClusterId, reservation.RemainingRiskAccountCurrency);
        }

        return new PortfolioHeatSnapshot
        {
            OpenHeat = open,
            PendingHeat = pending,
            TotalHeat = open + pending,
            Equity = equity,
            TotalHeatPercent = equity > 0m ? (open + pending) / equity * 100m : 0m,
            StrategyHeat = strategy,
            InstrumentHeat = instrument,
            ClusterHeat = cluster
        };
    }

    private static void Add<TKey>(Dictionary<TKey, decimal> values, TKey key, decimal amount)
        where TKey : notnull => values[key] = values.GetValueOrDefault(key) + amount;
}
