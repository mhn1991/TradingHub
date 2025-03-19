using DBManager.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DBManager.Repositories
{
    public class TradeRepository : ITradeRepository
    {
        private readonly ApplicationDbContext _context;

        public TradeRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Trade>> GetAllTradesAsync()
        {
            return await _context.Trades.ToListAsync();
        }

        public async Task<Trade> GetTradeByIdAsync(int id)
        {
            return await _context.Trades.FirstOrDefaultAsync(t => t.Id == id);
        }

        public async Task CreateTradeAsync(Trade trade)
        {
            await _context.Trades.AddAsync(trade);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateTradeAsync(Trade trade)
        {
            _context.Trades.Update(trade);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteTradeAsync(int id)
        {
            var trade = await _context.Trades.FindAsync(id);
            if (trade != null)
            {
                _context.Trades.Remove(trade);
                await _context.SaveChangesAsync();
            }
        }
    }
}