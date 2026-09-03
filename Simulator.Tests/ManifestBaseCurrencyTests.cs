using System.Text.Json;
using Simulator.Replay;

namespace Simulator.Tests;

/// <summary>
/// Cover for the 2026-09-03 audit finding behind 3.43's corrected dollar figure. A run's account
/// currency is *derived* from the instrument's quote currency when `--base-currency` is absent, so
/// a six-instrument suite can produce one JPY-denominated run alongside five USD ones. The manifest
/// recorded no currency at all, which is why summing their profit and loss looked reasonable and
/// was not. Recording it makes the mismatch detectable in the output.
/// </summary>
[TestFixture]
public sealed class ManifestBaseCurrencyTests
{
    [Test]
    public async Task WrittenManifest_RecordsTheAccountCurrency()
    {
        string root = Path.Combine(Path.GetTempPath(), $"manifest-ccy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using (var writer = new ChunkedReplayWriter(root, 128, 0, 0, captureMarketReplay: false))
            {
                await writer.WriteManifestAsync(Manifest("JPY"), CancellationToken.None);
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "manifest.json")));

            Assert.That(
                document.RootElement.TryGetProperty("baseCurrency", out JsonElement currency),
                Is.True,
                "the manifest must record the currency the run was denominated in");
            Assert.That(currency.GetString(), Is.EqualTo("JPY"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SimulationManifest Manifest(string baseCurrency) => new()
    {
        SimulationId = Guid.NewGuid(),
        SchemaVersion = 2,
        Instrument = "FX:GBP/JPY",
        From = DateTimeOffset.UnixEpoch,
        To = DateTimeOffset.UnixEpoch.AddDays(1),
        WarmupFrom = null,
        BaseInterval = "1m",
        AnalysisIntervals = ["1m"],
        Strategies = ["alfonso"],
        InputStreamId = "test",
        CreatedAt = DateTimeOffset.UnixEpoch,
        BaseCurrency = baseCurrency
    };
}
