namespace Networking;

/// <summary>
/// A single HTTP request header. Repeated names are allowed so multi-value headers are preserved.
/// </summary>
public sealed record HttpNetworkHeader
{
    public HttpNetworkHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!name.All(IsTokenCharacter))
        {
            throw new ArgumentException("The header name contains an invalid character.", nameof(name));
        }

        if (value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Header values cannot contain a carriage return or line feed.", nameof(value));
        }

        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    internal static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or
            '^' or '_' or '`' or '|' or '~';
}
