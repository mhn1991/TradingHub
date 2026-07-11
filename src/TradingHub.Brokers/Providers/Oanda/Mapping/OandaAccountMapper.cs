using System.Globalization;
using TradingHub.Brokers.Oanda.Contracts;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Oanda.Mapping;

internal static class OandaAccountMapper
{
    public static BrokerAccountSnapshot ToCanonical(
        OandaAccountDto account,
        DateTimeOffset observedAt)
    {
        var balance = Parse(account.Balance);
        var currency = account.Currency ?? "UNKNOWN";
        return new BrokerAccountSnapshot
        {
            AccountId = account.Id ?? throw new InvalidOperationException("OANDA omitted the account ID."),
            ObservedAt = observedAt,
            Balance = balance,
            Equity = Parse(account.Nav),
            MarginAvailable = Parse(account.MarginAvailable),
            CashBalances =
            [
                new CashBalance { Asset = currency, Available = balance, Reserved = 0m }
            ]
        };
    }

    private static decimal Parse(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}
