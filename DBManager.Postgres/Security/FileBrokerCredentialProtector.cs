using System.Security.Cryptography;
using System.Text;

namespace DBManager.Postgres.Security;

internal sealed class FileBrokerCredentialProtector
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public FileBrokerCredentialProtector(string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        _key = LoadOrCreateKey(Path.GetFullPath(keyFile));
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using AesGcm aes = new(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));

        byte[] payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = FormatVersion;
        nonce.CopyTo(payload.AsSpan(1, NonceSize));
        tag.CopyTo(payload.AsSpan(1 + NonceSize, TagSize));
        ciphertext.CopyTo(payload.AsSpan(1 + NonceSize + TagSize));
        return payload;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose)
    {
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != FormatVersion)
            throw new CryptographicException("Unsupported broker credential payload format.");

        ReadOnlySpan<byte> nonce = payload.Slice(1, NonceSize);
        ReadOnlySpan<byte> tag = payload.Slice(1 + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = payload[(1 + NonceSize + TagSize)..];
        byte[] plaintext = new byte[ciphertext.Length];
        using AesGcm aes = new(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    private static byte[] LoadOrCreateKey(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            byte[] existing = File.ReadAllBytes(keyFile);
            if (existing.Length != KeySize)
                throw new CryptographicException($"Broker credential key must be exactly {KeySize} bytes.");
            return existing;
        }

        string? directory = Path.GetDirectoryName(keyFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using FileStream stream = new(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
            stream.Flush(true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
        catch (IOException) when (File.Exists(keyFile))
        {
            CryptographicOperations.ZeroMemory(key);
            byte[] existing = File.ReadAllBytes(keyFile);
            if (existing.Length != KeySize)
                throw new CryptographicException($"Broker credential key must be exactly {KeySize} bytes.");
            return existing;
        }
    }
}
