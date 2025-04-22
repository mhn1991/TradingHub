namespace Brokers.Oanda;

public class Oanda: Broker
{
    private string? _apiKey;
    private string? _secretKey;

    public Oanda()
    {
        this.Init("Oanda", "Rest", "https://api-fxpractice.oanda.com/");
    }

    public Oanda(string apiKey, string secretKey)
    {
        this._apiKey = apiKey;
        this._secretKey = secretKey;
        this.Init("Oanda", "Rest", "https://api-fxpractice.oanda.com/");
    }
}