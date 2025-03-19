using DBManager.Models;
using DBManager.Repositories;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DBManager.Services
{
    public class TradeService
    {
        private readonly ITradeRepository _tradeRepository;

        public TradeService(ITradeRepository tradeRepository)
        {
            _tradeRepository = tradeRepository;
        }

        // Get all trades
        public async Task<IEnumerable<Trade>> GetAllTradesAsync()
        {
            return await _tradeRepository.GetAllTradesAsync();
        }

        // Get a trade by ID
        public async Task<Trade> GetTradeByIdAsync(int id)
        {
            return await _tradeRepository.GetTradeByIdAsync(id);
        }

        // Create a new trade
        public async Task CreateTradeAsync(Trade trade)
        {
            if (trade == null)
                throw new ArgumentNullException(nameof(trade), "Trade cannot be null.");

            await _tradeRepository.CreateTradeAsync(trade);
        }

        // Update an existing trade
        public async Task UpdateTradeAsync(Trade trade)
        {
            if (trade == null)
                throw new ArgumentNullException(nameof(trade), "Trade cannot be null.");

            await _tradeRepository.UpdateTradeAsync(trade);
        }

        // Delete a trade by ID
        public async Task DeleteTradeAsync(int id)
        {
            var trade = await _tradeRepository.GetTradeByIdAsync(id);
            if (trade == null)
                throw new ArgumentException($"Trade with id {id} not found.");

            await _tradeRepository.DeleteTradeAsync(id);
        }
    }
}