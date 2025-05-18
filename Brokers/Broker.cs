using DBManager.Services;

namespace Brokers;

public class Broker
{
    BrokerService _brokerService;
    public Broker(string brokerName, IBrokerRepository brokerRepository)
    {
        _brokerService = new BrokerService(brokerRepository);
    }
    
    public void GetChartCandles()
    {
        
    } 
}