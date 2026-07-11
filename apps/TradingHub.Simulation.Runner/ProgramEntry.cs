namespace TradingHub.Simulation.Runner;

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configPath = RunnerArguments.GetConfigurationPath(args);
            var loader = new SimulationConfigurationLoader();
            var loadedConfiguration = await loader.LoadAsync(configPath);
            var runtime = SimulationCompositionRoot.Build(loadedConfiguration);
            var result = await runtime.ExecuteAsync();

            Console.WriteLine($"Simulation '{result.RunId}' completed.");
            Console.WriteLine($"Final equity: {result.Summary.FinalEquity:F2}");
            Console.WriteLine($"Fills: {result.Summary.FillCount}");
            Console.WriteLine($"Results: {runtime.OutputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Simulation failed: {exception.Message}");
            return 1;
        }
    }
}
