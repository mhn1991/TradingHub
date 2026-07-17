using System.Security.Cryptography;
using System.Text;

namespace ChartAnnotator.Engine;

/// <summary>
/// Canonicalization/hashing for <see cref="ChartAnnotationOptions"/>, extracted from
/// <c>TradingCore.Pipeline.RuntimeFeaturePolicy.ComputeHash()</c> so both that type and
/// <c>TradingCore.Pipeline.AnalysisProfileKey</c> can produce a content-accurate identity for the
/// same options without duplicating the canonicalization logic.
/// </summary>
public static class ChartAnnotationOptionsHasher
{
    /// <summary>
    /// A canonical string representation of <paramref name="options"/>, safe for hashing or
    /// equality comparison. Record-generated <c>ToString()</c> prints list/dictionary-typed
    /// properties as their runtime type name, not their contents -
    /// <see cref="ChartAnnotationOptions.PriceActionSetups"/>'s <c>EnabledSetups</c> is
    /// canonicalized explicitly here (cleared before the outer <c>with</c>-rebuild so the rest of
    /// the record's <c>ToString()</c> is stable, then serialized separately, in full, ordered) so
    /// the result reflects the option's actual content rather than its backing collection type.
    /// This matters in practice: JSON deserialization always produces a concrete <c>List&lt;T&gt;</c>
    /// for an <c>IReadOnlyList&lt;T&gt;</c> property, while a collection-expression default
    /// (<c>= []</c>) is compiler-synthesized as a distinct array-backed type - same content,
    /// different <c>ToString()</c> type name, which would otherwise make a content-identical
    /// options record fail its own hash check after a JSON round trip.
    /// </summary>
    public static string Canonicalize(ChartAnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ChartAnnotationOptions canonicalAnnotation = options with
        {
            PriceActionSetups = options.PriceActionSetups with { EnabledSetups = [] }
        };
        return $"annotation:{canonicalAnnotation}|" +
            $"enabled-setups:[{string.Join(',', options.PriceActionSetups.EnabledSetups.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal))}]";
    }

    public static string ComputeHash(ChartAnnotationOptions options)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(options)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
