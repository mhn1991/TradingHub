using Microsoft.EntityFrameworkCore;
using DBManager.Models;

namespace DBManager
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // Define your DbSet properties here (e.g., for Trade, Broker, etc.)
        public DbSet<Trade> Trades { get; set; }
        public DbSet<Broker> Brokers { get; set; }
    }
}