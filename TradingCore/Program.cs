using DBManager;
using DBManager.Data;
using DBManager.Repositories;
using DBManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradeManager;

namespace TradingCore;
using Agent;

public class TradingCore
{
    static void Main(string[] args)
    {
        var services = new ServiceCollection();

        // Add DbContext
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql("Server=localhost;Port=54320;User Id=db;Password=mysecretpassword;Database=tradinghub;"));

        // Register repositories (MISSING in your code)
        services.AddScoped<IBrokerRepository, BrokerRepository>();

        // Register DBManager services
        services.AddScoped<BrokerService>();

        // Register TradeManager service
        services.AddScoped<TradeManagerService>();

        // Build the service provider
        var serviceProvider = services.BuildServiceProvider();

        // Create an agent with injected dependencies
        var tradeManagerService = serviceProvider.GetRequiredService<TradeManagerService>();
        //Agent oandAgent = new Agent(tradeManagerService, ); // Save for later use in tests
    }
}