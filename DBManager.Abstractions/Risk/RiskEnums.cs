namespace DBManager.Abstractions.Risk;

/// <summary>
/// Reservation lifecycle (section 7.5). The partial-index states named in the doc
/// (Created/SubmissionPending/BrokerAccepted/PartiallyFilled) are the "still holding risk"
/// states; the remaining terminal states were designed this session since the doc names the
/// active subset but not the full enum.
/// </summary>
public enum ReservationState
{
    Created,
    SubmissionPending,
    BrokerAccepted,
    PartiallyFilled,
    Filled,
    Released,
    Expired,
    Rejected
}
