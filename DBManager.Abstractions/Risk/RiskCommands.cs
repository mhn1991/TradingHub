namespace DBManager.Abstractions.Risk;

public sealed record RecordPositionSizingEvaluation
{
    public required Guid SizingId { get; init; }
    public required Guid CandidateId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required long AccountSnapshotVersion { get; init; }
    public required long InstrumentMetadataRevision { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required decimal Equity { get; init; }
    public required decimal BaseRiskAmount { get; init; }
    public required decimal StopDistance { get; init; }
    public required decimal ExpectedSpread { get; init; }
    public required decimal ExpectedSlippage { get; init; }
    public required decimal ExpectedCommission { get; init; }
    public required decimal ConversionRate { get; init; }
    public required string ConversionPath { get; init; }
    public required decimal RawQuantity { get; init; }
    public required decimal NormalizedQuantity { get; init; }
    public required decimal EstimatedStopLoss { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required bool Approved { get; init; }
    public required string ReasonCode { get; init; }
    public required string AuditJson { get; init; }
}

/// <summary>
/// One candidate's requested share of a decision epoch's risk budget. Sizing math and ranking
/// happen in the domain layer (RiskManager/PortfolioManager) — this store only makes the epoch's
/// admission decision durable and atomic (section 3's non-goal: PostgreSQL is not a direct
/// dependency of RiskManager/PortfolioManager).
/// </summary>
public sealed record PortfolioEpochCandidate
{
    public required Guid CandidateId { get; init; }
    public required int Rank { get; init; }
    public required decimal RequestedRisk { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required string PortfolioSnapshotJson { get; init; }
}

/// <summary>
/// The atomic decision-epoch admission transaction (section 12.2). Candidates are admitted in
/// rank order until <see cref="MaxAccountReservedRisk"/> would be exceeded by the sum of this
/// account's already-active reservations plus the candidate being considered; the rest are
/// recorded as rejected in the same transaction. This is the persistence layer's actual
/// "cannot oversubscribe risk" guarantee — a hard ceiling enforced atomically under an
/// account-keyed lock, not a re-implementation of risk sizing.
/// </summary>
public sealed record CommitPortfolioEpoch
{
    public required Guid BrokerAccountId { get; init; }
    public required DateTimeOffset DecisionEpoch { get; init; }
    public required decimal MaxAccountReservedRisk { get; init; }
    public required TimeSpan ReservationTtl { get; init; }
    public required IReadOnlyList<PortfolioEpochCandidate> Candidates { get; init; }
}

public sealed record TransitionReservation
{
    public required Guid ReservationId { get; init; }
    public required ReservationState ToState { get; init; }
    public required string ReasonCode { get; init; }
    public Guid? RelatedOrderId { get; init; }
    public required string DetailsJson { get; init; }
}
