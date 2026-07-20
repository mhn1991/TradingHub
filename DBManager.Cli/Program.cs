using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres;
using DBManager.Postgres.Config;
using DBManager.Postgres.Operations;
using DBManager.Postgres.Operations.Backup;
using DBManager.Postgres.Operations.Retention;
using DBManager.Postgres.Reference;
using DBManager.Postgres.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Simulator.Jobs;
using Simulator.Models;
using TradingHub.Persistence.Postgres.Simulation;

string command = args.FirstOrDefault() ?? "bootstrap";
string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
string configPath = GetOption(args, "--config") ?? Path.Combine(repositoryRoot, ".state", "tradinghub.database.json");
string envPath = GetOption(args, "--env-file") ?? Path.Combine(repositoryRoot, ".env.integration");

if (!File.Exists(configPath))
    throw new FileNotFoundException("Database configuration was not found. Copy the example into .state first.", configPath);

DatabaseConfiguration configuration = JsonSerializer.Deserialize<DatabaseConfiguration>(
    await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Database configuration is empty.");
configuration.Validate();
string keyFile = Path.IsPathRooted(configuration.BrokerCredentialKeyFile)
    ? configuration.BrokerCredentialKeyFile
    : Path.GetFullPath(Path.Combine(repositoryRoot, configuration.BrokerCredentialKeyFile));

if (command == "health")
{
    await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(configuration.ConnectionString);
    PersistenceHealthReport report = await new PostgresPersistenceHealthCheck(dataSource, TimeProvider.System)
        .CheckAsync(CancellationToken.None);
    WriteJson(report);
    Environment.ExitCode = report.Status == PersistenceHealthStatus.Healthy ? 0 : 2;
    return;
}

if (command == "schema-version")
{
    IReadOnlyList<SchemaVersionInfo> versions = await new PostgresSchemaVersionReader(
        new StandaloneContextFactory(configuration.ConnectionString)).GetAppliedMigrationsAsync(CancellationToken.None);
    WriteJson(new { count = versions.Count, current = versions.LastOrDefault(), applied = versions });
    return;
}

if (command == "retention")
{
    var retention = new PostgresRetentionService(new StandaloneContextFactory(configuration.ConnectionString));
    RetentionResult result = await retention.RunAsync(new RetentionRequest(
        DryRun: !args.Contains("--apply", StringComparer.OrdinalIgnoreCase),
        DetailedTelemetryDays: GetIntOption(args, "--detail-days", 90),
        ActivityAggregateDays: GetIntOption(args, "--aggregate-days", 365)));
    WriteJson(result);
    return;
}

if (command == "backup")
{
    var factory = new StandaloneContextFactory(configuration.ConnectionString);
    var runner = new BackupRunner(
        new BackupStore(factory),
        new PostgresSchemaVersionReader(factory),
        NullLogger<BackupRunner>.Instance);
    string outputDirectory = Path.GetFullPath(GetOption(args, "--output-directory") ??
                                              Path.Combine(repositoryRoot, ".state", "backups"));
    BackupResult result = await runner.RunBackupAsync(
        configuration.ConnectionString,
        outputDirectory,
        TimeSpan.FromDays(GetIntOption(args, "--retention-days", 30)),
        CancellationToken.None);
    WriteJson(result);
    Environment.ExitCode = result.Succeeded ? 0 : 2;
    return;
}

if (command == "restore-test")
{
    var factory = new StandaloneContextFactory(configuration.ConnectionString);
    await using TradingHubDbContext db = await factory.CreateDbContextAsync();
    Guid? requestedId = Guid.TryParse(GetOption(args, "--id"), out Guid parsedId) ? parsedId : null;
    BackupRecordEntity? backup = await db.BackupRecords.AsNoTracking()
        .Where(row => row.CompletedAt != null && (requestedId == null || row.BackupId == requestedId))
        .OrderByDescending(row => row.CompletedAt)
        .FirstOrDefaultAsync();
    if (backup is null)
        throw new InvalidOperationException("No completed backup matched the requested ID.");
    string filePath = Path.GetFullPath(GetOption(args, "--file") ?? backup.FilePath);
    if (!File.Exists(filePath))
        throw new FileNotFoundException("Backup dump was not found.", filePath);
    var runner = new BackupRunner(
        new BackupStore(factory),
        new PostgresSchemaVersionReader(factory),
        NullLogger<BackupRunner>.Instance);
    RestoreVerificationResult result = await runner.VerifyRestoreAsync(
        backup.BackupId, filePath, configuration.ConnectionString, CancellationToken.None);
    WriteJson(new { backup.BackupId, filePath, result.Succeeded, result.FailureDetail });
    Environment.ExitCode = result.Succeeded ? 0 : 2;
    return;
}

if (command == "import-files")
{
    bool dryRun = !args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
    string jobsDirectory = Path.GetFullPath(GetOption(args, "--jobs-directory") ??
                                            Path.Combine(repositoryRoot, ".cache", "simulation-jobs"));
    var fileRepository = new FileSimulationJobRepository(jobsDirectory);
    IReadOnlyList<SimulationJobSnapshot> snapshots = await fileRepository.ListAsync(int.MaxValue);
    var factory = new StandaloneContextFactory(configuration.ConnectionString);
    var postgresRepository = new PostgresSimulationJobRepository(factory, TimeProvider.System);
    int insertedOrUpdated = 0;
    int alreadyCurrent = 0;
    var conflicts = new List<object>();
    foreach (SimulationJobSnapshot snapshot in snapshots)
    {
        SimulationJobSnapshot? existing = await postgresRepository.GetAsync(snapshot.Id);
        if (existing is not null && existing.Revision >= snapshot.Revision)
        {
            if (HashSnapshot(existing) == HashSnapshot(snapshot))
                alreadyCurrent++;
            else
                conflicts.Add(new { snapshot.Id, fileRevision = snapshot.Revision, databaseRevision = existing.Revision });
            continue;
        }
        if (!dryRun)
            await postgresRepository.SaveAsync(snapshot);
        insertedOrUpdated++;
    }
    WriteJson(new
    {
        dryRun,
        simulationJobs = new
        {
            source = jobsDirectory,
            discovered = snapshots.Count,
            insertedOrUpdated,
            alreadyCurrent,
            conflicts,
            quarantined = Directory.Exists(Path.Combine(jobsDirectory, "quarantine"))
                ? Directory.EnumerateFiles(Path.Combine(jobsDirectory, "quarantine")).Count()
                : 0
        },
        calibrationArtifacts = Inventory(Path.Combine(repositoryRoot, ".cache", "calibration-artifacts")),
        simulationProfiles = Inventory(Path.Combine(repositoryRoot, ".cache", "simulation-profiles")),
        simulationExperiments = Inventory(Path.Combine(repositoryRoot, ".cache", "simulation-experiments")),
        policyProfiles = Inventory(Path.Combine(repositoryRoot, ".cache", "trading-policy-profiles")),
        calibrationBundles = Inventory(Path.Combine(repositoryRoot, ".cache", "calibration-bundles")),
        livePersistence = Inventory(Path.Combine(repositoryRoot, ".state", "live-trading")),
        runtimeConfiguration = "Imported by the idempotent seed-reference command; appsettings are empty after PostgreSQL cutover.",
        note = "Only legacy categories present on disk are mutated; absent categories are reported with zero files."
    });
    Environment.ExitCode = conflicts.Count == 0 ? 0 : 3;
    return;
}

if (command == "verify-parity")
{
    string jobsDirectory = Path.GetFullPath(GetOption(args, "--jobs-directory") ??
                                            Path.Combine(repositoryRoot, ".cache", "simulation-jobs"));
    var files = new FileSimulationJobRepository(jobsDirectory);
    IReadOnlyList<SimulationJobSnapshot> snapshots = await files.ListAsync(int.MaxValue);
    var postgres = new PostgresSimulationJobRepository(
        new StandaloneContextFactory(configuration.ConnectionString), TimeProvider.System);
    var discrepancies = new List<object>();
    foreach (SimulationJobSnapshot snapshot in snapshots)
    {
        SimulationJobSnapshot? row = await postgres.GetAsync(snapshot.Id);
        if (row is null || HashSnapshot(row) != HashSnapshot(snapshot))
            discrepancies.Add(new
            {
                snapshot.Id,
                fileRevision = snapshot.Revision,
                databaseRevision = row?.Revision,
                reason = row is null ? "missing" : "hash_mismatch",
                firstDifference = row is null ? null : FindFirstDifference(snapshot, row)
            });
    }
    WriteJson(new
    {
        persistenceMode = "PostgresOnly",
        comparedSimulationJobs = snapshots.Count,
        discrepancies,
        parity = discrepancies.Count == 0,
        dualWrite = "not_applicable_after_cutover",
        runtimeProfiles = "PostgreSQL approved revisions are authoritative; appsettings projections are intentionally empty."
    });
    Environment.ExitCode = discrepancies.Count == 0 ? 0 : 3;
    return;
}

if (command is "migrate" or "bootstrap")
{
    await BrokerCredentialVault.MigrateAsync(configuration.ConnectionString);
    Console.WriteLine("Database migrations applied.");
}

if (command is "import-broker-credentials" or "bootstrap")
{
    if (!File.Exists(envPath))
        throw new FileNotFoundException("Broker credential import file was not found.", envPath);

    IReadOnlyDictionary<string, EnvValue> values = ParseEnvironmentFile(await File.ReadAllLinesAsync(envPath));
    IBrokerCredentialStore store = BrokerCredentialVault.Open(configuration.ConnectionString, keyFile);
    List<BrokerCredential> credentials = CreateCredentials(values);
    if (credentials.Count == 0)
        throw new InvalidOperationException("No broker credentials were found in the import file.");

    foreach (BrokerCredential credential in credentials)
    {
        await store.UpsertAsync(credential);
        BrokerCredential? verified = await store.GetAsync(
            credential.BrokerCode,
            credential.Environment,
            includeDisabled: true);
        if (verified is null)
            throw new InvalidOperationException($"Credential verification failed for {credential.BrokerCode}/{credential.Environment}.");
        Console.WriteLine($"Imported {credential.BrokerCode}/{credential.Environment} (enabled={credential.Enabled}).");
    }
}

if (command is "seed-reference" or "bootstrap")
{
    await SeedBrokerReferenceDataAsync(configuration.ConnectionString, keyFile);
    await SeedRuntimeProfilesAsync(configuration.ConnectionString, repositoryRoot);
    Console.WriteLine("Broker environments, endpoints, accounts, and secret references are current.");
    Console.WriteLine("Approved Dashboard/default and LiveHost/default runtime profiles are current.");
}

if (command == "verify")
{
    IBrokerCredentialStore store = BrokerCredentialVault.Open(configuration.ConnectionString, keyFile);
    (string Broker, string Environment)[] expected =
    [
        ("OANDA", "DEMO"),
        ("BINANCE", "TESTNET"),
        ("IG", "DEMO")
    ];
    foreach ((string broker, string environment) in expected)
    {
        BrokerCredential? credential = await store.GetAsync(broker, environment, includeDisabled: true);
        Console.WriteLine(credential is null
            ? $"Missing {broker}/{environment}."
            : $"Verified {broker}/{environment} (enabled={credential.Enabled}).");
    }
}

if (command is not ("migrate" or "bootstrap" or "import-broker-credentials" or "seed-reference" or "verify"))
    throw new ArgumentException("Command must be bootstrap, migrate, health, schema-version, seed-reference, import-files, verify-parity, backup, restore-test, retention, import-broker-credentials, or verify.");

return;

static List<BrokerCredential> CreateCredentials(IReadOnlyDictionary<string, EnvValue> values)
{
    List<BrokerCredential> result = [];

    EnvValue? oandaToken = First(values, "Oanda__AccessToken", "OANDA_TOKEN");
    EnvValue? oandaAccount = First(values, "Oanda__AccountId", "OANDA_ACCOUNT_ID");
    if (oandaToken is not null || oandaAccount is not null)
    {
        EnvValue? environment = First(values, "Oanda__Environment", "OANDA_ENVIRONMENT");
        result.Add(new BrokerCredential
        {
            BrokerCode = "OANDA",
            Environment = NormalizeEnvironment(environment?.Value, "DEMO"),
            Enabled = (oandaToken?.Active ?? false) && (oandaAccount?.Active ?? false),
            AccountId = oandaAccount?.Value,
            AccessToken = oandaToken?.Value
        });
    }

    EnvValue? binanceKey = First(values, "BINANCE_API_KEY");
    EnvValue? binanceSecret = First(values, "BINANCE_SECRET_KEY");
    if (binanceKey is not null || binanceSecret is not null)
    {
        result.Add(new BrokerCredential
        {
            BrokerCode = "BINANCE",
            Environment = NormalizeEnvironment(First(values, "BINANCE_ENVIRONMENT")?.Value, "LIVE"),
            Enabled = (binanceKey?.Active ?? false) && (binanceSecret?.Active ?? false),
            ApiKey = binanceKey?.Value,
            SecretKey = binanceSecret?.Value
        });
    }

    EnvValue? igKey = First(values, "IG_API_KEY");
    EnvValue? igIdentifier = First(values, "IG_IDENTIFIER");
    EnvValue? igPassword = First(values, "IG_PASSWORD");
    EnvValue? igAccount = First(values, "IG_ACCOUNT_ID");
    if (igKey is not null || igIdentifier is not null || igPassword is not null || igAccount is not null)
    {
        result.Add(new BrokerCredential
        {
            BrokerCode = "IG",
            Environment = "DEMO",
            Enabled = (igKey?.Active ?? false) && (igIdentifier?.Active ?? false) && (igPassword?.Active ?? false),
            ApiKey = igKey?.Value,
            Identifier = igIdentifier?.Value,
            Password = igPassword?.Value,
            AccountId = igAccount?.Value
        });
    }

    return result;
}

static async Task SeedBrokerReferenceDataAsync(string connectionString, string keyFile)
{
    DbContextOptions<TradingHubDbContext> options = new DbContextOptionsBuilder<TradingHubDbContext>()
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention()
        .Options;
    await using TradingHubDbContext context = new(options);
    IBrokerCredentialStore credentialStore = BrokerCredentialVault.Open(connectionString, keyFile);
    var credentialKeys = await context.BrokerCredentials.AsNoTracking()
        .OrderBy(row => row.BrokerCode)
        .ThenBy(row => row.Environment)
        .Select(row => new { row.BrokerCode, row.Environment })
        .ToArrayAsync();

    foreach (var key in credentialKeys)
    {
        BrokerCredential? credential = await credentialStore.GetAsync(
            key.BrokerCode,
            key.Environment,
            includeDisabled: true);
        if (credential is null)
            throw new InvalidOperationException($"Encrypted credential row could not be resolved for {key.BrokerCode}/{key.Environment}.");

        string brokerCode = credential.BrokerCode.Trim().ToUpperInvariant();
        string environmentCode = credential.Environment.Trim().ToUpperInvariant();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        BrokerEntity? broker = await context.Brokers.SingleOrDefaultAsync(row => row.Code == brokerCode);
        if (broker is null)
        {
            broker = new BrokerEntity
            {
                Code = brokerCode,
                DisplayName = BrokerDisplayName(brokerCode),
                CreatedAt = now
            };
            context.Brokers.Add(broker);
            await context.SaveChangesAsync();
        }

        BrokerEnvironmentEntity? environment = await context.BrokerEnvironments.SingleOrDefaultAsync(row =>
            row.BrokerId == broker.BrokerId && row.EnvironmentCode == environmentCode);
        if (environment is null)
        {
            environment = new BrokerEnvironmentEntity
            {
                BrokerEnvironmentId = Guid.NewGuid(),
                BrokerId = broker.BrokerId,
                EnvironmentCode = environmentCode,
                DisplayName = $"{broker.DisplayName} {environmentCode}",
                IsLive = environmentCode == "LIVE",
                Enabled = credential.Enabled,
                CreatedAt = now
            };
            context.BrokerEnvironments.Add(environment);
        }
        else
        {
            environment.Enabled = credential.Enabled;
            environment.IsLive = environmentCode == "LIVE";
        }
        await context.SaveChangesAsync();

        foreach ((BrokerEndpointKind kind, string address) in EndpointDefaults(brokerCode, environmentCode, credential.BaseAddress))
            await UpsertEndpointAsync(context, environment.BrokerEnvironmentId, kind, address, now);

        Guid? brokerAccountId = null;
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            string accountHash = Hash(credential.AccountId);
            BrokerAccountEntity? account = await context.BrokerAccounts.SingleOrDefaultAsync(row =>
                row.BrokerId == broker.BrokerId && row.ExternalAccountKeyHash == accountHash);
            if (account is null)
            {
                account = new BrokerAccountEntity
                {
                    BrokerAccountId = Guid.NewGuid(),
                    BrokerId = broker.BrokerId,
                    ExternalAccountKeyHash = accountHash,
                    MaskedAccountName = Mask(credential.AccountId),
                    Environment = environment.IsLive ? (short)2 : (short)1,
                    AccountCurrency = "USD",
                    Enabled = credential.Enabled,
                    CreatedAt = now
                };
                context.BrokerAccounts.Add(account);
            }
            else
            {
                account.Enabled = credential.Enabled;
            }
            await context.SaveChangesAsync();
            brokerAccountId = account.BrokerAccountId;
        }

        await UpsertAccountSettingsAsync(
            context,
            environment.BrokerEnvironmentId,
            brokerAccountId,
            $"{brokerCode}-{environmentCode}",
            credential.AccountId,
            now);

        foreach ((string purpose, string? value) in CredentialFields(credential))
        {
            if (!string.IsNullOrWhiteSpace(value))
                await UpsertCredentialReferenceAsync(context, environment.BrokerEnvironmentId, brokerCode, environmentCode, purpose, now);
        }

        Console.WriteLine($"Seeded {brokerCode}/{environmentCode} reference configuration (enabled={credential.Enabled}).");
    }
}

static async Task SeedRuntimeProfilesAsync(string connectionString, string repositoryRoot)
{
    DbContextOptions<TradingHubDbContext> options = new DbContextOptionsBuilder<TradingHubDbContext>()
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention()
        .Options;
    await using TradingHubDbContext context = new(options);
    await SeedRuntimeProfileAsync(
        context,
        RuntimeProfileKind.Dashboard,
        "default",
        Path.Combine(repositoryRoot, "DashboardLive", "appsettings.json"));
    await SeedRuntimeProfileAsync(
        context,
        RuntimeProfileKind.LiveHost,
        "default",
        Path.Combine(repositoryRoot, "LiveTradingHost", "appsettings.json"));
}

static async Task SeedRuntimeProfileAsync(
    TradingHubDbContext context,
    RuntimeProfileKind kind,
    string name,
    string sourcePath)
{
    RuntimeProfileEntity? profile = await context.RuntimeProfiles.SingleOrDefaultAsync(row =>
        row.ProfileKind == kind && row.Name == name);
    if (profile is not null)
        return;
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("Runtime profile import source was not found.", sourcePath);

    string settingsJson = SanitizeRuntimeProfile(await File.ReadAllTextAsync(sourcePath));
    DateTimeOffset now = DateTimeOffset.UtcNow;
    profile = new RuntimeProfileEntity
    {
        RuntimeProfileId = Guid.NewGuid(),
        ProfileKind = kind,
        Name = name,
        CreatedAt = now,
        CreatedBy = "dbmanager-seed"
    };
    context.RuntimeProfiles.Add(profile);
    context.RuntimeProfileRevisions.Add(new RuntimeProfileRevisionEntity
    {
        RuntimeProfileRevisionId = Guid.NewGuid(),
        RuntimeProfileId = profile.RuntimeProfileId,
        Revision = 1,
        Status = RuntimeProfileRevisionStatus.Approved,
        SettingsJson = settingsJson,
        SettingsHash = Hash(settingsJson),
        CreatedAt = now,
        CreatedBy = "dbmanager-seed",
        ApprovedAt = now,
        ApprovedBy = "dbmanager-seed"
    });
    await context.SaveChangesAsync();
}

static string SanitizeRuntimeProfile(string json)
{
    JsonObject root = JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException("Runtime profile import source is empty.");
    Remove(root, "Oanda", "AccountId", "AccessToken", "RestBaseAddress", "StreamBaseAddress");
    Remove(root, "LiveFeed", "RestBaseAddress", "WebSocketBaseAddress");
    Remove(root, "LiveHost", "ControlToken");
    return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

static void Remove(JsonObject root, string sectionName, params string[] properties)
{
    if (root[sectionName] is not JsonObject section)
        return;
    foreach (string property in properties)
        section.Remove(property);
}

static async Task UpsertEndpointAsync(
    TradingHubDbContext context,
    Guid environmentId,
    BrokerEndpointKind kind,
    string address,
    DateTimeOffset now)
{
    string normalized = new Uri(address, UriKind.Absolute).AbsoluteUri;
    string contentHash = Hash(normalized);
    BrokerEndpointRevisionEntity? active = await context.BrokerEndpointRevisions.SingleOrDefaultAsync(row =>
        row.BrokerEnvironmentId == environmentId && row.Kind == kind && row.Active);
    if (active?.ContentHash == contentHash)
        return;
    if (active is not null)
    {
        active.Active = false;
        await context.SaveChangesAsync();
    }
    int revision = (await context.BrokerEndpointRevisions
        .Where(row => row.BrokerEnvironmentId == environmentId && row.Kind == kind)
        .MaxAsync(row => (int?)row.Revision) ?? 0) + 1;
    context.BrokerEndpointRevisions.Add(new BrokerEndpointRevisionEntity
    {
        BrokerEndpointRevisionId = Guid.NewGuid(),
        BrokerEnvironmentId = environmentId,
        Kind = kind,
        Revision = revision,
        BaseAddress = normalized,
        ContentHash = contentHash,
        Active = true,
        CreatedAt = now,
        CreatedBy = "dbmanager-seed"
    });
    await context.SaveChangesAsync();
}

static async Task UpsertAccountSettingsAsync(
    TradingHubDbContext context,
    Guid environmentId,
    Guid? accountId,
    string alias,
    string? externalAccountId,
    DateTimeOffset now)
{
    string settings = "{}";
    string contentHash = Hash($"{alias}\n{externalAccountId}\nUSD\n{settings}");
    BrokerAccountSettingsRevisionEntity? active = await context.BrokerAccountSettingsRevisions.SingleOrDefaultAsync(row =>
        row.BrokerEnvironmentId == environmentId && row.Active);
    if (active?.ContentHash == contentHash && active.BrokerAccountId == accountId)
        return;
    if (active is not null)
    {
        active.Active = false;
        await context.SaveChangesAsync();
    }
    int revision = (await context.BrokerAccountSettingsRevisions
        .Where(row => row.BrokerEnvironmentId == environmentId)
        .MaxAsync(row => (int?)row.Revision) ?? 0) + 1;
    context.BrokerAccountSettingsRevisions.Add(new BrokerAccountSettingsRevisionEntity
    {
        BrokerAccountSettingsRevisionId = Guid.NewGuid(),
        BrokerEnvironmentId = environmentId,
        BrokerAccountId = accountId,
        Revision = revision,
        AccountAlias = alias,
        ExternalAccountId = externalAccountId,
        AccountCurrency = "USD",
        SettingsJson = settings,
        ContentHash = contentHash,
        Active = true,
        CreatedAt = now,
        CreatedBy = "dbmanager-seed"
    });
    await context.SaveChangesAsync();
}

static async Task UpsertCredentialReferenceAsync(
    TradingHubDbContext context,
    Guid environmentId,
    string brokerCode,
    string environmentCode,
    string purpose,
    DateTimeOffset now)
{
    const string provider = "encrypted_database";
    string secretKey = $"{brokerCode}/{environmentCode}/{purpose}";
    CredentialReferenceEntity? active = await context.CredentialReferences.SingleOrDefaultAsync(row =>
        row.BrokerEnvironmentId == environmentId && row.Purpose == purpose && row.Active);
    if (active is not null && active.Provider == provider && active.SecretKey == secretKey)
        return;
    if (active is not null)
    {
        active.Active = false;
        await context.SaveChangesAsync();
    }
    context.CredentialReferences.Add(new CredentialReferenceEntity
    {
        CredentialReferenceId = Guid.NewGuid(),
        BrokerEnvironmentId = environmentId,
        Purpose = purpose,
        Provider = provider,
        SecretKey = secretKey,
        Active = true,
        CreatedAt = now
    });
    await context.SaveChangesAsync();
}

static IEnumerable<(string Purpose, string? Value)> CredentialFields(BrokerCredential credential)
{
    yield return ("access_token", credential.AccessToken);
    yield return ("api_key", credential.ApiKey);
    yield return ("secret_key", credential.SecretKey);
    yield return ("identifier", credential.Identifier);
    yield return ("password", credential.Password);
}

static IEnumerable<(BrokerEndpointKind Kind, string Address)> EndpointDefaults(
    string broker,
    string environment,
    string? overrideRestAddress)
{
    if (!string.IsNullOrWhiteSpace(overrideRestAddress))
        yield return (BrokerEndpointKind.Rest, overrideRestAddress);
    else if (broker == "OANDA")
        yield return (BrokerEndpointKind.Rest, environment == "LIVE" ? "https://api-fxtrade.oanda.com/" : "https://api-fxpractice.oanda.com/");
    else if (broker == "BINANCE")
        yield return (BrokerEndpointKind.Rest, environment switch
        {
            "LIVE" => "https://api.binance.com/",
            "DEMO" => "https://demo-api.binance.com/",
            _ => "https://testnet.binance.vision/"
        });
    else if (broker == "IG")
        yield return (BrokerEndpointKind.Rest, environment == "LIVE" ? "https://api.ig.com/gateway/deal/" : "https://demo-api.ig.com/gateway/deal/");

    if (broker == "OANDA")
        yield return (BrokerEndpointKind.Streaming, environment == "LIVE" ? "https://stream-fxtrade.oanda.com/" : "https://stream-fxpractice.oanda.com/");
    else if (broker == "BINANCE")
    {
        yield return (BrokerEndpointKind.Streaming, environment switch
        {
            "LIVE" => "wss://ws-api.binance.com/ws-api/v3",
            "DEMO" => "wss://demo-ws-api.binance.com/ws-api/v3",
            _ => "wss://ws-api.testnet.binance.vision/ws-api/v3"
        });
        yield return (BrokerEndpointKind.MarketData, environment switch
        {
            "LIVE" => "wss://stream.binance.com:9443/ws/",
            "DEMO" => "wss://demo-stream.binance.com/ws/",
            _ => "wss://stream.testnet.binance.vision/ws/"
        });
    }
}

static string BrokerDisplayName(string brokerCode) => brokerCode switch
{
    "OANDA" => "OANDA",
    "BINANCE" => "Binance",
    "IG" => "IG",
    _ => brokerCode
};

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static string Mask(string value)
{
    string trimmed = value.Trim();
    return trimmed.Length <= 4 ? new string('*', trimmed.Length) : $"***{trimmed[^4..]}";
}

static IReadOnlyDictionary<string, EnvValue> ParseEnvironmentFile(IEnumerable<string> lines)
{
    Dictionary<string, EnvValue> values = new(StringComparer.OrdinalIgnoreCase);
    foreach (string rawLine in lines)
    {
        string line = rawLine.Trim();
        bool active = !line.StartsWith('#');
        if (!active)
            line = line[1..].TrimStart();
        if (line.StartsWith("export ", StringComparison.Ordinal))
            line = line[7..].TrimStart();
        int separator = line.IndexOf('=');
        if (separator <= 0)
            continue;
        string name = line[..separator].Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            continue;
        string value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '\"' && value[^1] == '\"') ||
                                  (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        value = Regex.Replace(value, @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)", match =>
            values.TryGetValue(match.Groups["name"].Value, out EnvValue? referenced) ? referenced.Value : match.Value);
        values[name] = new EnvValue(value, active);
    }
    return values;
}

static EnvValue? First(IReadOnlyDictionary<string, EnvValue> values, params string[] names)
{
    foreach (string name in names)
    {
        if (values.TryGetValue(name, out EnvValue? value) && !string.IsNullOrWhiteSpace(value.Value))
            return value;
    }
    return null;
}

static string NormalizeEnvironment(string? value, string fallback)
{
    if (string.IsNullOrWhiteSpace(value))
        return fallback;
    string normalized = value.Trim().ToUpperInvariant();
    return normalized is "PRACTICE" or "SANDBOX" or "TEST" ? "DEMO" : normalized;
}

static string FindRepositoryRoot(string start)
{
    DirectoryInfo? directory = new(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate the TradingHub repository root.");
}

static string? GetOption(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static int GetIntOption(string[] arguments, string name, int fallback)
{
    string? value = GetOption(arguments, name);
    return value is null ? fallback : int.TryParse(value, out int result) && result > 0
        ? result
        : throw new ArgumentException($"{name} must be a positive integer.");
}

static object Inventory(string directory) => new
{
    source = directory,
    discovered = Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Count(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        : 0
};

static string HashSnapshot(SimulationJobSnapshot snapshot)
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    JsonNode? node = JsonSerializer.SerializeToNode(snapshot, options);
    using var payload = new MemoryStream();
    using (var writer = new Utf8JsonWriter(payload))
        WriteCanonicalJson(writer, node);
    return Convert.ToHexString(SHA256.HashData(payload.ToArray()));
}

static void WriteCanonicalJson(Utf8JsonWriter writer, JsonNode? node)
{
    switch (node)
    {
        case null:
            writer.WriteNullValue();
            break;
        case JsonObject jsonObject:
            writer.WriteStartObject();
            foreach ((string name, JsonNode? value) in jsonObject.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                WriteCanonicalJson(writer, value);
            }
            writer.WriteEndObject();
            break;
        case JsonArray jsonArray:
            writer.WriteStartArray();
            foreach (JsonNode? value in jsonArray)
                WriteCanonicalJson(writer, value);
            writer.WriteEndArray();
            break;
        default:
            node.WriteTo(writer);
            break;
    }
}

static string? FindFirstDifference(SimulationJobSnapshot file, SimulationJobSnapshot database)
{
    JsonNode? fileNode = JsonSerializer.SerializeToNode(file, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    });
    JsonNode? databaseNode = JsonSerializer.SerializeToNode(database, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    });
    return FindNodeDifference(fileNode, databaseNode, "$", out string? difference) ? difference : null;
}

static bool FindNodeDifference(JsonNode? left, JsonNode? right, string path, out string? difference)
{
    if (left is JsonObject leftObject && right is JsonObject rightObject)
    {
        foreach ((string name, JsonNode? value) in leftObject)
        {
            if (!rightObject.TryGetPropertyValue(name, out JsonNode? rightValue))
            {
                difference = $"{path}.{name}: missing from database projection";
                return true;
            }
            if (FindNodeDifference(value, rightValue, $"{path}.{name}", out difference))
                return true;
        }
        foreach ((string name, _) in rightObject)
        {
            if (!leftObject.ContainsKey(name))
            {
                difference = $"{path}.{name}: only in database projection";
                return true;
            }
        }
        difference = null;
        return false;
    }
    if (left is JsonArray leftArray && right is JsonArray rightArray)
    {
        if (leftArray.Count != rightArray.Count)
        {
            difference = $"{path}: array count {leftArray.Count} != {rightArray.Count}";
            return true;
        }
        for (int index = 0; index < leftArray.Count; index++)
        {
            if (FindNodeDifference(leftArray[index], rightArray[index], $"{path}[{index}]", out difference))
                return true;
        }
        difference = null;
        return false;
    }
    if (!JsonNode.DeepEquals(left, right))
    {
        difference = $"{path}: {BoundJson(left)} != {BoundJson(right)}";
        return true;
    }
    difference = null;
    return false;
}

static string BoundJson(JsonNode? value)
{
    string json = value?.ToJsonString() ?? "null";
    return json.Length <= 160 ? json : json[..160] + "...";
}

static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
}));

sealed record EnvValue(string Value, bool Active);

sealed class StandaloneContextFactory(string connectionString) : IDbContextFactory<TradingHubDbContext>
{
    private readonly DbContextOptions<TradingHubDbContext> _options = new DbContextOptionsBuilder<TradingHubDbContext>()
        .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
            PostgresSchemaVersionReader.MigrationsHistoryTableName,
            PostgresSchemaVersionReader.MigrationsHistorySchema))
        .UseSnakeCaseNamingConvention()
        .Options;

    public TradingHubDbContext CreateDbContext() => new(_options);
    public Task<TradingHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}

sealed record DatabaseConfiguration
{
    public required string ConnectionString { get; init; }
    public string BrokerCredentialKeyFile { get; init; } = ".state/keys/broker-credentials.key";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerCredentialKeyFile);
    }
}
