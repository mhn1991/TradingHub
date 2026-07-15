using Brokers.Models;

namespace PortfolioManager.CurrencyStrength;

public sealed record CurrencyPairReturn
{
    public required InstrumentKey Instrument { get; init; }
    public required decimal LogReturn { get; init; }
    public required decimal RealizedVolatility { get; init; }
    public decimal DataQualityWeight { get; init; } = 1m;
    public decimal LiquidityWeight { get; init; } = 1m;
}

public sealed record CurrencyStrengthSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyDictionary<string, decimal> Scores { get; init; }
    public required IReadOnlyDictionary<string, decimal> Coverage { get; init; }
    public required string MethodVersion { get; init; }
}

public sealed class CurrencyStrengthCalculator
{
    public const string Version = "normalized-return-v1";

    public CurrencyStrengthSnapshot Calculate(
        DateTimeOffset availableAt,
        IReadOnlyList<CurrencyPairReturn> returns,
        InstrumentKey? leaveOutInstrument = null)
    {
        ArgumentNullException.ThrowIfNull(returns);
        var sums = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var weights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (CurrencyPairReturn observation in returns
                     .Where(item => leaveOutInstrument is null || item.Instrument != leaveOutInstrument.Value)
                     .OrderBy(item => item.Instrument.Value, StringComparer.Ordinal))
        {
            if (observation.RealizedVolatility <= 0m || observation.DataQualityWeight <= 0m ||
                observation.LiquidityWeight <= 0m)
                continue;
            (string baseCurrency, string quoteCurrency) = Risk.CurrencyExposureCalculator.ParseCurrencies(observation.Instrument);
            decimal weight = observation.DataQualityWeight * observation.LiquidityWeight /
                observation.RealizedVolatility;
            decimal normalized = observation.LogReturn / observation.RealizedVolatility;
            Add(sums, baseCurrency, normalized * weight);
            Add(sums, quoteCurrency, -normalized * weight);
            Add(weights, baseCurrency, weight);
            Add(weights, quoteCurrency, weight);
        }

        string[] currencies = weights.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        decimal maximumWeight = weights.Count == 0 ? 0m : weights.Values.Max();
        return new CurrencyStrengthSnapshot
        {
            AvailableAt = availableAt,
            Scores = currencies.ToDictionary(currency => currency, currency => sums[currency] / weights[currency], StringComparer.OrdinalIgnoreCase),
            Coverage = currencies.ToDictionary(currency => currency, currency => maximumWeight <= 0m ? 0m : weights[currency] / maximumWeight * 100m, StringComparer.OrdinalIgnoreCase),
            MethodVersion = Version
        };
    }

    private static void Add(Dictionary<string, decimal> values, string key, decimal amount) =>
        values[key] = values.GetValueOrDefault(key) + amount;
}
