using DBManager.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DBManager.Repositories
{
    public interface IBrokerRepository
    {
        Task<IEnumerable<Broker>> GetAllBrokersAsync();
        Task<Broker> GetBrokerByIdAsync(int id);
        Task CreateBrokerAsync(Broker broker);
        Task UpdateBrokerAsync(Broker broker);
        Task DeleteBrokerAsync(int id);
    }
}