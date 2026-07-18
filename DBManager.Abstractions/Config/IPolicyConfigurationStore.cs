namespace DBManager.Abstractions.Config;

/// <summary>
/// Configuration and deployment persistence (design doc Phase 1, section 7.2 + 13.4). Backed by
/// EF Core per section 14.2 — configuration entities are moderate-volume administrative CRUD, not
/// the high-frequency critical lane.
/// </summary>
public interface IPolicyConfigurationStore
{
    Task<DurableResult> CreatePolicyProfileAsync(CreatePolicyProfile command, CancellationToken cancellationToken);

    Task<DurableResult> CreatePolicyRevisionAsync(CreatePolicyRevision command, CancellationToken cancellationToken);

    Task<DurableResult> PromotePolicyRevisionAsync(PromotePolicyRevision command, CancellationToken cancellationToken);

    Task<DurableResult> RegisterCalibrationArtifactAsync(
        RegisterCalibrationArtifact command, CancellationToken cancellationToken);

    Task<DurableResult> PromoteCalibrationArtifactAsync(
        PromoteCalibrationArtifact command, CancellationToken cancellationToken);

    Task<DurableResult> LinkPolicyArtifactAsync(LinkPolicyArtifact command, CancellationToken cancellationToken);

    /// <summary>The atomic policy-activation transaction described in section 13.4.</summary>
    Task<DurableResult> ActivateDeploymentAsync(ActivateDeployment command, CancellationToken cancellationToken);

    Task<DurableResult> SetDeploymentAssignmentAsync(
        SetDeploymentAssignment command, CancellationToken cancellationToken);

    /// <summary>Loads the exact promoted revision by ID. There is no "latest" query by design.</summary>
    Task<PolicyRevisionDetail?> GetPolicyRevisionAsync(Guid policyRevisionId, CancellationToken cancellationToken);

    Task<ActiveDeploymentDetail?> GetActiveDeploymentAsync(
        Guid brokerAccountId, CancellationToken cancellationToken);
}
