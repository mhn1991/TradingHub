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
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres;

/// <summary>
/// EF Core context used for schema migrations and administrative CRUD (section 14.2). Entity sets
/// are added schema-by-schema as each implementation phase reaches them.
/// </summary>
public sealed class TradingHubDbContext(DbContextOptions<TradingHubDbContext> options) : DbContext(options)
{
    public DbSet<BrokerEntity> Brokers => Set<BrokerEntity>();
    public DbSet<BrokerAccountEntity> BrokerAccounts => Set<BrokerAccountEntity>();
    public DbSet<InstrumentEntity> Instruments => Set<InstrumentEntity>();
    public DbSet<BrokerInstrumentEntity> BrokerInstruments => Set<BrokerInstrumentEntity>();

    public DbSet<PolicyProfileEntity> PolicyProfiles => Set<PolicyProfileEntity>();
    public DbSet<PolicyRevisionEntity> PolicyRevisions => Set<PolicyRevisionEntity>();
    public DbSet<PolicyPromotionEventEntity> PolicyPromotionEvents => Set<PolicyPromotionEventEntity>();
    public DbSet<CalibrationArtifactEntity> CalibrationArtifacts => Set<CalibrationArtifactEntity>();
    public DbSet<PolicyArtifactEntity> PolicyArtifacts => Set<PolicyArtifactEntity>();
    public DbSet<DeploymentEntity> Deployments => Set<DeploymentEntity>();
    public DbSet<DeploymentActivationEventEntity> DeploymentActivationEvents => Set<DeploymentActivationEventEntity>();
    public DbSet<DeploymentAssignmentEntity> DeploymentAssignments => Set<DeploymentAssignmentEntity>();

    public DbSet<DecisionKeyEntity> DecisionKeys => Set<DecisionKeyEntity>();
    public DbSet<AgentEvaluationEntity> AgentEvaluations => Set<AgentEvaluationEntity>();
    public DbSet<CandidateKeyEntity> CandidateKeys => Set<CandidateKeyEntity>();
    public DbSet<TradeCandidateEntity> TradeCandidates => Set<TradeCandidateEntity>();
    public DbSet<CandidateStageEventEntity> CandidateStageEvents => Set<CandidateStageEventEntity>();

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
    }
}
