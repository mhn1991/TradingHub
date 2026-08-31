using TradingClassifier.Configuration;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Evaluation;

/// <summary>Everything sections 21 to 23 ask to be reported for one model on one row set.</summary>
public sealed record EvaluationResult
{
    public required string ModelName { get; init; }
    public required string Period { get; init; }
    public required ClassificationReport Classification { get; init; }
    public required TradingReport Trading { get; init; }
    public IReadOnlyList<FeatureImportance> Importance { get; init; } = [];

    public string ToText() =>
        $"[{ModelName} / {Period}]\n{Classification.ToText()}  {Trading.ToText()}";
}

/// <summary>Section 30's <c>IModelEvaluator</c>.</summary>
public interface IModelEvaluator
{
    EvaluationResult Evaluate(
        TrainedModel model,
        IReadOnlyList<LabeledFeatureRow> rows,
        string period,
        bool includeImportance = false);
}

/// <summary>
/// Runs the section 21 classification metrics and the section 22 trading metrics together.
/// <para>
/// They are deliberately not separable. Section 22 closes with the point this whole class exists to
/// enforce: a classifier that scores well and loses money after costs is not useful, so no caller
/// gets one report without the other.
/// </para>
/// </summary>
public sealed class ModelEvaluator : IModelEvaluator
{
    private readonly ClassifierOptions _options;
    private readonly TradingCostModel _costs;
    private readonly int _importanceSeed;

    private readonly bool _allowOverlap;

    public ModelEvaluator(
        ClassifierOptions options,
        TradingCostModel? costs = null,
        int importanceSeed = 0,
        bool allowOverlappingPositions = false)
    {
        _allowOverlap = allowOverlappingPositions;
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _costs = costs ?? TradingCostModel.Free;
        _importanceSeed = importanceSeed;
    }

    public EvaluationResult Evaluate(
        TrainedModel model,
        IReadOnlyList<LabeledFeatureRow> rows,
        string period,
        bool includeImportance = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("Cannot evaluate an empty period.", nameof(rows));

        IReadOnlyList<Prediction> predictions = model.PredictAll(rows);

        return new EvaluationResult
        {
            ModelName = model.TrainerName,
            Period = period,
            Classification = ClassificationEvaluator.Evaluate(rows.Select(row => row.Label).ToArray(), predictions),
            Trading = ClassifierBacktester.Run(rows, predictions, _options, _costs, _allowOverlap),
            Importance = includeImportance
                ? PermutationImportance.Compute(model.Model, rows, model.FeatureSchema, _importanceSeed)
                : []
        };
    }

    /// <summary>
    /// Section 20's threshold tuning. Searches a probability grid on the validation rows and
    /// returns the options that maximise expectancy.
    /// <para>
    /// Section 20 is specific that this must not touch test data, so the caller passes the
    /// validation slice; feeding it the test slice would be selecting the threshold on the very
    /// period used to report the result.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>maximumTradeRate</c> caps trades per validation row. Without it the search can select a
    /// threshold that degenerates into trading almost every bar: two 1m walk-forward windows took
    /// 7,448 and 10,032 trades and between them supplied 17,480 of one run's 19,200 total,
    /// dominating the result (PROJECT_STATE.md section 3.12g). A trade rate near 1 is not a
    /// strategy, it is the absence of a filter, and expectancy alone will not reject it.
    /// </remarks>
    public ClassifierOptions TuneThresholds(
        TrainedModel model,
        IReadOnlyList<LabeledFeatureRow> validation,
        IReadOnlyList<double>? grid = null,
        int minimumTrades = 20,
        double maximumTradeRate = 0.25)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(validation);
        grid ??= [0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90];

        IReadOnlyList<Prediction> predictions = model.PredictAll(validation);
        ClassifierOptions best = _options;
        decimal bestExpectancy = decimal.MinValue;

        foreach (double buy in grid)
        {
            foreach (double sell in grid)
            {
                ClassifierOptions candidate = _options with
                {
                    BuyProbabilityThreshold = buy,
                    SellProbabilityThreshold = sell
                };
                TradingReport report = ClassifierBacktester.Run(validation, predictions, candidate, _costs, _allowOverlap);

                // A threshold so high that it fires twice can post a spectacular expectancy on
                // noise. Requiring a minimum trade count is what stops the search converging on it.
                if (report.Trades < minimumTrades)
                    continue;

                // The opposite failure: a threshold so low it trades constantly. Rejected on the
                // rate rather than an absolute count so the bound scales with window length.
                if (validation.Count > 0 && (double)report.Trades / validation.Count > maximumTradeRate)
                    continue;

                // Overlapping holds make the trade count exceed what one account could take; a
                // candidate needing many simultaneous positions is not a tradeable configuration.
                if (report.MaximumConcurrentPositions > _options.PredictionHorizon)
                    continue;
                if (report.Expectancy > bestExpectancy)
                {
                    bestExpectancy = report.Expectancy;
                    best = candidate;
                }
            }
        }

        return best;
    }
}
