using DBManager;
using DBManager.Repositories;
using DBManager.Services;
using Microsoft.EntityFrameworkCore;

namespace UnitTests;
using Agent;

public class AgentTests
{
    private Agent _agent;
    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Your_Connection_String")
            .Options;

        var dbContext = new ApplicationDbContext(options);
        var tradeRepository = new TradeRepository(dbContext);
        var brokerRepository = new BrokerRepository(dbContext);

        var tradeService = new TradeService(tradeRepository);
        var brokerService = new BrokerService(brokerRepository);

        var agent = new Agent(brokerService, tradeService);
        _agent = agent;

    }

    [Test]
    public async Task TestAgent()
    {
        //await _agent.InitAsync();
        await _agent.Run();
        //_agent.checkDB();
    }
}