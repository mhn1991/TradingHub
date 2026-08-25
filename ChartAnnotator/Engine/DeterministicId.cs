using System.Buffers;
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
    /// <summary>
    /// Hot path: called once per pool/zone/sweep/event/confluence identity, potentially many
    /// times per candle across a long backtest (profiling showed this as a measurable share of
    /// total CPU/allocations). The original implementation built the canonical string via LINQ
    /// <c>Select</c> + <c>string.Join</c> (an iterator plus one intermediate string per part),
    /// then allocated a UTF8 byte array and a 32-byte hash-output array per call. This version
    /// produces byte-identical output - same canonical string, same SHA-256 truncation - via a
    /// plain <see cref="StringBuilder"/> loop (no LINQ iterator) plus stack/pooled encode-and-hash
    /// buffers, removing the UTF8 byte-array and hash-output allocations. Must never change the
    /// canonical string format: replay reproducibility depends on identical IDs for identical
    /// inputs across runs (see class summary).
    /// </summary>
    public static Guid Create(string prefix, params object?[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(parts);

        // Exactly reproduces prefix + "|" + string.Join("|", parts.Select(Canonicalize)),
        // including the empty-parts case ("prefix|"), without the LINQ iterator/closure.
        var builder = new StringBuilder(prefix.Length + 1 + parts.Length * 24);
        builder.Append(prefix).Append('|');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) builder.Append('|');
            builder.Append(Canonicalize(parts[i]));
        }

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(builder.Length);
        byte[]? rentedBytes = maxByteCount > 512 ? ArrayPool<byte>.Shared.Rent(maxByteCount) : null;
        try
        {
            Span<byte> utf8 = rentedBytes ?? stackalloc byte[512];
            int written = 0;
            foreach (ReadOnlyMemory<char> chunk in builder.GetChunks())
                written += Encoding.UTF8.GetBytes(chunk.Span, utf8[written..]);

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(utf8[..written], hash);
            return new Guid(hash[..16]);
        }
        finally
        {
            if (rentedBytes is not null)
                ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    private static string Canonicalize(object? part) => part switch
    {
        null => "null",
        DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
        decimal value => value.ToString(CultureInfo.InvariantCulture),
        _ => part.ToString() ?? "null"
    };
}
