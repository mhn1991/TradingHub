namespace DBManager.Abstractions.Operations;

public interface IReconciliationStore
{
    Task<DurableResult> StartReconciliationRunAsync(StartReconciliationRun command, CancellationToken cancellationToken);

    Task<DurableResult> CompleteReconciliationRunAsync(
        CompleteReconciliationRun command, CancellationToken cancellationToken);

    Task<DurableResult> RecordDifferenceAsync(
        RecordReconciliationDifference command, CancellationToken cancellationToken);

    /// <summary>The atomic repair transaction from section 13.3.</summary>
    Task<DurableResult> ApplyCorrectionAsync(
        ApplyReconciliationCorrection command, CancellationToken cancellationToken);
}
