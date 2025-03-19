using DBManager.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DBManager.Repositories
{
    public class BrokerRepository : IBrokerRepository
    {
        private readonly ApplicationDbContext _context;

        public BrokerRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Broker>> GetAllBrokersAsync()
        {
            return await _context.Brokers.ToListAsync();
        }

        public async Task<Broker> GetBrokerByIdAsync(int id)
        {
            return await _context.Brokers.FirstOrDefaultAsync(b => b.Id == id);
        }

        public async Task CreateBrokerAsync(Broker broker)
        {
            await _context.Brokers.AddAsync(broker);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateBrokerAsync(Broker broker)
        {
            _context.Brokers.Update(broker);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteBrokerAsync(int id)
        {
            var broker = await _context.Brokers.FindAsync(id);
            if (broker != null)
            {
                _context.Brokers.Remove(broker);
                await _context.SaveChangesAsync();
            }
        }
    }
}