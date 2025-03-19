using DBManager.Models;
using DBManager.Repositories;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DBManager.Services
{
    public class BrokerService
    {
        private readonly IBrokerRepository _brokerRepository;

        public BrokerService(IBrokerRepository brokerRepository)
        {
            _brokerRepository = brokerRepository;
        }

        // Get all brokers
        public async Task<IEnumerable<Broker>> GetAllBrokersAsync()
        {
            return await _brokerRepository.GetAllBrokersAsync();
        }

        // Get a broker by ID
        public async Task<Broker> GetBrokerByIdAsync(int id)
        {
            return await _brokerRepository.GetBrokerByIdAsync(id);
        }

        // Create a new broker
        public async Task CreateBrokerAsync(Broker broker)
        {
            if (broker == null)
                throw new ArgumentNullException(nameof(broker), "Broker cannot be null.");

            await _brokerRepository.CreateBrokerAsync(broker);
        }

        // Update an existing broker
        public async Task UpdateBrokerAsync(Broker broker)
        {
            if (broker == null)
                throw new ArgumentNullException(nameof(broker), "Broker cannot be null.");

            await _brokerRepository.UpdateBrokerAsync(broker);
        }

        // Delete a broker by ID
        public async Task DeleteBrokerAsync(int id)
        {
            var broker = await _brokerRepository.GetBrokerByIdAsync(id);
            if (broker == null)
                throw new ArgumentException($"Broker with id {id} not found.");

            await _brokerRepository.DeleteBrokerAsync(id);
        }
    }
}