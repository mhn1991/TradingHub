using System.Net.Http.Json;
using System.Text.Json;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return;
}

Uri baseUri = new(GetOption(args, "--base-url") ?? "http://127.0.0.1:5088");
using var client = new HttpClient { BaseAddress = baseUri };
string? controlToken = GetOption(args, "--control-token");
if (!string.IsNullOrWhiteSpace(controlToken))
    client.DefaultRequestHeaders.Add("X-Live-Control-Token", controlToken);

string area = args[0].ToLowerInvariant();
string action = args.ElementAtOrDefault(1)?.ToLowerInvariant()
    ?? throw new ArgumentException("A command action is required.");

HttpResponseMessage response = (area, action) switch
{
    ("policy", "list") => await client.GetAsync(
        $"/api/live/policy-revisions?offset={GetIntOption(args, "--offset", 0)}&limit={GetIntOption(args, "--limit", 100)}"),
    ("deployment", "preflight") => await PostAsync(client, "/api/live/deployments/preflight",
        await BuildPreflightAsync(client, args)),
    ("deployment", "start") => await PostAsync(client, "/api/live/deployments", new
    {
        preflight = await BuildPreflightAsync(client, args),
        idempotencyKey = GetRequiredOption(args, "--idempotency-key"),
        actor = GetRequiredOption(args, "--actor"),
        reason = GetRequiredOption(args, "--reason")
    }, GetRequiredOption(args, "--idempotency-key")),
    ("deployment", "status") => await client.GetAsync(
        $"/api/live/deployments/{GetGuidOption(args, "--id")}"),
    ("deployment", "list") => await client.GetAsync(
        $"/api/live/deployments?offset={GetIntOption(args, "--offset", 0)}&limit={GetIntOption(args, "--limit", 100)}"),
    ("deployment", "pause") or ("deployment", "resume") or
    ("deployment", "reconcile") or ("deployment", "stop") =>
        await PostLifecycleAsync(client, $"/api/live/deployments/{GetGuidOption(args, "--id")}/{action}", args),
    ("deployment", "pause-agent") or ("deployment", "resume-agent") or
    ("deployment", "drain-agent") or ("deployment", "stop-agent") or
    ("deployment", "replace-agent") => await PostLifecycleAsync(client,
        $"/api/live/deployment-agents/{GetGuidOption(args, "--id")}/{action[..^6]}", args),
    _ => throw new ArgumentException($"Unknown command '{area} {action}'.")
};

string body = await response.Content.ReadAsStringAsync();
if (!string.IsNullOrWhiteSpace(body))
{
    try
    {
        using JsonDocument json = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (JsonException)
    {
        Console.WriteLine(body);
    }
}
if (!response.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Live API returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
    Environment.ExitCode = 2;
}

static async Task<HttpResponseMessage> PostLifecycleAsync(HttpClient client, string path, string[] arguments)
{
    string idempotencyKey = GetRequiredOption(arguments, "--idempotency-key");
    return await PostAsync(client, path, new
    {
        idempotencyKey,
        expectedVersion = GetLongOption(arguments, "--version"),
        actor = GetRequiredOption(arguments, "--actor"),
        reason = GetRequiredOption(arguments, "--reason"),
        replacementPolicyRevisionId = GetOptionalGuidOption(arguments, "--replacement-policy-revision"),
        expectedPackageHash = GetOption(arguments, "--package-hash")
    }, idempotencyKey);
}

static async Task<object> BuildPreflightAsync(HttpClient client, string[] arguments)
{
    Guid accountId = GetGuidOption(arguments, "--account");
    long instrumentId = await ResolveInstrumentIdAsync(client, accountId,
        GetRequiredOption(arguments, "--instrument"));
    return new
    {
        brokerAccountId = accountId,
        brokerEnvironment = GetRequiredOption(arguments, "--environment"),
        instrumentId,
        policyRevisionId = GetGuidOption(arguments, "--policy-revision"),
        mode = ParseMode(GetRequiredOption(arguments, "--mode")),
        expectedConfigurationHash = GetOption(arguments, "--configuration-hash"),
        expectedPackageHash = GetOption(arguments, "--package-hash"),
        requireParityCertification = !arguments.Contains("--allow-uncertified", StringComparer.OrdinalIgnoreCase)
    };
}

static async Task<long> ResolveInstrumentIdAsync(HttpClient client, Guid accountId, string value)
{
    if (long.TryParse(value, out long instrumentId))
        return instrumentId;
    using HttpResponseMessage response = await client.GetAsync(
        $"/api/live/broker-accounts/{accountId}/instruments");
    string body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"Instrument lookup failed with HTTP {(int)response.StatusCode}: {body}");
    using JsonDocument json = JsonDocument.Parse(body);
    foreach (JsonElement item in json.RootElement.EnumerateArray())
    {
        if (string.Equals(item.GetProperty("canonicalKey").GetString(), value,
                StringComparison.OrdinalIgnoreCase))
        {
            return item.GetProperty("instrumentId").GetInt64();
        }
    }
    throw new KeyNotFoundException($"Instrument '{value}' is not tradeable for account '{accountId}'.");
}

static async Task<HttpResponseMessage> PostAsync(
    HttpClient client, string path, object body, string? idempotencyKey = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = JsonContent.Create(body)
    };
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
        request.Headers.Add("Idempotency-Key", idempotencyKey);
    return await client.SendAsync(request);
}

static int ParseMode(string value) => value.ToLowerInvariant() switch
{
    "record" or "record-only" or "observe" => 0,
    "shadow" => 1,
    "manual" or "manual-approval" => 2,
    "automatic" => 3,
    _ => throw new ArgumentException("--mode must be record-only, shadow, manual-approval, or automatic.")
};

static string? GetOption(string[] arguments, string name)
{
    int index = Array.FindIndex(arguments, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string GetRequiredOption(string[] arguments, string name) =>
    GetOption(arguments, name) ?? throw new ArgumentException($"{name} is required.");

static Guid GetGuidOption(string[] arguments, string name) =>
    Guid.TryParse(GetRequiredOption(arguments, name), out Guid value)
        ? value
        : throw new ArgumentException($"{name} must be a UUID.");

static Guid? GetOptionalGuidOption(string[] arguments, string name)
{
    string? raw = GetOption(arguments, name);
    return raw is null ? null : Guid.TryParse(raw, out Guid value)
        ? value
        : throw new ArgumentException($"{name} must be a UUID.");
}

static int GetIntOption(string[] arguments, string name, int fallback) =>
    int.TryParse(GetOption(arguments, name), out int value) ? value : fallback;

static long GetLongOption(string[] arguments, string name) =>
    long.TryParse(GetRequiredOption(arguments, name), out long value)
        ? value
        : throw new ArgumentException($"{name} must be an integer.");

static void PrintUsage() => Console.WriteLine("""
    TradingHub live lifecycle administration (calls LiveTradingHost APIs)

      policy list [--offset 0] [--limit 100]
      deployment preflight --account <uuid> --environment <code> --policy-revision <uuid>
          --instrument <id|canonical-key> --mode <record-only|shadow|manual-approval|automatic>
      deployment start <preflight options> --actor <identity> --reason <text> --idempotency-key <key>
      deployment list | status --id <deployment-uuid>
      deployment pause|resume|reconcile|stop --id <deployment-uuid> --version <n>
          --actor <identity> --reason <text> --idempotency-key <key>
      deployment pause-agent|resume-agent|drain-agent|stop-agent --id <agent-uuid> --version <n>
          --actor <identity> --reason <text> --idempotency-key <key>
      deployment replace-agent <agent lifecycle options>
          --replacement-policy-revision <uuid> [--package-hash <sha256>]

    Common options: --base-url <url> (default http://127.0.0.1:5088)
                    --control-token <token> (required when configured by LiveTradingHost)
    """);
