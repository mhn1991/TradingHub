using System.Security.Cryptography;
using System.Text;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies.StructuralConfluence;

internal static class StructuralIdentity
{
    public static string Create(
        string strategyVersion,
        InstrumentKey instrument,
        string playbookId,
        Guid? primaryPoolId,
        Guid? primaryZoneId,
        string catalystIdentity,
        DateTimeOffset catalystAt,
        PriceActionDirection direction)
    {
        string source = string.Join('|', strategyVersion, instrument.Value, playbookId,
            primaryPoolId?.ToString("N") ?? "-", primaryZoneId?.ToString("N") ?? "-",
            catalystIdentity, catalystAt.UtcTicks, direction);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    public static string Decision(string setupId, DateTimeOffset availableAt, long version)
    {
        string source = string.Join('|', setupId, availableAt.UtcTicks, version);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }
}
