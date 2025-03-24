using System.Threading.Tasks;
using DBManager.Models;
using DBManager.Services;

namespace TradeManager
{
    public class TradeManagerService
    {
        private readonly TradeService _tradeService;

        public TradeManagerService(TradeService tradeService)
        {
            _tradeService = tradeService;
        }

        public async Task CreateTradeAsync(Trade trade)
        {
            await _tradeService.CreateTradeAsync(trade);
        }

        public async Task UpdateTradeAsync(Trade trade)
        {
            await _tradeService.UpdateTradeAsync(trade);
        }

        public async Task DeleteTradeAsync(int tradeId)
        {
            await _tradeService.DeleteTradeAsync(tradeId);
        }

        public async Task<Trade> GetTradeByIdAsync(int tradeId)
        {
            return await _tradeService.GetTradeByIdAsync(tradeId);
        }
    }
}