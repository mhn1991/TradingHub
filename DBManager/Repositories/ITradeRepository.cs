using DBManager.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DBManager.Repositories
{
    public interface ITradeRepository
    {
        Task<IEnumerable<Trade>> GetAllTradesAsync();
        Task<Trade> GetTradeByIdAsync(int id);
        Task CreateTradeAsync(Trade trade);
        Task UpdateTradeAsync(Trade trade);
        Task DeleteTradeAsync(int id);
    }
}