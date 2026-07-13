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
        bool hasHeader = header is not null &&
                         header.Contains("timestamp", StringComparison.OrdinalIgnoreCase);

        if (!hasHeader && header is not null)
        {
            MarketCandle? first = ParseLine(header, request);
            if (first is not null)
                yield return first;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            MarketCandle? candle = ParseLine(line, request);
            if (candle is not null)
                yield return candle;
        }
    }

    private MarketCandle? ParseLine(string line, HistoricalCandleRequest request)
    {
        string[] parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return null;

        if (!DateTimeOffset.TryParse(
                parts[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset openTime))
        {
            return null;
        }

        if (openTime < request.From || openTime >= request.To)
            return null;

        if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal open) ||
            !decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal high) ||
            !decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal low) ||
            !decimal.TryParse(parts[4], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal close))
        {
            return null;
        }

        decimal? volume = null;
        if (parts.Length > 5 &&
            decimal.TryParse(parts[5], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal vol))
        {
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
