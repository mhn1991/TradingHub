using System.Globalization;
using System.Text.RegularExpressions;
using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Models;

namespace BacktestRunner;

internal sealed record BacktestCommandOptions
{
    public InstrumentKey Instrument { get; init; } = new("FX:GBP/JPY");
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public BarInterval ExecutionInterval { get; init; } = BarInterval.Minutes(1);
    public IReadOnlyList<BarInterval> AnalysisIntervals { get; init; } =
        [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)];
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public string OutputDirectory { get; init; } = Path.Combine("Dashboard", "public", "data", "backtests");
    public string CacheDirectory { get; init; } = Path.Combine(".cache", "oanda");
    public string JobsDirectory { get; init; } = Path.Combine(".cache", "simulation-jobs");
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    public int PageSize { get; init; } = 5_000;
    public decimal StartingBalance { get; init; } = 100_000m;
    public string? BaseCurrency { get; init; }
    public decimal Quantity { get; init; } = 1_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public int WarmupDays { get; init; } = 45;
    public int DeterministicSeed { get; init; } = 12_345;
    public int ProgressIntervalMs { get; init; } = 500;
    public IReadOnlyList<string> Strategies { get; init; } = ["legacy", "improved"];
    public string StrategyExecution { get; init; } = "parallel";
    public string StrategyWorker { get; init; } = "task";
    public int StrategyChannelCapacity { get; init; } = 4;
    public int MaxParallelStrategies { get; init; } = 4;
    public string StrategyFailurePolicy { get; init; } = "stop-all";
    public string AnalysisSharing { get; init; } = "shared";
    public string AmbiguousPolicy { get; init; } = "stop-first";
    public bool ShowHelp { get; init; }

    public static BacktestCommandOptions Parse(string[] args)
    {
        DateTimeOffset defaultTo = new(DateTime.UtcNow.Date, TimeSpan.Zero);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (token is "-h" or "--help")
            {
                values["help"] = "true";
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{token}'. Use --help for usage.");
            }

            string key = token[2..];
            if (key is "refresh" or "no-cache" or "full-chart-history")
            {
                values[key] = "true";
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argument --{key} requires a value.");
            }

            values[key] = args[++index];
        }

        DateTimeOffset to = ParseDate(values.GetValueOrDefault("to"), defaultTo);
        DateTimeOffset from = ParseDate(values.GetValueOrDefault("from"), to.AddYears(-1));
        if (from >= to)
        {
            throw new ArgumentException("--from must be earlier than --to.");
        }

        string intervalRaw = values.GetValueOrDefault("base-interval")
            ?? values.GetValueOrDefault("execution-interval")
            ?? "1m";
        BarInterval execution = ParseInterval(intervalRaw);
        BarInterval[] analysis = (values.GetValueOrDefault("analysis-intervals") ?? "5m,15m,1h")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseInterval)
            .Distinct()
            .ToArray();
        if (analysis.Length == 0)
        {
            throw new ArgumentException("At least one analysis interval is required.");
        }

        BarInterval[] required =
        [
            BarInterval.Minutes(5),
            BarInterval.Minutes(15),
            BarInterval.Hours(1)
        ];
        if (required.Any(interval => !analysis.Contains(interval)))
        {
            throw new ArgumentException(
                "The current progressive agents require 5m, 15m, and 1h analysis intervals.");
        }

        string[] strategies = (values.GetValueOrDefault("strategies")
                ?? values.GetValueOrDefault("strategy")
                ?? "legacy,improved")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new BacktestCommandOptions
        {
            ShowHelp = values.ContainsKey("help"),
            Instrument = new InstrumentKey(values.GetValueOrDefault("instrument") ?? "FX:GBP/JPY"),
            From = from,
            To = to,
            ExecutionInterval = execution,
            AnalysisIntervals = analysis,
            Environment = ParseEnvironment(values.GetValueOrDefault("environment")),
            OutputDirectory = ResolvePath(
                values.GetValueOrDefault("output") ?? Path.Combine("Dashboard", "public", "data", "backtests")),
            CacheDirectory = ResolvePath(
                values.GetValueOrDefault("cache") ?? Path.Combine(".cache", "oanda")),
            JobsDirectory = ResolvePath(
                values.GetValueOrDefault("jobs") ?? Path.Combine(".cache", "simulation-jobs")),
            RefreshCache = values.ContainsKey("refresh"),
            NoCache = values.ContainsKey("no-cache"),
            PageSize = ParseInt(values.GetValueOrDefault("page-size"), 5_000, 1, 5_000, "page-size"),
            StartingBalance = ParseDecimal(values.GetValueOrDefault("starting-balance"), 100_000m, 0m, "starting-balance"),
            BaseCurrency = values.GetValueOrDefault("base-currency")?.Trim().ToUpperInvariant(),
            Quantity = ParseDecimal(values.GetValueOrDefault("quantity"), 1_000m, 0m, "quantity"),
            Leverage = ParseDecimal(values.GetValueOrDefault("leverage"), 20m, 0m, "leverage"),
            CommissionRate = ParseDecimal(values.GetValueOrDefault("commission-rate"), 0.00002m, -1m, "commission-rate", allowZero: true),
            SpreadBasisPoints = ParseDecimal(values.GetValueOrDefault("spread-bps"), 1m, -1m, "spread-bps", allowZero: true),
            SlippageBasisPoints = ParseDecimal(values.GetValueOrDefault("slippage-bps"), 0.5m, -1m, "slippage-bps", allowZero: true),
            MinimumRewardRisk = ParseDecimal(values.GetValueOrDefault("minimum-rr"), 1.5m, 0m, "minimum-rr"),
            WarmupDays = ParseInt(values.GetValueOrDefault("warmup-days"), 45, 0, 3650, "warmup-days"),
            DeterministicSeed = ParseInt(values.GetValueOrDefault("seed"), 12_345, 0, int.MaxValue, "seed"),
            ProgressIntervalMs = ParseInt(values.GetValueOrDefault("progress-interval"), 500, 50, 60_000, "progress-interval"),
            Strategies = strategies,
            StrategyExecution = values.GetValueOrDefault("strategy-execution") ?? "parallel",
            StrategyWorker = values.GetValueOrDefault("strategy-worker") ?? "task",
            StrategyChannelCapacity = ParseInt(values.GetValueOrDefault("strategy-channel-capacity"), 4, 1, 64, "strategy-channel-capacity"),
            MaxParallelStrategies = ParseInt(values.GetValueOrDefault("max-parallel-strategies"), 4, 1, 32, "max-parallel-strategies"),
            StrategyFailurePolicy = values.GetValueOrDefault("strategy-failure-policy") ?? "stop-all",
            AnalysisSharing = values.GetValueOrDefault("analysis-sharing") ?? "shared",
            AmbiguousPolicy = values.GetValueOrDefault("ambiguous-policy") ?? "stop-first"
        };
    }

    public BacktestRequest ToBacktestRequest() => new()
    {
        Instrument = Instrument,
        From = From,
        To = To,
        Environment = Environment,
        Strategies = Strategies,
        StartingBalance = StartingBalance,
        BaseCurrency = BaseCurrency ?? ResolveBaseCurrency(),
        Quantity = Quantity,
        Leverage = Leverage,
        CommissionRate = CommissionRate,
        SpreadBasisPoints = SpreadBasisPoints,
        SlippageBasisPoints = SlippageBasisPoints,
        MinimumRewardRisk = MinimumRewardRisk,
        OutputDirectory = Path.Combine(OutputDirectory, "simulations"),
        CacheDirectory = CacheDirectory,
        JobsDirectory = JobsDirectory,
        Runtime = new BacktestRuntimeOptions
        {
            BaseInterval = ExecutionInterval,
            AnalysisIntervals = AnalysisIntervals,
            SourcePageSize = PageSize,
            PrefetchCapacity = Math.Max(PageSize * 4, 20_000),
            PrefetchLowWatermark = Math.Max(PageSize, 5_000),
            WarmupDays = WarmupDays,
            StrategyExecutionMode = ParseExecutionMode(StrategyExecution),
            StrategyWorkerMode = ParseWorkerMode(StrategyWorker),
            StrategyChannelCapacity = StrategyChannelCapacity,
            MaximumParallelStrategies = MaxParallelStrategies,
            StrategyFailurePolicy = ParseFailurePolicy(StrategyFailurePolicy),
            AnalysisSharingMode = ParseAnalysisSharing(AnalysisSharing),
            AmbiguousIntrabarPolicy = ParseAmbiguousPolicy(AmbiguousPolicy),
            RefreshCache = RefreshCache,
            NoCache = NoCache,
            DeterministicSeed = DeterministicSeed,
            ProgressPublishIntervalMilliseconds = ProgressIntervalMs
        }
    };

    public static BarInterval ParseInterval(string value)
    {
        Match match = Regex.Match(value.Trim(), "^(?<number>[1-9][0-9]*)(?<unit>s|m|h|d|w|mo)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new ArgumentException($"Unsupported interval '{value}'. Examples: 1m, 5m, 15m, 1h.");
        }

        int number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "s" => BarInterval.Seconds(number),
            "m" => BarInterval.Minutes(number),
            "h" => BarInterval.Hours(number),
            "d" => BarInterval.Days(number),
            "w" => BarInterval.Weeks(number),
            "mo" => BarInterval.Months(number),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    public static string FormatInterval(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => $"{interval.Value}s",
        BarUnit.Minute => $"{interval.Value}m",
        BarUnit.Hour => $"{interval.Value}h",
        BarUnit.Day => $"{interval.Value}d",
        BarUnit.Week => $"{interval.Value}w",
        BarUnit.Month => $"{interval.Value}mo",
        _ => interval.ToString()
    };

    public string ResolveBaseCurrency()
    {
        if (!string.IsNullOrWhiteSpace(BaseCurrency))
        {
            return BaseCurrency;
        }

        string pair = Instrument.Value[(Instrument.Value.IndexOf(':') + 1)..];
        int slash = pair.LastIndexOf('/');
        return slash >= 0 && slash + 1 < pair.Length ? pair[(slash + 1)..] : "USD";
    }

    private static Simulator.Models.StrategyExecutionMode ParseExecutionMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "sequential" or "seq" => Simulator.Models.StrategyExecutionMode.Sequential,
            "parallel" or "parallelworkers" or "workers" => Simulator.Models.StrategyExecutionMode.ParallelWorkers,
            _ => throw new ArgumentException($"Unknown --strategy-execution '{value}'. Use sequential or parallel.")
        };

    private static Simulator.Models.StrategyWorkerMode ParseWorkerMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "task" or "tasks" => Simulator.Models.StrategyWorkerMode.Task,
            "thread" or "dedicated" or "dedicatedthread" => Simulator.Models.StrategyWorkerMode.DedicatedThread,
            _ => throw new ArgumentException($"Unknown --strategy-worker '{value}'. Use task or thread.")
        };

    private static Simulator.Models.StrategyFailurePolicy ParseFailurePolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stop-all" or "stopentirecomparison" or "all" => Simulator.Models.StrategyFailurePolicy.StopEntireComparison,
            "stop-one" or "stopfailedstrategyonly" or "one" => Simulator.Models.StrategyFailurePolicy.StopFailedStrategyOnly,
            _ => throw new ArgumentException($"Unknown --strategy-failure-policy '{value}'.")
        };

    private static Simulator.Models.AnalysisSharingMode ParseAnalysisSharing(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "shared" or "sharedimmutablesnapshots" => Simulator.Models.AnalysisSharingMode.SharedImmutableSnapshots,
            "independent" or "independentperstrategy" => Simulator.Models.AnalysisSharingMode.IndependentPerStrategy,
            _ => throw new ArgumentException($"Unknown --analysis-sharing '{value}'.")
        };

    private static Simulator.Models.AmbiguousIntrabarPolicy ParseAmbiguousPolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stop-first" or "conservative" or "conservativestopfirst" => Simulator.Models.AmbiguousIntrabarPolicy.ConservativeStopFirst,
            "target-first" or "optimistic" or "optimistictargetfirst" => Simulator.Models.AmbiguousIntrabarPolicy.OptimisticTargetFirst,
            "nearest-open" or "nearest" or "nearesttoopenfirst" => Simulator.Models.AmbiguousIntrabarPolicy.NearestToOpenFirst,
            _ => throw new ArgumentException($"Unknown --ambiguous-policy '{value}'.")
        };

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string? root = FindSolutionRoot(Directory.GetCurrentDirectory()) ??
            FindSolutionRoot(AppContext.BaseDirectory);
        return Path.GetFullPath(Path.Combine(root ?? Directory.GetCurrentDirectory(), path));
    }

    private static string? FindSolutionRoot(string startingPath)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startingPath));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static BrokerEnvironment ParseEnvironment(string? value) =>
        string.Equals(value, "live", StringComparison.OrdinalIgnoreCase)
            ? BrokerEnvironment.Live
            : BrokerEnvironment.Demo;

    private static DateTimeOffset ParseDate(string? value, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd", "O"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw new ArgumentException($"Date '{value}' must use yyyy-MM-dd or ISO-8601 format.");
        }

        return parsed;
    }

    private static int ParseInt(string? value, int fallback, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} must be from {minimum} to {maximum}.");
        }
        return parsed;
    }

    private static decimal ParseDecimal(
        string? value,
        decimal fallback,
        decimal minimumExclusive,
        string name,
        bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ||
            (allowZero ? parsed < 0m : parsed <= minimumExclusive))
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} contains an invalid numeric value.");
        }
        return parsed;
    }
}
