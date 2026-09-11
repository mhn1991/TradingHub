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
    /// <summary>
    /// Risk budget as percentage points of equity.
    /// Example: 0.5 means half of one percent (0.5% of equity), not 50%.
    /// </summary>
    public decimal RiskPercentOfEquity { get; init; } = 0.5m;
    public decimal MinimumQuantity { get; init; } = 1m;
    public decimal? MaximumQuantity { get; init; }
    public decimal QuantityStep { get; init; } = 1m;
    /// <summary>Percentage points of equity (30 = 30%).</summary>
    public decimal MaximumAccountMarginUsagePercent { get; init; } = 30m;
    /// <summary>Percentage points of equity (10 = 10%).</summary>
    public decimal MaximumSinglePositionMarginPercent { get; init; } = 10m;
    public decimal Leverage { get; init; } = 20m;
    /// <summary>Spread, entry/exit slippage and commissions expressed as total basis points.</summary>
    public decimal EstimatedRoundTripCostBasisPoints { get; init; }

    /// <summary>
    /// Maximum combined residual open risk (existing positions + this trade) as percentage
    /// points of equity. Example: 1.5 means 1.5% of equity. Null disables heat capping.
    /// </summary>
    public decimal? MaximumOpenRiskPercentOfEquity { get; init; }

    /// <summary>
    /// When open positions lack an explicit residual-risk total, assume each position's stop
    /// distance is this many percentage points of its average price (0.5 = half a percent).
    /// Null disables auto-estimation of open-book risk.
    /// </summary>
    public decimal? AssumedOpenPositionRiskDistancePercentOfPrice { get; init; }

    /// <summary>
    /// When true, scale the sized quantity by a linear multiplier of
    /// <see cref="Agent.Models.AgentDecision.Confidence"/> between
    /// <see cref="ConfidenceMultiplierFloorConfidence"/> and
    /// <see cref="ConfidenceMultiplierCeilingConfidence"/>. Confidence is a rule score, not a
    /// calibrated probability, so the multiplier is deliberately bounded to never exceed
    /// <see cref="MaximumConfidenceMultiplier"/> (1.0) - it can only shrink size on a weak
    /// score, never leverage up on a strong one.
    /// </summary>
    public bool EnableConfidenceScaledSizing { get; init; }
    public decimal ConfidenceMultiplierFloorConfidence { get; init; } = 40m;
    public decimal ConfidenceMultiplierCeilingConfidence { get; init; } = 85m;
    public decimal MinimumConfidenceMultiplier { get; init; } = 0.5m;
    public decimal MaximumConfidenceMultiplier { get; init; } = 1.0m;

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
            EstimatedRoundTripCostBasisPoints < 0m ||
            MaximumOpenRiskPercentOfEquity is <= 0m or > 100m ||
            AssumedOpenPositionRiskDistancePercentOfPrice is <= 0m or > 100m ||
            ConfidenceMultiplierFloorConfidence is < 0m or > 100m ||
            ConfidenceMultiplierCeilingConfidence is < 0m or > 100m ||
            ConfidenceMultiplierCeilingConfidence <= ConfidenceMultiplierFloorConfidence ||
            MinimumConfidenceMultiplier is <= 0m or > 1m ||
            MaximumConfidenceMultiplier is <= 0m or > 1m ||
            MaximumConfidenceMultiplier < MinimumConfidenceMultiplier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PositionSizingOptions),
                "Position-sizing values must be positive and percentage values must be within 0-100.");
        }
    }

    /// <summary>
    /// Folds a broker's per-instrument quantity granularity into these options, taking the stricter
    /// bound on each: the larger minimum, the smaller maximum, and the coarser step. Returns this
    /// instance unchanged when the broker supplies no metadata.
    /// <para>
    /// This is the single definition of the rule. Sizing that ignores it can emit a quantity the
    /// broker will refuse at submission, and because a rejected order is dropped rather than
    /// resized, the trade is simply lost.
    /// </para>
    /// </summary>
    public PositionSizingOptions WithBrokerConstraints(InstrumentTradingMetadata? metadata)
    {
        if (metadata is null)
            return this;
        metadata.Validate();

        decimal? maximum = MaximumQuantity;
        if (metadata.MaximumOrderQuantity is decimal brokerMaximum)
        {
            maximum = maximum is decimal configuredMaximum
                ? Math.Min(configuredMaximum, brokerMaximum)
                : brokerMaximum;
        }

        decimal minimum = Math.Max(MinimumQuantity, metadata.MinimumQuantity);
        if (maximum is decimal effectiveMaximum && effectiveMaximum < minimum)
        {
            throw new InvalidOperationException(
                $"Broker maximum quantity {effectiveMaximum} is below the effective minimum {minimum} " +
                $"for {metadata.Instrument}.");
        }

        return this with
        {
            MinimumQuantity = minimum,
            MaximumQuantity = maximum,
            QuantityStep = Math.Max(QuantityStep, metadata.QuantityStep)
        };
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
    /// <summary>Contract / pip model for the decision instrument. Defaults to unit notional.</summary>
    public InstrumentRiskSpec InstrumentSpec { get; init; } = InstrumentRiskSpec.UnitNotional;
    /// <summary>
    /// Explicit residual open-book risk in account currency (excluding the new trade).
    /// When null, may be auto-estimated from open positions.
    /// </summary>
    public decimal? KnownOpenRiskAccountCurrency { get; init; }
    public IReadOnlyDictionary<InstrumentKey, InstrumentRiskSpec>? InstrumentSpecs { get; init; }
    public IReadOnlyDictionary<InstrumentKey, decimal>? QuoteToAccountRatesByInstrument { get; init; }
    /// <summary>
    /// Broker-supplied quantity granularity for the decision instrument. When present it is folded
    /// into the configured options via <see cref="PositionSizingOptions.WithBrokerConstraints"/>,
    /// taking the stricter bound on each side.
    /// </summary>
    public InstrumentTradingMetadata? BrokerQuantitySpec { get; init; }
    /// <summary>Final composed risk-budget multiplier, always within 0..1.</summary>
    public decimal RiskBudgetMultiplier { get; init; } = 1m;
    public RiskBudgetDecision? RiskBudgetDecision { get; init; }
}

public sealed record PositionSizingResult
{
    public required bool Approved { get; init; }
    public required decimal Quantity { get; init; }
    public decimal? RiskBudget { get; init; }
    public decimal? EstimatedLossAtStop { get; init; }
    public decimal? EstimatedMargin { get; init; }
    public decimal? OpenRiskAccountCurrency { get; init; }
    public decimal? ProjectedOpenRiskAccountCurrency { get; init; }
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
        InstrumentRiskSpec spec = context.InstrumentSpec ?? InstrumentRiskSpec.UnitNotional;
        spec.Validate();
        if (context.RiskBudgetMultiplier is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(context.RiskBudgetMultiplier));

        AgentDecision decision = context.Decision;
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
        {
            return Reject("SizingNotApplicable", "Position sizing is only applicable to opening buy/sell decisions.");
        }

        // Broker granularity overrides the configured defaults where it is stricter. A contradiction
        // between the two is rejected rather than thrown, so this stays fail-closed like the rest.
        PositionSizingOptions effective;
        try
        {
            effective = _options.WithBrokerConstraints(context.BrokerQuantitySpec);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Reject("BrokerQuantityConstraintConflict", exception.Message);
        }

        if (decision.QuantityUnit is not (QuantityUnit.Units or QuantityUnit.BaseAsset or QuantityUnit.Contracts))
        {
            return Reject(
                "UnsupportedQuantityUnit",
                $"Risk-based sizing cannot calculate {decision.QuantityUnit} quantities.");
        }

        decimal reference = decision.ReferencePrice ?? decision.LimitPrice ?? decision.StopPrice ?? 0m;

        // FixedQuantity remains backwards compatible when legacy callers do not supply
        // monetary-risk inputs. When the entry, stop, conversion and account are available,
        // calculate stop risk and margin as well so the live portfolio allocator can enforce
        // the same account-level caps as risk-based sizing. Missing inputs therefore remain
        // usable by legacy code, but fail closed later at live portfolio admission.
        if (_options.Mode == PositionSizingMode.FixedQuantity)
        {
            decimal fixedQuantity = context.RequestedQuantity > 0m
                ? context.RequestedQuantity
                : _options.FixedQuantity;
            fixedQuantity *= context.RiskBudgetMultiplier * ConfidenceSizingMultiplier(decision.Confidence);
            if (effective.MaximumQuantity is decimal fixedMaximum)
                fixedQuantity = Math.Min(fixedQuantity, fixedMaximum);
            fixedQuantity = RoundDown(fixedQuantity, effective.QuantityStep);
            if (fixedQuantity < effective.MinimumQuantity)
            {
                return Reject(
                    "QuantityBelowMinimum",
                    $"Fixed quantity {fixedQuantity:F8} is below the minimum {effective.MinimumQuantity:F8}.");
            }

            AccountSnapshot? fixedAccount = context.Accounts.FirstOrDefault(item => item.Balance is > 0m);
            // Locals in this branch use a fixed* prefix so they do not collide with method-scope
            // names declared later for risk-based sizing (CS0136 under C# block scoping).
            if (reference > 0m &&
                decision.StopLossPrice is decimal fixedStopPrice &&
                Math.Abs(reference - fixedStopPrice) > 0m &&
                context.QuoteToAccountCurrencyRate > 0m &&
                fixedAccount?.Balance is decimal fixedBalance)
            {
                decimal fixedEquity = fixedBalance + (fixedAccount.UnrealizedProfitLoss ?? 0m);
                if (fixedEquity <= 0m)
                    return Reject("NonPositiveEquity", "Account equity must be positive before fixed quantity can be admitted live.");

                decimal fixedCostDistance =
                    reference * _options.EstimatedRoundTripCostBasisPoints / 10_000m;
                decimal fixedEstimatedLoss = spec.EstimateStopLossAccountCurrency(
                    Math.Abs(reference - fixedStopPrice) + fixedCostDistance,
                    fixedQuantity,
                    context.QuoteToAccountCurrencyRate);
                decimal fixedEstimatedMargin = spec.EstimateMarginAccountCurrency(
                    reference,
                    fixedQuantity,
                    _options.Leverage,
                    context.QuoteToAccountCurrencyRate);
                decimal fixedMaximumAccountMargin =
                    fixedEquity * _options.MaximumAccountMarginUsagePercent / 100m;
                decimal fixedMaximumSinglePositionMargin =
                    fixedEquity * _options.MaximumSinglePositionMarginPercent / 100m;
                decimal fixedCurrentMargin = fixedAccount.MarginUsed ?? 0m;
                if (fixedEstimatedMargin > fixedMaximumSinglePositionMargin ||
                    fixedCurrentMargin + fixedEstimatedMargin > fixedMaximumAccountMargin)
                {
                    return Reject(
                        "FixedQuantityMarginLimit",
                        $"Fixed quantity requires margin {fixedEstimatedMargin:F2}, above the configured account or single-position cap.");
                }

                decimal fixedOpenRisk = PortfolioOpenRisk.EstimateAccountCurrency(
                    context.Positions,
                    spec,
                    context.QuoteToAccountCurrencyRate,
                    context.KnownOpenRiskAccountCurrency,
                    _options.AssumedOpenPositionRiskDistancePercentOfPrice,
                    context.InstrumentSpecs,
                    context.QuoteToAccountRatesByInstrument);
                decimal fixedProjectedOpenRisk = fixedOpenRisk + fixedEstimatedLoss;
                if (_options.MaximumOpenRiskPercentOfEquity is decimal fixedMaxOpenRiskPercent &&
                    fixedProjectedOpenRisk > fixedEquity * fixedMaxOpenRiskPercent / 100m)
                {
                    return Reject(
                        "FixedQuantityOpenRiskLimit",
                        $"Fixed quantity projects open risk {fixedProjectedOpenRisk:F2}, above the configured heat cap.");
                }

                return new PositionSizingResult
                {
                    Approved = true,
                    Quantity = fixedQuantity,
                    RiskBudget = fixedEstimatedLoss,
                    EstimatedLossAtStop = fixedEstimatedLoss,
                    EstimatedMargin = fixedEstimatedMargin,
                    OpenRiskAccountCurrency = fixedOpenRisk,
                    ProjectedOpenRiskAccountCurrency = fixedProjectedOpenRisk,
                    ReasonCode = PositionSizingMode.FixedQuantity.ToString(),
                    Reason = $"Using fixed quantity {fixedQuantity:F8}; estimated stop loss {fixedEstimatedLoss:F2}, " +
                        $"margin {fixedEstimatedMargin:F2}, projected open risk {fixedProjectedOpenRisk:F2}."
                };
            }

            return new PositionSizingResult
            {
                Approved = true,
                Quantity = fixedQuantity,
                ReasonCode = PositionSizingMode.FixedQuantity.ToString(),
                Reason = $"Using configured fixed quantity {fixedQuantity:F8}; monetary risk was not estimated because live-risk inputs were incomplete."
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
        decimal riskBudget;
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
            riskBudget *= context.RiskBudgetMultiplier * ConfidenceSizingMultiplier(decision.Confidence);

            decimal estimatedCostDistance =
                reference * _options.EstimatedRoundTripCostBasisPoints / 10_000m;
            perUnitLoss = spec.EstimateStopLossAccountCurrency(
                riskDistance + estimatedCostDistance,
                quantity: 1m,
                context.QuoteToAccountCurrencyRate);
            if (perUnitLoss <= 0m)
            {
                return Reject("InvalidPerUnitRisk", "Calculated per-unit account risk is not positive.");
            }

            // Every other failure in this method returns a rejection. A per-unit risk small enough
            // to push the quotient past decimal's range would instead throw out of the sizer, so
            // guard it and fail closed like the rest.
            try
            {
                quantity = riskBudget / perUnitLoss.Value;
            }
            catch (OverflowException)
            {
                return Reject(
                    "UnboundedQuantity",
                    "The risk budget divided by per-unit risk exceeds the representable quantity range.");
            }
        }

        if (reference <= 0m)
        {
            return Reject("MissingReferencePrice", "A positive reference price is required to enforce margin limits.");
        }

        decimal marginPerUnit = spec.EstimateMarginAccountCurrency(
            reference,
            quantity: 1m,
            _options.Leverage,
            context.QuoteToAccountCurrencyRate);
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

        decimal openRisk = PortfolioOpenRisk.EstimateAccountCurrency(
            context.Positions,
            spec,
            context.QuoteToAccountCurrencyRate,
            context.KnownOpenRiskAccountCurrency,
            _options.AssumedOpenPositionRiskDistancePercentOfPrice,
            context.InstrumentSpecs,
            context.QuoteToAccountRatesByInstrument);

        if (_options.MaximumOpenRiskPercentOfEquity is decimal maxOpenRiskPercent &&
            perUnitLoss is decimal heatPerUnit)
        {
            decimal maxOpenRisk = equity * maxOpenRiskPercent / 100m;
            decimal remainingHeatBudget = Math.Max(0m, maxOpenRisk - openRisk);
            quantity = Math.Min(quantity, remainingHeatBudget / heatPerUnit);
        }

        if (effective.MaximumQuantity is decimal maximumQuantity)
        {
            quantity = Math.Min(quantity, maximumQuantity);
        }

        quantity = RoundDown(quantity, effective.QuantityStep);
        if (quantity < effective.MinimumQuantity)
        {
            return Reject(
                "QuantityBelowMinimum",
                $"Risk, margin, and open-risk heat limits allow {quantity:F8}, below the minimum " +
                $"{effective.MinimumQuantity:F8}.");
        }

        decimal estimatedLoss = perUnitLoss is decimal lossPerUnit
            ? lossPerUnit * quantity
            : decision.StopLossPrice is decimal fixedStop
                ? spec.EstimateStopLossAccountCurrency(
                    Math.Abs(reference - fixedStop),
                    quantity,
                    context.QuoteToAccountCurrencyRate)
                : 0m;
        decimal estimatedMargin = marginPerUnit * quantity;
        decimal projectedOpenRisk = openRisk + estimatedLoss;

        return new PositionSizingResult
        {
            Approved = true,
            Quantity = quantity,
            RiskBudget = riskBudget,
            EstimatedLossAtStop = estimatedLoss,
            EstimatedMargin = estimatedMargin,
            OpenRiskAccountCurrency = openRisk,
            ProjectedOpenRiskAccountCurrency = projectedOpenRisk,
            ReasonCode = _options.Mode.ToString(),
            Reason = $"Sized {quantity:F8} units from a {riskBudget:F2} risk budget; " +
                $"estimated stop loss {estimatedLoss:F2}, margin {estimatedMargin:F2}, " +
                $"projected open risk {projectedOpenRisk:F2}."
        };
    }

    private decimal ConfidenceSizingMultiplier(decimal confidence)
    {
        if (!_options.EnableConfidenceScaledSizing)
            return 1m;

        decimal floor = _options.ConfidenceMultiplierFloorConfidence;
        decimal ceiling = _options.ConfidenceMultiplierCeilingConfidence;
        if (confidence <= floor)
            return _options.MinimumConfidenceMultiplier;
        if (confidence >= ceiling)
            return _options.MaximumConfidenceMultiplier;

        decimal fraction = (confidence - floor) / (ceiling - floor);
        return _options.MinimumConfidenceMultiplier +
            fraction * (_options.MaximumConfidenceMultiplier - _options.MinimumConfidenceMultiplier);
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (value <= 0m)
            return 0m;
        try
        {
            return decimal.Floor(value / step) * step;
        }
        catch (OverflowException)
        {
            // Fail closed: an un-representable quantity is rejected by the MinimumQuantity check
            // rather than crashing the sizer.
            return 0m;
        }
    }

    private static PositionSizingResult Reject(string code, string reason) => new()
    {
        Approved = false,
        Quantity = 0m,
        ReasonCode = code,
        Reason = reason
    };
}
