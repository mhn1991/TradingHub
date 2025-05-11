using DBManager.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DBManager.Data;

namespace DBManager.Repositories
{
    public class BrokerRepository : IBrokerRepository
    {
        private readonly AppDbContext _context;

        public BrokerRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Broker>> GetAllBrokersAsync()
        {
            return await _context.Brokers.ToListAsync();
        }

        public async Task<Broker?> GetBrokerByNameAsync(string name)
        {
            return await _context.Brokers.FirstOrDefaultAsync(b => b.Name == name);
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

        public async Task DeleteBrokerAsync(string name)
        {
            var broker = await _context.Brokers.FindAsync(name);
            if (broker != null)
            {
                _context.Brokers.Remove(broker);
                await _context.SaveChangesAsync();
            }
        }
    }
}