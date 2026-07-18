namespace DBManager.Abstractions.Operations;

public enum ReconciliationTrigger
{
    Scheduled,
    Startup,
    Manual,
    SafetyEvent
}

public enum ReconciliationRunStatus
{
    Running,
    Completed,
    Failed
}

public enum ReconciliationDifferenceType
{
    MissingLocalOrder,
    MissingLocalPosition,
    UnknownBrokerPosition,
    QuantityMismatch,
    StateMismatch,
    PriceMismatch
}

public enum ReconciliationSeverity
{
    Informational,
    Warning,
    Critical
}

/// <summary>
/// The corrective action taken. Section 13.3: "Do not automatically adopt an unknown broker
/// position without an explicit policy and audit event" — <see cref="AdoptBrokerValue"/> is
/// always an explicit, audited call, never an automatic default.
/// </summary>
public enum ReconciliationAction
{
    AdoptBrokerValue,
    AdoptLocalValue,
    ManualOverride,
    NoActionRequired
}
