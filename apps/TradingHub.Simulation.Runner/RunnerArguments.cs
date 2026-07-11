namespace TradingHub.Simulation.Runner;

internal static class RunnerArguments
{
    private const string DefaultConfigurationPath = "samples/simulation.json";

    public static string GetConfigurationPath(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return DefaultConfigurationPath;
        }

        if (args.Count == 2 && string.Equals(args[0], "--config", StringComparison.OrdinalIgnoreCase))
        {
            return args[1];
        }

        throw new ArgumentException("Usage: TradingHub.Simulation.Runner [--config <path>]");
    }
}
