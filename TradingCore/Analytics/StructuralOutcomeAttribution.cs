namespace TradingCore.Analytics;

public sealed record StructuralOutcomeObservation(
    Guid StableId,
    Guid StructuralEntityId,
    Guid? CandidateId,
    Guid? TradeId,
    DateTimeOffset StartedAt,
    DateTimeOffset ObservedAt,
    decimal MaximumFavourableExcursion,
    decimal MaximumAdverseExcursion,
    TimeSpan? TimeToReaction,
    TimeSpan? TimeToInvalidation,
    bool TargetReachedBeforeInvalidation,
    bool WasFresh,
    int PriorTouchCount,
    string Instrument,
    string Interval,
    string Session,
    string Regime,
    string StructuralType,
    IReadOnlyDictionary<string, decimal> Contributions);

/// <summary>
/// Pure, append-only outcome accumulator for research exports. It records observations but never
/// calibrates or changes a runtime policy; purging, embargo and final-test isolation remain the
/// responsibility of the existing research planners.
/// </summary>
public sealed class StructuralOutcomeAttributor(int maximumRetainedOutcomes = 10_000)
{
    private readonly Queue<StructuralOutcomeObservation> _outcomes = new();

    public IReadOnlyList<StructuralOutcomeObservation> Outcomes => _outcomes.ToArray();

    public void Record(StructuralOutcomeObservation outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.ObservedAt < outcome.StartedAt)
            throw new ArgumentException("An outcome cannot be observed before it starts.", nameof(outcome));
        if (maximumRetainedOutcomes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedOutcomes));

        _outcomes.Enqueue(outcome);
        while (_outcomes.Count > maximumRetainedOutcomes)
            _outcomes.Dequeue();
    }
}
