namespace DBManager.Abstractions.Config;

/// <summary>Status of a <c>config.policy_revisions</c> row (section 7.2).</summary>
public enum PolicyRevisionStatus
{
    Research = 0,
    Reviewed = 1,
    ApprovedForDemo = 2,
    Retired = 3,
    Backtested = 4,
    Validated = 5,
    ShadowCertified = 6,
    DemoCertified = 7,
    LiveCertified = 8,
    Suspended = 9
}

[Flags]
public enum PolicyExecutionPermission
{
    None = 0,
    Shadow = 1,
    DemoManual = 2,
    DemoAutomatic = 4,
    LiveManual = 8,
    LiveAutomatic = 16
}

/// <summary>Status of a <c>config.calibration_artifacts</c> row.</summary>
public enum CalibrationArtifactStatus
{
    Registered,
    Validated,
    ApprovedForDemo,
    Retired
}

/// <summary>
/// Role an artifact plays within a policy revision, or the kind of artifact produced
/// (section 7.2's <c>config.policy_artifacts.role</c>).
/// </summary>
public enum ArtifactRole
{
    SetupCalibration,
    MetaModel,
    ManagementCalibration
}

/// <summary>How a deployment is allowed to act on Agent decisions.</summary>
public enum ExecutionMode
{
    Simulated,
    ManualApproval,
    Automatic
}

/// <summary>Lifecycle state of a <c>config.deployments</c> row (section 13.4).</summary>
public enum DeploymentStatus
{
    Active = 0,
    Running = Active,
    Stopped = 1,
    Requested = 2,
    Validating = 3,
    Preparing = 4,
    WarmingUp = 5,
    Reconciling = 6,
    Paused = 7,
    Draining = 8,
    Faulted = 9
}

public enum DeploymentAgentStatus
{
    Requested,
    Validating,
    Preparing,
    WarmingUp,
    Running,
    Paused,
    Draining,
    Stopped,
    Faulted,
    ValidationFailed
}

public enum DeploymentCommandType
{
    CreateDeployment,
    StartAgent,
    PauseAgent,
    ResumeAgent,
    StopAgent,
    DrainAgent,
    ReplaceAgentRevision,
    StopDeployment,
    Reconcile,
    PauseDeployment,
    ResumeDeployment
}

public enum DeploymentCommandStatus
{
    Pending,
    Claimed,
    Completed,
    Failed
}

public enum ParityCertificationStatus
{
    Pending,
    Passed,
    Failed,
    Stale
}
