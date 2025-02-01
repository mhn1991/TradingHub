namespace Brokers;

public abstract class Broker
{
    protected string _brokerName;
    protected string _apiType;
    protected string _baseUrl;

    protected void Init(string brokerName, string apiType, string baseUrl)
    {
        _brokerName = brokerName;
        _apiType = apiType;
        _baseUrl = baseUrl;
    }
    
    public string BrokerName => _brokerName;
    public string ApiType => _apiType;
    public string BaseUrl => _baseUrl;
}