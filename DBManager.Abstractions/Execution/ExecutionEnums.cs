namespace DBManager.Abstractions.Execution;

/// <summary>What a durable order command asks the broker to do (section 7.6).</summary>
public enum OrderCommandType
{
    MarketEntry,
    StopReplace,
    TargetReplace,
    PartialClose,
    FullClose
}

public enum OrderCommandStatus
{
    Pending,
    InFlight,
    Completed,
    Failed
}

/// <summary>
/// Order lifecycle. <see cref="SubmissionPending"/> is the first state (section 12.5: "create
/// local order in SubmissionPending"), not a separate "Created" state.
/// </summary>
public enum OrderState
{
    SubmissionPending,
    SubmissionUnknown,
    Accepted,
    Rejected,
    PartiallyFilled,
    Filled,
    Closed,
    Cancelled
}

/// <summary>
/// Orthogonal to <see cref="OrderState"/> — whether the host actually knows the broker received
/// the submission (section 1.1's "unknown-submission state", section 12.5's "Unknown" result).
/// </summary>
public enum SubmissionCertainty
{
    Certain,
    Unknown
}

public enum OrderEventType
{
    Created,
    Submitted,
    Accepted,
    Rejected,
    SubmissionUnknown,
    PartialFill,
    Filled,
    StopReplaced,
    TargetReplaced,
    PartiallyReduced,
    Closed,
    ReconciliationCorrection
}

public enum BrokerTransactionType
{
    OrderFill,
    OrderCancel,
    OrderReject,
    Financing,
    Commission,
    MarginCall,
    AccountAdjustment,
    Unknown
}

public enum PositionState
{
    Open,
    PartiallyClosed,
    Closed
}

/// <summary>"Append-only fill, reduction, close and broker projection events" (section 7.6 prose).</summary>
public enum PositionEventType
{
    Opened,
    Increased,
    PartiallyReduced,
    StopUpdated,
    TargetUpdated,
    Closed,
    BrokerProjectionUpdate
}
