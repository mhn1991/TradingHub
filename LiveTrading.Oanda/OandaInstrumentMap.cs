using Brokers.Models;

namespace LiveTrading.Oanda;

/// <summary>
/// Reverse-mirrors <c>Brokers.Infrastructure.InstrumentMappers</c>'s <c>ToOanda</c>/
/// <c>FromNative</c> fallback rules exactly (that type is <c>internal</c> to the <c>Brokers</c>
/// assembly, so it cannot be called directly - see <c>Brokers/Infrastructure/InstrumentMappers.cs</c>).
/// Seeded from the same <c>OandaOptions.InstrumentMappings</c> dictionary passed to
/// <c>BrokerClientFactory.CreateOanda</c>. Any change to the real mapper's fallback algorithm must
/// be mirrored here - <c>LiveTrading.Tests/OandaInstrumentMapTests.cs</c> guards against drift.
/// </summary>
public sealed class OandaInstrumentMap
{
    private readonly IReadOnlyDictionary<string, string> _canonicalToNative;

    public OandaInstrumentMap(IReadOnlyDictionary<string, string> canonicalToNative)
    {
        _canonicalToNative = canonicalToNative ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string ToNative(InstrumentKey instrument)
    {
        if (instrument.IsEmpty)
        {
            throw new ArgumentException("An instrument must have a value.", nameof(instrument));
        }

        if (_canonicalToNative.TryGetValue(instrument.Value, out string? mapped))
        {
            return mapped;
        }

        string value = RemoveAssetClassPrefix(instrument.Value);
        return value.Replace('/', '_').Replace('-', '_');
    }

    public InstrumentKey FromNative(string nativeInstrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeInstrument);

        foreach ((string canonical, string native) in _canonicalToNative)
        {
            if (string.Equals(native, nativeInstrument, StringComparison.OrdinalIgnoreCase))
            {
                return new InstrumentKey(canonical);
            }
        }

        return new InstrumentKey(nativeInstrument);
    }

    private static string RemoveAssetClassPrefix(string value)
    {
        int separator = value.IndexOf(':');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }
}
