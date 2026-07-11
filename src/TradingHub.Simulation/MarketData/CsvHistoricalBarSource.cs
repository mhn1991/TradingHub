using System.Runtime.CompilerServices;
using TradingHub.Abstractions.MarketData;
using TradingHub.Domain.Markets;

namespace TradingHub.Simulation.MarketData;

public sealed class CsvHistoricalBarSource : IMarketDataSource
{
    private const string ExpectedHeader =
        "open_time,bid_open,bid_high,bid_low,bid_close,ask_open,ask_high,ask_low,ask_close,volume";

    private readonly CsvHistoricalBarOptions _options;

    public CsvHistoricalBarSource(CsvHistoricalBarOptions options)
    {
        options.EnsureValid();
        _options = options;
    }

    public async IAsyncEnumerable<PriceBar> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            _options.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        var header = await reader.ReadLineAsync(cancellationToken);
        EnsureHeader(header);

        DateTimeOffset? previousOpenTime = null;
        var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = CsvBarDto.Parse(line, lineNumber);
            EnsureChronological(dto.OpenTime, previousOpenTime, lineNumber);
            previousOpenTime = dto.OpenTime;
            yield return CsvBarMapper.ToCanonical(dto, _options);
        }
    }

    private static void EnsureHeader(string? header)
    {
        if (!string.Equals(header?.Trim(), ExpectedHeader, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Historical CSV header must be: {ExpectedHeader}");
        }
    }

    private static void EnsureChronological(
        DateTimeOffset openTime,
        DateTimeOffset? previousOpenTime,
        int lineNumber)
    {
        if (previousOpenTime is not null && openTime <= previousOpenTime)
        {
            throw new FormatException($"Line {lineNumber} is duplicated or out of chronological order.");
        }
    }
}
