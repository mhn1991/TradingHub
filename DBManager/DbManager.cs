using DBManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DBManager;

using Npgsql;
using System.Data;
using System.Threading.Tasks;

public class DbManager
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DbManager(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task ExecuteQueryAsync(string query)
    {
        // Use NpgsqlConnection directly for async support
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        
        // Open the connection asynchronously
        await connection.OpenAsync();  // OpenAsync is available on NpgsqlConnection
        
        using var command = connection.CreateCommand();
        command.CommandText = query;

        // Execute the query asynchronously
        await command.ExecuteNonQueryAsync(); 
    }

    // Example method to read data asynchronously
    public async Task<IDataReader> ExecuteReaderAsync(string query)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        
        await connection.OpenAsync();  // Open connection asynchronously
        using var command = connection.CreateCommand();
        command.CommandText = query;

        // Execute and return data reader asynchronously
        return await command.ExecuteReaderAsync();
    }
    
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            // You can replace this with your actual connection string or configuration
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

            // Read the connection string from the appsettings.json or environment variables
            var connectionString = "Host=localhost;Port=54320;Username=db;Password=mysecretpassword;Database=tradinghub";

            optionsBuilder.UseNpgsql(connectionString);  // Or UseSqlServer() depending on your DB type

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}