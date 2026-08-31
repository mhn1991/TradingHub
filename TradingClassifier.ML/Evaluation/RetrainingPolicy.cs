namespace TradingClassifier.ML.Evaluation;

public enum RetrainTrigger
{
    None,
    Scheduled,
    InputDrift,
    PredictionDrift,
    PerformanceDecay
}

public sealed record RetrainDecision
{
    public required bool ShouldRetrain { get; init; }
    public required RetrainTrigger Trigger { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Decides when a model should be refitted — the ML catalogue's Phase 7 "automatic retraining".
/// <para>
/// Deliberately a <b>policy object that returns a decision</b>, not a scheduler that acts on it.
/// Automatic retraining on a live account is an unattended write to production behaviour, and the
/// judgement of when that is acceptable belongs to the host, not to a library. This class makes the
/// trigger auditable; something else has to agree to it.
/// </para>
/// <para>
/// Order matters: input drift is checked before performance decay. A model whose inputs have left
/// the training distribution is not underperforming, it is being asked the wrong question, and
/// refitting on the new regime is a different act from refitting on more of the same.
/// </para>
/// </summary>
public sealed record RetrainingPolicy
{
    /// <summary>Refit after this many events regardless of drift. Zero disables the schedule.</summary>
    public int ScheduledEventInterval { get; init; } = 2_000;

    /// <summary>PSI above which inputs count as having moved. 0.25 is the conventional "significant".</summary>
    public double InputDriftThreshold { get; init; } = 0.25;

    /// <summary>PSI on the model's own output distribution.</summary>
    public double PredictionDriftThreshold { get; init; } = 0.25;

    /// <summary>
    /// Fraction of the reference expectancy below which performance counts as decayed. 0.5 means
    /// "halved". Compared in R, never in currency: PROJECT_STATE §3.14 amendment 1 records that a
    /// decaying equity curve makes currency comparisons a function of when a trade happened.
    /// </summary>
    public double ExpectancyDecayFraction { get; init; } = 0.5;

    /// <summary>Never act on fewer than this many events; small samples produce false alarms.</summary>
    public int MinimumEvents { get; init; } = 200;

    public RetrainDecision Evaluate(
        int eventsSinceLastFit,
        IReadOnlyList<DriftReport> inputDrift,
        DriftReport? predictionDrift,
        double referenceExpectancyR,
        double currentExpectancyR)
    {
        ArgumentNullException.ThrowIfNull(inputDrift);

        if (eventsSinceLastFit < MinimumEvents)
        {
            return new RetrainDecision
            {
                ShouldRetrain = false,
                Trigger = RetrainTrigger.None,
                Reason = $"Only {eventsSinceLastFit} events since the last fit; {MinimumEvents} required."
            };
        }

        DriftReport? worstInput = inputDrift
            .Where(report => report.PopulationStabilityIndex >= InputDriftThreshold)
            .OrderByDescending(report => report.PopulationStabilityIndex)
            .FirstOrDefault();

        if (worstInput is not null)
        {
            return new RetrainDecision
            {
                ShouldRetrain = true,
                Trigger = RetrainTrigger.InputDrift,
                Reason = $"Feature '{worstInput.Name}' PSI {worstInput.PopulationStabilityIndex:F3} " +
                    $"({worstInput.Verdict}); the model is being asked about a distribution it never saw."
            };
        }

        if (predictionDrift is not null &&
            predictionDrift.PopulationStabilityIndex >= PredictionDriftThreshold)
        {
            return new RetrainDecision
            {
                ShouldRetrain = true,
                Trigger = RetrainTrigger.PredictionDrift,
                Reason = $"Output distribution PSI {predictionDrift.PopulationStabilityIndex:F3} " +
                    $"({predictionDrift.Verdict})."
            };
        }

        // Only meaningful against a reference that was actually positive; halving a negative
        // expectancy is an improvement, not decay.
        if (referenceExpectancyR > 0 &&
            currentExpectancyR < referenceExpectancyR * ExpectancyDecayFraction)
        {
            return new RetrainDecision
            {
                ShouldRetrain = true,
                Trigger = RetrainTrigger.PerformanceDecay,
                Reason = $"Expectancy fell from {referenceExpectancyR:F4}R to {currentExpectancyR:F4}R."
            };
        }

        if (ScheduledEventInterval > 0 && eventsSinceLastFit >= ScheduledEventInterval)
        {
            return new RetrainDecision
            {
                ShouldRetrain = true,
                Trigger = RetrainTrigger.Scheduled,
                Reason = $"{eventsSinceLastFit} events since the last fit."
            };
        }

        return new RetrainDecision
        {
            ShouldRetrain = false,
            Trigger = RetrainTrigger.None,
            Reason = "Inputs, outputs and expectancy are all within tolerance."
        };
    }
}
