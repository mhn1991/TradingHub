using Brokers;
using DBManager.Data;
using DBManager.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingCore;

public class TradingCore
{
    static async Task Main(string[] args)
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
        Broker broker = new Broker("BINANCE",brokerRepo);
        await broker.GetChart();

    }
}