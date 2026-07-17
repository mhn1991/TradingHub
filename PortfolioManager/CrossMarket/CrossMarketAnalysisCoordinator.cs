using Brokers.Models;
using ChartAnnotator.Models;
using PortfolioManager.CurrencyStrength;
using PortfolioManager.Risk;

namespace PortfolioManager.CrossMarket;

public sealed record CurrencyStrengthOptions
{
    public bool Enabled { get; init; }
    public BarInterval Interval { get; init; } = BarInterval.Hours(1);
    public int ReturnLookbackBars { get; init; } = 12;
    public int VolatilityLookbackBars { get; init; } = 120;
    public decimal MinimumCurrencyCoveragePercent { get; init; } = 60m;
    public IReadOnlyDictionary<string, IReadOnlyList<InstrumentKey>> Baskets { get; init; } =
        new Dictionary<string, IReadOnlyList<InstrumentKey>>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!Enabled) return;
        if (!Interval.IsValid || ReturnLookbackBars < 1 || VolatilityLookbackBars < 2 ||
            MinimumCurrencyCoveragePercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(CurrencyStrengthOptions));
        if (Baskets.Count == 0 || Baskets.Values.All(items => items.Count == 0))
            throw new ArgumentException("Enabled currency strength requires at least one non-empty basket.", nameof(Baskets));
    }

    /// <summary>Deduplicated set of every instrument across every configured basket.</summary>
    public IReadOnlyCollection<InstrumentKey> AllBasketInstruments() =>
        Baskets.Values.SelectMany(items => items).Distinct().ToArray();
}

/// <summary>
/// Feeds completed basket-instrument closes into <see cref="CurrencyStrengthCalculator"/>
/// on a rolling basis and exposes the latest snapshot - with leave-one-out support so a
/// traded pair never contributes to its own strength differential. Data-fetching is the
/// caller's responsibility (see StreamingComparativeEngine's side-channel basket fetch);
/// this class only ever sees whatever completed closes it is given, in chronological
/// order, so it cannot look ahead.
/// </summary>
public sealed class CrossMarketAnalysisCoordinator
{
    private readonly CurrencyStrengthOptions _options;
    private readonly CurrencyStrengthCalculator _calculator = new();
    private readonly int _capacity;
    private readonly Dictionary<InstrumentKey, Queue<decimal>> _closeHistory = [];
    private DateTimeOffset _lastAvailableAt = DateTimeOffset.MinValue;
    private IReadOnlyList<CurrencyPairReturn> _latestObservations = [];

    public CrossMarketAnalysisCoordinator(CurrencyStrengthOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _capacity = Math.Max(_options.ReturnLookbackBars, _options.VolatilityLookbackBars) + 1;
    }

    public DateTimeOffset? LatestAvailableAt => _lastAvailableAt == DateTimeOffset.MinValue ? null : _lastAvailableAt;

    /// <summary>
    /// Feeds one or more basket instruments' completed close prices for one timestamp.
    /// Must be called with strictly increasing timestamps across calls.
    /// </summary>
    public void Update(DateTimeOffset availableAt, IReadOnlyDictionary<InstrumentKey, decimal> completedCloses)
    {
        ArgumentNullException.ThrowIfNull(completedCloses);
        if (availableAt <= _lastAvailableAt)
            throw new InvalidOperationException("Cross-market observations must be chronological.");
        _lastAvailableAt = availableAt;

        foreach ((InstrumentKey instrument, decimal close) in completedCloses.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            if (close <= 0m) continue;
            if (!_closeHistory.TryGetValue(instrument, out Queue<decimal>? history))
            {
                history = new Queue<decimal>();
                _closeHistory[instrument] = history;
            }
            history.Enqueue(close);
            while (history.Count > _capacity)
                history.Dequeue();
        }

        var observations = new List<CurrencyPairReturn>();
        foreach ((InstrumentKey instrument, Queue<decimal> history) in _closeHistory
                     .OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            decimal[] closes = history.ToArray();
            if (closes.Length <= _options.ReturnLookbackBars) continue;
            decimal oldest = closes[^(1 + _options.ReturnLookbackBars)];
            if (oldest <= 0m) continue;
            decimal logReturn = (decimal)Math.Log((double)(closes[^1] / oldest));

            var oneBarReturns = new List<double>();
            for (int i = 1; i < closes.Length; i++)
            {
                if (closes[i - 1] <= 0m || closes[i] <= 0m) continue;
                oneBarReturns.Add(Math.Log((double)(closes[i] / closes[i - 1])));
            }
            if (oneBarReturns.Count < 2) continue;
            double mean = oneBarReturns.Average();
            double variance = oneBarReturns.Sum(r => (r - mean) * (r - mean)) / (oneBarReturns.Count - 1);
            decimal realizedVolatility = (decimal)Math.Sqrt(variance);
            if (realizedVolatility <= 0m) continue;

            observations.Add(new CurrencyPairReturn
            {
                Instrument = instrument,
                LogReturn = logReturn,
                RealizedVolatility = realizedVolatility
            });
        }
        _latestObservations = observations;
    }

    /// <summary>
    /// The latest strength snapshot, optionally excluding one instrument from its own
    /// leave-one-out computation (use for the pair currently being traded).
    /// </summary>
    public CurrencyStrengthSnapshot? GetSnapshot(InstrumentKey? leaveOut = null)
    {
        if (_lastAvailableAt == DateTimeOffset.MinValue || _latestObservations.Count == 0) return null;
        return _calculator.Calculate(_lastAvailableAt, _latestObservations, leaveOut);
    }

    /// <summary>
    /// Base-minus-quote strength differential for one instrument, leaving that instrument
    /// out of its own computation. Null when data is unavailable or coverage for either
    /// currency falls below <see cref="CurrencyStrengthOptions.MinimumCurrencyCoveragePercent"/> -
    /// callers must treat null as "unavailable", never as zero/neutral confirmation.
    /// </summary>
    public decimal? ComputeDifferential(InstrumentKey tradedInstrument)
    {
        CurrencyStrengthSnapshot? snapshot = GetSnapshot(tradedInstrument);
        if (snapshot is null) return null;
        (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(tradedInstrument);
        if (!snapshot.Scores.TryGetValue(baseCurrency, out decimal baseScore) ||
            !snapshot.Scores.TryGetValue(quoteCurrency, out decimal quoteScore))
            return null;
        if (snapshot.Coverage.GetValueOrDefault(baseCurrency) < _options.MinimumCurrencyCoveragePercent ||
            snapshot.Coverage.GetValueOrDefault(quoteCurrency) < _options.MinimumCurrencyCoveragePercent)
            return null;
        return baseScore - quoteScore;
    }
}
