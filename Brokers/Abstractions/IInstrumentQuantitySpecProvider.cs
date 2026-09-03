using Brokers.Models;

namespace Brokers.Abstractions;

/// <summary>
/// Optional broker capability exposing cached per-instrument quantity granularity to position
/// sizing. Mirrors <see cref="IAccountCurrencyConversionProvider"/>: implement it only when the
/// metadata is already cached, because sizing consults it on the order path and must not make a
/// network call there.
/// </summary>
public interface IInstrumentQuantitySpecProvider
{
    bool TryGetInstrumentQuantitySpec(InstrumentKey instrument, out InstrumentTradingMetadata? metadata);
}
