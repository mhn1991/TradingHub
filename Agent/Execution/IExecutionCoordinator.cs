using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;

namespace Agent.Execution;

public interface IExecutionCoordinator
{
    Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default);
}
