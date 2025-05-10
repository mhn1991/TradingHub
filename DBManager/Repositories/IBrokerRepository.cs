using DBManager.Models;

public interface IBrokerRepository
{
    Task<IEnumerable<Broker>> GetAllBrokersAsync();
    Task<Broker?> GetBrokerByNameAsync(string name);
    Task CreateBrokerAsync(Broker broker);
    Task UpdateBrokerAsync(Broker broker);
    Task DeleteBrokerAsync(string name);
}