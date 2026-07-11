using System.Globalization;

namespace TradingHub.Simulation.MarketData;

internal sealed record CsvBarDto
{
    public required DateTimeOffset OpenTime { get; init; }

    public required decimal BidOpen { get; init; }

    public required decimal BidHigh { get; init; }

    public required decimal BidLow { get; init; }

    public required decimal BidClose { get; init; }

    public required decimal AskOpen { get; init; }

    public required decimal AskHigh { get; init; }

    public required decimal AskLow { get; init; }

    public required decimal AskClose { get; init; }

    public decimal Volume { get; init; }

    public static CsvBarDto Parse(string line, int lineNumber)
    {
        var values = line.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 10)
        {
            throw new FormatException($"Line {lineNumber} must contain exactly 10 values.");
        }

        return new CsvBarDto
        {
            OpenTime = ParseTime(values[0], lineNumber),
            BidOpen = ParseDecimal(values[1], lineNumber),
            BidHigh = ParseDecimal(values[2], lineNumber),
            BidLow = ParseDecimal(values[3], lineNumber),
            BidClose = ParseDecimal(values[4], lineNumber),
            AskOpen = ParseDecimal(values[5], lineNumber),
            AskHigh = ParseDecimal(values[6], lineNumber),
            AskLow = ParseDecimal(values[7], lineNumber),
            AskClose = ParseDecimal(values[8], lineNumber),
            Volume = ParseDecimal(values[9], lineNumber)
        };
    }

    private static DateTimeOffset ParseTime(string value, int lineNumber)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return result.ToUniversalTime();
        }

        throw new FormatException($"Line {lineNumber} has an invalid UTC timestamp.");
    }

    private static decimal ParseDecimal(string value, int lineNumber)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"Line {lineNumber} contains an invalid decimal value.");
    }
}
