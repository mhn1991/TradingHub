using DBManager;
using DBManager.Data;
using DBManager.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql("Server=localhost;Port=54320;User Id=db;Password=mysecretpassword;Database=tradinghub;")); // Or use PostgreSQL, SQLite, etc.

        // Register repositories
        services.AddScoped<IBrokerRepository, BrokerRepository>();

        // Other service registrations
    }
}