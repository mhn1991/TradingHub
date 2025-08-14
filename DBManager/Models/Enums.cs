namespace DBManager.Models;

public enum FormulaType
{
    Factor,       // Factor only (time, length)
    FactorOffset  // Factor + offset (temperature, etc.)
}

public enum TradeType
{
    BUY,
    SELL
}

public enum TradeStatus
{
    Open,
    Closed
}

public enum EndpointType
{
    GetCandles,
    OrderBook,
    Ticker,
    Trades,
    PlaceOrder,
    CancelOrder,
    Balance,
    Position,
    AccountInfo
}