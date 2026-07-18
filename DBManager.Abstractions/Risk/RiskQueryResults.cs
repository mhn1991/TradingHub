namespace DBManager.Abstractions.Risk;

public sealed record CandidateAdmissionOutcome
{
    public required Guid CandidateId { get; init; }
    public required bool Approved { get; init; }
    public Guid? ReservationId { get; init; }
    public required string ReasonCode { get; init; }
}

public sealed record PortfolioAdmissionResult
{
    public required DurableResult Result { get; init; }
    public required IReadOnlyList<CandidateAdmissionOutcome> Outcomes { get; init; }
}

public sealed record ReservationDetail
{
    public required Guid ReservationId { get; init; }
    public required Guid CandidateId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required ReservationState State { get; init; }
    public required decimal ReservedRisk { get; init; }
    public required decimal ReservedMargin { get; init; }
    public required decimal ReservedQuantity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required long Version { get; init; }
}
