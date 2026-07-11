using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TradingHub.Abstractions.Brokers;
using TradingHub.Brokers.Configuration;

namespace TradingHub.Brokers.Factory;

public sealed class BrokerFactory : IBrokerFactory
{
    private readonly IReadOnlyDictionary<string, BrokerDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, IBrokerProvider> _providers;
    private readonly IBrokerCredentialStore _credentialStore;
    private readonly ConcurrentDictionary<string, Lazy<Task<IBrokerGateway>>> _instances =
        new(StringComparer.OrdinalIgnoreCase);

    public BrokerFactory(
        IOptions<BrokerCatalogOptions> options,
        IEnumerable<IBrokerProvider> providers,
        IBrokerCredentialStore credentialStore)
    {
        _definitions = BuildDefinitions(options.Value.Items);
        _providers = BuildProviders(providers);
        _credentialStore = credentialStore;
    }

    public IReadOnlyCollection<string> ConfiguredBrokerIds => _definitions.Keys.ToArray();

    public async Task<IBrokerGateway> CreateAsync(
        string brokerId,
        CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(brokerId, out var definition))
        {
            throw new KeyNotFoundException($"Broker instance '{brokerId}' is not configured.");
        }

        if (!_providers.TryGetValue(definition.Provider, out var provider))
        {
            throw new NotSupportedException($"Broker provider '{definition.Provider}' is not registered.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lazyInstance = _instances.GetOrAdd(
            brokerId,
            _ => new Lazy<Task<IBrokerGateway>>(
                () => CreateCoreAsync(definition, provider),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyInstance.Value.WaitAsync(cancellationToken);
        }
        catch when (lazyInstance.IsValueCreated && lazyInstance.Value.IsFaulted)
        {
            _instances.TryRemove(brokerId, out _);
            throw;
        }
    }

    private async Task<IBrokerGateway> CreateCoreAsync(
        BrokerDefinition definition,
        IBrokerProvider provider)
    {
        var credentials = await _credentialStore.GetAsync(definition.Account.CredentialKey);
        return provider.Create(definition, credentials);
    }

    private static IReadOnlyDictionary<string, BrokerDefinition> BuildDefinitions(
        IEnumerable<BrokerDefinition> definitions)
    {
        var result = new Dictionary<string, BrokerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            definition.EnsureValid();
            if (!result.TryAdd(definition.Id, definition))
            {
                throw new InvalidOperationException($"Broker ID '{definition.Id}' is duplicated.");
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IBrokerProvider> BuildProviders(
        IEnumerable<IBrokerProvider> providers)
    {
        var result = new Dictionary<string, IBrokerProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!result.TryAdd(provider.ProviderName, provider))
            {
                throw new InvalidOperationException($"Broker provider '{provider.ProviderName}' is duplicated.");
            }
        }

        return result;
    }
}
