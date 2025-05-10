using DBManager.Models;
using DBManager.Repositories;
using System;
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

        // Get a broker by Name (string)
        public async Task<Broker?> GetBrokerByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Broker name cannot be null or empty.", nameof(name));

            return await _brokerRepository.GetBrokerByNameAsync(name);
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

        // Delete a broker by Name (string)
        public async Task DeleteBrokerAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Broker name cannot be null or empty.", nameof(name));

            var broker = await _brokerRepository.GetBrokerByNameAsync(name);
            if (broker == null)
                throw new ArgumentException($"Broker with name '{name}' not found.");

            await _brokerRepository.DeleteBrokerAsync(name);
        }
    }
}
