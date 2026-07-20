using Brokers.Abstractions;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Security;
using NUnit.Framework;

namespace Brokers.IntegrationTests;

internal static class IntegrationTestEnvironment
{
    private static readonly Lazy<IBrokerCredentialStore?> CredentialStore = new(OpenCredentialStore);

    public static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Ignore($"Set the {name} environment variable to run this integration test.");
            return null!;
        }

        return value;
    }

    public static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : null;

    public static BrokerCredential RequiredCredential(string broker, string environment)
    {
        BrokerCredential? credential = OptionalCredential(broker, environment);
        if (credential is null)
        {
            Assert.Ignore(
                $"Import and enable {broker}/{environment} in the broker credential database to run this integration test.");
            return null!;
        }

        return credential;
    }

    public static BrokerCredential? OptionalCredential(string broker, string environment)
    {
        IBrokerCredentialStore? store = CredentialStore.Value;
        return store?.GetAsync(broker, environment).GetAwaiter().GetResult();
    }

    public static string RequiredCredentialValue(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Ignore($"The enabled broker credential row has no {field}.");
            return null!;
        }

        return value;
    }

    public static BrokerEnvironment ParseStoredBrokerEnvironment(string raw)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out BrokerEnvironment environment))
            return environment;

        Assert.Fail($"The broker credential environment '{raw}' is not supported.");
        return BrokerEnvironment.Demo;
    }

    public static BrokerEnvironment ParseBrokerEnvironment(
        string variableName,
        BrokerEnvironment defaultValue = BrokerEnvironment.Demo)
    {
        string? raw = Optional(variableName);
        if (raw is null)
        {
            return defaultValue;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out BrokerEnvironment environment))
        {
            return environment;
        }

        Assert.Fail($"{variableName} must be Demo or Live, but was '{raw}'.");
        return defaultValue;
    }

    public static Uri? OptionalUri(string name)
    {
        string? raw = Optional(name);
        if (raw is null)
        {
            return null;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
        {
            return uri;
        }

        Assert.Fail($"{name} must contain an absolute URL, but was '{raw}'.");
        return null;
    }

    public static Uri? CredentialUri(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
            return uri;

        Assert.Fail($"The broker credential {field} must contain an absolute URL.");
        return null;
    }

    private static IBrokerCredentialStore? OpenCredentialStore()
    {
        BrokerCredentialDatabaseConfiguration? configuration =
            BrokerCredentialDatabaseConfiguration.TryLoad(
                TestContext.CurrentContext.TestDirectory,
                out string repositoryRoot);
        return configuration?.OpenStore(repositoryRoot);
    }
}
