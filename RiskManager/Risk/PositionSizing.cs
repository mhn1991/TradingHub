using Agent.Models;
using Brokers.Models;

namespace RiskManager;

public enum PositionSizingMode
{
    FixedQuantity,
    FixedCashRisk,
    FixedFractionalRisk
}

/// <summary>
/// Converts a strategy setup into an executable quantity. Risk is measured in the
/// account currency from the original entry-to-stop distance plus estimated round-trip
/// costs. Quantity is always rounded down so the configured risk budget is not exceeded.
/// </summary>
public sealed record PositionSizingOptions
{
    public PositionSizingMode Mode { get; init; } = PositionSizingMode.FixedQuantity;
    public decimal FixedQuantity { get; init; } = 1_000m;
    public decimal FixedCashRisk { get; init; } = 250m;
    /// <summary>Percentage points, so 0.5 means one half of one percent.</summary>
    public decimal RiskPercentOfEquity { get; init; } = 0.5m;
    public decimal MinimumQuantity { get; init; } = 1m;
    public decimal? MaximumQuantity { get; init; }
    public decimal QuantityStep { get; init; } = 1m;
    public decimal MaximumAccountMarginUsagePercent { get; init; } = 30m;
    public decimal MaximumSinglePositionMarginPercent { get; init; } = 10m;
    public decimal Leverage { get; init; } = 20m;
    /// <summary>Spread, entry/exit slippage and commissions expressed as total basis points.</summary>
    public decimal EstimatedRoundTripCostBasisPoints { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) ||
            FixedQuantity <= 0m ||
            FixedCashRisk <= 0m ||
            RiskPercentOfEquity is <= 0m or > 100m ||
            MinimumQuantity <= 0m ||
            MaximumQuantity is <= 0m ||
            (MaximumQuantity is decimal maximum && maximum < MinimumQuantity) ||
            QuantityStep <= 0m ||
            MaximumAccountMarginUsagePercent is <= 0m or > 100m ||
            MaximumSinglePositionMarginPercent is <= 0m or > 100m ||
            MaximumSinglePositionMarginPercent > MaximumAccountMarginUsagePercent ||
            Leverage <= 0m ||
            EstimatedRoundTripCostBasisPoints < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PositionSizingOptions),
                "Position-sizing values must be positive and percentage values must be within 0-100.");
        }
    }
}

public sealed record PositionSizingContext
{
    public required AgentDecision Decision { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public required IReadOnlyList<AccountSnapshot> Accounts { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    /// <summary>Value of one quote-currency unit in the account currency.</summary>
    public required decimal QuoteToAccountCurrencyRate { get; init; }
}

public sealed record PositionSizingResult
{
    public required bool Approved { get; init; }
    public required decimal Quantity { get; init; }
    public decimal? RiskBudget { get; init; }
    public decimal? EstimatedLossAtStop { get; init; }
    public decimal? EstimatedMargin { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
}

public interface IPositionSizer
{
    PositionSizingResult Calculate(PositionSizingContext context);
}

public sealed class PositionSizer : IPositionSizer
{
    private readonly PositionSizingOptions _options;

    public PositionSizer(PositionSizingOptions? options = null)
    {
        _options = options ?? new PositionSizingOptions();
        _options.Validate();
    }

    public PositionSizingResult Calculate(PositionSizingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Decision);
        ArgumentNullException.ThrowIfNull(context.Accounts);
        ArgumentNullException.ThrowIfNull(context.Positions);

        AgentDecision decision = context.Decision;
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
        {
            return Reject("SizingNotApplicable", "Position sizing is only applicable to opening buy/sell decisions.");
        }

        if (decision.QuantityUnit is not (QuantityUnit.Units or QuantityUnit.BaseAsset))
        {
            return Reject(
                "UnsupportedQuantityUnit",
                $"Risk-based sizing cannot calculate {decision.QuantityUnit} quantities.");
        }

        decimal reference = decision.ReferencePrice ?? decision.LimitPrice ?? decision.StopPrice ?? 0m;

        // FixedQuantity is the backwards-compatible/manual mode. It intentionally does
        // not invent account-currency conversions or reject legacy decisions that do not
        // carry a reference/stop. Safe Dashboard and CLI defaults use risk-based sizing.
        if (_options.Mode == PositionSizingMode.FixedQuantity)
        {
            decimal fixedQuantity = context.RequestedQuantity > 0m
                ? context.RequestedQuantity
                : _options.FixedQuantity;
            if (_options.MaximumQuantity is decimal fixedMaximum)
                fixedQuantity = Math.Min(fixedQuantity, fixedMaximum);
            fixedQuantity = RoundDown(fixedQuantity, _options.QuantityStep);
            if (fixedQuantity < _options.MinimumQuantity)
            {
                return Reject(
                    "QuantityBelowMinimum",
                    $"Fixed quantity {fixedQuantity:F8} is below the minimum {_options.MinimumQuantity:F8}.");
            }

            return new PositionSizingResult
            {
                Approved = true,
                Quantity = fixedQuantity,
                ReasonCode = PositionSizingMode.FixedQuantity.ToString(),
                Reason = $"Using configured fixed quantity {fixedQuantity:F8}."
            };
        }

        if (context.QuoteToAccountCurrencyRate <= 0m)
        {
            return Reject(
                "MissingCurrencyConversion",
                "A positive quote-to-account-currency conversion rate is required.");
        }

        AccountSnapshot? account = context.Accounts.FirstOrDefault(item => item.Balance is > 0m);
        if (account?.Balance is not decimal balance)
        {
            return Reject("MissingAccountBalance", "A positive account balance is required for sizing.");
        }

        decimal equity = balance + (account.UnrealizedProfitLoss ?? 0m);
        if (equity <= 0m)
        {
            return Reject("NonPositiveEquity", "Account equity must be positive before a position can be sized.");
        }

        decimal quantity;
        decimal? riskBudget = null;
        decimal? perUnitLoss = null;

        {
            if (reference <= 0m || decision.StopLossPrice is not decimal stop)
            {
                return Reject(
                    "MissingEntryOrStop",
                    "Risk-based sizing requires a positive reference price and protective stop.");
            }

            decimal riskDistance = Math.Abs(reference - stop);
            if (riskDistance <= 0m)
            {
                return Reject("InvalidStopDistance", "Entry-to-stop distance must be positive.");
            }

            riskBudget = _options.Mode == PositionSizingMode.FixedCashRisk
                ? _options.FixedCashRisk
                : equity * _options.RiskPercentOfEquity / 100m;

            decimal estimatedCostDistance =
                reference * _options.EstimatedRoundTripCostBasisPoints / 10_000m;
            perUnitLoss = (riskDistance + estimatedCostDistance) *
                context.QuoteToAccountCurrencyRate;
            if (perUnitLoss <= 0m)
            {
                return Reject("InvalidPerUnitRisk", "Calculated per-unit account risk is not positive.");
            }

            quantity = riskBudget.Value / perUnitLoss.Value;
        }

        if (reference <= 0m)
        {
            return Reject("MissingReferencePrice", "A positive reference price is required to enforce margin limits.");
        }

        decimal marginPerUnit = reference * context.QuoteToAccountCurrencyRate / _options.Leverage;
        if (marginPerUnit <= 0m)
        {
            return Reject("InvalidMarginRate", "Calculated margin per unit is not positive.");
        }

        decimal currentMargin = account.MarginUsed ?? 0m;
        decimal maximumAccountMargin = equity * _options.MaximumAccountMarginUsagePercent / 100m;
        decimal availableAccountMarginBudget = Math.Max(0m, maximumAccountMargin - currentMargin);
        decimal maximumSinglePositionMargin =
            equity * _options.MaximumSinglePositionMarginPercent / 100m;
        decimal marginCappedQuantity = Math.Min(
            availableAccountMarginBudget / marginPerUnit,
            maximumSinglePositionMargin / marginPerUnit);
        quantity = Math.Min(quantity, marginCappedQuantity);

        if (_options.MaximumQuantity is decimal maximumQuantity)
        {
            quantity = Math.Min(quantity, maximumQuantity);
        }

        quantity = RoundDown(quantity, _options.QuantityStep);
        if (quantity < _options.MinimumQuantity)
        {
            return Reject(
                "QuantityBelowMinimum",
                $"Risk and margin limits allow {quantity:F8}, below the minimum {_options.MinimumQuantity:F8}.");
        }

        decimal estimatedLoss = perUnitLoss is decimal lossPerUnit
            ? lossPerUnit * quantity
            : decision.StopLossPrice is decimal fixedStop
                ? Math.Abs(reference - fixedStop) * quantity * context.QuoteToAccountCurrencyRate
                : 0m;
        decimal estimatedMargin = marginPerUnit * quantity;

        return new PositionSizingResult
        {
            Approved = true,
            Quantity = quantity,
            RiskBudget = riskBudget,
            EstimatedLossAtStop = estimatedLoss,
            EstimatedMargin = estimatedMargin,
            ReasonCode = _options.Mode.ToString(),
            Reason = $"Sized {quantity:F8} units from a {riskBudget:F2} risk budget; " +
                $"estimated stop loss {estimatedLoss:F2} and margin {estimatedMargin:F2}."
        };
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (value <= 0m)
            return 0m;
        return decimal.Floor(value / step) * step;
    }

    private static PositionSizingResult Reject(string code, string reason) => new()
    {
        Approved = false,
        Quantity = 0m,
        ReasonCode = code,
        Reason = reason
    };
}
