using Brokers.Models;

namespace Brokers.Abstractions;

/// <summary>
/// Optional broker capability used by risk-based position sizing. Implementations must
/// return the value of one quote-currency unit in the trading account currency.
/// </summary>
public interface IAccountCurrencyConversionProvider
{
    bool TryGetQuoteToAccountCurrencyRate(InstrumentKey instrument, out decimal rate);
}
