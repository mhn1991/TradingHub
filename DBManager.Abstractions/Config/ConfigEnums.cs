namespace DBManager.Abstractions.Config;

/// <summary>Status of a <c>config.policy_revisions</c> row (section 7.2).</summary>
public enum PolicyRevisionStatus
{
    Research,
    Reviewed,
    ApprovedForDemo,
    Retired
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
    Active,
    Stopped
}
