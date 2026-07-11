namespace TradingHub.Brokers;

public sealed class BrokerInstrumentMap
{
    private readonly IReadOnlyDictionary<string, string> _symbols;

    public BrokerInstrumentMap(IReadOnlyDictionary<string, string> symbols)
    {
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
    }

    public string GetBrokerSymbol(string instrumentId)
    {
        if (_symbols.TryGetValue(instrumentId, out var symbol))
        {
            return symbol;
        }

        throw new KeyNotFoundException($"No broker symbol is configured for '{instrumentId}'.");
    }
}
