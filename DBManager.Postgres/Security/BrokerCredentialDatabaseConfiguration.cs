using System.Text.Json;
using DBManager.Abstractions.Credentials;

namespace DBManager.Postgres.Security;

/// <summary>Loads the ignored, machine-local database configuration used by runtime hosts.</summary>
public sealed record BrokerCredentialDatabaseConfiguration
{
    public required string ConnectionString { get; init; }
    public string BrokerCredentialKeyFile { get; init; } = ".state/keys/broker-credentials.key";

    public IBrokerCredentialStore OpenStore(string repositoryRoot)
    {
        return BrokerCredentialVault.Open(ConnectionString, ResolveKeyFile(repositoryRoot));
    }

    public string ResolveKeyFile(string repositoryRoot) => Path.IsPathRooted(BrokerCredentialKeyFile)
        ? BrokerCredentialKeyFile
        : Path.GetFullPath(Path.Combine(repositoryRoot, BrokerCredentialKeyFile));

    public static BrokerCredentialDatabaseConfiguration? TryLoad(
        string startDirectory,
        out string repositoryRoot)
    {
        repositoryRoot = FindRepositoryRoot(startDirectory);
        string path = Path.Combine(repositoryRoot, ".state", "tradinghub.database.json");
        if (!File.Exists(path))
            return null;
        BrokerCredentialDatabaseConfiguration? configuration = JsonSerializer.Deserialize<BrokerCredentialDatabaseConfiguration>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.ConnectionString))
            throw new InvalidOperationException("The local TradingHub database configuration is invalid.");
        return configuration;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the TradingHub repository root.");
    }
}
