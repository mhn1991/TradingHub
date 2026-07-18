namespace DBManager.Abstractions.Execution;

/// <summary>
/// Order, broker-event and position persistence (design doc Phase 4, section 7.6 + 13.1).
/// Mirrors <c>ITradingPersistence</c>'s representative shape from section 15.
/// </summary>
public interface IExecutionStore
{
    Task<OrderSubmissionIntentResult> CreateSubmissionIntentAsync(
        CreateOrderSubmissionIntent command, CancellationToken cancellationToken);

    Task<DurableResult> ApplyImmediateSubmissionResultAsync(
        ApplyImmediateSubmissionResult command, CancellationToken cancellationToken);

    /// <summary>The atomic broker-event applier from section 13.1.</summary>
    Task<BrokerEventApplyResult> ApplyBrokerEventAsync(
        ApplyBrokerEvent command, CancellationToken cancellationToken);

    Task<RecoveryState> LoadRecoveryStateAsync(Guid brokerAccountId, CancellationToken cancellationToken);
}
