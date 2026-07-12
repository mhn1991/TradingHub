using Brokers.Models;

namespace Brokers.Infrastructure;

internal static class InstrumentMappers
{
    public static InstrumentKey FromNative(
        string nativeInstrument,
        IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeInstrument);

        foreach ((string canonical, string native) in mappings)
        {
            if (string.Equals(native, nativeInstrument, StringComparison.OrdinalIgnoreCase))
            {
                return new InstrumentKey(canonical);
            }
        }

        return new InstrumentKey(nativeInstrument);
    }

    public static string ToOanda(
        InstrumentKey instrument,
        IReadOnlyDictionary<string, string>? mappings = null)
    {
        Validate(instrument);
        if (mappings?.TryGetValue(instrument.Value, out string? mapped) == true)
        {
            return mapped;
        }

        string value = RemoveAssetClassPrefix(instrument.Value);
        return value.Replace('/', '_').Replace('-', '_');
    }

    public static string ToBinance(
        InstrumentKey instrument,
        IReadOnlyDictionary<string, string>? mappings = null)
    {
        Validate(instrument);
        if (mappings?.TryGetValue(instrument.Value, out string? mapped) == true)
        {
            return mapped;
        }

        string value = RemoveAssetClassPrefix(instrument.Value);
        return value.Replace("/", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);
    }

    public static string ToIg(
        InstrumentKey instrument,
        IReadOnlyDictionary<string, string> mappings)
    {
        Validate(instrument);
        if (mappings.TryGetValue(instrument.Value, out string? epic))
        {
            return epic;
        }

        string raw = RemoveAssetClassPrefix(instrument.Value);
        if (raw.Contains('.'))
        {
            return raw;
        }

        throw new KeyNotFoundException(
            $"No IG EPIC mapping exists for '{instrument.Value}'. " +
            "Add it to IgOptions.InstrumentMappings.");
    }

    private static string RemoveAssetClassPrefix(string value)
    {
        int separator = value.IndexOf(':');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private static void Validate(InstrumentKey instrument)
    {
        if (instrument.IsEmpty)
        {
            throw new ArgumentException("An instrument must have a value.", nameof(instrument));
        }
    }
}
