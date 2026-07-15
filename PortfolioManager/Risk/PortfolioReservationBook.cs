using Brokers.Models;

namespace PortfolioManager.Risk;

public enum PortfolioReleaseReason
{
    Cancelled,
    Rejected,
    Expired,
    FillReconciled,
    SimulationEnded,
    Manual
}

public sealed record PortfolioReservation
{
    public required string ReservationId { get; init; }
    public required string StrategyId { get; init; }
    public required string DecisionId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal PlannedStopRiskAccountCurrency { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required IReadOnlyDictionary<string, decimal> CurrencyExposureDelta { get; init; }
    public required string CorrelationClusterId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long CreatedSequence { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal RemainingQuantity => Math.Max(0m, Quantity - FilledQuantity);
    public decimal RemainingRiskAccountCurrency => Quantity <= 0m
        ? 0m
        : PlannedStopRiskAccountCurrency * RemainingQuantity / Quantity;
    public decimal RemainingMargin => Quantity <= 0m ? 0m : EstimatedMargin * RemainingQuantity / Quantity;
}

public sealed record PortfolioReservationRequest
{
    public required PortfolioReservation Reservation { get; init; }
    public required decimal AccountEquity { get; init; }
    public decimal CurrentOpenRiskAccountCurrency { get; init; }
    public decimal CurrentMarginUsed { get; init; }
    public int CurrentOpenPositions { get; init; }
    public IReadOnlyDictionary<string, decimal> OpenStrategyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
    public IReadOnlyDictionary<InstrumentKey, decimal> OpenInstrumentRisk { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<string, decimal> OpenCurrencyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}

public sealed record PortfolioFillAllocation
{
    public required decimal FilledQuantity { get; init; }
    public required decimal FillRiskAccountCurrency { get; init; }
    public required decimal FillMarginAccountCurrency { get; init; }
}

public sealed record PortfolioReservationResult
{
    public required bool Approved { get; init; }
    public PortfolioReservation? Reservation { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public sealed record PortfolioReservationSnapshot
{
    public required long Version { get; init; }
    public required IReadOnlyList<PortfolioReservation> Reservations { get; init; }
    public required decimal PendingRisk { get; init; }
    public required decimal ReservedMargin { get; init; }
}

public interface IPortfolioReservationBook
{
    PortfolioReservationResult TryReserve(PortfolioReservationRequest request);
    void CommitFill(string reservationId, PortfolioFillAllocation fill);
    void Release(string reservationId, PortfolioReleaseReason reason);
    PortfolioReservationSnapshot Snapshot { get; }
}

/// <summary>One-lock atomic admission and exactly-once reservation lifecycle.</summary>
public sealed class PortfolioReservationBook : IPortfolioReservationBook
{
    private readonly object _sync = new();
    private readonly PortfolioRiskOptions _options;
    private readonly Dictionary<string, PortfolioReservation> _reservations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _released = new(StringComparer.Ordinal);
    private long _version;

    public PortfolioReservationBook(PortfolioRiskOptions? options = null)
    {
        _options = options ?? new PortfolioRiskOptions();
        _options.Validate();
    }

    public PortfolioReservationResult TryReserve(PortfolioReservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reservation);
        PortfolioReservation candidate = request.Reservation;
        if (request.AccountEquity <= 0m || candidate.Quantity <= 0m ||
            candidate.PlannedStopRiskAccountCurrency <= 0m || candidate.EstimatedMargin < 0m ||
            string.IsNullOrWhiteSpace(candidate.ReservationId) || string.IsNullOrWhiteSpace(candidate.StrategyId) ||
            string.IsNullOrWhiteSpace(candidate.DecisionId) || candidate.Instrument.IsEmpty)
            return Reject("InvalidReservation", "Reservation fields and account equity must be positive and complete.");

        lock (_sync)
        {
            if (_reservations.ContainsKey(candidate.ReservationId) || _released.Contains(candidate.ReservationId))
                return Reject("DuplicateReservation", $"Reservation '{candidate.ReservationId}' was already used.");

            decimal pendingRisk = _reservations.Values.Sum(item => item.RemainingRiskAccountCurrency);
            decimal reservedMargin = _reservations.Values.Sum(item => item.RemainingMargin);
            decimal Percent(decimal amount) => amount / request.AccountEquity * 100m;
            if (Percent(pendingRisk + candidate.PlannedStopRiskAccountCurrency) > _options.MaximumPendingRiskPercent)
                return Reject("PendingRiskLimit", "The pending-order risk limit would be exceeded.");
            if (Percent(request.CurrentOpenRiskAccountCurrency + pendingRisk + candidate.PlannedStopRiskAccountCurrency) >
                _options.MaximumTotalOpenRiskPercent)
                return Reject("TotalHeatLimit", "The total portfolio heat limit would be exceeded.");

            decimal strategyRisk = request.OpenStrategyRisk.GetValueOrDefault(candidate.StrategyId) +
                _reservations.Values.Where(item => item.StrategyId == candidate.StrategyId)
                    .Sum(item => item.RemainingRiskAccountCurrency) + candidate.PlannedStopRiskAccountCurrency;
            if (Percent(strategyRisk) > _options.MaximumStrategyRiskPercent)
                return Reject("StrategyRiskLimit", "The strategy heat limit would be exceeded.");

            decimal instrumentRisk = request.OpenInstrumentRisk.GetValueOrDefault(candidate.Instrument) +
                _reservations.Values.Where(item => item.Instrument == candidate.Instrument)
                    .Sum(item => item.RemainingRiskAccountCurrency) + candidate.PlannedStopRiskAccountCurrency;
            if (Percent(instrumentRisk) > _options.MaximumInstrumentRiskPercent)
                return Reject("InstrumentRiskLimit", "The instrument heat limit would be exceeded.");

            foreach ((string currency, decimal delta) in candidate.CurrencyExposureDelta.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                decimal currencyRisk = Math.Abs(request.OpenCurrencyRisk.GetValueOrDefault(currency)) +
                    _reservations.Values.Sum(item => Math.Abs(item.CurrencyExposureDelta.GetValueOrDefault(currency))) +
                    Math.Abs(delta);
                if (Percent(currencyRisk) > _options.MaximumCurrencyRiskPercent)
                    return Reject("CurrencyRiskLimit", $"The {currency} concentration limit would be exceeded.");
            }

            decimal projectedMarginPercent = Percent(request.CurrentMarginUsed + reservedMargin + candidate.EstimatedMargin);
            decimal effectiveMarginLimit = Math.Min(
                _options.MaximumMarginUsagePercent,
                100m - _options.MinimumUnallocatedMarginReservePercent);
            if (projectedMarginPercent > effectiveMarginLimit)
                return Reject("MarginReserveLimit", "The shared margin or unallocated reserve limit would be exceeded.");
            if (Percent(candidate.EstimatedMargin) > _options.MaximumSinglePositionMarginPercent)
                return Reject("SinglePositionMarginLimit", "The single-position margin limit would be exceeded.");
            if (request.CurrentOpenPositions + _reservations.Count >= _options.MaximumOpenPositions)
                return Reject("MaximumOpenPositions", "The maximum number of open and reserved positions has been reached.");

            _reservations.Add(candidate.ReservationId, candidate);
            _version++;
            return new PortfolioReservationResult
            {
                Approved = true,
                Reservation = candidate,
                ReasonCode = "PortfolioRiskReserved",
                Explanation = $"Reserved {candidate.PlannedStopRiskAccountCurrency:F2} risk and {candidate.EstimatedMargin:F2} margin."
            };
        }
    }

    public void CommitFill(string reservationId, PortfolioFillAllocation fill)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentNullException.ThrowIfNull(fill);
        lock (_sync)
        {
            if (!_reservations.TryGetValue(reservationId, out PortfolioReservation? reservation))
                throw new KeyNotFoundException($"Active reservation '{reservationId}' was not found.");
            if (fill.FilledQuantity <= 0m || fill.FillRiskAccountCurrency < 0m || fill.FillMarginAccountCurrency < 0m ||
                fill.FilledQuantity > reservation.RemainingQuantity)
                throw new ArgumentOutOfRangeException(nameof(fill));

            PortfolioReservation updated = reservation with
            {
                FilledQuantity = reservation.FilledQuantity + fill.FilledQuantity
            };
            if (updated.RemainingQuantity <= 0m)
            {
                _reservations.Remove(reservationId);
                _released.Add(reservationId);
            }
            else
            {
                _reservations[reservationId] = updated;
            }
            _version++;
        }
    }

    public void Release(string reservationId, PortfolioReleaseReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_sync)
        {
            if (_released.Contains(reservationId))
                return;
            if (_reservations.Remove(reservationId))
            {
                _released.Add(reservationId);
                _version++;
            }
        }
    }

    public PortfolioReservationSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                PortfolioReservation[] values = _reservations.Values
                    .OrderBy(item => item.CreatedSequence)
                    .ThenBy(item => item.ReservationId, StringComparer.Ordinal)
                    .ToArray();
                return new PortfolioReservationSnapshot
                {
                    Version = _version,
                    Reservations = values,
                    PendingRisk = values.Sum(item => item.RemainingRiskAccountCurrency),
                    ReservedMargin = values.Sum(item => item.RemainingMargin)
                };
            }
        }
    }

    private static PortfolioReservationResult Reject(string code, string explanation) => new()
    {
        Approved = false,
        ReasonCode = code,
        Explanation = explanation
    };
}
