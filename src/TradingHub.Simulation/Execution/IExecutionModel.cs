using TradingHub.Domain.Markets;

namespace TradingHub.Simulation.Execution;

internal interface IExecutionModel
{
    FillDecision? TryExecute(
        PendingOrder order,
        PriceBar bar,
        InstrumentDefinition instrument);
}
