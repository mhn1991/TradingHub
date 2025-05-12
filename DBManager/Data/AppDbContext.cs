using DBManager.Models;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Data;

public class AppDbContext:DbContext
{
    public DbSet<Broker> Brokers { get; set; }
    public DbSet<Endpoint> Endpoints { get; set; }
    public DbSet<Parameter> Parameters { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Apply model constraints
        modelBuilder.Entity<Broker>()
            .HasKey(b => b.Name);

        modelBuilder.Entity<Endpoint>()
            .HasOne(e => e.Broker)
            .WithMany(b => b.Endpoints)
            .HasForeignKey(e => e.BrokerName)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Parameter>()
            .HasOne(p => p.Broker)
            .WithMany()
            .HasForeignKey(p => p.BrokerName);

        modelBuilder.Entity<Parameter>()
            .HasIndex(p => new { p.BrokerName, p.Path, p.Name })
            .IsUnique();
    }

    private void SetUpBrokers(ModelBuilder modelBuilder)
    {
        Broker Binance = new Broker()
        {
            Name = "BINANCE",
            APIKey = null,
            SecretKey = null,
            BaseURL = "https://api.binance.com/api/v3/",
        };
        Endpoint BinanceEnd = new Endpoint()
        {
            Id = 1,
            BrokerName = "BINANCE",
            Path = "klines",
            ProtocolType = "REST",
            ActionType = "GET",
        };
        Parameter BinanceParam = new Parameter()
        {
            Id = 1,
            BrokerName = "BINANCE",
            Path = "klines",
            Name = "symbol",
            LocatedIn = "query",
            Type = "string",
            Value = null
        };
    }
}