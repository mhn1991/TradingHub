namespace TradingHub.Domain.Markets;

public readonly record struct Ohlc(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close)
{
    public void EnsureValid()
    {
        if (Open <= 0m || High <= 0m || Low <= 0m || Close <= 0m)
        {
            throw new InvalidOperationException("OHLC prices must be positive.");
        }

        if (High < Low || High < Open || High < Close || Low > Open || Low > Close)
        {
            throw new InvalidOperationException("OHLC prices are inconsistent.");
        }
    }
}
