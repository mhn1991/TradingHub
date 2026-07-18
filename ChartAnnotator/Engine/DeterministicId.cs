using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChartAnnotator.Engine;

/// <summary>
/// Deterministic <see cref="Guid"/> derivation from normalized inputs, shared by every
/// price-derived supply/demand and price-inferred liquidity identity (zone, pool, sweep, event,
/// confluence IDs) so replaying the same candle stream under the same profile reproduces
/// byte-identical IDs (blueprint requirement: "Replay must produce identical zone, pool, sweep,
/// and confluence IDs"). Mirrors the canonicalization switch in
/// <c>ChartAnnotator.NeoWave.NeoWaveAnalyzer.StableId</c> (decimal/DateTimeOffset need explicit,
/// culture-invariant formatting - the default <c>ToString()</c> is culture-sensitive and would
/// break determinism across environments) but returns a <see cref="Guid"/> instead of a hex
/// string, matching the blueprint's typed ID fields. Not an RFC 4122 UUIDv5 (that specifies SHA-1
/// over a namespace+name); this truncates a SHA-256 digest to 16 bytes, which is sufficient for
/// determinism without claiming interoperability with another UUID generator.
/// </summary>
public static class DeterministicId
{
    public static Guid Create(string prefix, params object?[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(parts);
        string canonical = prefix + "|" + string.Join("|", parts.Select(Canonicalize));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Canonicalize(object? part) => part switch
    {
        null => "null",
        DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
        decimal value => value.ToString(CultureInfo.InvariantCulture),
        _ => part.ToString() ?? "null"
    };
}
