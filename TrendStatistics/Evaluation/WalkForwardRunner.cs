using TrendStatistics.Data;
using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Segmentation;
using TrendStatistics.Trading;

namespace TrendStatistics.Evaluation;

public sealed record WalkForwardWindow
{
    public required int Index { get; init; }
    public required DateTimeOffset TrainThrough { get; init; }
    public required DateTimeOffset TestFrom { get; init; }
    public required DateTimeOffset TestTo { get; init; }
    public required int TrainingTrends { get; init; }
    public required decimal SelectedEntryPercentile { get; init; }
    public required SwingEntryMode SelectedMode { get; init; }
    public required TrendMetricsReport Test { get; init; }
}

public sealed record WalkForwardReport
{
    public required string Symbol { get; init; }
    public required IReadOnlyList<WalkForwardWindow> Windows { get; init; }
    public required TrendMetricsReport Pooled { get; init; }
    public required bool RegimeConditioned { get; init; }

    public int PositiveWindows => Windows.Count(window => window.Test.NetPct > 0m);

    /// <summary>Section 69: a positive majority of windows, not merely a positive total.</summary>
    public bool MajorityPositive => Windows.Count > 0 && PositiveWindows * 2 > Windows.Count;

    /// <summary>Section 69: no single window may account for essentially all the profit.</summary>
    public bool IsConcentrated => Windows.Count > 1 && Pooled.NetPct > 0m
        && Windows.Max(window => window.Test.NetPct) >= Pooled.NetPct;

    public bool MeetsSuccessBar =>
        Pooled.ProfitFactor > 1.0 && Pooled.NetPct > 0m && MajorityPositive && !IsConcentrated;

    public string ToText()
    {
        System.Text.StringBuilder builder = new();
        builder.AppendLine($"Walk-forward {Symbol}{(RegimeConditioned ? " (regime-conditioned)" : "")}: " +
            $"{Windows.Count} windows");
        builder.AppendLine($"  {"win",4}{"test from",13}{"test to",13}{"trainN",8}{"entry",7}{"mode",14}" +
            $"{"trades",8}{"PF",8}{"net%",9}");
        foreach (WalkForwardWindow window in Windows)
        {
            builder.AppendLine($"  {window.Index,4}{window.TestFrom:  yyyy-MM-dd}{window.TestTo:   yyyy-MM-dd}" +
                $"{window.TrainingTrends,8}{window.SelectedEntryPercentile,7:F2}{window.SelectedMode,14}" +
                $"{window.Test.Trades,8}{window.Test.ProfitFactor,8:F3}{window.Test.NetPct,9:F2}");
        }
        builder.AppendLine($"  POOLED  {Pooled.ToText()}");
        builder.AppendLine($"  positive windows {PositiveWindows}/{Windows.Count}   " +
            $"concentrated={IsConcentrated}   section 69 bar: {(MeetsSuccessBar ? "MET" : "not met")}");
        return builder.ToString();
    }
}

/// <summary>
/// Section 48's walk-forward, and the thing that decides whether any earlier result was real.
/// <para>
/// Every number reported before this existed came from one pass over all history. The profiles were
/// causal, but the <i>threshold</i> - P10, time-only - was chosen by looking at the whole period.
/// Section 48 forbids exactly that: the test period must not influence trend statistics, bootstrap
/// quantiles, or entry thresholds before it is evaluated.
/// </para>
/// <para>
/// So each window here selects its own threshold and mode on a validation slice carved from the
/// training history, freezes the profile built from trends completed before the test starts, and
/// only then evaluates. A window that picks a different threshold than its neighbours is
/// information, not a bug - it says the choice is unstable.
/// </para>
/// </summary>
public sealed class WalkForwardRunner(
    TrendDetectorConfig config,
    TrendCostModel? costs = null,
    int minimumSamples = 30)
{
    private readonly TrendDetectorConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly TrendCostModel _costs = costs ?? TrendCostModel.Free;
    private readonly int _minimumSamples = minimumSamples;

    public WalkForwardReport Run(
        IReadOnlyList<Candle> candles,
        string symbol,
        TimeSpan testSpan,
        TimeSpan validationSpan,
        TimeSpan minimumTrainSpan,
        IReadOnlyList<decimal>? entryGrid = null,
        IReadOnlyList<SwingEntryMode>? modeGrid = null,
        bool regimeConditioned = false)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0)
            throw new ArgumentException("No candles supplied.", nameof(candles));

        entryGrid ??= [0.05m, 0.10m, 0.15m, 0.20m, 0.25m];
        modeGrid ??= [SwingEntryMode.PriceOnly, SwingEntryMode.TimeOnly, SwingEntryMode.PriceAndTime];

        // Segment once: the detector is causal, so the trend library is identical regardless of how
        // it is later sliced. Section 49's rule is about which trends a PROFILE may see, not about
        // re-running the detector per window.
        IReadOnlyList<TrendRecord> allTrends = new TrendSegmenter(_config).Segment(candles);

        DateTimeOffset start = candles[0].OpenTime;
        DateTimeOffset end = candles[^1].OpenTime;
        List<WalkForwardWindow> windows = [];
        List<SwingTrade> pooled = [];

        DateTimeOffset testFrom = start + minimumTrainSpan;
        int index = 1;

        while (testFrom + testSpan <= end)
        {
            DateTimeOffset testTo = testFrom + testSpan;
            DateTimeOffset validationFrom = testFrom - validationSpan;

            // Threshold selection sees only trends completed before validation begins.
            TrendRecord[] preValidation = [.. allTrends.Where(trend => trend.EndTime <= validationFrom)];
            if (preValidation.Length >= _minimumSamples)
            {
                (decimal entry, SwingEntryMode mode) = SelectOnValidation(
                    candles, symbol, preValidation, validationFrom, testFrom,
                    entryGrid, modeGrid, regimeConditioned);

                // The test profile may use everything completed before the test starts - including
                // the validation period, which is now in the past.
                TrendRecord[] preTest = [.. allTrends.Where(trend => trend.EndTime <= testFrom)];
                RegimeClassifier? regimes = regimeConditioned ? RegimeClassifier.Fit(preTest) : null;
                ProfileRepository frozen = ProfileRepository.Build(symbol, preTest, regimes);

                IReadOnlyList<SwingTrade> trades = new TrendBacktester(_config, _costs).Run(
                    candles, frozen, regimes,
                    new SwingEntryOptions
                    {
                        EntryPercentile = entry,
                        Mode = mode,
                        MinimumTrendSamples = _minimumSamples,
                        MaximumEntryPercentile = 0.95m
                    },
                    testFrom, testTo, _minimumSamples);

                windows.Add(new WalkForwardWindow
                {
                    Index = index++,
                    TrainThrough = testFrom,
                    TestFrom = testFrom,
                    TestTo = testTo,
                    TrainingTrends = preTest.Length,
                    SelectedEntryPercentile = entry,
                    SelectedMode = mode,
                    Test = TrendMetricsReport.From(trades)
                });
                pooled.AddRange(trades);
            }

            testFrom = testTo;
        }

        return new WalkForwardReport
        {
            Symbol = symbol,
            Windows = windows,
            Pooled = TrendMetricsReport.From(pooled),
            RegimeConditioned = regimeConditioned
        };
    }

    /// <summary>
    /// Picks the entry threshold and mode on the validation slice alone. Ties and empty results fall
    /// back to the blueprint's own starting suggestion (section 28's P05, price-based) rather than
    /// to whatever happened to score highest on two trades.
    /// </summary>
    private (decimal Entry, SwingEntryMode Mode) SelectOnValidation(
        IReadOnlyList<Candle> candles,
        string symbol,
        IReadOnlyList<TrendRecord> trainingTrends,
        DateTimeOffset validationFrom,
        DateTimeOffset validationTo,
        IReadOnlyList<decimal> entryGrid,
        IReadOnlyList<SwingEntryMode> modeGrid,
        bool regimeConditioned)
    {
        RegimeClassifier? regimes = regimeConditioned ? RegimeClassifier.Fit(trainingTrends) : null;
        ProfileRepository frozen = ProfileRepository.Build(symbol, trainingTrends, regimes);
        TrendBacktester backtester = new(_config, _costs);

        decimal bestEntry = 0.05m;
        SwingEntryMode bestMode = SwingEntryMode.PriceOnly;
        double bestScore = double.NegativeInfinity;

        foreach (SwingEntryMode mode in modeGrid)
        {
            foreach (decimal entry in entryGrid)
            {
                IReadOnlyList<SwingTrade> trades = backtester.Run(
                    candles, frozen, regimes,
                    new SwingEntryOptions
                    {
                        EntryPercentile = entry,
                        Mode = mode,
                        MinimumTrendSamples = _minimumSamples,
                        MaximumEntryPercentile = 0.95m
                    },
                    validationFrom, validationTo, _minimumSamples);

                // Too few validation trades cannot distinguish skill from luck, so such a cell is
                // not allowed to win the selection.
                if (trades.Count < 5)
                    continue;

                TrendMetricsReport report = TrendMetricsReport.From(trades);
                double score = double.IsFinite(report.ProfitFactor) ? report.ProfitFactor : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEntry = entry;
                    bestMode = mode;
                }
            }
        }

        return (bestEntry, bestMode);
    }
}
