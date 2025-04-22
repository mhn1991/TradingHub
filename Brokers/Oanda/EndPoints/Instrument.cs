using Brokers.Interfaces;

namespace Brokers.Oanda.EndPoints;

public class Instrument: Oanda, Iinstrument
{
    private string _endpoint;

    public string GetEndpoint() => _endpoint;
    
    public void SetEndpoint(string endpoint)
    {
        this._endpoint = $"/v3/instruments/{endpoint}/candles";
    }
    
    public string GetTheLastKlines(string granularity,int count)
    {
        // price M means mid A means Ask B means Bid I can later use them
        return $"{this._baseUrl}{this._endpoint}?count={count}&granularity={granularity}&price=M";
    }
}