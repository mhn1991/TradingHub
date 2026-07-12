using Brokers.Abstractions;
using NUnit.Framework;

namespace Brokers.IntegrationTests;

internal static class IntegrationTestEnvironment
{
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
}
