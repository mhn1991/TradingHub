using Brokers;
using DBManager;
using DBManager.Data;
using DBManager.Repositories;
using DBManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeManager;

namespace TradingCore;
using Agent;

public class TradingCore
{
    static void Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        var services = new ServiceCollection();

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Register your repository
        services.AddScoped<IBrokerRepository, BrokerRepository>();

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        IBrokerRepository brokerRepo = serviceProvider.GetRequiredService<IBrokerRepository>();
        Broker broker = new Broker("Binance",brokerRepo);

    }
}