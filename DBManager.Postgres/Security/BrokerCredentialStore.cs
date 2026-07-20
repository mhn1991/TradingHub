using System.Text.Json;
using DBManager.Abstractions.Credentials;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Security;

public sealed class BrokerCredentialStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    string keyFile,
    TimeProvider? timeProvider = null) : IBrokerCredentialStore
{
    public const string ProtectionScheme = "aes-256-gcm-v1";
    private readonly FileBrokerCredentialProtector _protector = new(keyFile);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<BrokerCredential?> GetAsync(
        string brokerCode,
        string environment,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedBroker = Normalize(brokerCode);
        string normalizedEnvironment = Normalize(environment);
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        BrokerCredentialEntity? entity = await context.BrokerCredentials.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.BrokerCode == normalizedBroker && value.Environment == normalizedEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        if (entity is null || (!includeDisabled && !entity.Enabled))
            return null;
        if (!string.Equals(entity.ProtectionScheme, ProtectionScheme, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported broker credential protection scheme '{entity.ProtectionScheme}'.");

        string purpose = CreatePurpose(normalizedBroker, normalizedEnvironment);
        byte[] plaintext = _protector.Unprotect(entity.ProtectedPayload, purpose);
        try
        {
            BrokerCredential credential = JsonSerializer.Deserialize<BrokerCredential>(plaintext)
                ?? throw new InvalidOperationException("The broker credential payload is empty.");
            return credential with
            {
                BrokerCode = normalizedBroker,
                Environment = normalizedEnvironment,
                Enabled = entity.Enabled
            };
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task UpsertAsync(BrokerCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        string brokerCode = Normalize(credential.BrokerCode);
        string environment = Normalize(credential.Environment);
        BrokerCredential normalized = credential with { BrokerCode = brokerCode, Environment = environment };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized);
        byte[] protectedPayload;
        try
        {
            protectedPayload = _protector.Protect(plaintext, CreatePurpose(brokerCode, environment));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }

        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        BrokerCredentialEntity? entity = await context.BrokerCredentials.SingleOrDefaultAsync(
            value => value.BrokerCode == brokerCode && value.Environment == environment,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (entity is null)
        {
            context.BrokerCredentials.Add(new BrokerCredentialEntity
            {
                BrokerCode = brokerCode,
                Environment = environment,
                ProtectedPayload = protectedPayload,
                ProtectionScheme = ProtectionScheme,
                Enabled = normalized.Enabled,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            entity.ProtectedPayload = protectedPayload;
            entity.ProtectionScheme = ProtectionScheme;
            entity.Enabled = normalized.Enabled;
            entity.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant();
    }

    private static string CreatePurpose(string brokerCode, string environment) =>
        $"tradinghub:broker-credential:{brokerCode}:{environment}";
}
