using DBManager.Abstractions.Credentials;

namespace DBManager.Postgres.Security;

/// <summary>
/// Resolves provider coordinates against the encrypted PostgreSQL credential vault. The
/// reference itself is safe to persist; the returned value must be disposed promptly.
/// </summary>
public sealed class EncryptedDatabaseSecretResolver(IBrokerCredentialStore credentials) : ISecretResolver
{
    public const string ProviderName = "encrypted_database";

    public async Task<SecretValue> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The credential reference uses an unsupported secret provider.");

        string[] coordinates = reference.SecretKey.Split('/', StringSplitOptions.TrimEntries);
        if (coordinates.Length != 3 || coordinates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("The credential reference coordinates are invalid.");

        BrokerCredential credential = await credentials.GetAsync(
            coordinates[0], coordinates[1], cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The referenced broker credential is unavailable or disabled.");

        string? value = coordinates[2].ToLowerInvariant() switch
        {
            "access_token" => credential.AccessToken,
            "api_key" => credential.ApiKey,
            "secret_key" => credential.SecretKey,
            "identifier" => credential.Identifier,
            "password" => credential.Password,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("The referenced broker credential field is unavailable.");

        return new SecretValue(value);
    }
}
