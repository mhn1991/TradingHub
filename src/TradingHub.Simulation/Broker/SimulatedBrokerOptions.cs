namespace TradingHub.Simulation.Broker;

public sealed record SimulatedBrokerOptions
{
    public required string BrokerId { get; init; }

    public required string AccountId { get; init; }

    public required string AccountCurrency { get; init; }

    public required decimal InitialBalance { get; init; }

    public TimeSpan OutboundLatency { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(BrokerId)
            || string.IsNullOrWhiteSpace(AccountId)
            || string.IsNullOrWhiteSpace(AccountCurrency))
        {
            throw new InvalidOperationException("Simulated broker identifiers are required.");
        }

        if (InitialBalance <= 0m || OutboundLatency < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Simulated balance and latency are invalid.");
        }
    }
}
