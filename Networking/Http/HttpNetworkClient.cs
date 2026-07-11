using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace Networking;

/// <summary>
/// Concurrent HTTP/HTTPS client backed by a reusable <see cref="SocketsHttpHandler"/> connection pool.
/// </summary>
public sealed class HttpNetworkClient : INetworkProtocolClient
{
    private static readonly IReadOnlyCollection<NetworkProtocol> Protocols =
        Array.AsReadOnly(new[] { NetworkProtocol.Http, NetworkProtocol.Https });

    private readonly HttpClient _httpClient;
    private readonly HttpNetworkClientOptions _options;
    private readonly bool _disposeHttpClient;
    private int _disposed;

    public HttpNetworkClient(HttpNetworkClientOptions? options = null)
    {
        _options = options ?? new HttpNetworkClientOptions();
        _options.Validate();
        _httpClient = CreatePooledHttpClient(_options);
        _disposeHttpClient = true;
    }

    /// <summary>
    /// Creates a protocol client around an externally managed HttpClient. This is useful for dependency injection and tests.
    /// The supplied handler determines redirect behavior; production callers normally use the options-only constructor.
    /// </summary>
    public HttpNetworkClient(
        HttpClient httpClient,
        HttpNetworkClientOptions? options = null,
        bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _options = options ?? new HttpNetworkClientOptions();
        _options.Validate();
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
    }

    public IReadOnlyCollection<NetworkProtocol> SupportedProtocols => Protocols;

    public async Task<HttpNetworkResponse> SendAsync(
        HttpNetworkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!Protocols.Contains(request.Protocol))
        {
            throw new NotSupportedException($"The HTTP client cannot dispatch protocol '{request.Protocol}'.");
        }

        using HttpRequestMessage requestMessage = CreateRequestMessage(request);
        TimeSpan timeout = request.Timeout ?? _options.RequestTimeout;
        using CancellationTokenSource? timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        CancellationToken effectiveToken = timeoutSource?.Token ?? cancellationToken;
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            using HttpResponseMessage responseMessage = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    effectiveToken)
                .ConfigureAwait(false);

            byte[] responseBody = await ReadBodyAsync(responseMessage.Content, request, effectiveToken)
                .ConfigureAwait(false);

            IReadOnlyDictionary<string, IReadOnlyList<string>> headers = ReadHeaders(responseMessage);
            Uri responseUri = responseMessage.RequestMessage?.RequestUri ?? request.Endpoint;
            Uri? redirectLocation = ResolveRedirectLocation(responseMessage.Headers.Location, responseUri);

            return new HttpNetworkResponse(
                request.Protocol,
                responseUri,
                responseMessage.StatusCode,
                responseMessage.ReasonPhrase,
                headers,
                responseBody,
                redirectLocation,
                responseMessage.Version,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NetworkTimeoutException(
                request.Protocol,
                request.Endpoint.AbsoluteUri,
                timeout,
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new NetworkTransportException(
                request.Protocol,
                request.Endpoint.AbsoluteUri,
                exception);
        }
    }

    async Task<NetworkResponse> INetworkProtocolClient.DispatchAsync(
        NetworkRequest request,
        CancellationToken cancellationToken)
    {
        if (request is not HttpNetworkRequest httpRequest)
        {
            throw new ArgumentException(
                $"The HTTP client requires a {nameof(HttpNetworkRequest)}.",
                nameof(request));
        }

        return await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static HttpClient CreatePooledHttpClient(HttpNetworkClientOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout,
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            UseCookies = false
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    private static HttpRequestMessage CreateRequestMessage(HttpNetworkRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Endpoint)
        {
            Version = request.HttpVersion,
            VersionPolicy = request.VersionPolicy
        };

        try
        {
            HttpContent? content = request.HasBody || request.ContentType is not null
                ? new ByteArrayContent(request.Body.ToArray())
                : null;

            if (request.ContentType is not null)
            {
                content!.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }

            foreach (HttpNetworkHeader header in request.Headers)
            {
                if (message.Headers.TryAddWithoutValidation(header.Name, header.Value))
                {
                    continue;
                }

                content ??= new ByteArrayContent([]);
                if (!content.Headers.TryAddWithoutValidation(header.Name, header.Value))
                {
                    throw new InvalidOperationException($"Header '{header.Name}' is not valid for an HTTP request.");
                }
            }

            message.Content = content;
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task<byte[]> ReadBodyAsync(
        HttpContent content,
        HttpNetworkRequest request,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var contentLength &&
            contentLength > _options.MaxResponseBodyBytes)
        {
            throw new NetworkResponseTooLargeException(
                request.Protocol,
                request.Endpoint.AbsoluteUri,
                _options.MaxResponseBodyBytes);
        }

        await using Stream responseStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        int initialCapacity = content.Headers.ContentLength is > 0 and <= int.MaxValue
            ? (int)content.Headers.ContentLength.Value
            : 0;
        using var bufferStream = new MemoryStream(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            while (true)
            {
                int bytesRead = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return bufferStream.ToArray();
                }

                if (bytesRead > _options.MaxResponseBodyBytes - bufferStream.Length)
                {
                    throw new NetworkResponseTooLargeException(
                        request.Protocol,
                        request.Endpoint.AbsoluteUri,
                        _options.MaxResponseBodyBytes);
                }

                bufferStream.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadHeaders(
        HttpResponseMessage response)
    {
        var valuesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        AddHeaders(valuesByName, response.Headers);
        AddHeaders(valuesByName, response.Content.Headers);
        AddHeaders(valuesByName, response.TrailingHeaders);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<string> values) in valuesByName)
        {
            result[name] = new ReadOnlyCollection<string>(values);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(result);
    }

    private static void AddHeaders(
        IDictionary<string, List<string>> destination,
        HttpHeaders headers)
    {
        foreach ((string name, IEnumerable<string> values) in headers)
        {
            if (!destination.TryGetValue(name, out List<string>? existingValues))
            {
                existingValues = [];
                destination.Add(name, existingValues);
            }

            existingValues.AddRange(values);
        }
    }

    private static Uri? ResolveRedirectLocation(Uri? location, Uri requestUri)
    {
        if (location is null || location.IsAbsoluteUri)
        {
            return location;
        }

        return new Uri(requestUri, location);
    }

    private static CancellationTokenSource? CreateTimeoutSource(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HttpNetworkClient));
        }
    }
}
