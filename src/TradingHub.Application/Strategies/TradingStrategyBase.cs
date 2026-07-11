namespace TradingHub.Application.Strategies;

public abstract class TradingStrategyBase : ITradingStrategy
{
    private long _intentSequence;

    protected TradingStrategyBase(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A strategy ID is required.", nameof(id));
        }

        Id = id;
    }

    public string Id { get; }

    public abstract ValueTask<IReadOnlyList<Domain.Trading.TradeIntent>> OnBarAsync(
        StrategyContext context,
        Domain.Markets.PriceBar bar,
        CancellationToken cancellationToken = default);

    protected string NextIntentId(string instrumentId)
    {
        var sequence = Interlocked.Increment(ref _intentSequence);
        return $"{Id}-{instrumentId}-{sequence:D12}";
    }
}
