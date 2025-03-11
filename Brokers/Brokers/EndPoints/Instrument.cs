namespace Brokers.Brokers;

public class Instrument : Binance
{
    private string _coinName;
    private string _endPoint = "klines";
    public string CoinName => _coinName;
    
    public Instrument(string coinName, string interval, string limit)
    {
        this._coinName = coinName;
        this._endPoint = $"klines?symbol={this._coinName}&interval={interval}&limit={limit}";
    }
    
    public Instrument(string coinName, string interval, string limit, string endTime)
    {
        this._coinName = coinName;
        this._endPoint = $"klines?symbol={this._coinName}&interval={interval}&limit={limit}&endTime={endTime}";
    }

    public string GetTheLastKlines(string interval,int limit)
    {
        return $"{this._baseUrl}klines?symbol={this._coinName}&interval={interval}&limit={limit}";
    }

    public string GetFinalUrl()
    {
        return $"{this._baseUrl}{this._endPoint}";
    }
}