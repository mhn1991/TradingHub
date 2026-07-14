using System.Globalization;
using System.Runtime.CompilerServices;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.MarketData;

/// <summary>
/// Streams genuine imported 1-second (or 5-second) mid candles from CSV.
/// Format: timestamp,open,high,low,close[,volume]
/// Does not invent rows from coarser OHLC.
/// </summary>
public sealed class ImportedSecondCandleSource : IHistoricalCandleStream
{
    private readonly string _path;
    private readonly InstrumentKey _instrument;
    private readonly BarInterval _interval;

    public ImportedSecondCandleSource(
        string path,
        InstrumentKey instrument,
        BarInterval interval)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Imported candle file not found.", path);
        if (instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.", nameof(instrument));
        if (interval != BarInterval.Seconds(1) && interval != BarInterval.Seconds(5))
            throw new ArgumentException("Imported second source supports 1s or 5s only.", nameof(interval));

        _path = Path.GetFullPath(path);
        _instrument = instrument;
        _interval = interval;
    }

    public async IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(_path);
        string? header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        int lineNumber = 1;
        bool hasHeader = header is not null &&
                         header.Contains("timestamp", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset? previous = null;

        if (!hasHeader && header is not null)
        {
            MarketCandle first = ParseLine(header, lineNumber);
            previous = first.OpenTime;
            if (IsInRange(first, request))
                yield return first;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            MarketCandle candle = ParseLine(line, lineNumber);
            if (previous is DateTimeOffset last && candle.OpenTime <= last)
            {
                string issue = candle.OpenTime == last ? "duplicate" : "out-of-order";
                throw new InvalidDataException(
                    $"Imported candle line {lineNumber} is {issue}: {candle.OpenTime:O} follows {last:O}.");
            }

            previous = candle.OpenTime;
            if (IsInRange(candle, request))
                yield return candle;
        }
    }

    public static async Task<long> ValidateFileAsync(
        string path,
        BarInterval interval,
        CancellationToken cancellationToken = default)
    {
        var source = new ImportedSecondCandleSource(
            path,
            new InstrumentKey("IMPORT:VALIDATION"),
            interval);
        var request = new HistoricalCandleRequest(
            new InstrumentKey("IMPORT:VALIDATION"),
            interval,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            UseHistoricalBidAsk: false,
            NoCache: true);
        long count = 0;
        await foreach (MarketCandle _ in source.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            count++;
        if (count == 0)
            throw new InvalidDataException("The imported candle file contains no data rows.");
        return count;
    }

    private static bool IsInRange(MarketCandle candle, HistoricalCandleRequest request) =>
        candle.OpenTime >= request.From && candle.OpenTime < request.To;

    private MarketCandle ParseLine(string line, int lineNumber)
    {
        string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (5 or 6) || parts.Take(5).Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"Imported candle line {lineNumber} must be timestamp,open,high,low,close[,volume].");
        }

        if (!DateTimeOffset.TryParse(
                parts[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset openTime))
        {
            throw new InvalidDataException(
                $"Imported candle line {lineNumber} has an invalid timestamp '{parts[0]}'.");
        }

        long intervalTicks = TimeSpan.FromSeconds(_interval.Value).Ticks;
        if (openTime.UtcDateTime.Ticks % intervalTicks != 0)
        {
            throw new InvalidDataException(
                $"Imported candle line {lineNumber} timestamp {openTime:O} is not aligned to {_interval}.");
        }

        if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal open) ||
            !decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal high) ||
            !decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal low) ||
            !decimal.TryParse(parts[4], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal close))
        {
            throw new InvalidDataException(
                $"Imported candle line {lineNumber} contains an invalid OHLC value.");
        }

        if (high < Math.Max(open, close) || low > Math.Min(open, close) || high < low)
        {
            throw new InvalidDataException(
                $"Imported candle line {lineNumber} violates OHLC invariants.");
        }

        decimal? volume = null;
        if (parts.Length == 6)
        {
            if (!decimal.TryParse(parts[5], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal vol) ||
                vol < 0m)
            {
                throw new InvalidDataException(
                    $"Imported candle line {lineNumber} contains an invalid volume.");
            }
            volume = vol;
        }

        return MarketCandle.FromMid(new Candle
        {
            Instrument = _instrument,
            Interval = _interval,
            OpenTime = openTime,
            CloseTime = _interval.AddTo(openTime),
            Prices = new Ohlc(open, high, low, close),
            Volume = volume is null ? null : new MarketVolume(volume.Value, VolumeKind.Unknown),
            IsComplete = true
        });
    }
}
