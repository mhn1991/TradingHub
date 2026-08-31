namespace TradingClassifier.Evaluation;

/// <summary>
/// Maps a raw model score to a calibrated probability.
/// <para>
/// The repo already <i>reports</i> Brier score, log loss and expected calibration error
/// (see <c>ClassificationMetrics</c>) but nothing has ever <b>corrected</b> the probabilities.
/// That gap matters the moment a probability is used as more than a ranking: an expected-value
/// threshold, a position size, or a "take if p &gt; x" rule all assume p means what it says.
/// </para>
/// <para>
/// MUST be fitted on validation data only. Fitting on training data reproduces the model's own
/// overconfidence; fitting on test data is selection on the answer.
/// </para>
/// </summary>
public interface IProbabilityCalibrator
{
    string Name { get; }

    double Calibrate(double rawScore);
}

/// <summary>
/// Platt scaling: a one-dimensional logistic fitted to the raw score, <c>p = 1 / (1 + exp(A·s + B))</c>.
/// <para>
/// The first choice for small samples (V2 §4.5). It has two parameters, so it cannot chase noise the
/// way isotonic regression can, and it degrades gracefully when the validation slice is thin — which
/// is the normal case here, where a walk-forward fold may hold only a few dozen events.
/// </para>
/// <para>
/// Uses Platt's own target smoothing: positives are fitted toward <c>(N+ + 1)/(N+ + 2)</c> rather
/// than 1, which stops the fit running the coefficients to infinity when a fold happens to be
/// perfectly separable.
/// </para>
/// </summary>
public sealed class PlattCalibrator : IProbabilityCalibrator
{
    private readonly double _a;
    private readonly double _b;
    private readonly bool _identity;

    private PlattCalibrator(double a, double b, bool identity = false)
    {
        _a = a;
        _b = b;
        _identity = identity;
    }

    public string Name => "Platt";

    public double A => _a;
    public double B => _b;

    /// <summary>
    /// Pass-through, for when a fold cannot support a fit. Returns the raw score unchanged rather
    /// than any sigmoid of it — a two-parameter curve fitted to one outcome class would encode the
    /// base rate as certainty.
    /// </summary>
    public static PlattCalibrator Identity { get; } = new(1.0, 0.0, identity: true);

    public static PlattCalibrator Fit(
        IReadOnlyList<double> scores,
        IReadOnlyList<bool> outcomes,
        double l2 = 1e-6,
        int iterations = 200)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (scores.Count != outcomes.Count)
            throw new ArgumentException("Scores and outcomes must be the same length.", nameof(outcomes));

        int positives = outcomes.Count(item => item);
        int negatives = outcomes.Count - positives;

        // Nothing to learn from a single-class slice; a fit here would just encode the base rate as
        // certainty.
        if (positives == 0 || negatives == 0)
            return Identity;

        double highTarget = (positives + 1.0) / (positives + 2.0);
        double lowTarget = 1.0 / (negatives + 2.0);

        double a = 1.0, b = 0.0;
        for (int step = 0; step < iterations; step++)
        {
            double gradientA = 2 * l2 * a, gradientB = 2 * l2 * b;
            double hessianAa = 2 * l2, hessianBb = 2 * l2, hessianAb = 0;

            for (int index = 0; index < scores.Count; index++)
            {
                double score = scores[index];
                double target = outcomes[index] ? highTarget : lowTarget;
                double p = Sigmoid(a * score + b);
                double error = p - target;
                double weight = Math.Max(p * (1 - p), 1e-12);

                gradientA += error * score;
                gradientB += error;
                hessianAa += weight * score * score;
                hessianBb += weight;
                hessianAb += weight * score;
            }

            double determinant = (hessianAa * hessianBb) - (hessianAb * hessianAb);
            if (Math.Abs(determinant) < 1e-15)
                break;

            double stepA = ((hessianBb * gradientA) - (hessianAb * gradientB)) / determinant;
            double stepB = ((hessianAa * gradientB) - (hessianAb * gradientA)) / determinant;
            a -= stepA;
            b -= stepB;

            if (Math.Abs(stepA) + Math.Abs(stepB) < 1e-10)
                break;
        }

        return new PlattCalibrator(a, b);
    }

    // Must mirror the form used in Fit exactly: p = sigmoid(a·s + b). Fitting one orientation and
    // applying the other inverts every correction, which shows up as calibration making the Brier
    // score worse rather than better.
    public double Calibrate(double rawScore) =>
        _identity ? rawScore : Sigmoid((_a * rawScore) + _b);

    private static double Sigmoid(double value) => value >= 0
        ? 1.0 / (1.0 + Math.Exp(-value))
        : Math.Exp(value) / (1.0 + Math.Exp(value));
}

/// <summary>
/// Isotonic regression via pool-adjacent-violators: a monotone step function fitted to the data.
/// <para>
/// Strictly more flexible than Platt and strictly more able to overfit — it can place a step at every
/// observation. V2 §4.5 is explicit that it should only be considered when the validation sample is
/// large enough, so <c>Fit</c> returns null below its minimum-sample floor rather than a
/// confident fit to noise.
/// </para>
/// </summary>
public sealed class IsotonicCalibrator : IProbabilityCalibrator
{
    private readonly double[] _thresholds;
    private readonly double[] _values;

    private IsotonicCalibrator(double[] thresholds, double[] values)
    {
        _thresholds = thresholds;
        _values = values;
    }

    public string Name => "Isotonic";

    public static IsotonicCalibrator? Fit(
        IReadOnlyList<double> scores,
        IReadOnlyList<bool> outcomes,
        int minimumSamples = 200)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (scores.Count != outcomes.Count || scores.Count < minimumSamples)
            return null;

        var ordered = scores
            .Select((score, index) => (Score: score, Value: outcomes[index] ? 1.0 : 0.0))
            .OrderBy(item => item.Score)
            .ToArray();

        // Pool adjacent violators: merge neighbouring blocks until the sequence is non-decreasing.
        List<(double Sum, int Count, double MaxScore)> blocks = [];
        foreach ((double score, double value) in ordered)
        {
            blocks.Add((value, 1, score));
            while (blocks.Count > 1)
            {
                var last = blocks[^1];
                var previous = blocks[^2];
                if (previous.Sum / previous.Count <= last.Sum / last.Count)
                    break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (previous.Sum + last.Sum, previous.Count + last.Count, last.MaxScore);
            }
        }

        return new IsotonicCalibrator(
            [.. blocks.Select(block => block.MaxScore)],
            [.. blocks.Select(block => block.Sum / block.Count)]);
    }

    public double Calibrate(double rawScore)
    {
        if (_thresholds.Length == 0)
            return rawScore;

        int index = Array.BinarySearch(_thresholds, rawScore);
        if (index < 0)
            index = ~index;
        if (index >= _values.Length)
            index = _values.Length - 1;
        return _values[index];
    }
}
