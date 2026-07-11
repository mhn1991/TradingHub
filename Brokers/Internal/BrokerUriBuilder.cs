using System.Globalization;

namespace Brokers;

internal sealed class BrokerUriBuilder
{
    private readonly Uri _baseUri;
    private readonly string _path;
    private readonly List<KeyValuePair<string, string>> _parameters = [];

    public BrokerUriBuilder(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        _baseUri = baseUri;
        _path = relativePath.TrimStart('/');
    }

    public BrokerUriBuilder Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _parameters.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public BrokerUriBuilder Add(string name, int value) =>
        Add(name, value.ToString(CultureInfo.InvariantCulture));

    public BrokerUriBuilder AddUnixMilliseconds(string name, DateTimeOffset value) =>
        Add(name, value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    public Uri Build()
    {
        Uri endpoint = new(_baseUri, _path);
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join(
                "&",
                _parameters.Select(parameter =>
                    $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"))
        };

        return builder.Uri;
    }
}
