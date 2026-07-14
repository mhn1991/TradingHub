using Brokers.Abstractions;
using Brokers.Models;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class StreamingCacheIntegrityTests
{
    [Test]
    public async Task FullValidation_RejectsCacheTruncatedAfterFirstRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tradinghub-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "candles.jsonl.gz");
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var request = new HistoricalCandleRequest(
            instrument,
            BarInterval.Minutes(1),
            start,
            start.AddMinutes(2),
            UseHistoricalBidAsk: false);

        try
        {
            await using (var writer = new StreamingCandleCacheWriter(
                             path,
                             request,
                             "test",
                             BrokerEnvironment.Demo))
            {
                await writer.AppendAsync(MarketCandle.FromMid(TestCandles.Create(
                    instrument, start, BarInterval.Minutes(1), 1m, 1.1m, 0.9m, 1.05m)));
                await writer.AppendAsync(MarketCandle.FromMid(TestCandles.Create(
                    instrument, start.AddMinutes(1), BarInterval.Minutes(1), 1.05m, 1.2m, 1m, 1.1m)));
                await writer.CommitAsync(new StreamingCacheCommitMetadata(
                    writer.CandleCount,
                    start,
                    start.AddMinutes(1),
                    writer.CurrentHashHex!,
                    DateTimeOffset.UtcNow));
            }

            Assert.That(await StreamingCandleCacheWriter.TryValidateManifestAsync(
                path, request, CancellationToken.None), Is.True);

            await using (FileStream file = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
                file.SetLength(Math.Max(1, file.Length / 2));

            Assert.That(await StreamingCandleCacheWriter.TryValidateManifestAsync(
                path, request, CancellationToken.None), Is.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
