namespace Brokers.Brokers;

public class Binance : Broker
{
    private string? _apiKey;
    private string? _secretKey;

    public Binance(string apiKey, string secretKey)
    {
        this._apiKey = apiKey;
        this._secretKey = secretKey;
        this.Init("Binance","Rest","https://api.binance.com/api/v3/");
    }

    public Binance()
    {
        this.Init("Binance", "Rest", "https://api.binance.com/api/v3/");
    }
}