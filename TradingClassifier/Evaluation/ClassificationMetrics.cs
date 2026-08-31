using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.Evaluation;

/// <summary>Per-class precision/recall/F1 plus support.</summary>
public readonly record struct ClassMetrics(
    TradeLabel Label,
    double Precision,
    double Recall,
    double F1,
    double AveragePrecision,
    int Support);

/// <summary>
/// Section 21's metric set.
/// <para>
/// Accuracy is computed but deliberately reported alongside <see cref="MajorityClassRate"/>, which
/// is what a model that always answers NO_TRADE would score. Section 21 exists because those two
/// numbers being equal is the most common way a useless classifier looks successful.
/// </para>
/// </summary>
public sealed record ClassificationReport
{
    public required IReadOnlyList<ClassMetrics> PerClass { get; init; }
    public required int[,] ConfusionMatrix { get; init; }
    public required double Accuracy { get; init; }
    public required double MacroF1 { get; init; }
    public required double MajorityClassRate { get; init; }
    public required int Rows { get; init; }

    /// <summary>
    /// Multiclass log loss. Unlike accuracy or F1 this scores the probabilities themselves, which
    /// matters because the probability - not the argmax - is what drives the entry threshold and is
    /// reported as the agent's confidence.
    /// </summary>
    public required double LogLoss { get; init; }

    /// <summary>Multiclass Brier score: mean squared error of the probability vector. Lower is better.</summary>
    public required double BrierScore { get; init; }

    /// <summary>
    /// Expected calibration error over ten probability bins, for the BUY and SELL classes.
    /// <para>
    /// This is the number that decides whether "confidence 0.70" means anything. The signal
    /// generator fires at a probability threshold and the agent reports that probability as its
    /// confidence, so a model whose 0.70 bucket wins 45% of the time is mis-stating its own risk to
    /// every downstream consumer - and no accuracy or F1 figure would reveal it.
    /// </para>
    /// </summary>
    public required double ExpectedCalibrationError { get; init; }

    /// <summary>Per-bin reliability for the actionable classes: predicted vs observed frequency.</summary>
    public required IReadOnlyList<(double Predicted, double Observed, int Count)> ReliabilityBins { get; init; }

    public ClassMetrics For(TradeLabel label) => PerClass.First(metric => metric.Label == label);

    /// <summary>True when accuracy is no better than always predicting the majority class.</summary>
    public bool IsNoBetterThanMajority => Accuracy <= MajorityClassRate + 1e-9;

    public string ToText()
    {
        System.Text.StringBuilder builder = new();
        builder.AppendLine($"rows={Rows}  accuracy={Accuracy:P2}  majority-class={MajorityClassRate:P2}  macro-F1={MacroF1:F4}");
        builder.AppendLine($"  log-loss={LogLoss:F4}  Brier={BrierScore:F4}  calibration-error={ExpectedCalibrationError:P2}" +
            (ExpectedCalibrationError > 0.10 ? "   <-- probabilities are not trustworthy as confidence" : string.Empty));
        if (IsNoBetterThanMajority)
            builder.AppendLine("  WARNING: accuracy does not beat always predicting the majority class (section 21).");
        builder.AppendLine("  class     precision   recall       F1       AP  support");
        foreach (ClassMetrics metric in PerClass)
        {
            builder.AppendLine(
                $"  {metric.Label,-9}{metric.Precision,10:F4}{metric.Recall,9:F4}{metric.F1,9:F4}{metric.AveragePrecision,9:F4}{metric.Support,9}");
        }
        builder.AppendLine("  confusion (rows = actual, columns = predicted; order Sell/NoTrade/Buy)");
        for (int actual = 0; actual < 3; actual++)
        {
            builder.AppendLine(
                $"    {(TradeLabel)actual,-9}{ConfusionMatrix[actual, 0],8}{ConfusionMatrix[actual, 1],8}{ConfusionMatrix[actual, 2],8}");
        }
        return builder.ToString();
    }
}

public static class ClassificationEvaluator
{
    /// <summary>
    /// Scores argmax predictions against the true labels, and computes each class's average
    /// precision from its probability column (section 21's PR-AUC, in its finite-sample form).
    /// </summary>
    public static ClassificationReport Evaluate(
        IReadOnlyList<TradeLabel> actual,
        IReadOnlyList<Prediction> predicted)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(predicted);
        if (actual.Count != predicted.Count)
            throw new ArgumentException("Actual and predicted must be the same length.", nameof(predicted));
        if (actual.Count == 0)
            throw new ArgumentException("Cannot evaluate an empty set.", nameof(actual));

        int[,] confusion = new int[3, 3];
        for (int index = 0; index < actual.Count; index++)
            confusion[(int)actual[index], (int)predicted[index].MostLikely]++;

        List<ClassMetrics> perClass = [];
        double correct = 0;
        for (int label = 0; label < 3; label++)
        {
            int truePositive = confusion[label, label];
            int predictedPositive = 0;
            int actualPositive = 0;
            for (int other = 0; other < 3; other++)
            {
                predictedPositive += confusion[other, label];
                actualPositive += confusion[label, other];
            }

            // Undefined precision (nothing predicted for this class) is reported as 0, not skipped:
            // a model that never predicts SELL has failed at SELL, and averaging it away as "not
            // applicable" would hide exactly that.
            double precision = predictedPositive == 0 ? 0 : (double)truePositive / predictedPositive;
            double recall = actualPositive == 0 ? 0 : (double)truePositive / actualPositive;
            double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

            perClass.Add(new ClassMetrics(
                (TradeLabel)label,
                precision,
                recall,
                f1,
                AveragePrecision(actual, predicted, (TradeLabel)label),
                actualPositive));
            correct += truePositive;
        }

        int majority = 0;
        for (int label = 0; label < 3; label++)
            majority = Math.Max(majority, perClass[label].Support);

        (double logLoss, double brier) = ProbabilityScores(actual, predicted);
        (double calibrationError, var bins) = Calibration(actual, predicted);

        return new ClassificationReport
        {
            PerClass = perClass,
            ConfusionMatrix = confusion,
            Accuracy = correct / actual.Count,
            MacroF1 = perClass.Average(metric => metric.F1),
            MajorityClassRate = (double)majority / actual.Count,
            Rows = actual.Count,
            LogLoss = logLoss,
            BrierScore = brier,
            ExpectedCalibrationError = calibrationError,
            ReliabilityBins = bins
        };
    }

    private static (double LogLoss, double Brier) ProbabilityScores(
        IReadOnlyList<TradeLabel> actual,
        IReadOnlyList<Prediction> predicted)
    {
        double logLoss = 0;
        double brier = 0;

        for (int index = 0; index < actual.Count; index++)
        {
            double[] probabilities =
            [
                predicted[index].SellProbability,
                predicted[index].NoTradeProbability,
                predicted[index].BuyProbability
            ];

            // Clamped so a confident miss costs a large but finite amount; an unclamped log(0) would
            // make a single wrong certainty dominate the whole metric.
            double truth = Math.Clamp(probabilities[(int)actual[index]], 1e-15, 1);
            logLoss -= Math.Log(truth);

            for (int label = 0; label < 3; label++)
            {
                double target = label == (int)actual[index] ? 1 : 0;
                double error = probabilities[label] - target;
                brier += error * error;
            }
        }

        return (logLoss / actual.Count, brier / actual.Count);
    }

    /// <summary>
    /// Expected calibration error across ten bins, measured on the actionable classes only - a
    /// well-calibrated NO_TRADE probability is of no use to a trading decision.
    /// </summary>
    private static (double Error, IReadOnlyList<(double, double, int)> Bins) Calibration(
        IReadOnlyList<TradeLabel> actual,
        IReadOnlyList<Prediction> predicted)
    {
        const int BinCount = 10;
        double[] sumPredicted = new double[BinCount];
        int[] hits = new int[BinCount];
        int[] counts = new int[BinCount];

        void Accumulate(double probability, bool correct)
        {
            int bin = Math.Min(BinCount - 1, (int)(probability * BinCount));
            sumPredicted[bin] += probability;
            counts[bin]++;
            if (correct) hits[bin]++;
        }

        for (int index = 0; index < actual.Count; index++)
        {
            Accumulate(predicted[index].BuyProbability, actual[index] == TradeLabel.Buy);
            Accumulate(predicted[index].SellProbability, actual[index] == TradeLabel.Sell);
        }

        List<(double, double, int)> bins = [];
        double error = 0;
        int total = counts.Sum();

        for (int bin = 0; bin < BinCount; bin++)
        {
            if (counts[bin] == 0)
                continue;
            double meanPredicted = sumPredicted[bin] / counts[bin];
            double observed = (double)hits[bin] / counts[bin];
            bins.Add((meanPredicted, observed, counts[bin]));
            error += counts[bin] / (double)total * Math.Abs(meanPredicted - observed);
        }

        return (error, bins);
    }

    /// <summary>
    /// Average precision for one class, treated one-vs-rest: the precision-recall curve summarised
    /// by stepping down the probability ranking. This is the standard finite-sample stand-in for
    /// the PR-AUC section 21 asks for, and unlike ROC-AUC it does not flatter a model on the
    /// heavily imbalanced classes section 18 predicts.
    /// </summary>
    private static double AveragePrecision(
        IReadOnlyList<TradeLabel> actual,
        IReadOnlyList<Prediction> predicted,
        TradeLabel label)
    {
        double Probability(Prediction prediction) => label switch
        {
            TradeLabel.Sell => prediction.SellProbability,
            TradeLabel.NoTrade => prediction.NoTradeProbability,
            _ => prediction.BuyProbability
        };

        var ranked = Enumerable.Range(0, actual.Count)
            .Select(index => (Score: Probability(predicted[index]), IsPositive: actual[index] == label))
            .OrderByDescending(item => item.Score)
            .ToArray();

        int positives = ranked.Count(item => item.IsPositive);
        if (positives == 0)
            return 0;

        double truePositives = 0;
        double sum = 0;
        for (int index = 0; index < ranked.Length; index++)
        {
            if (!ranked[index].IsPositive)
                continue;
            truePositives++;
            sum += truePositives / (index + 1);
        }

        return sum / positives;
    }
}
