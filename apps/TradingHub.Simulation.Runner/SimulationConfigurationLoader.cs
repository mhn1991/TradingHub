using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Simulation.Runner.Configuration;

namespace TradingHub.Simulation.Runner;

internal sealed class SimulationConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public async Task<LoadedSimulationConfiguration> LoadAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(configurationPath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var configuration = await JsonSerializer.DeserializeAsync<SimulationConfigurationDto>(
            stream,
            JsonOptions,
            cancellationToken);

        if (configuration is null)
        {
            throw new InvalidOperationException("The simulation configuration is empty.");
        }

        configuration.EnsureValid();
        return new LoadedSimulationConfiguration
        {
            Value = configuration,
            BaseDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory()
        };
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
