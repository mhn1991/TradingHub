using Microsoft.ML;
using Microsoft.ML.Data;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Candidates;

internal sealed class RegressionInput
{
    [VectorType]
    public float[] Features { get; set; } = [];
    public float Label { get; set; }
}

internal sealed class RegressionOutput
{
    public float Score { get; set; }
}

/// <summary>What the regression is asked to predict.</summary>
public enum CandidateTarget
{
    /// <summary>After-cost realised R — what the decision rule consumes.</summary>
    RealizedR,

    /// <summary>How far the trade went in favour, in R. Informs target placement.</summary>
    MaximumFavourableR,

    /// <summary>How far it went against, in R. Diagnostic only — see MaximumAdverseAtr.</summary>
    MaximumAdverseR,

    /// <summary>
    /// How far it went against, in ATR. The target for stop placement, because it does not depend
    /// on the stop being placed.
    /// </summary>
    MaximumAdverseAtr
}

public sealed record FoldPrediction(TradingCandidate Candidate, float Predicted, float Realized)
{
    /// <summary>Outcome carried alongside so a target policy can be scored on the same rows.</summary>
    public CandidateOutcome Outcome { get; init; }
}

public sealed record RegressionReport
{
    public required string Model { get; init; }
    public required IReadOnlyList<FoldPrediction> Predictions { get; init; }
    public required int Folds { get; init; }

    /// <summary>
    /// The pre-registered statistic (PROJECT_STATE §3.18): out-of-sample Pearson correlation between
    /// predicted and realised R. The bar is 0.12; measured base rate is ~0.026.
    /// </summary>
    public double Rho
    {
        get
        {
            if (Predictions.Count < 3) return 0;
            double[] x = [.. Predictions.Select(p => (double)p.Predicted)];
            double[] y = [.. Predictions.Select(p => (double)p.Realized)];
            double mx = x.Average(), my = y.Average();
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double dx = x[i] - mx, dy = y[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
        }
    }

    public double MeanAbsoluteError =>
        Predictions.Count == 0 ? 0 : Predictions.Average(p => Math.Abs(p.Predicted - p.Realized));

    /// <summary>Mean realised R of the top <paramref name="fraction"/> of candidates by predicted R.</summary>
    public (int Kept, double MeanR, double TotalR) TopFraction(double fraction)
    {
        int keep = (int)Math.Round(Predictions.Count * fraction);
        if (keep <= 0) return (0, 0, 0);
        var top = Predictions.OrderByDescending(p => p.Predicted).Take(keep).ToArray();
        return (keep, top.Average(p => (double)p.Realized), top.Sum(p => (double)p.Realized));
    }

    public double BaselineMeanR =>
        Predictions.Count == 0 ? 0 : Predictions.Average(p => (double)p.Realized);
}

/// <summary>
/// V2 P2 slice: predict after-cost realised R directly, rather than direction at a fixed horizon.
/// <para>
/// Walk-forward by candidate event: fit on the training candidates, score the untouched test
/// candidates once, never look at the test period to choose anything. A regularised linear model is
/// the primary (§7 orders linear before LightGBM, so that LightGBM has to earn its complexity).
/// </para>
/// </summary>
public static class CandidateRegression
{
    public static RegressionReport Run(
        CandidateDataset dataset,
        int trainCandidates,
        int testCandidates,
        bool useLightGbm,
        int seed = 11,
        CandidateTarget target = CandidateTarget.RealizedR)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (trainCandidates <= 0 || testCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(trainCandidates));

        MLContext context = new(seed);
        List<FoldPrediction> predictions = [];
        int folds = 0;

        // ML.NET needs a KNOWN vector width; a bare [VectorType] yields VarVector, which the
        // normalizer rejects. The width is only known at runtime, so declare it in the schema.
        int width = dataset.Rows[0].Features.Length;
        SchemaDefinition schema = SchemaDefinition.Create(typeof(RegressionInput));
        schema[nameof(RegressionInput.Features)].ColumnType =
            new VectorDataViewType(NumberDataViewType.Single, width);

        for (int start = trainCandidates; start + testCandidates <= dataset.Rows.Count; start += testCandidates)
        {
            var train = dataset.Rows.Skip(start - trainCandidates).Take(trainCandidates).ToArray();
            var test = dataset.Rows.Skip(start).Take(testCandidates).ToArray();
            if (train.Length < 30 || test.Length == 0)
                continue;

            IDataView trainView = context.Data.LoadFromEnumerable(
                train.Select(r => new RegressionInput { Features = r.Features, Label = Label(r, target) }),
                schema);

            var estimator = context.Transforms.NormalizeMeanVariance("Features")
                .Append(useLightGbm
                    ? context.Regression.Trainers.LightGbm(
                        labelColumnName: "Label", featureColumnName: "Features",
                        numberOfLeaves: 8, minimumExampleCountPerLeaf: 40, numberOfIterations: 100,
                        learningRate: 0.05)
                    : (IEstimator<ITransformer>)context.Regression.Trainers.Sdca(
                        labelColumnName: "Label", featureColumnName: "Features",
                        l2Regularization: 0.5f, maximumNumberOfIterations: 100));

            ITransformer model = estimator.Fit(trainView);
            var engine = context.Model.CreatePredictionEngine<RegressionInput, RegressionOutput>(
                model, inputSchemaDefinition: schema);

            foreach (CandidateRow row in test)
            {
                RegressionOutput output = engine.Predict(
                    new RegressionInput { Features = row.Features, Label = 0f });
                predictions.Add(new FoldPrediction(row.Candidate, output.Score, Label(row, target))
                {
                    Outcome = row.Outcome
                });
            }
            folds++;
        }

        return new RegressionReport
        {
            Model = $"{(useLightGbm ? "LightGBM" : "Sdca-linear")}:{target}",
            Predictions = predictions,
            Folds = folds
        };
    }

    private static float Label(CandidateRow row, CandidateTarget target) => target switch
    {
        CandidateTarget.MaximumFavourableR => (float)row.Outcome.MaximumFavourableR,
        CandidateTarget.MaximumAdverseR => (float)row.Outcome.MaximumAdverseR,
        CandidateTarget.MaximumAdverseAtr => (float)row.Outcome.MaximumAdverseAtr,
        _ => row.Target
    };
}
