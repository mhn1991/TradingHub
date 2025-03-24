using System;

namespace DBManager.Models;

public class Trade
{
    public int Id { get; set; }  
    public int BrokerId { get; set; }  // Foreign Key to Brokers
    public string Symbol { get; set; }  // e.g., BTC/USD
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TakeProfit { get; set; }
    public TradeType TradeType { get; set; }  // Enum: Buy/Sell
    public DateTime Timestamp { get; set; }
    public TradeStatus Status { get; set; }  // Enum: Open/Closed
    public decimal Fee { get; set; }  
    public string TimeFrame { get; set; }
    public string OrderId { get; set; }  // If tracking orders separately
    public Broker Broker { get; set; } 
}