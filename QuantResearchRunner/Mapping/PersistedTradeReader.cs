using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Models;

namespace QuantResearchRunner.Mapping;

/// <summary>
/// Reads the gzip-compressed <c>trades.json.gz</c> sidecar a completed simulation writes per
/// strategy (<c>ChunkedReplayWriter.WriteStrategySidecarsAsync</c>,
/// <c>{outputDirectory}/strategies/{strategyId}/trades.json.gz</c>) - the final full
/// <see cref="SimulatedTradeRecord"/> list for that strategy, used to re-analyze an
/// already-completed run (e.g. <c>monte-carlo</c>) without rerunning it.
/// </summary>
public static class PersistedTradeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<IReadOnlyList<SimulatedTradeRecord>> ReadAsync(
        string simulationOutputDirectory,
        string strategyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(simulationOutputDirectory))
            throw new ArgumentException("A simulation output directory is required.", nameof(simulationOutputDirectory));
        if (string.IsNullOrWhiteSpace(strategyId))
            throw new ArgumentException("A strategy id is required.", nameof(strategyId));

        string path = Path.Combine(simulationOutputDirectory, "strategies", strategyId, "trades.json.gz");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No persisted trades found for strategy '{strategyId}'.", path);

        await using FileStream file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        SimulatedTradeRecord[]? trades = await JsonSerializer
            .DeserializeAsync<SimulatedTradeRecord[]>(gzip, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return trades ?? [];
    }
}
