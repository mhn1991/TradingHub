using Agent.Models;
using PortfolioManager.Risk;
using RiskManager;

namespace PortfolioManager.Allocation;

public sealed record PortfolioOpportunity
{
    public required string StrategyId { get; init; }
    public required AgentDecision Decision { get; init; }
    public required PositionSizingResult Sizing { get; init; }
    public required decimal SetupQuality { get; init; }
    public required decimal ExpectedRewardRisk { get; init; }
    public required decimal RegimeSuitability { get; init; }
    public required decimal TransactionCostPenalty { get; init; }
    public required decimal CorrelationPenalty { get; init; }
    public required decimal CurrencyConcentrationPenalty { get; init; }
    public required decimal MarginConsumptionPenalty { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long Sequence { get; init; }
    public bool AllowPartialAllocation { get; init; } = true;
    public decimal MinimumQuantity { get; init; } = 1m;
    public decimal QuantityStep { get; init; } = 1m;
}

public sealed record CapitalAllocatorOptions
{
    public decimal SetupQualityWeight { get; init; } = 1m;
    public decimal RewardRiskWeight { get; init; } = 1m;
    public decimal RegimeSuitabilityWeight { get; init; } = 1m;

    public void Validate()
    {
        if (SetupQualityWeight < 0m || RewardRiskWeight < 0m || RegimeSuitabilityWeight < 0m)
            throw new ArgumentOutOfRangeException(nameof(CapitalAllocatorOptions));
    }
}

public sealed record PortfolioAllocationContext
{
    public required decimal AccountEquity { get; init; }
    public decimal CurrentOpenRiskAccountCurrency { get; init; }
    public decimal CurrentMarginUsed { get; init; }
    public int CurrentOpenPositions { get; init; }
    public IReadOnlyDictionary<string, decimal> OpenStrategyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
    public IReadOnlyDictionary<Brokers.Models.InstrumentKey, decimal> OpenInstrumentRisk { get; init; } =
        new Dictionary<Brokers.Models.InstrumentKey, decimal>();
    public IReadOnlyDictionary<string, decimal> OpenCurrencyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public required Func<PortfolioOpportunity, decimal, IReadOnlyDictionary<string, decimal>> CurrencyExposureFactory { get; init; }
    public required Func<PortfolioOpportunity, string> CorrelationClusterFactory { get; init; }
}

public sealed record PortfolioAllocationDecision
{
    public required PortfolioOpportunity Opportunity { get; init; }
    public required decimal Score { get; init; }
    public required bool Approved { get; init; }
    public required decimal OriginalQuantity { get; init; }
    public required decimal AllocatedQuantity { get; init; }
    public string? ReservationId { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public interface ICapitalAllocator
{
    IReadOnlyList<PortfolioAllocationDecision> Allocate(
        IReadOnlyList<PortfolioOpportunity> opportunities,
        PortfolioAllocationContext context);
}

public sealed class CapitalAllocator : ICapitalAllocator
{
    private readonly IPortfolioReservationBook _reservations;
    private readonly CapitalAllocatorOptions _options;

    public CapitalAllocator(IPortfolioReservationBook reservations, CapitalAllocatorOptions? options = null)
    {
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _options = options ?? new CapitalAllocatorOptions();
        _options.Validate();
    }

    public IReadOnlyList<PortfolioAllocationDecision> Allocate(
        IReadOnlyList<PortfolioOpportunity> opportunities,
        PortfolioAllocationContext context)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        ArgumentNullException.ThrowIfNull(context);
        var ranked = opportunities
            .Select(opportunity => (Opportunity: opportunity, Score: Score(opportunity)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Opportunity.Sizing.EstimatedLossAtStop ?? decimal.MaxValue)
            .ThenBy(item => item.Opportunity.CreatedAt)
            .ThenBy(item => item.Opportunity.StrategyId, StringComparer.Ordinal)
            .ThenBy(item => item.Opportunity.Decision.DecisionId, StringComparer.Ordinal)
            .ToArray();
        var results = new List<PortfolioAllocationDecision>(ranked.Length);

        foreach ((PortfolioOpportunity opportunity, decimal score) in ranked)
        {
            decimal original = opportunity.Sizing.Quantity;
            if (!opportunity.Sizing.Approved || original <= 0m ||
                opportunity.Sizing.EstimatedLossAtStop is not decimal risk ||
                opportunity.Sizing.EstimatedMargin is not decimal margin)
            {
                string reasonCode = opportunity.Sizing.Approved
                    ? "SizingUnavailable"
                    : opportunity.Sizing.ReasonCode;
                string explanation = opportunity.Sizing.Approved
                    ? "Approved monetary sizing is required."
                    : opportunity.Sizing.Reason;
                results.Add(Rejected(opportunity, score, original, reasonCode, explanation));
                continue;
            }

            decimal allocated = original;
            PortfolioReservationResult reservationResult = TryReserve(opportunity, context, allocated, risk, margin);
            if (!reservationResult.Approved && opportunity.AllowPartialAllocation)
            {
                // Deterministic monotonic search at broker quantity-step resolution.
                decimal low = opportunity.MinimumQuantity;
                decimal high = original;
                decimal best = 0m;
                while (high >= low)
                {
                    decimal midpoint = RoundDown((low + high) / 2m, opportunity.QuantityStep);
                    if (midpoint < opportunity.MinimumQuantity) break;
                    PortfolioReservationResult attempt = TryReserve(
                        opportunity,
                        context,
                        midpoint,
                        risk * midpoint / original,
                        margin * midpoint / original,
                        suffix: $"resize-{midpoint}");
                    if (attempt.Approved)
                    {
                        best = midpoint;
                        reservationResult = attempt;
                        break;
                    }
                    high = midpoint - opportunity.QuantityStep;
                }
                allocated = best;
            }

            if (!reservationResult.Approved || allocated < opportunity.MinimumQuantity)
            {
                results.Add(Rejected(opportunity, score, original, reservationResult.ReasonCode, reservationResult.Explanation));
                continue;
            }

            results.Add(new PortfolioAllocationDecision
            {
                Opportunity = opportunity,
                Score = score,
                Approved = true,
                OriginalQuantity = original,
                AllocatedQuantity = allocated,
                ReservationId = reservationResult.Reservation!.ReservationId,
                ReasonCode = allocated < original ? "PortfolioQuantityReduced" : "PortfolioOpportunityApproved",
                Explanation = allocated < original
                    ? $"Quantity was reduced from {original} to {allocated} to fit portfolio capacity."
                    : reservationResult.Explanation
            });
        }
        return results;
    }

    private PortfolioReservationResult TryReserve(
        PortfolioOpportunity opportunity,
        PortfolioAllocationContext context,
        decimal quantity,
        decimal risk,
        decimal margin,
        string? suffix = null)
    {
        string decisionId = opportunity.Decision.DecisionId ?? $"sequence-{opportunity.Sequence}";
        string reservationId = $"res:{opportunity.StrategyId}:{decisionId}" +
            (suffix is null ? string.Empty : $":{suffix}");
        return _reservations.TryReserve(new PortfolioReservationRequest
        {
            Reservation = new PortfolioReservation
            {
                ReservationId = reservationId,
                StrategyId = opportunity.StrategyId,
                DecisionId = decisionId,
                Instrument = opportunity.Decision.Instrument,
                Quantity = quantity,
                PlannedStopRiskAccountCurrency = risk,
                EstimatedMargin = margin,
                CurrencyExposureDelta = context.CurrencyExposureFactory(opportunity, quantity),
                CorrelationClusterId = context.CorrelationClusterFactory(opportunity),
                CreatedAt = opportunity.CreatedAt,
                CreatedSequence = opportunity.Sequence
            },
            AccountEquity = context.AccountEquity,
            CurrentOpenRiskAccountCurrency = context.CurrentOpenRiskAccountCurrency,
            CurrentMarginUsed = context.CurrentMarginUsed,
            CurrentOpenPositions = context.CurrentOpenPositions,
            OpenStrategyRisk = context.OpenStrategyRisk,
            OpenInstrumentRisk = context.OpenInstrumentRisk,
            OpenCurrencyRisk = context.OpenCurrencyRisk
        });
    }

    private decimal Score(PortfolioOpportunity opportunity) =>
        opportunity.SetupQuality * _options.SetupQualityWeight +
        opportunity.ExpectedRewardRisk * _options.RewardRiskWeight +
        opportunity.RegimeSuitability * _options.RegimeSuitabilityWeight -
        opportunity.TransactionCostPenalty - opportunity.CorrelationPenalty -
        opportunity.CurrencyConcentrationPenalty - opportunity.MarginConsumptionPenalty;

    private static PortfolioAllocationDecision Rejected(
        PortfolioOpportunity opportunity,
        decimal score,
        decimal original,
        string code,
        string explanation) => new()
    {
        Opportunity = opportunity,
        Score = score,
        Approved = false,
        OriginalQuantity = original,
        AllocatedQuantity = 0m,
        ReasonCode = code,
        Explanation = explanation
    };

    private static decimal RoundDown(decimal value, decimal step) =>
        step <= 0m ? value : decimal.Floor(value / step) * step;
}
