using Brokers.Models;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Evaluation;
using TradingClassifier.ML.Training;

namespace TradingClassifier.ML.Experiments;

public sealed record SelectionStep
{
    public required int Round { get; init; }
    public required FeatureGroups Added { get; init; }
    public required FeatureGroups Accumulated { get; init; }
    public required int FeatureCount { get; init; }
    public required double ValidationScore { get; init; }
    public required double PreviousScore { get; init; }

    /// <summary>Every candidate tried this round, best first — not just the winner.</summary>
    public required IReadOnlyList<(FeatureGroups Group, double Score)> Candidates { get; init; }

    public double Gain => ValidationScore - PreviousScore;
}

public sealed record SelectionResult
{
    public required IReadOnlyList<SelectionStep> Steps { get; init; }
    public required IReadOnlyList<(FeatureGroups Group, double Score)> Rejected { get; init; }
    public required FeatureGroups Selected { get; init; }
    public required WalkForwardResult TestResult { get; init; }
    public required int TestFeatureCount { get; init; }
}

/// <summary>
/// Greedy forward selection over feature groups: start from OHLC, and at each round try every group
/// not yet chosen, keep the one that improves the most, stop when nothing improves.
/// <para>
/// The point of doing this in code rather than by reading a ladder table is <b>where the decision is
/// made</b>. Hand-reordering a ladder by its test-period net is selection on the test set — the exact
/// mechanism that crowned §3.12's rung 3 before §3.12g destroyed it on 3.5 years. Here every add
/// decision is scored on the <b>validation</b> slice of each walk-forward window; the test period is
/// untouched until the group set is frozen, and is then scored exactly once.
/// </para>
/// <para>
/// Groups that never improve validation are simply never added, which is the automated form of
/// "demote the harmful ones to the end" — with the ordering discovered rather than assumed.
/// </para>
/// </summary>
public sealed class GreedyGroupSelector
{
    private readonly ClassifierOptions _baseOptions;
    private readonly Func<IModelTrainer> _trainerFactory;
    private readonly TradingCostModel _costs;
    private readonly BarInterval _interval;
    private readonly bool _allowOverlap;
    private readonly double _minimumGain;

    public GreedyGroupSelector(
        ClassifierOptions baseOptions,
        Func<IModelTrainer> trainerFactory,
        TradingCostModel? costs = null,
        BarInterval interval = default,
        bool allowOverlappingPositions = false,
        double minimumGain = 0.0)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(trainerFactory);
        _baseOptions = baseOptions;
        _trainerFactory = trainerFactory;
        _costs = costs ?? TradingCostModel.Free;
        _interval = interval.IsValid ? interval : BarInterval.Minutes(15);
        _allowOverlap = allowOverlappingPositions;
        _minimumGain = minimumGain;
    }

    /// <summary>Groups offered to the search, in no particular order — the order is the output.</summary>
    public static IReadOnlyList<FeatureGroups> Candidates { get; } =
    [
        FeatureGroups.Bollinger, FeatureGroups.Rsi, FeatureGroups.Cci, FeatureGroups.Analysis,
        FeatureGroups.Adx, FeatureGroups.StochRsi, FeatureGroups.Donchian, FeatureGroups.Efficiency,
        FeatureGroups.Volume, FeatureGroups.Structure, FeatureGroups.SupportResistance,
        FeatureGroups.SupplyDemand, FeatureGroups.Liquidity, FeatureGroups.Regime,
        // One candidate per trend timeframe, so the search can keep 2h and reject 30m —
        // offering the union would make the cascade all-or-nothing.
        FeatureGroups.TrendState30m, FeatureGroups.TrendState1h, FeatureGroups.TrendState2h,
        FeatureGroups.Trend, FeatureGroups.Macd, FeatureGroups.Atr
    ];

    /// <summary>
    /// Called as each candidate is scored. A search that prints nothing for hours is
    /// indistinguishable from a hung one — the ATR-normalised run died after four silent hours with
    /// no way to tell which it had been.
    /// </summary>
    public Action<string>? Progress { get; init; }

    public SelectionResult Run(
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan,
        FeatureGroups seed = FeatureGroups.Experiment1)
    {
        ArgumentNullException.ThrowIfNull(candles);

        FeatureExperimentRunner runner = new(
            _baseOptions, _trainerFactory, _costs, _interval, _allowOverlap);

        FeatureGroups accumulated = seed;
        double best = Validate(runner, accumulated, candles, trainSpan, validationSpan, testSpan);
        List<SelectionStep> steps = [];
        List<FeatureGroups> remaining = [.. Candidates.Where(group => !accumulated.HasFlag(group))];
        List<(FeatureGroups, double)> rejected = [];

        for (int round = 1; remaining.Count > 0; round++)
        {
            FeatureGroups? winner = null;
            double winnerScore = best;
            List<(FeatureGroups Group, double Score)> tried = [];

            Progress?.Invoke($"round {round}: {remaining.Count} candidates, base {accumulated}");
            foreach (FeatureGroups candidate in remaining)
            {
                double score = Validate(
                    runner, accumulated | candidate, candles, trainSpan, validationSpan, testSpan);
                tried.Add((candidate, score));
                Progress?.Invoke($"    {candidate,-22}{score,12:F2}");
                if (score > winnerScore + _minimumGain)
                {
                    winnerScore = score;
                    winner = candidate;
                }
            }
            tried.Sort((left, right) => right.Score.CompareTo(left.Score));

            if (winner is not FeatureGroups chosen)
            {
                // Nothing left improves validation. Reuse the scores already computed this round
                // rather than recomputing them — the old code paid for every candidate twice on the
                // final round, which on an annotation-backed search is a full extra pass.
                rejected.AddRange(tried);
                break;
            }

            steps.Add(new SelectionStep
            {
                Round = round,
                Added = chosen,
                Accumulated = accumulated | chosen,
                FeatureCount = FeatureSchema.Create(
                    _baseOptions with { EnabledGroups = accumulated | chosen }).Count,
                ValidationScore = winnerScore,
                PreviousScore = best,
                Candidates = tried
            });

            accumulated |= chosen;
            best = winnerScore;
            remaining.Remove(chosen);
        }

        // The frozen set meets the test period for the first and only time here.
        ExperimentResult test = runner.Run(
            "selected", accumulated, candles, trainSpan, validationSpan, testSpan);

        return new SelectionResult
        {
            Steps = steps,
            Rejected = rejected,
            Selected = accumulated,
            TestResult = test.WalkForward,
            TestFeatureCount = test.FeatureCount
        };
    }

    /// <summary>
    /// Validation-only score for a group set: pooled net across the VALIDATION slices, never test.
    /// </summary>
    private double Validate(
        FeatureExperimentRunner runner,
        FeatureGroups groups,
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        ExperimentResult result = runner.RunValidationOnly(
            groups, candles, trainSpan, validationSpan, testSpan);
        return (double)result.WalkForward.Pooled.NetProfit;
    }
}
