using DBManager;
using DBManager.Repositories;
using DBManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradeManager;

namespace UnitTests;
using Agent;

public class AgentTests
{
    private Agent _agent;
    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();

        // Add DbContext
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql("Server=localhost;Port=54320;User Id=db;Password=mysecretpassword;Database=tradinghub;"));

        // Register repositories (MISSING in your code)
        services.AddScoped<ITradeRepository, TradeRepository>();
        services.AddScoped<IBrokerRepository, BrokerRepository>();

        // Register DBManager services
        services.AddScoped<TradeService>();
        services.AddScoped<BrokerService>();

        // Register TradeManager service
        services.AddScoped<TradeManagerService>();

        // Build the service provider
        var serviceProvider = services.BuildServiceProvider();

        // Create an agent with injected dependencies
        var tradeManagerService = serviceProvider.GetRequiredService<TradeManagerService>();
        _agent = new Agent(tradeManagerService); // Save for later use in tests

    }

    [Test]
    public async Task TestAgent()
    {
        //await _agent.InitAsync();
        await _agent.Run();
        //_agent.checkDB();
    }
}