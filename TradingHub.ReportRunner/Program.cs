using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Postgres;
using DBManager.Postgres.Reporting;
using DBManager.Postgres.Security;
using Microsoft.EntityFrameworkCore;

string[] commandLine = args.FirstOrDefault()?.Equals("report", StringComparison.OrdinalIgnoreCase) == true
    ? args[1..]
    : args;
if (commandLine.Length == 0 || commandLine[0] is "--help" or "-h")
{
    PrintUsage();
    return;
}

string reportKind = commandLine[0].ToLowerInvariant();
if (!Guid.TryParse(GetRequiredOption(commandLine, "--id"), out Guid id))
    throw new ArgumentException("--id must be a valid UUID.");
string format = (GetOption(commandLine, "--format") ?? "json").ToLowerInvariant();
DateTimeOffset? from = ParseDateTimeOffset(GetOption(commandLine, "--from"), "--from");
DateTimeOffset? to = ParseDateTimeOffset(GetOption(commandLine, "--to"), "--to");
if (from > to)
    throw new ArgumentException("--from must be earlier than or equal to --to.");

BrokerCredentialDatabaseConfiguration database =
    BrokerCredentialDatabaseConfiguration.TryLoad(AppContext.BaseDirectory, out _) ??
    throw new InvalidOperationException(
        "PostgreSQL configuration is required at .state/tradinghub.database.json.");
var service = new PostgresTradingReportQueryService(
    new StandaloneTradingHubContextFactory(database.ConnectionString));
object? report = reportKind switch
{
    "simulation" => await service.GetSimulationAsync(id),
    "experiment" => await service.GetExperimentAsync(id),
    "live-session" => await service.GetLiveSessionAsync(id),
    "deployment" => await service.GetDeploymentAsync(id, from, to),
    "candidate" => await service.GetCandidateAsync(id),
    "position" => await service.GetPositionAsync(id),
    "agent" => await service.GetAgentAsync(id, from, to),
    _ => throw new ArgumentException(
        "Report kind must be simulation, experiment, live-session, deployment, candidate, position, or agent.")
};
if (report is null)
{
    Console.Error.WriteLine($"No {reportKind} report exists for {id}.");
    Environment.ExitCode = 2;
    return;
}

JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
JsonElement root = JsonSerializer.SerializeToElement(report, report.GetType(), jsonOptions);
string output = format switch
{
    "json" => JsonSerializer.Serialize(report, report.GetType(), jsonOptions),
    "markdown" or "md" => ReportTextFormatter.ToMarkdown(reportKind, root),
    "csv" => ReportTextFormatter.ToCsv(root, GetRequiredOption(commandLine, "--section")),
    _ => throw new ArgumentException("--format must be json, markdown, or csv.")
};

string? outputPath = GetOption(commandLine, "--output");
if (string.IsNullOrWhiteSpace(outputPath))
    Console.WriteLine(output);
else
{
    await File.WriteAllTextAsync(Path.GetFullPath(outputPath), output);
    Console.Error.WriteLine($"Report written to {Path.GetFullPath(outputPath)}.");
}

static string? GetOption(string[] arguments, string name)
{
    int index = Array.FindIndex(arguments, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string GetRequiredOption(string[] arguments, string name) =>
    GetOption(arguments, name) ?? throw new ArgumentException($"{name} is required.");

static DateTimeOffset? ParseDateTimeOffset(string? value, string option)
{
    if (value is null)
        return null;
    return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed
        : throw new ArgumentException($"{option} must be an ISO-8601 timestamp.");
}

static void PrintUsage()
{
    Console.WriteLine("""
        TradingHub structured report exporter

          report simulation   --id <uuid> [--format json|markdown]
          report experiment   --id <uuid> [--format json|markdown]
          report live-session --id <uuid> [--format json|markdown]
          report deployment   --id <uuid> [--from <iso>] [--to <iso>]
          report candidate    --id <uuid> [--format json|markdown]
          report position     --id <uuid> [--format json|markdown]
          report agent        --id <uuid> [--from <iso>] [--to <iso>]

        Add --output <path> to write a file. CSV requires --section <tabular-property>;
        complete reports are intentionally not flattened into CSV.
        """);
}

static class ReportTextFormatter
{
    public static string ToMarkdown(string kind, JsonElement root)
    {
        StringBuilder output = new();
        output.Append("# TradingHub ").Append(Humanize(kind)).AppendLine(" Report").AppendLine();
        AppendObject(output, root, 2);
        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string ToCsv(JsonElement root, string sectionName)
    {
        JsonProperty section = default;
        bool found = root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(property =>
        {
            if (!property.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                return false;
            section = property;
            return true;
        });
        if (!found || section.Value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"CSV section '{sectionName}' is not a tabular report property.");
        }

        JsonElement[] rows = section.Value.EnumerateArray().ToArray();
        if (rows.Length == 0)
            return string.Empty;
        if (rows.Any(row => row.ValueKind != JsonValueKind.Object))
            throw new ArgumentException($"CSV section '{sectionName}' is not an object table.");
        string[] columns = rows.SelectMany(ScalarProperties).Select(property => property.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        StringBuilder output = new();
        output.AppendLine(string.Join(',', columns.Select(Csv)));
        foreach (JsonElement row in rows)
        {
            Dictionary<string, string> values = ScalarProperties(row)
                .ToDictionary(property => property.Name, property => Scalar(property.Value), StringComparer.OrdinalIgnoreCase);
            output.AppendLine(string.Join(',', columns.Select(column => Csv(values.GetValueOrDefault(column, string.Empty)))));
        }
        return output.ToString();
    }

    private static void AppendObject(StringBuilder output, JsonElement element, int level)
    {
        JsonProperty[] scalar = ScalarProperties(element).ToArray();
        if (scalar.Length > 0)
        {
            output.AppendLine("| Field | Value |").AppendLine("|---|---|");
            foreach (JsonProperty property in scalar)
                output.Append("| ").Append(Humanize(property.Name)).Append(" | ")
                    .Append(Markdown(Scalar(property.Value))).AppendLine(" |");
            output.AppendLine();
        }

        foreach (JsonProperty property in element.EnumerateObject().Where(value => !IsScalar(value.Value)))
        {
            output.Append('#', Math.Min(level, 6)).Append(' ').AppendLine(Humanize(property.Name)).AppendLine();
            if (property.Value.ValueKind == JsonValueKind.Object)
                AppendObject(output, property.Value, level + 1);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                AppendArray(output, property.Value, level + 1);
        }
    }

    private static void AppendArray(StringBuilder output, JsonElement array, int level)
    {
        JsonElement[] items = array.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            output.AppendLine("_None._").AppendLine();
            return;
        }
        if (items.All(item => IsScalar(item)))
        {
            foreach (JsonElement item in items)
                output.Append("- ").AppendLine(Markdown(Scalar(item)));
            output.AppendLine();
            return;
        }

        string[] columns = items.Where(item => item.ValueKind == JsonValueKind.Object)
            .SelectMany(ScalarProperties).Select(property => property.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (columns.Length > 0)
        {
            output.Append('|').Append(string.Join('|', columns.Select(column => $" {Humanize(column)} "))).AppendLine("|");
            output.Append('|').Append(string.Join('|', columns.Select(_ => "---"))).AppendLine("|");
            foreach (JsonElement item in items.Where(item => item.ValueKind == JsonValueKind.Object))
            {
                Dictionary<string, string> values = ScalarProperties(item)
                    .ToDictionary(property => property.Name, property => Scalar(property.Value), StringComparer.OrdinalIgnoreCase);
                output.Append('|').Append(string.Join('|', columns.Select(column =>
                    $" {Markdown(values.GetValueOrDefault(column, string.Empty))} "))).AppendLine("|");
            }
            output.AppendLine();
        }
        foreach (JsonElement item in items.Where(item => item.ValueKind == JsonValueKind.Object))
        {
            foreach (JsonProperty nested in item.EnumerateObject().Where(property => !IsScalar(property.Value)))
            {
                output.Append('#', Math.Min(level, 6)).Append(' ').AppendLine(Humanize(nested.Name)).AppendLine();
                if (nested.Value.ValueKind == JsonValueKind.Object)
                    AppendObject(output, nested.Value, level + 1);
                else
                    AppendArray(output, nested.Value, level + 1);
            }
        }
    }

    private static IEnumerable<JsonProperty> ScalarProperties(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => IsScalar(property.Value))
            : [];

    private static bool IsScalar(JsonElement value) => value.ValueKind is
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or
        JsonValueKind.Null or JsonValueKind.Undefined;

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.GetRawText()
    };

    private static string Humanize(string value)
    {
        StringBuilder result = new();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character is '-' or '_')
                result.Append(' ');
            else
            {
                if (index > 0 && char.IsUpper(character) &&
                    (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
                    result.Append(' ');
                result.Append(character);
            }
        }
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.ToString());
    }

    private static string Markdown(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

sealed class StandaloneTradingHubContextFactory(string connectionString)
    : IDbContextFactory<TradingHubDbContext>
{
    private readonly DbContextOptions<TradingHubDbContext> _options =
        new DbContextOptionsBuilder<TradingHubDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    public TradingHubDbContext CreateDbContext() => new(_options);

    public Task<TradingHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
