namespace TradingHub.Simulation.MarketData;

public sealed record CsvHistoricalBarOptions
{
    public required string FilePath { get; init; }

    public required string InstrumentId { get; init; }

    public required TimeSpan Period { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrWhiteSpace(InstrumentId))
        {
            throw new InvalidOperationException("A data file and instrument ID are required.");
        }

        if (Period <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The historical bar period must be positive.");
        }
    }
}
