using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DBManager.Abstractions.Credentials;

public sealed record SecretReference
{
    public required Guid CredentialReferenceId { get; init; }
    public required string Provider { get; init; }
    public required string SecretKey { get; init; }
    public required string Purpose { get; init; }
    public string? Version { get; init; }
}

/// <summary>A short-lived, explicitly revealed secret that redacts itself when formatted.</summary>
public sealed class SecretValue : IDisposable
{
    private char[]? _value;

    public SecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value.ToCharArray();
    }

    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(_value is null, this);
        return new string(_value);
    }

    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        char[]? value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}

public interface ISecretResolver
{
    Task<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}
