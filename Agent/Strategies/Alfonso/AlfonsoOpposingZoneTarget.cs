using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

internal static class AlfonsoOpposingZoneTarget
{
    // A reaction level need not qualify as a fresh entry. Include tested/used-up levels,
    // but never eliminated levels, another timeframe, or an unfinished confirmation bar.
    internal static Imbalance? Find(
        bool buy, decimal entry, TimeSpan interval, DateTimeOffset decisionAt,
        IEnumerable<Imbalance> zones) => zones
        .Where(zone => zone.Interval == interval &&
            zone.Kind == (buy ? ImbalanceKind.Supply : ImbalanceKind.Demand) &&
            zone.State != ImbalanceState.Eliminated && zone.EliminatedAt is null &&
            zone.ConfirmedAt + zone.Interval <= decisionAt &&
            (buy ? zone.Proximal > entry : zone.Proximal < entry))
        .OrderBy(zone => Math.Abs(zone.Proximal - entry))
        .ThenByDescending(zone => zone.ConfirmedAt)
        .FirstOrDefault();

    internal static string Describe(Imbalance zone) => FormattableString.Invariant(
        $"opposing {zone.Kind} {zone.Interval.TotalMinutes:0.##}m: proximal {zone.Proximal}, distal {zone.Distal}; base {zone.BaseStart:O} to {zone.BaseEnd:O}; confirmed close {zone.ConfirmedAt + zone.Interval:O}; state {zone.State}");
}
