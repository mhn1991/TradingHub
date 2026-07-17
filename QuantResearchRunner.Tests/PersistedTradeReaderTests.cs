using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearchRunner.Mapping;
using Simulator.Models;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class PersistedTradeReaderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Test]
    public async Task ReadAsync_RoundTripsGzippedTradesJson()
    {
        string simulationDirectory = TempDirectory();
        string strategyDirectory = Path.Combine(simulationDirectory, "strategies", "improved");
        Directory.CreateDirectory(strategyDirectory);

        SimulatedTradeRecord[] trades =
        [
            new()
            {
                StrategyId = "improved",
                StrategyName = "Improved Progressive",
                SetupId = "setup-1",
                Instrument = new InstrumentKey("FX:EUR/USD"),
                Side = OrderSide.Buy,
                SetupStartedAt = DateTimeOffset.UnixEpoch,
                SignalCreatedAt = DateTimeOffset.UnixEpoch,
                EntryRegime = MarketRegime.TrendingUp,
                RMultiple = 1.2m,
                SetupReason = "fixture"
            }
        ];

        await using (FileStream file = File.Create(Path.Combine(strategyDirectory, "trades.json.gz")))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            await JsonSerializer.SerializeAsync(gzip, trades, JsonOptions);
        }

        IReadOnlyList<SimulatedTradeRecord> result = await PersistedTradeReader.ReadAsync(simulationDirectory, "improved");

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SetupId, Is.EqualTo("setup-1"));
            Assert.That(result[0].RMultiple, Is.EqualTo(1.2m));
            Assert.That(result[0].EntryRegime, Is.EqualTo(MarketRegime.TrendingUp));
        });
    }

    [Test]
    public void ReadAsync_MissingFile_ThrowsFileNotFoundException()
    {
        string simulationDirectory = TempDirectory();

        Assert.ThrowsAsync<FileNotFoundException>(() =>
            PersistedTradeReader.ReadAsync(simulationDirectory, "improved"));
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
