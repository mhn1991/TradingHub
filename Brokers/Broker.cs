using DBManager.Models;
using DBManager.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Brokers;

public class Broker
{
    private BrokerService _brokerService;
    private string _brokerName;
    public Broker(string brokerName, IBrokerRepository brokerRepository)
    {
        _brokerService = new BrokerService(brokerRepository);
        _brokerName = brokerName;
    }
    
    public async Task GetChart()
    {
        DBManager.Models.Broker? broker = await _brokerService.GetBrokerWithEndpointsByTypeAsync(_brokerName,EndpointType.GetCandles);
        Console.WriteLine(broker.ToString());
    } 
}