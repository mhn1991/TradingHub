using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;

namespace ExecutionManager;

public interface IExecutionCoordinator
{
    Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default);

    Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        ProtectiveStopAmendmentCommand command,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default);
}
