using DBManager.Postgres.Analytics;
using DBManager.Postgres.Config;
using DBManager.Postgres.Decision;
using DBManager.Postgres.Execution;
using DBManager.Postgres.Management;
using DBManager.Postgres.Operations;
using DBManager.Postgres.Operations.Backup;
using DBManager.Postgres.Reference;
using DBManager.Postgres.Research;
using DBManager.Postgres.Risk;
using DBManager.Postgres.Security;
using DBManager.Postgres.Simulation;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres;

/// <summary>
/// EF Core context used for schema migrations and administrative CRUD (section 14.2). Entity sets
/// are added schema-by-schema as each implementation phase reaches them.
/// </summary>
public sealed class TradingHubDbContext(DbContextOptions<TradingHubDbContext> options) : DbContext(options)
{
    public DbSet<BrokerEntity> Brokers => Set<BrokerEntity>();
    public DbSet<BrokerEnvironmentEntity> BrokerEnvironments => Set<BrokerEnvironmentEntity>();
    public DbSet<BrokerEndpointRevisionEntity> BrokerEndpointRevisions => Set<BrokerEndpointRevisionEntity>();
    public DbSet<CredentialReferenceEntity> CredentialReferences => Set<CredentialReferenceEntity>();
    public DbSet<BrokerAccountSettingsRevisionEntity> BrokerAccountSettingsRevisions => Set<BrokerAccountSettingsRevisionEntity>();
    public DbSet<BrokerAccountEntity> BrokerAccounts => Set<BrokerAccountEntity>();
    public DbSet<InstrumentEntity> Instruments => Set<InstrumentEntity>();
    public DbSet<BrokerInstrumentEntity> BrokerInstruments => Set<BrokerInstrumentEntity>();

    public DbSet<PolicyProfileEntity> PolicyProfiles => Set<PolicyProfileEntity>();
    public DbSet<PolicyRevisionEntity> PolicyRevisions => Set<PolicyRevisionEntity>();
    public DbSet<PolicyPromotionEventEntity> PolicyPromotionEvents => Set<PolicyPromotionEventEntity>();
    public DbSet<CalibrationArtifactEntity> CalibrationArtifacts => Set<CalibrationArtifactEntity>();
    public DbSet<CalibrationArtifactStatusEventEntity> CalibrationArtifactStatusEvents => Set<CalibrationArtifactStatusEventEntity>();
    public DbSet<CalibrationBundleCandidateEntity> CalibrationBundleCandidates => Set<CalibrationBundleCandidateEntity>();
    public DbSet<PolicyArtifactEntity> PolicyArtifacts => Set<PolicyArtifactEntity>();
    public DbSet<DeploymentEntity> Deployments => Set<DeploymentEntity>();
    public DbSet<DeploymentActivationEventEntity> DeploymentActivationEvents => Set<DeploymentActivationEventEntity>();
    public DbSet<DeploymentAssignmentEntity> DeploymentAssignments => Set<DeploymentAssignmentEntity>();
    public DbSet<PolicyPermissionEventEntity> PolicyPermissionEvents => Set<PolicyPermissionEventEntity>();
    public DbSet<ParityCertificationEntity> ParityCertifications => Set<ParityCertificationEntity>();
    public DbSet<DeploymentAgentEntity> DeploymentAgents => Set<DeploymentAgentEntity>();
    public DbSet<DeploymentCommandEntity> DeploymentCommands => Set<DeploymentCommandEntity>();
    public DbSet<DeploymentEventEntity> DeploymentEvents => Set<DeploymentEventEntity>();
    public DbSet<RuntimeProfileEntity> RuntimeProfiles => Set<RuntimeProfileEntity>();
    public DbSet<RuntimeProfileRevisionEntity> RuntimeProfileRevisions => Set<RuntimeProfileRevisionEntity>();

    public DbSet<DecisionKeyEntity> DecisionKeys => Set<DecisionKeyEntity>();
    public DbSet<AgentEvaluationEntity> AgentEvaluations => Set<AgentEvaluationEntity>();
    public DbSet<CandidateKeyEntity> CandidateKeys => Set<CandidateKeyEntity>();
    public DbSet<TradeCandidateEntity> TradeCandidates => Set<TradeCandidateEntity>();
    public DbSet<CandidateStageEventEntity> CandidateStageEvents => Set<CandidateStageEventEntity>();
    public DbSet<SetupStateEventEntity> SetupStateEvents => Set<SetupStateEventEntity>();

    public DbSet<PositionSizingEvaluationEntity> PositionSizingEvaluations => Set<PositionSizingEvaluationEntity>();
    public DbSet<PortfolioDecisionEntity> PortfolioDecisions => Set<PortfolioDecisionEntity>();
    public DbSet<ReservationEntity> Reservations => Set<ReservationEntity>();
    public DbSet<ReservationEventEntity> ReservationEvents => Set<ReservationEventEntity>();

    public DbSet<OrderCommandEntity> OrderCommands => Set<OrderCommandEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderEventEntity> OrderEvents => Set<OrderEventEntity>();
    public DbSet<BrokerTransactionEntity> BrokerTransactions => Set<BrokerTransactionEntity>();
    public DbSet<BrokerTransactionKeyEntity> BrokerTransactionKeys => Set<BrokerTransactionKeyEntity>();
    public DbSet<FillEntity> Fills => Set<FillEntity>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<PositionEventEntity> PositionEvents => Set<PositionEventEntity>();

    public DbSet<BrokerStreamCursorEntity> BrokerStreamCursors => Set<BrokerStreamCursorEntity>();
    public DbSet<ReconciliationRunEntity> ReconciliationRuns => Set<ReconciliationRunEntity>();
    public DbSet<ReconciliationDifferenceEntity> ReconciliationDifferences => Set<ReconciliationDifferenceEntity>();
    public DbSet<AccountLeaseEntity> AccountLeases => Set<AccountLeaseEntity>();
    public DbSet<SafetyEventEntity> SafetyEvents => Set<SafetyEventEntity>();
    public DbSet<OperatorCommandEntity> OperatorCommands => Set<OperatorCommandEntity>();
    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();
    public DbSet<AccountSnapshotEntity> AccountSnapshots => Set<AccountSnapshotEntity>();

    public DbSet<PositionManagementStateEntity> PositionManagementStates => Set<PositionManagementStateEntity>();
    public DbSet<ManagementEventEntity> ManagementEvents => Set<ManagementEventEntity>();

    public DbSet<ResearchRunEntity> ResearchRuns => Set<ResearchRunEntity>();
    public DbSet<TimeSeriesFoldEntity> TimeSeriesFolds => Set<TimeSeriesFoldEntity>();
    public DbSet<CalibrationRunEntity> CalibrationRuns => Set<CalibrationRunEntity>();

    public DbSet<CandidateOutcomeEntity> CandidateOutcomes => Set<CandidateOutcomeEntity>();
    public DbSet<ExecutionQualityEntity> ExecutionQualities => Set<ExecutionQualityEntity>();
    public DbSet<ModelMonitoringWindowEntity> ModelMonitoringWindows => Set<ModelMonitoringWindowEntity>();

    public DbSet<BackupRecordEntity> BackupRecords => Set<BackupRecordEntity>();
    public DbSet<BrokerCredentialEntity> BrokerCredentials => Set<BrokerCredentialEntity>();
    public DbSet<SimulationJobEntity> SimulationJobs => Set<SimulationJobEntity>();
    public DbSet<SimulationJobProgressEntity> SimulationJobProgress => Set<SimulationJobProgressEntity>();
    public DbSet<SimulationJobEventEntity> SimulationJobEvents => Set<SimulationJobEventEntity>();
    public DbSet<SimulationOutputManifestEntity> SimulationOutputManifests => Set<SimulationOutputManifestEntity>();
    public DbSet<SimulationOutputBlobEntity> SimulationOutputBlobs => Set<SimulationOutputBlobEntity>();
    public DbSet<SimulationExperimentEntity> SimulationExperiments => Set<SimulationExperimentEntity>();
    public DbSet<SimulationProfileEntity> SimulationProfiles => Set<SimulationProfileEntity>();
    public DbSet<SimulationProfileRevisionEntity> SimulationProfileRevisions => Set<SimulationProfileRevisionEntity>();
    public DbSet<SimulationExperimentRunEntity> SimulationExperimentRuns => Set<SimulationExperimentRunEntity>();
    public DbSet<SimulationExperimentComparisonEntity> SimulationExperimentComparisons => Set<SimulationExperimentComparisonEntity>();
    public DbSet<RuntimeSessionEntity> RuntimeSessions => Set<RuntimeSessionEntity>();
    public DbSet<TradingTelemetryEventEntity> TradingTelemetryEvents => Set<TradingTelemetryEventEntity>();
    public DbSet<AgentActivityWindowEntity> AgentActivityWindows => Set<AgentActivityWindowEntity>();
    public DbSet<ManualApprovalCandidateEntity> ManualApprovalCandidates => Set<ManualApprovalCandidateEntity>();
    public DbSet<ManualApprovalEventEntity> ManualApprovalEvents => Set<ManualApprovalEventEntity>();
    public DbSet<LiveEventJournalEntity> LiveEventJournal => Set<LiveEventJournalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        ReferenceModelConfiguration.Configure(modelBuilder);
        ConfigModelConfiguration.Configure(modelBuilder);
        DecisionModelConfiguration.Configure(modelBuilder);
        RiskModelConfiguration.Configure(modelBuilder);
        ExecutionModelConfiguration.Configure(modelBuilder);
        OperationsModelConfiguration.Configure(modelBuilder);
        ManagementModelConfiguration.Configure(modelBuilder);
        ResearchModelConfiguration.Configure(modelBuilder);
        AnalyticsModelConfiguration.Configure(modelBuilder);
        BackupModelConfiguration.Configure(modelBuilder);
        SecurityModelConfiguration.Configure(modelBuilder);
        SimulationModelConfiguration.Configure(modelBuilder);
        RuntimeObservabilityModelConfiguration.Configure(modelBuilder);
    }
}
