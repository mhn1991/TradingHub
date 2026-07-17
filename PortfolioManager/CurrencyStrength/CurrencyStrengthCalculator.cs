using Brokers.Models;
using ChartAnnotator.Models;

namespace PortfolioManager.CurrencyStrength;

public sealed class CurrencyStrengthCalculator
{
    public const string Version = "normalized-return-v1";

    public CurrencyStrengthSnapshot Calculate(
        DateTimeOffset availableAt,
        IReadOnlyList<CurrencyPairReturn> returns,
        InstrumentKey? leaveOutInstrument = null)
    {
        ArgumentNullException.ThrowIfNull(returns);
        // Volatility-normalized weight/return ratios use double, not decimal: a very
        // quiet/illiquid pair can have realized volatility many orders of magnitude
        // smaller than its return, and decimal's ~28-digit range overflows on that
        // division/multiplication chain where double's much wider exponent range does not.
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (CurrencyPairReturn observation in returns
                     .Where(item => leaveOutInstrument is null || item.Instrument != leaveOutInstrument.Value)
                     .OrderBy(item => item.Instrument.Value, StringComparer.Ordinal))
        {
            if (observation.RealizedVolatility <= 0m || observation.DataQualityWeight <= 0m ||
                observation.LiquidityWeight <= 0m)
                continue;
            (string baseCurrency, string quoteCurrency) = Risk.CurrencyExposureCalculator.ParseCurrencies(observation.Instrument);
            double volatility = (double)observation.RealizedVolatility;
            double weight = (double)observation.DataQualityWeight * (double)observation.LiquidityWeight / volatility;
            double normalized = (double)observation.LogReturn / volatility;
            if (double.IsNaN(weight) || double.IsInfinity(weight) ||
                double.IsNaN(normalized) || double.IsInfinity(normalized))
                continue;
            Add(sums, baseCurrency, normalized * weight);
            Add(sums, quoteCurrency, -normalized * weight);
            Add(weights, baseCurrency, weight);
            Add(weights, quoteCurrency, weight);
        }

        string[] currencies = weights.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        double maximumWeight = weights.Count == 0 ? 0d : weights.Values.Max();
        return new CurrencyStrengthSnapshot
        {
            AvailableAt = availableAt,
            Scores = currencies.ToDictionary(
                currency => currency,
                currency => ToDecimalScore(sums[currency] / weights[currency]),
                StringComparer.OrdinalIgnoreCase),
            Coverage = currencies.ToDictionary(
                currency => currency,
                currency => maximumWeight <= 0d ? 0m : (decimal)(weights[currency] / maximumWeight * 100d),
                StringComparer.OrdinalIgnoreCase),
            MethodVersion = Version
        };
    }

    private static void Add(Dictionary<string, double> values, string key, double amount) =>
        values[key] = values.GetValueOrDefault(key) + amount;

    /// <summary>Clamps into decimal's representable range instead of throwing on a
    /// pathological ratio - an extreme score is still meaningful ("very strong/weak"),
    /// an unhandled overflow crash is not.</summary>
    private static decimal ToDecimalScore(double value)
    {
        if (double.IsNaN(value)) return 0m;
        if (value >= (double)decimal.MaxValue) return decimal.MaxValue;
        if (value <= (double)decimal.MinValue) return decimal.MinValue;
        return (decimal)value;
    }
}
