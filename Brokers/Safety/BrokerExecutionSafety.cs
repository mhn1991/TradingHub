using Brokers.Models;

namespace Brokers.Safety;

public sealed record BrokerExecutionSafetyOptions
{
    public bool BlockWhenAccountCannotTrade { get; init; } = true;
    public bool BlockDuplicateOpenOrders { get; init; } = true;
}

public sealed record BrokerExecutionSafetyContext
{
    public required InstrumentKey Instrument { get; init; }
    public required IReadOnlyList<AccountSnapshot> Accounts { get; init; }
    public required IReadOnlyList<BrokerOrder> OpenOrders { get; init; }
}

public sealed record BrokerExecutionSafetyAssessment
{
    public required bool Approved { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }

    public string Summary => Approved ? "Approved" : string.Join(" ", Reasons);
}

/// <summary>
/// Validates broker-owned execution state before an order reaches a broker adapter.
/// It deliberately contains no strategy, portfolio, or account-risk policy.
/// </summary>
public interface IBrokerExecutionSafety
{
    BrokerExecutionSafetyAssessment Evaluate(BrokerExecutionSafetyContext context);
}

public sealed class BrokerExecutionSafety : IBrokerExecutionSafety
{
    private readonly BrokerExecutionSafetyOptions _options;

    public BrokerExecutionSafety(BrokerExecutionSafetyOptions? options = null)
    {
        _options = options ?? new BrokerExecutionSafetyOptions();
    }

    public BrokerExecutionSafetyAssessment Evaluate(BrokerExecutionSafetyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Accounts);
        ArgumentNullException.ThrowIfNull(context.OpenOrders);

        var reasons = new List<string>();
        if (_options.BlockWhenAccountCannotTrade && context.Accounts.Count == 0)
        {
            reasons.Add("The broker did not return an account to authorize trading.");
        }
        else if (_options.BlockWhenAccountCannotTrade &&
                 context.Accounts.All(account => account.CanTrade != true))
        {
            reasons.Add("No broker account explicitly reports that trading is enabled.");
        }

        if (_options.BlockDuplicateOpenOrders && context.OpenOrders.Any(order =>
                order.Instrument == context.Instrument &&
                order.NormalizedStatus is OrderStatus.Pending or
                    OrderStatus.Open or
                    OrderStatus.PartiallyFilled))
        {
            reasons.Add($"An open order already exists for {context.Instrument}.");
        }

        return new BrokerExecutionSafetyAssessment
        {
            Approved = reasons.Count == 0,
            Reasons = reasons
        };
    }
}
