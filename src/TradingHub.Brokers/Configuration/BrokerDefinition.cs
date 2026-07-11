using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Configuration;

public sealed class BrokerDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public TradingEnvironment Environment { get; set; }

    public BrokerAccountDefinition Account { get; set; } = new();

    public List<BrokerInstrumentDefinition> Instruments { get; set; } = [];

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Provider))
        {
            throw new InvalidOperationException("A broker ID and provider are required.");
        }

        if (Environment is not TradingEnvironment.Demo and not TradingEnvironment.Live)
        {
            throw new InvalidOperationException("Configured external brokers must use Demo or Live environment.");
        }

        Account.EnsureValid();
        if (Instruments.Count == 0)
        {
            throw new InvalidOperationException($"Broker '{Id}' needs at least one valid instrument mapping.");
        }

        var instrumentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instrument in Instruments)
        {
            instrument.EnsureValid();
            if (!instrumentIds.Add(instrument.InstrumentId))
            {
                throw new InvalidOperationException(
                    $"Instrument '{instrument.InstrumentId}' is duplicated for broker '{Id}'.");
            }
        }
    }

    public IReadOnlyDictionary<string, string> CreateInstrumentMap()
    {
        return Instruments.ToDictionary(
            instrument => instrument.InstrumentId,
            instrument => instrument.BrokerSymbol,
            StringComparer.Ordinal);
    }
}

public sealed class BrokerInstrumentDefinition
{
    public string InstrumentId { get; set; } = string.Empty;

    public string BrokerSymbol { get; set; } = string.Empty;

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(InstrumentId) || string.IsNullOrWhiteSpace(BrokerSymbol))
        {
            throw new InvalidOperationException("Instrument ID and broker symbol are required.");
        }
    }
}

public sealed class BrokerAccountDefinition
{
    public string AccountId { get; set; } = string.Empty;

    public string CredentialKey { get; set; } = string.Empty;

    public string? LiveAccountConfirmation { get; set; }

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(AccountId) || string.IsNullOrWhiteSpace(CredentialKey))
        {
            throw new InvalidOperationException("A broker account ID and credential key are required.");
        }
    }
}
