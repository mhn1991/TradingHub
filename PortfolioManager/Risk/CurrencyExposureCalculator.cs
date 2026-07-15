using Brokers.Models;

namespace PortfolioManager.Risk;

public sealed record FxConversionSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required string AccountCurrency { get; init; }
    /// <summary>Value of one unit of the keyed currency in account currency.</summary>
    public required IReadOnlyDictionary<string, decimal> CurrencyToAccountRates { get; init; }
}

public sealed record CurrencyExposureResult
{
    public required bool Complete { get; init; }
    public required IReadOnlyDictionary<string, decimal> GrossExposureAccountCurrency { get; init; }
    public required IReadOnlyDictionary<string, decimal> NetExposureAccountCurrency { get; init; }
    public required IReadOnlyDictionary<string, decimal> RiskToStopAccountCurrency { get; init; }
    public required IReadOnlyList<string> MissingCurrencies { get; init; }
}

public interface ICurrencyExposureCalculator
{
    CurrencyExposureResult Calculate(
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations,
        FxConversionSnapshot conversion);
}

public sealed class CurrencyExposureCalculator : ICurrencyExposureCalculator
{
    public CurrencyExposureResult Calculate(
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations,
        FxConversionSnapshot conversion)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(conversion);
        var signedNative = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var reservedAccount = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var stopRisk = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (PortfolioPositionLot lot in positions.OrderBy(item => item.LotId, StringComparer.Ordinal))
        {
            (string Base, string Quote) = ParseCurrencies(lot.Instrument);
            decimal sign = lot.Side == OrderSide.Buy ? 1m : -1m;
            Add(signedNative, Base, sign * lot.Quantity * lot.ContractMultiplier);
            Add(signedNative, Quote, -sign * lot.Quantity * lot.CurrentPrice * lot.ContractMultiplier);
            if (lot.ProtectiveStopPrice is decimal stop)
            {
                decimal lossDistance = lot.Side == OrderSide.Buy
                    ? Math.Max(0m, lot.CurrentPrice - stop)
                    : Math.Max(0m, stop - lot.CurrentPrice);
                decimal risk = lossDistance * lot.Quantity * lot.ContractMultiplier *
                    lot.QuoteToAccountCurrencyRate + lot.EstimatedExitCostAccountCurrency;
                Add(stopRisk, Base, risk / 2m);
                Add(stopRisk, Quote, risk / 2m);
            }
        }

        foreach (PortfolioReservation reservation in reservations.OrderBy(item => item.CreatedSequence))
        {
            foreach ((string currency, decimal delta) in reservation.CurrencyExposureDelta
                         .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                // Reservation deltas are already account-currency exposure values.
                Add(reservedAccount, currency, delta);
            }
            (string baseCurrency, string quoteCurrency) = ParseCurrencies(reservation.Instrument);
            Add(stopRisk, baseCurrency, reservation.RemainingRiskAccountCurrency / 2m);
            Add(stopRisk, quoteCurrency, reservation.RemainingRiskAccountCurrency / 2m);
        }

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var net = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var gross = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach ((string currency, decimal native) in signedNative.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            decimal rate;
            if (string.Equals(currency, conversion.AccountCurrency, StringComparison.OrdinalIgnoreCase))
                rate = 1m;
            else if (!conversion.CurrencyToAccountRates.TryGetValue(currency, out rate) || rate <= 0m)
            {
                missing.Add(currency);
                continue;
            }
            net[currency] = native * rate;
            gross[currency] = Math.Abs(native * rate);
        }
        foreach ((string currency, decimal amount) in reservedAccount.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            net[currency] = net.GetValueOrDefault(currency) + amount;
            gross[currency] = gross.GetValueOrDefault(currency) + Math.Abs(amount);
        }

        return new CurrencyExposureResult
        {
            Complete = missing.Count == 0,
            GrossExposureAccountCurrency = gross,
            NetExposureAccountCurrency = net,
            RiskToStopAccountCurrency = stopRisk,
            MissingCurrencies = missing.ToArray()
        };
    }

    public static (string Base, string Quote) ParseCurrencies(InstrumentKey instrument)
    {
        string value = instrument.Value;
        string symbol = value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value;
        string[] parts = symbol.Split(['/', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Instrument '{instrument}' does not expose base/quote currencies.", nameof(instrument));
        return (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
    }

    private static void Add(Dictionary<string, decimal> values, string key, decimal amount) =>
        values[key] = values.GetValueOrDefault(key) + amount;
}
