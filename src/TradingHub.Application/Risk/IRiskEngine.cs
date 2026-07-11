using TradingHub.Domain.Trading;

namespace TradingHub.Application.Risk;

public interface IRiskEngine
{
    RiskDecision Evaluate(TradeIntent intent, RiskContext context);
}
