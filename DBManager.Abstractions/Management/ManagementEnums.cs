namespace DBManager.Abstractions.Management;

/// <summary>Position management lifecycle stage (section 7.7 — values not literally specified by the doc).</summary>
public enum ManagementStage
{
    Initial,
    BreakEven,
    Trailing,
    PartialTaken,
    Runner
}

public enum ManagementActionType
{
    Observed,
    BreakEvenApplied,
    StopTrailed,
    PartialReduced,
    TargetReached,
    StopReplacementRejected,
    FullyClosed
}

public enum AccountLeaseStatus
{
    Active,
    Released,
    Expired
}

/// <summary>Shared across <c>reconciliation_differences</c> and <c>safety_events</c> — both are informational/warning/critical.</summary>
public enum OperationsSeverity
{
    Informational,
    Warning,
    Critical
}

public enum SafetySource
{
    Broker,
    RiskManager,
    ExecutionManager,
    Reconciliation,
    Operator,
    MarketData
}

/// <summary>Section 1.1's "safety directives" / section 23.2's outage execution policy.</summary>
public enum SafetyDirective
{
    PauseEntries,
    ResumeEntries,
    FlattenPosition,
    FlattenAll,
    RequireManualApproval
}

public enum OperatorCommandType
{
    FlattenPosition,
    FlattenAll,
    PauseEntries,
    ResumeEntries,
    StopDeployment,
    ForceReconciliation
}

public enum OperatorCommandStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Rejected
}

/// <summary>
/// Mirrors <c>LiveEngineCheckpoint</c>'s composition (<c>LiveTrading/Persistence/LiveTradingPersistence.cs</c>) —
/// that file-based checkpoint is the emergency-spool path (section 23.1); this is its PostgreSQL counterpart.
/// </summary>
public enum CheckpointType
{
    EngineState,
    AgentState,
    ManagementState
}

public enum AccountSnapshotTrigger
{
    Periodic,
    PostFill,
    PostClose,
    PostReconciliation,
    PostSafetyDirective
}
