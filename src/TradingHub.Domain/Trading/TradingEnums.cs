namespace TradingHub.Domain.Trading;

public enum TradingEnvironment
{
    HistoricalSimulation,
    Shadow,
    Demo,
    Live
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum TimeInForce
{
    FillOrKill,
    ImmediateOrCancel,
    GoodTilCancelled,
    GoodTilDate
}

public enum OrderStatus
{
    Proposed,
    RiskRejected,
    Submitted,
    Acknowledged,
    PartiallyFilled,
    Filled,
    CancelPending,
    Cancelled,
    Rejected,
    Expired,
    Unknown
}

public enum SubmissionOutcome
{
    Accepted,
    Rejected,
    Unknown
}
