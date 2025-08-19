using DBManager.Models;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Data;

public class AppDbContext : DbContext
{
    public DbSet<Broker> Brokers { get; set; }
    public DbSet<Endpoint> Endpoints { get; set; }
    public DbSet<Parameter> Parameters { get; set; }
    public DbSet<Unit> Units { get; set; }
    public DbSet<UnitConversion> UnitConversions { get; set; }

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
        modelBuilder.Entity<Endpoint>()
            .Property(e => e.EndpointType)
            .HasConversion<string>();

        SetUpBaseBrokers(modelBuilder);
        SetUpUnits(modelBuilder);
    }

    private void SetUpBaseBrokers(ModelBuilder modelBuilder)
    {
        // Seed Broker
        modelBuilder.Entity<Broker>().HasData(new Broker
        {
            Name = "BINANCE",
            APIKey = null,
            SecretKey = null,
            BaseURL = "https://api.binance.com/api/v3/"
        });

        // Seed Endpoint
        modelBuilder.Entity<Endpoint>().HasData(new Endpoint
        {
            Id = 1,
            BrokerName = "BINANCE",
            Path = "klines",
            ProtocolType = "REST",
            ActionType = "GET",
            EndpointType = EndpointType.GetCandles
        });

        // Seed Parameter
        modelBuilder.Entity<Parameter>().HasData(new Parameter
        {
            Id = 1,
            BrokerName = "BINANCE",
            Path = "klines",
            Name = "symbol",
            LocatedIn = "query",
            Type = "string",
            Value = null
        });
    }

    private void SetUpUnits(ModelBuilder modelBuilder)
    {
        var year = new Unit
            { UnitId = 1, Symbol = "Year", UnitType = "Time", Description = "Calendar Year (365 days)" };
        var month = new Unit
            { UnitId = 2, Symbol = "Month", UnitType = "Time", Description = "Calendar Month (30 days approx)" };
        var week = new Unit { UnitId = 3, Symbol = "Week", UnitType = "Time", Description = "7 Days" };
        var day = new Unit { UnitId = 4, Symbol = "Day", UnitType = "Time", Description = "24 Hours" };
        var hour = new Unit { UnitId = 5, Symbol = "Hour", UnitType = "Time", Description = "60 Minutes" };
        var minute = new Unit { UnitId = 6, Symbol = "Minute", UnitType = "Time", Description = "60 Seconds" };
        var second = new Unit { UnitId = 7, Symbol = "Second", UnitType = "Time", Description = "Base Time Unit" };

        modelBuilder.Entity<Unit>().HasData(year, month, week, day, hour, minute, second);

        // --- Forward conversions ---
        var conversions = new List<UnitConversion>
        {
            new UnitConversion
            {
                ConversionId = 1, FromUnitId = year.UnitId, ToUnitId = month.UnitId, FormulaType = FormulaType.Factor,
                FormulaValue = 12
            },
            new UnitConversion
            {
                ConversionId = 2, FromUnitId = month.UnitId, ToUnitId = week.UnitId, FormulaType = FormulaType.Factor,
                FormulaValue = 4.345m
            }, // approx
            new UnitConversion
            {
                ConversionId = 3, FromUnitId = week.UnitId, ToUnitId = day.UnitId, FormulaType = FormulaType.Factor,
                FormulaValue = 7
            },
            new UnitConversion
            {
                ConversionId = 4, FromUnitId = day.UnitId, ToUnitId = hour.UnitId, FormulaType = FormulaType.Factor,
                FormulaValue = 24
            },
            new UnitConversion
            {
                ConversionId = 5, FromUnitId = hour.UnitId, ToUnitId = minute.UnitId, FormulaType = FormulaType.Factor,
                FormulaValue = 60
            },
            new UnitConversion
            {
                ConversionId = 6, FromUnitId = minute.UnitId, ToUnitId = second.UnitId,
                FormulaType = FormulaType.Factor, FormulaValue = 60
            }
        };

        // --- Reverse conversions (auto-generated) ---
        var reverseConversions = conversions.Select((c, index) => new UnitConversion
        {
            ConversionId = 100 + index, // unique IDs for reverse
            FromUnitId = c.ToUnitId,
            ToUnitId = c.FromUnitId,
            FormulaType = FormulaType.Factor,
            FormulaValue = 1 / c.FormulaValue
        });

        // --- Register both ---
        modelBuilder.Entity<UnitConversion>().HasData(conversions.Concat(reverseConversions));
    }
}