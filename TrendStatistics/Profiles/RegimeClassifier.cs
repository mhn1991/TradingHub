using TrendStatistics.Detection;
using TrendStatistics.Segmentation;
using TrendStatistics.Statistics;

namespace TrendStatistics.Profiles;

/// <summary>Blueprint section 60's volatility regimes.</summary>
public enum VolatilityRegime { Unknown, Low, Normal, High }

/// <summary>
/// Section 60's regime conditioning: split the trend population by the volatility environment the
/// trend started in.
/// <para>
/// Section 60 opens with a warning this class cannot enforce but callers must respect - conditioning
/// "fragments the sample population". With 375 XAU bull trends a three-way split leaves ~125 per
/// regime, and section 2.16's bootstrap showed that 76 trends put the P95 confidence interval at a
/// relative width of 1.42. So a conditioned profile must be bootstrap-checked before it is used, not
/// assumed usable because the unconditional one was.
/// </para>
/// <para>
/// Regime is assigned from ATR as a percentage of confirmation price against terciles fitted only
/// from preceding trends. The feature is known at confirmation; final trend size is not.
/// </para>
/// </summary>
public sealed class RegimeClassifier
{
    private readonly decimal _lowerTercile;
    private readonly decimal _upperTercile;

    private RegimeClassifier(decimal lower, decimal upper)
    {
        _lowerTercile = lower;
        _upperTercile = upper;
    }

    public decimal LowerBoundary => _lowerTercile;
    public decimal UpperBoundary => _upperTercile;

    /// <summary>
    /// Fits boundaries from already-completed trends. ATR/price is scale-free and was known at each
    /// trend's confirmation, so it neither encodes the symbol's price level nor leaks its outcome.
    /// </summary>
    public static RegimeClassifier Fit(IReadOnlyList<TrendRecord> trends)
    {
        ArgumentNullException.ThrowIfNull(trends);
        if (trends.Count == 0)
            return new RegimeClassifier(0m, 0m);

        decimal[] sorted = QuantileCalculator.Sorted(trends
            .Select(trend => trend.VolatilityPctAtConfirmation)
            .Where(volatility => volatility > 0m));
        if (sorted.Length == 0)
            return new RegimeClassifier(0m, 0m);
        return new RegimeClassifier(
            QuantileCalculator.Quantile(sorted, 1m / 3m),
            QuantileCalculator.Quantile(sorted, 2m / 3m));
    }

    public VolatilityRegime Classify(decimal volatilityPctAtConfirmation)
    {
        if (volatilityPctAtConfirmation <= 0m
            || (_lowerTercile == 0m && _upperTercile == 0m))
            return VolatilityRegime.Unknown;
        if (volatilityPctAtConfirmation <= _lowerTercile) return VolatilityRegime.Low;
        return volatilityPctAtConfirmation >= _upperTercile
            ? VolatilityRegime.High
            : VolatilityRegime.Normal;
    }

    public VolatilityRegime Classify(TrendRecord trend)
    {
        ArgumentNullException.ThrowIfNull(trend);
        return Classify(trend.VolatilityPctAtConfirmation);
    }
}

/// <summary>Identifies one conditioned population: section 60's (symbol, direction, regime).</summary>
public readonly record struct ProfileKey(string Symbol, TrendDirection Direction, VolatilityRegime Regime)
{
    /// <summary>The unconditional key, i.e. section 21's original (symbol, direction) profile.</summary>
    public static ProfileKey Unconditional(string symbol, TrendDirection direction) =>
        new(symbol, direction, VolatilityRegime.Unknown);

    public override string ToString() => Regime == VolatilityRegime.Unknown
        ? $"{Symbol}/{Direction}"
        : $"{Symbol}/{Direction}/{Regime}";
}

/// <summary>
/// Section 51's <c>ProfileRepository</c>: a keyed store of built profiles, with the regime fallback
/// that section 60's fragmentation warning makes necessary.
/// </summary>
public sealed class ProfileRepository
{
    private readonly Dictionary<ProfileKey, DirectionTrendProfile> _profiles = [];

    public int Count => _profiles.Count;
    public IReadOnlyCollection<ProfileKey> Keys => _profiles.Keys;

    public void Add(ProfileKey key, DirectionTrendProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[key] = profile;
    }

    public DirectionTrendProfile? Get(ProfileKey key) =>
        _profiles.TryGetValue(key, out DirectionTrendProfile? profile) ? profile : null;

    /// <summary>
    /// Returns the regime-conditioned profile when it has enough samples to be trustworthy, and
    /// otherwise falls back to the unconditional one.
    /// <para>
    /// This is the mechanism that makes section 60 safe to switch on. Conditioning divides the
    /// population roughly three ways; where that leaves too few trends the conditioned quantiles
    /// are noise, and silently using them would be strictly worse than not conditioning at all.
    /// </para>
    /// </summary>
    public DirectionTrendProfile? GetWithFallback(ProfileKey key, int minimumSamples)
    {
        DirectionTrendProfile? conditioned = Get(key);
        if (conditioned is not null && conditioned.IsReliable(minimumSamples))
            return conditioned;
        return Get(ProfileKey.Unconditional(key.Symbol, key.Direction));
    }

    /// <summary>
    /// Builds the unconditional profiles and, if <paramref name="classifier"/> is supplied, one per
    /// volatility regime as well.
    /// </summary>
    public static ProfileRepository Build(
        string symbol,
        IReadOnlyList<TrendRecord> trends,
        RegimeClassifier? classifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(trends);

        ProfileRepository repository = new();
        foreach (TrendDirection direction in (TrendDirection[])[TrendDirection.Bullish, TrendDirection.Bearish])
        {
            repository.Add(
                ProfileKey.Unconditional(symbol, direction),
                DirectionTrendProfile.Build(symbol, direction, trends));

            if (classifier is null)
                continue;

            foreach (VolatilityRegime regime in (VolatilityRegime[])
                [VolatilityRegime.Low, VolatilityRegime.Normal, VolatilityRegime.High])
            {
                TrendRecord[] subset = [.. trends.Where(trend => classifier.Classify(trend) == regime)];
                repository.Add(
                    new ProfileKey(symbol, direction, regime),
                    DirectionTrendProfile.Build(symbol, direction, subset));
            }
        }

        return repository;
    }
}
