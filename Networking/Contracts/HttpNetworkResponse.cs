using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace Networking;

/// <summary>
/// A fully buffered HTTP response. It remains usable after the underlying HTTP response is disposed.
/// </summary>
public sealed class HttpNetworkResponse : NetworkResponse
{
    private readonly byte[] _body;

    public HttpNetworkResponse(
        NetworkProtocol protocol,
        Uri requestUri,
        HttpStatusCode statusCode,
        string? reasonPhrase = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        ReadOnlyMemory<byte> body = default,
        Uri? redirectLocation = null,
        Version? httpVersion = null,
        TimeSpan elapsed = default)
        : base(protocol)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        if (protocol is not NetworkProtocol.Http and not NetworkProtocol.Https)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocol),
                protocol,
                "An HTTP response requires the HTTP or HTTPS protocol.");
        }

        RequestUri = requestUri;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = FreezeHeaders(headers);
        _body = body.ToArray();
        RedirectLocation = redirectLocation;
        HttpVersion = httpVersion ?? System.Net.HttpVersion.Version11;
        Elapsed = elapsed;
        Category = Classify((int)statusCode);
    }

    public Uri RequestUri { get; }

    public HttpStatusCode StatusCode { get; }

    public int StatusCodeValue => (int)StatusCode;

    public string? ReasonPhrase { get; }

    public HttpResponseCategory Category { get; }

    public bool IsSuccessStatusCode => Category == HttpResponseCategory.Success;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    public ReadOnlyMemory<byte> Body => _body;

    public Uri? RedirectLocation { get; }

    public Version HttpVersion { get; }

    public TimeSpan Elapsed { get; }

    public string GetBodyAsString(Encoding? encoding = null) =>
        (encoding ?? Encoding.UTF8).GetString(_body);

    public bool TryGetHeader(string name, out IReadOnlyList<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Headers.TryGetValue(name, out values!);
    }

    private static HttpResponseCategory Classify(int statusCode) => statusCode switch
    {
        >= 100 and <= 199 => HttpResponseCategory.Informational,
        >= 200 and <= 299 => HttpResponseCategory.Success,
        >= 300 and <= 399 => HttpResponseCategory.Redirect,
        >= 400 and <= 499 => HttpResponseCategory.ClientError,
        >= 500 and <= 599 => HttpResponseCategory.ServerError,
        _ => HttpResponseCategory.Unknown
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> FreezeHeaders(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (headers is not null)
        {
            foreach ((string name, IReadOnlyList<string> values) in headers)
            {
                copy[name] = new ReadOnlyCollection<string>(values.ToArray());
            }
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }
}
