using System.Globalization;
using TradingHub.Brokers.Binance.Contracts;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Binance.Mapping;

internal static class BinanceAccountMapper
{
    public static BrokerAccountSnapshot ToCanonical(
        BinanceAccountDto source,
        string accountId,
        DateTimeOffset fallbackTime)
    {
        var observedAt = source.UpdateTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(source.UpdateTime)
            : fallbackTime;
        var balances = source.Balances
            .Where(item => Parse(item.Free) != 0m || Parse(item.Locked) != 0m)
            .Select(item => new CashBalance
            {
                Asset = item.Asset ?? "UNKNOWN",
                Available = Parse(item.Free),
                Reserved = Parse(item.Locked)
            })
            .ToArray();

        return new BrokerAccountSnapshot
        {
            AccountId = accountId,
            ObservedAt = observedAt,
            CashBalances = balances
        };
    }

    private static decimal Parse(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}
