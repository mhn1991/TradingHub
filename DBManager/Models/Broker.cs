using System.Collections.Generic;

namespace DBManager.Models;

public class Broker
{
    public int Id { get; set; }  // Primary Key
    public string Name { get; set; }  // Broker name (e.g., Binance, Kraken)
    public string ApiKey { get; set; }  // API key (encrypted for security)
    public string ApiSecret { get; set; }  // API secret (encrypted)
    public string Url { get; set; }  // API base URL
    public ICollection<Trade> Trades { get; set; }  // Relationship with trades
}
