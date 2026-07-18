namespace DBManager.Abstractions.Risk;

/// <summary>Risk, portfolio admission and reservation persistence (design doc Phase 3, section 7.5 + 12.2).</summary>
public interface IRiskStore
{
    Task<DurableResult> RecordPositionSizingEvaluationAsync(
        RecordPositionSizingEvaluation command, CancellationToken cancellationToken);

    /// <summary>The atomic decision-epoch admission transaction from section 12.2.</summary>
    Task<PortfolioAdmissionResult> CommitPortfolioEpochAsync(
        CommitPortfolioEpoch command, CancellationToken cancellationToken);

    Task<DurableResult> TransitionReservationAsync(
        TransitionReservation command, CancellationToken cancellationToken);

    /// <summary>
    /// All reservations still holding risk for this account (section 30 Phase 3 acceptance:
    /// "restart restores active reservations").
    /// </summary>
    Task<IReadOnlyList<ReservationDetail>> GetActiveReservationsAsync(
        Guid brokerAccountId, CancellationToken cancellationToken);
}
