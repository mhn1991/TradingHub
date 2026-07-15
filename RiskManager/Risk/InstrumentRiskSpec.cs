using Brokers.Models;

namespace RiskManager;

/// <summary>
/// Instrument-specific conversion of price distance and quantity into quote/account currency risk.
/// Default (unit notional) model:
/// <c>loss = riskDistance × quantity × ContractMultiplier × quoteToAccountRate</c>.
/// </summary>
public sealed record InstrumentRiskSpec
{
    /// <summary>Unit/base notional instruments (spot FX units, crypto base asset, stock shares).</summary>
    public static InstrumentRiskSpec UnitNotional { get; } = new();

    /// <summary>
    /// Scales price-distance × quantity into quote-currency PnL.
    /// Use 1 for unit/base FX and spot crypto. Futures/CFD contracts use the exchange multiplier.
    /// </summary>
    public decimal ContractMultiplier { get; init; } = 1m;

    /// <summary>
    /// Optional pip/point size for documentation and pip helpers.
    /// When null, loss uses raw price distance rather than pip counts.
    /// </summary>
    public decimal? PipSize { get; init; }

    /// <summary>
    /// Optional initial margin as a fraction of account-currency notional (0.05 = 5%).
    /// When null, <see cref="PositionSizer"/> uses <c>1 / Leverage</c>.
    /// </summary>
    public decimal? MarginRate { get; init; }

    public void Validate()
    {
        if (ContractMultiplier <= 0m ||
            PipSize is <= 0m ||
            MarginRate is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InstrumentRiskSpec),
                "Contract multiplier and pip size must be positive; margin rate must be in (0, 1].");
        }
    }

    /// <summary>
    /// Resolves a default spec from the instrument key. Callers can override with explicit specs.
    /// Unknown instruments fall back to <see cref="UnitNotional"/>.
    /// </summary>
    public static InstrumentRiskSpec ForInstrument(InstrumentKey instrument)
    {
        // Heuristic registry kept small on purpose. Multi-asset live venues should inject
        // explicit specs rather than relying on string prefixes alone.
        string value = instrument.Value;
        if (value.StartsWith("FX:", StringComparison.Ordinal) ||
            value.StartsWith("CRYPTO:", StringComparison.Ordinal) ||
            value.StartsWith("BINANCE:", StringComparison.Ordinal) ||
            value.StartsWith("OANDA:", StringComparison.Ordinal))
        {
            return UnitNotional;
        }

        return UnitNotional;
    }

    /// <summary>Quote-currency PnL for a given price distance and quantity.</summary>
    public decimal PriceDistanceToQuotePnl(decimal priceDistance, decimal quantity) =>
        Math.Abs(priceDistance) * quantity * ContractMultiplier;

    /// <summary>Account-currency stop loss for a given price distance and quantity.</summary>
    public decimal EstimateStopLossAccountCurrency(
        decimal riskDistance,
        decimal quantity,
        decimal quoteToAccountCurrencyRate)
    {
        if (quoteToAccountCurrencyRate <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quoteToAccountCurrencyRate),
                "Quote-to-account rate must be positive.");
        }

        return PriceDistanceToQuotePnl(riskDistance, quantity) * quoteToAccountCurrencyRate;
    }

    /// <summary>Account-currency initial margin estimate for a position size.</summary>
    public decimal EstimateMarginAccountCurrency(
        decimal referencePrice,
        decimal quantity,
        decimal leverage,
        decimal quoteToAccountCurrencyRate)
    {
        if (referencePrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(referencePrice));
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (leverage <= 0m)
            throw new ArgumentOutOfRangeException(nameof(leverage));
        if (quoteToAccountCurrencyRate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quoteToAccountCurrencyRate));

        decimal notionalAccount =
            referencePrice * quantity * ContractMultiplier * quoteToAccountCurrencyRate;
        if (MarginRate is decimal marginRate)
            return notionalAccount * marginRate;

        return notionalAccount / leverage;
    }
}

/// <summary>
/// Estimates residual open-book risk when broker positions do not carry protective stops.
/// Prefer an explicit account-currency total when the pipeline knows residual stop risk.
/// </summary>
public static class PortfolioOpenRisk
{
    /// <summary>
    /// Residual open risk in account currency, excluding any proposed new trade.
    /// </summary>
    /// <param name="positions">Current open broker positions.</param>
    /// <param name="defaultSpec">Spec used when a per-instrument map has no entry.</param>
    /// <param name="quoteToAccountCurrencyRate">
    /// Fallback conversion when a per-instrument rate is not supplied.
    /// </param>
    /// <param name="knownOpenRiskAccountCurrency">
    /// Explicit residual open risk. When set, it is used as-is (no auto-estimate).
    /// </param>
    /// <param name="assumedRiskDistancePercentOfPrice">
    /// Percentage points of each position's average price used as assumed stop distance
    /// when no known total is supplied. Example: 0.5 means half of one percent of entry.
    /// </param>
    /// <param name="specsByInstrument">Optional per-instrument multipliers.</param>
    /// <param name="quoteToAccountRatesByInstrument">Optional per-instrument FX rates.</param>
    public static decimal EstimateAccountCurrency(
        IReadOnlyList<BrokerPosition> positions,
        InstrumentRiskSpec defaultSpec,
        decimal quoteToAccountCurrencyRate,
        decimal? knownOpenRiskAccountCurrency = null,
        decimal? assumedRiskDistancePercentOfPrice = null,
        IReadOnlyDictionary<InstrumentKey, InstrumentRiskSpec>? specsByInstrument = null,
        IReadOnlyDictionary<InstrumentKey, decimal>? quoteToAccountRatesByInstrument = null)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(defaultSpec);
        defaultSpec.Validate();

        if (knownOpenRiskAccountCurrency is decimal known)
            return Math.Max(0m, known);

        if (assumedRiskDistancePercentOfPrice is not decimal percent || percent <= 0m)
            return 0m;

        decimal total = 0m;
        foreach (BrokerPosition position in positions)
        {
            if (position.Quantity <= 0m || position.AveragePrice is not decimal average || average <= 0m)
                continue;

            InstrumentRiskSpec spec = ResolveSpec(position.Instrument, defaultSpec, specsByInstrument);
            decimal rate = ResolveRate(
                position.Instrument,
                quoteToAccountCurrencyRate,
                quoteToAccountRatesByInstrument);
            if (rate <= 0m)
                continue;

            decimal riskDistance = average * percent / 100m;
            total += spec.EstimateStopLossAccountCurrency(riskDistance, position.Quantity, rate);
        }

        return total;
    }

    private static InstrumentRiskSpec ResolveSpec(
        InstrumentKey instrument,
        InstrumentRiskSpec defaultSpec,
        IReadOnlyDictionary<InstrumentKey, InstrumentRiskSpec>? specsByInstrument)
    {
        if (specsByInstrument is not null &&
            specsByInstrument.TryGetValue(instrument, out InstrumentRiskSpec? mapped) &&
            mapped is not null)
        {
            mapped.Validate();
            return mapped;
        }

        return defaultSpec;
    }

    private static decimal ResolveRate(
        InstrumentKey instrument,
        decimal fallbackRate,
        IReadOnlyDictionary<InstrumentKey, decimal>? ratesByInstrument)
    {
        if (ratesByInstrument is not null &&
            ratesByInstrument.TryGetValue(instrument, out decimal rate) &&
            rate > 0m)
        {
            return rate;
        }

        return fallbackRate;
    }
}
